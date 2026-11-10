namespace CraftDriver.Core.State;

/// <summary>
/// Uma entrada da cópia offline deixada na pasta real do ponto de montagem ao encerrar (ver docs/copias-offline.md).
/// <paramref name="Rel"/> é o caminho dentro da montagem ("/Drive/pasta/arquivo"); tamanho e data (ticks UTC) são os da
/// cópia gravada, para saber na próxima abertura se ela foi editada com o app fechado.
/// </summary>
public sealed record OfflineCopy(string Root, string Rel, long Lid, bool IsDir, long Size, long MTimeTicks);

public sealed partial class StateDb
{
    void InicializarCopiasOffline() => Exec("""
        CREATE TABLE IF NOT EXISTS offline_copies(
          rel TEXT PRIMARY KEY, root TEXT NOT NULL, lid INTEGER NOT NULL, is_dir INTEGER NOT NULL,
          size INTEGER NOT NULL, mtime_ticks INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS offline_deletions(
          lid INTEGER PRIMARY KEY, drive_id TEXT NOT NULL, path TEXT NOT NULL, detected_at INTEGER NOT NULL);
        """);

    public List<OfflineCopy> OfflineCopies()
    {
        lock (Lock)
        {
            using var cmd = Cmd("SELECT root,rel,lid,is_dir,size,mtime_ticks FROM offline_copies ORDER BY rel", []);
            using var r = cmd.ExecuteReader();
            var list = new List<OfflineCopy>();
            while (r.Read())
                list.Add(new OfflineCopy(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3) != 0, r.GetInt64(4), r.GetInt64(5)));
            return list;
        }
    }

    /// <summary>Troca o manifesto inteiro (uma transação).</summary>
    public void SaveOfflineCopies(IReadOnlyCollection<OfflineCopy> copies) => InTransaction(() =>
    {
        Exec("DELETE FROM offline_copies");
        foreach (var c in copies)
            Exec("INSERT OR REPLACE INTO offline_copies(rel,root,lid,is_dir,size,mtime_ticks) VALUES($r,$o,$l,$d,$s,$m)",
                ("$r", c.Rel), ("$o", c.Root), ("$l", c.Lid), ("$d", c.IsDir ? 1 : 0), ("$s", c.Size), ("$m", c.MTimeTicks));
    });

    public void ClearOfflineCopies() => Exec("DELETE FROM offline_copies");

    // ---------- exclusões feitas com o app fechado, aguardando a confirmação do usuário (docs/copias-offline.md) ----------

    /// <summary>Registra (ou renova) a pendência: o item sumiu das cópias offline. Nada é apagado na nuvem por isso.</summary>
    public void AddOfflineDeletion(long lid, string driveId, string path) =>
        Exec("INSERT OR REPLACE INTO offline_deletions(lid,drive_id,path,detected_at) VALUES($l,$d,$p,$t)",
            ("$l", lid), ("$d", driveId), ("$p", path), ("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

    public List<(long Lid, string DriveId, string Path)> OfflineDeletions()
    {
        lock (Lock)
        {
            using var cmd = Cmd("SELECT lid,drive_id,path FROM offline_deletions ORDER BY path", []);
            using var r = cmd.ExecuteReader();
            var list = new List<(long, string, string)>();
            while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
            return list;
        }
    }

    public void RemoveOfflineDeletion(long lid) => Exec("DELETE FROM offline_deletions WHERE lid=$l", ("$l", lid));
}
