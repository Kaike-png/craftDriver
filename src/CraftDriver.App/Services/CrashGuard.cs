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
    }

    /// <summary>Texto curto para a tela.</summary>
    public static string UserMessage(Exception ex) =>
        $"Erro inesperado: {ex.GetBaseException().Message} (detalhes no log)";

    /// <summary>Registra a exceção no craftdriver.log (pelo log de atividades, se houver). Nunca lança.</summary>
    public static void Report(Exception ex, string where)
    {
        try
        {
            var log = _activity?.Invoke();
            if (log != null)
            {
                log.Add(ActivityKind.Error, $"Erro inesperado ({where}): {ex.GetBaseException().Message}");
                log.Log("ERRO", $"{where}: {ex}");
            }
            else
            {
                Directory.CreateDirectory(AppPaths.StateDir);
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [ERRO] {where}: {ex}\n");
            }
        }
        catch { /* o log é o último recurso; sem ele, resta o stderr */ }
        try { Console.Error.WriteLine($"craftDriver: erro inesperado ({where}): {ex}"); }
        catch { }
    }

    static void OnDispatcherException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Report(e.Exception, "interface");
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
