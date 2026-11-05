using Avalonia.Controls;
using CraftDriver.App.ViewModels;

namespace CraftDriver.App.Views;

public partial class ChooseFoldersWindow : Window
{
    public ChooseFoldersWindow() => InitializeComponent();

    public ChooseFoldersWindow(ChooseFoldersViewModel vm) : this()
    {
        Icon = App.LoadIcon("cloud");
        DataContext = vm;
        vm.CloseRequested += Close;
    }
}
