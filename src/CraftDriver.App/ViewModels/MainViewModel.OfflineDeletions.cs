using CraftDriver.App.Services;
using CraftDriver.Core.Sync;

namespace CraftDriver.App.ViewModels;

/// <summary>Pergunta da abertura: apagar no OneDrive o que foi apagado das cópias offline com o app fechado?</summary>
public sealed record OfflineDeletionsRequest(string Title, string Text, IReadOnlyList<string> Paths);

public sealed partial class MainViewModel
{
    /// <summary>
    /// Mostra a pergunta e devolve true = apagar no OneDrive, false = manter, null = fechou sem decidir (Esc/fechar:
    /// a pergunta volta na próxima abertura). Sem assinante (sem janela), nada é decidido.
    /// </summary>
    public event Func<OfflineDeletionsRequest, Task<bool?>>? OfflineDeletionsRequested;

    bool _offlineDeletionsAsked;

    /// <summary>
    /// §30(a): pergunta (uma vez por abertura) sobre as exclusões feitas nas cópias offline com o app fechado. Chamar
    /// só com o motor rodando (depois do <c>Host.Start</c>) — o VM é criado antes. Devolve se perguntou.
    /// </summary>
    public async Task<bool> OfferOfflineDeletionsAsync()
    {
        if (_offlineDeletionsAsked || OfflineDeletionsRequested is not { } ask) return false;
        var pending = Host.OfflineDeletions.Pending();
        if (pending.Count == 0) return false;
        _offlineDeletionsAsked = true;
        var n = pending.Count;
        var text = (n == 1 ? "1 arquivo foi apagado" : $"{n} arquivos foram apagados") +
                   " da pasta com o app fechado. Apagar também no OneDrive? (vão para a Lixeira)";
        var choice = await ask(new OfflineDeletionsRequest("Arquivos apagados com o app fechado", text, pending.Select(p => p.Path).ToList()));
        if (choice == null) return true; // fechou sem decidir: nada muda, pergunta de novo na próxima abertura
        var lids = pending.Select(p => p.Lid).ToArray();
        try
        {
            if (choice == true)
            {
                var deleted = await Task.Run(() => Host.OfflineDeletions.ConfirmDelete(lids));
                Message = deleted == 1 ? "1 arquivo apagado no OneDrive (está na Lixeira)."
                    : $"{deleted} arquivos apagados no OneDrive (estão na Lixeira).";
            }
            else
            {
                await Task.Run(() => Host.OfflineDeletions.Keep(lids));
                Message = n == 1 ? "1 arquivo mantido no OneDrive: ele volta para a pasta."
                    : $"{n} arquivos mantidos no OneDrive: eles voltam para a pasta.";
            }
        }
        catch (Exception e)
        {
            ShowError("Não foi possível aplicar a escolha sobre os arquivos apagados: " + e.Message);
        }
        return true;
    }

    /// <summary>
    /// Iniciado minimizado na bandeja: avisa por notificação que há exclusões aguardando confirmação; clicar abre a
    /// janela (<paramref name="open"/>), que faz a pergunta.
    /// </summary>
    public void NotifyOfflineDeletionsAtStart(Action open)
    {
        var n = Host.OfflineDeletions.Pending().Count;
        if (n == 0) return;
        Host.Notify("CraftDriver",
            (n == 1 ? "1 exclusão aguardando confirmação" : $"{n} exclusões aguardando confirmação") +
            " (arquivos apagados da pasta com o app fechado). Abra o CraftDriver para decidir se apaga também no OneDrive.",
            [new(NotificationAction.Default, "Abrir o CraftDriver", open), new("open-offline-deletions", "Abrir o CraftDriver", open)]);
    }
}
