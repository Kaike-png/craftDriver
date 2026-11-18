using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using CraftDriver.App.Services;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;
using Xunit;

namespace CraftDriver.UiTests;

/// <summary>org.freedesktop.login1 falso: Inhibit devolve a ponta de escrita de um pipe (a trava fica viva enquanto
/// alguém tiver uma cópia dela, como no logind real) e o teste emite PrepareForShutdown.</summary>
sealed class FakeLogind : IPathMethodHandler, IDisposable
{
    public sealed record InhibitCall(string What, string Who, string Why, string Mode);

    [DllImport("libc", SetLastError = true)]
    static extern int dup(int fd);

    readonly DBusConnection _conn;
    readonly ConcurrentQueue<AnonymousPipeServerStream> _pipes = new();
    public ConcurrentQueue<InhibitCall> Calls { get; } = new();
    public string Path => "/org/freedesktop/login1";
    public bool HandlesChildPaths => false;

    FakeLogind(DBusConnection conn) => _conn = conn;

    public static async Task<FakeLogind> StartAsync(string address)
    {
        var conn = new DBusConnection(address);
        await conn.ConnectAsync();
        var fake = new FakeLogind(conn);
        conn.AddMethodHandler(fake);
        await conn.RequestNameAsync("org.freedesktop.login1", RequestNameOptions.Default);
        return fake;
    }

    /// <summary>Completa quando todas as cópias da trava nº <paramref name="i"/> foram fechadas (EOF no pipe).</summary>
    public Task<int> ReleasedAsync(int i) => _pipes.ElementAt(i).ReadAsync(new byte[1]).AsTask();

    public ValueTask HandleMethodAsync(MethodContext ctx)
    {
        if (ctx.Request.InterfaceAsString != "org.freedesktop.login1.Manager") return ValueTask.CompletedTask;
        if (ctx.Request.MemberAsString != "Inhibit") { ctx.ReplyUnknownMethodError(); return ValueTask.CompletedTask; }
        var r = ctx.Request.GetBodyReader();
        Calls.Enqueue(new InhibitCall(r.ReadString(), r.ReadString(), r.ReadString(), r.ReadString()));
        var pipe = new AnonymousPipeServerStream(PipeDirection.In);
        _pipes.Enqueue(pipe);
        // a mensagem leva (e fecha depois de enviar) uma cópia própria; a nossa sai já
        var copy = new SafeFileHandle(dup((int)pipe.ClientSafePipeHandle.DangerousGetHandle()), ownsHandle: true);
        pipe.DisposeLocalCopyOfClientHandle();
        using var w = ctx.CreateReplyWriter("h");
        w.WriteHandle(copy);
        ctx.Reply(w.CreateMessage());
        return ValueTask.CompletedTask;
    }

    public void EmitPrepareForShutdown(bool starting)
    {
        using var w = _conn.GetMessageWriter();
        w.WriteSignalHeader(null, Path, "org.freedesktop.login1.Manager", "PrepareForShutdown", "b");
        w.WriteBool(starting);
        Assert.True(_conn.TrySendMessage(w.CreateMessage()));
    }

    public void Dispose()
    {
        _conn.Dispose();
        foreach (var p in _pipes) p.Dispose();
    }
}

