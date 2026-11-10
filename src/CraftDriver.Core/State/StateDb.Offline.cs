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
}
