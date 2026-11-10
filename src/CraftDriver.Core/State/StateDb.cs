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
    /// <summary>Exclusão de arquivo: eTag que o item tinha aqui (If-Match: não apaga versão mais nova de outro dispositivo).</summary>
    public string? ETag;
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
public sealed partial class StateDb : IDisposable
{
    readonly SqliteConnection _c;
    /// <summary>
    /// Conexão só de leitura para consultas da interface/status (contagens, problemas, fixados). Com WAL ela lê o
    /// último estado confirmado sem esperar o <see cref="Lock"/>, que o FUSE e o motor seguram em transações.
    /// </summary>
    readonly SqliteConnection _ro;
    readonly string _path;
    readonly object _roLock = new();
    public object Lock { get; } = new();
    /// <summary>Geração para caches de resolução; ler sob Lock. Inclui rollback.</summary>
    public long ItemsVersion { get; private set; }

    public StateDb(string path)
    {
        _path = path;
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
        // migrações de esquema: a coluna é conferida antes (PRAGMA table_info); qualquer erro do ALTER (disco cheio,
        // banco só-leitura, E/S) sobe — engolir como "já existe" só adiava a falha para o primeiro SELECT
        foreach (var col in new[] { "parent_remote TEXT", "name TEXT", "upload_url TEXT", "upload_snap TEXT", "upload_gen INTEGER NOT NULL DEFAULT 0", "is_dir INTEGER NOT NULL DEFAULT 0", "etag TEXT" })
            AddColumnIfMissing("ops", col);
        Exec("CREATE INDEX IF NOT EXISTS ix_ops_remote ON ops(drive_id, kind, remote_id)");
        // reserva persistente da fila (uploads paralelos): 'pending' ou 'running'
        AddColumnIfMissing("ops", "state TEXT NOT NULL DEFAULT 'pending'");
        Exec("CREATE INDEX IF NOT EXISTS ix_ops_claim ON ops(drive_id,state,next_try,id)");
        AddColumnIfMissing("items", "mode INTEGER NOT NULL DEFAULT 0");
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
        AddColumnIfMissing("failed_ops", "is_dir INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("failed_ops", "etag TEXT");
        foreach (var col in new[] { "delete_hold INTEGER NOT NULL DEFAULT 0", "held_count INTEGER NOT NULL DEFAULT 0" })
            AddColumnIfMissing("drives", col);
        // índices das consultas frequentes (fila por item, contagens por drive, fixados, LRU do cache, Problemas/Conflitos)
        Exec("""
            CREATE INDEX IF NOT EXISTS ix_ops_lid ON ops(lid, kind);
            CREATE INDEX IF NOT EXISTS ix_items_drive ON items(drive_id);
            CREATE INDEX IF NOT EXISTS ix_items_pinned ON items(pinned) WHERE pinned=1;
            CREATE INDEX IF NOT EXISTS ix_items_lru ON items(last_access) WHERE cached_ctag IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_failed_drive ON failed_ops(drive_id);
            CREATE INDEX IF NOT EXISTS ix_conflicts_drive ON conflicts(drive_id);
            CREATE INDEX IF NOT EXISTS ix_excluded_drive ON excluded(drive_id);
            """);
        EnsureTrash(); // aqui (e não sob demanda): a Lixeira é lida pela conexão só de leitura, que não cria tabela
        InicializarVfs();
        InicializarCopiasOffline();
        LoadExcluded();
        Exec("""
            CREATE TABLE IF NOT EXISTS shared_roots(
              key TEXT PRIMARY KEY, host_drive TEXT NOT NULL, source TEXT NOT NULL, host_item TEXT,
              remote_drive TEXT NOT NULL, remote_item TEXT NOT NULL, root_lid INTEGER NOT NULL,
              writable INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_shared_host ON shared_roots(host_drive);
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS upload_delta(drive_id TEXT NOT NULL,lid INTEGER NOT NULL,item_id TEXT NOT NULL,data TEXT NOT NULL,PRIMARY KEY(drive_id,lid,item_id));
            CREATE TABLE IF NOT EXISTS delta_progress(drive_id TEXT PRIMARY KEY,next_link TEXT,deferred TEXT NOT NULL DEFAULT '[]');
            CREATE TABLE IF NOT EXISTS delta_seen(drive_id TEXT NOT NULL,item_id TEXT NOT NULL,PRIMARY KEY(drive_id,item_id));
            CREATE TABLE IF NOT EXISTS delta_candidates(drive_id TEXT NOT NULL,lid INTEGER NOT NULL,PRIMARY KEY(drive_id,lid));
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

    void AddColumnIfMissing(string table, string columnDef)
    {
        var name = columnDef.Split(' ')[0];
        var exists = false;
        using (var cmd = Cmd($"SELECT 1 FROM pragma_table_info('{table}') WHERE name=$n", [("$n", name)]))
            exists = cmd.ExecuteScalar() != null;
        if (!exists) Exec($"ALTER TABLE {table} ADD COLUMN {columnDef}");
    }

    void Exec(string sql, params (string, object?)[] args)
    {
        lock (Lock)
        {
            using var cmd = Cmd(sql, args);
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
        ("$lid", i.Lid), ("$drive", i.DriveId), ("$item", i.ItemId), ("$parent", i.ParentLid), ("$name", i.Name), ("$namekey", ChaveNome(i.Name)),
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
            catch
            {
                MudouCaminhos(); // caminhos vistos dentro de uma transação abortada não sobrevivem
                throw;
            }
            // confirmada: cada Insert/Update/Delete de items já incrementou; sem mudança, o cache de caminhos continua válido
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
        Items($"SELECT {Cols} FROM items WHERE parent_lid=$p AND name_key=$k",
                ("$p", parentLid), ("$k", ChaveNome(name)))
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

    public long Insert(Item i)
    {
        lock (Lock)
        {
            using var cmd = Cmd("""
                INSERT INTO items(drive_id,item_id,parent_lid,name,is_dir,size,mtime,ctime,etag,ctag,hash,cached_ctag,dirty,dirty_gen,pinned,local_only,last_access,mode,name_key)
                VALUES($drive,$item,$parent,$name,$dir,$size,$mtime,$ctime,$etag,$ctag,$hash,$cached,$dirty,$gen,$pinned,$local,$access,$mode,$namekey);
                SELECT last_insert_rowid();
                """, Params(i));
            MudouCaminhos();
            i.Lid = (long)cmd.ExecuteScalar()!;
            AvisarItem(null, i);
            return i.Lid;
        }
    }

    public void Update(Item i)
    {
        lock (Lock)
        {
            // Geração dos caches só sobe se mudou o que eles usam (§24.5): caminhos = pasta, nome, tipo, drive;
            // fixados = também a marca de fixação. write() (MarkDirty), confirmação de envio e LRU não invalidam mais.
            var antes = Get(i.Lid);
            if (antes == null || antes.ParentLid != i.ParentLid || antes.Name != i.Name || antes.IsDir != i.IsDir
                || antes.DriveId != i.DriveId) MudouCaminhos();
            else if (antes.Pinned != i.Pinned) _geracaoFixados++;
            var p = Params(i);
            Exec("""
        UPDATE items SET drive_id=$drive,item_id=$item,parent_lid=$parent,name=$name,is_dir=$dir,size=$size,mtime=$mtime,
          ctime=$ctime,etag=$etag,ctag=$ctag,hash=$hash,cached_ctag=$cached,dirty=$dirty,dirty_gen=$gen,pinned=$pinned,
          local_only=$local,last_access=$access,mode=$mode,name_key=$namekey WHERE lid=$lid
        """, p);
            if (DeveNotificar && antes != null && (antes.ParentLid != i.ParentLid || antes.Name != i.Name
                || antes.CTag != i.CTag || antes.Size != i.Size || antes.MTime != i.MTime) && !ConfirmacaoDeEnvio(antes, i)) AvisarItem(antes, i);
        }
    }

    public void Touch(long lid) =>
        Exec("UPDATE items SET last_access=$t WHERE lid=$l", ("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("$l", lid));

    public void Delete(long lid)
    {
        lock (Lock)
        {
            var antes = !DeveNotificar ? null : Get(lid);
            Exec("DELETE FROM items WHERE lid=$l", ("$l", lid));
            DefinirFracaoMtime(lid, 0, 0);
            if (antes != null) AvisarItem(antes, null);
        }
    }

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
            MudouCaminhos(); // caminhos já resolvidos podem ter mudado de visibilidade
            // §24.6: o kernel descobre na hora (sem esperar o timeout): excluir = a pasta "some" (notify_delete do
            // caminho antigo); reincluir = o nome passa a existir (invalida um cache negativo).
            if (DeveNotificar && Get(lid) is { } pasta) AvisarItem(excluded ? pasta : null, pasta);
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

    public void DeleteDriveData(string driveId) => InTransaction(() =>
    {
        // sub-drives e pasta virtual pertencem à drive hospedeira: não deixar metadados nem avisos órfãos
        foreach (var sr in SharedRoots(driveId))
        {
            DeleteDriveRows(sr.Key);
            DeleteSharedRoot(sr.Key);
        }
        DeleteDriveRows(driveId + "|comigo");
        DeleteDriveRows(driveId);
    });

    /// <summary>Como <see cref="DeleteDriveData"/>, mas dentro da transação de quem chama.</summary>
    public void DeleteDriveRows(string driveId)
    {
        lock (Lock)
        {
            Exec("DELETE FROM items WHERE drive_id=$d", ("$d", driveId));
            Exec("DELETE FROM ops WHERE drive_id=$d", ("$d", driveId));
            Exec("DELETE FROM failed_ops WHERE drive_id=$d", ("$d", driveId));
            ClearDeltaProgress(driveId);
            Exec("DELETE FROM upload_delta WHERE drive_id=$d", ("$d", driveId));
            Exec("DELETE FROM drives WHERE drive_id=$d", ("$d", driveId));
            Exec("DELETE FROM conflicts WHERE drive_id=$d", ("$d", driveId));
            Exec("DELETE FROM trash WHERE drive_id=$d", ("$d", driveId));
            Exec("DELETE FROM excluded WHERE drive_id=$d", ("$d", driveId));
            LoadExcluded();
            MudouCaminhos();
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

    public void SetDeltaLink(string driveId, string? link)
    {
        InTransaction(() =>
        {
            Exec("UPDATE drives SET delta_link=$l WHERE drive_id=$d", ("$l", link), ("$d", driveId));
            ClearDeltaProgress(driveId);
        });
    }

    /// <summary>Enumeração completa em andamento: nextLink opaco, filhos adiados, ids já vistos e candidatos ao Sweep.</summary>
    public (string? Link, List<CraftDriver.Core.Graph.RemoteItem> Deferred, HashSet<string> Seen, HashSet<long> Candidates)? DeltaProgress(string driveId)
    {
        lock (Lock)
        {
            string? link;
            string deferred;
            using (var cmd = Cmd("SELECT next_link,deferred FROM delta_progress WHERE drive_id=$d", [("$d", driveId)]))
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) return null;
                link = r.IsDBNull(0) ? null : r.GetString(0);
                deferred = r.GetString(1);
            }
            var seen = new HashSet<string>();
            using (var cmd = Cmd("SELECT item_id FROM delta_seen WHERE drive_id=$d", [("$d", driveId)]))
            using (var r = cmd.ExecuteReader()) while (r.Read()) seen.Add(r.GetString(0));
            var candidates = new HashSet<long>();
            using (var cmd = Cmd("SELECT lid FROM delta_candidates WHERE drive_id=$d", [("$d", driveId)]))
            using (var r = cmd.ExecuteReader()) while (r.Read()) candidates.Add(r.GetInt64(0));
            return (link, System.Text.Json.JsonSerializer.Deserialize(deferred, CraftDriver.Core.Config.DeltaJsonContext.Default.ListRemoteItem) ?? [], seen, candidates);
        }
    }

    /// <summary>Inicia uma enumeração completa e congela os candidatos ao Sweep (uploads novos não são ausências).</summary>
    public void BeginDeltaProgress(string driveId)
    {
        InTransaction(() =>
        {
            ClearDeltaProgress(driveId);
            Exec("INSERT INTO delta_progress(drive_id) VALUES($d)", ("$d", driveId));
            Exec("INSERT INTO delta_candidates(drive_id,lid) SELECT drive_id,lid FROM items WHERE drive_id=$d AND item_id IS NOT NULL", ("$d", driveId));
        });
    }

    public void SaveDeltaProgress(string driveId, string? link, IEnumerable<string> seen, List<CraftDriver.Core.Graph.RemoteItem> deferred)
    {
        InTransaction(() =>
        {
            foreach (var id in seen)
                Exec("INSERT OR IGNORE INTO delta_seen(drive_id,item_id) VALUES($d,$i)", ("$d", driveId), ("$i", id));
            var text = System.Text.Json.JsonSerializer.Serialize(deferred, CraftDriver.Core.Config.DeltaJsonContext.Default.ListRemoteItem);
            Exec("UPDATE delta_progress SET next_link=$n,deferred=$f WHERE drive_id=$d", ("$n", link), ("$f", text), ("$d", driveId));
        });
    }

    /// <summary>Delta recebido durante um upload: guarda antes de avançar o deltaLink (sobrevive a reinício).</summary>
    public void DeferUploadDelta(string driveId, long lid, CraftDriver.Core.Graph.RemoteItem remote)
    {
        var data = System.Text.Json.JsonSerializer.Serialize(remote, CraftDriver.Core.Config.DeltaJsonContext.Default.RemoteItem);
        Exec("INSERT INTO upload_delta(drive_id,lid,item_id,data) VALUES($d,$l,$i,$j) ON CONFLICT(drive_id,lid,item_id) DO UPDATE SET data=excluded.data",
            ("$d", driveId), ("$l", lid), ("$i", remote.Id), ("$j", data));
    }

    public List<(long Lid, CraftDriver.Core.Graph.RemoteItem Remote)> DeferredUploadDeltas(string driveId, long? lid = null)
    {
        lock (Lock)
        {
            using var cmd = Cmd("SELECT lid,data FROM upload_delta WHERE drive_id=$d" + (lid != null ? " AND lid=$l" : ""), [("$d", driveId), ("$l", lid)]);
            using var reader = cmd.ExecuteReader();
            var result = new List<(long, CraftDriver.Core.Graph.RemoteItem)>();
            while (reader.Read())
                result.Add((reader.GetInt64(0), System.Text.Json.JsonSerializer.Deserialize(reader.GetString(1), CraftDriver.Core.Config.DeltaJsonContext.Default.RemoteItem)!));
            return result;
        }
    }

    public void CompleteUploadDelta(string driveId, long lid, string itemId) =>
        Exec("DELETE FROM upload_delta WHERE drive_id=$d AND lid=$l AND item_id=$i", ("$d", driveId), ("$l", lid), ("$i", itemId));

    /// <summary>Item confirmado por upload durante a enumeração: não é candidato à limpeza daquela enumeração.</summary>
    public void ProtectDeltaUpload(string driveId, long lid) =>
        Exec("DELETE FROM delta_candidates WHERE drive_id=$d AND lid=$l", ("$d", driveId), ("$l", lid));

    void ClearDeltaProgress(string driveId)
    {
        Exec("DELETE FROM delta_progress WHERE drive_id=$d", ("$d", driveId));
        Exec("DELETE FROM delta_seen WHERE drive_id=$d", ("$d", driveId));
        Exec("DELETE FROM delta_candidates WHERE drive_id=$d", ("$d", driveId));
    }

    // ---------- fila de operações ----------

    /// <param name="isDir">Exclusão de pasta (para a lista de exclusões retidas).</param>
    /// <param name="runningOp">Operação em execução que não conta para a coalescência: ela já leu o estado do item e
    /// sai da fila ao terminar (ex.: o arquivo mudou durante o envio — precisa de outro envio depois deste).</param>
    /// <param name="etag">Exclusão: eTag para o If-Match; sem ele, o de um arquivo ainda no banco (pasta não leva: o eTag dela
    /// muda com os filhos).</param>
    public void Enqueue(string driveId, string kind, long lid, string? remoteId = null, string? parentRemote = null, string? name = null, bool isDir = false,
        long runningOp = 0, string? etag = null)
    {
        lock (Lock)
        {
            if (kind == OpKind.Delete && etag == null && lid != 0 && remoteId != null)
                etag = Scalar<string>("SELECT etag FROM items WHERE lid=$l AND is_dir=0 AND item_id=$r", ("$l", lid), ("$r", remoteId));
            // coalescência: não duplica upload/mkdir/move/settime pendente para o mesmo item
            if (kind is OpKind.Upload or OpKind.Mkdir or OpKind.Move or OpKind.SetTime)
            {
                var exists = Scalar<long>("SELECT COUNT(*) FROM ops WHERE drive_id=$d AND kind=$k AND lid=$l AND attempts=0 AND state='pending' AND id<>$r",
                    ("$d", driveId), ("$k", kind), ("$l", lid), ("$r", runningOp));
                if (exists > 0) return;
            }
            Exec("INSERT INTO ops(drive_id,kind,lid,remote_id,created,parent_remote,name,is_dir,etag) VALUES($d,$k,$l,$r,$t,$p,$n,$i,$e)",
                ("$d", driveId), ("$k", kind), ("$l", lid), ("$r", remoteId), ("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                ("$p", parentRemote), ("$n", name), ("$i", isDir ? 1 : 0), ("$e", kind == OpKind.Delete ? etag : null));
        }
    }

    public Op? PeekOp(string driveId)
    {
        lock (Lock)
        {
            using var cmd = Cmd("SELECT id,drive_id,kind,lid,remote_id,attempts,next_try,last_error,parent_remote,name,created,upload_url,upload_snap,upload_gen,is_dir,etag FROM ops WHERE drive_id=$d ORDER BY id LIMIT 1", [("$d", driveId)]);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new Op
            {
                Id = r.GetInt64(0), DriveId = r.GetString(1), Kind = r.GetString(2), Lid = r.GetInt64(3),
                RemoteId = r.IsDBNull(4) ? null : r.GetString(4), Attempts = (int)r.GetInt64(5), NextTry = r.GetInt64(6),
                LastError = r.IsDBNull(7) ? null : r.GetString(7), ParentRemote = r.IsDBNull(8) ? null : r.GetString(8),
                Name = r.IsDBNull(9) ? null : r.GetString(9), Created = r.GetInt64(10),
                UploadUrl = r.IsDBNull(11) ? null : r.GetString(11), UploadSnapshot = r.IsDBNull(12) ? null : r.GetString(12),
                UploadGen = r.GetInt64(13), IsDir = r.GetInt64(14) != 0, ETag = r.IsDBNull(15) ? null : r.GetString(15),
            };
        }
    }

    /// <summary>Reserva até <paramref name="count"/> operações prontas, sem ultrapassar dependências.</summary>
    public List<Op> ClaimOps(string driveId, int count, long now, long uploadBefore = long.MaxValue,
        long deleteBefore = long.MaxValue, bool allowDeletes = true)
    {
        lock (Lock)
        {
            var picked = new List<Op>();
            // Só uploads são paralelos. Operações de metadados/exclusões são barreiras curtas: não podem excluir
            // uma pasta enquanto um upload ainda a usa, nem mover um pai no meio do envio de um filho.
            var running = Scalar<long>("SELECT COUNT(*) FROM ops WHERE drive_id=$d AND state='running'", ("$d", driveId));
            if (Scalar<long>("SELECT COUNT(*) FROM ops WHERE drive_id=$d AND state='running' AND kind<>'upload'", ("$d", driveId)) > 0)
                return picked;
            var candidates = new List<long>();
            using (var cmd = Cmd("""
                SELECT o.id FROM ops o WHERE o.drive_id=$d AND o.state='pending' AND o.next_try<=$t
                AND (o.kind<>'upload' OR o.created<=$u)
                AND (o.kind<>'delete' OR ($allow=1 AND o.created<=$del))
                AND NOT EXISTS(SELECT 1 FROM ops prev WHERE prev.drive_id=o.drive_id AND prev.lid=o.lid
                    AND o.lid<>0 AND prev.id<o.id)
                ORDER BY o.id
                """, [("$d", driveId), ("$t", now), ("$u", uploadBefore), ("$allow", allowDeletes ? 1 : 0), ("$del", deleteBefore)]))
            using (var r = cmd.ExecuteReader()) while (r.Read()) candidates.Add(r.GetInt64(0));
            foreach (var id in candidates)
            {
                if (picked.Count >= count) break;
                var op = ReadOp(id);
                if (op == null) continue;
                if (op.Lid != 0 && op.Kind != OpKind.Delete)
                {
                    // Ancestrais sem id remoto esperam o mkdir pendente/em execução. Se o mkdir foi descartado,
                    // deixa o filho entrar: ParentRemoteId o reenfileira (ou falha se estiver em Problemas).
                    var item = Get(op.Lid);
                    var visited = new HashSet<long>();
                    var blocked = false;
                    for (var lid = item?.ParentLid ?? 0; lid != 0 && visited.Add(lid);)
                    {
                        var parent = Get(lid);
                        if (parent == null) break;
                        if (parent.ItemId == null && PendingOpsFor(lid, OpKind.Mkdir)) { blocked = true; break; }
                        lid = parent.ParentLid;
                    }
                    if (blocked) continue;
                    // Ocupa um nome que uma exclusão anterior ainda vai liberar (mv sobre destino; rm + criar de novo):
                    // espera ela. Move/mkdir/envio de item novo vão com conflictBehavior=fail — passar à frente da
                    // exclusão (que aguarda o período de silêncio) virava 409 e cópia de conflito.
                    if (op.Kind == OpKind.Move || op.Kind == OpKind.Mkdir || (op.Kind == OpKind.Upload && item?.ItemId == null))
                        if (item != null && Get(item.ParentLid)?.ItemId is { } parentRemote
                            && NomeRetidoNaNuvem(driveId, op.Lid, parentRemote, item.Name))
                            continue;
                }
                if (op.Kind != OpKind.Upload)
                {
                    // Barreira justa: drena uploads já iniciados, sem deixar uploads mais novos ultrapassarem
                    // indefinidamente um metadado pronto (rename/mkdir/delete). Exclusão só espera envio que ainda não
                    // resolveu a pasta de destino: os demais gravam fora da subárvore excluída (a linha da pasta de destino
                    // existe) ou dentro dela — esses o motor cancela antes de excluir (§23.5).
                    if ((op.Kind == OpKind.Delete ? RunningUploadsWithoutTarget(driveId) : running) + picked.Count > 0) break;
                    Exec("UPDATE ops SET state='running' WHERE id=$i", ("$i", id));
                    picked.Add(op);
                    break;
                }
                // pasta de destino é resolvida de novo pelo envio (RunningUploadsWithoutTarget)
                Exec("UPDATE ops SET state='running', parent_remote=NULL WHERE id=$i", ("$i", id));
                picked.Add(op);
            }
            return picked;
        }
    }

    Op? ReadOp(long id)
    {
        using var cmd = Cmd("SELECT id,drive_id,kind,lid,remote_id,attempts,next_try,last_error,parent_remote,name,created,upload_url,upload_snap,upload_gen,is_dir,etag FROM ops WHERE id=$i", [("$i", id)]);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new Op
        {
            Id = r.GetInt64(0), DriveId = r.GetString(1), Kind = r.GetString(2), Lid = r.GetInt64(3),
            RemoteId = r.IsDBNull(4) ? null : r.GetString(4), Attempts = (int)r.GetInt64(5), NextTry = r.GetInt64(6),
            LastError = r.IsDBNull(7) ? null : r.GetString(7), ParentRemote = r.IsDBNull(8) ? null : r.GetString(8),
            Name = r.IsDBNull(9) ? null : r.GetString(9), Created = r.GetInt64(10),
            UploadUrl = r.IsDBNull(11) ? null : r.GetString(11), UploadSnapshot = r.IsDBNull(12) ? null : r.GetString(12),
            UploadGen = r.GetInt64(13), IsDir = r.GetInt64(14) != 0, ETag = r.IsDBNull(15) ? null : r.GetString(15),
        };
    }

    /// <summary>Reinício da drive: operações interrompidas voltam para pendentes (sessões e snapshots permanecem).</summary>
    public void ResetRunningOps(string driveId) =>
        Exec("UPDATE ops SET state='pending' WHERE drive_id=$d AND state='running'", ("$d", driveId));

    /// <summary>Devolve uma reserva à fila sem contar erro (cancelamento ou estado local ajustado).</summary>
    public void ReleaseOp(long id) => Exec("UPDATE ops SET state='pending' WHERE id=$i", ("$i", id));

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
        "UPDATE ops SET state='pending',attempts=attempts+1,last_error=$e,next_try=$n WHERE id=$i",
        ("$e", error), ("$n", DateTimeOffset.UtcNow.Add(retryIn).ToUnixTimeMilliseconds()), ("$i", id));

    public void RemoveOpsForLid(long lid)
    {
        lock (Lock)
        {
            Exec("DELETE FROM ops WHERE lid=$l AND kind<>'delete' AND state<>'running'", ("$l", lid));
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
            else if (op.RemoteId != null) // exclusão de item que já saiu do banco (lid 0): a mesma é o mesmo id remoto
                Exec("DELETE FROM failed_ops WHERE drive_id=$d AND kind=$k AND lid=0 AND remote_id=$r", ("$d", op.DriveId), ("$k", op.Kind), ("$r", op.RemoteId));
            Exec("""
                INSERT INTO failed_ops(drive_id,kind,lid,remote_id,parent_remote,name,error,at,is_dir,etag)
                VALUES($d,$k,$l,$r,$p,$n,$e,$t,$i,$tag)
                """, ("$d", op.DriveId), ("$k", op.Kind), ("$l", op.Lid), ("$r", op.RemoteId), ("$p", op.ParentRemote),
                ("$n", op.Name ?? Get(op.Lid)?.Name), ("$e", error), ("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("$i", op.IsDir ? 1 : 0), ("$tag", op.ETag));
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
            // is_dir volta junto: exclusão de pasta que sai de Problemas continua pasta (HeldDeletes/Lixeira)
            Exec("""
                INSERT INTO ops(drive_id,kind,lid,remote_id,created,parent_remote,name,is_dir,etag)
                SELECT drive_id,kind,lid,remote_id,$t,parent_remote,name,is_dir,etag FROM failed_ops WHERE id=$i
                """, ("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("$i", id));
            Exec("DELETE FROM failed_ops WHERE id=$i", ("$i", id));
        });
        return drive;
    }

    /// <summary>Há exclusão pendente para este item remoto? (a exclusão da pasta cobre os filhos)</summary>
    public bool PendingDeleteFor(string driveId, string remoteId) =>
        Scalar<long>("SELECT EXISTS(SELECT 1 FROM ops WHERE drive_id=$d AND kind='delete' AND remote_id=$r)",
            ("$d", driveId), ("$r", remoteId)) != 0;

    /// <summary>Envio reservado/em execução (o delta não pode sobrescrever seu estado antes do commit).</summary>
    public bool HasRunningUpload(long lid) => Scalar<long>(
        "SELECT EXISTS(SELECT 1 FROM ops WHERE lid=$l AND kind='upload' AND state='running')", ("$l", lid)) != 0;

    public bool PendingOpsFor(long lid, string kind) =>
        Scalar<long>("SELECT EXISTS(SELECT 1 FROM ops WHERE lid=$l AND kind=$k)", ("$l", lid), ("$k", kind)) != 0;

    /// <summary>
    /// Arquivos com alterações locais sem envio na fila nem em Problemas: o fd de escrita estava aberto quando o
    /// app encerrou/caiu (o envio só entra na fila no release). (drive, lid).
    /// </summary>
    public List<(string driveId, long lid)> DirtyWithoutUpload()
    {
        lock (Lock)
        {
            using var cmd = Cmd("""
                SELECT drive_id, lid FROM items i WHERE dirty=1 AND local_only=0 AND is_dir=0
                  AND NOT EXISTS(SELECT 1 FROM ops o WHERE o.lid=i.lid AND o.kind='upload')
                  AND NOT EXISTS(SELECT 1 FROM failed_ops f WHERE f.lid=i.lid AND f.kind='upload')
                ORDER BY lid
                """, []);
            using var r = cmd.ExecuteReader();
            var list = new List<(string, long)>();
            while (r.Read()) list.Add((r.GetString(0), r.GetInt64(1)));
            return list;
        }
    }

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
        lock (_roLock) // conexão de leitura: a janela da Lixeira não espera o Db.Lock do motor/FUSE
        {
            using var cmd = Cmd(_ro, "SELECT id,drive_id,item_id,name,is_dir,at FROM trash" + (driveId != null ? " WHERE drive_id=$d" : "") + " ORDER BY at DESC, id DESC",
                driveId != null ? [("$d", driveId)] : []);
            using var r = cmd.ExecuteReader();
            var list = new List<TrashRecord>();
            while (r.Read()) list.Add(new TrashRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4) != 0, r.GetInt64(5)));
            return list;
        }
    }

    public void RemoveTrash(long id)
    {
        // _ro é ReadOnly e não pode fazer DELETE. Escrita curta em conexão própria: não bloqueia a leitura da UI nem
        // espera o monitor Db.Lock; ainda respeita a serialização de escritores do SQLite se há transação ativa.
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
        c.Open();
        using var cmd = Cmd(c, "DELETE FROM trash WHERE id=$i", [("$i", id)]);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Cabeça da fila de cada drive (a operação de menor id) e quantas há atrás dela — painel "o que está travando"
    /// da interface (conexão de leitura).
    /// </summary>
    public List<QueueHead> QueueHeads()
    {
        lock (_roLock)
        {
            using var cmd = Cmd(_ro, """
                SELECT o.drive_id,o.kind,o.lid,o.name,o.attempts,o.next_try,o.last_error,o.created,
                       (SELECT COUNT(*) FROM ops p WHERE p.drive_id=o.drive_id) - 1
                FROM ops o WHERE o.id IN (SELECT MIN(id) FROM ops GROUP BY drive_id)
                """, []);
            using var r = cmd.ExecuteReader();
            var list = new List<QueueHead>();
            while (r.Read())
                list.Add(new QueueHead(r.GetString(0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3),
                    (int)r.GetInt64(4), r.GetInt64(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetInt64(7), r.GetInt64(8)));
            return list;
        }
    }

    /// <summary>
    /// O nome <paramref name="name"/> em <paramref name="parentRemote"/> ainda está ocupado na nuvem por um item que vai
    /// liberá-lo: exclusão na fila (em qualquer ordem — a do commit de um envio entra depois do novo envio do mesmo nome),
    /// exclusão em Problemas cujo item continua fora do banco (espera o usuário resolver; §23.6) ou Move de OUTRO item que
    /// ainda carrega esse nome na nuvem (versão da nuvem renomeada para nome de conflito; §23.3). Compara como a nuvem
    /// (<see cref="SameCloudName"/>: maiúsculas fora do ASCII e NFC/NFD). Chamar com <see cref="Lock"/>.
    /// </summary>
    bool NomeRetidoNaNuvem(string driveId, long lid, string parentRemote, string name)
    {
        using var cmd = Cmd("""
            SELECT name FROM ops WHERE drive_id=$d AND parent_remote=$p AND name IS NOT NULL
              AND (kind='delete' OR (kind='move' AND lid<>$l))
            UNION ALL
            SELECT f.name FROM failed_ops f WHERE f.drive_id=$d AND f.kind='delete' AND f.parent_remote=$p AND f.name IS NOT NULL
              AND NOT EXISTS(SELECT 1 FROM items i WHERE i.drive_id=f.drive_id AND i.item_id=f.remote_id)
            """, [("$d", driveId), ("$p", parentRemote), ("$l", lid)]);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (SameCloudName(r.GetString(0), name)) return true;
        return false;
    }

    /// <summary>Envios em execução que ainda não gravaram a pasta de destino (<see cref="SetUploadTarget"/>).</summary>
    long RunningUploadsWithoutTarget(string driveId) => Scalar<long>(
        "SELECT COUNT(*) FROM ops WHERE drive_id=$d AND state='running' AND kind='upload' AND parent_remote IS NULL", ("$d", driveId));

    /// <summary>Pasta remota em que o envio em execução grava (a exclusão de outras pastas não precisa esperá-lo).</summary>
    public void SetUploadTarget(long opId, string parentRemote) =>
        Exec("UPDATE ops SET parent_remote=$p WHERE id=$i AND kind='upload'", ("$p", parentRemote), ("$i", opId));
}

/// <summary>Item que o craftDriver viu ir para a Lixeira da nuvem (excluído aqui ou em outro dispositivo).</summary>
public sealed record TrashRecord(long Id, string DriveId, string ItemId, string Name, bool IsDir, long At);

/// <summary>Operação na cabeça da fila de uma drive; <see cref="Behind"/> = quantas esperam atrás dela.</summary>
public sealed record QueueHead(string DriveId, string Kind, long Lid, string? Name, int Attempts, long NextTry, string? LastError,
    long Created, long Behind);
