using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CraftDriver.App.Services;
using CraftDriver.App.ViewModels;
using CraftDriver.App.Views;
using CraftDriver.Core.Config;
using CraftDriver.Core.Ipc;
using CraftDriver.Core.Sync;
using CraftDriver.Core.Updates;
using CraftDriver.Tests;

namespace CraftDriver.UiTests;

/// <summary>Nenhum teste de interface procura atualização na rede (o AppHost.Start agenda a verificação).</summary>
static class NoUpdateCheck
{
    [ModuleInitializer]
    internal static void Install() => Environment.SetEnvironmentVariable(UpdateCheck.DisableVariable, "1");
}

/// <summary>GitHub falso em memória: release, pacote e SHA256SUMS por URL.</summary>
sealed class UpdHttp : HttpMessageHandler
{
    public readonly Dictionary<string, Func<HttpResponseMessage>> Routes = new();
    public readonly List<string> Requested = [];
    /// <summary>Segura a resposta do pacote até liberar (download em andamento).</summary>
    public TaskCompletionSource? HoldPackage;
    public const string Base = "http://github.falso";

    public static string ReleaseJson(string version, IEnumerable<(string Name, long Size)> assets, string? url = null) =>
        $$"""
        { "tag_name": "v{{version}}", "name": "craftDriver v{{version}}", "draft": false, "prerelease": false,
          "html_url": "https://github.com/Kaike-png/craftDriver/releases/tag/v{{version}}",
          "body": "### Novidades\n- **Atualização automática**: o craftDriver avisa quando há versão nova e se `atualiza` sozinho.\n- Veja [a documentação](https://x/y).\n\n## Instalar\n| Distro | Comando |\n|---|---|\n| Arch | `sudo pacman -U x` |\n",
          "assets": [{{string.Join(",", assets.Select(a => $$"""{ "name": "{{a.Name}}", "browser_download_url": "{{url ?? Base}}/dl/{{a.Name}}", "size": {{a.Size}} }"""))}}] }
        """;

    /// <summary>Publica a release com o pacote (e SHA256SUMS certo, errado ou sem o pacote).</summary>
    public static (UpdHttp Http, byte[] Data) Serve(string version, string packageName, string sums = "certo")
    {
        var data = new byte[700_000];
        new Random(3).NextBytes(data);
        var hash = Convert.ToHexStringLower(SHA256.HashData(data));
        var http = new UpdHttp();
        http.Routes[UpdateCheck.LatestUrl(Base)] = () => Ok(ReleaseJson(version, [(packageName, data.Length), (UpdateAssets.SumsName, 100)]));
        http.Routes[$"{Base}/dl/{packageName}"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        var sumsText = sums switch
        {
            "errado" => $"{new string('0', 64)}  {packageName}\n",
            "sem" => $"{hash}  outro.tar.gz\n",
            _ => $"{hash}  {packageName}\n",
        };
        http.Routes[$"{Base}/dl/{UpdateAssets.SumsName}"] = () => Ok(sumsText);
        return (http, data);
    }

    static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        lock (Requested) Requested.Add(url);
        if (HoldPackage != null && url.Contains("/dl/") && !url.EndsWith(UpdateAssets.SumsName)) await HoldPackage.Task.WaitAsync(ct);
        return Routes.TryGetValue(url, out var r) ? r() : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") };
    }

    public int Count(string part)
    {
        lock (Requested) return Requested.Count(u => u.Contains(part));
    }
}

/// <summary>pacman/pkexec falsos (nada é instalado de verdade).</summary>
sealed class UpdRunner : ICommandRunner
{
    public readonly Dictionary<string, Func<IReadOnlyList<string>, Task<CommandResult>>> Programs = new();
    public readonly List<string> Calls = [];

    public UpdRunner On(string program, int rc, string stdout = "")
    {
        Programs[program] = _ => Task.FromResult(new CommandResult(rc, stdout, rc == 0 ? "" : "erro falso de " + program));
        return this;
    }

    /// <summary>Arch com pkexec respondendo <paramref name="pkexecRc"/>.</summary>
    public static UpdRunner Arch(int pkexecRc = 0) => new UpdRunner().On("pacman", 0, "craftdriver-bin\n").On("pkexec", pkexecRc);

    public Task<CommandResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add(string.Join(' ', new[] { file }.Concat(args)));
        return Programs.TryGetValue(file, out var f) ? f(args) : Task.FromResult(new CommandResult(CommandResult.NotFound, "", file + ": não encontrado"));
    }

    public bool Exists(string program) => Programs.ContainsKey(program);

    public List<string> Pkexec()
    {
        lock (Calls) return Calls.Where(c => c.StartsWith("pkexec ")).ToList();
    }
}

static class Upd
{
    public const string ArchPackage = "craftdriver-bin-0.3.0-1-x86_64.pkg.tar.zst";
    public const string SysExe = "/usr/lib/craftdriver/craftdriver";

