using System.Collections.Concurrent;
using System.Diagnostics;
using Tmds.DBus.Protocol;

namespace CraftDriver.App.Services;

/// <summary>
/// Botão de uma notificação. <see cref="OnInvoke"/> roda numa thread do pool (nunca na do motor nem na
/// de leitura do D-Bus), então pode demorar; para mexer na interface, despache para a thread de UI.
/// </summary>
public sealed record NotificationAction(string Id, string Label, Action OnInvoke)
{
    /// <summary>Id que o servidor de notificações usa para o clique no corpo da notificação (sem botão).</summary>
    public const string Default = "default";
}

/// <summary>
/// Notificações da área de trabalho pelo D-Bus (org.freedesktop.Notifications), com ações e a dica
/// desktop-entry=craftdriver (o servidor agrupa e mostra o nome/ícone do app). Mantém uma conexão de
/// sessão aberta para receber ActionInvoked / NotificationClosed. Sem D-Bus (variável ausente, barramento
/// fora do ar ou chamada falhou) cai para o <c>notify-send</c> — sem botões, só título e texto.
/// </summary>
public sealed class DesktopNotifier : IDisposable
{
    const string Service = "org.freedesktop.Notifications";
    const string ObjPath = "/org/freedesktop/Notifications";
    const string Iface = "org.freedesktop.Notifications";
    static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    readonly string? _address;
    readonly string _notifySend;
    readonly string _appName;
    readonly string _desktopEntry;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly ConcurrentDictionary<uint, IReadOnlyList<NotificationAction>> _callbacks = new();
    DBusConnection? _conn;
    readonly List<IDisposable> _watches = [];
    bool _disposed;

    /// <summary>Diagnóstico (falhas do D-Bus, uso do fallback). Padrão: nada.</summary>
    public Action<string>? Log { get; set; }

    /// <param name="busAddress">Endereço do barramento de sessão; null/vazio = sem D-Bus (só notify-send).</param>
    /// <param name="notifySend">Executável do fallback (nome no PATH ou caminho).</param>
    public DesktopNotifier(string? busAddress, string notifySend = "notify-send", string appName = "craftDriver", string desktopEntry = "craftdriver")
    {
        _address = string.IsNullOrWhiteSpace(busAddress) ? null : busAddress;
        _notifySend = notifySend;
        _appName = appName;
        _desktopEntry = desktopEntry;
    }

