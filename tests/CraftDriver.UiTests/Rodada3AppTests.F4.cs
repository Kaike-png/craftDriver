using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using CraftDriver.App.Services;
using CraftDriver.App.ViewModels;
using CraftDriver.Core.Config;
using CraftDriver.Core.Ipc;
using CraftDriver.Tests;
using Microsoft.Data.Sqlite;
using Tmds.DBus.Protocol;

namespace CraftDriver.UiTests;

/// <summary>
/// Rodada 4, frente F4 (app/UI): testes novos para §25.3 (quit espera), §25.5 (2º sinal), §25.8 (link simbólico),
/// §25.11 (notificação sem botões / após reconexão), §25.12 (WantedBy da unit) e §22.1 (painel com op que bloqueia).
/// </summary>
public partial class Rodada3AppTests
{
    // ---------- §25.5 ----------

    [Fact]
    public void S25_5_Segundo_sinal_durante_o_encerramento_sai_na_hora()
    {
        var quits = 0;
        var forced = new List<int>();
        var cleaned = 0;
        Action? current = null;
        var gate = new ShutdownSignalGate(() => current, forced.Add, () => cleaned++);

        // sem encerramento definido: o padrão do sistema (terminar) vale
        var ctx0 = new PosixSignalContext(PosixSignal.SIGTERM);
        gate.Handle(ctx0);
        Assert.False(ctx0.Cancel);

        current = () => quits++;
        var ctx1 = new PosixSignalContext(PosixSignal.SIGTERM);
        gate.Handle(ctx1);
        Assert.True(ctx1.Cancel);
        Assert.Equal(1, quits);
        Assert.Empty(forced); // o 1º é o encerramento normal

        var ctx2 = new PosixSignalContext(PosixSignal.SIGINT);
        gate.Handle(ctx2);
        Assert.Equal(1, quits); // não chama o encerramento de novo
        Assert.Equal([130], forced); // 128 + SIGINT
        Assert.Equal(1, cleaned); // apaga o socket antes de sair
    }

    // ---------- §25.12 ----------

