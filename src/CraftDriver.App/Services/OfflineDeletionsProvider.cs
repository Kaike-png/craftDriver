using CraftDriver.Core.Sync;

namespace CraftDriver.App.Services;

/// <summary>
/// Exclusões feitas nas cópias offline com o app fechado (docs/copias-offline.md), aguardando o usuário decidir se
/// apaga também na nuvem. Interface para a UI/CLI não depender da tabela real nos testes.
/// </summary>
public interface IOfflineDeletionsProvider
{
    IReadOnlyList<OfflineDeletion> Pending();
    /// <summary>Apaga na nuvem (Lixeira) pela fila normal; devolve quantos foram apagados.</summary>
    int ConfirmDelete(IReadOnlyCollection<long> lids);
    /// <summary>Mantém na nuvem; o arquivo volta para a montagem/cópias.</summary>
    void Keep(IReadOnlyCollection<long> lids);
}

/// <summary>Implementação da instância: delega a <see cref="OfflineDeletions"/> sobre o motor.</summary>
public sealed class EngineOfflineDeletions(SyncEngine engine) : IOfflineDeletionsProvider
{
    public IReadOnlyList<OfflineDeletion> Pending() => OfflineDeletions.Pending(engine);
    public int ConfirmDelete(IReadOnlyCollection<long> lids) => OfflineDeletions.ConfirmDelete(engine, lids);
    public void Keep(IReadOnlyCollection<long> lids) => OfflineDeletions.Keep(engine, lids);
}
