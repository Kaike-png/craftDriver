using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using CraftDriver.App.Services;
using CraftDriver.App.ViewModels;
using CraftDriver.App.Views;
using CraftDriver.Core.Config;
using CraftDriver.Core.Graph;
using CraftDriver.Core.Sync;
using CraftDriver.Tests;

namespace CraftDriver.UiTests;

/// <summary>
/// PENDENCIAS rodada 3, frente D (app/UI): §25.1, §25.2, §25.4, §25.6, §25.7, §25.9, §25.10, §22.1, §22.9.
/// Todos devem FALHAR hoje pelo motivo descrito no PENDENCIAS. Nenhum monta FUSE: processos reais ficam na espera do
/// chaveiro (barramento inexistente) ou nem pegam a trava.
/// </summary>
[Collection("host-isolado")]
public partial class Rodada3AppTests
{
    static readonly string Scratch = Environment.GetEnvironmentVariable("CD_R3_SCRATCH") ?? Path.Combine(Path.GetTempPath(), "cd-r3D-inst");

    static AppHost NewHost()
    {
        ShellUi.NewHome();
        var host = Task.Run(() => AppHost.CreateAsync()).GetAwaiter().GetResult();
        CraftDriver.App.App.Host = host;
        host.GraphFactory = _ => new FakeGraph();
        host.Notify = (_, _, _) => { };
        host.MountFunc = (_, _) => new TaskCompletionSource().Task; // nunca FUSE
        return host;
    }

    static string LogText() => File.Exists(AppPaths.LogFile) ? File.ReadAllText(AppPaths.LogFile) : "";

    // ---------- utilitários de processo ----------

