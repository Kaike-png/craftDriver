using Microsoft.Data.Sqlite;

namespace CraftDriver.Core.State;

/// <summary>Item espelhado localmente. <see cref="Lid"/> é estável mesmo antes de existir na nuvem.</summary>
public sealed class Item
{
    public long Lid;
    public string DriveId = "";
    /// <summary>Id remoto; null enquanto o item só existe localmente (criação pendente).</summary>
    public string? ItemId;
    public long ParentLid;
    public string Name = "";
    public bool IsDir;
    public long Size;
    /// <summary>Unix ms.</summary>
    public long MTime;
    public long CTime;
    public string? ETag;
    public string? CTag;
    public string? Hash;
    /// <summary>cTag da versão que está no cache local (null = nada em cache).</summary>
    public string? CachedCTag;
    /// <summary>Conteúdo local alterado ainda não enviado.</summary>
    public bool Dirty;
    public long DirtyGen;
    public bool Pinned;
    /// <summary>Nunca sincronizado (nome inválido no OneDrive ou arquivo temporário).</summary>
    public bool LocalOnly;
    public long LastAccess;

    public Item Clone() => (Item)MemberwiseClone();
    public override string ToString() => $"{Name} (lid {Lid}, id {ItemId ?? "-"})";
}

public sealed class Op
{
    public long Id;
    public string DriveId = "";
    public string Kind = "";
    public long Lid;
    public string? RemoteId;
    public int Attempts;
    public long NextTry;
    public string? LastError;
    /// <summary>Id remoto da pasta pai no momento da exclusão (para agrupar exclusões recursivas).</summary>
    public string? ParentRemote;
    public string? Name;
    public long Created;
}

public static class OpKind
{
    public const string Upload = "upload";
    public const string Mkdir = "mkdir";
    public const string Delete = "delete";
    public const string Move = "move";
    public const string SetTime = "settime";
}

public sealed record DriveState(string DriveId, long RootLid, string? DeltaLink, long QuotaTotal, long QuotaUsed);

/// <summary>
/// Estado persistente em SQLite. Uma conexão única protegida por lock
/// (chamado tanto pelas threads do FUSE quanto pelo motor de sync).
/// </summary>
public sealed class StateDb : IDisposable
{
    readonly SqliteConnection _c;
    public object Lock { get; } = new();

    public StateDb(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _c.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=OFF;");
        Exec("""
            CREATE TABLE IF NOT EXISTS items(
              lid INTEGER PRIMARY KEY AUTOINCREMENT,
              drive_id TEXT NOT NULL, item_id TEXT, parent_lid INTEGER NOT NULL, name TEXT NOT NULL,
              is_dir INTEGER NOT NULL, size INTEGER NOT NULL DEFAULT 0,
              mtime INTEGER NOT NULL DEFAULT 0, ctime INTEGER NOT NULL DEFAULT 0,
              etag TEXT, ctag TEXT, hash TEXT, cached_ctag TEXT,
              dirty INTEGER NOT NULL DEFAULT 0, dirty_gen INTEGER NOT NULL DEFAULT 0,
              pinned INTEGER NOT NULL DEFAULT 0, local_only INTEGER NOT NULL DEFAULT 0,
              last_access INTEGER NOT NULL DEFAULT 0);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_items_remote ON items(drive_id, item_id) WHERE item_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_items_parent ON items(parent_lid, name COLLATE NOCASE);
            CREATE TABLE IF NOT EXISTS drives(
              drive_id TEXT PRIMARY KEY, root_lid INTEGER NOT NULL, delta_link TEXT,
              quota_total INTEGER NOT NULL DEFAULT 0, quota_used INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS ops(
              id INTEGER PRIMARY KEY AUTOINCREMENT, drive_id TEXT NOT NULL, kind TEXT NOT NULL,
              lid INTEGER NOT NULL, remote_id TEXT, attempts INTEGER NOT NULL DEFAULT 0,
              next_try INTEGER NOT NULL DEFAULT 0, last_error TEXT, created INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_ops_drive ON ops(drive_id, id);
            """);
        // migrações de esquema
        foreach (var col in new[] { "parent_remote TEXT", "name TEXT" })
        {
            try { Exec($"ALTER TABLE ops ADD COLUMN {col}"); }
            catch (SqliteException) { /* já existe */ }
        }
        Exec("CREATE INDEX IF NOT EXISTS ix_ops_remote ON ops(drive_id, kind, remote_id)");
        foreach (var col in new[] { "delete_hold INTEGER NOT NULL DEFAULT 0", "held_count INTEGER NOT NULL DEFAULT 0" })
        {
            try { Exec($"ALTER TABLE drives ADD COLUMN {col}"); }
            catch (SqliteException) { /* já existe */ }
        }
    }

