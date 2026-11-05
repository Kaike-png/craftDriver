using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using CraftDriver.Core.Auth;
using Microsoft.Identity.Client;

namespace CraftDriver.UiTests;

/// <summary>
/// Microsoft falsa em memória para o MSAL dos testes de interface: responde descoberta de instância,
/// código de dispositivo e troca de código por token (sempre recusada, como a Microsoft faz com um código
/// inventado). Qualquer outro endereço falha na hora — nenhum teste de UI chega à rede real.
/// Ligada para o assembly inteiro antes do primeiro teste (inicializador de módulo).
/// </summary>
public sealed class FakeMicrosoftLogin : HttpMessageHandler, IMsalHttpClientFactory
{
    public static readonly FakeMicrosoftLogin Instance = new();

    /// <summary>Endereços que o MSAL pediu (em ordem), com o corpo de cada POST.</summary>
    public ConcurrentQueue<(string Method, Uri Uri, string Body)> Requests { get; } = new();

    /// <summary>Pedidos para fora da Microsoft falsa (deveria ficar vazio).</summary>
    public ConcurrentQueue<Uri> Blocked { get; } = new();

    public const string UserCode = "FALSO-123";
    public const string VerificationUri = "https://login.microsoft.com/device";

    static readonly HashSet<string> Hosts = ["login.microsoftonline.com", "login.microsoft.com", "login.windows.net"];

    [ModuleInitializer]
    internal static void Install() => AuthService.HttpClientFactory = Instance;

    // um HttpClient por pedido do MSAL; o handler é compartilhado e nunca é descartado por ele
    public HttpClient GetHttpClient() => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        if (!Hosts.Contains(uri.Host))
        {
            Blocked.Enqueue(uri);
            throw new HttpRequestException($"teste sem rede: pedido bloqueado para {uri}");
        }
        Requests.Enqueue((request.Method.Method, uri, body));
        var path = uri.AbsolutePath;

        if (path.EndsWith("/discovery/instance", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, $$"""
                {"tenant_discovery_endpoint":"https://{{uri.Host}}/common/v2.0/.well-known/openid-configuration","api-version":"1.1",
                 "metadata":[{"preferred_network":"login.microsoftonline.com","preferred_cache":"login.windows.net",
                   "aliases":["login.microsoftonline.com","login.windows.net","login.microsoft.com","sts.windows.net"]}]}
                """);

        if (path.EndsWith("/oauth2/v2.0/devicecode", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, $$"""
                {"user_code":"{{UserCode}}","device_code":"codigo-de-dispositivo-falso","verification_uri":"{{VerificationUri}}",
                 "expires_in":120,"interval":5,
                 "message":"To sign in, use a web browser to open the page {{VerificationUri}} and enter the code {{UserCode}} to authenticate."}
                """);

        if (path.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal))
            return body.Contains("grant_type=device_code", StringComparison.Ordinal)
                ? Json(HttpStatusCode.BadRequest, """
                    {"error":"authorization_pending","error_description":"AADSTS70016: OAuth 2.0 device flow error. Authorization is pending.","error_codes":[70016]}
                    """)
                : Json(HttpStatusCode.BadRequest, """
                    {"error":"invalid_grant","error_description":"AADSTS70000: The provided authorization code is invalid (Microsoft falsa dos testes).","error_codes":[70000]}
                    """);

        return Json(HttpStatusCode.NotFound, $$"""{"error":"not_found","error_description":"Microsoft falsa: {{path}} não simulado"}""");
    }

    /// <summary>Pedidos recebidos, um por linha (vai para a pasta dos PNGs: artefato do CI).</summary>
    public string Dump() => string.Join("\n", Requests.Select(r => $"{r.Method} {r.Uri} {r.Body}"));

    static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class FakeMicrosoftLoginTests
{
    [Fact]
    public async Task Msal_dos_testes_usa_a_microsoft_falsa_e_bloqueia_outros_hosts()
    {
        Assert.Same(FakeMicrosoftLogin.Instance, AuthService.HttpClientFactory); // ligada antes de qualquer teste
        // instância própria: o registro de bloqueios da compartilhada fica vazio para os testes de login
        var fake = new FakeMicrosoftLogin();
        using var http = fake.GetHttpClient();
        var ct = TestContext.Current.CancellationToken;
        Assert.True((await http.GetAsync("https://login.microsoftonline.com/common/discovery/instance?api-version=1.1", ct)).IsSuccessStatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("https://graph.microsoft.com/v1.0/me", ct));
        Assert.Equal("graph.microsoft.com", Assert.Single(fake.Blocked).Host);
    }
}
