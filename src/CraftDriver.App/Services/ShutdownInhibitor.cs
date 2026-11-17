using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace CraftDriver.App.Services;

/// <summary>O pedaço do systemd-logind que o <see cref="ShutdownInhibitor"/> usa (falso nos testes).</summary>
public interface ILogindShutdown
{
    /// <summary>Trava "delay" de desligamento (Inhibit("shutdown", …, "delay")); descartar solta a trava.</summary>
    Task<IDisposable> InhibitShutdownDelayAsync(string who, string why);

    /// <summary>Sinal PrepareForShutdown(true = vai desligar/reiniciar; false = desligamento cancelado).</summary>
    Task<IDisposable> WatchPrepareForShutdownAsync(Action<bool> handler);
}

/// <summary>
/// Desligar/reiniciar o PC com o app aberto: sem trava, o systemd desmonta ~/OneDrive e mata o processo no meio das
/// cópias offline (a pasta ficava com cópias pela metade e a montagem seguinte era recusada). Com a trava "delay" do
/// logind, o desligamento espera (até InhibitDelayMaxSec, 5 s por padrão) o app encerrar como no "Sair": desmontar,
/// gravar as cópias dos fixados, parar o motor. O processo saindo fecha o descritor e solta a trava.
/// Sem logind (contêiner, outro init) só registra no log: o resto do app não depende disso.
/// </summary>
public sealed class ShutdownInhibitor(ILogindShutdown logind, Action quit, Action<string>? log = null) : IDisposable
{
    public const string Why = "Gravar as cópias offline dos arquivos fixados e desmontar a pasta do OneDrive";

    IDisposable? _lock, _watch;
    int _requested;
    volatile bool _disposed;

    /// <summary>Segurando a trava de desligamento?</summary>
    public bool Holding => _lock != null && !_disposed;

    /// <summary>Quantas vezes o desligamento pediu o encerramento (no máximo 1 por desligamento).</summary>
    public int QuitRequests { get; private set; }

    public async Task StartAsync()
    {
        try
        {
            // o sinal antes da trava: um desligamento que comece entre os dois ainda é visto
            var watch = await logind.WatchPrepareForShutdownAsync(OnPrepareForShutdown).ConfigureAwait(false);
            var held = await logind.InhibitShutdownDelayAsync("CraftDriver", Why).ConfigureAwait(false);
            lock (this)
            {
                if (_disposed) { held.Dispose(); watch.Dispose(); return; }
                _watch = watch;
                _lock = held;
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Sem trava de desligamento do logind ({ex.GetType().Name}: {ex.Message}); " +
                        "desligar o PC com o app aberto pode não deixar as cópias offline completas");
        }
    }

    void OnPrepareForShutdown(bool starting)
    {
        if (!starting)
        {
            Interlocked.Exchange(ref _requested, 0); // cancelado (ex.: outro inibidor recusou): vale para o próximo
            return;
        }
        if (Interlocked.Exchange(ref _requested, 1) == 1 || _disposed) return;
        QuitRequests++;
        log?.Invoke("Desligamento do sistema: encerrando o CraftDriver antes de o systemd desmontar a pasta");
        quit();
    }

    public void Dispose()
    {
        lock (this)
        {
            _disposed = true;
            _watch?.Dispose();
            _lock?.Dispose();
        }
    }
}

/// <summary>logind real pelo barramento do sistema (Tmds.DBus.Protocol, como o DesktopNotifier).</summary>
public sealed class LogindDBus(string? address = null) : ILogindShutdown, IDisposable
{
    const string Service = "org.freedesktop.login1", ObjPath = "/org/freedesktop/login1", Iface = "org.freedesktop.login1.Manager";
    static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);
    readonly SemaphoreSlim _gate = new(1, 1);
    DBusConnection? _conn;

    async Task<DBusConnection> ConnectionAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_conn != null) return _conn;
            var conn = new DBusConnection(address ?? DBusAddress.System ?? throw new InvalidOperationException("sem barramento do sistema"));
            try { await conn.ConnectAsync().AsTask().WaitAsync(CallTimeout).ConfigureAwait(false); }
            catch { conn.Dispose(); throw; }
            return _conn = conn;
        }
        finally { _gate.Release(); }
    }

    public async Task<IDisposable> InhibitShutdownDelayAsync(string who, string why)
    {
        var conn = await ConnectionAsync().ConfigureAwait(false);
        MessageBuffer msg;
        using (var w = conn.GetMessageWriter())
        {
            w.WriteMethodCallHeader(Service, ObjPath, Iface, "Inhibit", "ssss", MessageFlags.None);
            w.WriteString("shutdown");
            w.WriteString(who);
            w.WriteString(why);
            w.WriteString("delay");
            msg = w.CreateMessage();
        }
        return await conn.CallMethodAsync(msg, static (m, _) => m.GetBodyReader().ReadHandle<SafeFileHandle>()
            ?? throw new InvalidOperationException("o logind não devolveu o descritor da trava"), null)
            .WaitAsync(CallTimeout).ConfigureAwait(false);
    }

    public async Task<IDisposable> WatchPrepareForShutdownAsync(Action<bool> handler)
    {
        var conn = await ConnectionAsync().ConfigureAwait(false);
        // emitOnCapturedContext=false: roda no laço de leitura; o handler só despacha (OnShutdownSignal posta na UI)
        return await conn.WatchSignalAsync(Service, ObjPath, Iface, "PrepareForShutdown",
            static (m, _) => m.GetBodyReader().ReadBool(),
            n => { if (n.HasValue) handler(n.Value); }, ObserverFlags.None, false, null).ConfigureAwait(false);
    }

    public void Dispose() => _conn?.Dispose();
}
