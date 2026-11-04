using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CraftDriver.App.ViewModels;
using CraftDriver.Core.Config;

namespace CraftDriver.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Icon = App.LoadIcon("cloud");
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            vm.AddAccountRequested += async () =>
            {
                var dlg = new LoginWindow(vm.Host);
                await dlg.ShowDialog(this);
                return dlg.Success;
            };
            vm.AddLibraryRequested += async acc =>
            {
                var dlg = new AddLibraryWindow(vm.Host, acc);
                await dlg.ShowDialog(this);
                if (dlg.Added) vm.LibraryAdded();
            };
            vm.PickFolderRequested += async () =>
            {
                var start = await StorageProvider.TryGetFolderFromPathAsync(vm.Host.Config.MountPoint);
                var res = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Pasta para manter sempre neste dispositivo",
                    SuggestedStartLocation = start,
                    AllowMultiple = false,
                });
                return res.Count > 0 ? res[0].TryGetLocalPath() : null;
            };
        };
    }
}