    /// <summary>Filhos diretos deste processo que são o sh do AppRestart (script com "alive()").</summary>
    static List<(int Pid, string[] Argv)> RelaunchChildren()
    {
        var me = Environment.ProcessId.ToString();
        var list = new List<(int, string[])>();
        foreach (var d in Directory.GetDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(d), out var pid)) continue;
            try
            {
                var st = File.ReadAllText($"/proc/{pid}/stat");
                var rest = st[(st.LastIndexOf(')') + 2)..].Split(' ');
                if (rest[1] != me) continue;
                var argv = File.ReadAllText($"/proc/{pid}/cmdline").Split('\0', StringSplitOptions.None);
                if (argv.Any(a => a.Contains("alive()"))) list.Add((pid, argv));
            }
            catch { }
        }
        return list;
    }

    static void KillRelaunchChildren()
    {
        foreach (var (pid, _) in RelaunchChildren())
            try { using var p = Process.GetProcessById(pid); p.Kill(); p.WaitForExit(2000); } catch { }
    }

    // ---------- §25.1 ----------

    /// <summary>
    /// "Reiniciar agora" com transferência ativa: o diálogo "Sair do craftDriver?" aparece; Cancelar não pode deixar
    /// o sh do relaunch esperando este PID (horas depois, ao sair normalmente, o app reabriria sozinho).
    /// </summary>
    [AvaloniaFact]
    public void S25_1_Cancelar_a_saida_do_Reiniciar_agora_nao_deixa_relaunch_pendente()
    {
        var host = NewHost();
        var vm = new MainViewModel(host);
        var quits = 0;
        vm.QuitRequested += () => quits++;
        var t = host.Activity.StartTransfer("grande.iso", 1000, upload: true);
        var w = new MainWindow { DataContext = vm, Width = 920, Height = 640 };
        w.Show();
        ShellUi.Pump(200);
        try
        {
            vm.RestartCommand.Execute(null);
            ShellUi.PumpUntil(() => w.OwnedWindows.OfType<ConfirmWindow>().Any(), "diálogo de confirmação", 5000);
            var dlg = w.OwnedWindows.OfType<ConfirmWindow>().Single();
            dlg.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); // Cancelar (IsCancel)
            ShellUi.Pump(300);
            Assert.Equal(0, quits); // cancelou de fato
            var orphans = RelaunchChildren();
            Assert.True(orphans.Count == 0,
                $"saída cancelada, mas ficou {orphans.Count} sh de relaunch esperando o PID {Environment.ProcessId}: " +
                string.Join(" | ", orphans.Select(o => o.Pid + ": " + string.Join(' ', o.Argv.Skip(5)))));
        }
        finally
        {
            KillRelaunchChildren();
            host.Activity.EndTransfer(t);
            w.Close();
            vm.Dispose();
        }
    }

    // ---------- §25.6 ----------

    /// <summary>
    /// Rodando como "dotnet X.dll" o relaunch precisa repetir a DLL. O testhost daqui é apphost; então o teste roda o
    /// <see cref="S25_6_Relaunch_sob_dotnet_repete_a_dll_filho"/> em "dotnet exec CraftDriver.UiTests.dll".
    /// </summary>
    [Fact]
    public async Task S25_6_Relaunch_sob_dotnet_repete_a_dll()
    {
        var ct = TestContext.Current.CancellationToken;
        var dotnet = Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? Path.Combine(Environment.GetEnvironmentVariable("HOME")!, ".dotnet"), "dotnet");
        var psi = new ProcessStartInfo(dotnet, ["exec", Path.Combine(AppContext.BaseDirectory, "CraftDriver.UiTests.dll"),
            "-method", typeof(Rodada3AppTests).FullName + "." + nameof(S25_6_Relaunch_sob_dotnet_repete_a_dll_filho), "-explicit", "only"])
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        psi.Environment["CRAFTDRIVER_HOME"] = Path.Combine(Scratch, "filho-s25-6");
        using var p = Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync(ct);
        var outText = await p.StandardOutput.ReadToEndAsync(ct);
        Assert.True(p.WaitForExit(60000));
        var all = outText + await errTask;
        Assert.True(p.ExitCode == 0, "no filho \"dotnet exec\": " + string.Join("\n", all.Split('\n').Where(l => l.Contains("relaunch") || l.Contains("FAIL") || l.Contains("Total"))));
    }

    [Fact(Explicit = true)]
    public void S25_6_Relaunch_sob_dotnet_repete_a_dll_filho()
    {
        var exe = Environment.ProcessPath!;
        Assert.Equal("dotnet", Path.GetFileNameWithoutExtension(exe));
        var dll = Environment.GetCommandLineArgs()[0];
        try
        {
            AppRestart.ScheduleRelaunch();
            var until = DateTime.UtcNow.AddSeconds(5);
            while (RelaunchChildren().Count == 0 && DateTime.UtcNow < until) Thread.Sleep(20);
            var (_, argv) = Assert.Single(RelaunchChildren());
            // argv: /bin/sh -c SCRIPT PID STARTTIME EXE ARGS...
            var cmd = argv.Skip(5).Where(a => a.Length > 0).ToArray();
            Assert.Equal(exe, cmd[0]);
            Assert.True(cmd.Length > 1 && cmd[1] == dll,
                $"o relaunch roda \"{string.Join(' ', cmd.Take(4))} …\" sem a DLL {dll}");
        }
        finally { KillRelaunchChildren(); }
    }

    // ---------- §25.2 ----------

    /// <summary>Namespace de usuário com tmpfs sem inodes livres (ENOSPC) e bind somente leitura (EROFS).</summary>
    sealed class BrokenFs : IDisposable
    {
        readonly Process _p;
        public readonly string Dir;
        public string Full => $"/proc/{_p.Id}/root{Dir}/full";
        public string Ro => $"/proc/{_p.Id}/root{Dir}/ro";
        public string HomeFull => $"/proc/{_p.Id}/root{Dir}/home";

        public BrokenFs()
        {
            Dir = Path.Combine(Scratch, "brokenfs-" + Guid.NewGuid().ToString("N")[..6]);
            foreach (var s in new[] { "full", "ro", "home/run", "home/config" }) Directory.CreateDirectory(Path.Combine(Dir, s));
            File.WriteAllText(Path.Combine(Dir, "home", "config", "config.json"),
                $$"""{ "MountPoint": "{{Dir}}/home/OneDrive", "CacheDir": "{{Dir}}/home/cache" }""");
            File.WriteAllText(Path.Combine(Dir, "home", "config", "token-storage"), "file");
            var psi = new ProcessStartInfo("unshare", ["-rm", "sh", "-c",
                "set -e; d=\"$1\"; " +
                "mount -t tmpfs -o size=64k,nr_inodes=2 tmpfs \"$d/full\"; " +
                "i=0; while touch \"$d/full/f$i\" 2>/dev/null; do i=$((i+1)); done; " +
                "mount -t tmpfs -o size=64k,nr_inodes=2 tmpfs \"$d/home/run\"; chmod 700 \"$d/home/run\"; " +
                "j=0; while touch \"$d/home/run/f$j\" 2>/dev/null; do j=$((j+1)); done; " +
                "mount --bind \"$d/ro\" \"$d/ro\"; mount -o remount,bind,ro \"$d/ro\"; " +
                "echo pronto; exec sleep 600", "sh", Dir])
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            _p = Process.Start(psi)!;
            var line = _p.StandardOutput.ReadLine();
            if (line != "pronto") throw new Exception("unshare falhou: " + line + _p.StandardError.ReadToEnd());
        }

        public void Dispose()
        {
            try { _p.Kill(); _p.WaitForExit(2000); } catch { }
            _p.Dispose();
        }
    }

    static string? NullMeansOtherInstance(string lockPath, string what)
    {
        SingleInstance? got = null;
        var ex = Record.Exception(() => got = SingleInstance.TryAcquire(lockPath));
        got?.Dispose();
        return ex == null && got == null ? $"{what} ao criar a trava virou null (= \"outra instância já está em execução\")" :
            ex != null ? null : null;
    }

    [Fact]
    public void S25_2_ENOSPC_e_EROFS_na_trava_nao_viram_outra_instancia()
    {
        using var fs = new BrokenFs();
        // sanidade: o erro é mesmo o do sistema de arquivos
        var e1 = Assert.ThrowsAny<IOException>(() => File.Create(Path.Combine(fs.Full, "sonda")).Dispose());
        var e2 = Assert.ThrowsAny<IOException>(() => File.Create(Path.Combine(fs.Ro, "sonda")).Dispose());
        Assert.Equal(28, e1.HResult & 0xFFFF); // ENOSPC
        Assert.Equal(30, e2.HResult & 0xFFFF); // EROFS
        // caracterização da correção sugerida: a trava presa de verdade dá IOException com HResult EWOULDBLOCK (11)
        ShellUi.NewHome();
        AppPaths.EnsureDirs();
        using (var first = SingleInstance.TryAcquire(AppPaths.LockFile))
        {
            Assert.NotNull(first);
            var held = Assert.ThrowsAny<IOException>(() => new FileStream(AppPaths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None).Dispose());
            Assert.Equal(11, held.HResult & 0xFFFF);
        }
        var bad = new[]
        {
            NullMeansOtherInstance(Path.Combine(fs.Full, "craftdriver.lock"), "ENOSPC"),
            NullMeansOtherInstance(Path.Combine(fs.Ro, "craftdriver.lock"), "EROFS"),
        }.Where(x => x != null).ToList();
        Assert.True(bad.Count == 0, string.Join("; ", bad));
    }

    /// <summary>Diretório sem permissão de escrita (EACCES): UnauthorizedAccessException NÃO é IOException e escapa do catch.</summary>
    [Fact]
    public void S25_2_EACCES_na_trava_nao_derruba_o_processo_com_excecao_crua()
    {
        Assert.SkipWhen(Environment.UserName == "root", "root ignora permissões");
        var dir = Path.Combine(Scratch, "semescrita-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var ex = Record.Exception(() => SingleInstance.TryAcquire(Path.Combine(dir, "craftdriver.lock"))?.Dispose());
            Assert.True(ex is null or IOException, $"EACCES escapou como {ex?.GetType().Name}: {ex?.Message} — o Main não trata e o processo cai com exceção não tratada");
        }
        finally { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    /// <summary>Processo real: run/ sem espaço → hoje diz "já está em execução" e sai 0, sem instância nenhuma.</summary>
    [Fact]
    public void S25_2_App_real_com_run_cheio_nao_diz_ja_em_execucao()
    {
        using var fs = new BrokenFs();
        using var c = Child.Start(fs.HomeFull, "--headless");
        Assert.True(c.P.WaitForExit(30000), "não saiu: " + c.Text);
        Assert.DoesNotContain("já está em execução", c.Text);
        Assert.NotEqual(0, c.P.ExitCode);
    }

    // ---------- §25.4 ----------

    /// <summary>Lifetime falso (a interface do Avalonia não é implementável em C#: DispatchProxy). Conta Shutdown.</summary>
    public class FakeLifetime : DispatchProxy
    {
        public int Shutdowns;
        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            switch (m!.Name)
            {
                case "Shutdown": Shutdowns++; return null;
                case "TryShutdown": Shutdowns++; return true;
                case "get_Windows": return new List<Window>();
                case "get_ShutdownMode": return ShutdownMode.OnExplicitShutdown;
                default: return m.ReturnType.IsValueType && m.ReturnType != typeof(void) ? Activator.CreateInstance(m.ReturnType) : null;
            }
        }
    }

    static readonly BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;

    static (CraftDriver.App.App app, FakeLifetime life) NewApp()
    {
        var app = new CraftDriver.App.App();
        var proxy = DispatchProxy.Create<IClassicDesktopStyleApplicationLifetime, FakeLifetime>();
        typeof(CraftDriver.App.App).GetField("_desktop", Priv)!.SetValue(app, proxy);
        return (app, (FakeLifetime)(object)proxy);
    }

    /// <summary>
    /// Exceção depois da fábrica do AppHost (aqui: a Graph da drive não pode ser criada → Engine.Start → Host.Start)
    /// fica presa na Task descartada de StartAsync: sem log, sem janela, sem Shutdown — processo vivo à toa.
    /// </summary>
    [AvaloniaFact]
    public void S25_4_Excecao_depois_da_fabrica_nao_deixa_processo_vivo_sem_janela()
    {
        var host = NewHost();
        host.Config.Accounts.Add(new AccountConfig { Id = "a1", Username = "u@exemplo.com" });
        host.Config.Drives.Add(new DriveConfig { DriveId = FakeGraph.DriveId, AccountId = "a1", DisplayName = "OneDrive" });
        host.GraphFactory = _ => throw new InvalidOperationException("falha-r3-depois-da-fabrica");
        var oldFactory = CraftDriver.App.App.HostFactory;
        CraftDriver.App.App.HostFactory = (_, _, _) => Task.FromResult(host);
        var (app, life) = NewApp();
        try
        {
            var start = typeof(CraftDriver.App.App).GetMethod("StartAsync", Priv)!;
            var task = (Task)start.Invoke(app, [(IClassicDesktopStyleApplicationLifetime)(object)life, host.Config])!; // a produção descarta com "_ ="
            ShellUi.PumpUntil(() => task.IsCompleted, "StartAsync", 10000);
            ShellUi.Pump(500);
            // a correção trata a exceção dentro do StartAsync (a Task termina sem falha); o gatilho é provado pelo log
            var reported = LogText().Contains("falha-r3-depois-da-fabrica");
            Assert.True(life.Shutdowns > 0 && reported,
                $"exceção presa na Task descartada: Shutdown chamado {life.Shutdowns}×, registrada no log: {reported} " +
                $"({task.Exception?.GetBaseException().GetType().Name}: {task.Exception?.GetBaseException().Message})");
        }
        finally
        {
            CraftDriver.App.App.HostFactory = oldFactory;
            ShellUi.Wait(Task.Run(host.ShutdownAsync), 20000);
        }
    }

    // ---------- §25.7 ----------

    /// <summary>Exceção determinística repetida (layout/binding) não pode inundar Atividade e log.</summary>
    [AvaloniaFact]
    public void S25_7_CrashGuard_agrupa_repeticoes()
    {
        var host = NewHost();
        var shown = 0;
        CrashGuard.Install(() => host.Activity, _ => shown++);
        try
        {
            for (var i = 0; i < 300; i++) Dispatcher.UIThread.Post(() => throw new InvalidOperationException("falha-r3-repetida"));
            ShellUi.Pump(500);
            var inActivity = host.Activity.Entries.Count(e => e.Text.Contains("falha-r3-repetida"));
            var inLog = LogText().Split('\n').Count(l => l.Contains("falha-r3-repetida"));
            Assert.True(inActivity <= 10 && inLog <= 30,
                $"300 exceções iguais em 0,5 s: {inActivity} entradas na Atividade, {inLog} linhas no log, {shown} mensagens na tela");
        }
        finally { CrashGuard.Uninstall(); }
    }

    // ---------- §25.10 ----------

    static void RaiseClick(NativeMenuItem item)
    {
        var m = typeof(NativeMenuItem).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .First(x => x.Name.Contains("RaiseClick") && x.GetParameters().Length == 0);
        m.Invoke(item, null);
    }

    /// <summary>"Sair" da bandeja com transferência ativa: precisa da mesma confirmação do Ctrl+Q.</summary>
    [AvaloniaFact]
    public void S25_10_Sair_da_bandeja_com_transferencia_pede_confirmacao()
    {
        var host = NewHost();
        CraftDriver.App.App.Host = host;
        var (app, life) = NewApp();
        typeof(CraftDriver.App.App).GetMethod("InitWithHost", Priv)!.Invoke(app, [false]);
        var vm = (MainViewModel)typeof(CraftDriver.App.App).GetField("_vm", Priv)!.GetValue(app)!;
        ConfirmRequest? asked = null;
        vm.ConfirmRequested += r => { asked = r; return Task.FromResult(false); };
        var t = host.Activity.StartTransfer("grande.iso", 1000, upload: true);
        try
        {
            var tray = (TrayIcon)typeof(CraftDriver.App.App).GetField("_tray", Priv)!.GetValue(app)!;
            var sair = tray.Menu!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Sair");
            RaiseClick(sair);
            ShellUi.Pump(1500);
            Assert.True(asked != null && life.Shutdowns == 0,
                $"\"Sair\" da bandeja com 1 transferência: confirmação pedida = {asked != null}, Shutdown chamado {life.Shutdowns}×");
        }
        finally
        {
            host.Activity.EndTransfer(t);
            ShellUi.Wait(Task.Run(host.ShutdownAsync), 20000);
        }
    }

    /// <summary>Ctrl+S é o "Salvar" da aba Configurações; na aba Status não pode gravar nada.</summary>
    [AvaloniaFact]
    public void S25_10_CtrlS_fora_da_aba_Configuracoes_nao_salva()
    {
        var host = NewHost();
        var vm = new MainViewModel(host);
        var w = new MainWindow { DataContext = vm, Width = 920, Height = 640 };
        w.Show();
        vm.SelectedTab = 0;
        ShellUi.Pump(300);
        var cfg = Path.Combine(AppPaths.ConfigDir, "config.json");
        File.SetLastWriteTimeUtc(cfg, DateTime.UtcNow.AddDays(-1));
        var before = File.GetLastWriteTimeUtc(cfg);
        vm.Message = "";
        try
        {
            w.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            ShellUi.Pump(500);
            Assert.True(before == File.GetLastWriteTimeUtc(cfg) && vm.Message == "",
                $"Ctrl+S na aba {vm.SelectedTab} (Status) salvou: config.json regravado = {before != File.GetLastWriteTimeUtc(cfg)}, mensagem = \"{vm.Message}\"");
        }
        finally { w.Close(); vm.Dispose(); }
    }

    // ---------- §22.1 ----------

    /// <summary>
    /// Motor real + FakeGraph: a.txt em backoff (503) e b.txt subindo ao mesmo tempo. O painel diz "Envio parado …
    /// Esperando atrás dela: 1" — mas a fila não está parada (ClaimOps pegou b.txt).
    /// </summary>
    [AvaloniaFact]
    public void S22_1_Upload_em_backoff_com_outro_subindo_nao_e_fila_parada()
    {
        var host = NewHost();
        var remote = new FakeGraph();
        host.GraphFactory = _ => remote;
        host.Config.Accounts.Add(new AccountConfig { Id = "a1", Username = "u@exemplo.com", Type = "personal" });
        host.Config.Drives.Add(new DriveConfig { DriveId = FakeGraph.DriveId, AccountId = "a1", DisplayName = "OneDrive - Pessoal", Kind = "personal" });
        var bStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.BeforeUpload = async (name, ct) =>
        {
            if (name == "a.txt") throw new GraphException(HttpStatusCode.ServiceUnavailable, null, "HTTP 503 Service Unavailable");
            if (name == "b.txt") { bStarted.TrySetResult(); await releaseB.Task.WaitAsync(ct); }
        };
        ShellUi.Wait(Task.Run(host.Engine.Start));
        MainViewModel? vm = null;
        try
        {
            ShellUi.PumpUntil(() => host.Engine.GetDrive(FakeGraph.DriveId)?.LastSync != null, "carga inicial", 15000);
            var dir = "/OneDrive - Pessoal/";
            Assert.Equal(0, host.Fs.Create(dir + "a.txt", 2, out var fa));
            host.Fs.Write(fa, "aaa"u8, 0);
            host.Fs.Release(fa);
            ShellUi.PumpUntil(() => host.Db.QueueHeads().Any(h => h.Attempts > 0), "a.txt em backoff", 15000);
            Assert.Equal(0, host.Fs.Create(dir + "b.txt", 2, out var fb));
            host.Fs.Write(fb, "bbb"u8, 0);
            host.Fs.Release(fb);
            ShellUi.PumpUntil(() => bStarted.Task.IsCompleted, "b.txt subindo", 15000);

            vm = new MainViewModel(host);
            vm.RefreshQueue();
            var lines = string.Join(" / ", vm.QueueBlocked);
            Assert.True(host.Activity.Transfers.Count > 0, "b.txt deveria estar em transferência");
            Assert.False(lines.Contains("parada") || lines.Contains("atrás dela"),
                $"b.txt está subindo agora ({host.Activity.Transfers.Count} transferência), mas o painel diz: {lines}");
        }
        finally
        {
            releaseB.TrySetResult();
            vm?.Dispose();
            ShellUi.Wait(Task.Run(host.ShutdownAsync), 30000);
        }
    }

    // ---------- processos reais: §25.9 e §22.9 ----------

    sealed class Child : IDisposable
    {
        public readonly Process P;
        readonly StringBuilder _out = new();
        Child(Process p) => P = p;
        public static string Exe => Path.Combine(AppContext.BaseDirectory, "CraftDriver.App");

        public static Child Start(string home, params string[] args)
        {
            var psi = new ProcessStartInfo(Exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["CRAFTDRIVER_HOME"] = home;
            psi.Environment["XDG_RUNTIME_DIR"] = Path.Combine(Scratch, "run");
            psi.Environment["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/nonexistent";
            psi.Environment["CRAFTDRIVER_NO_NOTIFY"] = "1";
            psi.Environment.Remove("WAYLAND_DISPLAY");
            psi.Environment.Remove("DISPLAY");
            psi.Environment["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? Path.GetDirectoryName(Environment.ProcessPath)!;
            Directory.CreateDirectory(Path.Combine(Scratch, "run"));
            var c = new Child(Process.Start(psi)!);
            c.P.OutputDataReceived += (_, e) => { if (e.Data != null) lock (c._out) c._out.AppendLine(e.Data); };
            c.P.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (c._out) c._out.AppendLine(e.Data); };
            c.P.BeginOutputReadLine();
            c.P.BeginErrorReadLine();
            return c;
        }

        public string Text { get { lock (_out) return _out.ToString(); } }

        public void Dispose()
        {
            if (!P.HasExited) { try { P.Kill(); } catch { } P.WaitForExit(5000); }
            P.Dispose();
        }
    }

    /// <summary>Home isolada esperando o chaveiro (60 s, barramento inexistente): nunca chega a montar.</summary>
    static string KeyringHome(string mount, string cache)
    {
        var home = Path.Combine(Scratch, "home-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(home, "config"));
        File.WriteAllText(Path.Combine(home, "config", "config.json"),
            $$"""{ "MountPoint": "{{mount}}", "CacheDir": "{{cache}}", "Accounts": [ { "Id": "a1", "Username": "u@exemplo.com" } ] }""");
        File.WriteAllText(Path.Combine(home, "config", "token-storage"), "keyring");
        return home;
    }

    static void WaitFile(string path, Child c, int ms = 15000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!File.Exists(path) && DateTime.UtcNow < until && !c.P.HasExited) Thread.Sleep(50);
        Assert.True(File.Exists(path), $"{path} não apareceu: {c.Text}");
    }

    /// <summary>Dois CRAFTDRIVER_HOME com o MESMO MountPoint/CacheDir: a 2ª tinha de recusar; hoje as duas seguem.</summary>
    [Fact]
    public void S25_9_Duas_instancias_com_o_mesmo_ponto_de_montagem_sao_recusadas()
    {
        var shared = Path.Combine(Scratch, "comum-" + Guid.NewGuid().ToString("N")[..6]);
        var h1 = KeyringHome(shared + "/OneDrive", shared + "/cache");
        var h2 = KeyringHome(shared + "/OneDrive", shared + "/cache");
        using var first = Child.Start(h1, "--headless");
        WaitFile(Path.Combine(h1, "run", "craftdriver.sock"), first);
        using var second = Child.Start(h2, "--headless");
        // a recusa vem antes de abrir o socket (a 2ª nunca chega a responder IPC): espera a saída, não o socket
        Assert.True(second.P.WaitForExit(15000),
            $"duas instâncias vivas para o mesmo {shared}/OneDrive (travas em {h1}/run e {h2}/run): {second.Text}");
        Assert.False(first.P.HasExited, "a 1ª morreu: " + first.Text);
        Assert.Equal(3, second.P.ExitCode); // Program.AlreadyRunningExitCode: a unit do systemd não reinicia em laço
        Assert.Contains("já usa a pasta", second.Text);
        Assert.False(File.Exists(Path.Combine(h2, "run", "craftdriver.sock")));
    }

    /// <summary>
    /// Mesmo ponto alcançado por um link simbólico (outra configuração aponta para o link): o caminho canônico é o mesmo,
    /// a 2ª também recusa.
    /// </summary>
    [Fact]
    public void S25_9_Mesmo_ponto_por_link_simbolico_tambem_e_recusado()
    {
        var shared = Path.Combine(Scratch, "comum-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(shared, "real", "OneDrive"));
        File.CreateSymbolicLink(Path.Combine(shared, "link"), Path.Combine(shared, "real"));
        var h1 = KeyringHome(shared + "/real/OneDrive", shared + "/cache1");
        var h2 = KeyringHome(shared + "/link/OneDrive", shared + "/cache2");
        using var first = Child.Start(h1, "--headless");
        WaitFile(Path.Combine(h1, "run", "craftdriver.sock"), first);
        using var second = Child.Start(h2, "--headless");
        Assert.True(second.P.WaitForExit(15000), "a 2ª (pelo link) seguiu: " + second.Text);
        Assert.Equal(3, second.P.ExitCode);
        Assert.Contains(shared + "/real/OneDrive", second.Text); // a mensagem mostra o caminho real
    }

    /// <summary>2ª --headless (unit do systemd) com outra instância viva: sai 0 calada → systemd "inactive", sem erro.</summary>
    [Fact]
    public void S25_9_Segunda_headless_nao_sai_com_sucesso()
    {
        var shared = Path.Combine(Scratch, "comum-" + Guid.NewGuid().ToString("N")[..6]);
        var h = KeyringHome(shared + "/OneDrive", shared + "/cache");
        using var first = Child.Start(h, "--headless");
        WaitFile(Path.Combine(h, "run", "craftdriver.sock"), first);
        using var second = Child.Start(h, "--headless");
        Assert.True(second.P.WaitForExit(30000), "2ª não saiu: " + second.Text);
        Assert.True(second.P.ExitCode != 0,
            $"2ª --headless saiu {second.P.ExitCode} (\"{second.Text.Trim()}\"): o systemd marca o serviço como concluído e `stop` nunca alcança a instância viva");
    }

    /// <summary>SIGTERM durante a espera do chaveiro: hoje morte sem finally — o socket fica.</summary>
    [Fact]
    public void S22_9_SIGTERM_na_espera_do_chaveiro_apaga_o_socket()
    {
        var shared = Path.Combine(Scratch, "comum-" + Guid.NewGuid().ToString("N")[..6]);
        var h = KeyringHome(shared + "/OneDrive", shared + "/cache");
        var sock = Path.Combine(h, "run", "craftdriver.sock");
        using var c = Child.Start(h, "--headless");
        WaitFile(sock, c);
        Thread.Sleep(500);
        Assert.Equal(0, kill(c.P.Id, 15));
        Assert.True(c.P.WaitForExit(15000), "não saiu com SIGTERM: " + c.Text);
        Assert.True(!File.Exists(sock) && c.P.ExitCode == 0,
            $"SIGTERM na espera do chaveiro: saída {c.P.ExitCode}, socket ficou = {File.Exists(sock)}");
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    static extern int kill(int pid, int sig);
}
