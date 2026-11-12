using System.Net;
using System.Security.Cryptography;
using System.Text;
using CraftDriver.Core.Updates;

namespace CraftDriver.Tests;

/// <summary>Resposta HTTP falsa por URL (nenhum teste de atualização acessa a rede).</summary>
sealed class FakeHttp : HttpMessageHandler
{
    readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);
    public List<string> Requested { get; } = [];
    public List<HttpRequestMessage> Requests { get; } = [];

    public FakeHttp Route(string url, byte[] body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[url] = () => new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        return this;
    }

    public FakeHttp Route(string url, string body, HttpStatusCode status = HttpStatusCode.OK) => Route(url, Encoding.UTF8.GetBytes(body), status);

    public FakeHttp Fail(string url, Exception ex)
    {
        _routes[url] = () => throw ex;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        lock (Requested)
        {
            Requested.Add(url);
            Requests.Add(request);
        }
        if (!_routes.TryGetValue(url, out var r)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") });
        return Task.FromResult(r());
    }
}

/// <summary>Executor de comandos falso: responde por programa e registra as chamadas.</summary>
sealed class FakeRunner : ICommandRunner
{
    public readonly Dictionary<string, Func<IReadOnlyList<string>, CommandResult>> Programs = new();
    public readonly List<string> Calls = [];

    public FakeRunner On(string program, Func<IReadOnlyList<string>, CommandResult> result)
    {
        Programs[program] = result;
        return this;
    }

    public FakeRunner On(string program, int rc, string stdout = "", string stderr = "") => On(program, _ => new CommandResult(rc, stdout, stderr));

    public Task<CommandResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add(string.Join(' ', new[] { file }.Concat(args)));
        return Task.FromResult(Programs.TryGetValue(file, out var f) ? f(args) : new CommandResult(CommandResult.NotFound, "", file + ": não encontrado"));
    }

    public bool Exists(string program) => Programs.ContainsKey(program);
}

