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
    /// <summary>Último fluxo iniciado: "Tentar de novo" o repete (mesmo tipo de conta e método).</summary>
    Func<Task>? _lastStart;
    public bool Success { get; private set; }

    /// <summary>Testes: substitui a chamada ao MSAL (ex.: lançar MsalServiceException simulada).</summary>
    internal Func<CancellationToken, Task<AccountConfig>>? SignInForTest { get; set; }

    /// <summary>Último erro mostrado (testes).</summary>
    public LoginError? LastError { get; private set; }

    readonly AccountConfig? _reauth;

    public LoginWindow() : this(App.Host) { }

    /// <param name="reauth">Conta cuja sessão expirou: pula a escolha do tipo e já abre o login certo.</param>
    public LoginWindow(AppHost host, AccountConfig? reauth = null)
    {
        _host = host;
        _reauth = reauth;
        InitializeComponent();
        if (reauth != null)
        {
            Title = "Entrar novamente";
            Opened += async (_, _) =>
            {
                _personal = reauth.IsPersonal;
                if (_personal) await StartTracked(() => StartAutoAsync(openBrowser: true));
                else await StartTracked(StartDeviceCodeAsync);
            };
        }
        Icon = App.LoadIcon("cloud");
        CloseButton.Click += (_, _) => Close();
        Closed += (_, _) => _cts.Cancel();
        PersonalButton.Click += async (_, _) => { _personal = true; await StartTracked(() => StartAutoAsync(openBrowser: true)); };
        RetryButton.Click += async (_, _) => await RetryAsync();
        ReopenAuto.Click += (_, _) => { if (_autoUri != null) DesktopIntegration.OpenPath(_autoUri.AbsoluteUri); };
        SwitchToManual.Click += async (_, _) =>
        {
            _cts.Cancel();
            _cts = new CancellationTokenSource();
            AutoPanel.IsVisible = false;
            await StartTracked(() => StartBrowserAsync(openBrowser: true));
        };
        WorkButton.Click += async (_, _) => { _personal = false; await StartTracked(StartDeviceCodeAsync); };
        SwitchToBrowser.Click += async (_, _) =>
        {
            _cts.Cancel();
            _cts = new CancellationTokenSource();
            await StartTracked(() => StartBrowserAsync(openBrowser: true));
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
        if (!personal) return StartTracked(StartDeviceCodeAsync);
        return manual ? StartTracked(() => StartBrowserAsync(openBrowser: false)) : StartTracked(() => StartAutoAsync(openBrowser: false));
    }

    Task StartTracked(Func<Task> start)
    {
        _lastStart = start;
        return start();
    }

    /// <summary>"Tentar de novo": novo token de cancelamento, limpa o erro e repete o último fluxo.</summary>
    public async Task RetryAsync()
    {
        if (_lastStart == null) return;
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        ErrorPanel.IsVisible = false;
        RetryButton.IsVisible = false;
        ErrorDetails.IsExpanded = false;
        LastError = null;
        CloseButton.Content = "Cancelar";
        PasteBox.Text = "";
        PasteError.IsVisible = false;
        await _lastStart();
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
            var acc = SignInForTest != null ? await SignInForTest(_cts.Token) : await signIn();
            Success = true;
            Busy.IsVisible = false;
            CodePanel.IsVisible = false;
            BrowserPanel.IsVisible = false;
            AutoPanel.IsVisible = false;
            StatusText.Text = _reauth == null
                ? $"Conectado como {acc.Username}. Seus arquivos aparecerão em {_host.Config.MountPoint}."
                : acc.Id == _reauth.Id
                    ? $"Sessão de {acc.Username} renovada. A sincronização continua de onde parou."
                    : $"Você entrou como {acc.Username}, não como {_reauth.Username}: ela foi adicionada como outra conta. " +
                      $"Para renovar {_reauth.Username}, use \"Entrar novamente\" e escolha esse usuário.";
            CloseButton.Content = "Concluir";
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ShowError(LoginErrors.Describe(ex));
        }
    }

    void ShowError(LoginError err)
    {
        LastError = err;
        Busy.IsVisible = false;
        FinishBrowser.IsEnabled = true;
        // os painéis do fluxo que falhou saem: o código/URL antigos não valem mais
        CodePanel.IsVisible = false;
        AutoPanel.IsVisible = false;
        BrowserPanel.IsVisible = false;
        StatusText.Text = "Não foi possível entrar.";
        ErrorText.Text = err.Message;
        ErrorDetailText.Text = err.Detail;
        ErrorDetails.IsExpanded = false;
        ErrorPanel.IsVisible = true;
        RetryButton.IsVisible = err.CanRetry && _lastStart != null;
        CloseButton.Content = "Fechar";
    }
}
