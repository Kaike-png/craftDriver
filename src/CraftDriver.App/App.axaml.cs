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
    /// <summary>O host (null enquanto ele não existe — espera do chaveiro; teste com <c>Host?.</c>).</summary>
    public static AppHost Host { get; set; } = null!;

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
    /// <summary>"Atualizar para X" (só aparece com versão nova).</summary>
    NativeMenuItem? _updateItem;
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
            CrashGuard.Install(() => Host?.Activity, ShowUnexpectedError);
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
        else Host?.Notify("craftDriver — erro", text, []);
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
        try
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
        catch (Exception ex)
        {
            // chamada com "_ =": sem isto a exceção ficava presa na Task descartada (o OnUnobserved só a vê no GC) e o
            // processo seguia vivo sem janela, segurando trava e socket ("ainda iniciando" para sempre)
            await FailStartAsync(desktop, ex);
        }
    }

    /// <summary>Falha depois da fábrica do AppHost (Start, janela, bandeja): registra, avisa, encerra o que subiu e sai.</summary>
    async Task FailStartAsync(IClassicDesktopStyleApplicationLifetime desktop, Exception ex)
    {
        CrashGuard.Report(ex, "início");
        _quitting = true;
        _startup = null;
        try
        {
            if (_tray != null) _tray.IsVisible = false;
            _window?.Close();
            var text = "O craftDriver não conseguiu iniciar: " + ex.GetBaseException().Message + " (detalhes no log)";
            if (Host is { } h)
            {
                h.Notify("craftDriver não iniciou", text, []);
                await h.ShutdownAsync(); // desmonta e fecha o IPC se o Start chegou a abri-los
            }
            else _ = DesktopIntegration.Notify("craftDriver não iniciou", text);
        }
        catch (Exception again) { CrashGuard.Report(again, "início (encerrando)"); }
        finally { desktop.Shutdown(); }
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
            Host.UpdateNowRequested += OnUpdateNowRequested;
            _vm.QuitRequested += Quit;
            CreateTray(starting: false);
            _lastStatus = null;
            _vm.Refreshed += UpdateTray;
            _vm.PropertyChanged += OnVmPropertyChanged;
            UpdateTray();
            desktop.Exit += OnExit;
            if (ScalingNote != null) Host.Activity.Add(ActivityKind.Info, ScalingNote);
            if (_noTrayAtStart)
                Host.Activity.Add(ActivityKind.Info, "Nenhuma área de notificação (bandeja) encontrada: o início minimizado abriu a janela.");
            _closeBehavior = new CloseToTrayBehavior(Host.Config,
                () => TrayHostProbe.IsHostAvailableAsync(DesktopNotifier.SessionBusAddress(), TimeSpan.FromSeconds(1)),
                (t, b) => Host.Notify(t, b, []),
                w => CloseToTrayBehavior.AskWithDialog(w, Host.Config.MountPoint),
                QuitWithConfirmation);

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
        _updateItem = new NativeMenuItem("Atualizar") { IsVisible = false };
        _updateItem.Click += (_, _) => OnUpdateNowRequested();
        var quit = new NativeMenuItem("Sair");
        quit.Click += (_, _) => QuitWithConfirmation();
        menu.Items.Add(open);
        menu.Items.Add(folder);
        menu.Items.Add(sync);
        menu.Items.Add(trash);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_updateItem);
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
        if (_updateItem != null)
        {
            _updateItem.IsVisible = _vm.UpdateTrayText != null;
            if (_vm.UpdateTrayText != null) _updateItem.Header = _vm.UpdateTrayText;
        }
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

    /// <summary>Cria a janela principal sem mostrar (ela é a dona dos diálogos de confirmação).</summary>
    MainWindow EnsureWindow()
    {
        if (_window != null) return _window;
        _window = new MainWindow { DataContext = _vm };
        // fechar a janela mantém o app na bandeja, como no Windows; sem bandeja, pergunta
        _closeBehavior!.Attach(_window, () => _quitting);
        return _window;
    }

    void ShowWindow()
    {
        if (_window == null)
        {
            EnsureWindow().Show();
            _window!.Activate();
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

    /// <summary>Versão nova encontrada/instalada: o item da bandeja muda na hora (sem esperar o próximo segundo).</summary>
    void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.UpdateTrayText)) UpdateTray();
    }

    /// <summary>
    /// "Atualizar agora" da notificação, da bandeja ou do CLI (IPC "update"): a janela aparece (é dona das
    /// confirmações) na Visão geral, com o painel da atualização, e o fluxo do botão começa.
    /// </summary>
    void OnUpdateNowRequested() => Dispatcher.UIThread.Post(() =>
    {
        if (_quitting) return;
        ShowWindow();
        _vm.SelectedTab = 0;
        if (_vm.UpdateNowCommand.CanExecute(null)) _vm.UpdateNowCommand.Execute(null);
    });

    void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e) => StopUpdates();

    void StopUpdates()
    {
        Host.ShowRequested -= OnShowRequested;
        Host.QuitRequested -= OnQuitRequested;
        Host.VersionsRequested -= OnVersionsRequested;
        Host.TrashRequested -= OnTrashRequested;
        Host.UpdateNowRequested -= OnUpdateNowRequested;
        _vm.Refreshed -= UpdateTray;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.QuitRequested -= Quit;
        _vm.Dispose();
        if (_desktop != null) _desktop.Exit -= OnExit;
    }

    bool _confirmingQuit;

    /// <summary>
    /// "Sair" da bandeja e "Encerrar" do aviso sem bandeja: a mesma confirmação do Ctrl+Q/botão quando há
    /// transferências. Iniciado minimizado não há janela — e sem janela ninguém responde à confirmação (o "Sair" ficaria
    /// sem efeito): ela é criada antes e aparece com o diálogo. Comandos de fora (IPC "quit", sinais) não perguntam.
    /// </summary>
    async void QuitWithConfirmation()
    {
        if (_quitting || _confirmingQuit) return;
        _confirmingQuit = true;
        try
        {
            if (!_vm.HasConfirmHandler) EnsureWindow();
            if (!await _vm.ConfirmQuitAsync()) return;
        }
        catch (Exception ex) { CrashGuard.Report(ex, "confirmação de saída"); } // sair continua saindo
        finally { _confirmingQuit = false; }
        Quit();
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
