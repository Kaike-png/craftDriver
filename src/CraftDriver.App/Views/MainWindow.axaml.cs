using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CraftDriver.App.Services;
using CraftDriver.App.ViewModels;
using CraftDriver.Core.Config;

namespace CraftDriver.App.Views;

public partial class MainWindow : Window
{
    bool _placementRestored;
    Avalonia.Size _normalSize;
    Avalonia.PixelPoint? _normalPosition;

    /// <summary>
    /// Grava tamanho/posição/maximizado no config.json. Roda em todo fechamento (inclusive o que só esconde na
    /// bandeja e o do "Sair"). A posição só é lembrada se o sistema informou alguma (no Wayland pode não haver).
    /// </summary>
    internal void SavePlacement()
    {
        if (DataContext is not MainViewModel vm) return;
        if (WindowState == WindowState.Normal)
        {
            _normalSize = ClientSize;
            _normalPosition = Position;
        }
        WindowPlacement.Store(vm.Host.Config, _normalSize, _normalPosition, WindowState == WindowState.Maximized);
        try { vm.Host.Config.Save(); }
        catch (Exception ex) { vm.Host.Activity.Add(CraftDriver.Core.Sync.ActivityKind.Error, "Não foi possível salvar o tamanho da janela: " + ex.Message); }
    }

    /// <summary>
    /// Diálogos (ShowDialog) exigem dona visível: com a janela escondida na bandeja, um pedido vindo de fora
    /// ("Entrar novamente" da notificação, confirmação pela bandeja) lançava "Cannot show window with
    /// non-visible owner" num async void e derrubava o processo. Todo handler que abre diálogo chama isto antes.
    /// </summary>
    void EnsureVisibleOwner()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
    }

    public MainWindow()
    {
        InitializeComponent();
        Icon = App.LoadIcon("cloud");
        // último tamanho/posição no estado normal (maximizado não sobrescreve o tamanho a restaurar)
        PositionChanged += (_, _) => { if (WindowState == WindowState.Normal && IsVisible) _normalPosition = Position; };
        SizeChanged += (_, _) => { if (WindowState == WindowState.Normal && IsVisible) _normalSize = ClientSize; };
        Closing += (_, _) => SavePlacement();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            if (!_placementRestored)
            {
                _placementRestored = true;
                WindowPlacement.Restore(this, vm.Host.Config);
                _normalSize = new Avalonia.Size(Width, Height);
                if (vm.Host.Config.WindowX is { } x && vm.Host.Config.WindowY is { } y && WindowStartupLocation == WindowStartupLocation.Manual)
                    _normalPosition = new Avalonia.PixelPoint(x, y);
            }
            vm.AddAccountRequested += async reauth =>
            {
                EnsureVisibleOwner();
                var dlg = new LoginWindow(vm.Host, reauth);
                await dlg.ShowDialog(this);
                return dlg.Success;
            };
            vm.AddLibraryRequested += async acc =>
            {
                EnsureVisibleOwner();
                var dlg = new AddLibraryWindow(vm.Host, acc);
                await dlg.ShowDialog(this);
                if (dlg.Added) vm.LibraryAdded();
            };
            vm.ChooseFoldersRequested += async folders =>
            {
                EnsureVisibleOwner();
                await new ChooseFoldersWindow(folders).ShowDialog(this);
                vm.RefreshDrives();
            };
            vm.PickFolderRequested += async () =>
            {
                EnsureVisibleOwner();
                var start = await StorageProvider.TryGetFolderFromPathAsync(vm.Host.Config.MountPoint);
                var res = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Pasta para manter sempre neste dispositivo",
                    SuggestedStartLocation = start,
                    AllowMultiple = false,
                });
                return res.Count > 0 ? res[0].TryGetLocalPath() : null;
            };
            vm.ConfirmRequested += async request =>
            {
                EnsureVisibleOwner();
                var dlg = new ConfirmWindow(request);
                await dlg.ShowDialog(this);
                return dlg.Result;
            };
            vm.OfflineDeletionsRequested += async request =>
            {
                EnsureVisibleOwner();
                var dlg = new OfflineDeletionsWindow(request);
                await dlg.ShowDialog(this);
                return dlg.Result;
            };
            vm.PickSettingsFolderRequested += async title =>
            {
                EnsureVisibleOwner();
                var res = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
                return res.Count > 0 ? res[0].TryGetLocalPath() : null;
            };
            // a saída já foi confirmada pelo RestartCommand: agenda o relançamento e sai sem perguntar de novo
            vm.RestartRequested += () =>
            {
                AppRestart.ScheduleRelaunch();
                vm.QuitConfirmed();
            };
        };
    }
}
