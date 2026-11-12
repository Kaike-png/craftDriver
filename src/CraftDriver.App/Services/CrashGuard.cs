using Avalonia.Threading;
using CraftDriver.Core.Config;
using CraftDriver.Core.Sync;

namespace CraftDriver.App.Services;

/// <summary>
/// Tratador global de exceções. Antes, uma exceção num comando async/async void (desvincular conta, restaurar
/// exclusões, Sair com o disco cheio…) derrubava o processo sem nenhuma linha no craftdriver.log.
/// <list type="bullet">
/// <item>Thread de UI (<see cref="Dispatcher.UnhandledException"/>): registra, mostra a mensagem e segue — o
/// comando falhou, o app não.</item>
/// <item>Task não observada: registra e marca como observada.</item>
/// <item><see cref="AppDomain.UnhandledException"/> (outras threads): não dá para impedir o fim do processo;
/// registra no log para o diagnóstico.</item>
/// </list>
/// </summary>
public static class CrashGuard
{
    static Func<ActivityLog?>? _activity;
    static Action<string>? _showError;
    static bool _installed;

    /// <param name="activity">Log de atividades (null enquanto o AppHost não existe: grava direto no arquivo).</param>
    /// <param name="showError">Mostra a mensagem ao usuário (thread de UI); null = só registra.</param>
    public static void Install(Func<ActivityLog?> activity, Action<string>? showError)
    {
        _activity = activity;
        _showError = showError;
        if (_installed) return;
        _installed = true;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        Dispatcher.UIThread.UnhandledException += OnDispatcherException;
    }

    /// <summary>Testes: desfaz a instalação.</summary>
    public static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainException;
        TaskScheduler.UnobservedTaskException -= OnUnobserved;
        Dispatcher.UIThread.UnhandledException -= OnDispatcherException;
        _activity = null;
        _showError = null;
        lock (RepeatsLock)
        {
            Repeats.Clear();
            _flushTimer?.Dispose();
            _flushTimer = null;
            _windowStart = default;
        }
    }

    /// <summary>Texto curto para a tela.</summary>
    public static string UserMessage(Exception ex) =>
        $"Erro inesperado: {ex.GetBaseException().Message} (detalhes no log)";

    /// <summary>Janela de agrupamento: a mesma falha (onde + tipo + mensagem) só é registrada uma vez por janela.</summary>
    internal static TimeSpan RepeatWindow { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Falhas distintas registradas por janela (proteção contra mensagens variáveis em laço).</summary>
    const int MaxDistinctPerWindow = 20;

    sealed class Repeat
    {
        public DateTime WindowStart;
        public int Suppressed;
        public string Where = "";
        public string Message = "";
    }

    static readonly Dictionary<string, Repeat> Repeats = new();
    static readonly Lock RepeatsLock = new();
    static DateTime _windowStart;
    static int _distinctInWindow;
    static Timer? _flushTimer;

    /// <summary>
    /// Limite de taxa: exceção determinística em layout/binding ou dentro do próprio <c>RefreshActivity</c>
    /// (Report → Activity.Add → Changed → Post(RefreshActivity) → exceção…) inundava log e Atividade. A 1ª de cada
    /// falha na janela é registrada inteira; as repetições só contam e saem num resumo ("repetiu N vezes") quando a
    /// janela fecha. true = registrar esta.
    /// </summary>
    static bool ShouldReport(Exception ex, string where, out bool showToUser, out string? previous)
    {
        previous = null;
        var msg = ex.GetBaseException().Message;
        var key = where + "\n" + ex.GetBaseException().GetType().FullName + "\n" + msg;
        var now = DateTime.UtcNow;
        lock (RepeatsLock)
        {
            if (now - _windowStart >= RepeatWindow)
            {
                _windowStart = now;
                _distinctInWindow = 0;
            }
            if (Repeats.TryGetValue(key, out var r) && now - r.WindowStart < RepeatWindow)
            {
                r.Suppressed++;
                ArmFlush();
                showToUser = false;
                return false;
            }
            if (_distinctInWindow >= MaxDistinctPerWindow)
            {
                // chave "": balde das falhas distintas além do teto desta janela
                if (!Repeats.TryGetValue("", out var overflow) || now - overflow.WindowStart >= RepeatWindow)
                    Repeats[""] = overflow = new Repeat { WindowStart = now, Where = "várias", Message = "outras falhas inesperadas" };
                overflow.Suppressed++;
                ArmFlush();
                showToUser = false;
                return false;
            }
            _distinctInWindow++;
            if (r is { Suppressed: > 0 }) // janela anterior desta falha fechou com repetições ainda não resumidas
                previous = $"Erro inesperado ({r.Where}) repetiu {r.Suppressed} vez(es) em {RepeatWindow.TotalSeconds:0} s: {r.Message}";
            Repeats[key] = new Repeat { WindowStart = now, Where = where, Message = msg };
            showToUser = true;
            return true;
        }
    }

    static void ArmFlush() => _flushTimer ??= new Timer(_ => FlushRepeats(), null, RepeatWindow, Timeout.InfiniteTimeSpan);

    /// <summary>Resumo das repetições suprimidas cujas janelas fecharam (chamado pelo timer; testes chamam direto).</summary>
    internal static void FlushRepeats(bool all = false)
    {
        var lines = new List<string>();
        lock (RepeatsLock)
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
            var now = DateTime.UtcNow;
            foreach (var (key, r) in Repeats.ToList())
            {
                if (!all && now - r.WindowStart < RepeatWindow) continue;
                if (r.Suppressed > 0)
                    lines.Add($"Erro inesperado ({r.Where}) repetiu {r.Suppressed} vez(es) em {RepeatWindow.TotalSeconds:0} s: {r.Message}");
                Repeats.Remove(key);
            }
            if (Repeats.Values.Any(r => r.Suppressed > 0)) ArmFlush();
        }
        foreach (var l in lines) Write(l, null);
    }

    /// <summary>Registra a exceção no craftdriver.log (pelo log de atividades, se houver). Nunca lança.</summary>
    public static void Report(Exception ex, string where) => Report(ex, where, out _);

    static void Report(Exception ex, string where, out bool reported)
    {
        reported = false;
        bool show;
        string? previous = null;
        try { if (!ShouldReport(ex, where, out show, out previous)) return; }
        catch { show = true; }
        reported = show;
        if (previous != null) Write(previous, null);
        Write($"Erro inesperado ({where}): {ex.GetBaseException().Message}", $"{where}: {ex}");
    }

    /// <param name="summary">Linha da Atividade.</param>
    /// <param name="detail">Detalhe só no log (null = a própria linha).</param>
    static void Write(string summary, string? detail)
    {
        try
        {
            var log = _activity?.Invoke();
            if (log != null)
            {
                log.Add(ActivityKind.Error, summary);
                if (detail != null) log.Log("ERRO", detail);
            }
            else
            {
                Directory.CreateDirectory(AppPaths.StateDir);
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [ERRO] {detail ?? summary}\n");
            }
        }
        catch { /* o log é o último recurso; sem ele, resta o stderr */ }
        try { Console.Error.WriteLine($"CraftDriver: {detail ?? summary}"); }
        catch { }
    }

    static void OnDispatcherException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Report(e.Exception, "interface", out var show);
        if (!show) return; // repetição: já está na tela/no log
        try { _showError?.Invoke(UserMessage(e.Exception)); }
        catch (Exception again) { Report(again, "mensagem de erro"); }
    }

    static void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        Report(e.Exception, "tarefa em segundo plano");
    }

    static void OnDomainException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) Report(ex, e.IsTerminating ? "fatal" : "thread");
    }
}