public class UpdateCoreTests
{
    static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "github-release-latest.json"));

    static ReleaseInfo Release(string version = "0.2.0", params (string Name, string Url)[] assets) =>
        new(version, "v" + version, "CraftDriver v" + version, "notas", "https://github.com/Kaike-png/craftDriver/releases/tag/v" + version,
            assets.Select(a => new ReleaseAsset(a.Name, a.Url, 0)).ToList());

    // ---------- JSON da release ----------

    [Fact]
    public void Le_a_release_real_do_GitHub()
    {
        var r = UpdateCheck.Parse(Fixture);
        Assert.NotNull(r);
        Assert.Equal("0.2.0", r.Version);
        Assert.Equal("v0.2.0", r.Tag);
        Assert.Equal("CraftDriver v0.2.0", r.Name);
        Assert.Equal("https://github.com/Kaike-png/craftDriver/releases/tag/v0.2.0", r.HtmlUrl);
        Assert.StartsWith("### Novidades", r.Notes);
        Assert.Equal(12, r.Assets.Count);
        var deb = r.Asset("craftdriver_0.2.0-1_amd64.deb");
        Assert.NotNull(deb);
        Assert.Equal("https://github.com/Kaike-png/craftDriver/releases/download/v0.2.0/craftdriver_0.2.0-1_amd64.deb", deb.Url);
        Assert.True(deb.Size > 1_000_000);
        Assert.NotNull(r.Asset(UpdateAssets.SumsName));
    }

    [Theory]
    [InlineData("\"draft\": true", "v0.3.0")]
    [InlineData("\"prerelease\": true", "v0.3.0")]
    [InlineData("\"draft\": false", "v0.3.0-rc1")]
    [InlineData("\"draft\": false", "nightly")]
    public void Rascunho_pre_lancamento_e_tag_que_nao_e_versao_nao_sao_oferecidos(string flag, string tag)
    {
        var json = $$"""{ "tag_name": "{{tag}}", {{flag}}, "html_url": "https://x/y", "assets": [] }""";
        Assert.Null(UpdateCheck.Parse(json));
        // controle: a mesma resposta estável é aceita
        Assert.NotNull(UpdateCheck.Parse("""{ "tag_name": "v0.3.0", "draft": false, "prerelease": false, "html_url": "https://x/y", "assets": [] }"""));
    }

    [Fact]
    public void Json_invalido_vira_erro_de_atualizacao()
    {
        var ex = Assert.Throws<UpdateException>(() => UpdateCheck.Parse("<html>rate limit</html>"));
        Assert.Contains("resposta", ex.Message);
    }

    [Fact]
    public void Resumo_das_notas_em_texto_simples_sem_a_tabela_de_instalacao()
    {
        var s = UpdateCheck.Summarize(UpdateCheck.Parse(Fixture)!.Notes, maxLines: 6);
        Assert.DoesNotContain("**", s);
        Assert.DoesNotContain("`", s);
        Assert.DoesNotContain("###", s);
        Assert.DoesNotContain("pacman -U", s); // tabela "Instalar" fica de fora
        Assert.Contains("Novidades", s);
        Assert.Contains("Arquivos grandes abrem na hora", s);
        Assert.True(s.Split('\n').Length <= 7, s); // 6 linhas + "…"
        Assert.EndsWith("…", s);
        Assert.Equal("Veja a página", UpdateCheck.Summarize("Veja [a página](https://x/y)"));
        // sem limite de linhas: a seção "Instalar" inteira fica de fora (não só a tabela)
        var all = UpdateCheck.Summarize(UpdateCheck.Parse(Fixture)!.Notes, maxLines: 500);
        Assert.DoesNotContain("Instalar", all);
        Assert.DoesNotContain("Para ARM", all);
        Assert.Contains("Pacotes", all);
    }

    [Fact]
    public async Task Busca_com_cabecalhos_do_GitHub_e_falhas_viram_erro()
    {
        var url = UpdateCheck.LatestUrl("http://falso");
        Assert.Equal("http://falso/repos/Kaike-png/craftDriver/releases/latest", url);
        var fake = new FakeHttp().Route(url, Fixture);
        using var http = new HttpClient(fake);
        var r = await UpdateCheck.FetchLatestAsync(http, "http://falso", "0.1.0", TestContext.Current.CancellationToken);
        Assert.Equal("0.2.0", r?.Version);
        var req = Assert.Single(fake.Requests);
        Assert.Equal("CraftDriver/0.1.0", req.Headers.UserAgent.ToString());
        Assert.Contains("application/vnd.github+json", req.Headers.Accept.ToString());

        var down = new FakeHttp().Route(url, "{}", HttpStatusCode.Forbidden);
        using var http2 = new HttpClient(down);
        var ex = await Assert.ThrowsAsync<UpdateException>(() => UpdateCheck.FetchLatestAsync(http2, "http://falso", "0.1.0", TestContext.Current.CancellationToken));
        Assert.Contains("403", ex.Message);
        var offline = new FakeHttp().Fail(url, new HttpRequestException("sem rede"));
        using var http3 = new HttpClient(offline);
        await Assert.ThrowsAsync<UpdateException>(() => UpdateCheck.FetchLatestAsync(http3, "http://falso", "0.1.0", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Base_da_api_trocada_so_pela_variavel()
    {
        Assert.Equal(UpdateCheck.DefaultApiBase, UpdateCheck.ApiBase(_ => null));
        Assert.Equal("http://127.0.0.1:9", UpdateCheck.ApiBase(v => v == UpdateCheck.ApiBaseVariable ? "http://127.0.0.1:9/" : null));
    }

    // ---------- versões ----------

    [Theory]
    [InlineData("v0.3.0", "0.2.0", true)]
    [InlineData("v0.2.0", "0.2.0", false)]
    [InlineData("v0.1.9", "0.2.0", false)]
    [InlineData("v0.10.0", "0.9.1", true)] // numérico, não texto
    [InlineData("v1.0.0", "0.99.99", true)]
    [InlineData("v0.2.1", "0.2.0-dev+abc", true)]
    [InlineData("v0.2.0", "0.2.0+abc123", false)]
    [InlineData("v1.0.0", "1.0.0-rc1", false)] // sufixo da versão atual é ignorado
    [InlineData("v0.3", "0.2.0", true)]
    [InlineData("lixo", "0.2.0", false)]
    [InlineData("v0.3.0", "?", false)]
    public void Compara_versoes_semver(string candidate, string current, bool newer) =>
        Assert.Equal(newer, SemVer.IsNewer(candidate, current));

    [Fact]
    public void Nucleo_da_versao()
    {
        Assert.Equal(new Version(0, 3, 0), SemVer.Core("v0.3"));
        Assert.Equal(new Version(1, 2, 3), SemVer.Core("1.2.3-beta.1+git"));
        Assert.Null(SemVer.Core("v"));
        Assert.Null(SemVer.Core("1.2.3.4"));
        Assert.True(SemVer.IsPrerelease("v1.0.0-rc1"));
        Assert.False(SemVer.IsPrerelease("v1.0.0+build"));
    }

    // ---------- escolha do pacote ----------

    [Theory]
    [InlineData(InstallKind.Pacman, UpdateArch.X64, "craftdriver-bin-0.2.0-1-x86_64.pkg.tar.zst")]
    [InlineData(InstallKind.Pacman, UpdateArch.Arm64, null)] // o CI não publica pacote Arch arm64
    [InlineData(InstallKind.Deb, UpdateArch.X64, "craftdriver_0.2.0-1_amd64.deb")]
    [InlineData(InstallKind.Deb, UpdateArch.Arm64, "craftdriver_0.2.0-1_arm64.deb")]
    [InlineData(InstallKind.Rpm, UpdateArch.X64, "craftdriver-0.2.0-1.x86_64.rpm")]
    [InlineData(InstallKind.Rpm, UpdateArch.Arm64, "craftdriver-0.2.0-1.aarch64.rpm")]
    [InlineData(InstallKind.UserInstall, UpdateArch.X64, "craftdriver-0.2.0-linux-x64.tar.gz")]
    [InlineData(InstallKind.UserInstall, UpdateArch.Arm64, "craftdriver-0.2.0-linux-arm64.tar.gz")]
    [InlineData(InstallKind.Unknown, UpdateArch.X64, null)]
    [InlineData(InstallKind.Deb, UpdateArch.Other, null)]
    public void Escolhe_o_pacote_pela_instalacao_e_arquitetura(InstallKind kind, UpdateArch arch, string? expected)
    {
        var r = UpdateCheck.Parse(Fixture)!;
        Assert.Equal(expected, UpdateAssets.Choose(r, kind, arch)?.Name);
    }

    [Fact]
    public void Release_sem_o_arquivo_esperado_cai_para_so_avisar()
    {
        var r = Release("0.3.0", ("craftdriver_0.3.0-1_amd64.deb", "http://x/deb"));
        Assert.Null(UpdateAssets.Choose(r, InstallKind.Rpm, UpdateArch.X64));
        // pkgrel diferente de 1 também serve
        var r2 = Release("0.3.0", ("craftdriver_0.3.0-2_amd64.deb", "http://x/deb"));
        Assert.Equal("craftdriver_0.3.0-2_amd64.deb", UpdateAssets.Choose(r2, InstallKind.Deb, UpdateArch.X64)?.Name);
        var hint = UpdateAssets.ManualHint(InstallKind.Pacman, r);
        Assert.Contains(r.HtmlUrl, hint);
        Assert.Contains("pacman -U", hint);
    }

    // ---------- jeito de instalação ----------

    const string SysExe = "/usr/lib/craftdriver/craftdriver";

    [Fact]
    public async Task Detecta_install_user_pelo_caminho_sem_rodar_comandos()
    {
        var run = new FakeRunner().On("pacman", 0, "craftdriver-bin\n");
        var info = await InstallDetector.DetectAsync("/home/u/.local/opt/craftdriver/lib/craftdriver", "/home/u", run, TestContext.Current.CancellationToken);
        Assert.Equal(InstallKind.UserInstall, info.Kind);
        Assert.Empty(run.Calls);
    }

    [Fact]
    public async Task Detecta_pacman_pelo_dono_do_arquivo()
    {
        var run = new FakeRunner().On("pacman", 0, "craftdriver-bin\n");
        var info = await InstallDetector.DetectAsync(SysExe, "/home/u", run, TestContext.Current.CancellationToken);
        Assert.Equal(InstallKind.Pacman, info.Kind);
        Assert.Equal("craftdriver-bin", info.Package);
        Assert.Equal("pacman -Qqo " + SysExe, Assert.Single(run.Calls));
    }

    [Fact]
    public async Task Detecta_deb_quando_o_pacman_nao_conhece_o_arquivo()
    {
        var run = new FakeRunner()
            .On("pacman", 1, "", "error: No package owns " + SysExe)
            .On("dpkg", 0, "craftdriver: " + SysExe + "\n");
        var info = await InstallDetector.DetectAsync(SysExe, "/home/u", run, TestContext.Current.CancellationToken);
        Assert.Equal(InstallKind.Deb, info.Kind);
        Assert.Equal("craftdriver", info.Package);
        Assert.Contains("dpkg -S " + SysExe, run.Calls);
    }

    [Fact]
    public async Task Detecta_rpm()
    {
        var run = new FakeRunner()
            .On("dpkg", 1, "", "dpkg-query: no path found matching pattern")
            .On("rpm", 0, "craftdriver");
        var info = await InstallDetector.DetectAsync(SysExe, "/home/u", run, TestContext.Current.CancellationToken);
        Assert.Equal(InstallKind.Rpm, info.Kind);
        Assert.Equal("craftdriver", info.Package);
        Assert.Contains("rpm -qf --qf %{NAME} " + SysExe, run.Calls);
    }

    [Fact]
    public async Task Sem_dono_conhecido_ou_pacote_de_terceiros_e_desconhecido()
    {
        var none = await InstallDetector.DetectAsync(SysExe, "/home/u", new FakeRunner(), TestContext.Current.CancellationToken);
        Assert.Equal(InstallKind.Unknown, none.Kind);
        // pacote do AUR compilado (craftdriver-git): instalar o craftdriver-bin por cima daria conflito
        var git = await InstallDetector.DetectAsync(SysExe, "/home/u", new FakeRunner().On("pacman", 0, "craftdriver-git\n"), TestContext.Current.CancellationToken);
        Assert.Equal(InstallKind.Unknown, git.Kind);
        Assert.Equal("craftdriver-git", git.Package);
        // dotnet run / dotnet app.dll
        var dev = new FakeRunner().On("pacman", 0, "dotnet-runtime\n");
        Assert.Equal(InstallKind.Unknown, (await InstallDetector.DetectAsync("/usr/share/dotnet/dotnet", "/home/u", dev, TestContext.Current.CancellationToken)).Kind);
        Assert.Empty(dev.Calls);
        Assert.Equal(InstallKind.Unknown, (await InstallDetector.DetectAsync(null, "/home/u", dev, TestContext.Current.CancellationToken)).Kind);
    }

    // ---------- download + SHA256 ----------

    static (ReleaseInfo Release, FakeHttp Http, byte[] Data) Served(string? sumsLine, bool withSums = true)
    {
        var data = new byte[300_000];
        new Random(7).NextBytes(data);
        var hash = Convert.ToHexStringLower(SHA256.HashData(data));
        var assets = new List<(string, string)> { ("craftdriver_0.3.0-1_amd64.deb", "http://f/deb") };
        if (withSums) assets.Add((UpdateAssets.SumsName, "http://f/sums"));
        var http = new FakeHttp().Route("http://f/deb", data)
            .Route("http://f/sums", (sumsLine ?? "").Replace("{hash}", hash) + "\n");
        return (Release("0.3.0", [.. assets]), http, data);
    }

    static string NewDir() => Directory.CreateTempSubdirectory("cd-upd-").FullName;

    [Fact]
    public async Task Baixa_e_confere_o_sha256()
    {
        var (r, fake, data) = Served("abcd  craftdriver-0.3.0-linux-x64.tar.gz\n{hash}  craftdriver_0.3.0-1_amd64.deb");
        var dir = NewDir();
        Directory.CreateDirectory(Path.Combine(dir, "0.2.9"));
        File.WriteAllText(Path.Combine(dir, "0.2.9", "velho.deb"), "x");
        var seen = new List<DownloadProgress>();
        using var http = new HttpClient(fake);
        var path = await UpdateDownloader.DownloadAsync(http, r, r.Assets[0], dir, new SyncProgress(seen), TestContext.Current.CancellationToken);
        Assert.Equal(Path.Combine(dir, "0.3.0", "craftdriver_0.3.0-1_amd64.deb"), path);
        Assert.Equal(data, File.ReadAllBytes(path));
        Assert.Equal([path], Directory.GetFiles(Path.Combine(dir, "0.3.0")));
        Assert.False(Directory.Exists(Path.Combine(dir, "0.2.9"))); // versões antigas saem
        Assert.NotEmpty(seen);
        Assert.Equal(data.Length, seen[^1].Done);
    }

    [Fact]
    public async Task Hash_errado_aborta_e_apaga_o_arquivo()
    {
        var (r, fake, _) = Served(new string('0', 64) + "  craftdriver_0.3.0-1_amd64.deb");
        var dir = NewDir();
        using var http = new HttpClient(fake);
        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            UpdateDownloader.DownloadAsync(http, r, r.Assets[0], dir, null, TestContext.Current.CancellationToken));
        Assert.Contains("SHA256", ex.Message);
        Assert.Contains("não confere", ex.Message);
        Assert.Empty(Directory.GetFiles(dir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Arquivo_fora_do_SHA256SUMS_aborta_e_apaga()
    {
        var (r, fake, _) = Served("{hash}  outro-arquivo.deb");
        var dir = NewDir();
        using var http = new HttpClient(fake);
        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            UpdateDownloader.DownloadAsync(http, r, r.Assets[0], dir, null, TestContext.Current.CancellationToken));
        Assert.Contains("não lista", ex.Message);
        Assert.Empty(Directory.GetFiles(dir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Release_sem_SHA256SUMS_nao_baixa_nada()
    {
        var (r, fake, _) = Served(null, withSums: false);
        var dir = NewDir();
        using var http = new HttpClient(fake);
        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            UpdateDownloader.DownloadAsync(http, r, r.Assets[0], dir, null, TestContext.Current.CancellationToken));
        Assert.Contains("não tem o arquivo SHA256SUMS", ex.Message);
        Assert.DoesNotContain("http://f/deb", fake.Requested);
        Assert.Empty(Directory.GetFiles(dir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Download_que_falha_nao_deixa_arquivo()
    {
        var (r, _, _) = Served("{hash}  craftdriver_0.3.0-1_amd64.deb");
        var fake = new FakeHttp().Route("http://f/sums", new string('c', 64) + "  craftdriver_0.3.0-1_amd64.deb\n"); // o .deb dá 404
        var dir = NewDir();
        using var http = new HttpClient(fake);
        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            UpdateDownloader.DownloadAsync(http, r, r.Assets[0], dir, null, TestContext.Current.CancellationToken));
        Assert.Contains("404", ex.Message);
        Assert.Empty(Directory.GetFiles(dir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Le_o_SHA256SUMS_nos_dois_formatos()
    {
        var h = new string('a', 64);
        Assert.Equal(h, UpdateDownloader.HashFromSums($"{h.ToUpperInvariant()} *x.deb\n", "x.deb"));
        Assert.Equal(h, UpdateDownloader.HashFromSums($"{new string('b', 64)}  y.deb\r\n{h}  x.deb\r\n", "x.deb"));
        Assert.Null(UpdateDownloader.HashFromSums($"{h}  x.deb.sha256\n", "x.deb"));
        Assert.Null(UpdateDownloader.HashFromSums("curto  x.deb\n", "x.deb"));
    }

    sealed class SyncProgress(List<DownloadProgress> list) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) { lock (list) list.Add(value); }
    }

    // ---------- comando de instalação ----------

    static Func<string, bool> Has(params string[] programs) => p => programs.Contains(p);

    [Fact]
    public void Monta_o_comando_de_cada_gerenciador()
    {
        const string f = "/home/u/.cache/craftdriver/updates/0.3.0/pacote";
        Assert.Equal("pkexec pacman -U --noconfirm " + f, UpdateInstaller.PackageCommand(InstallKind.Pacman, f, Has("pacman"))?.Display);
        Assert.Equal("pkexec apt-get install -y " + f, UpdateInstaller.PackageCommand(InstallKind.Deb, f, Has("apt-get", "dpkg"))?.Display);
        Assert.Equal("pkexec dpkg -i " + f, UpdateInstaller.PackageCommand(InstallKind.Deb, f, Has("dpkg"))?.Display);
        Assert.Equal("pkexec dnf install -y " + f, UpdateInstaller.PackageCommand(InstallKind.Rpm, f, Has("dnf", "rpm"))?.Display);
        Assert.Equal("pkexec zypper --non-interactive install --allow-unsigned-rpm " + f,
            UpdateInstaller.PackageCommand(InstallKind.Rpm, f, Has("zypper", "rpm"))?.Display);
        Assert.Equal("pkexec rpm -U " + f, UpdateInstaller.PackageCommand(InstallKind.Rpm, f, Has("rpm"))?.Display);
        Assert.Equal("sudo pacman -U " + f, UpdateInstaller.PackageCommand(InstallKind.Pacman, f, Has("pacman"), "sudo")?.Display);
        Assert.Null(UpdateInstaller.PackageCommand(InstallKind.Rpm, f, Has()));
        Assert.Null(UpdateInstaller.PackageCommand(InstallKind.UserInstall, f, Has("pacman")));
        Assert.Null(UpdateInstaller.PackageCommand(InstallKind.Unknown, f, Has("pacman")));
    }

    [Fact]
    public void Caminho_vira_absoluto_e_com_aspas_quando_preciso()
    {
        var cmd = UpdateInstaller.PackageCommand(InstallKind.Deb, "pacote 1.deb", Has("apt-get"))!;
        Assert.Equal(Path.GetFullPath("pacote 1.deb"), cmd.Args[^1]);
        Assert.EndsWith("'" + Path.GetFullPath("pacote 1.deb") + "'", cmd.Display);
    }

    [Theory]
    [InlineData(126, true)]
    [InlineData(127, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void Senha_cancelada_no_pkexec(int rc, bool cancelled) => Assert.Equal(cancelled, UpdateInstaller.IsAuthCancelled(rc));
}
