using CraftDriver.Core.Ipc;

namespace CraftDriver.App.Services;

/// <summary>
/// Respostas do socket de comandos enquanto o AppHost ainda não existe (esperando o chaveiro). O servidor é aberto
/// pelo Program logo após pegar a trava de instância única; o AppHost assume o mesmo servidor ao iniciar.
/// </summary>
public static class StartupIpc
{
    public const string StillStarting = "O craftDriver ainda está iniciando (esperando o chaveiro do sistema). Tente de novo em instantes.";

    /// <param name="show">Mostra a janela de espera; null no --headless (sem janela).</param>
    /// <param name="quit">
    /// Desiste da inicialização ("craftdriver quit", ExecStop do serviço): nada foi montado ainda, então é só sair.
    /// Chamado depois de a resposta sair (o chamador agenda).
    /// </param>
    public static IpcResponse Handle(IpcRequest req, Action? show, Action? quit = null)
    {
        switch (req.Cmd)
        {
            case "quit" when quit != null:
                quit();
                return new(true, "encerrando");
            case "ping":
                return new(true, "iniciando");
            case "show" when show != null:
                show();
                return new(true, "ok");
            case "show":
                return new(false, "O craftDriver já está rodando sem interface (--headless) e ainda está iniciando.");
            default:
                return new(false, StillStarting);
        }
    }
}
