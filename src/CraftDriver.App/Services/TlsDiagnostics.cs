using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CraftDriver.App.Services;

/// <summary>
/// "craftdriver diagnose --tls [URL] [--offline]": confere o que o login e a Graph precisam do sistema —
/// OpenSSL carregável pelo .NET, certificados raiz instalados (ca-certificates) e, sem --offline, um HTTPS de
/// verdade a um host público (padrão https://github.com). Sem libssl o .NET aborta o processo (core dump) e sem
/// ca-certificates todo HTTPS falha com UntrustedRoot — foi o que aconteceu com o .deb da 0.2.0 no Debian/Ubuntu
/// mínimos. O smoke dos pacotes (packaging/test/smoke.sh) roda este comando; não depende da Microsoft.
/// </summary>
internal static class TlsDiagnostics
{
    public const string DefaultUrl = "https://github.com";

    public static int Run(string[] args, TextWriter? output = null, Func<HttpClient>? httpFactory = null)
    {
        var o = output ?? Console.Out;
        if (!args.Contains("--tls"))
        {
            o.WriteLine("Uso: craftdriver diagnose --tls [URL] [--offline]");
            return 2;
        }
        var ok = true;
        try
        {
            var v = SafeEvpPKeyHandle.OpenSslVersion;
            // 0xMNNFFPPS (OpenSSL 3: maior, menor, correção)
            o.WriteLine($"OpenSSL: {(v >> 28) & 0xF}.{(v >> 20) & 0xFF}.{(v >> 4) & 0xFF}");
        }
        catch (Exception ex)
        {
            o.WriteLine("FALHA: OpenSSL (libssl) não carregou: " + ex.Message + " — instale libssl3 / openssl-libs / openssl.");
            return 1;
        }

        int roots;
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            roots = store.Certificates.Count;
        }
        catch (Exception ex)
        {
            roots = 0;
            o.WriteLine("Certificados raiz: erro ao ler (" + ex.Message + ")");
        }
        o.WriteLine($"Certificados raiz: {roots}");
        if (roots == 0)
        {
            o.WriteLine("FALHA: nenhum certificado raiz — instale o pacote ca-certificates.");
            ok = false;
        }

        if (!args.Contains("--offline"))
        {
            var url = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? DefaultUrl;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                o.WriteLine("FALHA: informe uma URL HTTPS absoluta (ex.: https://github.com).");
                return 2;
            }
            try
            {
                using var http = httpFactory?.Invoke() ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                using var resp = http.Send(new HttpRequestMessage(HttpMethod.Head, url), HttpCompletionOption.ResponseHeadersRead);
                o.WriteLine($"HTTPS {url}: {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                var inner = ex;
                while (inner.InnerException != null) inner = inner.InnerException;
                o.WriteLine($"FALHA: HTTPS {url}: {ex.Message}" + (inner != ex ? " (" + inner.Message + ")" : ""));
                ok = false;
            }
        }
        o.WriteLine(ok ? "TLS OK" : "TLS com problemas");
        return ok ? 0 : 1;
    }
}
