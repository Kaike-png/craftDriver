using System.Net;
using CraftDriver.Core.Graph;

namespace CraftDriver.Tests;

/// <summary>OneDrive em memória com semântica suficiente de delta/eTag/cTag/conflitos.</summary>
public sealed class FakeGraph : IGraphApi
{
    public sealed class Node
    {
        public string Id = "";
        public string Name = "";
        public string? ParentId;
        public bool IsFolder;
        public byte[] Content = [];
        public int Version;
        public int MetaVersion;
        public bool Deleted;
        public long Seq;
        public DateTimeOffset Modified = DateTimeOffset.UtcNow;
        /// <summary>Atalho ("Adicionar a Meus arquivos"): drive e item apontados.</summary>
        public string? RemoteDrive, RemoteId;
        public bool RemoteIsFolder;
        public string ETag => $"e-{Id}-{Version}-{MetaVersion}";
        public string CTag => $"c-{Id}-{Version}";
    }

    readonly object _lock = new();
    readonly Dictionary<string, Node> _nodes = new();
    long _seq;
    int _nextId;
    public const string DriveId = "fake-drive";
    public int Uploads, Downloads, Deletes, Moves;
    public bool OfflineMode;
    /// <summary>Os próximos N downloads chegam corrompidos (último byte trocado), sem erro de rede.</summary>
    public int CorruptNextDownloads;
    /// <summary>Erro (permanente) a lançar no envio de um arquivo, pelo nome.</summary>
    public Func<string, Exception?>? UploadError;

    readonly string _driveId;

    public FakeGraph() : this(DriveId) { }

    /// <summary>Drive com outro id (ver <see cref="AddOwnerDrive"/>).</summary>
    public FakeGraph(string driveId)
    {
        _driveId = driveId;
        _nodes["root"] = new Node { Id = "root", Name = "root", IsFolder = true, Seq = ++_seq };
    }

    void Check()
    {
        if (OfflineMode) throw new HttpRequestException("offline (simulado)");
    }

    RemoteItem ToRemote(Node n) => new()
    {
        Id = n.Id, Name = n.Name, ParentId = n.ParentId, IsFolder = n.IsFolder, IsRoot = n.Id == "root", IsDeleted = n.Deleted,
        Size = n.Content.Length, LastModified = n.Modified, Created = n.Modified, ETag = n.ETag, CTag = n.IsFolder ? null : n.CTag,
        QuickXorHash = n.IsFolder ? null : QuickXorHash.Compute(n.Content),
        IsRemoteShortcut = n.RemoteId != null, RemoteDriveId = n.RemoteDrive, RemoteId = n.RemoteId, RemoteIsFolder = n.RemoteIsFolder,
    };

