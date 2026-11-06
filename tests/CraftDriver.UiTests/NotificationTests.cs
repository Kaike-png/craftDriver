using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Headless.XUnit;
using CraftDriver.App.Services;
using CraftDriver.App.ViewModels;
using CraftDriver.Core.Config;
using Tmds.DBus.Protocol;

namespace CraftDriver.UiTests;

static class NoRealNotifications
{
    /// <summary>Nenhum teste deste projeto pode mandar notificação para a área de trabalho de verdade.</summary>
    [ModuleInitializer]
    internal static void Init() => Environment.SetEnvironmentVariable("CRAFTDRIVER_NO_NOTIFY", "1");
}

/// <summary>
/// dbus-daemon de sessão privado (sem diretórios de serviço: nada é ativado nele, nem um servidor de
/// notificações real). Encerrado no Dispose e, por garantia, na saída do processo de testes.
/// </summary>
sealed class PrivateBus : IDisposable
{
    readonly Process _daemon;
    readonly string _dir;
    public string Address { get; }

    public PrivateBus()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cd-bus-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(_dir);
        var config = Path.Combine(_dir, "bus.conf");
        File.WriteAllText(config, $"""
            <!DOCTYPE busconfig PUBLIC "-//freedesktop//DTD D-Bus Bus Configuration 1.0//EN"
             "http://www.freedesktop.org/standards/dbus/1.0/busconfig.dtd">
            <busconfig>
              <type>session</type>
              <listen>unix:dir={_dir}</listen>
              <auth>EXTERNAL</auth>
              <policy context="default">
                <allow send_destination="*" eavesdrop="true"/>
                <allow eavesdrop="true"/>
                <allow own="*"/>
              </policy>
            </busconfig>
            """);
        var psi = new ProcessStartInfo("dbus-daemon") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "--config-file=" + config, "--nofork", "--nopidfile", "--print-address" }) psi.ArgumentList.Add(a);
        _daemon = Process.Start(psi)!;
        AppDomain.CurrentDomain.ProcessExit += OnExit;
        var line = _daemon.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        Address = line?.Trim() is { Length: > 0 } a2 ? a2 : throw new InvalidOperationException("dbus-daemon não informou o endereço: " + _daemon.StandardError.ReadToEnd());
    }

    void OnExit(object? s, EventArgs e) => Kill();

    void Kill()
    {
        try { if (!_daemon.HasExited) { _daemon.Kill(); _daemon.WaitForExit(5000); } } catch { }
    }

    public int Pid => _daemon.Id;

    public void Dispose()
    {
        Kill();
        AppDomain.CurrentDomain.ProcessExit -= OnExit;
        _daemon.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }
}

/// <summary>Servidor org.freedesktop.Notifications falso: grava as chamadas Notify e emite os sinais.</summary>
sealed class FakeNotificationServer : IPathMethodHandler, IDisposable
{
    public sealed record Call(uint Id, string App, string Icon, string Summary, string Body, string[] Actions, Dictionary<string, string> Hints, int Timeout, uint ReplacesId = 0);

    readonly DBusConnection _conn;
    uint _next = 41;
    public ConcurrentQueue<Call> Calls { get; } = new();
    /// <summary>Ids pedidos em CloseNotification.</summary>
    public ConcurrentQueue<uint> Closed { get; } = new();
    /// <summary>Resposta do GetCapabilities (sem "actions" = servidor que não mostra botões).</summary>
    public string[] Capabilities { get; set; } = ["actions", "body"];
    public string Path => "/org/freedesktop/Notifications";
    public bool HandlesChildPaths => false;

    FakeNotificationServer(DBusConnection conn) => _conn = conn;

    public static async Task<FakeNotificationServer> StartAsync(string address)
    {
        var conn = new DBusConnection(address);
        await conn.ConnectAsync();
        var server = new FakeNotificationServer(conn);
        conn.AddMethodHandler(server);
        await conn.RequestNameAsync("org.freedesktop.Notifications", RequestNameOptions.Default);
        return server;
    }

