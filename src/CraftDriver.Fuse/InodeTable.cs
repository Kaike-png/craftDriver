namespace CraftDriver.Fuse;

/// <summary>
/// Inodes da API de baixo nível ↔ caminhos do <see cref="CraftDriver.Core.Vfs.VirtualFs"/>.
/// <para>
/// Regras do protocolo: cada resposta de entrada (lookup, mkdir, create, entrada de readdirplus) soma 1 à contagem
/// do kernel, e o kernel devolve com forget; só com contagem zero o inode pode sumir. Números nunca são
/// reaproveitados (generation fica 0). Apagar ou renomear por cima "solta" o inode do caminho: ele continua
/// válido para fds abertos (que usam o fh), mas lookups novos daquele nome recebem outro número.
/// </para>
/// <para>
/// Invariante: cada inode tem no máximo UM caminho e cada caminho aponta para um inode que está na tabela
/// (<c>_byPath[p] == i ⇔ _byIno[i].Path == p</c>). O lookup é exato (o Linux diferencia maiúsculas), então um item
/// só tem um nome válido; se o delta renomeia/move o item por trás do kernel, o próximo lookup que o achar pelo nome
/// novo leva o inode (e a subárvore) para lá — não fica "apelido" velho que possa ficar pendurado depois do forget.
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
    /// <summary>
    /// Índice de filhos: caminho do diretório → caminhos registrados logo abaixo dele. Sem ele cada unlink/rename
    /// varria a tabela inteira (rm -rf de 10 k arquivos depois de um find grande = 10 k × 100 k comparações).
    /// </summary>
    readonly Dictionary<string, HashSet<string>> _kids = new(StringComparer.Ordinal);
    ulong _next = VirtualBase;

    public static string Combine(string dir, string name) => dir == "/" ? "/" + name : dir + "/" + name;

    static string ParentOf(string path)
    {
        var i = path.LastIndexOf('/');
        return i <= 0 ? "/" : path[..i];
    }

    public static bool IsStable(ulong ino) => ino > RootIno && ino < VirtualBase;

    public string? PathOf(ulong ino)
    {
        lock (_lock) return _byIno.TryGetValue(ino, out var n) ? n.Path : null;
    }

    /// <summary>
    /// Inode do caminho com uma referência a mais do kernel (resposta de entrada). Confere o lid atual do caminho
    /// (<paramref name="lid"/>, ou consulta o banco fora do lock da tabela — nunca segurar os dois): se o caminho
    /// passou a ser de outro item, ou o item mudou de nome, a tabela segue o banco.
    /// </summary>
    public ulong Lookup(string path, long? lid = null)
    {
        var stable = StableId(path, lid);
        lock (_lock)
        {
            var ino = Bind(path, stable);
            _byIno[ino].Lookups++;
            return ino;
        }
    }

    ulong? StableId(string path, long? lid) =>
        path == "/" ? RootIno : (lid ?? stableIdOf?.Invoke(path)) is long l ? (ulong)l + 1 : null;

    /// <summary>
    /// Entrada de readdirplus: conta a referência ANTES de pôr no buffer (<paramref name="fits"/>) e devolve se não
    /// coube. Com "Peek, depois AddRef" um readdirplus concorrente do mesmo diretório que não coube apagava o nó no
    /// meio e o kernel ficava com um inode que a tabela não conhece. Retorna o inode entregue, ou 0 se não coube.
    /// </summary>
    public ulong ReaddirEntry(string path, long? lid, Func<ulong, bool> fits)
    {
        var ino = Lookup(path, lid);
        if (fits(ino)) return ino;
        Forget(ino, 1);
        return 0;
    }

    /// <summary>Inode já conhecido do caminho, ou 0xffffffff (FUSE_UNKNOWN_INO) — readdir simples não cria.</summary>
    public ulong Known(string path)
    {
        lock (_lock) return _byPath.TryGetValue(path, out var ino) ? ino : 0xffffffff;
    }

    /// <summary>Inode está na tabela (o kernel tem referência a ele).</summary>
    public bool Has(ulong ino)
    {
        lock (_lock) return _byIno.ContainsKey(ino);
    }

    public void Forget(ulong ino, long count)
    {
        lock (_lock)
        {
            if (ino == RootIno || !_byIno.TryGetValue(ino, out var n)) return;
            n.Lookups -= count;
            if (n.Lookups > 0) return;
            _byIno.Remove(ino);
            if (n.Path != null) RemovePath(n.Path);
        }
    }

    /// <summary>Apagado: o caminho (e o que estava abaixo) deixa de apontar para estes inodes.</summary>
    public void Detach(string path)
    {
        lock (_lock) Unbind(path);
    }

    /// <summary>rename: a subárvore de <paramref name="from"/> passa a <paramref name="to"/> (troca, com RENAME_EXCHANGE).</summary>
    public void Move(string from, string to, bool exchange = false)
    {
        lock (_lock) MoveLocked(from, to, exchange);
    }

    /// <summary>
    /// O item do inode está agora em <paramref name="path"/> (delta renomeou/moveu): leva o inode e a subárvore.
    /// Inode fora da tabela (o kernel já esqueceu) não é criado.
    /// </summary>
    public void Relocate(ulong ino, string path)
    {
        lock (_lock)
        {
            if (!_byIno.ContainsKey(ino)) return;
            if (_byPath.TryGetValue(path, out var cur) && cur == ino) return;
            Bind(path, ino);
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

    // ---------- sob _lock ----------

    /// <summary>Associa o caminho ao inode do item (<paramref name="stable"/>), criando o nó se preciso.</summary>
    ulong Bind(string path, ulong? stable)
    {
        if (_byPath.TryGetValue(path, out var cur))
        {
            if (stable == null || cur == stable) return cur;
            Unbind(path); // o nome agora é de outro item: o inode antigo (e o que estava abaixo) solta o caminho
        }
        if (stable is { } s && _byIno.TryGetValue(s, out var node))
        {
            // o item mudou de nome/pasta por trás do kernel (delta), ou reapareceu antes do forget
            if (node.Path != null) MoveLocked(node.Path, path, false);
            else AddPath(path, s);
            return s;
        }
        var ino = stable ?? _next++;
        _byIno[ino] = new Node();
        AddPath(path, ino);
        return ino;
    }

    void AddPath(string path, ulong ino)
    {
        _byPath[path] = ino;
        _byIno[ino].Path = path;
        // Arestas também para diretórios intermediários sem inode (FORGET pode tirar o pai e manter o filho).
        while (path != "/")
        {
            var parent = ParentOf(path);
            if (!_kids.TryGetValue(parent, out var set)) _kids[parent] = set = new HashSet<string>(StringComparer.Ordinal);
            if (!set.Add(path)) break;
            path = parent;
        }
    }

    void RemovePath(string path)
    {
        _byPath.Remove(path);
        while (path != "/" && !_byPath.ContainsKey(path) && !_kids.ContainsKey(path))
        {
            var parent = ParentOf(path);
            if (_kids.TryGetValue(parent, out var set) && set.Remove(path) && set.Count == 0) _kids.Remove(parent);
            path = parent;
        }
    }

    /// <summary>Solta o caminho e tudo abaixo dele; nós sem referência do kernel saem da tabela.</summary>
    void Unbind(string path)
    {
        foreach (var (p, ino) in Subtree(path))
        {
            RemovePath(p);
            if (!_byIno.TryGetValue(ino, out var n)) continue;
            n.Path = null;
            if (n.Lookups <= 0) _byIno.Remove(ino);
        }
    }

    void MoveLocked(string from, string to, bool exchange)
    {
        if (from == to) return;
        var moving = Subtree(from);
        var target = exchange ? Subtree(to) : [];
        if (!exchange) Unbind(to);
        foreach (var (p, _) in moving.Concat(target)) RemovePath(p);
        foreach (var (p, ino) in target) Relink(ino, from + p[to.Length..]);
        foreach (var (p, ino) in moving) Relink(ino, to + p[from.Length..]);
    }

    void Relink(ulong ino, string path)
    {
        if (_byIno.ContainsKey(ino)) AddPath(path, ino);
    }

    /// <summary>O caminho (se registrado) e todos os registrados abaixo dele, pelo índice de filhos.</summary>
    List<(string path, ulong ino)> Subtree(string path)
    {
        var list = new List<(string, ulong)>();
        if (_byPath.TryGetValue(path, out var self)) list.Add((path, self));
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            if (!_kids.TryGetValue(stack.Pop(), out var set)) continue;
            foreach (var c in set)
            {
                if (_byPath.TryGetValue(c, out var ino)) list.Add((c, ino));
                stack.Push(c);
            }
        }
        return list;
    }
}