    Node? Child(string parentId, string name) =>
        _nodes.Values.FirstOrDefault(n => !n.Deleted && n.ParentId == parentId && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));

    // ---------- helpers para os testes (simulam outro dispositivo) ----------

    public Node? FindByPath(string path)
    {
        lock (_lock)
        {
            var cur = _nodes["root"];
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var c = Child(cur.Id, part);
                if (c == null) return null;
                cur = c;
            }
            return cur;
        }
    }

    public string? ReadText(string path) => FindByPath(path) is { IsFolder: false } n ? System.Text.Encoding.UTF8.GetString(n.Content) : null;

    public Node RemoteCreate(string parentPath, string name, string? text, bool folder = false)
    {
        lock (_lock)
        {
            var parent = FindByPath(parentPath) ?? throw new InvalidOperationException("pai não existe");
            var n = new Node
            {
                Id = "id" + ++_nextId, Name = name, ParentId = parent.Id, IsFolder = folder,
                Content = text == null ? [] : System.Text.Encoding.UTF8.GetBytes(text), Version = 1, Seq = ++_seq,
            };
            _nodes[n.Id] = n;
            RecordVersion(n);
            return n;
        }
    }

    public void RemoteUpdate(string path, string text)
    {
        lock (_lock)
        {
            var n = FindByPath(path)!;
            n.Content = System.Text.Encoding.UTF8.GetBytes(text);
            n.Version++;
            n.Modified = DateTimeOffset.UtcNow;
            n.Seq = ++_seq;
            RecordVersion(n);
        }
    }

    public void RemoteDelete(string path)
    {
        lock (_lock)
        {
            var n = FindByPath(path)!;
            MarkDeleted(n);
        }
    }

    /// <summary>Exclusão em que o delta só traz a pasta (sem um evento por filho, como pode vir no OneDrive for Business).</summary>
    public void RemoteDeleteFolderOnly(string path)
    {
        lock (_lock)
        {
            var n = FindByPath(path)!;
            n.Deleted = true;
            n.Seq = ++_seq;
        }
    }

    void MarkDeleted(Node n)
    {
        foreach (var c in _nodes.Values.Where(c => c.ParentId == n.Id && !c.Deleted).ToList()) MarkDeleted(c);
        n.Deleted = true;
        n.Seq = ++_seq;
    }

    public void RemoteRename(string path, string newName)
    {
        lock (_lock)
        {
            var n = FindByPath(path)!;
            n.Name = newName;
            n.MetaVersion++;
            n.Seq = ++_seq;
        }
    }

    public List<string> ListPaths()
    {
        lock (_lock)
        {
            string PathOf(Node n) => n.ParentId == null ? "" : PathOf(_nodes[n.ParentId]) + "/" + n.Name;
            return _nodes.Values.Where(n => !n.Deleted && n.Id != "root").Select(PathOf).OrderBy(x => x).ToList();
        }
    }

    // ---------- IGraphApi ----------

    public Task<GraphDrive> GetDriveAsync(string driveId, CancellationToken ct)
    {
        if (Route(driveId) is { } o) return o.GetDriveAsync(driveId, ct);
        Check();
        return Task.FromResult(new GraphDrive(DriveId, "OneDrive", "personal", null, 5L << 30, 1L << 30));
    }

    public Task<RemoteItem> GetRootAsync(string driveId, CancellationToken ct)
    {
        if (Route(driveId) is { } o) return o.GetRootAsync(driveId, ct);
        Check();
        lock (_lock) return Task.FromResult(ToRemote(_nodes["root"]));
    }

    public Task<RemoteItem?> GetItemAsync(string driveId, string itemId, CancellationToken ct)
    {
        if (Route(driveId) is { } o) return o.GetItemAsync(driveId, itemId, ct);
        if (Restricted) { Check(); lock (_lock) return Task.FromResult(_nodes.TryGetValue(itemId, out var r) && !r.Deleted && ShareOf(r) != null ? ToRemote(r) : null); }
        Check();
        lock (_lock) return Task.FromResult(_nodes.TryGetValue(itemId, out var n) && !n.Deleted ? ToRemote(n) : null);
    }

    public Task<RemoteItem?> GetChildByNameAsync(string driveId, string parentId, string name, CancellationToken ct)
    {
        if (Route(driveId) is { } o) return o.GetChildByNameAsync(driveId, parentId, name, ct);
        Check();
        lock (_lock) return Task.FromResult(Child(parentId, name) is { } n ? ToRemote(n) : null);
    }

    /// <summary>Página capturada no servidor, antes de entregá-la ao motor (janela enumeração × commit).</summary>
    public Func<DeltaPage, CancellationToken, Task<DeltaPage>>? AfterDelta;

    public async Task<DeltaPage> GetDeltaAsync(string driveId, string? link, CancellationToken ct)
    {
        var page = await GetDeltaCoreAsync(driveId, link, ct);
        return AfterDelta is { } after ? await after(page, ct) : page;
    }

    async Task<DeltaPage> GetDeltaCoreAsync(string driveId, string? link, CancellationToken ct)
    {
        if (Route(driveId) is { } o) return await o.GetDeltaAsync(driveId, link, ct);
        Check();
        DeltaPage result;
        lock (_lock)
        {
            if (link == null) FullDeltaCalls++;
            long since = link == null ? 0 : long.Parse(link.Split(':')[1]);
            IEnumerable<Node> nodes = _nodes.Values.Where(n => n.Seq > since && (since > 0 || !n.Deleted));
            // pais antes dos filhos
            int Depth(Node n) => n.ParentId == null ? 0 : 1 + Depth(_nodes[n.ParentId]);
            var items = nodes.OrderBy(n => since == 0 ? Depth(n) : 0).ThenBy(n => n.Seq).Select(ToRemote).ToList();
            result = new DeltaPage(items, null, "delta:" + _seq);
        }
        if (AfterDeltaPage is { } after) await after(ct);
        return result;
    }

    /// <summary>Último limite de envio configurado por drive (bytes/s).</summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> UploadLimits = new();
    public void ConfigureUploadBandwidth(string driveId, long bytesPerSecond) => UploadLimits[driveId] = bytesPerSecond;

    /// <summary>Executado no início de cada download (ex.: segurar até cancelar).</summary>
    public Func<CancellationToken, Task>? BeforeDownload;

    public async Task DownloadAsync(string driveId, string itemId, Stream destination, long offset, Action<long>? progress, CancellationToken ct)
    {
        if (Route(driveId) is { } o) { await o.DownloadAsync(driveId, itemId, destination, offset, progress, ct); return; }
        if (Restricted) lock (_lock) if (!_nodes.TryGetValue(itemId, out var r) || ShareOf(r) == null) throw new GraphException(HttpStatusCode.Forbidden, "accessDenied", "sem acesso");
        if (BeforeDownload is { } before) await before(ct);
        Check();
        byte[] data;
        lock (_lock)
        {
            if (!_nodes.TryGetValue(itemId, out var n) || n.Deleted) throw new GraphException(HttpStatusCode.NotFound, "itemNotFound", "não existe");
            data = n.Content.ToArray();
            Downloads++;
            DownloadOffsets.Add(offset);
            BytesServed += Math.Max(0, data.Length - (offset > 0 && offset < data.Length ? offset : 0));
            if (CorruptNextDownloads > 0 && data.Length > 0)
            {
                CorruptNextDownloads--;
                data[^1] ^= 0xFF;
            }
        }
        if (offset > 0 && offset < data.Length)
            destination.Seek(offset, SeekOrigin.Begin); // 206: só o restante
        else
        {
            destination.SetLength(0);
            offset = 0;
        }
        // como o GraphClient: WriteAsync (o fluxo com limite de banda não aceita escrita síncrona)
        var rest = data.AsMemory((int)offset);
        if (DropDownloadAfter is { } drop && drop < rest.Length)
        {
            DropDownloadAfter = null;
            await destination.WriteAsync(rest[..(int)drop], ct);
            await destination.FlushAsync(ct);
            throw new HttpIOException(HttpRequestError.ResponseEnded, "conexão caiu (simulado)");
        }
        await destination.WriteAsync(rest, ct);
        progress?.Invoke(data.Length);
    }

    /// <summary>O próximo download entrega só estes bytes e a conexão "cai".</summary>
    public long? DropDownloadAfter;
    public readonly List<long> DownloadOffsets = [];

    /// <summary>Faixas pedidas com <see cref="DownloadRangeAsync"/> (offset, tamanho).</summary>
    public readonly List<(long offset, long length)> RangeRequests = [];
    /// <summary>Total de bytes de conteúdo entregues (downloads inteiros + faixas).</summary>
    public long BytesServed;

    public async Task DownloadRangeAsync(string driveId, string itemId, Stream destination, long offset, long length,
        Action<long>? progress, CancellationToken ct)
    {
        if (BeforeDownload is { } before) await before(ct);
        Check();
        byte[] data;
        lock (_lock)
        {
            if (!_nodes.TryGetValue(itemId, out var n) || n.Deleted) throw new GraphException(HttpStatusCode.NotFound, "itemNotFound", "não existe");
            if (offset < 0 || offset >= n.Content.Length)
                throw new GraphException(HttpStatusCode.RequestedRangeNotSatisfiable, "invalidRange", "faixa fora do arquivo");
            var len = (int)Math.Min(length, n.Content.Length - offset);
            data = n.Content.AsSpan((int)offset, len).ToArray();
            RangeRequests.Add((offset, length));
            BytesServed += len;
            if (CorruptNextDownloads > 0 && data.Length > 0)
            {
                CorruptNextDownloads--;
                data[^1] ^= 0xFF;
            }
        }
        await destination.WriteAsync(data, ct);
        progress?.Invoke(data.Length);
        if (data.Length != length) throw new HttpIOException(HttpRequestError.ResponseEnded, "faixa incompleta (simulado)");
    }

    /// <summary>Cria um arquivo binário (conteúdo arbitrário) como se viesse de outro dispositivo.</summary>
    public Node RemoteCreateBytes(string parentPath, string name, byte[] content)
    {
        var n = RemoteCreate(parentPath, name, null);
        lock (_lock) n.Content = content;
        return n;
    }

    public void RemoteUpdateBytes(string path, byte[] content)
    {
        lock (_lock)
        {
            var n = FindByPath(path)!;
            n.Content = content;
            n.Version++;
            n.Modified = DateTimeOffset.UtcNow;
            n.Seq = ++_seq;
        }
    }
    /// <summary>URLs de sessão recebidas em cada envio (simula a persistência da sessão de upload).</summary>
    public readonly List<string?> UploadSessionsSeen = [];
    /// <summary>Próximo envio: grava esta "sessão" e cai a conexão (retomável).</summary>
    public string? FailUploadAfterSession;

    /// <summary>Executado no início de cada envio (nome do arquivo, token), ex.: segurar até cancelar.</summary>
    public Func<string, CancellationToken, Task>? BeforeUpload;

    /// <summary>Resposta do servidor retida depois de gravar o arquivo (janela upload × delta).</summary>
    public Func<RemoteItem, CancellationToken, Task>? AfterUpload;

    public async Task<RemoteItem> UploadAsync(string driveId, string parentId, string name, string? itemId, string? ifMatchETag,
        string localFile, DateTimeOffset modified, Action<long>? progress, CancellationToken ct, IUploadSessionStore? session = null)
    {
        var result = await UploadCoreAsync(driveId, parentId, name, itemId, ifMatchETag, localFile, modified, progress, ct, session);
        if (AfterUpload is { } after) await after(result, ct);
        return result;
    }

    async Task<RemoteItem> UploadCoreAsync(string driveId, string parentId, string name, string? itemId, string? ifMatchETag,
        string localFile, DateTimeOffset modified, Action<long>? progress, CancellationToken ct, IUploadSessionStore? session)
    {
        if (Route(driveId) is { } o) return await o.UploadAsync(driveId, parentId, name, itemId, ifMatchETag, localFile, modified, progress, ct, session);
        CheckWrite(itemId ?? parentId);
        if (BeforeUpload is { } before) await before(name, ct);
        Check();
        lock (_lock) UploadSessionsSeen.Add(session?.Url);
        if (FailUploadAfterSession is { } fakeUrl)
        {
            FailUploadAfterSession = null;
            session?.Save(fakeUrl);
            throw new HttpRequestException("conexão caiu durante o envio (simulado)");
        }
        if (UploadError?.Invoke(name) is { } uploadError) throw uploadError;
        if (RealUploadSessions && new FileInfo(localFile).Length > SimpleUploadLimit)
        {
            var sent = await SessionClient.UploadAsync(driveId, parentId, name, itemId, ifMatchETag, localFile, modified, progress, ct, session);
            lock (_lock) return ToRemote(_nodes[sent.Id]);
        }
        var data = File.ReadAllBytes(localFile);
        lock (_lock)
        {
            Uploads++;
            Node n;
            if (itemId != null)
            {
                if (!_nodes.TryGetValue(itemId, out n!) || n.Deleted) throw new GraphException(HttpStatusCode.NotFound, "itemNotFound", "não existe");
                if (ifMatchETag != null && ifMatchETag != n.ETag) throw new GraphException(HttpStatusCode.PreconditionFailed, "resourceModified", "eTag não confere");
                n.Version++;
            }
            else
            {
                if (Child(parentId, name) != null) throw new GraphException(HttpStatusCode.Conflict, "nameAlreadyExists", "já existe");
                CheckPathLength(parentId, name);
                n = new Node { Id = "id" + ++_nextId, Name = name, ParentId = parentId, Version = 1 };
                _nodes[n.Id] = n;
            }
            n.Content = data;
            n.Modified = modified;
            n.Seq = ++_seq;
            RecordVersion(n);
            progress?.Invoke(data.Length);
            return ToRemote(n);
        }
    }

    public Task<RemoteItem> CreateFolderAsync(string driveId, string parentId, string name, CancellationToken ct)
    {
        if (Route(driveId) is { } o) return o.CreateFolderAsync(driveId, parentId, name, ct);
        CheckWrite(parentId);
        Check();
        lock (_lock)
        {
            if (Child(parentId, name) is { } existing)
            {
                if (existing.IsFolder) return Task.FromResult(ToRemote(existing));
                throw new GraphException(HttpStatusCode.Conflict, "nameAlreadyExists", "já existe");
            }
            CheckPathLength(parentId, name);
            var n = new Node { Id = "id" + ++_nextId, Name = name, ParentId = parentId, IsFolder = true, Version = 1, Seq = ++_seq };
            _nodes[n.Id] = n;
            return Task.FromResult(ToRemote(n));
        }
    }

    public Task DeleteAsync(string driveId, string itemId, CancellationToken ct) => DeleteAsync(driveId, itemId, null, ct);

    public Task DeleteAsync(string driveId, string itemId, string? ifMatchETag, CancellationToken ct)
    {
        if (Route(driveId) is { } o) return o.DeleteAsync(driveId, itemId, ifMatchETag, ct);
        CheckWrite(itemId);
        Check();
        lock (_lock)
        {
            if (ifMatchETag != null && _nodes.TryGetValue(itemId, out var cur) && !cur.Deleted && cur.ETag != ifMatchETag)
                throw new GraphException(HttpStatusCode.PreconditionFailed, "resourceModified", "eTag não confere");
            Deletes++;
            if (_nodes.TryGetValue(itemId, out var n) && !n.Deleted) MarkDeleted(n);
        }
        return Task.CompletedTask;
    }

    public Task<RemoteItem> MoveAsync(string driveId, string itemId, string newParentId, string newName, CancellationToken ct) =>
        MoveAsync(driveId, itemId, newParentId, newName, replace: false, ct);

    public Task<RemoteItem> MoveAsync(string driveId, string itemId, string newParentId, string newName, bool replace, CancellationToken ct)
    {
        if (Route(driveId) is { } o) return o.MoveAsync(driveId, itemId, newParentId, newName, replace, ct);
        CheckWrite(itemId);
        CheckWrite(newParentId);
        Check();
        lock (_lock)
        {
            CheckPathLength(newParentId, newName);
            var n = _nodes[itemId];
            if (Child(newParentId, newName) is { } clash && clash.Id != itemId)
            {
                if (!replace) throw new GraphException(HttpStatusCode.Conflict, "nameAlreadyExists", "já existe"); // conflictBehavior=fail
                MarkDeleted(clash); // conflictBehavior=replace
                MovesReplacing++;
            }
            Moves++;
            n.ParentId = newParentId;
            n.Name = newName;
            n.MetaVersion++;
            n.Seq = ++_seq;
            return Task.FromResult(ToRemote(n));
        }
    }

    public Task<RemoteItem> SetModifiedAsync(string driveId, string itemId, DateTimeOffset modified, CancellationToken ct)
    {
        if (Route(driveId) is { } o) return o.SetModifiedAsync(driveId, itemId, modified, ct);
        CheckWrite(itemId);
        Check();
        lock (_lock)
        {
            var n = _nodes[itemId];
            n.Modified = modified;
            n.MetaVersion++;
            n.Seq = ++_seq;
            return Task.FromResult(ToRemote(n));
        }
    }

    // ---------- outras drives: compartilhados comigo e atalhos ----------

    readonly Dictionary<string, FakeGraph> _others = new();

    /// <summary>Drive de outro usuário: só os itens compartilhados (e descendentes) são acessíveis.</summary>
    public bool Restricted;
    /// <summary>Compartilhamentos desta drive (dona): id do item → pode escrever.</summary>
    public readonly Dictionary<string, bool> Shares = new();
    /// <summary>Escritas recusadas por falta de permissão (não deveriam nem chegar aqui).</summary>
    public int DeniedWrites;
    /// <summary>Entradas de /me/drive/sharedWithMe: (drive dona, id do item).</summary>
    public readonly List<(FakeGraph owner, string id)> SharedWithMe = [];
    public int SharedWithMeCalls, ChildrenCalls;
    public bool SharedWithMeFails;
    /// <summary>Tamanho da página de /children (para exercitar o nextLink).</summary>
    public int ChildrenPageSize = 200;

    public string Id => _driveId;

    /// <summary>Cria a drive de outro usuário (o dono), acessível pela mesma conta só no que for compartilhado.</summary>
    public FakeGraph AddOwnerDrive(string driveId)
    {
        var g = new FakeGraph(driveId) { Restricted = true };
        lock (_lock) _others[driveId] = g;
        return g;
    }

    FakeGraph? Route(string driveId)
    {
        if (driveId == _driveId) return null;
        FakeGraph? o;
        lock (_lock) o = _others.TryGetValue(driveId, out var x) ? x : null;
        if (o != null) Check(); // sem rede aqui = sem rede para a drive do dono também
        return o;
    }

    /// <summary>Compartilhamento que dá acesso ao nó (ele ou um ancestral); null = sem acesso.</summary>
    bool? ShareOf(Node n)
    {
        if (!Restricted) return true;
        for (Node? cur = n; cur != null; cur = cur.ParentId != null && _nodes.TryGetValue(cur.ParentId, out var p) ? p : null)
            if (Shares.TryGetValue(cur.Id, out var w)) return w;
        return null;
    }

    void CheckWrite(string itemId)
    {
        if (!Restricted) return;
        lock (_lock)
        {
            if (_nodes.TryGetValue(itemId, out var n) && ShareOf(n) == true) return;
            DeniedWrites++;
        }
        throw new GraphException(HttpStatusCode.Forbidden, "accessDenied", "sem permissão de escrita");
    }

    /// <summary>O dono compartilha <paramref name="path"/> (da drive dele) com o usuário desta drive.</summary>
    public Node ShareWithMe(FakeGraph owner, string path, bool writable)
    {
        var n = owner.FindByPath(path) ?? throw new InvalidOperationException("não existe: " + path);
        lock (owner._lock) owner.Shares[n.Id] = writable;
        lock (_lock) SharedWithMe.Add((owner, n.Id));
        return n;
    }

    /// <summary>O dono para de compartilhar (some de sharedWithMe e o acesso acaba).</summary>
    public void Unshare(FakeGraph owner, string path)
    {
        var n = owner.FindByPath(path)!;
        lock (owner._lock) owner.Shares.Remove(n.Id);
        lock (_lock) SharedWithMe.RemoveAll(x => x.owner == owner && x.id == n.Id);
    }

    /// <summary>"Adicionar atalho a Meus arquivos": cria o atalho em <paramref name="parentPath"/> desta drive.</summary>
    public Node AddShortcut(string parentPath, string name, FakeGraph owner, string targetPath)
    {
        var target = owner.FindByPath(targetPath)!;
        lock (_lock)
        {
            var parent = FindByPath(parentPath) ?? throw new InvalidOperationException("pai não existe");
            var n = new Node
            {
                Id = "id" + ++_nextId, Name = name, ParentId = parent.Id, IsFolder = target.IsFolder, Version = 1, Seq = ++_seq,
                RemoteDrive = owner._driveId, RemoteId = target.Id, RemoteIsFolder = target.IsFolder,
            };
            _nodes[n.Id] = n;
            return n;
        }
    }

    public Task<List<RemoteItem>> GetSharedWithMeAsync(CancellationToken ct)
    {
        Check();
        if (SharedWithMeFails) throw new GraphException(HttpStatusCode.ServiceUnavailable, "serviceNotAvailable", "falha simulada");
        List<(FakeGraph owner, string id)> entries;
        lock (_lock)
        {
            SharedWithMeCalls++;
            entries = SharedWithMe.ToList();
        }
        var list = new List<RemoteItem>();
        foreach (var (owner, id) in entries)
            lock (owner._lock)
            {
                var n = owner._nodes[id];
                if (n.Deleted) continue;
                var r = owner.ToRemote(n);
                list.Add(new RemoteItem
                {
                    Id = "swm-" + id, Name = n.Name, IsFolder = n.IsFolder, Size = r.Size, LastModified = n.Modified, Created = n.Modified,
                    IsRemoteShortcut = true, RemoteDriveId = owner._driveId, RemoteId = id, RemoteIsFolder = n.IsFolder,
                });
            }
        return Task.FromResult(list);
    }

    public async Task<DeltaPage> GetChildrenAsync(string driveId, string itemId, string? next, CancellationToken ct)
    {
        if (Route(driveId) is { } o) return await o.GetChildrenAsync(driveId, itemId, next, ct);
        Check();
        DeltaPage result;
        lock (_lock)
        {
            ChildrenCalls++;
            if (!_nodes.TryGetValue(itemId, out var folder) || folder.Deleted || ShareOf(folder) == null)
                throw new GraphException(HttpStatusCode.NotFound, "itemNotFound", "não existe");
            var all = _nodes.Values.Where(n => !n.Deleted && n.ParentId == itemId).OrderBy(n => n.Name).ToList();
            var start = next == null ? 0 : int.Parse(next.Split(':')[2]);
            var page = all.Skip(start).Take(ChildrenPageSize).Select(ToRemote).ToList();
            var end = start + page.Count;
            result = new DeltaPage(page, end < all.Count ? $"children:{itemId}:{end}" : null, null);
        }
        if (AfterChildrenPage is { } after) await after(ct);
        return result;
    }

    public Task<bool> CanWriteAsync(string driveId, string itemId, CancellationToken ct)
    {
        if (Route(driveId) is { } o) return o.CanWriteAsync(driveId, itemId, ct);
        Check();
        lock (_lock) return Task.FromResult(_nodes.TryGetValue(itemId, out var n) && ShareOf(n) == true);
    }

    // ---------- compartilhamento, versões e Lixeira ----------

    /// <summary>Simula o tenant que proíbe links "qualquer pessoa" (anonymous): createLink com esse escopo dá 403.</summary>
    public bool RefuseAnonymousLinks;
    public readonly List<(string ItemId, string Type, string Scope)> LinkRequests = [];
    public int VersionRestores, VersionDownloads, TrashRestores;
    readonly Dictionary<string, List<(string Id, byte[] Content, DateTimeOffset Modified)>> _versions = new();

    void RecordVersion(Node n)
    {
        if (n.IsFolder) return;
        if (!_versions.TryGetValue(n.Id, out var list)) _versions[n.Id] = list = [];
        list.Add(($"{list.Count + 1}.0", n.Content.ToArray(), n.Modified));
    }

    Node Existing(string itemId) =>
        _nodes.TryGetValue(itemId, out var n) && !n.Deleted ? n : throw new GraphException(HttpStatusCode.NotFound, "itemNotFound", "não existe");

    public Task<SharingLink> CreateLinkAsync(string driveId, string itemId, string type, string scope, CancellationToken ct)
    {
        if (Route(driveId) is { } o) return o.CreateLinkAsync(driveId, itemId, type, scope, ct);
        CheckDriveId(driveId);
        Check();
        lock (_lock)
        {
            LinkRequests.Add((itemId, type, scope));
            Existing(itemId);
            if (LinkError?.Invoke(scope) is { } linkError) throw linkError;
            if (scope == "anonymous" && RefuseAnonymousLinks)
                throw new GraphException(HttpStatusCode.Forbidden, "accessDenied", "Sharing links of type anonymous are disabled by policy");
            return Task.FromResult(new SharingLink($"https://1drv.ms/fake/{itemId}?{type}&{scope}", type, scope));
        }
    }

    public Task<IReadOnlyList<DriveItemVersion>> ListVersionsAsync(string driveId, string itemId, CancellationToken ct)
    {
        if (Route(driveId) is { } o) return o.ListVersionsAsync(driveId, itemId, ct);
        CheckDriveId(driveId);
        Check();
        lock (_lock)
        {
            Existing(itemId);
            IReadOnlyList<DriveItemVersion> list = (_versions.GetValueOrDefault(itemId) ?? [])
                .Select(v => new DriveItemVersion(v.Id, v.Modified, v.Content.Length, "Teste"))
                .Reverse().ToList();
            return Task.FromResult(list);
        }
    }

    public Task RestoreVersionAsync(string driveId, string itemId, string versionId, CancellationToken ct)
    {
        if (Route(driveId) is { } o) return o.RestoreVersionAsync(driveId, itemId, versionId, ct);
        CheckDriveId(driveId);
        Check();
        lock (_lock)
        {
            var n = Existing(itemId);
            var v = _versions.GetValueOrDefault(itemId)?.FirstOrDefault(x => x.Id == versionId) is { Id: not null } found
                ? found : throw new GraphException(HttpStatusCode.NotFound, "itemNotFound", "versão não existe");
            VersionRestores++;
            n.Content = v.Content.ToArray();
            n.Version++;
            n.Modified = DateTimeOffset.UtcNow;
            n.Seq = ++_seq;
            RecordVersion(n);
            return Task.CompletedTask;
        }
    }

    public Task DownloadVersionAsync(string driveId, string itemId, string versionId, Stream destination, CancellationToken ct)
    {
        if (Route(driveId) is { } o) return o.DownloadVersionAsync(driveId, itemId, versionId, destination, ct);
        CheckDriveId(driveId);
        Check();
        byte[] data;
        lock (_lock)
        {
            Existing(itemId);
            var v = _versions.GetValueOrDefault(itemId)?.FirstOrDefault(x => x.Id == versionId) is { Id: not null } found
                ? found : throw new GraphException(HttpStatusCode.NotFound, "itemNotFound", "versão não existe");
            VersionDownloads++;
            data = v.Content.ToArray();
        }
        destination.Write(data);
        return Task.CompletedTask;
    }

    public Task<RemoteItem> RestoreDeletedAsync(string driveId, string itemId, CancellationToken ct)
    {
        Check();
        lock (_lock)
        {
            if (!_nodes.TryGetValue(itemId, out var n) || !n.Deleted)
                throw new GraphException(HttpStatusCode.NotFound, "itemNotFound", "não está na Lixeira");
            TrashRestores++;
            void Undelete(Node x)
            {
                x.Deleted = false;
                x.Seq = ++_seq;
                foreach (var c in _nodes.Values.Where(c => c.ParentId == x.Id && c.Deleted).ToList()) Undelete(c);
            }
            Undelete(n);
            return Task.FromResult(ToRemote(n));
        }
    }

    /// <summary>
    /// Limite do caminho inteiro na nuvem (o OneDrive/SharePoint recusa mais de 400 caracteres com
    /// <c>pathIsTooLong</c>); null = sem limite. Vale para criar pasta, enviar arquivo novo e mover.
    /// </summary>
    public int? MaxPathChars;
    public int PathTooLongRejections;

    /// <summary>Chamar com <c>_lock</c> preso.</summary>
    void CheckPathLength(string parentId, string name)
    {
        if (MaxPathChars is not { } max) return;
        var len = name.Length + 1;
        for (var cur = _nodes.GetValueOrDefault(parentId); cur is { ParentId: not null }; cur = _nodes.GetValueOrDefault(cur.ParentId))
            len += cur.Name.Length + 1;
        if (len <= max) return;
        PathTooLongRejections++;
        throw new GraphException(HttpStatusCode.BadRequest, "pathIsTooLong", "Path exceeds maximum length.");
    }

    // ---------- envio grande em sessão pelo protocolo real ----------

    const long SimpleUploadLimit = 4 * 1024 * 1024;

    /// <summary>
    /// Arquivos acima do limite do PUT simples (4 MiB) sobem pelo <see cref="GraphClient"/> de verdade
    /// (createUploadSession, partes com Content-Range, consulta de nextExpectedRanges ao retomar) contra um servidor
    /// HTTP falso que grava nesta drive — o código de sessão/retomada exercitado é o de produção.
    /// </summary>
    public bool RealUploadSessions;
    /// <summary>
    /// Imita a biblioteca do SharePoint (verificado em 12/11/2026): sessão que SUBSTITUI um documento do Office válido
    /// com <c>fileSystemInfo</c> no createUploadSession responde 404 "The upload session was not found" na 1ª parte e
    /// o eTag do item avança mesmo sem gravar o conteúdo — a nova tentativa com o If-Match antigo dá 412.
    /// </summary>
    public bool SharePointOfficeReplaceRejectsDate;
    /// <summary>Bytes de partes aceitas pelo servidor de sessões (soma de todos os PUTs gravados).</summary>
    public long SessionBytesReceived;
    public int SessionsCreated, SessionStatusQueries, SessionCreatesAttempted;
    /// <summary>Offset de cada parte que chegou ao servidor de sessões (aceita ou não).</summary>
    public readonly List<long> ChunkOffsets = [];
    /// <summary>Executado antes de gravar cada parte (offset, token do pedido), ex.: segurar até o motor parar.</summary>
    public Func<long, CancellationToken, Task>? BeforeUploadChunk;

    /// <summary>As sessões abertas expiram no servidor: a URL guardada passa a responder 404.</summary>
    public void ExpireUploadSessions()
    {
        lock (_lock) _sessions.Clear();
    }

    sealed class UploadSession
    {
        public string? ItemId, ParentId, Name;
        public byte[] Data = [];
        public long Received;
        public DateTimeOffset Modified = DateTimeOffset.UtcNow;
        public bool Doomed;
    }

    readonly Dictionary<string, UploadSession> _sessions = new();
    int _nextSession;
    GraphClient? _sessionClient;

    GraphClient SessionClient
    {
        get
        {
            lock (_lock) return _sessionClient ??= new GraphClient((_, _) => Task.FromResult("token"), new HttpClient(new SessionServer(this)));
        }
    }

    /// <summary>Servidor falso das sessões de upload (só os pedidos que o GraphClient faz num envio grande).</summary>
    sealed class SessionServer(FakeGraph g) : HttpMessageHandler
    {
        const string UploadBase = "https://upload.fake/sessao/";

        static HttpResponseMessage Json(HttpStatusCode code, string json) =>
            new(code) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

        static HttpResponseMessage Error(HttpStatusCode code, string err) => Json(code, $$$"""{"error":{"code":"{{{err}}}","message":"{{{err}}}"}}""");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.OriginalString;
            if (url.EndsWith("/createUploadSession", StringComparison.Ordinal) && req.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref g.SessionCreatesAttempted);
                using var doc = System.Text.Json.JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct));
                var item = doc.RootElement.GetProperty("item");
                var hasDate = item.TryGetProperty("fileSystemInfo", out var fsi);
                var rest = url[(url.IndexOf("/items/", StringComparison.Ordinal) + 7)..^"/createUploadSession".Length];
                var s = new UploadSession { Modified = hasDate ? fsi.GetProperty("lastModifiedDateTime").GetDateTimeOffset() : DateTimeOffset.UtcNow };
                var colon = rest.IndexOf(":/", StringComparison.Ordinal);
                lock (g._lock)
                {
                    if (colon >= 0)
                    {
                        s.ParentId = Uri.UnescapeDataString(rest[..colon]);
                        s.Name = Uri.UnescapeDataString(rest[(colon + 2)..^1]);
                        if (g.Child(s.ParentId, s.Name) != null) return Error(HttpStatusCode.Conflict, "nameAlreadyExists");
                    }
                    else
                    {
                        s.ItemId = Uri.UnescapeDataString(rest);
                        if (!g._nodes.TryGetValue(s.ItemId, out var n) || n.Deleted) return Error(HttpStatusCode.NotFound, "itemNotFound");
                        if (req.Headers.TryGetValues("If-Match", out var m) && m.First() != n.ETag) return Error(HttpStatusCode.PreconditionFailed, "resourceModified");
                        s.Doomed = g.SharePointOfficeReplaceRejectsDate && hasDate && n.Name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);
                    }
                    var id = UploadBase + ++g._nextSession;
                    g._sessions[id] = s;
                    g.SessionsCreated++;
                    return Json(HttpStatusCode.OK, $$"""{"uploadUrl":"{{id}}"}""");
                }
            }
            if (!url.StartsWith(UploadBase, StringComparison.Ordinal))
            {
                if (req.Method == HttpMethod.Get && url.Contains("/items/", StringComparison.Ordinal))
                {
                    var rest = url[(url.IndexOf("/items/", StringComparison.Ordinal) + 7)..];
                    var colon = rest.IndexOf(":/", StringComparison.Ordinal);
                    lock (g._lock)
                    {
                        Node? n = colon >= 0
                            ? g.Child(Uri.UnescapeDataString(rest[..colon]), Uri.UnescapeDataString(rest[(colon + 2)..^1]))
                            : g._nodes.GetValueOrDefault(Uri.UnescapeDataString(rest));
                        if (n == null || n.Deleted) return Error(HttpStatusCode.NotFound, "itemNotFound");
                        var body = new System.Text.Json.Nodes.JsonObject
                        {
                            ["id"] = n.Id, ["name"] = n.Name, ["size"] = n.Content.Length, ["eTag"] = n.ETag, ["cTag"] = n.CTag,
                            ["file"] = new System.Text.Json.Nodes.JsonObject
                            {
                                ["hashes"] = new System.Text.Json.Nodes.JsonObject { ["quickXorHash"] = QuickXorHash.Compute(n.Content) },
                            },
                        };
                        return Json(HttpStatusCode.OK, body.ToJsonString());
                    }
                }
                return Error(HttpStatusCode.NotFound, "itemNotFound");
            }
            if (req.Method == HttpMethod.Get)
            {
                lock (g._lock)
                {
                    g.SessionStatusQueries++;
                    return g._sessions.TryGetValue(url, out var s)
                        ? Json(HttpStatusCode.OK, $$"""{"nextExpectedRanges":["{{s.Received}}-"]}""")
                        : Error(HttpStatusCode.NotFound, "itemNotFound");
                }
            }
            if (req.Method == HttpMethod.Delete)
            {
                lock (g._lock) g._sessions.Remove(url);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            var range = req.Content!.Headers.ContentRange!;
            var data = await req.Content.ReadAsByteArrayAsync(ct);
            lock (g._lock) g.ChunkOffsets.Add(range.From!.Value);
            if (g.BeforeUploadChunk is { } before) await before(range.From!.Value, ct);
            lock (g._lock)
            {
                if (!g._sessions.TryGetValue(url, out var s)) return Error(HttpStatusCode.NotFound, "itemNotFound");
                if (s.Doomed)
                {
                    // SharePoint real: a sessão some na 1ª parte e o eTag do item avança (o próximo If-Match dá 412)
                    g._sessions.Remove(url);
                    if (g._nodes.TryGetValue(s.ItemId!, out var doomed)) { doomed.Version++; doomed.Seq = ++g._seq; } // eTag e cTag avançam, conteúdo igual
                    return Error(HttpStatusCode.NotFound, "itemNotFound");
                }
                if (range.From != s.Received) return Error(HttpStatusCode.RequestedRangeNotSatisfiable, "invalidRange");
                var total = range.Length!.Value;
                if (s.Data.Length != total) Array.Resize(ref s.Data, (int)total);
                data.CopyTo(s.Data, range.From!.Value);
                s.Received = range.To!.Value + 1;
                g.SessionBytesReceived += data.Length;
                if (s.Received < total) return Json(HttpStatusCode.Accepted, $$"""{"nextExpectedRanges":["{{s.Received}}-"]}""");

                g._sessions.Remove(url);
                g.Uploads++;
                Node n;
                if (s.ItemId != null)
                {
                    if (!g._nodes.TryGetValue(s.ItemId, out n!) || n.Deleted) return Error(HttpStatusCode.NotFound, "itemNotFound");
                    n.Version++;
                }
                else
                {
                    if (g.Child(s.ParentId!, s.Name!) != null) return Error(HttpStatusCode.Conflict, "nameAlreadyExists");
                    n = new Node { Id = "id" + ++g._nextId, Name = s.Name!, ParentId = s.ParentId, Version = 1 };
                    g._nodes[n.Id] = n;
                }
                n.Content = s.Data;
                n.Modified = s.Modified;
                n.Seq = ++g._seq;
                g.RecordVersion(n);
                if (g.LoseResponseAfterUploadComplete)
                {
                    // o servidor gravou o arquivo, mas a resposta não chega ao cliente
                    g.LoseResponseAfterUploadComplete = false;
                    throw new HttpRequestException("conexão caiu depois da última parte (simulado)");
                }
                return Json(HttpStatusCode.Created, $$$"""{"id":"{{{n.Id}}}","name":"{{{n.Name}}}","size":{{{n.Content.Length}}},"file":{}}""");
            }
        }
    }

    // ---------- costuras dos testes da seção 12 (PENDENCIAS.md) ----------

    /// <summary>
    /// A próxima sessão de upload que terminar grava o arquivo no servidor, mas a resposta da última parte se perde
    /// (conexão cai): o cliente não sabe que o envio terminou.
    /// </summary>
    public bool LoseResponseAfterUploadComplete;

    /// <summary>Como a Graph: id de drive que não existe (ex.: a chave interna de sub-drive com '|') → 400.</summary>
    void CheckDriveId(string driveId)
    {
        if (driveId != _driveId)
            throw new GraphException(HttpStatusCode.BadRequest, "invalidRequest", $"Invalid drive id '{driveId}'");
    }

    // ---------- costuras da frente A1 (rodada 2: §12/§13/§18) ----------

    /// <summary>Enumerações completas (delta sem link) pedidas a esta drive.</summary>
    public int FullDeltaCalls;
    /// <summary>Página já materializada (snapshot da listagem): permite atrasar a entrega sem depender de _mutate.</summary>
    public Func<CancellationToken, Task>? AfterDeltaPage, AfterChildrenPage;
    /// <summary>Moves que substituíram (conflictBehavior=replace) um item existente no destino.</summary>
    public int MovesReplacing;
    /// <summary>Erro a lançar no createLink, pelo escopo pedido (anonymous/organization).</summary>
    public Func<string, Exception?>? LinkError;
    /// <summary>
    /// Delta por pasta (/items/{id}/delta) como o OneDrive pessoal (HIPÓTESE); false (padrão) = responde 400 como o
    /// OneDrive for Business/SharePoint e o motor cai para children. Pedidos contados em <see cref="FolderDeltaCalls"/>.
    /// </summary>
    public bool SupportsFolderDelta;
    public int FolderDeltaCalls;

    bool InSubtree(Node n, string rootId)
    {
        for (Node? cur = n; cur != null; cur = cur.ParentId != null && _nodes.TryGetValue(cur.ParentId, out var p) ? p : null)
            if (cur.Id == rootId) return true;
        return false;
    }

    public Task<DeltaPage> GetFolderDeltaAsync(string driveId, string itemId, string? link, CancellationToken ct)
    {
        if (Route(driveId) is { } o) return o.GetFolderDeltaAsync(driveId, itemId, link, ct);
        Check();
        lock (_lock)
        {
            FolderDeltaCalls++;
            if (!SupportsFolderDelta) throw new GraphException(FolderDeltaRefusal, FolderDeltaRefusal == HttpStatusCode.Forbidden ? "accessDenied" : "invalidRequest", "delta por pasta recusado (simulado)");
            if (!_nodes.TryGetValue(itemId, out var folder) || folder.Deleted || ShareOf(folder) == null)
                throw new GraphException(HttpStatusCode.NotFound, "itemNotFound", "não existe");
            long since = link == null ? 0 : long.Parse(link.Split(':')[2]);
            int Depth(Node n) => n.ParentId == null ? 0 : 1 + Depth(_nodes[n.ParentId]);
            var items = _nodes.Values.Where(n => n.Seq > since && (since > 0 || !n.Deleted) && InSubtree(n, itemId))
                .OrderBy(n => since == 0 ? Depth(n) : 0).ThenBy(n => n.Seq).Select(ToRemote).ToList();
            return Task.FromResult(new DeltaPage(items, null, $"fdelta:{itemId}:{_seq}"));
        }
    }

    /// <summary>Status da recusa do delta por pasta quando <see cref="SupportsFolderDelta"/> é false (padrão 400).</summary>
    public HttpStatusCode FolderDeltaRefusal = HttpStatusCode.BadRequest;

    /// <summary>Move feito em outro dispositivo: como no OneDrive, o delta traz só o item movido, não os filhos dele.</summary>
    public void RemoteMove(string path, string newParentPath)
    {
        lock (_lock)
        {
            var n = FindByPath(path)!;
            n.ParentId = FindByPath(newParentPath)!.Id;
            n.MetaVersion++;
            n.Seq = ++_seq;
        }
    }
}