    /// <summary>Liga o serviço ao GitHub falso e ao executor falso (instalação = Arch x64, versão atual 0.2.0).</summary>
    public static UpdateService Wire(UpdateService svc, UpdHttp http, UpdRunner runner, string? exe = SysExe, string? home = null)
    {
        svc.HttpFactory = () => new HttpClient(http, disposeHandler: false);
        svc.Runner = runner;
        svc.Env = v => v == UpdateCheck.ApiBaseVariable ? UpdHttp.Base : null;
        svc.CurrentVersion = "0.2.0";
        svc.ExePath = exe;
        svc.Home = home ?? "/home/ninguem";
        svc.Arch = UpdateArch.X64;
        svc.UpdatesDir = Directory.CreateTempSubdirectory("cd-upd-ui-").FullName;
        svc.LaunchUserInstall = (_, _) => throw new InvalidOperationException("não deveria iniciar o install-user.sh");
        return svc;
    }

    public static (UpdateService Svc, AppConfig Cfg, List<string> Saved) NewService(UpdHttp http, UpdRunner runner)
    {
        var cfg = new AppConfig();
        var saved = new List<string>();
        var log = new ActivityLog(Path.Combine(Directory.CreateTempSubdirectory("cd-upd-log-").FullName, "log.txt"));
        var svc = new UpdateService(cfg, log, () => { lock (saved) saved.Add(cfg.SkippedUpdateVersion + "|" + cfg.NotifiedUpdateVersion); return true; });
        return (Wire(svc, http, runner), cfg, saved);
    }

    public static void Until(Func<bool> cond, string what, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException(what);
            Thread.Sleep(20);
        }
    }
}

