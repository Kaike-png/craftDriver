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
    /// <summary>Permissões (chmod) guardadas só localmente: 0 = padrão (0644/0755); senão VirtualFs.ExplicitMode | bits.</summary>
    public uint Mode;

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
    /// <summary>Exclusão de pasta (gravado desde a sincronização seletiva; ops antigas ficam false).</summary>
    public bool IsDir;
    public long Created;
    /// <summary>Envio grande em andamento: sessão de upload, cópia congelada do conteúdo e geração dela.</summary>
    public string? UploadUrl;
    public string? UploadSnapshot;
    public long UploadGen;
}

public static class OpKind
{
    public const string Upload = "upload";
    public const string Mkdir = "mkdir";
    public const string Delete = "delete";
    public const string Move = "move";
    public const string SetTime = "settime";
}

/// <summary>Operação que falhou de forma permanente; fica guardada para o usuário ver e tentar de novo.</summary>
/// <summary>
/// Conflito resolvido com cópia (as duas versões ficam): aparece na aba Conflitos até o usuário dispensar.
/// <paramref name="Lid"/> é o item que o usuário deve olhar (a cópia, ou o próprio item reenviado).
/// </summary>
public sealed record ConflictRecord(long Id, string DriveId, long Lid, string Original, string Copy, string Reason, long At);

public sealed record FailedOp(long Id, string DriveId, string Kind, long Lid, string? RemoteId, string? ParentRemote,
    string? Name, string Error, long At);

public sealed record DriveState(string DriveId, long RootLid, string? DeltaLink, long QuotaTotal, long QuotaUsed);

/// <summary>
/// Raiz de um item de outra drive que aparece dentro desta árvore: atalho ("Adicionar a Meus arquivos") ou
/// entrada de "Compartilhados comigo". <paramref name="Key"/> é o drive_id interno dos itens dela.
/// </summary>
/// <param name="Source"><see cref="SharedSource.Shortcut"/> ou <see cref="SharedSource.SharedWithMe"/>.</param>
/// <param name="HostItemId">Atalho: id do próprio atalho na drive do usuário (some no delta quando ele é removido).</param>
public sealed record SharedRoot(string Key, string HostDriveId, string Source, string? HostItemId, string RemoteDriveId,
    string RemoteItemId, long RootLid, bool Writable);

public static class SharedSource
{
    public const string Shortcut = "atalho";
    public const string SharedWithMe = "comigo";
}

