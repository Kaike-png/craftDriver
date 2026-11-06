using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CraftDriver.App.Services;
using CraftDriver.App.ViewModels;
using CraftDriver.Core.Config;
using CraftDriver.Core.Ipc;
using CraftDriver.Core.Sync;
using CraftDriver.Tests;

namespace CraftDriver.UiTests;

/// <summary>
/// PENDENCIAS §15 (continuação dos RED de <see cref="Secao15Tests"/>): tratador global, instância única,
/// sinais, montagem ocupada, configuração somente leitura, notificações, área de transferência e IPC.
/// Trocam CRAFTDRIVER_HOME/PATH (globais ao processo): coleção sem paralelismo.
/// </summary>
[Collection("host-isolado")]
public class Secao15ContinuacaoTests
{
    static AppHost NewHost(string? configJson = null)
    {
        var home = ShellUi.NewHome();
        if (configJson != null) File.WriteAllText(Path.Combine(home, "config", "config.json"), configJson.Replace("{HOME}", home));
        var host = Task.Run(() => AppHost.CreateAsync()).GetAwaiter().GetResult();
        CraftDriver.App.App.Host = host;
        host.GraphFactory = _ => new FakeGraph();
        host.Notify = (_, _, _) => { };
        return host;
    }

    static string LogText() => File.Exists(AppPaths.LogFile) ? File.ReadAllText(AppPaths.LogFile) : "";

    // ---------- 15.2 tratador global ----------

    /// <summary>Exceção solta num job da thread de UI (async void, comando async): vai para o log e para a tela; o processo segue.</summary>
    [AvaloniaFact]
    public void S15_2_Excecao_na_thread_de_UI_vai_para_o_log_e_nao_derruba()
    {
        var host = NewHost();
        var shown = new List<string>();
        CrashGuard.Install(() => host.Activity, shown.Add);
        try
        {
            Dispatcher.UIThread.Post(() => throw new InvalidOperationException("falha-de-teste-ui"));
            var escaped = Record.Exception(() => ShellUi.Pump(200));
            Assert.Null(escaped);
            Assert.Contains("falha-de-teste-ui", LogText());
            Assert.Contains(shown, s => s.Contains("falha-de-teste-ui"));
        }
        finally { CrashGuard.Uninstall(); }
    }

