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

    public FakeGraph()
    {
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
        Check();
        return Task.FromResult(new GraphDrive(DriveId, "OneDrive", "personal", null, "Teste", 5L << 30, 1L << 30));
    }

    public Task<RemoteItem> GetRootAsync(string driveId, CancellationToken ct)
    {
        Check();
        lock (_lock) return Task.FromResult(ToRemote(_nodes["root"]));
    }

    public Task<RemoteItem?> GetItemAsync(string driveId, string itemId, CancellationToken ct)
    {
        Check();
        lock (_lock) return Task.FromResult(_nodes.TryGetValue(itemId, out var n) && !n.Deleted ? ToRemote(n) : null);
    }

    public Task<RemoteItem?> GetChildByNameAsync(string driveId, string parentId, string name, CancellationToken ct)
    {
        Check();
        lock (_lock) return Task.FromResult(Child(parentId, name) is { } n ? ToRemote(n) : null);
    }

    public Task<DeltaPage> GetDeltaAsync(string driveId, string? link, CancellationToken ct)
    {
        Check();
        lock (_lock)
        {
            long since = link == null ? 0 : long.Parse(link.Split(':')[1]);
            IEnumerable<Node> nodes = _nodes.Values.Where(n => n.Seq > since && (since > 0 || !n.Deleted));
            // pais antes dos filhos
            int Depth(Node n) => n.ParentId == null ? 0 : 1 + Depth(_nodes[n.ParentId]);
            var items = nodes.OrderBy(n => since == 0 ? Depth(n) : 0).ThenBy(n => n.Seq).Select(ToRemote).ToList();
            return Task.FromResult(new DeltaPage(items, null, "delta:" + _seq));
        }
    }

    public Task DownloadAsync(string driveId, string itemId, Stream destination, Action<long>? progress, CancellationToken ct)
    {
        Check();
        byte[] data;
        lock (_lock)
        {
            if (!_nodes.TryGetValue(itemId, out var n) || n.Deleted) throw new GraphException(HttpStatusCode.NotFound, "itemNotFound", "não existe");
            data = n.Content.ToArray();
            Downloads++;
        }
        destination.Write(data);
        progress?.Invoke(data.Length);
        return Task.CompletedTask;
    }

    public Task<RemoteItem> UploadAsync(string driveId, string parentId, string name, string? itemId, string? ifMatchETag,
        string localFile, DateTimeOffset modified, Action<long>? progress, CancellationToken ct)
    {
        Check();
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
                n = new Node { Id = "id" + ++_nextId, Name = name, ParentId = parentId, Version = 1 };
                _nodes[n.Id] = n;
            }
            n.Content = data;
            n.Modified = modified;
            n.Seq = ++_seq;
            progress?.Invoke(data.Length);
            return Task.FromResult(ToRemote(n));
        }
    }

    public Task<RemoteItem> CreateFolderAsync(string driveId, string parentId, string name, CancellationToken ct)
    {
        Check();
        lock (_lock)
        {
            if (Child(parentId, name) is { } existing)
            {
                if (existing.IsFolder) return Task.FromResult(ToRemote(existing));
                throw new GraphException(HttpStatusCode.Conflict, "nameAlreadyExists", "já existe");
            }
            var n = new Node { Id = "id" + ++_nextId, Name = name, ParentId = parentId, IsFolder = true, Version = 1, Seq = ++_seq };
            _nodes[n.Id] = n;
            return Task.FromResult(ToRemote(n));
        }
    }

    public Task DeleteAsync(string driveId, string itemId, CancellationToken ct)
    {
        Check();
        lock (_lock)
        {
            Deletes++;
            if (_nodes.TryGetValue(itemId, out var n) && !n.Deleted) MarkDeleted(n);
        }
        return Task.CompletedTask;
    }

    public Task<RemoteItem> MoveAsync(string driveId, string itemId, string newParentId, string newName, CancellationToken ct)
    {
        Check();
        lock (_lock)
        {
            Moves++;
            var n = _nodes[itemId];
            if (Child(newParentId, newName) is { } clash && clash.Id != itemId) MarkDeleted(clash); // conflictBehavior=replace
            n.ParentId = newParentId;
            n.Name = newName;
            n.MetaVersion++;
            n.Seq = ++_seq;
            return Task.FromResult(ToRemote(n));
        }
    }

    public Task<RemoteItem> SetModifiedAsync(string driveId, string itemId, DateTimeOffset modified, CancellationToken ct)
    {
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
}