    /// <summary>
    /// Endereço do barramento de sessão do ambiente: DBUS_SESSION_BUS_ADDRESS ou, sem ela, o soquete padrão
    /// do systemd ($XDG_RUNTIME_DIR/bus). Null quando nenhum existe.
    /// </summary>
    public static string? SessionBusAddress()
    {
        if (Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") is { Length: > 0 } a) return a;
        if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } rt && File.Exists(Path.Combine(rt, "bus")))
            return "unix:path=" + Path.Combine(rt, "bus");
        return null;
    }

    /// <summary>Notificações com ações ainda abertas (callbacks guardados até NotificationClosed).</summary>
    internal int PendingCallbacks => _callbacks.Count;

    /// <summary>
    /// Mostra a notificação. Devolve o id do servidor quando foi pelo D-Bus, ou null quando usou o
    /// notify-send (ou nada funcionou — aí escreve no console). Nunca lança.
    /// </summary>
    public async Task<uint?> NotifyAsync(string title, string body, string icon, IReadOnlyList<NotificationAction> actions)
    {
        if (_address != null && !_disposed)
        {
            // uma nova tentativa com conexão nova: a antiga pode ter caído (reinício do barramento/servidor)
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var conn = await ConnectionAsync().ConfigureAwait(false);
                    return await SendAsync(conn, title, body, icon, actions).WaitAsync(CallTimeout).ConfigureAwait(false);
                }
                catch (DBusErrorReplyException ex)
                {
                    // a conexão está boa; o barramento respondeu erro (ex.: nenhum servidor de notificações)
                    Log?.Invoke($"Notificação pelo D-Bus recusada ({ex.ErrorName}); usando notify-send");
                    break;
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"Notificação pelo D-Bus falhou ({ex.GetType().Name}: {ex.Message})");
                    await ResetAsync().ConfigureAwait(false);
                }
            }
        }
        RunNotifySend(title, body, icon);
        return null;
    }

    async Task<DBusConnection> ConnectionAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_conn != null) return _conn;
            var conn = new DBusConnection(_address!);
            try
            {
                await conn.ConnectAsync().ConfigureAwait(false);
                // emitOnCapturedContext=false: o handler roda no laço de leitura; ele só despacha para o pool
                _watches.Add(await conn.WatchSignalAsync(null!, ObjPath, Iface, "ActionInvoked",
                    static (m, _) => { var r = m.GetBodyReader(); return (Id: r.ReadUInt32(), Key: r.ReadString()); },
                    n => { if (n.HasValue) OnActionInvoked(n.Value.Id, n.Value.Key); }, ObserverFlags.None, false, null).ConfigureAwait(false));
                _watches.Add(await conn.WatchSignalAsync(null!, ObjPath, Iface, "NotificationClosed",
                    static (m, _) => m.GetBodyReader().ReadUInt32(),
                    n => { if (n.HasValue) _callbacks.TryRemove(n.Value, out _); }, ObserverFlags.None, false, null).ConfigureAwait(false));
            }
            catch
            {
                DisposeWatches();
                conn.Dispose();
                throw;
            }
            _conn = conn;
            return conn;
        }
        finally { _gate.Release(); }
    }

    Task<uint> SendAsync(DBusConnection conn, string title, string body, string icon, IReadOnlyList<NotificationAction> actions)
    {
        MessageBuffer msg;
        using (var w = conn.GetMessageWriter())
        {
            w.WriteMethodCallHeader(Service, ObjPath, Iface, "Notify", "susssasa{sv}i", MessageFlags.None);
            w.WriteString(_appName);
            w.WriteUInt32(0); // replaces_id
            w.WriteString(icon);
            w.WriteString(title);
            w.WriteString(body);
            var arr = w.WriteArrayStart(DBusType.String);
            foreach (var a in actions)
            {
                w.WriteString(a.Id);
                w.WriteString(a.Label);
            }
            w.WriteArrayEnd(arr);
            var dict = w.WriteDictionaryStart();
            w.WriteDictionaryEntryStart();
            w.WriteString("desktop-entry");
            w.WriteVariantString(_desktopEntry);
            w.WriteDictionaryEnd(dict);
            w.WriteInt32(-1); // expire_timeout: padrão do servidor
            msg = w.CreateMessage();
        }
        // o leitor da resposta roda no laço de recepção, antes de qualquer sinal seguinte do servidor:
        // o callback já está registrado quando um ActionInvoked desse id chegar
        return conn.CallMethodAsync(msg, (m, _) =>
        {
            var id = m.GetBodyReader().ReadUInt32();
            if (actions.Count > 0) _callbacks[id] = actions;
            return id;
        }, null);
    }

    void OnActionInvoked(uint id, string key)
    {
        if (!_callbacks.TryGetValue(id, out var actions)) return; // de outro app, ou já fechada
        var action = actions.FirstOrDefault(a => a.Id == key);
        if (action == null) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { action.OnInvoke(); }
            catch (Exception ex) { Log?.Invoke($"Ação \"{action.Label}\" da notificação falhou: {ex.Message}"); }
        });
    }

    void RunNotifySend(string title, string body, string icon)
    {
        try
        {
            var psi = new ProcessStartInfo(_notifySend) { UseShellExecute = false };
            foreach (var a in new[] { "-a", _appName, "-i", icon, "-h", "string:desktop-entry:" + _desktopEntry, "--", title, body })
                psi.ArgumentList.Add(a);
            var p = Process.Start(psi);
            if (p != null)
            {
                p.EnableRaisingEvents = true; // recolhe o processo ao terminar, sem esperar por ele
                p.Exited += (_, _) => p.Dispose();
            }
        }
        catch { Console.WriteLine($"{title}: {body}"); }
    }

    async Task ResetAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            DisposeWatches();
            _conn?.Dispose();
            _conn = null;
            _callbacks.Clear();
        }
        finally { _gate.Release(); }
    }

    void DisposeWatches()
    {
        foreach (var w in _watches)
            try { w.Dispose(); } catch { }
        _watches.Clear();
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _disposed = true;
            DisposeWatches();
            _conn?.Dispose();
            _conn = null;
            _callbacks.Clear();
        }
        finally { _gate.Release(); }
    }
}
