using Avalonia.Automation;
using Avalonia.Controls;
using CraftDriver.App.ViewModels;

namespace CraftDriver.App.Views;

/// <summary>Confirmação de ação destrutiva. Esc/Cancelar = não; Enter = sim (o foco começa em Cancelar quando é perigosa).</summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow() => InitializeComponent();

    public ConfirmWindow(ConfirmRequest request) : this()
    {
        Title = request.Title + " — craftDriver";
        TitleText.Text = request.Title;
        BodyText.Text = request.Text;
        AcceptButton.Content = request.Accept;
        if (request.Danger)
        {
            AcceptButton.Classes.Remove("accent"); // não destacar em azul o botão que apaga
            AcceptButton.Classes.Add("danger");
        }
        AutomationProperties.SetName(this, request.Title);
        AcceptButton.Click += (_, _) => { Result = true; Close(true); };
        CancelButton.Click += (_, _) => Close(false);
        Opened += (_, _) => (request.Danger ? CancelButton : AcceptButton).Focus();
    }

    public bool Result { get; private set; }
}
