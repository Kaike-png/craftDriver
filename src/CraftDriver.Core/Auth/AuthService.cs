using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensibility;
using Microsoft.Identity.Client.Extensions.Msal;
using CraftDriver.Core.Config;

namespace CraftDriver.Core.Auth;

/// <summary>
/// Login Microsoft via MSAL (contas pessoais e corporativas/escolares).
/// Usa o fluxo de código de dispositivo, que funciona com o redirect "nativeclient"
/// do app registrado e não precisa de navegador embutido.
/// O cache de tokens fica no chaveiro do sistema (Secret Service / KWallet),
/// com fallback para arquivo chmod 600.
/// </summary>
public sealed class AuthService
{
    /// <summary>
    /// Contas pessoais não têm SharePoint: pedir Sites.ReadWrite.All a uma conta pessoal faz a página
    /// microsoft.com/link responder "O código inserido expirou" (mensagem enganosa da Microsoft).
    /// </summary>
    public static readonly string[] PersonalScopes = ["Files.ReadWrite.All"];
    public static readonly string[] WorkScopes = ["Files.ReadWrite.All", "Sites.ReadWrite.All"];

    static string[] ScopesFor(bool personal) => personal ? PersonalScopes : WorkScopes;
    static bool IsPersonalTenant(string? tenantId) => string.Equals(tenantId, ConsumerTenant, StringComparison.OrdinalIgnoreCase);
    const string ConsumerTenant = "9188040d-6c67-4c5b-b112-36a304b66dad";

    /// <summary>Redirect de loopback: o MSAL escuta numa porta livre e recebe o código sozinho.</summary>
    public const string LoopbackRedirect = "http://localhost";

    /// <summary>App principal (redirect http://localhost): login pelo navegador do sistema, código de dispositivo e renovação.</summary>
    readonly IPublicClientApplication _app;
    /// <summary>Mesmo Client ID com redirect "nativeclient": método manual de colar o endereço.</summary>
    readonly IPublicClientApplication _pasteApp;
    public string CacheStorage { get; private set; } = "?";

    AuthService(IPublicClientApplication app, IPublicClientApplication pasteApp)
    {
        _app = app;
        _pasteApp = pasteApp;
    }

    public static async Task<AuthService> CreateAsync(AppConfig cfg)
    {
        IPublicClientApplication Build(string redirect) => PublicClientApplicationBuilder.Create(cfg.ClientId)
            .WithAuthority(cfg.Authority)
            .WithRedirectUri(redirect)
            .WithClientName("craftdriver")
            .Build();
        var svc = new AuthService(Build(LoopbackRedirect), Build(cfg.RedirectUri));
        await svc.RegisterCacheAsync(cfg.Accounts.Count > 0).ConfigureAwait(false);
        return svc;
    }

    /// <summary>true quando os tokens deveriam estar no chaveiro, mas ele não respondeu na inicialização.</summary>
    public bool KeyringUnavailable { get; private set; }

    /// <summary>Mensagens de diagnóstico da inicialização do cache de tokens.</summary>
    public List<string> StartupNotes { get; } = [];

    /// <summary>Marca onde os tokens foram guardados da última vez ("keyring" ou "file").</summary>
    static string StorageMarker => Path.Combine(AppPaths.ConfigDir, "token-storage");

    async Task RegisterCacheAsync(bool hasAccounts)
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
        const string fileName = "msal_token_cache.bin";
        var filePath = Path.Combine(AppPaths.ConfigDir, fileName);
        var marker = File.Exists(StorageMarker) ? File.ReadAllText(StorageMarker).Trim() : null;
        // Já houve login usando o chaveiro (ou há contas e o arquivo está vazio): não vale cair
        // para o arquivo na primeira falha, pois o chaveiro pode estar só demorando a subir
        // (início da sessão) — cair no arquivo vazio faria todas as contas pedirem login de novo.
        var expectKeyring = marker == "keyring"
            || (marker == null && hasAccounts && (!File.Exists(filePath) || new FileInfo(filePath).Length == 0));
        var deadline = DateTime.UtcNow + (expectKeyring ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(10));
        Exception? lastError = null;
        do
        {
            if (!await SecretServiceLauncher.EnsureRunningAsync(TimeSpan.FromSeconds(10), StartupNotes.Add).ConfigureAwait(false) && !expectKeyring)
                break; // sem provedor de chaveiro na máquina e nada a perder: usa o arquivo
            try
            {
                await RegisterKeyringAsync(fileName).ConfigureAwait(false);
                CacheStorage = "chaveiro do sistema";
                if (marker != "keyring") File.WriteAllText(StorageMarker, "keyring");
                return;
            }
            catch (Exception ex) when (ex is MsalCachePersistenceException or DllNotFoundException or InvalidOperationException)
            {
                lastError = ex;
                if (!expectKeyring) break;
                await Task.Delay(2000).ConfigureAwait(false);
            }
        } while (DateTime.UtcNow < deadline);

