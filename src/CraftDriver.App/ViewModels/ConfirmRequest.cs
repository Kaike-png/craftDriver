namespace CraftDriver.App.ViewModels;

/// <summary>Pedido de confirmação de uma ação; a view mostra a janela e devolve a escolha.</summary>
public sealed record ConfirmRequest(string Title, string Text, string Accept, bool Danger = false);
