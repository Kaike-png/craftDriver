using Avalonia.Controls;

namespace CraftDriver.App.Views;

/// <summary>
/// Janela de espera da inicialização: aparece enquanto o cache de tokens espera o chaveiro do sistema
/// (até 60 s no início da sessão). "Continuar sem o chaveiro" desiste da espera; "Sair"/fechar encerra o app.
/// </summary>
public partial class SplashWindow : Window
{
    /// <summary>"Continuar sem o chaveiro".</summary>
    public event Action? SkipRequested;
    /// <summary>"Sair", Esc ou fechar a janela pelo gerenciador de janelas.</summary>
    public event Action? QuitRequested;

    bool _closingByCode;

    public SplashWindow()
    {
        InitializeComponent();
        try { Icon = App.LoadIcon("cloud"); } catch { /* sem assets (designer) */ }
        SkipButton.Click += (_, _) =>
        {
            SkipButton.IsEnabled = false;
            StatusText.Text = "Continuando sem o chaveiro…";
            SkipRequested?.Invoke();
        };
        QuitButton.Click += (_, _) => Close();
        Closed += (_, _) => { if (!_closingByCode) QuitRequested?.Invoke(); };
    }

    public string Status
    {
        get => StatusText.Text ?? "";
        set => StatusText.Text = value;
    }

    /// <summary>Falha na inicialização: mostra o erro e deixa só "Fechar".</summary>
    public void ShowError(string message)
    {
        StatusText.Text = "Não foi possível iniciar o CraftDriver.";
        Busy.IsVisible = false;
        KeyringHint.IsVisible = false;
        SkipButton.IsVisible = false;
        ErrorText.Text = message;
        ErrorPanel.IsVisible = true;
        QuitButton.Content = "Fechar";
    }

    /// <summary>Fecha sem tratar como "Sair" (a inicialização terminou).</summary>
    public void CloseQuietly()
    {
        _closingByCode = true;
        if (IsVisible) Close();
    }
}