    public ValueTask HandleMethodAsync(MethodContext ctx)
    {
        if (ctx.Request.InterfaceAsString != "org.freedesktop.Notifications") return ValueTask.CompletedTask;
        switch (ctx.Request.MemberAsString)
        {
            case "Notify":
            {
                var r = ctx.Request.GetBodyReader();
                var app = r.ReadString();
                var replaces = r.ReadUInt32();
                var icon = r.ReadString();
                var summary = r.ReadString();
                var body = r.ReadString();
                var actions = r.ReadArrayOfString();
                var hints = r.ReadDictionaryOfStringToVariantValue().ToDictionary(kv => kv.Key, kv => kv.Value.GetString());
                var timeout = r.ReadInt32();
                // como os servidores reais: substituir devolve o mesmo id
                var id = replaces != 0 ? replaces : Interlocked.Increment(ref _next);
                Calls.Enqueue(new Call(id, app, icon, summary, body, actions, hints, timeout, replaces));
                using var w = ctx.CreateReplyWriter("u");
                w.WriteUInt32(id);
                ctx.Reply(w.CreateMessage());
                break;
            }
            case "CloseNotification":
            {
                Closed.Enqueue(ctx.Request.GetBodyReader().ReadUInt32());
                using var w = ctx.CreateReplyWriter(null);
                ctx.Reply(w.CreateMessage());
                break;
            }
            case "GetCapabilities":
            {
                using var w = ctx.CreateReplyWriter("as");
                w.WriteArray(Capabilities);
                ctx.Reply(w.CreateMessage());
                break;
            }
            default:
                ctx.ReplyUnknownMethodError();
                break;
        }
        return ValueTask.CompletedTask;
    }

    public void EmitActionInvoked(uint id, string key)
    {
        using var w = _conn.GetMessageWriter();
        w.WriteSignalHeader(null, Path, "org.freedesktop.Notifications", "ActionInvoked", "us");
        w.WriteUInt32(id);
        w.WriteString(key);
        Assert.True(_conn.TrySendMessage(w.CreateMessage()));
    }

    public void EmitClosed(uint id)
    {
        using var w = _conn.GetMessageWriter();
        w.WriteSignalHeader(null, Path, "org.freedesktop.Notifications", "NotificationClosed", "uu");
        w.WriteUInt32(id);
        w.WriteUInt32(2); // fechada pelo usuário
        Assert.True(_conn.TrySendMessage(w.CreateMessage()));
    }

    public void Dispose() => _conn.Dispose();
}

