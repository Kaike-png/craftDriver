namespace CraftDriver.App.Services;

/// <summary>Decisões do início que não podem deixar o app invisível.</summary>
public static class StartupPlan
{
    /// <summary>
    /// Abrir a janela ao iniciar? "Minimizado" só vale com contas configuradas (sem conta, a janela é o
    /// único caminho para entrar) e com um host de bandeja (sem ele, nada mostraria que o app está aberto).
    /// </summary>
    public static bool ShowWindowAtStart(bool startMinimized, int accounts, bool trayHostAvailable) =>
        !startMinimized || accounts == 0 || !trayHostAvailable;
}