    /// <summary>Task com exceção que ninguém observou: registrada no log (antes: sumia sem rastro).</summary>
    [AvaloniaFact]
    public void S15_2_Excecao_de_task_nao_observada_vai_para_o_log()
    {
        var host = NewHost();
        CrashGuard.Install(() => host.Activity, null);
        try
        {
            static void Drop() => _ = Task.Run(() => throw new InvalidOperationException("falha-de-teste-task"));
            Drop();
            for (var i = 0; i < 20 && !LogText().Contains("falha-de-teste-task"); i++)
            {
                Thread.Sleep(50);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Assert.Contains("falha-de-teste-task", LogText());
        }
        finally { CrashGuard.Uninstall(); }
    }

    // ---------- 15.3 instância única ----------

    /// <summary>A trava é por arquivo (flock): a 2ª tentativa falha enquanto a 1ª vive — também para outro processo.</summary>
    [Fact]
    public void S15_3_Trava_de_instancia_unica_recusa_a_segunda()
    {
        ShellUi.NewHome();
        AppPaths.EnsureDirs();
        var first = SingleInstance.TryAcquire(AppPaths.LockFile);
        Assert.NotNull(first);
        try
        {
            Assert.Null(SingleInstance.TryAcquire(AppPaths.LockFile));
            if (DesktopIntegration.FindInPath("flock") != null)
            {
                using var p = Process.Start(new ProcessStartInfo("flock", ["-n", AppPaths.LockFile, "true"]))!;
                p.WaitForExit();
                Assert.NotEqual(0, p.ExitCode);
            }
        }
        finally { first!.Dispose(); }
        using var again = SingleInstance.TryAcquire(AppPaths.LockFile);
        Assert.NotNull(again);
    }

    [Fact]
    public void S15_3_Ipc_durante_o_inicio_responde_ping_show_e_ainda_iniciando()
    {
        var shows = 0;
        Assert.True(StartupIpc.Handle(new IpcRequest("ping", []), () => shows++).Ok);
        var show = StartupIpc.Handle(new IpcRequest("show", []), () => shows++);
        Assert.True(show.Ok);
        Assert.Equal(1, shows);
        var status = StartupIpc.Handle(new IpcRequest("status", []), () => shows++);
        Assert.False(status.Ok);
        Assert.Contains("iniciando", status.Text);
        // --headless: sem janela de espera para mostrar
        var headless = StartupIpc.Handle(new IpcRequest("show", []), null);
        Assert.False(headless.Ok);
        // quit desiste da inicialização (ExecStop do serviço durante a espera do chaveiro)
        var quits = 0;
        Assert.True(StartupIpc.Handle(new IpcRequest("quit", []), null, () => quits++).Ok);
        Assert.Equal(1, quits);
    }

    /// <summary>O AppHost assume o servidor aberto pelo Program: mesmo socket (mesmo inode), agora com as respostas dele.</summary>
    [AvaloniaFact]
    public void S15_3_AppHost_Start_reaproveita_o_servidor_aberto_no_inicio()
    {
        var host = NewHost();
        host.MountFunc = (_, _) => new TaskCompletionSource().Task;
        AppPaths.EnsureDirs();
        using var early = new IpcServer(AppPaths.SocketPath, r => Task.FromResult(StartupIpc.Handle(r, null)));
        var inode = Inode(AppPaths.SocketPath);
        try
        {
            host.Start(early);
            var resp = Task.Run(() => IpcClient.SendAsync(AppPaths.SocketPath, "status", [])).GetAwaiter().GetResult();
            Assert.NotNull(resp);
            Assert.True(resp!.Ok, resp.Text);
            Assert.Contains("Status:", resp.Text);
            Assert.Equal(inode, Inode(AppPaths.SocketPath));
        }
        finally { Task.Run(host.ShutdownAsync).GetAwaiter().GetResult(); }
    }

    // ---------- 15.3 / 15.4 / 15.2 com o executável de verdade (instância isolada, sem chaveiro real) ----------

    sealed class Child : IDisposable
    {
        public readonly Process P;
        public readonly StringBuilder Out = new();
        Child(Process p) => P = p;

        public static string Exe => Path.Combine(AppContext.BaseDirectory, "CraftDriver.App");

        public static ProcessStartInfo Psi(string home, params string[] args)
        {
            var psi = new ProcessStartInfo(Exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["CRAFTDRIVER_HOME"] = home;
            psi.Environment["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/nonexistent"; // nunca o chaveiro/notificações reais
            psi.Environment["CRAFTDRIVER_NO_NOTIFY"] = "1";
            psi.Environment.Remove("WAYLAND_DISPLAY");
            psi.Environment.Remove("DISPLAY");
            psi.Environment["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? Path.GetDirectoryName(Environment.ProcessPath)!;
            return psi;
        }

        public static Child Start(string home, params string[] args)
        {
            var c = new Child(Process.Start(Psi(home, args))!);
            c.P.OutputDataReceived += (_, e) => { if (e.Data != null) lock (c.Out) c.Out.AppendLine(e.Data); };
            c.P.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (c.Out) c.Out.AppendLine(e.Data); };
            c.P.BeginOutputReadLine();
            c.P.BeginErrorReadLine();
            return c;
        }

        public string Text { get { lock (Out) return Out.ToString(); } }

        public void WaitFor(string text, int ms = 20000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (!Text.Contains(text))
            {
                if (P.HasExited || DateTime.UtcNow > until) throw new TimeoutException($"esperando \"{text}\"; saída: {Text}");
                Thread.Sleep(50);
            }
        }

        public void Dispose()
        {
            if (!P.HasExited) { try { P.Kill(); } catch { } P.WaitForExit(5000); }
            P.Dispose();
        }
    }

    static string NewChildHome(string? tokenStorage = "file")
    {
        var home = Path.Combine(Path.GetTempPath(), "craftdriver-ui-home-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(home, "config"));
        File.WriteAllText(Path.Combine(home, "config", "config.json"), $$"""{ "MountPoint": "{{home}}/OneDrive", "CacheDir": "{{home}}/cache" }""");
        if (tokenStorage != null) File.WriteAllText(Path.Combine(home, "config", "token-storage"), tokenStorage);
        return home;
    }

    static bool MountedAt(string dir) => File.ReadAllLines("/proc/self/mounts").Any(l => l.Split(' ') is { Length: > 2 } p && p[1] == dir);

    static void CleanupMount(string dir)
    {
        if (!MountedAt(dir)) return;
        using var p = Process.Start(new ProcessStartInfo("fusermount3", ["-uz", dir]) { UseShellExecute = false });
        p?.WaitForExit(5000);
    }

    [Fact]
    public void S15_4_SIGTERM_encerra_limpo_desmonta_salva_e_apaga_o_socket()
    {
        var home = NewChildHome();
        var mount = Path.Combine(home, "OneDrive");
        using var c = Child.Start(home, "--headless");
        try
        {
            c.WaitFor("em execução");
            var cfg = Path.Combine(home, "config", "config.json");
            File.SetLastWriteTimeUtc(cfg, DateTime.UtcNow.AddDays(-1));
            var before = File.GetLastWriteTimeUtc(cfg);
            Assert.Equal(0, kill(c.P.Id, 15));
            Assert.True(c.P.WaitForExit(30000), "não saiu com SIGTERM: " + c.Text);
            Assert.Equal(0, c.P.ExitCode);
            Assert.False(File.Exists(Path.Combine(home, "run", "craftdriver.sock")), "socket ficou");
            Assert.False(MountedAt(mount), "montagem ficou");
            Assert.NotEqual(before, File.GetLastWriteTimeUtc(cfg)); // Config.Save do encerramento rodou
        }
        finally { CleanupMount(mount); }
    }

    /// <summary>
    /// 15.3: a 1ª instância espera o chaveiro (marca "keyring", barramento inexistente → até 60 s). A 2ª não
    /// pode apagar o socket da 1ª nem montar: sai dizendo que já está em execução, e a 1ª responde "ainda iniciando".
    /// </summary>
    [Fact]
    public void S15_3_Segunda_execucao_durante_a_espera_do_chaveiro_nao_rouba_o_socket()
    {
        var home = NewChildHome(tokenStorage: "keyring");
        File.WriteAllText(Path.Combine(home, "config", "config.json"),
            $$"""{ "MountPoint": "{{home}}/OneDrive", "CacheDir": "{{home}}/cache", "Accounts": [ { "Id": "a1", "Username": "u@exemplo.com" } ] }""");
        var mount = Path.Combine(home, "OneDrive");
        var sock = Path.Combine(home, "run", "craftdriver.sock");
        using var first = Child.Start(home, "--headless");
        try
        {
            var until = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(sock) && DateTime.UtcNow < until && !first.P.HasExited) Thread.Sleep(50);
            var inodeBefore = File.Exists(sock) ? Inode(sock) : null;
            using var second = Child.Start(home, "--headless");
            Assert.True(second.P.WaitForExit(20000), "2ª instância não saiu: " + second.Text);
            // §25.9: a 2ª --headless (unit do systemd) sai ≠ 0 — com 0 a unit ficava "inactive" calada e o "stop" nunca
            // chegava à instância viva; 3 = AlreadyRunningExitCode (RestartPreventExitStatus na unit, sem reinício em laço)
            Assert.Equal(3, second.P.ExitCode);
            Assert.Contains("já está em execução", second.Text);
            Assert.False(first.P.HasExited, "a 1ª morreu: " + first.Text);
            Assert.NotNull(inodeBefore);
            Assert.Equal(inodeBefore, Inode(sock));
            Assert.False(MountedAt(mount));
            using var cli = Process.Start(Child.Psi(home, "status"))!;
            var text = cli.StandardOutput.ReadToEnd() + cli.StandardError.ReadToEnd();
            cli.WaitForExit();
            Assert.Contains("iniciando", text);
        }
        finally
        {
            if (!first.P.HasExited) kill(first.P.Id, 15);
            first.P.WaitForExit(30000);
            CleanupMount(mount);
        }
    }

    static string? Inode(string path)
    {
        using var p = Process.Start(new ProcessStartInfo("stat", ["-c", "%i", path]) { RedirectStandardOutput = true })!;
        var s = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return s.Length > 0 ? s : null;
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    static extern int kill(int pid, int sig);

    // ---------- 15.6 montagem ocupada ----------

    static (AppHost host, MainViewModel vm) BusyMount()
    {
        var host = NewHost();
        host.IsMountedProbe = () => true; // fusermount -u com EBUSY: continua montado
        host.UnmountFunc = () => { };
        host.UnmountTimeout = TimeSpan.FromMilliseconds(200);
        var vm = new MainViewModel(host);
        vm.ConfirmRequested += _ => Task.FromResult(true);
        return (host, vm);
    }

    [AvaloniaFact]
    public void S15_6_Trocar_a_pasta_com_a_montagem_ocupada_mostra_erro_e_mantem_a_antiga()
    {
        var (host, vm) = BusyMount();
        var old = host.Config.MountPoint;
        var mounts = 0;
        host.MountFunc = (_, _) => { mounts++; return new TaskCompletionSource().Task; };
        vm.MountPoint = old + "-nova";
        ShellUi.Wait(vm.SaveSettingsCommand.ExecuteAsync(null));
        Assert.Equal(old, host.Config.MountPoint);
        Assert.Equal(old, AppConfig.Load().MountPoint);
        Assert.True(vm.MessageIsError, vm.Message);
        Assert.Contains("em uso", vm.Message);
        Assert.Equal(0, mounts);
        vm.Dispose();
    }

    [AvaloniaFact]
    public void S15_6_Remontar_com_a_montagem_ocupada_mostra_erro()
    {
        var (_, vm) = BusyMount();
        ShellUi.Wait(vm.RemountCommand.ExecuteAsync(null));
        Assert.True(vm.MessageIsError, vm.Message);
        Assert.Contains("em uso", vm.Message);
        vm.Dispose();
    }

    // ---------- 15.7 / 15.8 ----------

    [AvaloniaFact]
    public void S15_7_Salvar_configuracoes_somente_leitura_avisa_que_nao_gravou()
    {
        var host = NewHost("""{ "SchemaVersion": 999, "MountPoint": "{HOME}/OneDrive", "CacheDir": "{HOME}/cache" }""");
        var vm = new MainViewModel(host) { SetAutostart = (_, _) => null };
        vm.PollInterval = 77;
        ShellUi.Wait(vm.SaveSettingsCommand.ExecuteAsync(null));
        Assert.True(vm.MessageIsError, vm.Message);
        Assert.Contains("não foram gravadas", vm.Message);
        vm.Dispose();
    }

    [AvaloniaFact]
    public void S15_8_Aviso_do_inicio_automatico_nao_some_com_configuracoes_salvas()
    {
        var host = NewHost();
        var vm = new MainViewModel(host) { SetAutostart = (_, _) => "AVISO-DO-AUTOSTART" };
        ShellUi.Wait(vm.SaveSettingsCommand.ExecuteAsync(null));
        Assert.Contains("AVISO-DO-AUTOSTART", vm.Message);
        Assert.Contains("Configurações salvas", vm.Message);
        vm.Dispose();
    }

    // ---------- 15.9 ----------

    [AvaloniaFact]
    public void S15_9_Montagem_que_falha_depois_de_comecar_preenche_MountError()
    {
        var host = NewHost();
        host.IsMountedProbe = () => false;
        host.MountFunc = (_, _) => Task.Run(() => throw new IOException("loop-fuse-falhou"));
        host.Mount();
        ShellUi.PumpUntil(() => host.MountError != null, "MountError", 3000);
        Assert.Contains("loop-fuse-falhou", host.MountError);
    }

    // ---------- 15.10 / 15.11 ----------

    [Fact]
    public void S15_10_Relaunch_espera_o_processo_antigo_mesmo_que_demore()
    {
        var dir = Directory.CreateTempSubdirectory("cd-relaunch-").FullName;
        var marker = Path.Combine(dir, "novo");
        using var old = Process.Start(new ProcessStartInfo("sleep", ["4"]))!;
        // espera curta por volta (0,01 s): o limite fixo de 150 voltas acabava em ~2 s com o antigo vivo
        using var sh = Process.Start(AppRestart.BuildRelaunch(old.Id, "/bin/sh", ["-c", $"touch '{marker}'"], pollSeconds: 0.01))!;
        var sw = Stopwatch.StartNew();
        while (!old.HasExited)
        {
            Assert.False(File.Exists(marker), $"relançou com o antigo ainda vivo ({sw.ElapsedMilliseconds} ms)");
            Thread.Sleep(50);
        }
        Assert.True(sh.WaitForExit(10000));
        Assert.True(File.Exists(marker), "não relançou depois que o antigo saiu");
    }

    [Fact]
    public void S15_10_Relaunch_nao_espera_um_pid_reaproveitado_por_outro_processo()
    {
        var dir = Directory.CreateTempSubdirectory("cd-relaunch-").FullName;
        var marker = Path.Combine(dir, "novo");
        using var other = Process.Start(new ProcessStartInfo("sleep", ["10"]))!;
        try
        {
            // o pid existe, mas é de outro processo (hora de início diferente da registrada)
            using var sh = Process.Start(AppRestart.BuildRelaunch(other.Id, "/bin/sh", ["-c", $"touch '{marker}'"], startTime: "1", pollSeconds: 0.01))!;
            Assert.True(sh.WaitForExit(5000));
            Assert.True(File.Exists(marker));
        }
        finally { other.Kill(); }
    }

    [Fact]
    public void S15_11_Relaunch_nao_herda_a_escala_decidida_por_esta_execucao()
    {
        const string var = "AVALONIA_SCREEN_SCALE_FACTORS";
        var before = Environment.GetEnvironmentVariable(var);
        try
        {
            DisplayScaling.SetVariables(new ScalingDecision(new Dictionary<string, string> { [var] = "DP-1=1.5" }, null));
            var psi = AppRestart.BuildRelaunch(1, "/bin/true", []);
            Assert.False(psi.Environment.ContainsKey(var), "o relaunch herdaria " + var);
        }
        finally { Environment.SetEnvironmentVariable(var, before); }
    }

    // ---------- 15.12 ----------

    [AvaloniaFact]
    public void S15_12_Sessao_expirada_fecha_a_notificacao_ao_entrar_de_novo()
    {
        var host = NewHost();
        var tagged = new List<string>();
        var closed = new List<string>();
        host.NotifyTagged = (tag, _, _, _) => tagged.Add(tag);
        host.CloseNotification = closed.Add;
        host.ReauthRequested += _ => { };
        host.Config.Accounts.Add(new AccountConfig { Id = "a1", Username = "u@exemplo.com" });
        host.CheckSessions([("a1", true)]);
        var tag = Assert.Single(tagged);
        host.RegisterAccount(new AccountConfig { Id = "a1", Username = "u@exemplo.com" }, "d1", null);
        Assert.Contains(tag, closed);
        closed.Clear();
        host.CheckSessions([("a1", true)]);
        host.CheckSessions([("a1", false)]); // voltou a sincronizar (login por outro caminho)
        Assert.Contains(tag, closed);
    }

    /// <summary>
    /// 15.1 com o app iniciado minimizado: a janela ainda não existe (ninguém assina AddAccountRequested). O login
    /// precisa rodar depois do ShowWindow que a cria — antes, "Entrar novamente" só abria a janela.
    /// </summary>
    [AvaloniaFact]
    public void S15_1_Entrar_novamente_sem_janela_criada_abre_o_login()
    {
        var host = NewHost();
        NotificationAction[] actions = [];
        host.Notify = (_, _, a) => { if (a.Length > 0) actions = a; };
        host.Config.Accounts.Add(new AccountConfig { Id = "a1", Username = "u@empresa.exemplo", Type = "business" });
        var vm = new MainViewModel(host);
        CraftDriver.App.Views.MainWindow? w = null;
        // como App.ShowWindow: cria a janela na primeira vez
        host.ShowRequested += () => Dispatcher.UIThread.Post(() => { w ??= new CraftDriver.App.Views.MainWindow { DataContext = vm }; w.Show(); });
        try
        {
            host.CheckSessions([("a1", true)]);
            Task.Run(actions.Single(a => a.Id == "relogin").OnInvoke).Wait();
            ShellUi.PumpUntil(() => w?.OwnedWindows.OfType<CraftDriver.App.Views.LoginWindow>().Any() == true, "login aberto", 5000);
        }
        finally
        {
            if (w != null)
            {
                foreach (var o in w.OwnedWindows.ToList()) o.Close();
                w.Close();
            }
            vm.Dispose();
        }
    }

    /// <summary>
    /// 15.1 (defesa em todos os handlers, §18): qualquer diálogo pedido com a janela escondida na bandeja mostra a
    /// janela antes — ShowDialog com dona invisível lança num async void.
    /// </summary>
    [AvaloniaFact]
    public void S15_1_Dialogo_pedido_com_a_janela_escondida_mostra_a_janela_antes()
    {
        var host = NewHost();
        var vm = new MainViewModel(host);
        var w = new CraftDriver.App.Views.MainWindow { DataContext = vm };
        w.Show();
        ShellUi.Pump(100);
        w.Hide();
        var errors = new List<Exception>();
        void OnUnhandled(object? s, DispatcherUnhandledExceptionEventArgs e) { errors.Add(e.Exception); e.Handled = true; }
        Dispatcher.UIThread.UnhandledException += OnUnhandled;
        try
        {
            vm.RequestAddLibrary(null);
            ShellUi.Pump(300);
            Assert.True(errors.Count == 0, string.Join(" | ", errors.Select(e => e.Message)));
            Assert.True(w.IsVisible);
            Assert.Contains(w.OwnedWindows, x => x is CraftDriver.App.Views.AddLibraryWindow);
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= OnUnhandled;
            foreach (var o in w.OwnedWindows.ToList()) o.Close();
            w.Close();
            vm.Dispose();
        }
    }

    // ---------- 15.13 ----------

    [Fact]
    public async Task S15_13_wl_copy_que_sai_antes_de_ler_da_mensagem_de_instalacao_e_nao_lanca()
    {
        var bin = Directory.CreateTempSubdirectory("cd-clip-").FullName;
        var fake = Path.Combine(bin, "wl-copy");
        File.WriteAllText(fake, "#!/bin/sh\nexit 1\n");
        File.SetUnixFileMode(fake, (UnixFileMode)0b111_101_101);
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", bin + ":" + oldPath);
        try
        {
            var text = new string('x', 4 << 20); // maior que o buffer do pipe: a escrita dá EPIPE
            var err = await ClipboardService.CopyAsync(text, v => v == "WAYLAND_DISPLAY" ? "wayland-9" : null);
            Assert.NotNull(err);
            Assert.Contains("wl-clipboard", err);
        }
        finally { Environment.SetEnvironmentVariable("PATH", oldPath); }
    }

    [Fact]
    public async Task S15_13_Instancia_sem_sessao_grafica_devolve_os_links_para_o_CLI_copiar()
    {
        using var env = new ShareVersionsHostTests.Env();
        env.Remote.RemoteCreate("/", "a.txt", "conteúdo");
        env.Start();
        env.Host.ClipboardEnv = _ => null; // serviço do systemd sem WAYLAND_DISPLAY/DISPLAY
        var resp = await env.Ipc("share", CliShare.CliCopiesFlag, "--notify", env.Path("a.txt"));
        Assert.True(resp.Ok, resp.Text);
        Assert.Empty(env.Clipboard);
        Assert.Empty(env.Sent); // quem avisa é o CLI, depois de copiar
        var link = Assert.Single(resp.Links ?? []);
        var copied = new List<string>();
        var printed = await CliShare.FinishAsync(resp, t => { copied.Add(t); return Task.FromResult<string?>(null); });
        Assert.Equal([link], copied);
        Assert.Contains("copiado", printed);

        // com sessão gráfica a instância copia ela mesma (como antes)
        env.Host.ClipboardEnv = v => v == "WAYLAND_DISPLAY" ? "wayland-0" : null;
        var own = await env.Ipc("share", CliShare.CliCopiesFlag, env.Path("a.txt"));
        Assert.Null(own.Links);
        Assert.Single(env.Clipboard);
    }

    // ---------- 15.14 ----------

    [AvaloniaFact]
    public void S15_14_Liberar_espaco_de_tudo_inclui_as_pastas_compartilhadas()
    {
        using var env = new ShareVersionsHostTests.Env();
        var owner = env.Remote.AddOwnerDrive("dono-drive");
        owner.RemoteCreate("/", "Projeto", null, folder: true);
        owner.RemoteCreate("/Projeto", "a.txt", "conteúdo A");
        owner.Shares[owner.FindByPath("/Projeto")!.Id] = false;
        env.Remote.AddShortcut("/", "Projeto do Dono", owner, "/Projeto");
        // ShellUi.Wait (não Task.Wait): Wait pode executar a tarefa em linha na thread de UI, e o motor esperando
        // continuações nela trava o teste
        ShellUi.Wait(Task.Run(() =>
        {
            env.Start();
            env.Settle();
            ShareVersionsHostTests.Env.WaitUntil(() => env.Host.Engine.SharedDrives.Count > 0, "sub-drive");
            var sub = env.Host.Engine.SharedDrives[0];
            ShareVersionsHostTests.Env.WaitUntil(() => sub.Ready, "sub-drive pronta");
            sub.RunDeltaAsync(CancellationToken.None).GetAwaiter().GetResult();
        }), 60000);
        var sub = env.Host.Engine.SharedDrives[0];
        var rel = "/" + ShareVersionsHostTests.Env.Drive + "/Projeto do Dono/a.txt";
        ShellUi.Wait(Task.Run(() =>
        {
            Assert.Equal(0, env.Host.Fs.Open(rel, 0, 0, out var fh));
            var buf = new byte[64];
            env.Host.Fs.Read(fh, buf, 0);
            env.Host.Fs.Release(fh);
        }), 30000);
        Assert.Equal(1, env.Host.Db.Counts(sub.DriveId).cached);

        var vm = new MainViewModel(env.Host);
        vm.ConfirmRequested += _ => Task.FromResult(true);
        ShellUi.Wait(vm.FreeAllCommand.ExecuteAsync(null));
        Assert.Equal(0, env.Host.Db.Counts(sub.DriveId).cached);
        vm.Dispose();
    }

    // ---------- 15.15 ----------

    [Fact]
    public async Task S15_15_Ipc_recusa_linha_gigante_e_solta_cliente_parado()
    {
        var dir = Directory.CreateTempSubdirectory("cd-ipc-").FullName;
        var sock = Path.Combine(dir, "s.sock");
        var oldMax = IpcServer.MaxLineChars;
        var oldTimeout = IpcServer.ReadTimeout;
        IpcServer.MaxLineChars = 4096;
        IpcServer.ReadTimeout = TimeSpan.FromMilliseconds(300);
        var handled = 0;
        var ct = TestContext.Current.CancellationToken;
        try
        {
            using var server = new IpcServer(sock, r => { handled++; return Task.FromResult(new IpcResponse(true, "ok")); });

            // linha maior que o limite: recusada com mensagem própria (antes: lida inteira, sem limite de memória)
            using (var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            {
                await s.ConnectAsync(new UnixDomainSocketEndPoint(sock), ct);
                await s.SendAsync(Encoding.UTF8.GetBytes(new string('a', 10_000) + "\n"), SocketFlags.None, ct);
                var buf = new byte[4096];
                var n = await s.ReceiveAsync(buf, SocketFlags.None, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
                Assert.Contains("grande demais", Encoding.UTF8.GetString(buf, 0, n));
            }

            // cliente conectado que não manda nada: desconectado no prazo
            using (var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            {
                await s.ConnectAsync(new UnixDomainSocketEndPoint(sock), ct);
                var buf = new byte[256];
                var n = await s.ReceiveAsync(buf, SocketFlags.None, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
                Assert.Equal(0, n);
            }

            // e o servidor continua atendendo
            var ok = await IpcClient.SendAsync(sock, "ping", []);
            Assert.Equal("ok", ok?.Text);
            Assert.Equal(1, handled);
        }
        finally
        {
            IpcServer.MaxLineChars = oldMax;
            IpcServer.ReadTimeout = oldTimeout;
        }
    }

    [Fact]
    public void S15_15_Pasta_de_runtime_e_privada_e_de_outro_dono_e_recusada()
    {
        var dir = Path.Combine(Directory.CreateTempSubdirectory("cd-run-").FullName, "craftdriver-eu");
        AppPaths.EnsurePrivateDir(dir);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
        File.SetUnixFileMode(dir, (UnixFileMode)0b111_101_101); // 0755 de uma versão anterior
        AppPaths.EnsurePrivateDir(dir);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));

        var link = dir + "-link";
        Directory.CreateSymbolicLink(link, dir);
        Assert.Throws<IOException>(() => AppPaths.EnsurePrivateDir(link));

        if (Environment.UserName != "root")
        {
            var mode = File.GetUnixFileMode("/tmp");
            Assert.Throws<IOException>(() => AppPaths.EnsurePrivateDir("/tmp")); // de outro dono (root)
            Assert.Equal(mode, File.GetUnixFileMode("/tmp"));
        }
    }
}
