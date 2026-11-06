using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using CraftDriver.App.Services;
using CraftDriver.App.ViewModels;
using CraftDriver.App.Views;
using CraftDriver.Core.Auth;
using CraftDriver.Core.Config;
using CraftDriver.Core.Sync;

namespace CraftDriver.App;

public partial class App : Application
{
    public static AppHost Host { get; set; } = null!;

    /// <summary>O host, ou null enquanto ele não existe (espera do chaveiro).</summary>
    public static AppHost? HostOrNull => Host;

    /// <summary>Servidor IPC aberto pelo Program antes do Avalonia ("ainda iniciando"); o AppHost o assume.</summary>
    public static CraftDriver.Core.Ipc.IpcServer? EarlyIpc { get; set; }

    /// <summary>IPC "show" durante a inicialização: mostra a janela de espera (ou a principal, se já houver).</summary>
    public static void RequestStartupShow() => Dispatcher.UIThread.Post(() => (Current as App)?.OnTrayClicked());

    /// <summary>IPC "quit" durante a inicialização: o mesmo "Sair" da janela de espera.</summary>
    public static void RequestStartupQuit() => Dispatcher.UIThread.Post(() => (Current as App)?.QuitFromSignal());
    public static bool StartMinimized { get; set; }

    /// <summary>Configuração lida pelo Program antes do Avalonia (tema e decisões do início).</summary>
    public static AppConfig? StartupConfig { get; set; }

    /// <summary>Nota da decisão de escala sob XWayland (DisplayScaling), registrada no log ao iniciar.</summary>
    public static string? ScalingNote { get; set; }

    /// <summary>Cria o AppHost (espera o chaveiro). Trocável para testes/diagnóstico.</summary>
    internal static Func<AppConfig, Action<string>, CancellationToken, Task<AppHost>> HostFactory { get; set; } =
        (cfg, status, skip) => AppHost.CreateAsync(cfg, status, skip);

    StartupCoordinator<AppHost>? _startup;
    bool _noTrayAtStart;
    CloseToTrayBehavior? _closeBehavior;

    MainViewModel _vm = null!;
    MainWindow? _window;
    TrayIcon? _tray;
    NativeMenuItem? _pauseItem;
    EngineStatus? _lastStatus;
    IClassicDesktopStyleApplicationLifetime? _desktop;
    bool _quitting;

    public static WindowIcon LoadIcon(string name) =>
        new(AssetLoader.Open(new Uri($"avares://CraftDriver.App/Assets/{name}.png")));

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            CrashGuard.Install(() => HostOrNull?.Activity, ShowUnexpectedError);
            // SIGTERM/SIGINT/SIGHUP: o mesmo "Sair" da bandeja (desmonta, grava posição/configuração, apaga o socket)
            Program.OnShutdownSignal = () => Dispatcher.UIThread.Post(QuitFromSignal);
            var cfg = Host?.Config ?? StartupConfig ?? AppConfig.Load();
            ThemeService.Apply(cfg);
            if (Host != null) InitWithHost(showWindow: StartupPlan.ShowWindowAtStart(StartMinimized, cfg.Accounts.Count, true));
            else _ = StartAsync(desktop, cfg);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Erro inesperado na interface (CrashGuard): mensagem na barra da janela; sem janela, notificação.</summary>
    void ShowUnexpectedError(string text)
    {
        if (_vm != null && _window is { IsVisible: true }) _vm.ShowError(text);
        else HostOrNull?.Notify("craftDriver — erro", text, []);
    }

    void QuitFromSignal()
    {
        if (_startup != null) _startup.Quit(); // ainda esperando o chaveiro: StartAsync encerra
        else if (_vm != null) Quit();
        else _desktop?.Shutdown();
    }