/// <summary>
/// Desligar o PC com o app aberto: trava "delay" do logind para o encerramento (desmontar + cópias offline) terminar
/// antes de o systemd desmontar a pasta e matar o processo (o que deixava cópias pela metade e a montagem recusada).
/// </summary>
[Collection("dbus")]
public class ShutdownInhibitorTests
{
    static async Task Until(Func<bool> cond, string what, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException(what);
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Segura_a_trava_de_desligamento_e_encerra_uma_vez_quando_o_sistema_vai_desligar()
    {
        using var bus = new PrivateBus();
        using var logind = await FakeLogind.StartAsync(bus.Address);
        using var dbus = new LogindDBus(bus.Address);
        var quits = 0;
        var inh = new ShutdownInhibitor(dbus, () => Interlocked.Increment(ref quits));
        try
        {
            await inh.StartAsync();
            var call = Assert.Single(logind.Calls);
            Assert.Equal(new FakeLogind.InhibitCall("shutdown", "CraftDriver", ShutdownInhibitor.Why, "delay"), call);
            Assert.True(inh.Holding);
            var released = logind.ReleasedAsync(0);
            await Task.Delay(300, TestContext.Current.CancellationToken);
            Assert.False(released.IsCompleted, "a trava foi solta sem ninguém pedir");

            logind.EmitPrepareForShutdown(true);
            logind.EmitPrepareForShutdown(true); // repetido: um encerramento só
            await Until(() => Volatile.Read(ref quits) >= 1, "encerramento pedido no desligamento");
            await Task.Delay(300, TestContext.Current.CancellationToken);
            Assert.Equal(1, Volatile.Read(ref quits));
            // a trava fica até o processo sair (o encerramento ainda está gravando as cópias)
            Assert.False(released.IsCompleted, "a trava foi solta antes de o encerramento terminar");

            logind.EmitPrepareForShutdown(false); // cancelado (outro programa recusou)…
            logind.EmitPrepareForShutdown(true);  // …e pedido de novo: encerra de novo
            await Until(() => Volatile.Read(ref quits) == 2, "segundo desligamento depois do cancelamento");

            inh.Dispose(); // (o processo saindo faz o mesmo: fecha o descritor)
            Assert.Equal(0, await released.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.False(inh.Holding);
        }
        finally { inh.Dispose(); }
    }

    [Fact]
    public async Task Sem_logind_no_barramento_so_avisa_no_log()
    {
        using var bus = new PrivateBus(); // ninguém dono de org.freedesktop.login1
        using var dbus = new LogindDBus(bus.Address);
        var log = new List<string>();
        using var inh = new ShutdownInhibitor(dbus, () => Assert.Fail("não devia encerrar"), log.Add);
        await inh.StartAsync().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.False(inh.Holding);
        Assert.Contains(log, l => l.Contains("logind", StringComparison.Ordinal));
    }

    sealed class TestWindow : Avalonia.Controls.Window
    {
        public bool TriggerClosing(Avalonia.Controls.WindowClosingEventArgs e)
        {
            OnClosing(e);
            return e.Cancel;
        }
    }

    static Avalonia.Controls.WindowClosingEventArgs CreateClosingArgs(Avalonia.Controls.WindowCloseReason reason)
    {
        var ctor = typeof(Avalonia.Controls.WindowClosingEventArgs).GetConstructor(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null, [typeof(Avalonia.Controls.WindowCloseReason)], null);
        if (ctor != null)
        {
            return (Avalonia.Controls.WindowClosingEventArgs)ctor.Invoke([reason]);
        }
        var emptyCtor = typeof(Avalonia.Controls.WindowClosingEventArgs).GetConstructor(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null, Type.EmptyTypes, null);
        var instance = (Avalonia.Controls.WindowClosingEventArgs)(emptyCtor?.Invoke([])
            ?? System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Avalonia.Controls.WindowClosingEventArgs)));
        var prop = typeof(Avalonia.Controls.WindowClosingEventArgs).GetProperty("CloseReason");
        if (prop?.CanWrite == true) prop.SetValue(instance, reason);
        else
        {
            var field = typeof(Avalonia.Controls.WindowClosingEventArgs).GetField("<CloseReason>k__BackingField",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(instance, reason);
        }
        return instance;
    }

    [AvaloniaFact]
    public void Fechamento_da_janela_com_OSShutdown_nao_bloqueia_no_CloseToTrayBehavior()
    {
        ShutdownSignalGate.ResetShuttingDown();
        var cfg = new CraftDriver.Core.Config.AppConfig();
        var quitCount = 0;
        var behavior = new CloseToTrayBehavior(
            cfg,
            () => Task.FromResult(true),
            (_, _) => { },
            _ => Task.FromResult(NoTrayChoice.Cancel),
            () => quitCount++);

        var win = new TestWindow();
        behavior.Attach(win, () => false);

        // 1. Fechamento normal tenta cancelar para ir para a bandeja
        var normalArgs = CreateClosingArgs(Avalonia.Controls.WindowCloseReason.WindowClosing);
        var normalCanceled = win.TriggerClosing(normalArgs);
        Assert.True(normalCanceled, "fechamento normal da janela devia cancelar para bandeja");
        Assert.NotNull(behavior.Pending);

        // 2. Fechamento por OSShutdown NÃO pode ser cancelado
        var osShutdownArgs = CreateClosingArgs(Avalonia.Controls.WindowCloseReason.OSShutdown);
        var osCanceled = win.TriggerClosing(osShutdownArgs);
        Assert.False(osCanceled, "OSShutdown não pode ser cancelado pelo CloseToTrayBehavior");

        // 3. Fechamento por ApplicationShutdown NÃO pode ser cancelado
        var appShutdownArgs = CreateClosingArgs(Avalonia.Controls.WindowCloseReason.ApplicationShutdown);
        var appCanceled = win.TriggerClosing(appShutdownArgs);
        Assert.False(appCanceled, "ApplicationShutdown não pode ser cancelado pelo CloseToTrayBehavior");

        // 4. Se ShutdownSignalGate indicar desligamento ativo, mesmo WindowClosing não é cancelado
        try
        {
            ShutdownSignalGate.SetShuttingDown();
            var duringShutdownArgs = CreateClosingArgs(Avalonia.Controls.WindowCloseReason.WindowClosing);
            var duringShutdownCanceled = win.TriggerClosing(duringShutdownArgs);
            Assert.False(duringShutdownCanceled, "fechamento com shutdown ativo não pode ser cancelado");
        }
        finally
        {
            ShutdownSignalGate.ResetShuttingDown();
        }
    }

    [Fact]
    public async Task Liberacao_garantida_do_recurso_em_ShutdownInhibitor_sob_desligamento_com_tarefas_lentas()
    {
        using var bus = new PrivateBus();
        using var logind = await FakeLogind.StartAsync(bus.Address);
        using var dbus = new LogindDBus(bus.Address);
        var quits = 0;
        var slowTaskTcs = new TaskCompletionSource();

        // Tarefa lenta de flush simulada que demora mais do que o prazo curto de 2 segundos
        using var inh = new ShutdownInhibitor(dbus, () => Interlocked.Increment(ref quits), null,
            fastFlush: async ct =>
            {
                await slowTaskTcs.Task.WaitAsync(ct);
            });

        await inh.StartAsync();
        Assert.True(inh.Holding);
        var released = logind.ReleasedAsync(0);

        // Dispara o sinal de desligamento
        logind.EmitPrepareForShutdown(true);

        await Until(() => Volatile.Read(ref quits) == 1, "quit disparado pelo sinal de desligamento");

        // O flush rápido tem timeout curto (máx 2s). Mesmo com a slowTaskTcs não completada,
        // o inibidor deve liberar o descritor (FD) garantidamente após o timeout!
        var releaseResult = await released.WaitAsync(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        Assert.Equal(0, releaseResult);
        Assert.False(inh.Holding, "a trava do inibidor devia ser liberada após o timeout do flush rápido");

        slowTaskTcs.TrySetResult();
    }
}
