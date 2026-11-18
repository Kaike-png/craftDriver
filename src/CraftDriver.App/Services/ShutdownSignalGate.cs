using System.Runtime.InteropServices;

namespace CraftDriver.App.Services;

/// <summary>
/// SIGTERM/SIGINT/SIGHUP e ProcessExit: o 1º sinal chama o encerramento completo (desmonta, para o motor, grava configuração,
/// apaga o socket) e cancela o término padrão; um 2º sinal enquanto esse encerramento ainda roda (ex.: desmontagem
/// presa em EBUSY por 10 s) sai na hora — antes todo sinal seguinte era engolido e só <c>kill -9</c> resolvia.
/// Sem encerramento definido (null), o padrão do sistema (terminar) vale.
/// </summary>
public sealed class ShutdownSignalGate(Func<Action?> current, Action<int> forceExit, Action? beforeForce = null)
{
    int _count;
    static int _isShuttingDown;

    /// <summary>Indica se o desligamento/encerramento foi solicitado.</summary>
    public static bool IsShuttingDown => Volatile.Read(ref _isShuttingDown) != 0;

    public static void SetShuttingDown() => Interlocked.Exchange(ref _isShuttingDown, 1);
    public static void ResetShuttingDown() => Interlocked.Exchange(ref _isShuttingDown, 0);

    /// <summary>Quantos sinais chegaram com encerramento definido.</summary>
    public int Count => Volatile.Read(ref _count);

    public void Handle(PosixSignalContext ctx)
    {
        SetShuttingDown();
        if (current() is not { } quit) return;
        ctx.Cancel = true; // o encerramento é nosso: o processo sai quando ele terminar
        if (Interlocked.Increment(ref _count) == 1)
        {
            quit();
            return;
        }
        try { Console.Error.WriteLine("CraftDriver: novo sinal durante o encerramento — saindo sem esperar."); }
        catch { }
        try { beforeForce?.Invoke(); }
        catch { }
        forceExit(128 + SignalNumber(ctx.Signal));
    }

    /// <summary>Ação de quit configurada.</summary>
    public Action? CurrentQuit => current();

    /// <summary>Registra tratadores para SIGTERM, SIGINT, SIGHUP e ProcessExit.</summary>
    public static List<PosixSignalRegistration> Register(ShutdownSignalGate gate)
    {
        var regs = new List<PosixSignalRegistration>();
        foreach (var sig in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP })
            regs.Add(PosixSignalRegistration.Create(sig, gate.Handle));

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            SetShuttingDown();
            if (gate.Count == 0 && gate.CurrentQuit is { } quit)
            {
                quit();
            }
        };
        return regs;
    }

    /// <summary>Número do sinal no Linux (o enum do .NET usa valores próprios).</summary>
    public static int SignalNumber(PosixSignal s) => s switch
    {
        PosixSignal.SIGHUP => 1,
        PosixSignal.SIGINT => 2,
        PosixSignal.SIGQUIT => 3,
        PosixSignal.SIGTERM => 15,
        _ => (int)s > 0 ? (int)s : 15,
    };
}