        if (expectKeyring)
        {
            KeyringUnavailable = true;
            StartupNotes.Add("Chaveiro do sistema indisponível: os tokens salvos nele não puderam ser lidos"
                + (lastError != null ? $" ({lastError.GetBaseException().Message})" : ""));
        }

        var fileProps = new StorageCreationPropertiesBuilder(fileName, AppPaths.ConfigDir)
            .WithLinuxUnprotectedFile()
            .Build();
        var fileHelper = await MsalCacheHelper.CreateAsync(fileProps).ConfigureAwait(false);
        fileHelper.RegisterCache(_app.UserTokenCache);
        fileHelper.RegisterCache(_pasteApp.UserTokenCache);
        if (!File.Exists(filePath)) File.WriteAllBytes(filePath, []);
        File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        CacheStorage = "arquivo (" + filePath + ")" + (KeyringUnavailable ? " — chaveiro indisponível" : "");
        // só passa a "file" quando não há tokens no chaveiro a preservar
        if (!expectKeyring && marker != "file") File.WriteAllText(StorageMarker, "file");
    }

    async Task RegisterKeyringAsync(string fileName)
    {
        var props = new StorageCreationPropertiesBuilder(fileName, AppPaths.ConfigDir)
            // esquema/atributos mantidos do nome antigo ("onedrive-linux") para não invalidar os
            // tokens já salvos no chaveiro; só o rótulo visível mudou
            .WithLinuxKeyring("onedrive-linux.tokencache", MsalCacheHelper.LinuxKeyRingDefaultCollection,
                "craftDriver - tokens", new KeyValuePair<string, string>("Version", "1"),
                new KeyValuePair<string, string>("Product", "onedrive-linux"))
            .Build();
        var helper = await MsalCacheHelper.CreateAsync(props).ConfigureAwait(false);
        helper.VerifyPersistence();
        helper.RegisterCache(_app.UserTokenCache);
        helper.RegisterCache(_pasteApp.UserTokenCache);
    }

    /// <summary>
    /// Inicia o login. <paramref name="onCode"/> recebe a URL e o código a exibir ao usuário.
    /// O tipo de conta precisa ser escolhido antes: com a autoridade "common" a Microsoft devolve a
    /// página de código corporativa (login.microsoft.com/device), na qual contas pessoais fazem login
    /// mas são redirecionadas para account.microsoft.com sem concluir a autorização.
    /// Contas pessoais usam "consumers" (www.microsoft.com/link); trabalho/escola usam "organizations".
    /// </summary>
    public async Task<AccountConfig> SignInWithDeviceCodeAsync(bool personal, Func<string, string, string, Task> onCode, CancellationToken ct)
    {
        var result = await _app.AcquireTokenWithDeviceCode(ScopesFor(personal), dc => onCode(dc.VerificationUrl, dc.UserCode, dc.Message))
            .WithTenantId(personal ? "consumers" : "organizations")
            .ExecuteAsync(ct);
        return ToConfig(result.Account, result.TenantId);
    }

    /// <summary>
    /// Login pelo navegador com o redirect "nativeclient" (método manual, alternativa ao loopback):
    /// <paramref name="getRedirect"/> recebe a URL de autorização, abre o navegador e devolve o endereço
    /// final que o usuário colou (https://login.microsoftonline.com/common/oauth2/nativeclient?code=...).
    /// O MSAL valida o state, faz a troca do código com PKCE e grava o token no cache.
    /// É o método confiável para contas pessoais com o Client ID público.
    /// </summary>
    public async Task<AccountConfig> SignInWithBrowserAsync(bool personal, Func<Uri, CancellationToken, Task<Uri>> getRedirect, CancellationToken ct)
    {
        var result = await _pasteApp.AcquireTokenInteractive(ScopesFor(personal))
            .WithTenantId(personal ? "consumers" : "organizations")
            .WithPrompt(Prompt.SelectAccount)
            .WithCustomWebUi(new PasteRedirectWebUi(getRedirect))
            .ExecuteAsync(ct);
        return ToConfig(result.Account, result.TenantId);
    }

    /// <summary>
    /// Login automático pelo navegador do sistema: o MSAL abre a página de login e escuta em
    /// http://localhost:&lt;porta livre&gt;. Ao concluir, a Microsoft redireciona para lá e o app recebe o
    /// código sozinho — sem passar pela página "nativeclient" (que a Microsoft manda para /wrongplace).
    /// Exige http://localhost cadastrado como redirect (plataforma "Aplicativos móveis e da área de trabalho").
    /// </summary>
    public async Task<AccountConfig> SignInWithSystemBrowserAsync(bool personal, Func<Uri, Task> openBrowser, CancellationToken ct)
    {
        var result = await _app.AcquireTokenInteractive(ScopesFor(personal))
            .WithTenantId(personal ? "consumers" : "organizations")
            .WithPrompt(Prompt.SelectAccount)
            .WithUseEmbeddedWebView(false)
            .WithSystemWebViewOptions(new SystemWebViewOptions
            {
                OpenBrowserAsync = openBrowser,
                HtmlMessageSuccess = Page("Pronto! Login recebido.", "Pode fechar esta aba: o craftDriver conclui a conexão sozinho."),
                HtmlMessageError = Page("Não foi possível entrar.", "Erro: {0} — {1}. Volte ao app e tente de novo."),
            })
            .ExecuteAsync(ct);
        return ToConfig(result.Account, result.TenantId);
    }

    static string Page(string title, string body) =>
        $"<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><title>craftDriver</title></head>" +
        $"<body style=\"font-family:sans-serif;margin:15vh auto;max-width:520px;text-align:center\">" +
        $"<h1 style=\"color:#0F6CBD\">&#9729; {title}</h1><p>{body}</p></body></html>";

    sealed class PasteRedirectWebUi(Func<Uri, CancellationToken, Task<Uri>> getRedirect) : ICustomWebUi
    {
        public Task<Uri> AcquireAuthorizationCodeAsync(Uri authorizationUri, Uri redirectUri, CancellationToken cancellationToken) =>
            getRedirect(authorizationUri, cancellationToken);
    }

    /// <summary>Confere o endereço colado; retorna mensagem de erro ou null se ok.</summary>
    public static string? ValidatePastedRedirect(string? text, out Uri? uri)
    {
        uri = null;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return "Cole o endereço completo da página em branco.";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u) || !u.AbsolutePath.EndsWith("/oauth2/nativeclient", StringComparison.OrdinalIgnoreCase))
            return "Esse não é o endereço certo. Ele começa com https://login.microsoftonline.com/common/oauth2/nativeclient?code=…";
        var q = System.Web.HttpUtility.ParseQueryString(u.Query);
        if (q["error"] is { } err) return $"A Microsoft recusou o login: {q["error_description"] ?? err}";
        if (string.IsNullOrEmpty(q["code"])) return "O endereço não tem o parâmetro code=. Copie o endereço inteiro da barra do navegador.";
        uri = u;
        return null;
    }

    static AccountConfig ToConfig(IAccount acc, string? tenant)
    {
        tenant ??= acc.HomeAccountId.TenantId;
        return new AccountConfig
        {
            Id = acc.HomeAccountId.Identifier,
            Username = acc.Username,
            TenantId = tenant ?? "",
            Type = IsPersonalTenant(tenant) ? "personal" : "business",
        };
    }

    public async Task<string> GetTokenAsync(string accountId, bool forceRefresh, CancellationToken ct)
    {
        var acc = await _app.GetAccountAsync(accountId);
        // Logo após o login na sessão, a carteira (KWallet) pode estar terminando de abrir e a primeira
        // leitura do cache volta vazia; o helper do MSAL relê o chaveiro a cada acesso, então
        // tentamos de novo por alguns segundos antes de declarar que a conta precisa de login.
        for (var i = 0; acc == null && i < 5; i++)
        {
            await Task.Delay(1000, ct);
            acc = await _app.GetAccountAsync(accountId);
        }
        if (acc == null)
            throw new AuthRequiredException(accountId, KeyringUnavailable
                      ? "O chaveiro do sistema (KWallet/GNOME Keyring) não respondeu, então os tokens salvos não puderam ser lidos. Abra o chaveiro e reinicie o craftDriver."
                      : "Conta não encontrada no cache de tokens. Entre novamente.");
        try
        {
            var builder = _app.AcquireTokenSilent(ScopesFor(IsPersonalTenant(acc.HomeAccountId.TenantId)), acc).WithForceRefresh(forceRefresh);
            if (!string.IsNullOrEmpty(acc.HomeAccountId.TenantId)) builder = builder.WithTenantId(acc.HomeAccountId.TenantId);
            var r = await builder.ExecuteAsync(ct);
            return r.AccessToken;
        }
        catch (MsalUiRequiredException ex)
        {
            throw new AuthRequiredException(accountId, "Sessão expirada; entre novamente. " + ex.Message);
        }
    }

    public async Task SignOutAsync(string accountId)
    {
        var acc = await _app.GetAccountAsync(accountId);
        if (acc != null) await _app.RemoveAsync(acc);
    }

    public async Task<bool> HasAccountAsync(string accountId) => await _app.GetAccountAsync(accountId) != null;
}

public sealed class AuthRequiredException(string accountId, string message) : Exception(message)
{
    public string AccountId { get; } = accountId;
}
