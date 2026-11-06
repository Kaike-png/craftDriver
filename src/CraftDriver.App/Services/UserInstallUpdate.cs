using System.Diagnostics;
using System.Globalization;
using CraftDriver.Core.Updates;

namespace CraftDriver.App.Services;

/// <summary>
/// Atualização da instalação do usuário (install-user.sh, ~/.local/opt/craftdriver): um <c>sh</c> desacoplado
/// (setsid) extrai o tarball já conferido numa pasta temporária e roda o install-user.sh DELE — que encerra este app
/// ("craftdriver quit") e espera ele sair antes de trocar os arquivos. No fim, se este processo já saiu, abre o app de
/// novo com o mesmo comando e argumentos do "Reiniciar agora" (<see cref="AppRestart"/>). Se o instalador falhar antes
/// de encerrar o app, nada é reaberto: o app continua e mostra o erro (código de saída do sh). Saída em <c>log</c>.
/// </summary>
public static class UserInstallUpdate
{
    /// <summary>
    /// $1 tarball, $2 pasta temporária, $3 PID do app (vazio = não reabre), $4 hora de início do PID, $5 log (vazio =
    /// saída herdada); o resto é o comando para reabrir. Códigos: 3 = não extraiu, 4 = tarball sem install-user.sh,
    /// 5 = instalou mas o app não saiu; demais = código do install-user.sh.
    /// </summary>
    const string Script = """
        tar="$1"; work="$2"; pid="$3"; st="$4"; log="$5"; shift 5
        say() { if [ -n "$log" ]; then printf '%s\n' "$*" >>"$log"; else printf '%s\n' "$*" >&2; fi; }
        run() { if [ -n "$log" ]; then "$@" >>"$log" 2>&1 </dev/null; else "$@"; fi; }
        alive() { [ -n "$1" ] || return 1; kill -0 "$1" 2>/dev/null || return 1; [ -z "$2" ] && return 0
          s=$(sed 's/^.*) //' /proc/"$1"/stat 2>/dev/null | cut -d' ' -f20); [ "$s" = "$2" ]; }
        say "$(date '+%F %T') atualização do craftDriver: $tar"
        rm -rf "$work" && mkdir -p "$work" || { say "não foi possível criar $work"; exit 3; }
        if ! run tar -xzf "$tar" -C "$work"; then say "falha ao extrair $tar"; rm -rf "$work"; exit 3; fi
        inst=""
        for d in "$work"/craftdriver-*/; do if [ -x "$d/install-user.sh" ]; then inst="$d/install-user.sh"; break; fi; done
        if [ -z "$inst" ]; then say "o tarball não tem install-user.sh"; rm -rf "$work"; exit 4; fi
        run "$inst"; rc=$?
        rm -rf "$work"
        say "install-user.sh terminou com código $rc"
        [ -n "$pid" ] || exit $rc
        # deu certo: o install-user.sh já pediu o "quit" e esperou; dá um prazo para o processo sumir de vez
        if [ $rc -eq 0 ]; then i=0; while alive "$pid" "$st" && [ $i -lt 300 ]; do sleep 0.2; i=$((i + 1)); done; fi
        if alive "$pid" "$st"; then say "o craftDriver continua aberto: nada a reabrir"; [ $rc -eq 0 ] && exit 5; exit $rc; fi
        [ $# -gt 0 ] || exit $rc
        say "reabrindo: $*"
        exec "$@" >/dev/null 2>&1 </dev/null
        """;

    /// <param name="tarball">craftdriver-X-linux-ARCH.tar.gz já conferido (SHA256SUMS).</param>
    /// <param name="workDir">Pasta temporária para extrair (apagada no fim).</param>
    /// <param name="waitPid">Processo do app a reabrir quando sair; null = só instala (CLI sem instância).</param>
    /// <param name="startTime">Hora de início do <paramref name="waitPid"/> (PID reaproveitado).</param>
    /// <param name="log">Arquivo com a saída do instalador; null = saída herdada (terminal).</param>
    /// <param name="relaunch">Comando para reabrir (binário + argumentos).</param>
    /// <param name="detach">Nova sessão (setsid): sobrevive ao fim do app e ao SIGHUP do terminal que o abriu.</param>
    public static ProcessStartInfo Build(string tarball, string workDir, int? waitPid, string? startTime, string? log,
        IReadOnlyList<string> relaunch, bool detach = true)
    {
        var setsid = detach ? ProcessRunner.FindInPath("setsid") : null;
        var psi = new ProcessStartInfo(setsid ?? "/bin/sh") { UseShellExecute = false };
        if (setsid != null) psi.ArgumentList.Add("/bin/sh");
        foreach (var a in new[] { "-c", Script, "craftdriver-update", tarball, workDir, waitPid?.ToString(CultureInfo.InvariantCulture) ?? "",
                     startTime ?? "", log ?? "" })
            psi.ArgumentList.Add(a);
        foreach (var a in relaunch) psi.ArgumentList.Add(a);
        // como no AppRestart: a escala decidida nesta execução não congela no processo novo
        foreach (var v in DisplayScaling.AppliedVariables) psi.Environment.Remove(v);
        return psi;
    }

    /// <summary>Inicia o instalador desacoplado e reabre o app ao terminar (comando do "Reiniciar agora").</summary>
    public static Process? Launch(string tarball, string log)
    {
        var argv = DesktopIntegration.ExecArgv;
        string[] relaunch = [.. argv, .. Environment.GetCommandLineArgs().Skip(1).Where(a => a != "--minimized")];
        var work = Path.Combine(Path.GetTempPath(), "craftdriver-atualizacao-" + Guid.NewGuid().ToString("N")[..8]);
        return Process.Start(Build(tarball, work, Environment.ProcessId, AppRestart.StartTimeOf(Environment.ProcessId), log, relaunch));
    }
}