    static T WithConfigDir<T>(Func<string, T> body)
    {
        var dir = Path.Combine(Scratch, "cfg-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try { return body(dir); }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void S25_12_Com_graphical_session_ativo_a_unit_e_puxada_por_ele()
    {
        WithConfigDir(cfg =>
        {
            var calls = new List<string>();
            var graphical = true;
            int Fake(string[] a) { calls.Add(string.Join(' ', a)); return a[0] == "is-active" ? (graphical ? 0 : 3) : 0; }
            var unitPath = Path.Combine(cfg, "systemd", "user", "craftdriver.service");

            Assert.Null(DesktopIntegration.SetAutostart(true, AutostartMode.Systemd, cfg, Fake));
            var unit = File.ReadAllText(unitPath);
            Assert.Contains("\nWantedBy=graphical-session.target\n", unit);
            Assert.DoesNotContain("default.target", unit);
            Assert.Contains("\nAfter=graphical-session.target\n", unit);
            Assert.Contains("\nPartOf=graphical-session.target\n", unit);
            Assert.Equal(["is-active --quiet graphical-session.target", "daemon-reload", "enable craftdriver.service"], calls);

            // salvar de novo sem mudar o alvo: nada de disable
            calls.Clear();
            DesktopIntegration.SetAutostart(true, AutostartMode.Systemd, cfg, Fake);
            Assert.DoesNotContain("disable craftdriver.service", calls);

            // a sessão mudou (ex.: entrou no Hyprland sem uwsm): o disable lê o [Install] antigo e tira o link dele
            graphical = false;
            calls.Clear();
            DesktopIntegration.SetAutostart(true, AutostartMode.Systemd, cfg, Fake);
            Assert.Equal(["is-active --quiet graphical-session.target", "disable craftdriver.service", "daemon-reload", "enable craftdriver.service"], calls);
            Assert.Contains("\nWantedBy=default.target\n", File.ReadAllText(unitPath));
            return 0;
        });
    }

    [Fact]
    public void S25_12_Unit_da_sessao_grafica_valida_para_o_systemd_analyze()
    {
        var analyze = DesktopIntegration.FindInPath("systemd-analyze");
        Assert.SkipWhen(analyze == null, "systemd-analyze ausente");
        WithConfigDir(cfg =>
        {
            var unit = Path.Combine(cfg, "craftdriver.service");
            File.WriteAllText(unit, DesktopIntegration.SystemdUnitText(graphicalSession: true));
            var psi = new ProcessStartInfo(analyze!) { RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "--user", "verify", "--man=no", unit }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            Assert.True(p.WaitForExit(30_000));
            Assert.True(p.ExitCode == 0, err);
            return 0;
        });
    }

    // ---------- §25.8 ----------

    [Fact]
    public void S25_8_Caminho_canonico_resolve_links_e_aceita_o_que_nao_existe()
    {
        var root = Path.Combine(Scratch, "links-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(root, "data", "od"));
        File.CreateSymbolicLink(Path.Combine(root, "OneDrive"), Path.Combine(root, "data", "od"));
        File.CreateSymbolicLink(Path.Combine(root, "rel"), "data"); // alvo relativo
        var real = Path.Combine(root, "data", "od");
        Assert.Equal(real, MountPath.Canonical(Path.Combine(root, "OneDrive")));
        Assert.Equal(real + "/a/b.txt", MountPath.Canonical(Path.Combine(root, "OneDrive", "a", "b.txt"))); // não existe
        Assert.Equal(real, MountPath.Canonical(Path.Combine(root, "rel", "od")));
        // configuração no link, caminho real (o do /proc/mounts e do Dolphin); e o contrário
        Assert.Equal("/x/y.txt", MountPath.RelativeToMount(real + "/x/y.txt", Path.Combine(root, "OneDrive")));
        Assert.Equal("/x", MountPath.RelativeToMount(Path.Combine(root, "OneDrive", "x"), real));
        Assert.Equal("/", MountPath.RelativeToMount(Path.Combine(root, "OneDrive"), real));
        Assert.Null(MountPath.RelativeToMount(Path.Combine(root, "data"), Path.Combine(root, "OneDrive")));
        Assert.Null(MountPath.RelativeToMount(Path.Combine(root, "data", "od2"), Path.Combine(root, "OneDrive"))); // prefixo não basta
        // não desce na montagem: com stopAt, nada depois dela é consultado (um link lá dentro fica como está)
        File.CreateSymbolicLink(Path.Combine(real, "dentro"), "/etc");
        Assert.Equal(real + "/dentro", MountPath.Canonical(real + "/dentro", real));
        // aviso (não bloqueia) ao escolher a pasta pelo link
        Assert.Contains(real, MountPath.SymlinkWarning(Path.Combine(root, "OneDrive")));
        Assert.Null(MountPath.SymlinkWarning(real));
    }

    [AvaloniaFact]
    public void S25_8_CLI_com_a_pasta_sob_link_nao_diz_fora_da_pasta()
    {
        var host = NewHost();
        host.Config.Accounts.Add(new AccountConfig { Id = "a1", Username = "u@exemplo.com", Type = "personal" });
        host.Config.Drives.Add(new DriveConfig { DriveId = FakeGraph.DriveId, AccountId = "a1", DisplayName = "OneDrive - Pessoal", Kind = "personal" });
        ShellUi.Wait(Task.Run(host.Engine.Start));
        try
        {
            ShellUi.PumpUntil(() => host.Engine.GetDrive(FakeGraph.DriveId)?.LastSync != null, "carga inicial", 15000);
            var root = Path.Combine(Scratch, "links-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(Path.Combine(root, "data", "od"));
            File.CreateSymbolicLink(Path.Combine(root, "OneDrive"), Path.Combine(root, "data", "od"));
            const string drive = "OneDrive - Pessoal";
            host.Config.MountPoint = Path.Combine(root, "OneDrive"); // configuração pelo link
            Assert.NotNull(host.ItemFromLocalPath(Path.Combine(root, "data", "od", drive))); // caminho real (kernel/Dolphin)
            Assert.NotNull(host.ItemFromLocalPath(Path.Combine(root, "OneDrive", drive)));
            host.Config.MountPoint = Path.Combine(root, "data", "od"); // configuração pelo caminho real
            Assert.NotNull(host.ItemFromLocalPath(Path.Combine(root, "OneDrive", drive))); // terminal que entrou pelo link
            Assert.Null(host.ItemFromLocalPath(Path.Combine(root, "data", drive)));
        }
        finally { ShellUi.Wait(Task.Run(host.ShutdownAsync), 30000); }
    }

    // ---------- §22.1 ----------

    static SqliteConnection Sql()
    {
        var c = new SqliteConnection("Data Source=" + AppPaths.DatabaseFile + ";Pooling=False");
        c.Open();
        return c;
    }

    static long Insert(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql + "; SELECT last_insert_rowid();";
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>mkdir em backoff com um envio dentro da pasta: esse sim está parado, com a contagem real.</summary>
    [AvaloniaFact]
    public void S22_1_Mkdir_em_backoff_para_so_o_que_esta_dentro_dele()
    {
        var host = NewHost();
        host.Config.Drives.Add(new DriveConfig { DriveId = "d1", AccountId = "a", DisplayName = "OneDrive - Pessoal" });
        var next = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds();
        using (var c = Sql())
        {
            var folder = Insert(c, "INSERT INTO items(drive_id,item_id,parent_lid,name,is_dir) VALUES('d1',NULL,0,'Relatórios',1)");
            var child = Insert(c, $"INSERT INTO items(drive_id,item_id,parent_lid,name,is_dir) VALUES('d1',NULL,{folder},'jan.pdf',0)");
            var other = Insert(c, "INSERT INTO items(drive_id,item_id,parent_lid,name,is_dir) VALUES('d1',NULL,0,'solto.txt',0)");
            Insert(c, $"INSERT INTO ops(drive_id,kind,lid,name,attempts,next_try,last_error,created) VALUES('d1','mkdir',{folder},'Relatórios',3,{next},'HTTP 500',1)");
            Insert(c, $"INSERT INTO ops(drive_id,kind,lid,name,attempts,next_try,created) VALUES('d1','upload',{child},'jan.pdf',0,0,2)");
            Insert(c, $"INSERT INTO ops(drive_id,kind,lid,name,attempts,next_try,created) VALUES('d1','upload',{other},'solto.txt',0,0,3)");
        }
        var vm = new MainViewModel(host);
        try
        {
            vm.RefreshQueue();
            var line = Assert.Single(vm.QueueBlocked);
            Assert.Contains("Relatórios", line);
            Assert.Contains("Parada para 1 alteração que depende dela", line); // jan.pdf; solto.txt não depende
            Assert.True(vm.QueueStalled);
            Assert.Equal("Envio parado", vm.QueueHeader);
            var w = new CraftDriver.App.Views.MainWindow { DataContext = vm, Width = 920, Height = 640 };
            w.Show();
            vm.SelectedTab = 0;
            ShellUi.Save(w, "r4-fila-mkdir-parado");
            w.Close();
        }
        finally
        {
            vm.Dispose();
            ShellUi.Wait(Task.Run(host.ShutdownAsync), 20000);
        }
    }

    // ---------- §25.3 (lado do app): quit espera a instância sair ----------

    static ProcessStartInfo CliPsi(string home, params string[] args)
    {
        var psi = new ProcessStartInfo(Child.Exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["CRAFTDRIVER_HOME"] = home;
        psi.Environment["XDG_RUNTIME_DIR"] = Path.Combine(Scratch, "run");
        psi.Environment["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/nonexistent";
        psi.Environment["CRAFTDRIVER_NO_NOTIFY"] = "1";
        psi.Environment.Remove("WAYLAND_DISPLAY");
        psi.Environment.Remove("DISPLAY");
        psi.Environment["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? Path.GetDirectoryName(Environment.ProcessPath)!;
        return psi;
    }

    static async Task<(int Code, string Text)> RunCliAsync(ProcessStartInfo psi)
    {
        using var p = Process.Start(psi)!;
        var ct = TestContext.Current.CancellationToken;
        var err = p.StandardError.ReadToEndAsync(ct);
        var text = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct);
        return (p.ExitCode, text + await err);
    }

    /// <summary>
    /// "craftdriver quit" volta só depois de a instância soltar a trava: o install-user.sh/--uninstall apagam o
    /// binário logo em seguida, e a instância nova (aberta logo depois) achava a trava ainda presa e sumia.
    /// </summary>
    [Fact]
    public async Task S25_3_Quit_espera_a_instancia_sair()
    {
        var shared = Path.Combine(Scratch, "comum-" + Guid.NewGuid().ToString("N")[..6]);
        var h = KeyringHome(shared + "/OneDrive", shared + "/cache");
        using var first = Child.Start(h, "--headless");
        WaitFile(Path.Combine(h, "run", "craftdriver.sock"), first);
        var (code, text) = await RunCliAsync(CliPsi(h, "quit"));
        // a instância sai ~300 ms depois de responder (QuitWhileStarting): sem a espera, o quit voltava antes
        var exitedWhenQuitReturned = first.P.HasExited;
        Assert.True(code == 0, $"quit saiu {code}: {text}");
        Assert.True(exitedWhenQuitReturned, "quit voltou com a instância ainda viva: " + text);
    }

    /// <summary>Instância que responde ao quit mas não solta a trava: prazo estourado → mensagem e saída ≠ 0.</summary>
    [Fact]
    public async Task S25_3_Quit_com_prazo_estourado_avisa_e_sai_com_erro()
    {
        var home = Path.Combine(Scratch, "home-" + Guid.NewGuid().ToString("N")[..6]);
        var run = Path.Combine(home, "run");
        Directory.CreateDirectory(run);
        using var held = SingleInstance.TryAcquire(Path.Combine(run, "craftdriver.lock"));
        Assert.NotNull(held);
        using var server = new IpcServer(Path.Combine(run, "craftdriver.sock"),
            r => Task.FromResult(new IpcResponse(true, r.Cmd == "quit" ? "encerrando" : "ok")));
        var psi = CliPsi(home, "quit");
        psi.Environment["CRAFTDRIVER_QUIT_TIMEOUT"] = "1";
        var sw = Stopwatch.StartNew();
        var (code, text) = await RunCliAsync(psi);
        Assert.True(code == 1, $"quit saiu {code}: {text}");
        Assert.Contains("ainda não terminou", text);
        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(1), "não esperou o prazo: " + sw.Elapsed);
    }

    // ---------- §25.11 ----------

    static void DropConnection(DesktopNotifier n) =>
        ((IDisposable)typeof(DesktopNotifier).GetField("_conn", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(n)!).Dispose();

    const string Hint = "Para entrar: aba Contas ou \"craftdriver login\".";

    /// <summary>Servidor sem a capacidade "actions": sem botões, a dica vai no texto.</summary>
    [Fact]
    public async Task S25_11_Servidor_sem_botoes_recebe_a_dica_no_texto()
    {
        using var bus = new PrivateBus();
        using var server = await FakeNotificationServer.StartAsync(bus.Address);
        server.Capabilities = ["body"];
        using var notifier = new DesktopNotifier(bus.Address, notifySend: "/nonexistent/notify-send");
        await notifier.NotifyAsync("sessão expirada", "A sessão expirou.", "craftdriver",
            [new("relogin", "Entrar novamente", () => { }) { HintWithoutButtons = Hint }], tag: "sessao-a1");
        var call = Assert.Single(server.Calls);
        Assert.Empty(call.Actions);
        Assert.Equal("A sessão expirou.\n" + Hint, call.Body);
    }

    [Fact]
    public async Task S25_11_Servidor_com_botoes_nao_repete_a_dica()
    {
        using var bus = new PrivateBus();
        using var server = await FakeNotificationServer.StartAsync(bus.Address);
        using var notifier = new DesktopNotifier(bus.Address, notifySend: "/nonexistent/notify-send");
        await notifier.NotifyAsync("t", "A sessão expirou.", "craftdriver",
            [new("relogin", "Entrar novamente", () => { }) { HintWithoutButtons = Hint }]);
        var call = Assert.Single(server.Calls);
        Assert.Equal(["relogin", "Entrar novamente"], call.Actions);
        Assert.Equal("A sessão expirou.", call.Body);
    }

    /// <summary>A notificação "sessão expirada" do AppHost leva a dica para servidores sem botões.</summary>
    [AvaloniaFact]
    public void S25_11_Sessao_expirada_leva_a_dica_para_servidor_sem_botoes()
    {
        var host = NewHost();
        try
        {
            var sent = new List<NotificationAction[]>();
            host.Notify = (_, _, a) => sent.Add(a);
            host.ReauthRequested += _ => { };
            host.Config.Accounts.Add(new AccountConfig { Id = "a1", Username = "u@exemplo.com", Type = "personal" });
            host.CheckSessions([("a1", true)]);
            var actions = Assert.Single(sent);
            Assert.NotEmpty(actions);
            Assert.All(actions, a => Assert.Contains("craftdriver login", a.HintWithoutButtons));
            Assert.All(actions, a => Assert.Contains("aba Contas", a.HintWithoutButtons));
        }
        finally { ShellUi.Wait(Task.Run(host.ShutdownAsync), 20000); }
    }

    [Fact]
    public void S25_11_NotifySend_recebe_a_dica_no_texto()
    {
        Assert.Equal("corpo\n" + Hint, DesktopNotifier.WithHints("corpo",
            [new("a", "A", () => { }) { HintWithoutButtons = Hint }, new("b", "B", () => { }) { HintWithoutButtons = Hint }]));
        Assert.Equal("corpo", DesktopNotifier.WithHints("corpo", [new("a", "A", () => { })]));
    }

    /// <summary>Conexão caiu depois do aviso (ResetAsync): o id não se perde — fechar e substituir ainda alcançam o aviso antigo.</summary>
    [Fact]
    public async Task S25_11_Depois_de_reconectar_fecha_e_substitui_o_aviso_antigo()
    {
        using var bus = new PrivateBus();
        using var server = await FakeNotificationServer.StartAsync(bus.Address);
        using var notifier = new DesktopNotifier(bus.Address, notifySend: "/nonexistent/notify-send");
        var first = await notifier.NotifyAsync("sessão expirada", "a1", "craftdriver", [], tag: "sessao-a1");
        Assert.NotNull(first);
        DropConnection(notifier);
        // nova notificação da mesma etiqueta: a 1ª tentativa falha (conexão morta), reconecta e substitui o aviso antigo
        var second = await notifier.NotifyAsync("sessão expirada", "a1 de novo", "craftdriver", [], tag: "sessao-a1");
        Assert.Equal(first, server.Calls.Last().ReplacesId);
        Assert.Equal(first, second);
        DropConnection(notifier);
        await notifier.CloseAsync("sessao-a1"); // entrou de novo: o aviso some mesmo com a conexão caída antes
        Assert.Equal([first!.Value], server.Closed.ToArray());
    }

    // ---------- §25.10: iniciado minimizado (sem janela) o "Sair" com transferência não pode ficar sem efeito ----------

    [AvaloniaFact]
    public void S25_10_Sair_da_bandeja_sem_janela_cria_a_janela_e_pergunta()
    {
        var host = NewHost();
        CraftDriver.App.App.Host = host;
        var (app, life) = NewApp();
        typeof(CraftDriver.App.App).GetMethod("InitWithHost", Priv)!.Invoke(app, [false]);
        Assert.Null(typeof(CraftDriver.App.App).GetField("_window", Priv)!.GetValue(app)); // minimizado: nenhuma janela
        var t = host.Activity.StartTransfer("grande.iso", 1000, upload: true);
        try
        {
            var tray = (Avalonia.Controls.TrayIcon)typeof(CraftDriver.App.App).GetField("_tray", Priv)!.GetValue(app)!;
            RaiseClick(tray.Menu!.Items.OfType<Avalonia.Controls.NativeMenuItem>().Single(i => i.Header == "Sair"));
            ShellUi.PumpUntil(() => typeof(CraftDriver.App.App).GetField("_window", Priv)!.GetValue(app) is CraftDriver.App.Views.MainWindow w
                                    && w.OwnedWindows.OfType<CraftDriver.App.Views.ConfirmWindow>().Any(), "diálogo \"Sair do craftDriver?\"", 5000);
            var win = (CraftDriver.App.Views.MainWindow)typeof(CraftDriver.App.App).GetField("_window", Priv)!.GetValue(app)!;
            var dlg = win.OwnedWindows.OfType<CraftDriver.App.Views.ConfirmWindow>().Single();
            ShellUi.Save(win, "r4-sair-da-bandeja-confirma");
            Assert.Equal(0, life.Shutdowns);
            // confirmar: sai de fato
            dlg.KeyPressQwerty(Avalonia.Input.PhysicalKey.Enter, Avalonia.Input.RawInputModifiers.None); // "Sair" (IsDefault)
            ShellUi.PumpUntil(() => life.Shutdowns > 0, "Shutdown depois de confirmar", 20000);
        }
        finally
        {
            host.Activity.EndTransfer(t);
            ShellUi.Wait(Task.Run(host.ShutdownAsync), 20000);
        }
    }
}