/// <summary>Serviço de atualização: agenda, verificação, download conferido e instalação (sem rede, sem pkexec real).</summary>
public class UpdateServiceTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Agenda_procura_depois_do_atraso_e_periodicamente()
    {
        var (http, _) = UpdHttp.Serve("0.3.0", Upd.ArchPackage);
        var (svc, _, _) = Upd.NewService(http, UpdRunner.Arch());
        svc.FirstCheckDelay = TimeSpan.FromMilliseconds(300);
        svc.CheckInterval = TimeSpan.FromMilliseconds(300);
        using (svc)
        {
            svc.Start();
            await Task.Delay(150, Ct);
            Assert.Equal(0, http.Count("releases/latest")); // não procura logo ao iniciar
            Upd.Until(() => http.Count("releases/latest") >= 2, "duas verificações agendadas");
            Assert.Equal("0.3.0", svc.Available?.Version);
        }
    }

    [Fact]
    public async Task Desligada_na_configuracao_nao_procura_mas_volta_ao_religar()
    {
        var (http, _) = UpdHttp.Serve("0.3.0", Upd.ArchPackage);
        var (svc, cfg, _) = Upd.NewService(http, UpdRunner.Arch());
        cfg.CheckForUpdates = false;
        svc.FirstCheckDelay = TimeSpan.FromMilliseconds(50);
        svc.CheckInterval = TimeSpan.FromMilliseconds(100);
        using (svc)
        {
            Assert.False(svc.AutomaticEnabled);
            svc.Start();
            await Task.Delay(600, Ct);
            Assert.Equal(0, http.Count("releases/latest"));
            Assert.Null(await svc.CheckAsync(manual: false, Ct));
            Assert.Equal(0, http.Count("releases/latest"));
            cfg.CheckForUpdates = true;
            Upd.Until(() => http.Count("releases/latest") >= 1, "verificação depois de religar");
        }
    }

    [Fact]
    public async Task Variavel_de_ambiente_desliga_a_automatica_mas_nao_o_botao()
    {
        var (http, _) = UpdHttp.Serve("0.3.0", Upd.ArchPackage);
        var (svc, _, _) = Upd.NewService(http, UpdRunner.Arch());
        svc.Env = v => v switch { UpdateCheck.DisableVariable => "1", UpdateCheck.ApiBaseVariable => UpdHttp.Base, _ => null };
        svc.FirstCheckDelay = TimeSpan.FromMilliseconds(50);
        svc.CheckInterval = TimeSpan.FromMilliseconds(100);
        using (svc)
        {
            Assert.False(svc.AutomaticEnabled);
            svc.Start();
            await Task.Delay(600, Ct);
            Assert.Equal(0, http.Count("releases/latest"));
            Assert.Equal("0.3.0", (await svc.CheckAsync(manual: true, Ct))?.Version); // "Procurar agora" funciona
        }
    }

    [Fact]
    public async Task Ignorar_esta_versao_vale_ate_sair_uma_mais_nova()
    {
        var (http, _) = UpdHttp.Serve("0.3.0", Upd.ArchPackage);
        var (svc, cfg, saved) = Upd.NewService(http, UpdRunner.Arch());
        Assert.Equal("0.3.0", (await svc.CheckAsync(manual: false, Ct))?.Version);
        svc.Skip();
        Assert.Null(svc.Available);
        Assert.Equal("0.3.0", cfg.SkippedUpdateVersion);
        Assert.Contains(saved, s => s.StartsWith("0.3.0|"));
        Assert.Null(await svc.CheckAsync(manual: false, Ct));
        Assert.Null(svc.Available);
        // versão mais nova que a ignorada aparece
        var (http2, _) = UpdHttp.Serve("0.4.0", "craftdriver-bin-0.4.0-1-x86_64.pkg.tar.zst");
        svc.HttpFactory = () => new HttpClient(http2, false);
        Assert.Equal("0.4.0", (await svc.CheckAsync(manual: false, Ct))?.Version);
        svc.Skip();
        Assert.Equal("0.4.0", cfg.SkippedUpdateVersion);
        // uma versão mais velha que a ignorada não volta a aparecer
        svc.HttpFactory = () => new HttpClient(http, false);
        Assert.Null(await svc.CheckAsync(manual: false, Ct));
        // "Procurar agora" mostra até a ignorada (pedido explícito) e esquece o "ignorar"
        svc.HttpFactory = () => new HttpClient(http2, false);
        Assert.Equal("0.4.0", (await svc.CheckAsync(manual: true, Ct))?.Version);
        Assert.Null(cfg.SkippedUpdateVersion);
    }

    [Fact]
    public async Task Avisa_uma_vez_por_versao_inclusive_entre_reinicios()
    {
        var (http, _) = UpdHttp.Serve("0.3.0", Upd.ArchPackage);
        var (svc, cfg, _) = Upd.NewService(http, UpdRunner.Arch());
        var seen = new List<string>();
        svc.Discovered += r => seen.Add(r.Version);
        await svc.CheckAsync(manual: false, Ct);
        await svc.CheckAsync(manual: false, Ct);
        await svc.CheckAsync(manual: true, Ct);
        Assert.Equal(["0.3.0"], seen);
        Assert.Equal("0.3.0", cfg.NotifiedUpdateVersion);
        // outro processo com a mesma configuração (reinício): não avisa de novo
        var again = Upd.Wire(new UpdateService(cfg, new ActivityLog(), () => true), http, UpdRunner.Arch());
        again.Discovered += r => seen.Add("2:" + r.Version);
        await again.CheckAsync(manual: false, Ct);
        Assert.Equal(["0.3.0"], seen);
        Assert.Equal("0.3.0", again.Available?.Version); // o painel continua aparecendo
    }

    [Fact]
    public async Task Ja_atualizado_nao_oferece_nada()
    {
        var (http, _) = UpdHttp.Serve("0.2.0", "craftdriver-bin-0.2.0-1-x86_64.pkg.tar.zst");
        var (svc, _, _) = Upd.NewService(http, UpdRunner.Arch());
        Assert.Null(await svc.CheckAsync(manual: true, Ct));
        Assert.Null(svc.Available);
        Assert.False(svc.LastCheckFailed);
        Assert.Contains("mais recente", svc.LastCheckText);
    }

    [Fact]
    public async Task Falha_de_rede_na_automatica_e_silenciosa()
    {
        var http = new UpdHttp();
        http.Routes[UpdateCheck.LatestUrl(UpdHttp.Base)] = () => throw new HttpRequestException("rede caiu");
        var dir = Directory.CreateTempSubdirectory("cd-upd-log-").FullName;
        var log = new ActivityLog(Path.Combine(dir, "log.txt"));
        var svc = Upd.Wire(new UpdateService(new AppConfig(), log, () => true), http, UpdRunner.Arch());
        Assert.Null(await svc.CheckAsync(manual: false, Ct));
        Assert.True(svc.LastCheckFailed);
        Assert.Empty(log.Entries); // nada na Atividade
        Assert.Contains("[DEBUG]", File.ReadAllText(Path.Combine(dir, "log.txt")));
        Assert.Null(await svc.CheckAsync(manual: true, Ct));
        Assert.Contains("rede caiu", svc.LastCheckText);
    }

    [Fact]
    public async Task Atualizar_pacote_do_Arch_baixa_confere_e_roda_pkexec_com_o_app_aberto()
    {
        var (http, data) = UpdHttp.Serve("0.3.0", Upd.ArchPackage);
        var runner = UpdRunner.Arch();
        var (svc, _, _) = Upd.NewService(http, runner);
        await svc.CheckAsync(manual: true, Ct);
        var phases = new List<(UpdatePhase, double)>();
        svc.Changed += () => { lock (phases) phases.Add((svc.Phase, svc.Progress)); };
        var asked = 0;
        var outcome = await svc.InstallAsync(() => { asked++; return Task.FromResult(true); }, Ct);
        Assert.Equal(UpdateOutcomeKind.Installed, outcome.Kind);
        var file = Path.Combine(svc.UpdatesDir, "0.3.0", Upd.ArchPackage);
        Assert.Equal(data, File.ReadAllBytes(file));
        Assert.Equal(["pkexec pacman -U --noconfirm " + file], runner.Pkexec());
        Assert.Equal(0, asked); // pacote do sistema: instala com o app aberto, sem pedir para sair
        Assert.Equal(UpdatePhase.Installed, svc.Phase);
        lock (phases)
        {
            Assert.Contains(phases, p => p.Item1 == UpdatePhase.Downloading && p.Item2 > 0);
            Assert.Contains(phases, p => p.Item1 == UpdatePhase.Installing);
        }
    }

    [Theory]
    [InlineData(126)]
    [InlineData(127)]
    public async Task Senha_cancelada_mantem_o_app_e_permite_tentar_de_novo(int rc)
    {
        var (http, _) = UpdHttp.Serve("0.3.0", Upd.ArchPackage);
        var (svc, _, _) = Upd.NewService(http, UpdRunner.Arch(pkexecRc: rc));
        await svc.CheckAsync(manual: true, Ct);
        var outcome = await svc.InstallAsync(() => Task.FromResult(true), Ct);
        Assert.Equal(UpdateOutcomeKind.Cancelled, outcome.Kind);
        Assert.Contains("cancelada", outcome.Message);
        Assert.Equal(UpdatePhase.Idle, svc.Phase);
        Assert.Equal("0.3.0", svc.Available?.Version);
        Assert.False(svc.IsBusy);
    }

    [Fact]
    public async Task Gerenciador_que_falha_mostra_o_erro()
    {
        var (http, _) = UpdHttp.Serve("0.3.0", Upd.ArchPackage);
        var (svc, _, _) = Upd.NewService(http, UpdRunner.Arch(pkexecRc: 1));
        await svc.CheckAsync(manual: true, Ct);
        var outcome = await svc.InstallAsync(() => Task.FromResult(true), Ct);
        Assert.Equal(UpdateOutcomeKind.Failed, outcome.Kind);
        Assert.Contains("erro falso de pkexec", outcome.Message);
        Assert.Equal(UpdatePhase.Failed, svc.Phase);
    }

    [Fact]
    public async Task Sem_pkexec_mostra_o_comando_para_o_terminal()
    {
        var (http, _) = UpdHttp.Serve("0.3.0", Upd.ArchPackage);
        var runner = new UpdRunner().On("pacman", 0, "craftdriver-bin\n"); // sem pkexec
        var (svc, _, _) = Upd.NewService(http, runner);
        await svc.CheckAsync(manual: true, Ct);
        var outcome = await svc.InstallAsync(() => Task.FromResult(true), Ct);
        Assert.Equal(UpdateOutcomeKind.Manual, outcome.Kind);
        Assert.Contains("sudo pacman -U " + Path.Combine(svc.UpdatesDir, "0.3.0", Upd.ArchPackage), outcome.Message);
        Assert.True(File.Exists(Path.Combine(svc.UpdatesDir, "0.3.0", Upd.ArchPackage))); // já baixado e conferido
        Assert.Empty(runner.Pkexec());
    }

    [Fact]
    public async Task Arch_arm64_sem_pacote_so_avisa_sem_baixar()
    {
        var (http, _) = UpdHttp.Serve("0.3.0", Upd.ArchPackage);
        var (svc, _, _) = Upd.NewService(http, UpdRunner.Arch());
        svc.Arch = UpdateArch.Arm64;
        await svc.CheckAsync(manual: true, Ct);
        var outcome = await svc.InstallAsync(() => Task.FromResult(true), Ct);
        Assert.Equal(UpdateOutcomeKind.Manual, outcome.Kind);
        Assert.Contains("https://github.com/Kaike-png/craftDriver/releases/tag/v0.3.0", outcome.Message);
        Assert.Equal(0, http.Count("/dl/"));
    }

    [Fact]
    public async Task Execucao_de_desenvolvimento_so_avisa()
    {
        var (http, _) = UpdHttp.Serve("0.3.0", Upd.ArchPackage);
        var runner = UpdRunner.Arch();
        var (svc, _, _) = Upd.NewService(http, runner);
        svc.ExePath = "/usr/share/dotnet/dotnet";
        await svc.CheckAsync(manual: true, Ct);
        Assert.Equal(InstallKind.Unknown, svc.Install?.Kind);
        var outcome = await svc.InstallAsync(() => Task.FromResult(true), Ct);
        Assert.Equal(UpdateOutcomeKind.Manual, outcome.Kind);
        Assert.Equal(0, http.Count("/dl/"));
        Assert.Empty(runner.Pkexec());
    }

    [Theory]
    [InlineData("errado", "não confere")]
    [InlineData("sem", "não lista")]
    public async Task Sha256_errado_ou_ausente_nao_instala(string sums, string message)
    {
        var (http, _) = UpdHttp.Serve("0.3.0", Upd.ArchPackage, sums);
        var runner = UpdRunner.Arch();
        var (svc, _, _) = Upd.NewService(http, runner);
        await svc.CheckAsync(manual: true, Ct);
        var outcome = await svc.InstallAsync(() => Task.FromResult(true), Ct);
        Assert.Equal(UpdateOutcomeKind.Failed, outcome.Kind);
        Assert.Contains(message, outcome.Message);
        Assert.Empty(runner.Pkexec());
        Assert.Empty(Directory.GetFiles(svc.UpdatesDir, "*", SearchOption.AllDirectories));
        Assert.Equal(UpdatePhase.Failed, svc.Phase);
        Assert.Contains(message, svc.StatusText);
    }

    [Fact]
    public async Task Duas_atualizacoes_ao_mesmo_tempo_nao()
    {
        var (http, _) = UpdHttp.Serve("0.3.0", Upd.ArchPackage);
        http.HoldPackage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = UpdRunner.Arch();
        var (svc, _, _) = Upd.NewService(http, runner);
        await svc.CheckAsync(manual: true, Ct);
        var first = svc.InstallAsync(() => Task.FromResult(true), Ct);
        Upd.Until(() => svc.IsBusy && svc.Phase == UpdatePhase.Downloading, "download em andamento");
        // a segunda volta na hora (sem esperar o download da primeira)
        var second = await svc.InstallAsync(() => Task.FromResult(true), Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(UpdateOutcomeKind.Busy, second.Kind);
        http.HoldPackage.SetResult();
        Assert.Equal(UpdateOutcomeKind.Installed, (await first).Kind);
        Assert.Single(runner.Pkexec());
    }

    [Fact]
    public async Task Instalacao_do_usuario_confirma_a_saida_antes_de_iniciar_o_instalador()
    {
        var home = Directory.CreateTempSubdirectory("cd-upd-home-").FullName;
        const string tar = "craftdriver-0.3.0-linux-x64.tar.gz";
        var (http, _) = UpdHttp.Serve("0.3.0", tar);
        var (svc, _, _) = Upd.NewService(http, new UpdRunner());
        svc.Home = home;
        svc.ExePath = Path.Combine(home, ".local", "opt", "craftdriver", "lib", "craftdriver");
        var launched = new List<string>();
        svc.LaunchUserInstall = (t, _) => { launched.Add(t); return null; };
        await svc.CheckAsync(manual: true, Ct);
        Assert.Equal(InstallKind.UserInstall, svc.Install?.Kind);

        var no = await svc.InstallAsync(() => Task.FromResult(false), Ct);
        Assert.Equal(UpdateOutcomeKind.Cancelled, no.Kind);
        Assert.Empty(launched);
        Assert.False(svc.IsBusy);

        var yes = await svc.InstallAsync(() => Task.FromResult(true), Ct);
        Assert.Equal(UpdateOutcomeKind.Restarting, yes.Kind);
        Assert.Equal([Path.Combine(svc.UpdatesDir, "0.3.0", tar)], launched);
        Assert.Equal(UpdatePhase.Restarting, svc.Phase);
    }
}