    /// <summary>
    /// Início com a interface já de pé: o AppHost (que espera o chaveiro, até 60 s no começo da sessão) é
    /// criado em segundo plano. Se demorar, aparece a janela de espera; minimizado com bandeja, só o ícone
    /// ("iniciando…") — clicar nele mostra a espera. Sair da espera encerra o processo.
    /// </summary>
    async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop, AppConfig cfg)
    {
        var minimized = StartMinimized && cfg.Accounts.Count > 0;
        _startup = new StartupCoordinator<AppHost>((status, skip) => HostFactory(cfg, status, skip));
        var run = _startup.RunAsync(showSplash: !minimized);
        var tray = true;
        if (minimized)
        {
            // no login o painel pode subir depois do craftDriver: espera um pouco por um host de bandeja
            tray = await TrayHostProbe.WaitForHostAsync(DesktopNotifier.SessionBusAddress(), TimeSpan.FromSeconds(3));
            if (tray && !run.IsCompleted) CreateTray(starting: true);
            else if (!tray)
            {
                _noTrayAtStart = true;
                _startup?.ShowSplash();
            }
        }
        var host = await run;
        _startup = null;
        if (host == null)
        {
            if (_tray != null) _tray.IsVisible = false;
            desktop.Shutdown();
            return;
        }
        Host = host;
        InitWithHost(StartupPlan.ShowWindowAtStart(StartMinimized, cfg.Accounts.Count, tray));
    }

    void InitWithHost(bool showWindow)
    {
        var desktop = _desktop!;
        {
            _vm = new MainViewModel(Host);
            // comandos IPC que mexem na configuração rodam na thread de UI (antes do Start, que abre o socket)
            Host.RunOnMain = a => Dispatcher.UIThread.InvokeAsync(a).GetTask();
            Host.Start(EarlyIpc);
            Host.ShowRequested += OnShowRequested;
            Host.QuitRequested += OnQuitRequested;
            Host.VersionsRequested += OnVersionsRequested;
            Host.TrashRequested += OnTrashRequested;
            _vm.QuitRequested += Quit;
            CreateTray(starting: false);
            _lastStatus = null;
            _vm.Refreshed += UpdateTray;
            UpdateTray();
            desktop.Exit += OnExit;
            if (ScalingNote != null) Host.Activity.Add(ActivityKind.Info, ScalingNote);
            if (_noTrayAtStart)
                Host.Activity.Add(ActivityKind.Info, "Nenhuma área de notificação (bandeja) encontrada: o início minimizado abriu a janela.");
            _closeBehavior = new CloseToTrayBehavior(Host.Config,
                () => TrayHostProbe.IsHostAvailableAsync(DesktopNotifier.SessionBusAddress(), TimeSpan.FromSeconds(1)),
                (t, b) => Host.Notify(t, b, []),
                w => CloseToTrayBehavior.AskWithDialog(w, Host.Config.MountPoint),
                Quit);

            if (showWindow) ShowWindow();
        }
    }

    /// <summary>Ícone durante a inicialização: só "mostrar progresso" e "Sair".</summary>
    void CreateStartingTray()
    {
        var menu = new NativeMenu();
        var open = new NativeMenuItem("Mostrar a inicialização");
        open.Click += (_, _) => _startup?.ShowSplash();
        var quit = new NativeMenuItem("Sair");
        quit.Click += (_, _) => _startup?.Quit();
        menu.Items.Add(open);
        menu.Items.Add(quit);
        _tray = new TrayIcon
        {
            Icon = LoadIcon("cloud-sync"),
            ToolTipText = "craftDriver — " + AuthService.OpeningKeyringStatus,
            Menu = menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => OnTrayClicked();
        TrayIcon.SetIcons(this, [_tray]);
    }

    void OnTrayClicked()
    {
        if (_startup != null) _startup.ShowSplash();
        else if (_vm != null) ShowWindow();
    }

    void CreateTray(bool starting)
    {
        if (starting) { CreateStartingTray(); return; }

        var menu = new NativeMenu();
        var open = new NativeMenuItem("Abrir craftDriver");
        open.Click += (_, _) => ShowWindow();
        var folder = new NativeMenuItem("Abrir pasta");
        folder.Click += (_, _) => Host.OpenFolder(Host.Config.MountPoint);
        var sync = new NativeMenuItem("Sincronizar agora");
        sync.Click += (_, _) => Host.Engine.SyncNow();
        var trash = new NativeMenuItem("Lixeira…");
        trash.Click += (_, _) => OnTrashRequested();
        _pauseItem = new NativeMenuItem("Pausar sincronização");
        _pauseItem.Click += (_, _) => _vm.TogglePauseCommand.Execute(null);
        var quit = new NativeMenuItem("Sair");
        quit.Click += (_, _) => Quit();
        menu.Items.Add(open);
        menu.Items.Add(folder);
        menu.Items.Add(sync);
        menu.Items.Add(trash);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quit);

        if (_tray != null)
        {
            // reaproveita o ícone criado durante a inicialização (o clique já chama OnTrayClicked)
            _tray.Menu = menu;
            _tray.Icon = LoadIcon("cloud");
            _tray.ToolTipText = "craftDriver";
            return;
        }
        _tray = new TrayIcon
        {
            Icon = LoadIcon("cloud"),
            ToolTipText = "craftDriver",
            Menu = menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => OnTrayClicked();
        TrayIcon.SetIcons(this, [_tray]);
    }

    void UpdateTray()
    {
        if (_tray == null) return;
        var st = _vm.CurrentStatus;
        var pending = _vm.PendingCount;
        var transfers = _vm.TransferCount;
        _tray.ToolTipText = $"craftDriver — {AppHost.StatusText(st)}" +
                            (pending + transfers > 0 ? $"\n{pending} alterações na fila, {transfers} transferências" : "");
        if (_pauseItem != null) _pauseItem.Header = Host.Engine.Paused ? "Retomar sincronização" : "Pausar sincronização";
        if (st == _lastStatus) return;
        _lastStatus = st;
        _tray.Icon = LoadIcon(st switch
        {
            EngineStatus.Syncing => "cloud-sync",
            EngineStatus.Paused => "cloud-paused",
            EngineStatus.Offline => "cloud-offline",
            EngineStatus.AuthRequired or EngineStatus.Error or EngineStatus.DeleteConfirmation => "cloud-error",
            EngineStatus.NoAccounts => "cloud-paused",
            _ => "cloud",
        });
    }

    void ShowWindow()
    {
        if (_window == null)
        {
            _window = new MainWindow { DataContext = _vm };
            // fechar a janela mantém o app na bandeja, como no Windows; sem bandeja, pergunta
            _closeBehavior!.Attach(_window, () => _quitting);
            _window.Show();
            _window.Activate();
            return;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = Host.Config.WindowMaximized ? WindowState.Maximized : WindowState.Normal;
        _window.Activate();
    }

    void OnShowRequested() => Dispatcher.UIThread.Post(() => { if (!_quitting) ShowWindow(); });
    void OnQuitRequested() => Dispatcher.UIThread.Post(Quit);

    // janelas abertas pelo menu de contexto/CLI (IPC "versions"/"trash"), uma por arquivo
    readonly Dictionary<long, VersionsWindow> _versionWindows = new();
    TrashWindow? _trashWindow;

    void OnVersionsRequested(long lid) => Dispatcher.UIThread.Post(() =>
    {
        if (_quitting) return;
        if (!_versionWindows.TryGetValue(lid, out var w))
        {
            w = new VersionsWindow(new VersionsViewModel(Host, lid));
            w.Closed += (_, _) => _versionWindows.Remove(lid);
            _versionWindows[lid] = w;
        }
        w.Show();
        w.Activate();
    });

    void OnTrashRequested() => Dispatcher.UIThread.Post(() =>
    {
        if (_quitting) return;
        if (_trashWindow == null)
        {
            _trashWindow = new TrashWindow(new TrashViewModel(Host));
            _trashWindow.Closed += (_, _) => _trashWindow = null;
        }
        _trashWindow.Show();
        _trashWindow.Activate();
    });

    void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e) => StopUpdates();

    void StopUpdates()
    {
        Host.ShowRequested -= OnShowRequested;
        Host.QuitRequested -= OnQuitRequested;
        Host.VersionsRequested -= OnVersionsRequested;
        Host.TrashRequested -= OnTrashRequested;
        _vm.Refreshed -= UpdateTray;
        _vm.QuitRequested -= Quit;
        _vm.Dispose();
        if (_desktop != null) _desktop.Exit -= OnExit;
    }

    async void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        try
        {
            StopUpdates();
            if (_tray != null) _tray.IsVisible = false;
            _window?.Close();
            foreach (var w in _versionWindows.Values.ToList()) w.Close();
            _trashWindow?.Close();
            await Host.ShutdownAsync();
        }
        catch (Exception ex) { CrashGuard.Report(ex, "Sair"); }
        finally { _desktop?.Shutdown(); } // sair sempre sai, mesmo com um passo do encerramento falhando
    }
}
