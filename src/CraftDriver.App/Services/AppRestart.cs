using System.Diagnostics;

namespace CraftDriver.App.Services;

/// <summary>
/// "Reiniciar agora": um <c>sh</c> desacoplado espera este processo terminar (até 30 s) e abre o app de novo com os
/// mesmos argumentos. Esperar o PID evita que a nova instância ache a antiga ainda viva (instância única via IPC) e só
/// peça para ela se mostrar.
/// </summary>
public static class AppRestart
{
    public static ProcessStartInfo BuildRelaunch(int pid, string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("i=0; while kill -0 \"$0\" 2>/dev/null && [ $i -lt 150 ]; do sleep 0.2; i=$((i+1)); done; " +
                             "shift; exec \"$@\" >/dev/null 2>&1 </dev/null");
        psi.ArgumentList.Add(pid.ToString());
        psi.ArgumentList.Add("relaunch"); // vira $1 e é descartado pelo shift
        psi.ArgumentList.Add(exe);
        foreach (var a in args.Where(a => a != "--minimized")) psi.ArgumentList.Add(a);
        return psi;
    }

    public static void ScheduleRelaunch()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("caminho do executável desconhecido");
        Process.Start(BuildRelaunch(Environment.ProcessId, exe, Environment.GetCommandLineArgs().Skip(1)));
    }
}