    public void Dispose()
    {
        lock (Lock) _c.Dispose();
    }

    // ---------- helpers ----------

    void Exec(string sql, params (string, object?)[] args)
    {
        lock (Lock)
        {
            using var cmd = Cmd(sql, args);
            cmd.ExecuteNonQuery();
        }
    }

    SqliteCommand Cmd(string sql, (string, object?)[] args)
    {
        var cmd = _c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    T? Scalar<T>(string sql, params (string, object?)[] args)
    {
        lock (Lock)
        {
            using var cmd = Cmd(sql, args);
            var r = cmd.ExecuteScalar();
            if (r is null or DBNull) return default;
            return (T)Convert.ChangeType(r, typeof(T));
        }
    }

    List<Item> Items(string sql, params (string, object?)[] args)
    {
        lock (Lock)
        {
            using var cmd = Cmd(sql, args);
            using var r = cmd.ExecuteReader();
            var list = new List<Item>();
            while (r.Read()) list.Add(Read(r));
            return list;
        }
    }

    const string Cols = "lid,drive_id,item_id,parent_lid,name,is_dir,size,mtime,ctime,etag,ctag,hash,cached_ctag,dirty,dirty_gen,pinned,local_only,last_access";

    static Item Read(SqliteDataReader r) => new()
    {
        Lid = r.GetInt64(0),
        DriveId = r.GetString(1),
        ItemId = r.IsDBNull(2) ? null : r.GetString(2),
        ParentLid = r.GetInt64(3),
        Name = r.GetString(4),
        IsDir = r.GetInt64(5) != 0,
        Size = r.GetInt64(6),
        MTime = r.GetInt64(7),
        CTime = r.GetInt64(8),
        ETag = r.IsDBNull(9) ? null : r.GetString(9),
        CTag = r.IsDBNull(10) ? null : r.GetString(10),
        Hash = r.IsDBNull(11) ? null : r.GetString(11),
        CachedCTag = r.IsDBNull(12) ? null : r.GetString(12),
        Dirty = r.GetInt64(13) != 0,
        DirtyGen = r.GetInt64(14),
        Pinned = r.GetInt64(15) != 0,
        LocalOnly = r.GetInt64(16) != 0,
        LastAccess = r.GetInt64(17),
    };

    static (string, object?)[] Params(Item i) =>
    [
        ("$lid", i.Lid), ("$drive", i.DriveId), ("$item", i.ItemId), ("$parent", i.ParentLid), ("$name", i.Name),
        ("$dir", i.IsDir ? 1 : 0), ("$size", i.Size), ("$mtime", i.MTime), ("$ctime", i.CTime), ("$etag", i.ETag),
        ("$ctag", i.CTag), ("$hash", i.Hash), ("$cached", i.CachedCTag), ("$dirty", i.Dirty ? 1 : 0),
        ("$gen", i.DirtyGen), ("$pinned", i.Pinned ? 1 : 0), ("$local", i.LocalOnly ? 1 : 0), ("$access", i.LastAccess),
    ];

    public void InTransaction(Action action)
    {
        lock (Lock)
        {
            using var tx = _c.BeginTransaction();
            action();
            tx.Commit();
        }
    }

    // ---------- items ----------

    public Item? Get(long lid) => Items($"SELECT {Cols} FROM items WHERE lid=$l", ("$l", lid)).FirstOrDefault();

    public Item? GetByRemote(string driveId, string itemId) =>
        Items($"SELECT {Cols} FROM items WHERE drive_id=$d AND item_id=$i", ("$d", driveId), ("$i", itemId)).FirstOrDefault();

    /// <summary>Busca exata; se não achar, retorna null (o Linux diferencia maiúsculas).</summary>
    public Item? GetChild(long parentLid, string name) =>
        Items($"SELECT {Cols} FROM items WHERE parent_lid=$p AND name=$n COLLATE NOCASE", ("$p", parentLid), ("$n", name))
            .FirstOrDefault(i => i.Name == name);

    /// <summary>Busca ignorando maiúsculas (o OneDrive não diferencia).</summary>
    public Item? GetChildNoCase(long parentLid, string name) =>
        Items($"SELECT {Cols} FROM items WHERE parent_lid=$p AND name=$n COLLATE NOCASE", ("$p", parentLid), ("$n", name))
            .OrderByDescending(i => i.Name == name).FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    public List<Item> GetChildren(long parentLid) =>
        Items($"SELECT {Cols} FROM items WHERE parent_lid=$p ORDER BY name", ("$p", parentLid));

    public bool HasChildren(long parentLid) =>
        Scalar<long>("SELECT EXISTS(SELECT 1 FROM items WHERE parent_lid=$p)", ("$p", parentLid)) != 0;

    public long Insert(Item i)
    {
        lock (Lock)
        {
            using var cmd = Cmd("""
                INSERT INTO items(drive_id,item_id,parent_lid,name,is_dir,size,mtime,ctime,etag,ctag,hash,cached_ctag,dirty,dirty_gen,pinned,local_only,last_access)
                VALUES($drive,$item,$parent,$name,$dir,$size,$mtime,$ctime,$etag,$ctag,$hash,$cached,$dirty,$gen,$pinned,$local,$access);
                SELECT last_insert_rowid();
                """, Params(i));
            i.Lid = (long)cmd.ExecuteScalar()!;
            return i.Lid;
        }
    }

    public void Update(Item i) => Exec("""
        UPDATE items SET drive_id=$drive,item_id=$item,parent_lid=$parent,name=$name,is_dir=$dir,size=$size,mtime=$mtime,
          ctime=$ctime,etag=$etag,ctag=$ctag,hash=$hash,cached_ctag=$cached,dirty=$dirty,dirty_gen=$gen,pinned=$pinned,
          local_only=$local,last_access=$access WHERE lid=$lid
        """, Params(i));

    public void Touch(long lid) =>
        Exec("UPDATE items SET last_access=$t WHERE lid=$l", ("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("$l", lid));

    public void Delete(long lid) => Exec("DELETE FROM items WHERE lid=$l", ("$l", lid));

    /// <summary>Item e todos os descendentes (pré-ordem).</summary>
    public List<Item> GetSubtree(long lid) => Items($"""
        WITH RECURSIVE t(lid) AS (SELECT $l UNION ALL SELECT i.lid FROM items i JOIN t ON i.parent_lid=t.lid)
        SELECT {Cols} FROM items WHERE lid IN (SELECT lid FROM t)
        """, ("$l", lid));

    /// <summary>Caminho de ancestrais (do pai até a raiz da drive).</summary>
    public List<Item> GetAncestors(long lid)
    {
        var list = new List<Item>();
        var cur = Get(lid);
        var guard = 0;
        while (cur != null && cur.ParentLid != 0 && guard++ < 4096)
        {
            cur = Get(cur.ParentLid);
            if (cur != null) list.Add(cur);
        }
        return list;
    }

    public bool IsEffectivelyPinned(long lid)
    {
        var i = Get(lid);
        if (i == null) return false;
        return i.Pinned || GetAncestors(lid).Any(a => a.Pinned);
    }

    /// <summary>Arquivos dentro de pastas fixadas que ainda não estão atualizados no cache.</summary>
    public List<Item> PinnedNeedingDownload(string driveId) => Items($"""
        WITH RECURSIVE p(lid) AS (
          SELECT lid FROM items WHERE drive_id=$d AND pinned=1
          UNION SELECT i.lid FROM items i JOIN p ON i.parent_lid=p.lid)
        SELECT {Cols} FROM items WHERE lid IN (SELECT lid FROM p) AND is_dir=0 AND dirty=0 AND local_only=0
          AND item_id IS NOT NULL AND (cached_ctag IS NULL OR cached_ctag IS NOT ctag)
        LIMIT 500
        """, ("$d", driveId));

    /// <summary>Arquivos em cache candidatos a remoção (não sujos), do menos para o mais usado.</summary>
    public List<Item> CachedByLeastRecent() => Items($"""
        SELECT {Cols} FROM items WHERE is_dir=0 AND dirty=0 AND cached_ctag IS NOT NULL ORDER BY last_access ASC
        """);

    public List<Item> AllDirty() => Items($"SELECT {Cols} FROM items WHERE dirty=1");

    public (long files, long dirs, long cached, long dirty) Counts(string driveId)
    {
        lock (Lock)
        {
            using var cmd = Cmd("""
                SELECT COALESCE(SUM(is_dir=0),0), COALESCE(SUM(is_dir=1),0), COALESCE(SUM(cached_ctag IS NOT NULL),0), COALESCE(SUM(dirty),0)
                FROM items WHERE drive_id=$d
                """, [("$d", driveId)]);
            using var r = cmd.ExecuteReader();
            r.Read();
            return (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
        }
    }

    public List<Item> AllPinned() => Items($"SELECT {Cols} FROM items WHERE pinned=1");

    public void DeleteDriveData(string driveId)
    {
        InTransaction(() =>
        {
            Exec("DELETE FROM items WHERE drive_id=$d", ("$d", driveId));
            Exec("DELETE FROM ops WHERE drive_id=$d", ("$d", driveId));
            Exec("DELETE FROM drives WHERE drive_id=$d", ("$d", driveId));
        });
    }

    // ---------- drives ----------

    public DriveState? GetDrive(string driveId)
    {
        lock (Lock)
        {
            using var cmd = Cmd("SELECT drive_id,root_lid,delta_link,quota_total,quota_used FROM drives WHERE drive_id=$d", [("$d", driveId)]);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new DriveState(r.GetString(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt64(3), r.GetInt64(4));
        }
    }

    public void SaveDrive(DriveState d) => Exec("""
        INSERT INTO drives(drive_id,root_lid,delta_link,quota_total,quota_used) VALUES($d,$r,$l,$t,$u)
        ON CONFLICT(drive_id) DO UPDATE SET root_lid=$r,delta_link=$l,quota_total=$t,quota_used=$u
        """, ("$d", d.DriveId), ("$r", d.RootLid), ("$l", d.DeltaLink), ("$t", d.QuotaTotal), ("$u", d.QuotaUsed));

    /// <summary>Retenção de exclusão em massa (persistida para sobreviver a reinícios).</summary>
    public (bool hold, long count) GetDeleteHold(string driveId)
    {
        lock (Lock)
        {
            using var cmd = Cmd("SELECT delete_hold, held_count FROM drives WHERE drive_id=$d", [("$d", driveId)]);
            using var r = cmd.ExecuteReader();
            return r.Read() ? (r.GetInt64(0) != 0, r.GetInt64(1)) : (false, 0);
        }
    }

    public void SetDeleteHold(string driveId, bool hold, long count) =>
        Exec("UPDATE drives SET delete_hold=$h, held_count=$c WHERE drive_id=$d", ("$h", hold ? 1 : 0), ("$c", count), ("$d", driveId));

    public int RemoveDeleteOps(string driveId)
    {
        lock (Lock)
        {
            using var cmd = Cmd("DELETE FROM ops WHERE drive_id=$d AND kind='delete'", [("$d", driveId)]);
            return cmd.ExecuteNonQuery();
        }
    }

    public void SetDeltaLink(string driveId, string? link) =>
        Exec("UPDATE drives SET delta_link=$l WHERE drive_id=$d", ("$l", link), ("$d", driveId));

    // ---------- fila de operações ----------

    public void Enqueue(string driveId, string kind, long lid, string? remoteId = null, string? parentRemote = null, string? name = null)
    {
        lock (Lock)
        {
            // coalescência: não duplica upload/mkdir/move/settime pendente para o mesmo item
            if (kind is OpKind.Upload or OpKind.Mkdir or OpKind.Move or OpKind.SetTime)
            {
                var exists = Scalar<long>("SELECT COUNT(*) FROM ops WHERE drive_id=$d AND kind=$k AND lid=$l AND attempts=0",
                    ("$d", driveId), ("$k", kind), ("$l", lid));
                if (exists > 0) return;
            }
            Exec("INSERT INTO ops(drive_id,kind,lid,remote_id,created,parent_remote,name) VALUES($d,$k,$l,$r,$t,$p,$n)",
                ("$d", driveId), ("$k", kind), ("$l", lid), ("$r", remoteId), ("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                ("$p", parentRemote), ("$n", name));
        }
    }

    public Op? PeekOp(string driveId)
    {
        lock (Lock)
        {
            using var cmd = Cmd("SELECT id,drive_id,kind,lid,remote_id,attempts,next_try,last_error,parent_remote,name,created FROM ops WHERE drive_id=$d ORDER BY id LIMIT 1", [("$d", driveId)]);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new Op
            {
                Id = r.GetInt64(0), DriveId = r.GetString(1), Kind = r.GetString(2), Lid = r.GetInt64(3),
                RemoteId = r.IsDBNull(4) ? null : r.GetString(4), Attempts = (int)r.GetInt64(5), NextTry = r.GetInt64(6),
                LastError = r.IsDBNull(7) ? null : r.GetString(7), ParentRemote = r.IsDBNull(8) ? null : r.GetString(8),
                Name = r.IsDBNull(9) ? null : r.GetString(9), Created = r.GetInt64(10),
            };
        }
    }

    public void CompleteOp(long id) => Exec("DELETE FROM ops WHERE id=$i", ("$i", id));

    public void FailOp(long id, string error, TimeSpan retryIn) => Exec(
        "UPDATE ops SET attempts=attempts+1,last_error=$e,next_try=$n WHERE id=$i",
        ("$e", error), ("$n", DateTimeOffset.UtcNow.Add(retryIn).ToUnixTimeMilliseconds()), ("$i", id));

    public void RemoveOpsForLid(long lid) => Exec("DELETE FROM ops WHERE lid=$l AND kind<>'delete'", ("$l", lid));

    /// <summary>Há exclusão pendente para este item remoto? (a exclusão da pasta cobre os filhos)</summary>
    public bool PendingDeleteFor(string driveId, string remoteId) =>
        Scalar<long>("SELECT EXISTS(SELECT 1 FROM ops WHERE drive_id=$d AND kind='delete' AND remote_id=$r)",
            ("$d", driveId), ("$r", remoteId)) != 0;

    public bool PendingOpsFor(long lid, string kind) =>
        Scalar<long>("SELECT EXISTS(SELECT 1 FROM ops WHERE lid=$l AND kind=$k)", ("$l", lid), ("$k", kind)) != 0;

    public List<(long lid, string itemId)> AllRemoteIds(string driveId)
    {
        lock (Lock)
        {
            using var cmd = Cmd("SELECT lid,item_id FROM items WHERE drive_id=$d AND item_id IS NOT NULL", [("$d", driveId)]);
            using var r = cmd.ExecuteReader();
            var list = new List<(long, string)>();
            while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1)));
            return list;
        }
    }

    public List<Op> AllOps()
    {
        lock (Lock)
        {
            using var cmd = Cmd("SELECT id,drive_id,kind,lid,remote_id,attempts,next_try,last_error FROM ops ORDER BY id", []);
            using var r = cmd.ExecuteReader();
            var list = new List<Op>();
            while (r.Read())
                list.Add(new Op
                {
                    Id = r.GetInt64(0), DriveId = r.GetString(1), Kind = r.GetString(2), Lid = r.GetInt64(3),
                    RemoteId = r.IsDBNull(4) ? null : r.GetString(4), Attempts = (int)r.GetInt64(5), NextTry = r.GetInt64(6),
                    LastError = r.IsDBNull(7) ? null : r.GetString(7),
                });
            return list;
        }
    }

    public long PendingOps(string? driveId = null) => driveId == null
        ? Scalar<long>("SELECT COUNT(*) FROM ops")
        : Scalar<long>("SELECT COUNT(*) FROM ops WHERE drive_id=$d", ("$d", driveId));
}