[Collection("dbus")]
public class DBusNotificationTests
{
    static async Task<T> Eventually<T>(Func<T?> probe, string what, int ms = 5000) where T : class
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (probe() is { } v) return v;
            await Task.Delay(20);
        }
        throw new TimeoutException(what);
    }

    [Fact]
    public async Task Notify_pelo_dbus_envia_titulo_corpo_acoes_e_hint_e_roda_o_callback()
    {
        using var bus = new PrivateBus();
        using var server = await FakeNotificationServer.StartAsync(bus.Address);
        using var notifier = new DesktopNotifier(bus.Address, notifySend: "/nonexistent/notify-send");

        var restored = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = 0;
        var id = await notifier.NotifyAsync("craftDriver — muitos arquivos apagados", "1989 itens foram apagados", "craftdriver",
        [
            new(NotificationAction.Default, "Abrir craftDriver", () => Interlocked.Increment(ref opened)),
            new("restore", "Restaurar", () => restored.TrySetResult(Environment.CurrentManagedThreadId)),
        ]);

        Assert.NotNull(id);
        var call = Assert.Single(server.Calls);
        Assert.Equal(id, call.Id);
        Assert.Equal("craftDriver", call.App);
        Assert.Equal("craftdriver", call.Icon);
        Assert.Equal("craftDriver — muitos arquivos apagados", call.Summary);
        Assert.Equal("1989 itens foram apagados", call.Body);
        Assert.Equal(["default", "Abrir craftDriver", "restore", "Restaurar"], call.Actions);
        Assert.Equal("craftdriver", call.Hints["desktop-entry"]);
        Assert.Equal(-1, call.Timeout);

        server.EmitActionInvoked(id!.Value + 1000, "restore"); // id de outra notificação: ignora
        server.EmitActionInvoked(id.Value, "restore");
        await restored.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, opened);
        Assert.Equal(1, notifier.PendingCallbacks);

        server.EmitClosed(id.Value);
        await Eventually(() => notifier.PendingCallbacks == 0 ? "ok" : null, "NotificationClosed não liberou o callback");
        server.EmitActionInvoked(id.Value, NotificationAction.Default); // depois de fechada: nada roda
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(0, opened);
    }

    /// <summary>15.12: "sessão expirada" com etiqueta — a 2ª substitui a 1ª (replaces_id) e fechar chama CloseNotification e solta os botões.</summary>
    [Fact]
    public async Task Notificacao_com_etiqueta_substitui_a_anterior_e_pode_ser_fechada()
    {
        using var bus = new PrivateBus();
        using var server = await FakeNotificationServer.StartAsync(bus.Address);
        using var notifier = new DesktopNotifier(bus.Address, notifySend: "/nonexistent/notify-send");
        NotificationAction[] relogin = [new("relogin", "Entrar novamente", () => { })];

        var first = await notifier.NotifyAsync("sessão expirada", "u@exemplo.com", "craftdriver", relogin, tag: "sessao-a1");
        var second = await notifier.NotifyAsync("sessão expirada", "u@exemplo.com", "craftdriver", relogin, tag: "sessao-a1");
        var other = await notifier.NotifyAsync("sessão expirada", "outra@exemplo.com", "craftdriver", relogin, tag: "sessao-a2");
        var calls = server.Calls.ToArray();
        Assert.Equal(0u, calls[0].ReplacesId);
        Assert.Equal(first, calls[1].ReplacesId);
        Assert.Equal(0u, calls[2].ReplacesId);
        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.Equal(2, notifier.PendingCallbacks);

        await notifier.CloseAsync("sessao-a1");
        Assert.Equal([first!.Value], server.Closed.ToArray());
        Assert.Equal(1, notifier.PendingCallbacks);
        await notifier.CloseAsync("sessao-a1"); // já fechada: nada
        Assert.Single(server.Closed);
    }

    [Fact]
    public async Task Callback_lento_nao_trava_o_recebimento_de_outros_sinais()
    {
        using var bus = new PrivateBus();
        using var server = await FakeNotificationServer.StartAsync(bus.Address);
        using var notifier = new DesktopNotifier(bus.Address, notifySend: "/nonexistent/notify-send");
        using var release = new ManualResetEventSlim();
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var a = await notifier.NotifyAsync("a", "a", "x", [new("x", "X", () => release.Wait(10_000))]);
        var b = await notifier.NotifyAsync("b", "b", "x", [new("y", "Y", () => second.TrySetResult())]);
        server.EmitActionInvoked(a!.Value, "x");
        server.EmitActionInvoked(b!.Value, "y");
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        release.Set();
    }

    static string FakeNotifySend(string dir, string log)
    {
        var exe = Path.Combine(dir, "notify-send");
        File.WriteAllText(exe, $"#!/bin/sh\nfor a in \"$@\"; do printf '%s\\n' \"$a\"; done > '{log}.tmp' && mv '{log}.tmp' '{log}'\n");
        File.SetUnixFileMode(exe, (UnixFileMode)0b111_101_101);
        return exe;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unix:path=/nonexistent/craftdriver-bus")]
    public async Task Sem_dbus_usa_o_notify_send(string? address)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cd-ns-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try
        {
            var log = Path.Combine(dir, "args.txt");
            using var notifier = new DesktopNotifier(address, notifySend: FakeNotifySend(dir, log));
            var id = await notifier.NotifyAsync("Título", "-corpo com 'aspas' e $HOME", "craftdriver", [new("restore", "Restaurar", () => { })]);
            Assert.Null(id);
            var args = await Eventually(() => File.Exists(log) ? File.ReadAllLines(log) : null, "o notify-send falso não rodou");
            Assert.Equal(["-a", "craftDriver", "-i", "craftdriver", "-h", "string:desktop-entry:craftdriver", "--", "Título", "-corpo com 'aspas' e $HOME"], args);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Barramento_sem_servidor_de_notificacoes_usa_o_notify_send()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cd-ns-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try
        {
            using var bus = new PrivateBus(); // ninguém é dono de org.freedesktop.Notifications nele
            var log = Path.Combine(dir, "args.txt");
            var logs = new List<string>();
            using var notifier = new DesktopNotifier(bus.Address, notifySend: FakeNotifySend(dir, log)) { Log = logs.Add };
            Assert.Null(await notifier.NotifyAsync("t", "b", "x", []));
            await Eventually(() => File.Exists(log) ? "ok" : null, "não caiu para o notify-send");
            Assert.Contains("ServiceUnknown", Assert.Single(logs)); // erro do barramento: sem segunda tentativa
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Reconecta_quando_o_barramento_cai()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cd-ns-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try
        {
            var log = Path.Combine(dir, "args.txt");
            string address;
            DesktopNotifier notifier;
            using (var bus = new PrivateBus())
            using (var server = await FakeNotificationServer.StartAsync(bus.Address))
            {
                address = bus.Address;
                notifier = new DesktopNotifier(address, notifySend: FakeNotifySend(dir, log));
                Assert.NotNull(await notifier.NotifyAsync("1", "1", "x", []));
            }
            // barramento encerrado: a conexão antiga morreu e não há outro → fallback, sem exceção
            Assert.Null(await notifier.NotifyAsync("2", "2", "x", []));
            await Eventually(() => File.Exists(log) ? "ok" : null, "não caiu para o notify-send");
            notifier.Dispose();
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// <summary>Notificações do AppHost: exclusão em massa (com ações) e sessão expirada (uma vez por conta).</summary>
// CRAFTDRIVER_HOME é global ao processo: fora da coleção, dois AppHost abriam o mesmo banco ao mesmo tempo
// ("duplicate column name" na migração, visto no CI)
[Collection("host-isolado")]
public class HostNotificationTests
{
    sealed record Sent(string Title, string Body, NotificationAction[] Actions);

    static (AppHost host, ConcurrentQueue<Sent> sent) NewHost()
    {
        var home = Path.Combine(Path.GetTempPath(), "craftdriver-ui-home-" + Guid.NewGuid().ToString("N")[..6]);
        Environment.SetEnvironmentVariable("CRAFTDRIVER_HOME", home);
        Directory.CreateDirectory(Path.Combine(home, "config"));
        File.WriteAllText(Path.Combine(home, "config", "config.json"),
            $$"""{ "MountPoint": "{{home}}/OneDrive", "CacheDir": "{{home}}/cache" }""");
        var host = Task.Run(() => AppHost.CreateAsync()).GetAwaiter().GetResult();
        var sent = new ConcurrentQueue<Sent>();
        host.Notify = (t, b, a) => sent.Enqueue(new Sent(t, b, a));
        return (host, sent);
    }

    [Fact]
    public async Task Exclusao_em_massa_notifica_com_abrir_e_restaurar()
    {
        var (host, sent) = NewHost();
        host.Config.Accounts.Add(new AccountConfig { Id = "a1", Username = "u@exemplo.com", Type = "personal" });
        host.Config.Drives.Add(new DriveConfig { DriveId = "d1", AccountId = "a1", DisplayName = "OneDrive - Pessoal", Kind = "personal" });
        var shown = 0;
        host.ShowRequested += () => Interlocked.Increment(ref shown);
        host.Engine.Start();
        try
        {
            host.Engine.RecordLocalDeletes("d1", 1989);
            var n = Assert.Single(sent);
            Assert.Contains("1989 itens", n.Body);
            Assert.Equal(["default", "open", "restore"], n.Actions.Select(a => a.Id));
            Assert.Equal("Restaurar", n.Actions.Single(a => a.Id == "restore").Label);
            Assert.Equal(1, shown); // a janela abre sozinha, como antes

            n.Actions.Single(a => a.Id == "open").OnInvoke();
            Assert.Equal(2, shown);

            Assert.True(host.Engine.GetDrive("d1")!.DeleteHold);
            await Task.Run(n.Actions.Single(a => a.Id == "restore").OnInvoke, TestContext.Current.CancellationToken);
            Assert.False(host.Engine.GetDrive("d1")!.DeleteHold);
        }
        finally { host.Engine.Stop(); }
    }

    [Fact]
    public void Exclusao_em_massa_sem_janela_so_oferece_restaurar()
    {
        var (host, sent) = NewHost();
        host.Config.Accounts.Add(new AccountConfig { Id = "a1", Username = "u@exemplo.com", Type = "personal" });
        host.Config.Drives.Add(new DriveConfig { DriveId = "d1", AccountId = "a1", DisplayName = "OneDrive", Kind = "personal" });
        host.Engine.Start();
        try
        {
            host.Engine.RecordLocalDeletes("d1", 500);
            Assert.Equal(["restore"], Assert.Single(sent).Actions.Select(a => a.Id));
        }
        finally { host.Engine.Stop(); }
    }

    [Fact]
    public void Sessao_expirada_notifica_uma_vez_por_conta_e_entrar_novamente_dispara_o_relogin()
    {
        var (host, sent) = NewHost();
        var a1 = new AccountConfig { Id = "a1", Username = "pessoal@exemplo.com", Type = "personal" };
        var a2 = new AccountConfig { Id = "a2", Username = "trabalho@exemplo.com", Type = "business" };
        host.Config.Accounts.AddRange([a1, a2]);
        var reauth = new List<string>();
        host.ReauthRequested += acc => reauth.Add(acc.Id);

        // a1 tem duas drives (OneDrive + biblioteca); só uma pede login
        host.CheckSessions([("a1", true), ("a1", false), ("a2", false)]);
        var n = Assert.Single(sent);
        Assert.Contains("pessoal@exemplo.com", n.Body);
        Assert.Equal(["default", "relogin"], n.Actions.Select(a => a.Id));
        Assert.Equal("Entrar novamente", n.Actions.Single(a => a.Id == "relogin").Label);

        // ciclos seguintes de sincronização com a mesma expiração: nada de novo
        for (var i = 0; i < 5; i++) host.CheckSessions([("a1", true), ("a1", true), ("a2", false)]);
        Assert.Single(sent);

        // a outra conta expira: avisa só ela
        host.CheckSessions([("a1", true), ("a2", true)]);
        Assert.Equal(2, sent.Count);
        Assert.Contains("trabalho@exemplo.com", sent.Last().Body);

        n.Actions.Single(a => a.Id == "relogin").OnInvoke();
        Assert.Equal(["a1"], reauth);

        // entrou de novo e depois expirou outra vez: novo aviso
        host.CheckSessions([("a1", false), ("a2", true)]);
        Assert.Equal(2, sent.Count);
        host.CheckSessions([("a1", true), ("a2", true)]);
        Assert.Equal(3, sent.Count);
        Assert.Contains("pessoal@exemplo.com", sent.Last().Body);
    }

    [Fact]
    public void Sessao_expirada_sem_janela_indica_o_login_pelo_terminal()
    {
        var (host, sent) = NewHost();
        host.Config.Accounts.Add(new AccountConfig { Id = "a1", Username = "u@exemplo.com", Type = "personal" });
        host.CheckSessions([("a1", true)]);
        var n = Assert.Single(sent);
        Assert.Empty(n.Actions);
        Assert.Contains("craftdriver login", n.Body);
    }

    [Fact]
    public void Rastreador_esquece_contas_removidas()
    {
        var t = new SessionExpiryTracker();
        Assert.Equal(["a1"], t.Update([("a1", true)]));
        Assert.Empty(t.Update([("a1", true)]));
        Assert.Empty(t.Update([])); // conta removida
        Assert.Equal(["a1"], t.Update([("a1", true)])); // adicionada de novo e expirada
    }

    [Fact]
    public async Task Ipc_open_sem_montagem_nao_abre_nada()
    {
        var (host, _) = NewHost();
        var opened = new List<string>();
        host.OpenFolder = opened.Add;
        var resp = await host.HandleIpcAsync(new CraftDriver.Core.Ipc.IpcRequest("open", []));
        Assert.False(resp.Ok);
        Assert.Contains("não está montada", resp.Text);
        Assert.Empty(opened);
    }

    [Fact]
    public void Intervalo_de_conflitos_nao_e_estatico_mutavel()
    {
        var property = typeof(AppHost).GetProperty("ConflictNoticeInterval", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.False(property.SetMethod!.IsStatic, "o intervalo de notificações de uma instância/teste afeta as outras");
    }

    /// <summary>§13.14: as drives de uma conta compartilham o "throttled até T"; contas diferentes não.</summary>
    [Fact]
    public void ClientFor_compartilha_o_throttling_entre_drives_da_mesma_conta()
    {
        var (host, _) = NewHost();
        Assert.Same(host.ClientFor("a1").Throttle, host.ClientFor("a1").Throttle);
        Assert.NotSame(host.ClientFor("a1").Throttle, host.ClientFor("a2").Throttle);
    }

    [Fact]
    public void Conflito_notifica_no_maximo_uma_vez_por_intervalo()
    {
        var (host, sent) = NewHost();
        var shown = 0;
        host.ShowRequested += () => Interlocked.Increment(ref shown);
        var c = new CraftDriver.Core.State.ConflictRecord(1, "d1", 5, "notas.md", "notas (conflito).md", "edição nos dois lados", 0);
        host.OnConflict(c);
        host.OnConflict(c with { Id = 2 }); // rajada: o segundo fica só na aba Conflitos
        var n = Assert.Single(sent);
        Assert.Contains("notas.md", n.Body);
        Assert.Contains("notas (conflito).md", n.Body);
        n.Actions.Single(a => a.Id == "show").OnInvoke();
        Assert.Equal(1, shown);

        var interval = host.ConflictNoticeInterval;
        host.ConflictNoticeInterval = TimeSpan.Zero;
        try
        {
            Thread.Sleep(5);
            host.OnConflict(c with { Id = 3 });
            Assert.Equal(2, sent.Count);
        }
        finally { host.ConflictNoticeInterval = interval; }
    }

    [AvaloniaFact]
    public void Entrar_novamente_da_notificacao_abre_o_login_da_conta()
    {
        var (host, sent) = NewHost();
        var acc = new AccountConfig { Id = "a1", Username = "u@exemplo.com", Type = "personal" };
        host.Config.Accounts.Add(acc);
        var vm = new MainViewModel(host);
        var shown = 0;
        host.ShowRequested += () => shown++;
        AccountConfig? loginFor = null;
        vm.AddAccountRequested += a => { loginFor = a; return Task.FromResult(false); };

        host.CheckSessions([("a1", true)]);
        var n = Assert.Single(sent);
        Task.Run(n.Actions.Single(a => a.Id == "relogin").OnInvoke).Wait(); // como o D-Bus: thread do pool
        var until = DateTime.UtcNow.AddSeconds(3);
        while (loginFor == null && DateTime.UtcNow < until)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        Assert.Same(acc, loginFor);
        Assert.Equal(1, shown);
        vm.Dispose();
    }
}
