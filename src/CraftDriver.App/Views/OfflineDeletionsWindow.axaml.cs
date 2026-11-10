using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using CraftDriver.App.ViewModels;

namespace CraftDriver.App.Views;

/// <summary>
/// Pergunta da abertura sobre arquivos apagados das cópias offline com o app fechado. <see cref="Result"/>: true =
/// apagar no OneDrive, false = manter (Enter/padrão), null = fechou sem decidir (Esc ou o X da janela).
/// </summary>
public partial class OfflineDeletionsWindow : Window
{
    public OfflineDeletionsWindow() => InitializeComponent();

    public OfflineDeletionsWindow(OfflineDeletionsRequest request) : this()
    {
        Title = request.Title + " — craftDriver";
        TitleText.Text = request.Title;
        BodyText.Text = request.Text;
        PathsList.ItemsSource = request.Paths;
        AutomationProperties.SetName(this, request.Title);
        DeleteButton.Click += (_, _) => { Result = true; Close(true); };
        KeepButton.Click += (_, _) => { Result = false; Close(false); };
        // Esc fecha sem decidir (não vale como "Manter")
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close(null);
        };
        Opened += (_, _) => KeepButton.Focus();
    }

    public bool? Result { get; private set; }
}