public class UpdateServiceUserInstallTests
{
    /// <summary>O AppHost.Start agenda a verificação: nos testes de interface ela fica desligada pela variável.</summary>
    [Fact]
    public void Testes_de_interface_nao_procuram_atualizacao_na_rede()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable(UpdateCheck.DisableVariable));
        var svc = new UpdateService(new AppConfig(), new ActivityLog(), () => true);
        Assert.False(svc.AutomaticEnabled);
    }

    /// <summary>O install-user.sh desacoplado falhou e o app continua aberto: o painel mostra o erro e o log.</summary>
    [Fact]
    public async Task Instalador_do_usuario_que_falha_com_o_app_aberto_vira_erro_no_painel()
    {
        var home = Directory.CreateTempSubdirectory("cd-upd-home-").FullName;
        const string tar = "craftdriver-0.3.0-linux-x64.tar.gz";
        var (http, _) = UpdHttp.Serve("0.3.0", tar);
        var (svc, _, _) = Upd.NewService(http, new UpdRunner());
        svc.Home = home;
        svc.ExePath = Path.Combine(home, ".local", "opt", "craftdriver", "lib", "craftdriver");
        string? logSeen = null;
        svc.LaunchUserInstall = (_, log) =>
        {
            logSeen = log;
            return Process.Start(new ProcessStartInfo("/bin/sh", ["-c", "sleep 0.3; exit 4"]) { UseShellExecute = false });
        };
        await svc.CheckAsync(manual: true, TestContext.Current.CancellationToken);
        var outcome = await svc.InstallAsync(() => Task.FromResult(true), TestContext.Current.CancellationToken);
        Assert.Equal(UpdateOutcomeKind.Restarting, outcome.Kind);
        Upd.Until(() => svc.Phase == UpdatePhase.Failed, "erro do instalador desacoplado");
        Assert.Contains("código 4", svc.StatusText);
        Assert.Contains(logSeen!, svc.StatusText);
        Assert.Equal(Path.Combine(svc.UpdatesDir, "0.3.0", "install.log"), logSeen);
    }
}

