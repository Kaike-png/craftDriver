using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
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
}
