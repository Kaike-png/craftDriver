using Avalonia.Controls;
using CraftDriver.App.Services;

namespace CraftDriver.App.Views;

/// <summary>
/// Fechar a janela sem host de bandeja: esconder deixaria o app invisível (e sem jeito de reabrir a não ser
/// pelo menu de aplicativos). Pergunta entre encerrar, minimizar ou desistir.
/// </summary>
public partial class NoTrayCloseWindow : Window
{
    public NoTrayChoice Choice { get; private set; } = NoTrayChoice.Cancel;

    public NoTrayCloseWindow() : this("~/OneDrive") { }

    public NoTrayCloseWindow(string mountPoint)
    {
        InitializeComponent();
        try { Icon = App.LoadIcon("cloud"); } catch { }
        BodyText.Text = "O ícone do craftDriver não tem onde aparecer (nenhum painel com bandeja/StatusNotifier está ativo), " +
                        "então esconder a janela deixaria o app sem nenhum sinal na tela.\n\n" +
                        $"Encerrar desmonta {mountPoint} e para a sincronização. Minimizar mantém tudo funcionando " +
                        "(em compositores que não minimizam, como o Hyprland, a janela continua visível).";
        CancelButton.Click += (_, _) => Close(NoTrayChoice.Cancel);
        MinimizeButton.Click += (_, _) => Close(NoTrayChoice.Minimize);
        QuitButton.Click += (_, _) => Close(NoTrayChoice.Quit);
    }

    void Close(NoTrayChoice c)
    {
        Choice = c;
        Close(c as object);
    }
}
