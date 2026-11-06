using System.Diagnostics;

namespace CraftDriver.App.Services;

/// <summary>
/// Área de transferência pelo comando do sistema (o app pode estar sem janela/--headless, e o Avalonia
/// só copia com uma janela aberta): wl-copy no Wayland; xclip ou xsel no X11.
/// </summary>
public static class ClipboardService
{
    public sealed record Command(string Exe, string[] Args);

    /// <summary>Comandos candidatos, na ordem, conforme a sessão gráfica.</summary>
    public static List<Command> Plan(Func<string, string?> env)
    {
        var list = new List<Command>();
        if (!string.IsNullOrEmpty(env("WAYLAND_DISPLAY"))) list.Add(new("wl-copy", []));
        if (!string.IsNullOrEmpty(env("DISPLAY")))
        {
            list.Add(new("xclip", ["-selection", "clipboard", "-in"]));
            list.Add(new("xsel", ["--clipboard", "--input"]));
        }
        return list;
    }

    /// <summary>Roda o comando com o texto na entrada; devolve o código de saída (null = não existe/não iniciou).</summary>
    public delegate Task<int?> Runner(Command cmd, string stdin);

    /// <summary>Copia <paramref name="text"/>. Devolve null se deu certo, ou o motivo da falha (para mostrar).</summary>
    public static async Task<string?> CopyAsync(string text, Func<string, string?>? env = null, Runner? run = null)
    {
        var plan = Plan(env ?? Environment.GetEnvironmentVariable);
        if (plan.Count == 0) return "nenhuma sessão gráfica (WAYLAND_DISPLAY/DISPLAY) encontrada";
        run ??= RunAsync;
        var tried = new List<string>();
        foreach (var cmd in plan)
        {
            var code = await run(cmd, text);
            if (code == 0) return null;
            tried.Add(code == null ? $"{cmd.Exe} não instalado" : $"{cmd.Exe} saiu com {code}");
        }
        var hint = plan[0].Exe == "wl-copy" ? " (instale o pacote wl-clipboard)" : " (instale xclip ou xsel)";
        return string.Join("; ", tried) + hint;
    }

    static async Task<int?> RunAsync(Command cmd, string stdin)
    {
        if (DesktopIntegration.FindInPath(cmd.Exe) == null) return null;
        // stdout/stderr não redirecionados: wl-copy/xclip deixam um processo filho servindo a seleção, e um
        // pipe herdado por ele faria a espera nunca terminar
        var psi = new ProcessStartInfo(cmd.Exe) { UseShellExecute = false, RedirectStandardInput = true };
        foreach (var a in cmd.Args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return null;
            try
            {
                await p.StandardInput.WriteAsync(stdin);
                p.StandardInput.Close();
            }
            catch (IOException)
            {
                // EPIPE: o comando saiu sem ler (ex.: wl-copy sem compositor); o código de saída diz o resto
                try { p.StandardInput.Dispose(); } catch (IOException) { } // o descarte tentaria escrever de novo
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { p.Kill(); } catch { } return -1; }
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