/// <summary>Painel, notificação, bandeja, Configurações e IPC (janela headless; Graph e GitHub falsos).</summary>
[Collection("host-isolado")]
public class UpdateUiTests
{
    static readonly BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;

    static AppHost NewHost(UpdHttp http, UpdRunner runner)
    {
        ShellUi.NewHome();
        var host = Task.Run(() => AppHost.CreateAsync()).GetAwaiter().GetResult();
        CraftDriver.App.App.Host = host;
        host.GraphFactory = _ => new FakeGraph();
        host.Notify = (_, _, _) => { };
        host.MountFunc = (_, _) => new TaskCompletionSource().Task;
        Upd.Wire(host.Updates, http, runner);
        return host;
    }

    static (MainViewModel Vm, MainWindow W) Window(AppHost host)
    {
        var vm = new MainViewModel(host);
        var w = new MainWindow { DataContext = vm, Width = 920, Height = 640 };
        w.Show();
        vm.SelectedTab = 0;
        ShellUi.Pump(300);
        return (vm, w);
    }

    static T Result<T>(Task<T> t)
    {
        ShellUi.Wait(t);
        return t.Result;
    }

    static Button ButtonIn(Control root, string text) =>
        root.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == text && b.IsEffectivelyVisible);

    static void Close(AppHost host, MainViewModel? vm, Window? w)
    {
        w?.Close();
        vm?.Dispose();
        ShellUi.Wait(Task.Run(host.ShutdownAsync), 20000);
    }

    [AvaloniaFact]
    public void Painel_mostra_a_versao_nova_com_notas_e_botoes()
    {
        var (http, _) = UpdHttp.Serve("0.3.0", Upd.ArchPackage);
        var host = NewHost(http, UpdRunner.Arch());
        var opened = new List<string>();
        host.OpenUrl = opened.Add;
        ShellUi.Wait(host.Updates.CheckAsync(manual: false));
        var (vm, w) = Window(host);
        try
        {
            ShellUi.PumpUntil(() => vm.UpdateVisible, "painel da atualização");
            var panel = w.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "UpdatePanel");
            Assert.True(panel.IsEffectivelyVisible);
            var texts = panel.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains("Nova versão 0.3.0 disponível", texts);
            var notes = Assert.Single(texts, t => t?.Contains("Atualização automática") == true)!;
            Assert.DoesNotContain("**", notes);
            Assert.DoesNotContain("pacman -U", notes);
            Assert.Equal("Atualizar para 0.3.0", vm.UpdateTrayText);
            Assert.True(ButtonIn(panel, "Atualizar agora").IsEffectivelyEnabled);
            ShellUi.Save(w, "atualizacao-disponivel");

            ButtonIn(panel, "Ver novidades").Command!.Execute(null);
            Assert.Equal(["https://github.com/Kaike-png/craftDriver/releases/tag/v0.3.0"], opened);

            ButtonIn(panel, "Ignorar esta versão").Command!.Execute(null);
            ShellUi.Pump(200);
            Assert.False(vm.UpdateVisible);
            Assert.Null(vm.UpdateTrayText);
            Assert.Contains("\"SkippedUpdateVersion\": \"0.3.0\"", File.ReadAllText(AppPaths.ConfigFile));
        }
        finally { Close(host, vm, w); }
    }

    [AvaloniaFact]
    public void Atualizar_agora_com_sucesso_pede_para_reiniciar_e_so_reinicia_se_confirmado()
    {
        var (http, _) = UpdHttp.Serve("0.3.0", Upd.ArchPackage);
        var runner = UpdRunner.Arch();
        var host = NewHost(http, runner);
        ShellUi.Wait(host.Updates.CheckAsync(manual: true));
        var (vm, w) = Window(host);
        var asked = new List<ConfirmRequest>();
        var answer = false;
        vm.ConfirmRequested += r => { asked.Add(r); return Task.FromResult(answer); };
        var restarts = 0;
        // o MainWindow liga RestartRequested ao AppRestart.ScheduleRelaunch: troca por um contador
        typeof(MainViewModel).GetField("RestartRequested", Priv)!.SetValue(vm, null);
        vm.RestartRequested += () => restarts++;
        try
        {
            ShellUi.Wait(vm.UpdateNowCommand.ExecuteAsync(null), 20000);
            Assert.Single(runner.Pkexec());
            var req = Assert.Single(asked);
            Assert.Contains("0.3.0", req.Text);
            Assert.Equal("Reiniciar agora", req.Accept);
            Assert.Equal(0, restarts); // recusou: não reinicia
            Assert.True(vm.RestartNeeded); // o "Reiniciar agora" da barra fica disponível
            Assert.Contains("0.3.0", vm.Message);
            Assert.False(vm.UpdateCanStart);
            ShellUi.Pump(200);
            var panel = w.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "UpdatePanel");
            Assert.Equal(vm.RestartCommand, ButtonIn(panel, "Reiniciar agora").Command);
            Assert.DoesNotContain(panel.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && b.Content as string == "Atualizar agora");
            ShellUi.Save(w, "atualizacao-instalada");

            // "Reiniciar agora" da barra: o mesmo fluxo (ConfirmQuitAsync + RestartRequested)
            answer = true;
            ShellUi.Wait(vm.RestartCommand.ExecuteAsync(null));
            Assert.Equal(1, restarts);
        }
        finally { Close(host, vm, w); }
    }

    [AvaloniaFact]
    public void Reinicio_confirmado_depois_da_atualizacao_agenda_o_relancamento()
    {
        var (http, _) = UpdHttp.Serve("0.3.0", Upd.ArchPackage);
        var host = NewHost(http, UpdRunner.Arch());
        ShellUi.Wait(host.Updates.CheckAsync(manual: true));
        var (vm, w) = Window(host);
        vm.ConfirmRequested += _ => Task.FromResult(true);
        var restarts = 0;
        typeof(MainViewModel).GetField("RestartRequested", Priv)!.SetValue(vm, null);
        vm.RestartRequested += () => restarts++;
        // com transferência ativa, o ConfirmQuitAsync também pergunta (o mesmo do Sair)
        var t = host.Activity.StartTransfer("grande.iso", 1000, upload: true);
        var asked = new List<string>();
        vm.ConfirmRequested += r => { asked.Add(r.Title); return Task.FromResult(true); };
        try
        {
            ShellUi.Wait(vm.UpdateNowCommand.ExecuteAsync(null), 20000);
            Assert.Equal(1, restarts);
            Assert.Contains("Sair do craftDriver?", asked);
        }
        finally
        {
            host.Activity.EndTransfer(t);
            Close(host, vm, w);
        }
    }

    [AvaloniaFact]
    public void Senha_cancelada_mantem_o_app_aberto_e_avisa()
    {
        var (http, _) = UpdHttp.Serve("0.3.0", Upd.ArchPackage);
        var host = NewHost(http, UpdRunner.Arch(pkexecRc: 126));
        ShellUi.Wait(host.Updates.CheckAsync(manual: true));
        var (vm, w) = Window(host);
        var asked = 0;
        vm.ConfirmRequested += _ => { asked++; return Task.FromResult(true); };
        var quits = 0;
        vm.QuitRequested += () => quits++;
        typeof(MainViewModel).GetField("RestartRequested", Priv)!.SetValue(vm, null);
        var restarts = 0;
        vm.RestartRequested += () => restarts++;
        try
        {
            ShellUi.Wait(vm.UpdateNowCommand.ExecuteAsync(null), 20000);
            Assert.Equal(0, asked);
            Assert.Equal(0, quits + restarts);
            Assert.Contains("cancelada", vm.UpdateStatus);
            Assert.True(vm.UpdateVisible);
            Assert.True(vm.UpdateCanStart); // pode tentar de novo
        }
        finally { Close(host, vm, w); }
    }

    [AvaloniaFact]
    public void Erro_de_sha256_aparece_no_painel()
    {
        var (http, _) = UpdHttp.Serve("0.3.0", Upd.ArchPackage, "errado");
        var runner = UpdRunner.Arch();
        var host = NewHost(http, runner);
        ShellUi.Wait(host.Updates.CheckAsync(manual: true));
        var (vm, w) = Window(host);
        try
        {
            ShellUi.Wait(vm.UpdateNowCommand.ExecuteAsync(null), 20000);
            ShellUi.Pump(200);
            Assert.True(vm.UpdateStatusIsError);
            Assert.Contains("SHA256", vm.UpdateStatus);
            Assert.Empty(runner.Pkexec());
            var panel = w.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "UpdatePanel");
            Assert.Contains(panel.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text?.Contains("SHA256") == true);
            ShellUi.Save(w, "atualizacao-erro");
        }
        finally { Close(host, vm, w); }
    }

    [AvaloniaFact]
    public void Progresso_do_download_aparece_no_painel()
    {
        var (http, _) = UpdHttp.Serve("0.3.0", Upd.ArchPackage);
        http.HoldPackage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = NewHost(http, UpdRunner.Arch());
        ShellUi.Wait(host.Updates.CheckAsync(manual: true));
        var (vm, w) = Window(host);
        vm.ConfirmRequested += _ => Task.FromResult(false);
        try
        {
            var run = vm.UpdateNowCommand.ExecuteAsync(null);
            ShellUi.PumpUntil(() => vm.UpdateShowProgress && vm.UpdateStatus.Contains("Baixando"), "progresso visível");
            Assert.False(vm.UpdateCanStart); // sem segunda atualização
            ShellUi.Save(w, "atualizacao-baixando");
            http.HoldPackage.SetResult();
            ShellUi.Wait(run, 20000);
        }
        finally { Close(host, vm, w); }
    }

    [AvaloniaFact]
    public void Configuracoes_mostram_a_versao_a_caixa_e_procurar_agora()
    {
        var (http, _) = UpdHttp.Serve("0.2.0", "craftdriver-bin-0.2.0-1-x86_64.pkg.tar.zst");
        var host = NewHost(http, UpdRunner.Arch());
        var (vm, w) = Window(host);
        try
        {
            vm.SelectedTab = MainViewModel.SettingsTab;
            ShellUi.Pump(300);
            var box = w.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Procurar atualizações automaticamente");
            Assert.True(box.IsChecked);
            Assert.Contains(w.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Versão instalada: 0.2.0");
            box.IsChecked = false;
            ShellUi.Pump(100);
            Assert.False(host.Config.CheckForUpdates);
            Assert.Contains("\"CheckForUpdates\": false", File.ReadAllText(AppPaths.ConfigFile)); // grava na hora

            ShellUi.Wait(vm.CheckForUpdatesNowCommand.ExecuteAsync(null));
            Assert.Equal(1, http.Count("releases/latest")); // o botão procura mesmo com a automática desligada
            Assert.Contains("mais recente", vm.UpdateCheckText);
            ShellUi.Pump(200);
            ShellUi.Save(w, "configuracoes-atualizacoes");
        }
        finally { Close(host, vm, w); }
    }

    [AvaloniaFact]
    public void Procurar_agora_encontra_versao_nova_e_abre_o_painel()
    {
        var (http, _) = UpdHttp.Serve("0.3.0", Upd.ArchPackage);
        var host = NewHost(http, UpdRunner.Arch());
        var (vm, w) = Window(host);
        try
        {
            Assert.False(vm.UpdateVisible);
            ShellUi.Wait(vm.CheckForUpdatesNowCommand.ExecuteAsync(null));
            ShellUi.PumpUntil(() => vm.UpdateVisible, "painel depois do Procurar agora");
            Assert.Contains("0.3.0", vm.UpdateCheckText);
        }
        finally { Close(host, vm, w); }
    }

    [AvaloniaFact]
    public void Item_da_bandeja_aparece_com_versao_nova_e_inicia_o_fluxo()
    {
        var (http, _) = UpdHttp.Serve("0.3.0", Upd.ArchPackage);
        var runner = UpdRunner.Arch(pkexecRc: 126);
        var host = NewHost(http, runner);
        var app = new CraftDriver.App.App();
        var proxy = DispatchProxy.Create<IClassicDesktopStyleApplicationLifetime, Rodada3AppTests.FakeLifetime>();
        typeof(CraftDriver.App.App).GetField("_desktop", Priv)!.SetValue(app, proxy);
        typeof(CraftDriver.App.App).GetMethod("InitWithHost", Priv)!.Invoke(app, [false]);
        var vm = (MainViewModel)typeof(CraftDriver.App.App).GetField("_vm", Priv)!.GetValue(app)!;
        try
        {
            var tray = (TrayIcon)typeof(CraftDriver.App.App).GetField("_tray", Priv)!.GetValue(app)!;
            NativeMenuItem? Item() => tray.Menu!.Items.OfType<NativeMenuItem>().SingleOrDefault(i => i.Header?.StartsWith("Atualizar") == true);
            Assert.False(Item()?.IsVisible ?? false);
            ShellUi.Wait(host.Updates.CheckAsync(manual: true));
            ShellUi.PumpUntil(() => Item() is { IsVisible: true, Header: "Atualizar para 0.3.0" }, "item da bandeja", 4000);
            var m = typeof(NativeMenuItem).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .First(x => x.Name.Contains("RaiseClick") && x.GetParameters().Length == 0);
            m.Invoke(Item(), null);
            ShellUi.PumpUntil(() => runner.Pkexec().Count == 1, "fluxo iniciado pela bandeja", 10000);
            ShellUi.PumpUntil(() => vm.UpdateStatus.Contains("cancelada"), "fim do fluxo", 5000);
        }
        finally { ShellUi.Wait(Task.Run(host.ShutdownAsync), 20000); }
    }

    [AvaloniaFact]
    public void Notificacao_tem_Atualizar_agora_e_Ver_novidades()
    {
        var (http, _) = UpdHttp.Serve("0.3.0", Upd.ArchPackage);
        var host = NewHost(http, UpdRunner.Arch());
        var sent = new List<(string Title, string Body, NotificationAction[] Actions)>();
        host.Notify = (t, b, a) => sent.Add((t, b, a));
        var opened = new List<string>();
        host.OpenUrl = opened.Add;
        try
        {
            // sem janela (--headless): só "Ver novidades" e a dica do CLI
            ShellUi.Wait(host.Updates.CheckAsync(manual: false));
            ShellUi.PumpUntil(() => sent.Count == 1, "notificação");
            var (title, body, actions) = sent[0];
            Assert.Contains("0.3.0", title);
            Assert.Contains("0.2.0", body);
            Assert.Equal(["notes"], actions.Select(a => a.Id));
            Assert.Contains("craftdriver update", DesktopNotifier.WithHints(body, actions));

            // com janela: "Atualizar agora" (botão e clique no corpo) e "Ver novidades"
            var requested = 0;
            host.UpdateNowRequested += () => requested++;
            host.OnUpdateDiscovered(host.Updates.Available!);
            var (_, _, withWindow) = sent[1];
            Assert.Equal([NotificationAction.Default, "update", "notes"], withWindow.Select(a => a.Id));
            withWindow.Single(a => a.Id == "update").OnInvoke();
            withWindow.Single(a => a.Id == "notes").OnInvoke();
            Assert.Equal(1, requested);
            Assert.Equal(["https://github.com/Kaike-png/craftDriver/releases/tag/v0.3.0"], opened);

            // uma por versão: verificar de novo não notifica
            ShellUi.Wait(host.Updates.CheckAsync(manual: false));
            ShellUi.Pump(200);
            Assert.Equal(2, sent.Count);
        }
        finally { ShellUi.Wait(Task.Run(host.ShutdownAsync), 20000); }
    }

    [AvaloniaFact]
    public void Ipc_update_inicia_o_fluxo_da_janela_ou_devolve_o_trabalho_ao_cli()
    {
        var (http, _) = UpdHttp.Serve("0.3.0", Upd.ArchPackage);
        var host = NewHost(http, UpdRunner.Arch());
        try
        {
            var headless = Result(host.HandleIpcAsync(new IpcRequest("update", [])));
            Assert.False(headless.Ok);
            Assert.Equal(UpdateCli.HeadlessReply, headless.Text);

            var requested = 0;
            host.UpdateNowRequested += () => requested++;
            var resp = Result(host.HandleIpcAsync(new IpcRequest("update", [])));
            Assert.True(resp.Ok, resp.Text);
            Assert.Contains("0.3.0", resp.Text);
            Assert.Equal(1, requested);

            var (http2, _) = UpdHttp.Serve("0.2.0", "craftdriver-bin-0.2.0-1-x86_64.pkg.tar.zst");
            Upd.Wire(host.Updates, http2, UpdRunner.Arch());
            host.Updates.Skip(); // limpa o "disponível"
            var upToDate = Result(host.HandleIpcAsync(new IpcRequest("update", [])));
            Assert.True(upToDate.Ok);
            Assert.Contains("mais recente", upToDate.Text);
            Assert.Equal(1, requested);
        }
        finally { ShellUi.Wait(Task.Run(host.ShutdownAsync), 20000); }
    }
}
