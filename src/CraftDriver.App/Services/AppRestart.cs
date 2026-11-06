using System.Diagnostics;
using System.Globalization;

namespace CraftDriver.App.Services;

/// <summary>
/// "Reiniciar agora": um <c>sh</c> desacoplado espera este processo terminar e abre o app de novo com os mesmos
/// argumentos. Esperar o PID evita que a nova instância ache a antiga ainda viva (instância única) e só peça para
/// ela se mostrar. Sem limite de tempo: o encerramento pode demorar (desmontar com arquivo aberto, envio em curso)
/// e, com o antigo ainda de pé, o novo sairia na hora e nenhuma instância ficaria. Contra PID reaproveitado por
/// outro processo, a espera confere também a hora de início do processo (campo 22 de /proc/PID/stat).
/// </summary>
public static class AppRestart
{
    /// <param name="startTime">Hora de início (/proc/PID/stat) do processo a esperar; null = só o PID.</param>
    /// <param name="pollSeconds">Intervalo entre as conferências (testes usam curto).</param>
    public static ProcessStartInfo BuildRelaunch(int pid, string exe, IEnumerable<string> args, string? startTime = null, double pollSeconds = 0.2)
    {
        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        // $0 = pid, $1 = hora de início esperada (vazia = não confere); o resto é o comando
        psi.ArgumentList.Add(
            "alive() { kill -0 \"$1\" 2>/dev/null || return 1; [ -z \"$2\" ] && return 0; " +
            "st=$(sed 's/^.*) //' /proc/\"$1\"/stat 2>/dev/null | cut -d' ' -f20); [ \"$st\" = \"$2\" ]; }; " +
            "while alive \"$0\" \"$1\"; do sleep " + pollSeconds.ToString(CultureInfo.InvariantCulture) + "; done; " +
            "shift; exec \"$@\" >/dev/null 2>&1 </dev/null");
        psi.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(startTime ?? ""); // vira $1 e é descartado pelo shift
        psi.ArgumentList.Add(exe);
        foreach (var a in args.Where(a => a != "--minimized")) psi.ArgumentList.Add(a);
        // a escala que esta execução decidiu (DisplayScaling) não pode congelar no novo processo: ele decide de novo
        foreach (var v in DisplayScaling.AppliedVariables) psi.Environment.Remove(v);
        return psi;
    }

    /// <summary>Hora de início do processo (campo 22 de /proc/PID/stat), ou null.</summary>
    public static string? StartTimeOf(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            // o nome (campo 2) pode ter espaços e parênteses: corta depois do último ')'
            var rest = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return rest.Length > 19 ? rest[19] : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Agenda o relançamento com o mesmo comando (<see cref="DesktopIntegration.ExecArgv"/>: binário, ou "dotnet" + DLL)
    /// e os mesmos argumentos. Só chame depois de a saída estar confirmada: o sh fica esperando este PID.
    /// </summary>
    public static void ScheduleRelaunch()
    {
        if (Environment.ProcessPath == null) throw new InvalidOperationException("caminho do executável desconhecido");
        var argv = DesktopIntegration.ExecArgv;
        Process.Start(BuildRelaunch(Environment.ProcessId, argv[0], [.. argv.Skip(1), .. Environment.GetCommandLineArgs().Skip(1)],
            StartTimeOf(Environment.ProcessId)));
    }
}
