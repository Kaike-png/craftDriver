using Avalonia.Controls;
using CraftDriver.App.Views;
using CraftDriver.Core.Config;

namespace CraftDriver.App.Services;

public enum NoTrayChoice { Cancel, Minimize, Quit }

/// <summary>
/// Fechar a janela principal (X / Alt+F4):
/// - com host de bandeja (StatusNotifier): esconde e o app segue na bandeja; na primeira vez avisa por
///   notificação que ele continua lá (flag <see cref="AppConfig.TrayNoticeShown"/>);
/// - sem host: pergunta (encerrar / minimizar / cancelar) — esconder deixaria o app invisível.
/// A consulta ao D-Bus é feita na hora de fechar: o painel pode ter subido (ou caído) depois do início.
/// </summary>
public sealed class CloseToTrayBehavior
{
    public const string NoticeTitle = "O craftDriver continua em execução";
    public const string NoticeBody = "A janela foi fechada, mas a sincronização continua. Para abrir de novo ou encerrar, use o ícone do craftDriver na bandeja.";

    readonly AppConfig _cfg;
    readonly Func<Task<bool>> _hasTrayHost;
    readonly Action<string, string> _notify;
    readonly Func<Window, Task<NoTrayChoice>> _askNoTray;
    readonly Action _quit;
    bool _busy;

    public CloseToTrayBehavior(AppConfig cfg, Func<Task<bool>> hasTrayHost, Action<string, string> notify,
        Func<Window, Task<NoTrayChoice>> askNoTray, Action quit)
    {
        _cfg = cfg;
        _hasTrayHost = hasTrayHost;
        _notify = notify;
        _askNoTray = askNoTray;
        _quit = quit;
    }

    /// <summary>Diálogo padrão (<see cref="NoTrayCloseWindow"/>).</summary>
    public static async Task<NoTrayChoice> AskWithDialog(Window owner, string mountPoint)
    {
        var dlg = new NoTrayCloseWindow(mountPoint);
        await dlg.ShowDialog(owner);
        return dlg.Choice;
    }

    /// <summary>Último desfecho (testes).</summary>
    public string? LastOutcome { get; private set; }

    /// <summary>Tarefa da última decisão em andamento (testes esperam por ela).</summary>
    public Task? Pending { get; private set; }

    /// <param name="quitting">true quando o app está encerrando: aí fechar é fechar de verdade.</param>
    public void Attach(Window w, Func<bool> quitting)
    {
        w.Closing += (_, e) =>
        {
            if (quitting()) return;
            e.Cancel = true;
            if (_busy) return; // já há uma decisão em andamento (ex.: diálogo aberto)
            Pending = DecideAsync(w);
        };
    }

    async Task DecideAsync(Window w)
    {
        _busy = true;
        try
        {
            bool tray;
            try { tray = await _hasTrayHost(); }
            catch { tray = false; }
            if (tray)
            {
                w.Hide();
                LastOutcome = "bandeja";
                if (!_cfg.TrayNoticeShown)
                {
                    _cfg.TrayNoticeShown = true;
                    _cfg.Save();
                    _notify(NoticeTitle, NoticeBody);
                }
                return;
            }
            var choice = await _askNoTray(w);
            LastOutcome = choice.ToString();
            switch (choice)
            {
                case NoTrayChoice.Minimize:
                    w.WindowState = WindowState.Minimized;
                    break;
                case NoTrayChoice.Quit:
                    _quit();
                    break;
            }
        }
        finally { _busy = false; }
    }
}
