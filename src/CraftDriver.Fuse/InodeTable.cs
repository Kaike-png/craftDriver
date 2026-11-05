namespace CraftDriver.Fuse;

/// <summary>
/// Inodes da API de baixo nível ↔ caminhos do <see cref="CraftDriver.Core.Vfs.VirtualFs"/>.
/// <para>
/// Regras do protocolo: cada resposta de entrada (lookup, mkdir, create, entrada de readdirplus) soma 1 à contagem
/// do kernel, e o kernel devolve com forget; só com contagem zero o inode pode sumir. Números nunca são
/// reaproveitados (generation fica 0). Apagar ou renomear por cima "solta" o inode do caminho: ele continua
/// válido para fds abertos (que usam o fh), mas lookups novos daquele nome recebem outro número.
/// </para>
/// </summary>
/// <param name="stableIdOf">
/// lid do item no banco para o caminho (ou null). O inode é lid + 1 — o lid é AUTOINCREMENT e nunca reaproveitado,
/// então o número é o mesmo entre montagens e reinícios e sobrevive a renomear (backup, rsync --inplace, find -inum,
/// git status contam com isso). O 1 fica com a raiz virtual.
/// </param>
public sealed class InodeTable(Func<string, long?>? stableIdOf = null)
{
    public const ulong RootIno = 1;
    /// <summary>
    /// Nós sem item no banco (pastas virtuais) recebem números a partir daqui, longe dos lids, e só valem durante
    /// a montagem.
    /// </summary>
    public const ulong VirtualBase = 1UL << 48;

    sealed class Node
    {
        public string? Path;
        public long Lookups;
        /// <summary>mtime e tamanho no último open (para manter o cache de página só se não mudaram).</summary>
        public (long mtime, long size)? LastOpen;
    }

    readonly object _lock = new();
    readonly Dictionary<ulong, Node> _byIno = new() { [RootIno] = new Node { Path = "/", Lookups = 1 } };
    readonly Dictionary<string, ulong> _byPath = new(StringComparer.Ordinal) { ["/"] = RootIno };
    ulong _next = VirtualBase;


    public static string Combine(string dir, string name) => dir == "/" ? "/" + name : dir + "/" + name;

    public string? PathOf(ulong ino)
    {
        lock (_lock) return _byIno.TryGetValue(ino, out var n) ? n.Path : null;
    }

    /// <summary>Inode do caminho, criando se preciso, sem contar referência (readdirplus: conta só se couber).</summary>
    public ulong Peek(string path)
    {
        var id = StableId(path);
        lock (_lock) return GetOrAdd(path, id);
    }

    /// <summary>Consulta o banco fora do lock da tabela (nunca segurar os dois ao mesmo tempo).</summary>
    ulong? StableId(string path) => _byPathHas(path) ? null : stableIdOf?.Invoke(path) is long lid ? (ulong)lid + 1 : null;

    bool _byPathHas(string path)
    {
        lock (_lock) return _byPath.ContainsKey(path);
    }

    /// <summary>Inode do caminho com uma referência a mais do kernel (resposta de entrada).</summary>
    public ulong Lookup(string path)
    {
        var id = StableId(path);
        lock (_lock)
        {
            var ino = GetOrAdd(path, id);
            _byIno[ino].Lookups++;
            return ino;
        }
    }

    /// <summary>Inode já conhecido do caminho, ou 0xffffffff (FUSE_UNKNOWN_INO) — readdir simples não cria.</summary>
    public ulong Known(string path)
    {
        lock (_lock) return _byPath.TryGetValue(path, out var ino) ? ino : 0xffffffff;
    }

    public void AddRef(ulong ino)
    {
        lock (_lock)
            if (_byIno.TryGetValue(ino, out var n)) n.Lookups++;
    }

    public void DropIfUnused(ulong ino) => Forget(ino, 0);

    public void Forget(ulong ino, long count)
    {
        lock (_lock)
        {
            if (ino == RootIno || !_byIno.TryGetValue(ino, out var n)) return;
            n.Lookups -= count;
            if (n.Lookups > 0) return;
            _byIno.Remove(ino);
            if (n.Path != null && _byPath.TryGetValue(n.Path, out var cur) && cur == ino) _byPath.Remove(n.Path);
        }
    }

    /// <summary>Apagado: o caminho (e o que estava abaixo) deixa de apontar para estes inodes.</summary>
    public void Detach(string path)
    {
        lock (_lock)
            foreach (var (p, ino) in Subtree(path))
            {
                _byPath.Remove(p);
                _byIno[ino].Path = null;
                if (_byIno[ino].Lookups <= 0) _byIno.Remove(ino);
            }
    }

    /// <summary>rename: a subárvore de <paramref name="from"/> passa a <paramref name="to"/> (troca, com RENAME_EXCHANGE).</summary>
    public void Move(string from, string to, bool exchange = false)
    {
        lock (_lock)
        {
            var moving = Subtree(from);
            var target = Subtree(to);
            foreach (var (p, _) in moving.Concat(target)) _byPath.Remove(p);
            foreach (var (p, ino) in target)
            {
                if (exchange) Relink(ino, from + p[to.Length..]);
                else
                {
                    _byIno[ino].Path = null; // substituído
                    if (_byIno[ino].Lookups <= 0) _byIno.Remove(ino);
                }
            }
            foreach (var (p, ino) in moving) Relink(ino, to + p[from.Length..]);
        }
    }

    /// <summary>
    /// Registra mtime/tamanho deste open e diz se são os mesmos do open anterior (o kernel pode manter as páginas
    /// em cache — o que a opção auto_cache fazia na API de alto nível).
    /// </summary>
    public bool SameContentAsLastOpen(ulong ino, long mtime, long size)
    {
        lock (_lock)
        {
            if (!_byIno.TryGetValue(ino, out var n)) return false;
            var same = n.LastOpen == (mtime, size);
            n.LastOpen = (mtime, size);
            return same;
        }
    }

    public int Count
    {
        get { lock (_lock) return _byIno.Count; }
    }

    void Relink(ulong ino, string path)
    {
        _byIno[ino].Path = path;
        _byPath[path] = ino;
    }

    List<(string path, ulong ino)> Subtree(string path)
    {
        var prefix = path + "/";
        return _byPath.Where(kv => kv.Key == path || kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(kv => (kv.Key, kv.Value)).ToList();
    }

    ulong GetOrAdd(string path, ulong? stable)
    {
        if (_byPath.TryGetValue(path, out var ino)) return ino;
        ino = stable ?? _next++;
        if (_byIno.TryGetValue(ino, out var existing))
        {
            // mesmo item por outro nome (o OneDrive não diferencia maiúsculas): mesmo inode, caminho extra
            if (existing.Path == null) existing.Path = path; // item reapareceu (ex.: restaurado) antes do forget
            _byPath[path] = ino;
            return ino;
        }
        _byIno[ino] = new Node { Path = path };
        _byPath[path] = ino;
        return ino;
    }
}
