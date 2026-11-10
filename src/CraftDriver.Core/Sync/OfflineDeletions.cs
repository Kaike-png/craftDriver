using CraftDriver.Core.State;

namespace CraftDriver.Core.Sync;

/// <summary>Arquivo/pasta apagado das cópias offline com o app fechado, aguardando o usuário decidir.</summary>
public sealed record OfflineDeletion(long Lid, string Path);

/// <summary>
/// Exclusões detectadas por <see cref="OfflineCopies.Import"/> (item que sumiu das cópias offline com o app fechado).
/// Ausência local NUNCA apaga na nuvem sozinha: o app pergunta na abertura; enquanto o usuário não decide, o item continua
/// na nuvem e na montagem (e não volta nas cópias). Ver docs/copias-offline.md.
/// </summary>
public static class OfflineDeletions
{
    /// <summary>Pendências de exclusão detectadas na importação das cópias offline (só as cujo item ainda existe).</summary>
    public static IReadOnlyList<OfflineDeletion> Pending(SyncEngine e)
    {
        var list = new List<OfflineDeletion>();
        foreach (var (lid, driveId, path) in e.Db.OfflineDeletions())
        {
            if (e.Db.Get(lid) is { } it && it.DriveId == driveId) list.Add(new OfflineDeletion(lid, path));
            else e.Db.RemoveOfflineDeletion(lid); // sumiu (da nuvem ou daqui) nesse meio tempo: nada a decidir
        }
        return list;
    }

    /// <summary>Apaga na nuvem pela fila normal (vai para a Lixeira) e limpa as pendências. Devolve quantos foram apagados.</summary>
    public static int ConfirmDelete(SyncEngine e, IReadOnlyCollection<long> lids)
    {
        var db = e.Db;
        var pending = db.OfflineDeletions().ToDictionary(d => d.Lid);
        var deleted = 0;
        foreach (var lid in lids.Distinct())
        {
            if (!pending.TryGetValue(lid, out var p)) continue; // só o que foi detectado (não é um "apagar qualquer lid")
            if (db.Get(lid) is { } item && item.DriveId == p.DriveId && CanDelete(e, item))
            {
                RemoveItem(e, item);
                deleted++;
            }
            db.RemoveOfflineDeletion(lid); // sumiu da nuvem nesse meio tempo: descarta sem erro
        }
        return deleted;
    }

    /// <summary>Mantém na nuvem (o arquivo volta para a montagem/cópias como antes) e limpa as pendências.</summary>
    public static void Keep(SyncEngine e, IReadOnlyCollection<long> lids)
    {
        foreach (var lid in lids.Distinct()) e.Db.RemoveOfflineDeletion(lid);
    }

    static bool CanDelete(SyncEngine e, Item item) =>
        !e.IsReadOnly(item.DriveId) && !e.IsSharedTop(item) && e.Db.GetDrive(item.DriveId)?.RootLid != item.Lid;

    /// <summary>
    /// Mesmo caminho do unlink/rmdir do FUSE (VirtualFs.RemoveItem): op Delete na fila (a nuvem manda para a Lixeira),
    /// subárvore sai do banco e do cache numa transação, contagem de exclusões locais e acorda a fila.
    /// </summary>
    static void RemoveItem(SyncEngine e, Item item)
    {
        var db = e.Db;
        var cloudItems = 0;
        db.InTransaction(() =>
        {
            if (item.ItemId != null && !item.LocalOnly)
                db.Enqueue(item.DriveId, OpKind.Delete, item.Lid, item.ItemId, db.Get(item.ParentLid)?.ItemId, item.Name, item.IsDir);
            foreach (var x in item.IsDir ? db.GetSubtree(item.Lid) : [item])
            {
                if (x.ItemId != null && !x.LocalOnly) cloudItems++;
                db.RemoveOpsForLid(x.Lid);
                if (!x.IsDir) e.Cache.Delete(x.DriveId, x.Lid);
                db.Delete(x.Lid);
            }
        });
        e.RecordLocalDeletes(item.DriveId, cloudItems);
        e.NotifyLocalChange(item.DriveId);
    }
}
