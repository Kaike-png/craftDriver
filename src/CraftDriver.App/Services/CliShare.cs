using CraftDriver.Core.Ipc;

namespace CraftDriver.App.Services;

/// <summary>
/// "craftdriver share": quem copia o link é a instância em execução. Rodando como serviço do systemd (sem
/// WAYLAND_DISPLAY/DISPLAY) ela não tem a área de transferência; o CLI, que roda na sessão do usuário, manda
/// <see cref="CliCopiesFlag"/> e copia ele mesmo os links que a instância devolve sem copiar.
/// </summary>
public static class CliShare
{
    /// <summary>Argumento que o CLI manda quando ele mesmo pode copiar (tem sessão gráfica).</summary>
    public const string CliCopiesFlag = "--cli-copies";

    /// <summary>
    /// Completa a resposta: copia os links que a instância não copiou e devolve o texto para imprimir/notificar.
    /// Sem links pendentes, o texto da instância como veio.
    /// </summary>
    public static async Task<string> FinishAsync(IpcResponse resp, Func<string, Task<string?>> copy)
    {
        if (resp.Links is not { Length: > 0 } links) return resp.Text;
        var error = await copy(string.Join('\n', links));
        var line = error == null
            ? (links.Length == 1 ? "Link copiado para a área de transferência." : $"{links.Length} links copiados para a área de transferência.")
            : "Não foi possível copiar para a área de transferência: " + error;
        return resp.Text.Length == 0 ? line : resp.Text + "\n" + line;
    }
}
