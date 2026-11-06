using System.Net;
using System.Security.Authentication;
using CraftDriver.App.Services;

namespace CraftDriver.UiTests;

/// <summary>
/// "craftdriver diagnose --tls": o smoke dos pacotes depende dele para pegar libssl/ca-certificates ausentes
/// (o .deb da 0.2.0 abortava no Debian 12 e dava UntrustedRoot no Ubuntu 24.04 mínimos). Sem rede: o HTTPS
/// usa um HttpClient com handler falso.
/// </summary>
public class TlsDiagnosticsTests
{
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return respond(request);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    static (int rc, string output) Run(string[] args, Handler? handler = null)
    {
        var w = new StringWriter();
        var rc = TlsDiagnostics.Run(args, w, handler == null ? null : () => new HttpClient(handler));
        return (rc, w.ToString());
    }

    [Fact]
    public void Sem_tls_mostra_o_uso()
    {
        var (rc, output) = Run(["diagnose"]);
        Assert.Equal(2, rc);
        Assert.Contains("diagnose --tls", output);
    }

    [Fact]
    public void Offline_confere_openssl_e_certificados_raiz()
    {
        var (rc, output) = Run(["diagnose", "--tls", "--offline"]);
        Assert.True(rc == 0, output);
        Assert.Contains("OpenSSL: 3.", output);
        Assert.Matches(@"Certificados raiz: [1-9]\d*", output);
        Assert.DoesNotContain("HTTPS", output);
    }

    [Fact]
    public void Https_ok_usa_o_host_pedido()
    {
        var h = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var (rc, output) = Run(["diagnose", "--tls", "https://exemplo.test/x"], h);
        Assert.True(rc == 0, output);
        Assert.Equal("https://exemplo.test/x", Assert.Single(h.Requests).RequestUri!.AbsoluteUri);
        Assert.Contains("HTTPS https://exemplo.test/x: 200", output);
        Assert.EndsWith("TLS OK" + Environment.NewLine, output);
    }

    [Fact]
    public void Https_padrao_e_um_host_publico_que_nao_e_da_microsoft()
    {
        var h = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        Run(["diagnose", "--tls"], h);
        Assert.Equal(TlsDiagnostics.DefaultUrl + "/", Assert.Single(h.Requests).RequestUri!.AbsoluteUri);
        Assert.DoesNotContain("microsoft", TlsDiagnostics.DefaultUrl);
    }

    [Theory]
    [InlineData("http://exemplo.test")]
    [InlineData("nao-e-url")]
    public void Recusa_url_sem_https(string url)
    {
        var h = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var (rc, output) = Run(["diagnose", "--tls", url], h);
        Assert.Equal(2, rc);
        Assert.Contains("URL HTTPS", output);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public void Certificado_nao_confiavel_falha_com_o_motivo()
    {
        // o que o .NET lança sem ca-certificates: HttpRequestException ← AuthenticationException (UntrustedRoot)
        var h = new Handler(_ => throw new HttpRequestException("The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot")));
        var (rc, output) = Run(["diagnose", "--tls"], h);
        Assert.Equal(1, rc);
        Assert.Contains("FALHA: HTTPS", output);
        Assert.Contains("UntrustedRoot", output);
        Assert.Contains("TLS com problemas", output);
    }
}
