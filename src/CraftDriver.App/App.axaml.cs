using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using CraftDriver.App.Services;
using CraftDriver.App.ViewModels;
using CraftDriver.App.Views;
using CraftDriver.Core.Sync;

namespace CraftDriver.App;

public partial class App : Application
{
    public static AppHost Host { get; set; } = null!;
    public static bool StartMinimized { get; set; }

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
            _vm = new MainViewModel(Host);
            Host.Start();
            Host.ShowRequested += () => Dispatcher.UIThread.Post(ShowWindow);
            Host.QuitRequested += () => Dispatcher.UIThread.Post(Quit);
            _vm.QuitRequested += Quit;
            CreateTray();

            if (!StartMinimized || Host.Config.Accounts.Count == 0) ShowWindow();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) => UpdateTray();
            timer.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }

    void CreateTray()
    {
        var menu = new NativeMenu();
        var open = new NativeMenuItem("Abrir craftDriver");
        open.Click += (_, _) => ShowWindow();
        var folder = new NativeMenuItem("Abrir pasta");
        folder.Click += (_, _) => DesktopIntegration.OpenPath(Host.Config.MountPoint);
        var sync = new NativeMenuItem("Sincronizar agora");
        sync.Click += (_, _) => Host.Engine.SyncNow();
        _pauseItem = new NativeMenuItem("Pausar sincronização");
        _pauseItem.Click += (_, _) => _vm.TogglePauseCommand.Execute(null);
        var quit = new NativeMenuItem("Sair");
        quit.Click += (_, _) => Quit();
        menu.Items.Add(open);
        menu.Items.Add(folder);
        menu.Items.Add(sync);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quit);

        _tray = new TrayIcon
        {
            Icon = LoadIcon("cloud"),
            ToolTipText = "craftDriver",
            Menu = menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, [_tray]);
    }

    void UpdateTray()
    {
        if (_tray == null) return;
        var st = Host.Engine.Status;
        var pending = Host.Db.PendingOps();
        var transfers = Host.Activity.Transfers.Count;
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
            _window.Closing += (_, e) =>
            {
                if (_quitting) return;
                e.Cancel = true; // fechar a janela mantém o app na bandeja, como no Windows
                _window.Hide();
            };
        }
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    async void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        if (_tray != null) _tray.IsVisible = false;
        _window?.Close();
        await Host.ShutdownAsync();
        _desktop?.Shutdown();
    }
}
