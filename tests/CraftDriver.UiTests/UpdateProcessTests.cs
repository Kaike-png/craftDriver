using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CraftDriver.App.Services;
using CraftDriver.Core.Updates;

namespace CraftDriver.UiTests;

/// <summary>
/// CLI <c>craftdriver update</c> com o processo real (isolado: CRAFTDRIVER_HOME, XDG_RUNTIME_DIR existente, D-Bus
/// inexistente) contra um GitHub falso local (CRAFTDRIVER_UPDATE_URL), e o instalador desacoplado do install-user.sh
/// com HOME descartável e um tarball falso — nada toca a instalação real.
/// </summary>
public class UpdateProcessTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    static readonly string Scratch = Path.Combine(Path.GetTempPath(), "cd-upd-proc");

    /// <summary>API do GitHub falsa num HttpListener local (porta livre).</summary>
    sealed class LocalGitHub : IDisposable
    {
        readonly HttpListener _listener = new();
        public string Base { get; }
        public string Version = "9.9.9";
        public int Status = 200;
        public int Requests;

        public LocalGitHub()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Base = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Base + "/");
            _listener.Start();
            _ = Task.Run(Loop);
        }

        async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }
                Interlocked.Increment(ref Requests);
                var ok = ctx.Request.Url!.AbsolutePath == "/repos/Kaike-png/craftDriver/releases/latest" && Status == 200;
                var body = ok
                    ? UpdHttp.ReleaseJson(Version, [($"craftdriver-{Version}-linux-x64.tar.gz", 10), (UpdateAssets.SumsName, 10)], Base)
                    : "{\"message\":\"erro falso\"}";
                ctx.Response.StatusCode = ok ? 200 : Status == 200 ? 404 : Status;
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        }

        public void Dispose() => _listener.Close();
    }

    static (int Code, string Out, string Err) Cli(string apiBase, params string[] args)
    {
        var home = Path.Combine(Scratch, "home-" + Guid.NewGuid().ToString("N")[..6]);
        var run = Path.Combine(Scratch, "run");
        Directory.CreateDirectory(run);
        var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "CraftDriver.App"))
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["CRAFTDRIVER_HOME"] = home;
        psi.Environment["XDG_RUNTIME_DIR"] = run;
        psi.Environment["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/nonexistent";
        psi.Environment["CRAFTDRIVER_NO_NOTIFY"] = "1";
        psi.Environment[UpdateCheck.ApiBaseVariable] = apiBase;
        psi.Environment.Remove("WAYLAND_DISPLAY");
        psi.Environment.Remove("DISPLAY");
        psi.Environment["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? Path.GetDirectoryName(Environment.ProcessPath)!;
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync(Ct);
        var stderr = p.StandardError.ReadToEndAsync(Ct);
        if (!p.WaitForExit(60_000))
        {
            p.Kill(true);
            Assert.Fail("craftdriver " + string.Join(' ', args) + " não terminou em 60 s");
        }
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    [Fact]
    public void Check_com_versao_nova_sai_10_e_diz_o_jeito_de_instalacao()
    {
        using var gh = new LocalGitHub();
        var (code, stdout, stderr) = Cli(gh.Base, "update", "--check");
        Assert.True(code == UpdateCli.UpdateAvailable, $"rc {code}\n{stdout}\n{stderr}");
        Assert.Contains("Versão instalada: " + AppVersion.Current, stdout);
        Assert.Contains("9.9.9", stdout);
        Assert.Contains("Instalação:", stdout);
        Assert.Contains("https://github.com/Kaike-png/craftDriver/releases/tag/v9.9.9", stdout);
        Assert.Equal(1, gh.Requests);
    }

    [Fact]
    public void Check_na_versao_atual_sai_0()
    {
        using var gh = new LocalGitHub { Version = AppVersion.Current };
        var (code, stdout, stderr) = Cli(gh.Base, "update", "--check");
        Assert.True(code == UpdateCli.UpToDate, $"rc {code}\n{stdout}\n{stderr}");
        Assert.Contains("mais recente", stdout);
    }

    [Fact]
    public void Check_com_erro_do_GitHub_sai_1()
    {
        using var gh = new LocalGitHub { Status = 500 };
        var (code, stdout, stderr) = Cli(gh.Base, "update", "--check");
        Assert.True(code == UpdateCli.Error, $"rc {code}\n{stdout}\n{stderr}");
        Assert.Contains("500", stderr);
    }

    [Fact]
    public void Update_sem_instancia_numa_execucao_de_desenvolvimento_so_mostra_o_link()
    {
        using var gh = new LocalGitHub();
        var (code, stdout, stderr) = Cli(gh.Base, "update");
        Assert.True(code == UpdateCli.Error, $"rc {code}\n{stdout}\n{stderr}");
        Assert.Contains("https://github.com/Kaike-png/craftDriver/releases/tag/v9.9.9", stdout + stderr);
        Assert.Contains("automaticamente", stdout + stderr);
    }

    // ---------- install-user.sh desacoplado ----------

    static string Repo()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CraftDriver.slnx"))) root = Path.GetDirectoryName(root)!;
        return root;
    }

    static void Executable(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
    }

    static void Sh(string command, string? cwd = null)
    {
        var psi = new ProcessStartInfo("/bin/sh", ["-c", command]) { UseShellExecute = false, RedirectStandardError = true, WorkingDirectory = cwd ?? "/" };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, command + ": " + err);
    }

    sealed record Fixture(string Root, string Home, string Tarball, string Mark, string Log, string Work, Process App) : IDisposable
    {
        public void Dispose()
        {
            if (!App.HasExited) { try { App.Kill(); } catch { } }
            App.Dispose();
        }
    }

    /// <summary>
    /// HOME descartável com uma instalação "velha" (o binário falso, ao receber quit, encerra o "app" = sleep) e um
    /// tarball "9.9.9" no formato do release com o install-user.sh de verdade.
    /// </summary>
    static Fixture Prepare(bool withInstaller = true, bool withApp = true)
    {
        var root = Path.Combine(Scratch, "inst-" + Guid.NewGuid().ToString("N")[..6]);
        var home = Path.Combine(root, "home");
        var mark = Path.Combine(root, "marcas.txt");
        Directory.CreateDirectory(root);
        var app = Process.Start(new ProcessStartInfo("sleep", ["300"]) { UseShellExecute = false })!;
        File.WriteAllText(Path.Combine(root, "app.pid"), app.Id.ToString());
        var oldBin = Path.Combine(home, ".local", "opt", "craftdriver", "lib", "craftdriver");
        Executable(oldBin, $$"""
            #!/bin/sh
            echo "velha $*" >> "{{mark}}"
            [ "$1" = quit ] && kill "$(cat "{{root}}/app.pid")"
            exit 0
            """);
        Directory.CreateDirectory(Path.Combine(home, ".local", "bin"));
        File.CreateSymbolicLink(Path.Combine(home, ".local", "bin", "craftdriver"), oldBin);

        var pkg = Path.Combine(root, "pacote", "craftdriver-9.9.9-linux-x64");
        if (withApp)
            Executable(Path.Combine(pkg, "lib", "craftdriver"), $$"""
                #!/bin/sh
                echo "nova $*" >> "{{mark}}"
                exit 0
                """);
        else Directory.CreateDirectory(Path.Combine(pkg, "lib"));
        Sh($"cp -r '{Path.Combine(Repo(), "packaging", "share")}' '{pkg}/share'");
        if (withInstaller) Sh($"install -m755 '{Path.Combine(Repo(), "packaging", "install-user.sh")}' '{pkg}/install-user.sh'");
        var tarball = Path.Combine(root, "craftdriver-9.9.9-linux-x64.tar.gz");
        Sh($"tar -C '{Path.GetDirectoryName(pkg)}' -czf '{tarball}' craftdriver-9.9.9-linux-x64");
        return new Fixture(root, home, tarball, mark, Path.Combine(root, "install.log"), Path.Combine(root, "extraido"), app);
    }

    static Process StartInstaller(Fixture f)
    {
        var relaunch = new[] { Path.Combine(f.Home, ".local", "opt", "craftdriver", "lib", "craftdriver"), "--teste", "com espaço" };
        var psi = UserInstallUpdate.Build(f.Tarball, f.Work, f.App.Id, AppRestart.StartTimeOf(f.App.Id), f.Log, relaunch);
        psi.Environment["HOME"] = f.Home;
        psi.Environment["XDG_DATA_HOME"] = Path.Combine(f.Home, ".local", "share");
        psi.Environment["XDG_CONFIG_HOME"] = Path.Combine(f.Home, ".config");
        psi.Environment["CRAFTDRIVER_HOME"] = Path.Combine(f.Root, "instancia");
        psi.Environment["XDG_RUNTIME_DIR"] = Path.Combine(f.Root, "run");
        psi.Environment["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/nonexistent";
        psi.Environment["CRAFTDRIVER_QUIT_TIMEOUT"] = "10";
        Directory.CreateDirectory(Path.Combine(f.Root, "run"));
        return Process.Start(psi)!;
    }

    static string Read(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

    [Fact]
    public void Instalador_do_usuario_desacoplado_troca_a_versao_e_reabre_o_app()
    {
        using var f = Prepare();
        using var sh = StartInstaller(f);
        Assert.True(sh.WaitForExit(60_000), "o instalador desacoplado não terminou: " + Read(f.Log));
        // o sh termina com exec do app novo: espera a marca dele
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!Read(f.Mark).Contains("nova ") && DateTime.UtcNow < until) Thread.Sleep(50);
        var marks = Read(f.Mark).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(marks is ["velha quit", "nova --teste com espaço"], $"marcas: [{string.Join(" | ", marks)}]\nlog:\n{Read(f.Log)}");
        Assert.True(f.App.WaitForExit(5000), "o app antigo não foi encerrado");
        Assert.Contains("nova", File.ReadAllText(Path.Combine(f.Home, ".local", "opt", "craftdriver", "lib", "craftdriver")));
        Assert.Contains("Instalado em", Read(f.Log));
        Assert.False(Directory.Exists(f.Work), "pasta temporária ficou");
        Assert.Equal(0, sh.ExitCode);
    }

    /// <param name="installer">false = tarball sem install-user.sh; true = install-user.sh que recusa (sem lib/craftdriver).</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Instalador_que_falha_antes_de_encerrar_nao_reabre_e_devolve_erro(bool installer)
    {
        using var f = Prepare(withInstaller: installer, withApp: !installer);
        using var sh = StartInstaller(f);
        Assert.True(sh.WaitForExit(30_000), "o instalador desacoplado não terminou: " + Read(f.Log));
        Assert.NotEqual(0, sh.ExitCode);
        Assert.True(Read(f.Mark) == "", $"nem quit, nem reabertura: [{Read(f.Mark)}]\n{Read(f.Log)}");
        Assert.False(f.App.HasExited, "o app foi encerrado mesmo sem instalar");
        Assert.Contains(installer ? "Extraia o tarball inteiro" : "não tem install-user.sh", Read(f.Log));
        Assert.False(Directory.Exists(f.Work));
    }
}
