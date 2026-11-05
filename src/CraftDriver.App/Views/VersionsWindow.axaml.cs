using Avalonia.Controls;
using CraftDriver.App.ViewModels;

namespace CraftDriver.App.Views;

public partial class VersionsWindow : Window
{
    public VersionsWindow() : this(null) { }

    public VersionsWindow(VersionsViewModel? vm)
    {
        InitializeComponent();
        Icon = App.LoadIcon("cloud");
        DataContext = vm;
        CloseButton.Click += (_, _) => Close();
        Opened += async (_, _) =>
        {
            if (DataContext is VersionsViewModel v) await v.ReloadAsync();
        };
    }
}
