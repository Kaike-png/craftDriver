using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CraftDriver.App.Services;
using CraftDriver.Core.Auth;
using CraftDriver.Core.Config;

namespace CraftDriver.App.Views;

/// <summary>
/// Login Microsoft.
/// - Conta pessoal: página de login normal no navegador; no final o usuário cola o endereço
///   "nativeclient?code=…". O código de dispositivo não conclui com contas pessoais usando o
///   Client ID público (a Microsoft redireciona para account.microsoft.com sem autorizar).
/// - Trabalho/escola: código de dispositivo, com o método do navegador como alternativa.
/// </summary>
public partial class LoginWindow : Window
{
    readonly AppHost _host;
    CancellationTokenSource _cts = new();
    string? _url, _code;
    Uri? _authUri, _autoUri;
    TaskCompletionSource<Uri>? _pasted;
    bool _personal;
    public bool Success { get; private set; }

    public LoginWindow() : this(App.Host) { }

    public LoginWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        Icon = App.LoadIcon("cloud");
        CloseButton.Click += (_, _) => Close();
        Closed += (_, _) => _cts.Cancel();
        PersonalButton.Click += async (_, _) => { _personal = true; await StartAutoAsync(openBrowser: true); };
        ReopenAuto.Click += (_, _) => { if (_autoUri != null) DesktopIntegration.OpenPath(_autoUri.AbsoluteUri); };
        SwitchToManual.Click += async (_, _) =>
        {
            _cts.Cancel();
            _cts = new CancellationTokenSource();
            AutoPanel.IsVisible = false;
            await StartBrowserAsync(openBrowser: true);
        };
        WorkButton.Click += async (_, _) => { _personal = false; await StartDeviceCodeAsync(); };
        SwitchToBrowser.Click += async (_, _) =>
        {
            _cts.Cancel();
            _cts = new CancellationTokenSource();
            await StartBrowserAsync(openBrowser: true);
        };
        OpenBrowser.Click += async (_, _) =>
        {
            if (_code != null && Clipboard is { } cb) await cb.SetTextAsync(_code);
            if (_url != null) DesktopIntegration.OpenPath(_url);
            StatusText.Text = "Cole o código no navegador e conclua o login…";
        };
        OpenLoginPage.Click += (_, _) =>
        {
            if (_authUri != null) DesktopIntegration.OpenPath(_authUri.AbsoluteUri);
        };
        FinishBrowser.Click += (_, _) => SubmitPasted(showErrors: true);
        // colar o endereço completo já conclui, sem precisar clicar
        PasteBox.TextChanged += (_, _) => SubmitPasted(showErrors: false);
    }

    /// <summary>Usado pelos testes de interface (não abre o navegador).</summary>
    public Task StartForTestAsync(bool personal, bool manual = false)
    {
        _personal = personal;
        if (!personal) return StartDeviceCodeAsync();
        return manual ? StartBrowserAsync(openBrowser: false) : StartAutoAsync(openBrowser: false);
    }

    /// <summary>URL do login automático (testes).</summary>
    public Uri? AutoAuthorizationUri => _autoUri;

    async Task StartAutoAsync(bool openBrowser)
    {
        ChoosePanel.IsVisible = false;
        CodePanel.IsVisible = false;
        BrowserPanel.IsVisible = false;
        ProgressPanel.IsVisible = true;
        Busy.IsVisible = true;
        StatusText.Text = "Abrindo a página de login no navegador…";
        await RunAsync(() => _host.AddAccountWithSystemBrowserAsync(_personal, uri =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                _autoUri = uri;
                AutoPanel.IsVisible = true;
                StatusText.Text = "Aguardando você entrar no navegador…";
                if (openBrowser) DesktopIntegration.OpenPath(uri.AbsoluteUri);
            });
            return Task.CompletedTask;
        }, _cts.Token));
    }

    /// <summary>URL de autorização gerada pelo MSAL (testes).</summary>
    public Uri? AuthorizationUri => _authUri;

    void SubmitPasted(bool showErrors)
    {
        var err = AuthService.ValidatePastedRedirect(PasteBox.Text, out var uri);
        if (showErrors || uri != null || PasteBox.Text?.Contains("code=") == true)
        {
            PasteError.IsVisible = err != null;
            PasteError.Text = err;
        }
        if (uri != null && _pasted?.TrySetResult(uri) == true)
        {
            StatusText.Text = "Concluindo o login…";
            FinishBrowser.IsEnabled = false;
        }
    }

    async Task StartBrowserAsync(bool openBrowser)
    {
        ChoosePanel.IsVisible = false;
        CodePanel.IsVisible = false;
        ProgressPanel.IsVisible = true;
        Busy.IsVisible = true;
        StatusText.Text = "Preparando o login…";
        await RunAsync(() => _host.AddAccountWithBrowserAsync(_personal, (authUri, ct) =>
        {
            var tcs = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetCanceled());
            Dispatcher.UIThread.Post(() =>
            {
                _authUri = authUri;
                _pasted = tcs;
                BrowserPanel.IsVisible = true;
                FinishBrowser.IsEnabled = true;
                StatusText.Text = "Aguardando você entrar no navegador e colar o endereço final…";
                if (openBrowser) DesktopIntegration.OpenPath(authUri.AbsoluteUri);
            });
            return tcs.Task;
        }, _cts.Token));
    }

    async Task StartDeviceCodeAsync()
    {
        ChoosePanel.IsVisible = false;
        ProgressPanel.IsVisible = true;
        await RunAsync(() => _host.AddAccountAsync(_personal, (url, code, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                _url = url;
                _code = code;
                UrlText.Text = $"1. Clique no botão abaixo (ou acesse {url})\n2. Digite o código:";
                CodeText.Text = code;
                CodePanel.IsVisible = true;
                StatusText.Text = "Aguardando você concluir o login no navegador…";
            });
            return Task.CompletedTask;
        }, _cts.Token));
    }

    async Task RunAsync(Func<Task<AccountConfig>> signIn)
    {
        try
        {
            var acc = await signIn();
            Success = true;
            Busy.IsVisible = false;
            CodePanel.IsVisible = false;
            BrowserPanel.IsVisible = false;
            AutoPanel.IsVisible = false;
            StatusText.Text = $"Conectado como {acc.Username}. Seus arquivos aparecerão em {_host.Config.MountPoint}.";
            CloseButton.Content = "Concluir";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Busy.IsVisible = false;
            FinishBrowser.IsEnabled = true;
            StatusText.Text = "Não foi possível entrar: " + ex.Message;
            CloseButton.Content = "Fechar";
        }
    }
}
