namespace CraftDriver.Core.State;

public sealed partial class StateDb
{
    /// <summary>
    /// Painel da interface (conexão de leitura): operações que falharam e esperam nova tentativa — a fila é paralela
    /// (<see cref="ClaimOps"/> pega qualquer op pronta), então uma falha só segura o que DEPENDE dela, não "tudo atrás":
    /// <list type="bullet">
    /// <item>outras ops do mesmo item (andam em ordem por lid);</item>
    /// <item>mkdir: ops de itens dentro da pasta, que esperam o id remoto dela;</item>
    /// <item>exclusão: Move/mkdir/envio de item novo com o mesmo nome na mesma pasta (esperam o nome ser liberado).</item>
    /// </list>
    /// Com a drive em <c>delete_hold</c> (exclusão em massa esperando confirmação), as exclusões retidas que seguram
    /// alguém pelo nome também entram (<see cref="QueueWait.Held"/>), mesmo sem falha.
    /// </summary>
    public List<QueueWait> QueueWaits()
    {
        lock (_roLock)
        {
            using var cmd = Cmd(_ro, """
                WITH cand AS (
                  SELECT o.* FROM ops o
                  WHERE o.state='pending' AND (o.attempts>0
                     OR (o.kind='delete' AND EXISTS(SELECT 1 FROM drives d WHERE d.drive_id=o.drive_id AND d.delete_hold<>0)))
                )
                SELECT c.drive_id,c.kind,c.lid,c.name,c.attempts,c.next_try,c.last_error,
                  (SELECT COUNT(*) FROM ops p WHERE p.drive_id=c.drive_id AND c.lid<>0 AND p.lid=c.lid AND p.id>c.id)
                  + CASE WHEN c.kind='mkdir' AND c.lid<>0 THEN (
                      WITH RECURSIVE sub(lid) AS (
                        SELECT lid FROM items WHERE parent_lid=c.lid
                        UNION SELECT i.lid FROM items i JOIN sub ON i.parent_lid=sub.lid)
                      SELECT COUNT(*) FROM ops p WHERE p.drive_id=c.drive_id AND p.kind<>'delete' AND p.lid IN sub)
                    ELSE 0 END
                  + CASE WHEN c.kind='delete' AND c.parent_remote IS NOT NULL THEN (
                      SELECT COUNT(*) FROM ops p JOIN items it ON it.lid=p.lid JOIN items par ON par.lid=it.parent_lid
                      WHERE p.drive_id=c.drive_id AND p.id>c.id AND par.item_id=c.parent_remote
                        AND it.name=c.name COLLATE NOCASE
                        AND (p.kind IN ('move','mkdir') OR (p.kind='upload' AND it.item_id IS NULL)))
                    ELSE 0 END,
                  c.attempts=0
                FROM cand c ORDER BY c.drive_id, c.id
                """, []);
            using var r = cmd.ExecuteReader();
            var list = new List<QueueWait>();
            while (r.Read())
            {
                var held = r.GetInt64(8) != 0;
                var blocked = r.GetInt64(7);
                if (held && blocked == 0) continue; // exclusão retida que não segura ninguém: o aviso de retenção já cobre
                list.Add(new QueueWait(r.GetString(0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3),
                    (int)r.GetInt64(4), r.GetInt64(5), r.IsDBNull(6) ? null : r.GetString(6), blocked, held));
            }
            r.Close();
            AddFailedDeletesHoldingNames(list);
            return list;
        }
    }

    /// <summary>
    /// Exclusões em Problemas cujo item continua fora do banco seguram Move/mkdir/envio de item novo com o mesmo nome
    /// na mesma pasta até o usuário resolver (mesma regra de <c>NomeRetidoNaNuvem</c>, comparando como a nuvem).
    /// Chamar com <c>_roLock</c>.
    /// </summary>
    void AddFailedDeletesHoldingNames(List<QueueWait> list)
    {
        var failed = new List<(string Drive, string Parent, string Name, string? Error, long At)>();
        using (var cmd = Cmd(_ro, """
                   SELECT f.drive_id,f.parent_remote,f.name,f.error,f.at FROM failed_ops f
                   WHERE f.kind='delete' AND f.parent_remote IS NOT NULL AND f.name IS NOT NULL
                     AND NOT EXISTS(SELECT 1 FROM items i WHERE i.drive_id=f.drive_id AND i.item_id=f.remote_id)
                   """, []))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                failed.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4)));
        foreach (var f in failed)
        {
            using var cmd = Cmd(_ro, """
                SELECT it.name FROM ops p JOIN items it ON it.lid=p.lid JOIN items par ON par.lid=it.parent_lid
                WHERE p.drive_id=$d AND par.item_id=$p AND (p.kind IN ('move','mkdir') OR (p.kind='upload' AND it.item_id IS NULL))
                """, [("$d", f.Drive), ("$p", f.Parent)]);
            using var r = cmd.ExecuteReader();
            long blocked = 0;
            while (r.Read())
                if (SameCloudName(r.GetString(0), f.Name)) blocked++;
            if (blocked > 0)
                list.Add(new QueueWait(f.Drive, OpKind.Delete, 0, f.Name, 0, 0, f.Error, blocked, false, InProblems: true));
        }
    }
}

/// <summary>
/// Operação esperando nova tentativa (ou exclusão retida) e quantas outras ops dependem dela (<see cref="Blocked"/>;
/// 0 = falha isolada, o resto da fila segue).
/// </summary>
public sealed record QueueWait(string DriveId, string Kind, long Lid, string? Name, int Attempts, long NextTry, string? LastError,
    long Blocked, bool Held, bool InProblems = false);
