using Avalonia.Controls;
using CraftDriver.App.ViewModels;

namespace CraftDriver.App.Views;

public partial class TrashWindow : Window
{
    public TrashWindow() : this(null) { }

    public TrashWindow(TrashViewModel? vm)
    {
        InitializeComponent();
        Icon = App.LoadIcon("cloud");
        DataContext = vm;
        CloseButton.Click += (_, _) => Close();
        Activated += (_, _) => (DataContext as TrashViewModel)?.Reload();
    }
}