/// <summary>
/// Estado persistente em SQLite. Uma conexão única protegida por lock
/// (chamado tanto pelas threads do FUSE quanto pelo motor de sync).
/// </summary>
public sealed class StateDb : IDisposable
{
    readonly SqliteConnection _c;
    /// <summary>
    /// Conexão só de leitura para consultas da interface/status (contagens, problemas, fixados). Com WAL ela lê o
    /// último estado confirmado sem esperar o <see cref="Lock"/>, que o FUSE e o motor seguram em transações.
    /// </summary>
    readonly SqliteConnection _ro;
    readonly object _roLock = new();
    public object Lock { get; } = new();
    /// <summary>Geração para caches de resolução; ler sob Lock. Inclui rollback.</summary>
    public long ItemsVersion { get; private set; }

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
        foreach (var col in new[] { "parent_remote TEXT", "name TEXT", "upload_url TEXT", "upload_snap TEXT", "upload_gen INTEGER NOT NULL DEFAULT 0", "is_dir INTEGER NOT NULL DEFAULT 0" })
        {
            try { Exec($"ALTER TABLE ops ADD COLUMN {col}"); }
            catch (SqliteException) { /* já existe */ }
        }
        Exec("CREATE INDEX IF NOT EXISTS ix_ops_remote ON ops(drive_id, kind, remote_id)");
        try { Exec("ALTER TABLE items ADD COLUMN mode INTEGER NOT NULL DEFAULT 0"); }
        catch (SqliteException) { /* já existe */ }
        Exec("""
            CREATE TABLE IF NOT EXISTS excluded(lid INTEGER PRIMARY KEY, drive_id TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS conflicts(
              id INTEGER PRIMARY KEY AUTOINCREMENT, drive_id TEXT NOT NULL, lid INTEGER NOT NULL,
              original TEXT NOT NULL, copy TEXT NOT NULL, reason TEXT NOT NULL, at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS failed_ops(
              id INTEGER PRIMARY KEY AUTOINCREMENT, drive_id TEXT NOT NULL, kind TEXT NOT NULL, lid INTEGER NOT NULL,
              remote_id TEXT, parent_remote TEXT, name TEXT, error TEXT NOT NULL, at INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_failed_lid ON failed_ops(lid);
            """);
        foreach (var col in new[] { "delete_hold INTEGER NOT NULL DEFAULT 0", "held_count INTEGER NOT NULL DEFAULT 0" })
        {
            try { Exec($"ALTER TABLE drives ADD COLUMN {col}"); }
            catch (SqliteException) { /* já existe */ }
        }
        LoadExcluded();
        Exec("""
            CREATE TABLE IF NOT EXISTS shared_roots(
              key TEXT PRIMARY KEY, host_drive TEXT NOT NULL, source TEXT NOT NULL, host_item TEXT,
              remote_drive TEXT NOT NULL, remote_item TEXT NOT NULL, root_lid INTEGER NOT NULL,
              writable INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_shared_host ON shared_roots(host_drive);
            """);
        _ro = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        _ro.Open();
    }

    public void Dispose()
    {
        lock (_roLock) _ro.Dispose();
        lock (Lock) _c.Dispose();
    }

    // ---------- helpers ----------

    void Exec(string sql, params (string, object?)[] args)
    {
        lock (Lock)
        {
            using var cmd = Cmd(sql, args);
            if (sql.StartsWith("UPDATE items SET drive_id", StringComparison.Ordinal)
                || sql.StartsWith("DELETE FROM items", StringComparison.Ordinal)) ItemsVersion++;
            cmd.ExecuteNonQuery();
        }
    }

    SqliteCommand Cmd(string sql, (string, object?)[] args) => Cmd(_c, sql, args);

    static SqliteCommand Cmd(SqliteConnection conn, string sql, (string, object?)[] args)
    {
        var cmd = conn.CreateCommand();
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

    const string Cols = "lid,drive_id,item_id,parent_lid,name,is_dir,size,mtime,ctime,etag,ctag,hash,cached_ctag,dirty,dirty_gen,pinned,local_only,last_access,mode";

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
        Mode = (uint)r.GetInt64(18),
    };

    static (string, object?)[] Params(Item i) =>
    [
        ("$lid", i.Lid), ("$drive", i.DriveId), ("$item", i.ItemId), ("$parent", i.ParentLid), ("$name", i.Name),
        ("$dir", i.IsDir ? 1 : 0), ("$size", i.Size), ("$mtime", i.MTime), ("$ctime", i.CTime), ("$etag", i.ETag),
        ("$ctag", i.CTag), ("$hash", i.Hash), ("$cached", i.CachedCTag), ("$dirty", i.Dirty ? 1 : 0),
        ("$gen", i.DirtyGen), ("$pinned", i.Pinned ? 1 : 0), ("$local", i.LocalOnly ? 1 : 0), ("$access", i.LastAccess), ("$mode", (long)i.Mode),
    ];

    public void InTransaction(Action action)
    {
        lock (Lock)
        {
            using var tx = _c.BeginTransaction();
            try
            {
                action();
                tx.Commit();
            }
            finally { ItemsVersion++; } // caminhos vistos dentro de uma transação abortada não sobrevivem
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

    /// <summary>
    /// Busca como a nuvem compara nomes: sem diferenciar maiúsculas (o OneDrive não diferencia), inclusive fora do
    /// ASCII ("AÇÃO" = "ação"; o NOCASE do SQLite só dobra A–Z), e com NFC/NFD equivalentes. O nome exato tem prioridade.
    /// </summary>
    public Item? GetChildNoCase(long parentLid, string name) =>
        Items(System.Text.Ascii.IsValid(name)
                ? $"SELECT {Cols} FROM items WHERE parent_lid=$p AND name=$n COLLATE NOCASE"
                : $"SELECT {Cols} FROM items WHERE parent_lid=$p AND (name=$n COLLATE NOCASE OR name GLOB '*[^ -~]*')",
                ("$p", parentLid), ("$n", name))
            .OrderByDescending(i => i.Name == name).FirstOrDefault(i => SameCloudName(i.Name, name));

    /// <summary>Nomes que a nuvem trataria como o mesmo: maiúsculas e normalização Unicode (NFC/NFD) não contam.</summary>
    public static bool SameCloudName(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        if (System.Text.Ascii.IsValid(a) && System.Text.Ascii.IsValid(b)) return false;
        try { return string.Equals(a.Normalize(), b.Normalize(), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; } // sequência Unicode inválida: só a comparação direta
    }

    public List<Item> GetChildren(long parentLid) =>
        Items($"SELECT {Cols} FROM items WHERE parent_lid=$p ORDER BY name", ("$p", parentLid));

    public bool HasChildren(long parentLid) =>
        Scalar<long>("SELECT EXISTS(SELECT 1 FROM items WHERE parent_lid=$p)", ("$p", parentLid)) != 0;

    public long Insert(Item i)
    {
        lock (Lock)
        {
            using var cmd = Cmd("""
                INSERT INTO items(drive_id,item_id,parent_lid,name,is_dir,size,mtime,ctime,etag,ctag,hash,cached_ctag,dirty,dirty_gen,pinned,local_only,last_access,mode)
                VALUES($drive,$item,$parent,$name,$dir,$size,$mtime,$ctime,$etag,$ctag,$hash,$cached,$dirty,$gen,$pinned,$local,$access,$mode);
                SELECT last_insert_rowid();
                """, Params(i));
            ItemsVersion++;
            i.Lid = (long)cmd.ExecuteScalar()!;
            return i.Lid;
        }
    }

    public void Update(Item i) => Exec("""
        UPDATE items SET drive_id=$drive,item_id=$item,parent_lid=$parent,name=$name,is_dir=$dir,size=$size,mtime=$mtime,
          ctime=$ctime,etag=$etag,ctag=$ctag,hash=$hash,cached_ctag=$cached,dirty=$dirty,dirty_gen=$gen,pinned=$pinned,
          local_only=$local,last_access=$access,mode=$mode WHERE lid=$lid
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

    /// <summary>
    /// Pastas fora da sincronização seletiva (cópia em memória para o Resolve do FUSE não ir ao banco). Trocada
    /// inteira a cada mudança, então leituras não precisam de lock.
    /// </summary>
    volatile HashSet<long> _excluded = [];

    public bool IsExcluded(long lid) => _excluded.Contains(lid);

    /// <summary>Marca/desmarca a pasta como excluída da sincronização (a subárvore some da montagem).</summary>
    public void SetExcluded(string driveId, long lid, bool excluded)
    {
        lock (Lock)
        {
            if (excluded) Exec("INSERT OR IGNORE INTO excluded(lid, drive_id) VALUES($l, $d)", ("$l", lid), ("$d", driveId));
            else Exec("DELETE FROM excluded WHERE lid=$l", ("$l", lid));
            LoadExcluded();
            ItemsVersion++; // caminhos já resolvidos podem ter mudado de visibilidade
        }
    }

    void LoadExcluded()
    {
        using var cmd = Cmd("SELECT lid FROM excluded", []);
        using var r = cmd.ExecuteReader();
        var set = new HashSet<long>();
        while (r.Read()) set.Add(r.GetInt64(0));
        _excluded = set;
    }

    public List<Item> ExcludedFolders(string driveId) =>
        Items($"SELECT {Cols} FROM items WHERE lid IN (SELECT lid FROM excluded WHERE drive_id=$d) ORDER BY lid", ("$d", driveId));

    public bool IsEffectivelyPinned(long lid) => Scalar<long>("""
        WITH RECURSIVE ancestors(lid,parent_lid,pinned) AS (
          SELECT lid,parent_lid,pinned FROM items WHERE lid=$l
          UNION
          SELECT i.lid,i.parent_lid,i.pinned FROM items i JOIN ancestors a ON i.lid=a.parent_lid)
        SELECT EXISTS(SELECT 1 FROM ancestors WHERE pinned=1)
           AND NOT EXISTS(SELECT 1 FROM ancestors WHERE lid IN (SELECT lid FROM excluded))
        """, ("$l", lid)) != 0; // UNION (não ALL) também termina em ciclos no estado

    /// <summary>Arquivos dentro de pastas fixadas que ainda não estão atualizados no cache.</summary>
    public List<Item> PinnedNeedingDownload(string driveId, int limit = 500) => Items($"""
        WITH RECURSIVE p(lid) AS (
          SELECT lid FROM items WHERE drive_id=$d AND pinned=1 AND lid NOT IN (SELECT lid FROM excluded)
          UNION SELECT i.lid FROM items i JOIN p ON i.parent_lid=p.lid WHERE i.lid NOT IN (SELECT lid FROM excluded))
        SELECT {Cols} FROM items WHERE lid IN (SELECT lid FROM p) AND is_dir=0 AND dirty=0 AND local_only=0
          AND item_id IS NOT NULL AND (cached_ctag IS NULL OR cached_ctag IS NOT ctag)
        ORDER BY lid LIMIT $n
        """, ("$d", driveId), ("$n", limit));

    /// <summary>Arquivos em cache candidatos a remoção (não sujos), do menos para o mais usado.</summary>
    public List<Item> CachedByLeastRecent() => Items($"""
        SELECT {Cols} FROM items WHERE is_dir=0 AND dirty=0 AND cached_ctag IS NOT NULL ORDER BY last_access ASC
        """);

    public (long files, long dirs, long cached, long dirty) Counts(string driveId)
    {
        lock (_roLock)
        {
            using var cmd = Cmd(_ro, """
                SELECT COALESCE(SUM(is_dir=0),0), COALESCE(SUM(is_dir=1),0), COALESCE(SUM(cached_ctag IS NOT NULL),0), COALESCE(SUM(dirty),0)
                FROM items WHERE drive_id=$d
                """, [("$d", driveId)]);
            using var r = cmd.ExecuteReader();
            r.Read();
            return (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
        }
    }

    public List<Item> AllPinned()
    {
        lock (_roLock)
        {
            using var cmd = Cmd(_ro, $"SELECT {Cols} FROM items WHERE pinned=1", []);
            using var r = cmd.ExecuteReader();
            var list = new List<Item>();
            while (r.Read()) list.Add(Read(r));
            return list;
        }
    }

    public void DeleteDriveData(string driveId) => InTransaction(() => DeleteDriveRows(driveId));

    /// <summary>Como <see cref="DeleteDriveData"/>, mas dentro da transação de quem chama.</summary>
    public void DeleteDriveRows(string driveId)
    {
        lock (Lock)
        {
            Exec("DELETE FROM items WHERE drive_id=$d", ("$d", driveId));
            Exec("DELETE FROM ops WHERE drive_id=$d", ("$d", driveId));
            Exec("DELETE FROM failed_ops WHERE drive_id=$d", ("$d", driveId));
            Exec("DELETE FROM drives WHERE drive_id=$d", ("$d", driveId));
        }
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

    /// <summary>Exclusões na fila (retidas ou não): id remoto, pai remoto, nome e se era pasta.</summary>
    public List<(string? remoteId, string? parentRemote, string? name, bool isDir)> DeleteOps(string driveId)
    {
        lock (Lock)
        {
            using var cmd = Cmd("SELECT remote_id, parent_remote, name, is_dir FROM ops WHERE drive_id=$d AND kind='delete' ORDER BY id", [("$d", driveId)]);
            using var r = cmd.ExecuteReader();
            var list = new List<(string?, string?, string?, bool)>();
            while (r.Read())
                list.Add((r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2), r.GetInt64(3) != 0));
            return list;
        }
    }

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

    /// <param name="isDir">Exclusão de pasta (para a lista de exclusões retidas).</param>
    /// <param name="runningOp">Operação em execução que não conta para a coalescência: ela já leu o estado do item e
    /// sai da fila ao terminar (ex.: o arquivo mudou durante o envio — precisa de outro envio depois deste).</param>
    public void Enqueue(string driveId, string kind, long lid, string? remoteId = null, string? parentRemote = null, string? name = null, bool isDir = false,
        long runningOp = 0)
    {
        lock (Lock)
        {
            // coalescência: não duplica upload/mkdir/move/settime pendente para o mesmo item
            if (kind is OpKind.Upload or OpKind.Mkdir or OpKind.Move or OpKind.SetTime)
            {
                var exists = Scalar<long>("SELECT COUNT(*) FROM ops WHERE drive_id=$d AND kind=$k AND lid=$l AND attempts=0 AND id<>$r",
                    ("$d", driveId), ("$k", kind), ("$l", lid), ("$r", runningOp));
                if (exists > 0) return;
            }
            Exec("INSERT INTO ops(drive_id,kind,lid,remote_id,created,parent_remote,name,is_dir) VALUES($d,$k,$l,$r,$t,$p,$n,$i)",
                ("$d", driveId), ("$k", kind), ("$l", lid), ("$r", remoteId), ("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                ("$p", parentRemote), ("$n", name), ("$i", isDir ? 1 : 0));
        }
    }

    public Op? PeekOp(string driveId)
    {
        lock (Lock)
        {
            using var cmd = Cmd("SELECT id,drive_id,kind,lid,remote_id,attempts,next_try,last_error,parent_remote,name,created,upload_url,upload_snap,upload_gen,is_dir FROM ops WHERE drive_id=$d ORDER BY id LIMIT 1", [("$d", driveId)]);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new Op
            {
                Id = r.GetInt64(0), DriveId = r.GetString(1), Kind = r.GetString(2), Lid = r.GetInt64(3),
                RemoteId = r.IsDBNull(4) ? null : r.GetString(4), Attempts = (int)r.GetInt64(5), NextTry = r.GetInt64(6),
                LastError = r.IsDBNull(7) ? null : r.GetString(7), ParentRemote = r.IsDBNull(8) ? null : r.GetString(8),
                Name = r.IsDBNull(9) ? null : r.GetString(9), Created = r.GetInt64(10),
                UploadUrl = r.IsDBNull(11) ? null : r.GetString(11), UploadSnapshot = r.IsDBNull(12) ? null : r.GetString(12),
                UploadGen = r.GetInt64(13), IsDir = r.GetInt64(14) != 0,
            };
        }
    }

    public void CompleteOp(long id) => Exec("DELETE FROM ops WHERE id=$i", ("$i", id));

    /// <summary>Guarda o estado de um envio grande para retomá-lo (inclusive depois de reiniciar o app).</summary>
    public void SetOpUpload(long id, string? url, string? snapshot, long gen) =>
        Exec("UPDATE ops SET upload_url=$u, upload_snap=$s, upload_gen=$g WHERE id=$i", ("$u", url), ("$s", snapshot), ("$g", gen), ("$i", id));

    public List<string> UploadSnapshots()
    {
        lock (Lock)
        {
            using var cmd = Cmd("SELECT upload_snap FROM ops WHERE upload_snap IS NOT NULL", []);
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    public void FailOp(long id, string error, TimeSpan retryIn) => Exec(
        "UPDATE ops SET attempts=attempts+1,last_error=$e,next_try=$n WHERE id=$i",
        ("$e", error), ("$n", DateTimeOffset.UtcNow.Add(retryIn).ToUnixTimeMilliseconds()), ("$i", id));

    public void RemoveOpsForLid(long lid)
    {
        lock (Lock)
        {
            Exec("DELETE FROM ops WHERE lid=$l AND kind<>'delete'", ("$l", lid));
            Exec("DELETE FROM failed_ops WHERE lid=$l AND kind<>'delete'", ("$l", lid));
        }
    }

    // ---------- falhas permanentes ----------

    /// <summary>Tira a operação da fila e a guarda como falha (substitui falha anterior do mesmo tipo no item).</summary>
    public void FailPermanently(Op op, string error)
    {
        InTransaction(() =>
        {
            if (op.Lid != 0)
                Exec("DELETE FROM failed_ops WHERE drive_id=$d AND kind=$k AND lid=$l", ("$d", op.DriveId), ("$k", op.Kind), ("$l", op.Lid));
            Exec("""
                INSERT INTO failed_ops(drive_id,kind,lid,remote_id,parent_remote,name,error,at)
                VALUES($d,$k,$l,$r,$p,$n,$e,$t)
                """, ("$d", op.DriveId), ("$k", op.Kind), ("$l", op.Lid), ("$r", op.RemoteId), ("$p", op.ParentRemote),
                ("$n", op.Name ?? Get(op.Lid)?.Name), ("$e", error), ("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            Exec("DELETE FROM ops WHERE id=$i", ("$i", op.Id));
        });
    }

    /// <summary>Operação do mesmo tipo deu certo: a falha anterior do item deixa de valer.</summary>
    public void ClearFailures(long lid, string kind)
    {
        if (lid != 0) Exec("DELETE FROM failed_ops WHERE lid=$l AND kind=$k", ("$l", lid), ("$k", kind));
    }

    public List<FailedOp> Failures(string? driveId = null)
    {
        lock (_roLock) return FailuresOn(_ro, driveId);
    }

    static List<FailedOp> FailuresOn(SqliteConnection conn, string? driveId = null)
    {
        {
            using var cmd = Cmd(conn, "SELECT id,drive_id,kind,lid,remote_id,parent_remote,name,error,at FROM failed_ops" +
                                (driveId != null ? " WHERE drive_id=$d" : "") + " ORDER BY id", [("$d", driveId)]);
            using var r = cmd.ExecuteReader();
            var list = new List<FailedOp>();
            while (r.Read())
                list.Add(new FailedOp(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7), r.GetInt64(8)));
            return list;
        }
    }

    public ConflictRecord AddConflict(string driveId, long lid, string original, string copy, string reason)
    {
        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (Lock)
        {
            using var cmd = Cmd("INSERT INTO conflicts(drive_id,lid,original,copy,reason,at) VALUES($d,$l,$o,$c,$r,$a) RETURNING id",
                [("$d", driveId), ("$l", lid), ("$o", original), ("$c", copy), ("$r", reason), ("$a", at)]);
            return new ConflictRecord((long)cmd.ExecuteScalar()!, driveId, lid, original, copy, reason, at);
        }
    }

    /// <summary>Conflitos registrados, mais recentes primeiro (conexão de leitura: interface).</summary>
    public List<ConflictRecord> Conflicts(string? driveId = null)
    {
        lock (_roLock)
        {
            using var cmd = Cmd(_ro, "SELECT id,drive_id,lid,original,copy,reason,at FROM conflicts" +
                                     (driveId != null ? " WHERE drive_id=$d" : "") + " ORDER BY id DESC",
                driveId != null ? [("$d", driveId)] : []);
            using var r = cmd.ExecuteReader();
            var list = new List<ConflictRecord>();
            while (r.Read())
                list.Add(new ConflictRecord(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4),
                    r.GetString(5), r.GetInt64(6)));
            return list;
        }
    }

    /// <summary>Tira da lista (a cópia continua lá).</summary>
    public void DismissConflict(long id) => Exec("DELETE FROM conflicts WHERE id=$i", ("$i", id));

    public long ConflictCount() => ReadScalar("SELECT COUNT(*) FROM conflicts");

    public void DismissFailure(long id) => Exec("DELETE FROM failed_ops WHERE id=$i", ("$i", id));

    public long FailureCount() => ReadScalar("SELECT COUNT(*) FROM failed_ops");

    long ReadScalar(string sql, params (string, object?)[] args)
    {
        lock (_roLock)
        {
            using var cmd = Cmd(_ro, sql, args);
            return cmd.ExecuteScalar() is long n ? n : 0;
        }
    }

    public bool HasFailure(long lid) => Scalar<long>("SELECT EXISTS(SELECT 1 FROM failed_ops WHERE lid=$l)", ("$l", lid)) != 0;

    /// <summary>Devolve a operação à fila. Retorna a drive dela, ou null se a falha não existe mais.</summary>
    public string? RetryFailure(long id)
    {
        string? drive = null;
        InTransaction(() =>
        {
            var f = FailuresOn(_c).FirstOrDefault(x => x.Id == id); // conexão principal: dentro da transação
            if (f == null) return;
            drive = f.DriveId;
            Exec("DELETE FROM failed_ops WHERE id=$i", ("$i", id));
            Exec("INSERT INTO ops(drive_id,kind,lid,remote_id,created,parent_remote,name) VALUES($d,$k,$l,$r,$t,$p,$n)",
                ("$d", f.DriveId), ("$k", f.Kind), ("$l", f.Lid), ("$r", f.RemoteId), ("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                ("$p", f.ParentRemote), ("$n", f.Name));
        });
        return drive;
    }

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

    public long PendingOps(string? driveId = null) => driveId == null
        ? ReadScalar("SELECT COUNT(*) FROM ops")
        : ReadScalar("SELECT COUNT(*) FROM ops WHERE drive_id=$d", ("$d", driveId));

    // ---------- itens de outras drives (atalhos / compartilhados comigo) ----------

    public void SaveSharedRoot(SharedRoot r) => Exec("""
        INSERT INTO shared_roots(key,host_drive,source,host_item,remote_drive,remote_item,root_lid,writable)
        VALUES($k,$h,$s,$hi,$rd,$ri,$l,$w)
        ON CONFLICT(key) DO UPDATE SET host_drive=$h,source=$s,host_item=$hi,remote_drive=$rd,remote_item=$ri,root_lid=$l,writable=$w
        """, ("$k", r.Key), ("$h", r.HostDriveId), ("$s", r.Source), ("$hi", r.HostItemId), ("$rd", r.RemoteDriveId),
        ("$ri", r.RemoteItemId), ("$l", r.RootLid), ("$w", r.Writable ? 1 : 0));

    public void DeleteSharedRoot(string key) => Exec("DELETE FROM shared_roots WHERE key=$k", ("$k", key));

    /// <summary>Raízes compartilhadas (de uma drive hospedeira, ou todas).</summary>
    public List<SharedRoot> SharedRoots(string? hostDriveId = null)
    {
        lock (Lock)
        {
            using var cmd = Cmd("SELECT key,host_drive,source,host_item,remote_drive,remote_item,root_lid,writable FROM shared_roots" +
                                (hostDriveId != null ? " WHERE host_drive=$h" : "") + " ORDER BY key", [("$h", hostDriveId)]);
            using var r = cmd.ExecuteReader();
            var list = new List<SharedRoot>();
            while (r.Read())
                list.Add(new SharedRoot(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                    r.GetString(4), r.GetString(5), r.GetInt64(6), r.GetInt64(7) != 0));
            return list;
        }
    }

    // ---------- Lixeira: itens excluídos recentemente (para restaurar) ----------

    bool _trashReady;

    /// <summary>Tabela criada sob demanda (não mexe nas migrações do construtor); sempre sob <see cref="Lock"/>.</summary>
    void EnsureTrash()
    {
        if (_trashReady) return;
        using (var cmd = Cmd("""
            CREATE TABLE IF NOT EXISTS trash(
              id INTEGER PRIMARY KEY AUTOINCREMENT, drive_id TEXT NOT NULL, item_id TEXT NOT NULL,
              name TEXT NOT NULL, is_dir INTEGER NOT NULL DEFAULT 0, at INTEGER NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_trash_remote ON trash(drive_id, item_id);
            """, [])) cmd.ExecuteNonQuery();
        _trashReady = true;
    }

    /// <summary>Quantos itens excluídos cada drive guarda para "restaurar da Lixeira" (os mais antigos saem).</summary>
    public const int TrashKeep = 200;

    /// <summary>Registra (ou atualiza a data de) um item que foi para a Lixeira da nuvem.</summary>
    public void AddTrash(string driveId, string itemId, string name, bool isDir)
    {
        lock (Lock)
        {
            EnsureTrash();
            using (var cmd = Cmd("""
                INSERT INTO trash(drive_id,item_id,name,is_dir,at) VALUES($d,$i,$n,$k,$a)
                ON CONFLICT(drive_id,item_id) DO UPDATE SET name=excluded.name, is_dir=excluded.is_dir, at=excluded.at
                """, [("$d", driveId), ("$i", itemId), ("$n", name), ("$k", isDir ? 1 : 0), ("$a", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())]))
                cmd.ExecuteNonQuery();
            using var prune = Cmd("DELETE FROM trash WHERE drive_id=$d AND id NOT IN (SELECT id FROM trash WHERE drive_id=$d ORDER BY at DESC, id DESC LIMIT $k)",
                [("$d", driveId), ("$k", TrashKeep)]);
            prune.ExecuteNonQuery();
        }
    }

    /// <summary>Excluídos recentes, mais novos primeiro.</summary>
    public List<TrashRecord> Trash(string? driveId = null)
    {
        lock (Lock)
        {
            EnsureTrash();
            using var cmd = Cmd("SELECT id,drive_id,item_id,name,is_dir,at FROM trash" + (driveId != null ? " WHERE drive_id=$d" : "") + " ORDER BY at DESC, id DESC",
                driveId != null ? [("$d", driveId)] : []);
            using var r = cmd.ExecuteReader();
            var list = new List<TrashRecord>();
            while (r.Read()) list.Add(new TrashRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4) != 0, r.GetInt64(5)));
            return list;
        }
    }

    public void RemoveTrash(long id)
    {
        lock (Lock)
        {
            EnsureTrash();
            using var cmd = Cmd("DELETE FROM trash WHERE id=$i", [("$i", id)]);
            cmd.ExecuteNonQuery();
        }
    }
}

/// <summary>Item que o craftDriver viu ir para a Lixeira da nuvem (excluído aqui ou em outro dispositivo).</summary>
public sealed record TrashRecord(long Id, string DriveId, string ItemId, string Name, bool IsDir, long At);
