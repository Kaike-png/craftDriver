using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;
using CraftDriver.Core.State;
using CraftDriver.Core.Sync;

namespace CraftDriver.Core.Vfs;

public static class Errno
{
    public const int EPERM = 1, ENOENT = 2, EINTR = 4, EIO = 5, EBADF = 9, EACCES = 13, EEXIST = 17, EXDEV = 18,
        ENOTDIR = 20, EISDIR = 21, EINVAL = 22, ENOSPC = 28, ENAMETOOLONG = 36,
        ENOTEMPTY = 39, ENODATA = 61, EILSEQ = 84, ENOTSUP = 95, EHOSTUNREACH = 113, ECANCELED = 125;
}

public static class OpenFlags
{
    public const int AccMode = 3, WrOnly = 1, RdWr = 2, Creat = 0x40, Excl = 0x80, Trunc = 0x200, Append = 0x400;
}

/// <summary>
/// Atributos para o stat. Não há ctime próprio: o POSIX ctime é "última mudança de status" (não a data de criação
/// que a nuvem informa) e um ctime anterior ao mtime confunde backups/rsync — o FUSE usa o mtime.
/// </summary>
/// <param name="Mode">0 = padrão (0644 arquivo, 0755 pasta); senão <see cref="VirtualFs.ExplicitMode"/> | bits.</param>
/// <param name="Lid">Item do banco (0 = sem item: raiz virtual): o readdirplus do FUSE usa sem consultar o banco de novo.</param>
public readonly record struct FileAttr(bool IsDir, long Size, long MTimeMs, long Blocks, uint Mode = 0, long Lid = 0, int SubMtimeNs = 0);

/// <summary>Estado de um arquivo como o Windows mostra no Explorer.</summary>
public enum ItemStatus { OnlineOnly, Available, AlwaysAvailable, Syncing, LocalOnly, Folder }

/// <summary>
/// Sistema de arquivos virtual: raiz com uma pasta por drive configurada
/// ("OneDrive - Pessoal", "Empresa - Documentos" ...). Toda mudança local
/// é aplicada no banco na hora e enfileirada para a nuvem.
/// Métodos retornam 0 ou -errno (convenção do FUSE).
/// </summary>
public sealed class VirtualFs
{
    readonly SyncEngine _e;
    StateDb Db => _e.Db;

    sealed class Handle
    {
        public long Lid;
        public string DriveId = "";
        public SafeFileHandle File = null!;
        public bool Writable;
        public bool Wrote;
        /// <summary>Leitura por faixas (arquivo ainda incompleto no cache quando foi aberto).</summary>
        public StreamSession? Stream;
        /// <summary>Detecção de leitura sequencial (dispara o download do resto em segundo plano).</summary>
        public long NextRead, SequentialBytes;
        public bool ReportedError;
    }

    readonly ConcurrentDictionary<ulong, Handle> _handles = new();
    /// <summary>Diretórios abertos (opendir): fh → lid (0 = raiz virtual). Com nullpath_ok o readdir não recebe caminho.</summary>
    readonly ConcurrentDictionary<ulong, long> _dirHandles = new();
    /// <summary>Handles abertos + reservas de abertura em andamento, por item (impede despejo do cache).</summary>
    readonly ConcurrentDictionary<long, int> _openCount = new();
    /// <summary>
    /// mtime definido explicitamente (utimens) com o arquivo aberto para escrita — "cp -p", "install -p" e
    /// "rsync -t" gravam, ajustam a data e só então fecham; o fechamento não pode trocá-la pela hora atual.
    /// </summary>
    readonly ConcurrentDictionary<long, long> _mtimeOverride = new();
    readonly ConcurrentDictionary<int, (bool blocked, DateTime at)> _pidCache = new();
    readonly long _rootMtime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    long _nextFh;
    Dictionary<(string drive, string path), long> _resolved = new();
    const int LimiteCaminhos = 65_536;
    LinkedList<(string drive, string path)> _lruCaminhos = new();
    Dictionary<(string drive, string path), LinkedListNode<(string drive, string path)>> _posicoesCaminhos = new();
    long _resolvedVersion = -1;

    public VirtualFs(SyncEngine engine)
    {
        _e = engine;
        _e.IsOpen = lid => _openCount.TryGetValue(lid, out var n) && n > 0;
        _e.MoveOpenHandles = MoveOpenHandles;
    }

    public int OpenHandles => _handles.Count;

    /// <summary>Eventos do motor: somente enfileirar, pois podem vir sob Db.Lock e de handlers locais.</summary>
    public event Action<Item?, Item?> ItemChanged
    {
        add => _e.ItemChanged += value;
        remove => _e.ItemChanged -= value;
    }

    /// <summary>Caminho atual de um lid, sem depender dos aliases do kernel.</summary>
    public string? PathOfLid(long lid)
    {
        lock (Db.Lock)
        {
            var item = Db.Get(lid);
            if (item == null) return null;
            var nomes = new List<string>();
            var visitados = new HashSet<long>();
            while (visitados.Add(item.Lid))
            {
                foreach (var d in MountedDrives)
                    if (item.Lid == d.RootLid) return "/" + Sanitize(d.Config.DisplayName) + (nomes.Count == 0 ? "" : "/" + string.Join('/', nomes.AsEnumerable().Reverse()));
                foreach (var c in _e.SharedContainers)
                    if (item.Lid == c.Lid) return "/" + Sanitize(c.Name) + (nomes.Count == 0 ? "" : "/" + string.Join('/', nomes.AsEnumerable().Reverse()));
                if (Db.IsExcluded(item.Lid)) return null;
                nomes.Add(item.Name);
                if (Db.Get(item.ParentLid) is not { } pai) return null;
                item = pai;
            }
            return null;
        }
    }

    /// <summary>Confirma o estado fora do evento; o monitor garante que a transação do delta terminou.</summary>
    public Item? ItemOfLid(long lid) => Db.Get(lid);

    public StateDb.EscopoLocal MutacaoLocal() => Db.MutacaoLocal();

    // ---------- resolução de caminhos ----------

    public readonly record struct Node(bool IsVirtualRoot, DriveSync? Drive, Item? Item)
    {
        public bool IsDir => IsVirtualRoot || Item is { IsDir: true };
        public bool IsDriveRoot => Drive != null && Item != null && Item.Lid == Drive.RootLid;
    }

    IEnumerable<DriveSync> MountedDrives => _e.Drives.Where(d => d.Ready);

    static string Sanitize(string name) => name.Replace('/', '-');

    public Node? Resolve(string path) => Resolve(path, false);

    /// <summary>
    /// Caminho da montagem → item. Pastas fora da sincronização seletiva (e tudo abaixo delas) não existem para
    /// a montagem; <paramref name="includeExcluded"/> as enxerga (para reincluir pelo caminho).
    /// </summary>
    public Node? Resolve(string path, bool includeExcluded)
    {
        if (path == "/" || path == "") return new Node(true, null, null);
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var drive = MountedDrives.FirstOrDefault(d => Sanitize(d.Config.DisplayName) == parts[0]);
        var startLid = drive?.RootLid ?? 0;
        if (drive == null)
        {
            // pasta virtual "Compartilhados comigo" da conta (irmã da pasta da drive)
            foreach (var c in _e.SharedContainers)
                if (Sanitize(c.Name) == parts[0]) { drive = c.Host; startLid = c.Lid; break; }
            if (drive == null) return null;
        }
        lock (Db.Lock)
        {
            if (_resolvedVersion != Db.ItemsVersion)
            {
                // Dictionary.Clear conserva os buckets: depois de um find grande cada unlink voltaria a zerar
                // dezenas de milhares de posições, mesmo com só dois caminhos vivos. Trocar a geração é O(1).
                _resolved = new();
                _lruCaminhos = new();
                _posicoesCaminhos = new();
                _resolvedVersion = Db.ItemsVersion;
            }
            var key = (drive.Config.DriveId, path);
            if (!includeExcluded && _resolved.TryGetValue(key, out var lid))
            {
                var posicao = _posicoesCaminhos[key];
                _lruCaminhos.Remove(posicao);
                _lruCaminhos.AddLast(posicao);
                return Db.Get(lid) is { } hit ? new Node(false, drive, hit) : null;
            }
            var cur = Db.Get(startLid);
            for (var i = 1; i < parts.Length && cur != null; i++)
            {
                if (!cur.IsDir) return null;
                cur = Db.GetChild(cur.Lid, parts[i]);
                if (cur != null && !includeExcluded && Db.IsExcluded(cur.Lid)) return null;
            }
            if (cur == null) return null;
            if (includeExcluded) return new Node(false, drive, cur); // não entra no cache dos caminhos visíveis
            if (_resolved.Count >= LimiteCaminhos)
            {
                var antigo = _lruCaminhos.First!.Value;
                _lruCaminhos.RemoveFirst();
                _resolved.Remove(antigo);
                _posicoesCaminhos.Remove(antigo);
            }
            _posicoesCaminhos[key] = _lruCaminhos.AddLast(key);
            _resolved[key] = cur.Lid; // só lid: tamanho/mtime/dirty sempre vêm frescos do banco
            return new Node(false, drive, cur);
        }
    }

    /// <summary>NAME_MAX do Linux: 255 BYTES (UTF-8), não caracteres — 128 "á" já passam. O kernel do FUSE só barra acima de 1024.</summary>
    static bool TooLong(string name) => System.Text.Encoding.UTF8.GetByteCount(name) > 255;

    static (string parent, string name) Split(string path)
    {
        var trimmed = path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return (idx <= 0 ? "/" : trimmed[..idx], trimmed[(idx + 1)..]);
    }

    // ---------- atributos ----------

    /// <summary>Itens de outra drive sem permissão de escrita (e a pasta virtual): 0555/0444.</summary>
    bool IsReadOnly(Item it) => SyncEngine.IsSharedKey(it.DriveId) && _e.IsReadOnly(it.DriveId);

    uint ModeOf(Item it) => IsReadOnly(it) ? ExplicitMode | (it.IsDir ? 0x16Du /*0555*/ : 0x124u /*0444*/) : it.Mode;

    FileAttr AttrOf(Item it)
    {
        if (it.IsDir) return new FileAttr(true, 4096, it.MTime, 0, ModeOf(it), it.Lid, Db.FracaoMtime(it.Lid, it.MTime));
        long size = it.Size, blocks = 0;
        if (SyncEngine.IsContentCurrent(it, _e.Cache))
        {
            var p = _e.Cache.PathFor(it.DriveId, it.Lid);
            if (File.Exists(p))
            {
                size = new FileInfo(p).Length;
                blocks = (size + 511) / 512;
            }
        }
        return new FileAttr(false, size, it.MTime, blocks, ModeOf(it), it.Lid, Db.FracaoMtime(it.Lid, it.MTime));
    }

    public int GetAttr(string path, out FileAttr attr)
    {
        attr = default;
        var n = Resolve(path);
        if (n == null) return -Errno.ENOENT;
        if (n.Value.IsVirtualRoot)
        {
            attr = new FileAttr(true, 4096, _rootMtime, 0, ExplicitMode | 0x16D);
            return 0;
        }
        attr = AttrOf(n.Value.Item!);
        return 0;
    }

    public int ReadDir(string path, List<(string name, FileAttr attr)> entries)
    {
        var n = Resolve(path);
        if (n == null) return -Errno.ENOENT;
        if (n.Value.IsVirtualRoot)
        {
            foreach (var d in MountedDrives)
                if (Db.Get(d.RootLid) is { } raiz) entries.Add((Sanitize(d.Config.DisplayName), AttrOf(raiz)));
            foreach (var c in _e.SharedContainers)
                if (Db.Get(c.Lid) is { } raiz) entries.Add((Sanitize(c.Name), AttrOf(raiz)));
            return 0;
        }
        if (!n.Value.Item!.IsDir) return -Errno.ENOTDIR;
        foreach (var c in Db.GetChildren(n.Value.Item.Lid))
            if (!Db.IsExcluded(c.Lid)) entries.Add((c.Name, AttrOf(c)));
        return 0;
    }

    // ---------- processo chamador ----------

    public bool IsBlockedProcess(int pid)
    {
        if (pid <= 0 || _e.Config.BlockedProcesses.Count == 0) return false;
        if (_pidCache.TryGetValue(pid, out var c) && DateTime.UtcNow - c.at < TimeSpan.FromSeconds(30)) return c.blocked;
        var blocked = false;
        try
        {
            // só o nome do programa (argv[0] sem o caminho, e o comm da thread): os argumentos não contam — senão
            // "cat Fotos/thumbnail.jpg" seria tratado como gerador de miniaturas. O argv[0] cobre threads renomeadas
            // (comm "QThread") e nomes maiores que os 15 caracteres do comm (gnome-desktop-thumbnailer).
            var argv0 = Path.GetFileName(File.ReadAllText($"/proc/{pid}/cmdline").Split('\0')[0]);
            var comm = File.ReadAllText($"/proc/{pid}/comm").Trim();
            blocked = _e.Config.BlockedProcesses.Any(b =>
                argv0.Contains(b, StringComparison.OrdinalIgnoreCase) || comm.Contains(b, StringComparison.OrdinalIgnoreCase));
        }
        catch { }
        if (_pidCache.Count > 2000) _pidCache.Clear();
        _pidCache[pid] = (blocked, DateTime.UtcNow);
        return blocked;
    }

    // ---------- downloads dentro de handlers do FUSE ----------

    /// <summary>
    /// Pergunta (na thread do handler) se o kernel cancelou a requisição atual — o processo que chamou recebeu
    /// um sinal (Ctrl-C). Ligado a fuse_req_interrupted() pelo FuseHost; null fora do FUSE (testes).
    /// </summary>
    public Func<bool>? InterruptRequested { get; set; }

    sealed class InterruptedException : Exception;

    /// <summary>
    /// Baixa o conteúdo esperando na thread do handler (no FUSE, uma thread de SlowOps, não a do loop), mas
    /// consultando a cada 100 ms se a requisição foi interrompida — aí cancela o download e o open devolve
    /// EINTR, em vez de seguir baixando para um processo que já morreu. O parcial fica para retomar depois.
    /// </summary>
    void DownloadInterruptibly(long lid) => RunInterruptibly(ct => _e.EnsureContentAsync(lid, ct));

    /// <summary>Espera <paramref name="work"/> na thread do handler, cancelando-o se a requisição for interrompida.</summary>
    void RunInterruptibly(Func<CancellationToken, Task> work)
    {
        var check = InterruptRequested;
        if (check == null)
        {
            work(_e.Token).GetAwaiter().GetResult();
            return;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_e.Token);
        var task = work(cts.Token);
        var interrupted = false;
        while (!((IAsyncResult)task).AsyncWaitHandle.WaitOne(100))
        {
            if (interrupted || !check()) continue;
            interrupted = true;
            cts.Cancel();
        }
        try { task.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (interrupted) { throw new InterruptedException(); }
    }

    // ---------- abrir / criar ----------

    void Reserve(long lid) => _openCount.AddOrUpdate(lid, 1, (_, v) => v + 1);

    void Unreserve(long lid)
    {
        if (_openCount.AddOrUpdate(lid, 0, (_, v) => Math.Max(0, v - 1)) > 0) return;
        _openCount.TryRemove(new KeyValuePair<long, int>(lid, 0));
        _mtimeOverride.TryRemove(lid, out _);
    }

    /// <summary>Registra o handle de um item já reservado com <see cref="Reserve"/>.</summary>
    ulong Register(Item it, SafeFileHandle fh, bool writable, bool wrote, StreamSession? stream = null)
    {
        var id = (ulong)Interlocked.Increment(ref _nextFh);
        _handles[id] = new Handle { Lid = it.Lid, DriveId = it.DriveId, File = fh, Writable = writable, Wrote = wrote, Stream = stream };
        return id;
    }

    /// <summary>
    /// Testes: chamado no <see cref="Open"/> com "reservando" (antes da reserva) e "conferido" (depois de decidir se
    /// o conteúdo do cache vale), para forçar intercalações com o despejo do cache.
    /// </summary>
    public Action<string, long>? OpenStepForTests { get; set; }

    /// <summary>Intercalações determinísticas depois de Resolve, antes do monitor da mutação.</summary>
    public Action<string>? MutationStepForTests { get; set; }

    public int Open(string path, int flags, int callerPid, out ulong fh)
    {
        fh = 0;
        var n = Resolve(path);
        if (n == null) return -Errno.ENOENT;
        if (n.Value.IsDir) return -Errno.EISDIR;
        var item = n.Value.Item!;
        var write = (flags & OpenFlags.AccMode) != 0;
        var trunc = (flags & OpenFlags.Trunc) != 0;
        if ((write || trunc) && IsReadOnly(item)) return -Errno.EACCES; // compartilhado só para leitura

        // reserva antes de baixar: o limite do cache (LRU) não despeja o arquivo entre o download e a abertura
        OpenStepForTests?.Invoke("reservando", item.Lid);
        Reserve(item.Lid);
        var opened = false;
        try
        {
            // Relê o item DEPOIS de reservar (Db.Get passa pelo Db.Lock): um despejo que conferiu IsOpen antes da
            // reserva segura o lock até apagar, então aqui já se vê o resultado dele; os seguintes veem a reserva.
            // Decidir pela cópia lida antes da reserva dava EIO num arquivo limpo que o LRU acabara de despejar.
            if (Db.Get(item.Lid) is not { } fresh) return -Errno.ENOENT;
            item = fresh;
            var current = SyncEngine.IsContentCurrent(item, _e.Cache);
            OpenStepForTests?.Invoke("conferido", item.Lid);
            if (trunc)
            {
                lock (Db.Lock)
                {
                    File.WriteAllBytes(_e.Cache.PathFor(item.DriveId, item.Lid), []);
                    MarkDirty(item.Lid, 0);
                }
            }
            else if (!current)
            {
                // indexador/gerador de miniaturas: "permissão negada", distinto de falta de conexão
                if (IsBlockedProcess(callerPid)) return -Errno.EACCES;
                // arquivo grande, só leitura: responde já e baixa só os blocos lidos (escrita continua exigindo tudo)
                if (!write)
                {
                    StreamSession? stream;
                    try { stream = _e.TryOpenStream(item); }
                    catch (Exception ex)
                    {
                        _e.Activity.Add(ActivityKind.Error, $"Não foi possível abrir '{item.Name}' para leitura por faixas: {ex.Message}");
                        return -Errors.ErrnoForDownload(ex);
                    }
                    if (stream != null)
                    {
                        SafeFileHandle sh;
                        try { sh = stream.OpenReadHandle(); }
                        catch (Exception ex)
                        {
                            _e.ReleaseStream(stream);
                            _e.Activity.Add(ActivityKind.Error, $"Não foi possível abrir '{item.Name}': {ex.Message}");
                            return -Errors.ErrnoForDownload(ex);
                        }
                        fh = Register(item, sh, false, false, stream);
                        opened = true;
                        return 0;
                    }
                }
                try
                {
                    DownloadInterruptibly(item.Lid);
                }
                catch (FileNotFoundException) { return -Errno.ENOENT; }
                catch (InterruptedException) { return -Errno.EINTR; } // Ctrl-C no processo que abriu
                catch (TransferCancelledException) { return -Errno.ECANCELED; } // botão "cancelar" na janela
                catch (Exception ex)
                {
                    _e.Activity.Add(ActivityKind.Error, $"Não foi possível baixar '{item.Name}': {ex.Message}");
                    return -Errors.ErrnoForDownload(ex);
                }
            }
            else Db.Touch(item.Lid);

            SafeFileHandle handle;
            lock (Db.Lock)
            {
                var cur = Db.Get(item.Lid);
                if (cur == null) return -Errno.ENOENT;
                var cachePath = _e.Cache.PathFor(cur.DriveId, cur.Lid);
                if (!File.Exists(cachePath))
                {
                    // criar um arquivo vazio aqui mostraria (e depois enviaria) o arquivo zerado
                    _e.Activity.Add(ActivityKind.Error, $"O conteúdo local de '{cur.Name}' não está no cache; o arquivo não foi aberto.");
                    return -Errno.EIO;
                }
                handle = File.OpenHandle(cachePath, FileMode.Open, write ? FileAccess.ReadWrite : FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
            }
            fh = Register(item, handle, write, trunc);
            opened = true;
            return 0;
        }
        finally
        {
            if (!opened) Unreserve(item.Lid);
        }
    }

    public int Create(string path, int flags, out ulong fh) => Create(path, flags, 0x1A4 /*0644*/, out fh);

    /// <param name="mode">Permissões pedidas no open(O_CREAT) (já com a umask aplicada pelo kernel).</param>
    public int Create(string path, int flags, uint mode, out ulong fh)
    {
        fh = 0;
        var (parentPath, name) = Split(path);
        if (TooLong(name)) return -Errno.ENAMETOOLONG;
        var p = Resolve(parentPath);
        if (p == null) return -Errno.ENOENT;
        if (p.Value.IsVirtualRoot) return -Errno.EACCES;
        var parent = p.Value.Item!;
        if (!parent.IsDir) return -Errno.ENOTDIR;
        if (IsReadOnly(parent)) return -Errno.EACCES;

        Item item;
        MutationStepForTests?.Invoke("create");
        lock (Db.Lock)
        {
            if (Db.Get(parent.Lid) is not { IsDir: true } atual) return -Errno.ENOENT;
            parent = atual;
            var existing = Db.GetChildNoCase(parent.Lid, name);
            if (existing != null)
            {
                if (existing.Name == name && !existing.IsDir && (flags & OpenFlags.Excl) == 0)
                    goto reabrir; // sair do monitor antes de qualquer download
                return -Errno.EEXIST;
            }
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            item = new Item
            {
                DriveId = parent.DriveId, ParentLid = parent.Lid, Name = name, IsDir = false, MTime = now, CTime = now,
                Dirty = true, DirtyGen = 1, LocalOnly = parent.LocalOnly || _e.Names.IsLocalOnly(name), LastAccess = now,
                Mode = NormalizeMode(mode, isDir: false),
            };
            Db.Insert(item);
            File.WriteAllBytes(_e.Cache.PathFor(item.DriveId, item.Lid), []);
        }
        var handle = File.OpenHandle(_e.Cache.PathFor(item.DriveId, item.Lid), FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        Reserve(item.Lid);
        fh = Register(item, handle, true, wrote: true);
        return 0;

    reabrir:
        return Open(path, flags, 0, out fh);
    }

    void MarkDirty(long lid, long? size, long? mtime = null)
    {
        lock (Db.Lock)
        {
            var cur = Db.Get(lid);
            if (cur == null) return;
            cur.Dirty = true;
            cur.DirtyGen++;
            cur.MTime = mtime ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (mtime == null) Db.DefinirFracaoMtime(lid, cur.MTime, 0);
            if (size != null) cur.Size = size.Value;
            Db.Update(cur);
        }
    }

    void EnqueueUpload(long lid)
    {
        var cur = Db.Get(lid);
        if (cur == null || cur.LocalOnly || !cur.Dirty) return;
        Db.Enqueue(cur.DriveId, OpKind.Upload, lid);
        _e.NotifyLocalChange(cur.DriveId);
    }

    // ---------- leitura / escrita ----------

    long _reads;
    /// <summary>read() recebidos pelo VFS (diagnóstico/testes; o kernel pode atender releituras do cache de página).</summary>
    public long ReadCount => Interlocked.Read(ref _reads);

    /// <summary>true se o handle lê por faixas (arquivo ainda incompleto no cache): o FUSE não dá keep_cache ao próximo open.</summary>
    public bool IsStreaming(ulong fh) => _handles.TryGetValue(fh, out var h) && h.Stream != null;

    /// <summary>true se ler essa faixa precisa de rede (bloco ainda não baixado): o FUSE roda o read fora do loop.</summary>
    public bool ReadNeedsNetwork(ulong fh, long offset, long size) =>
        _handles.TryGetValue(fh, out var h) && h.Stream is { } s && !s.HasRange(offset, size);

    /// <summary>Mesmo hit streaming pode perder a bitmap antes de TryRead; jamais permitir rede no loop FUSE.</summary>
    public bool ReadOutsideLoop(ulong fh) => IsStreaming(fh);

    public bool OpenNeedsNetwork(string? path, int flags)
    {
        if (path == null || (flags & OpenFlags.Trunc) != 0) return false;
        var item = Resolve(path)?.Item;
        return item != null && !SyncEngine.IsContentCurrent(item, _e.Cache);
    }

    public bool RenameNeedsNetwork(string? from, string? to)
    {
        if (from == null || to == null) return false;
        var src = Resolve(from)?.Item;
        if (src == null) return false;
        var (pai, nome) = Split(to);
        var parent = Resolve(pai)?.Item;
        var local = parent?.LocalOnly == true || _e.Names.IsLocalOnly(nome);
        return local && Db.GetSubtree(src.Lid).Any(x => !x.IsDir && !SyncEngine.IsContentCurrent(x, _e.Cache))
            || !src.IsDir && !CanMoveRemote(src) && !SyncEngine.IsContentCurrent(src, _e.Cache);
    }

    public int Read(ulong fh, Span<byte> buffer, long offset)
    {
        Interlocked.Increment(ref _reads);
        if (!_handles.TryGetValue(fh, out var h)) return -Errno.EBADF;
        if (h.Stream is { } stream) return ReadStreaming(h, stream, buffer, offset);
        try { return RandomAccess.Read(h.File, buffer, offset); }
        catch (IOException) { return -Errno.EIO; }
    }

    /// <summary>
    /// Leitura por faixas: blocos já baixados saem do arquivo de trabalho; os que faltam são pedidos (Range) e
    /// esperados aqui — no FUSE numa thread de SlowOps (ver <see cref="ReadNeedsNetwork"/>), cancelável com Ctrl-C.
    /// Falha de rede vira erro para o processo, nunca dados zerados.
    /// </summary>
    int ReadStreaming(Handle h, StreamSession s, Span<byte> buffer, long offset)
    {
        var len = (int)Math.Clamp(s.Size - offset, 0, buffer.Length);
        if (offset == h.NextRead) h.SequentialBytes += len;
        else h.SequentialBytes = 0;
        h.NextRead = offset + len;
        if (len > 0 && h.SequentialBytes >= s.ReadAheadTrigger) s.StartBackgroundFill(offset);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var n = s.TryRead(buffer, offset);
                if (n >= 0) return n;
                RunInterruptibly(ct => s.EnsureRangeAsync(offset, len, ct));
            }
            catch (InterruptedException) { return -Errno.EINTR; }
            catch (TransferCancelledException) { return -Errno.ECANCELED; }
            catch (Exception ex)
            {
                if (!h.ReportedError)
                {
                    h.ReportedError = true;
                    _e.Activity.Add(ActivityKind.Error, $"Não foi possível ler '{s.Name}': {ex.Message}");
                }
                else _e.Activity.Debug($"read {s.Name} @{offset}: {ex.Message}");
                return -Errors.ErrnoForDownload(ex);
            }
        }
        return -Errno.EIO; // blocos descartados repetidamente durante a leitura (hash divergente)
    }

    public int Write(ulong fh, ReadOnlySpan<byte> data, long offset)
    {
        if (!_handles.TryGetValue(fh, out var h)) return -Errno.EBADF;
        if (!h.Writable) return -Errno.EBADF;
        try
        {
            RandomAccess.Write(h.File, data, offset);
        }
        catch (IOException ex) when (Errors.IsDiskFull(ex)) // HResult = errno no Unix (a mensagem é traduzida)
        {
            return -Errno.ENOSPC;
        }
        catch (IOException) { return -Errno.EIO; }
        lock (Db.Lock) // a identidade do fd pode mudar na cópia de conflito (MoveOpenHandles, sob este mesmo lock)
        {
            if (!_mtimeOverride.IsEmpty) _mtimeOverride.TryRemove(h.Lid, out _); // escrita depois do utimens: vale a hora atual
            h.Wrote = true;
            MarkDirty(h.Lid, null); // cada write altera mtime, não só o primeiro
        }
        return data.Length;
    }

    public int Truncate(string? path, long size, ulong? fh)
    {
        Handle? h = null;
        if (fh is { } f && f != 0) _handles.TryGetValue(f, out h);
        long lid;
        if (h != null) lid = h.Lid;
        else
        {
            if (path == null) return -Errno.EBADF;
            var n = Resolve(path);
            if (n == null) return -Errno.ENOENT;
            if (n.Value.IsDir) return -Errno.EISDIR;
            lid = n.Value.Item!.Lid;
        }
        // sem handle (truncate por caminho) nada impede o LRU de despejar o arquivo entre o download e o SetLength:
        // reserva como o Open e relê o item depois (Db.Get passa pelo Db.Lock)
        if (h == null) Reserve(lid);
        try { return TruncateReserved(path, lid, size, h); }
        finally { if (h == null) Unreserve(lid); }
    }

    int TruncateReserved(string? path, long lid, long size, Handle? h)
    {
        var item = Db.Get(lid);
        if (item != null && IsReadOnly(item)) return -Errno.EACCES;
        if (item == null)
        {
            if (h == null) return -Errno.ENOENT;
            // apagado com o fd aberto (unlink + ftruncate): só o conteúdo do handle existe
            try { RandomAccess.SetLength(h.File, size); }
            catch (IOException) { return -Errno.EIO; }
            return 0;
        }
        _mtimeOverride.TryRemove(lid, out _);
        var cachePath = _e.Cache.PathFor(item.DriveId, lid);
        try
        {
            if (size == 0) { if (!File.Exists(cachePath)) File.WriteAllBytes(cachePath, []); }
            else if (!SyncEngine.IsContentCurrent(item, _e.Cache))
                DownloadInterruptibly(lid);
            OpenStepForTests?.Invoke("truncate-conferido", lid);

            if (h != null) RandomAccess.SetLength(h.File, size);
            else
            {
                using var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                fs.SetLength(size);
            }
        }
        catch (InterruptedException) { return -Errno.EINTR; }
        catch (Exception ex)
        {
            _e.Activity.Debug($"truncate {path}: {ex.Message}");
            return -Errors.ErrnoForDownload(ex);
        }
        MarkDirty(lid, size);
        if (h != null) h.Wrote = true;
        else EnqueueUpload(lid);
        return 0;
    }

    /// <summary>Quantas vezes <see cref="CopyRange"/> foi usado (diagnóstico/testes).</summary>
    public long CopyRangeCalls => Interlocked.Read(ref _copyRangeCalls);
    long _copyRangeCalls;

    /// <summary>
    /// copy_file_range entre dois arquivos abertos da montagem: copia pelo cache, num pedido só ao FUSE (sem ele o
    /// kernel faz read/write de 128 KB cada). Passa por <see cref="Read"/>/<see cref="Write"/>, então marca o destino
    /// como alterado como uma escrita normal. Para no fim da origem; devolve os bytes copiados ou errno negativo.
    /// </summary>
    public long CopyRange(ulong fhIn, long offIn, ulong fhOut, long offOut, long length)
    {
        Interlocked.Increment(ref _copyRangeCalls);
        if (!_handles.ContainsKey(fhIn) || !_handles.ContainsKey(fhOut)) return -Errno.EBADF;
        var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            long done = 0;
            while (done < length)
            {
                var want = (int)Math.Min(buf.Length, length - done);
                var n = Read(fhIn, buf.AsSpan(0, want), offIn + done);
                if (n < 0) return done > 0 ? done : n;
                if (n == 0) break;
                var w = Write(fhOut, buf.AsSpan(0, n), offOut + done);
                if (w < 0) return done > 0 ? done : w;
                done += n;
                if (n < want) break;
            }
            return done;
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }
    }

    public int Flush(ulong fh) => _handles.ContainsKey(fh) ? 0 : -Errno.EBADF;

    /// <summary>
    /// fsync = durável neste disco (cache) e marcado como alterado. O envio fica para o close, como em qualquer
    /// escrita: enviar aqui subiria um estado intermediário de um arquivo ainda aberto (editores e bancos de dados
    /// chamam fsync no meio da gravação) e gastaria um envio a mais.
    /// </summary>
    public int Fsync(ulong fh)
    {
        if (!_handles.TryGetValue(fh, out var h)) return -Errno.EBADF;
        if (h.Wrote)
        {
            RandomAccess.FlushToDisk(h.File);
            var p = _e.Cache.PathFor(h.DriveId, h.Lid);
            if (File.Exists(p)) MarkDirty(h.Lid, new FileInfo(p).Length, Db.Get(h.Lid)?.MTime);
        }
        return 0;
    }

    /// <summary>
    /// Enfileira as alterações de fds ainda abertos antes de desmontar/parar o motor: fuse_session_exit pode encerrar
    /// sem receber release (o app que abriu o arquivo continua vivo). Não fecha os fds: eles ainda pertencem ao FUSE.
    /// </summary>
    public void CommitOpenWrites()
    {
        foreach (var h in _handles.Values.Where(h => h.Wrote)) CommitWrite(h);
    }

    void CommitWrite(Handle h)
    {
        var p = _e.Cache.PathFor(h.DriveId, h.Lid);
        if (!File.Exists(p)) return;
        MarkDirty(h.Lid, new FileInfo(p).Length, Db.Get(h.Lid)?.MTime);
        EnqueueUpload(h.Lid);
    }

    public int Release(ulong fh)
    {
        if (!_handles.TryRemove(fh, out var h)) return 0;
        try { h.File.Dispose(); } catch { }
        if (h.Stream != null) _e.ReleaseStream(h.Stream);
        if (h.Wrote) CommitWrite(h);
        Unreserve(h.Lid);
        return 0;
    }

    // ---------- operações por handle (fstat, opendir/readdir; caminho nulo com nullpath_ok) ----------

    /// <summary>Atributos pelo handle aberto (fstat). Funciona também depois de o arquivo ser apagado.</summary>
    public int GetAttr(string? path, ulong fh, out FileAttr attr)
    {
        attr = default;
        if (fh != 0 && _handles.TryGetValue(fh, out var h))
        {
            long len;
            try { len = RandomAccess.GetLength(h.File); }
            catch (IOException) { return -Errno.EIO; }
            var it = Db.Get(h.Lid);
            var mtime = it?.MTime ?? new DateTimeOffset(File.GetLastWriteTimeUtc(h.File)).ToUnixTimeMilliseconds();
            attr = new FileAttr(false, len, mtime, (len + 511) / 512, it != null ? ModeOf(it) : 0, h.Lid, Db.FracaoMtime(h.Lid, mtime));
            return 0;
        }
        if (fh != 0 && _dirHandles.TryGetValue(fh, out var dirLid))
        {
            var it = dirLid == 0 ? null : Db.Get(dirLid);
            attr = it != null ? AttrOf(it) : new FileAttr(true, 4096, _rootMtime, 0, ExplicitMode | 0x16D);
            return 0;
        }
        return path == null ? -Errno.EBADF : GetAttr(path, out attr);
    }

    /// <summary>getattr sem fh de inode apagado ainda aberto (ex.: /proc/self/fd/N).</summary>
    public int GetAttrByLid(long lid, out FileAttr attr)
    {
        foreach (var (fh, h) in _handles)
            if (h.Lid == lid) return GetAttr(null, fh, out attr);
        attr = default;
        return -Errno.ENOENT;
    }

    public int OpenDir(string path, out ulong fh)
    {
        fh = 0;
        var n = Resolve(path);
        if (n == null) return -Errno.ENOENT;
        if (!n.Value.IsDir) return -Errno.ENOTDIR;
        fh = (ulong)Interlocked.Increment(ref _nextFh);
        _dirHandles[fh] = n.Value.IsVirtualRoot ? 0 : n.Value.Item!.Lid;
        return 0;
    }

    public int ReadDir(ulong fh, List<(string name, FileAttr attr)> entries)
    {
        if (!_dirHandles.TryGetValue(fh, out var lid)) return -Errno.EBADF;
        if (lid == 0) return ReadDir("/", entries);
        var dir = Db.Get(lid);
        if (dir == null) return 0; // removida com o diretório aberto: vazia
        foreach (var c in Db.GetChildren(lid))
            if (!Db.IsExcluded(c.Lid)) entries.Add((c.Name, AttrOf(c)));
        return 0;
    }

    public int ReleaseDir(ulong fh)
    {
        _dirHandles.TryRemove(fh, out _);
        return 0;
    }

    // ---------- pastas / exclusão ----------

    public int Mkdir(string path) => Mkdir(path, 0x1ED /*0755*/);

    public int Mkdir(string path, uint mode)
    {
        var (parentPath, name) = Split(path);
        if (TooLong(name)) return -Errno.ENAMETOOLONG;
        var p = Resolve(parentPath);
        if (p == null) return -Errno.ENOENT;
        if (p.Value.IsVirtualRoot) return -Errno.EACCES;
        var parent = p.Value.Item!;
        if (!parent.IsDir) return -Errno.ENOTDIR;
        if (IsReadOnly(parent)) return -Errno.EACCES;
        MutationStepForTests?.Invoke("mkdir");
        lock (Db.Lock)
        {
            if (Db.Get(parent.Lid) is not { IsDir: true } atual) return -Errno.ENOENT;
            parent = atual;
            if (Db.GetChildNoCase(parent.Lid, name) != null) return -Errno.EEXIST;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var item = new Item
            {
                DriveId = parent.DriveId, ParentLid = parent.Lid, Name = name, IsDir = true, MTime = now, CTime = now,
                LocalOnly = parent.LocalOnly || _e.Names.IsLocalOnly(name), Mode = NormalizeMode(mode, isDir: true),
            };
            Db.Insert(item);
            if (!item.LocalOnly) Db.Enqueue(item.DriveId, OpKind.Mkdir, item.Lid);
        }
        _e.NotifyLocalChange(parent.DriveId);
        return 0;
    }

    /// <summary>Remove item (e subárvore) localmente e agenda exclusão na nuvem.</summary>
    void RemoveItem(Item item)
    {
        var cloudItems = 0;
        lock (Db.Lock)
        {
            if (item.ItemId != null && !item.LocalOnly)
                Db.Enqueue(item.DriveId, OpKind.Delete, item.Lid, item.ItemId, Db.Get(item.ParentLid)?.ItemId, item.Name, item.IsDir);
            foreach (var x in Db.GetSubtree(item.Lid))
            {
                if (x.ItemId != null && !x.LocalOnly) cloudItems++;
                Db.RemoveOpsForLid(x.Lid);
                if (!x.IsDir) _e.Cache.Delete(x.DriveId, x.Lid);
                Db.Delete(x.Lid);
            }
        }
        _e.RecordLocalDeletes(item.DriveId, cloudItems);
        _e.NotifyLocalChange(item.DriveId);
    }

    public int Unlink(string path)
    {
        var n = Resolve(path);
        if (n == null) return -Errno.ENOENT;
        if (n.Value.IsVirtualRoot || n.Value.IsDriveRoot || _e.IsSharedTop(n.Value.Item!)) return -Errno.EPERM;
        if (n.Value.Item!.IsDir) return -Errno.EISDIR;
        if (IsReadOnly(n.Value.Item)) return -Errno.EACCES;
        RemoveItem(n.Value.Item);
        return 0;
    }

    public int Rmdir(string path)
    {
        var n = Resolve(path);
        if (n == null) return -Errno.ENOENT;
        if (n.Value.IsVirtualRoot || n.Value.IsDriveRoot || _e.IsSharedTop(n.Value.Item!)) return -Errno.EPERM;
        var item = n.Value.Item!;
        if (!item.IsDir) return -Errno.ENOTDIR;
        if (IsReadOnly(item)) return -Errno.EACCES;
        if (TemFilhos(item)) return -Errno.ENOTEMPTY;
        RemoveItem(item);
        return 0;
    }

    bool TemFilhos(Item pasta)
    {
        var filhos = Db.GetChildren(pasta.Lid);
        if (filhos.Count == 0) return false;
        if (filhos.All(f => Db.IsExcluded(f.Lid)))
            _e.Activity.Add(ActivityKind.Info, $"Não foi possível remover ou substituir '{pasta.Name}': há filhos excluídos da sincronização seletiva. Reinclua-os antes de apagar a pasta; eles continuam na nuvem.");
        return true;
    }

    // ---------- renomear / mover ----------

    public const uint RenameNoReplace = 1, RenameExchange = 2;

    // Uma origem remota sem alterações locais pode ser movida pelo id (inclui somente-online).
    // Dirty/LocalOnly/ItemId null também tornam IsContentCurrent true, mas NÃO permitem mover sem upload.
    bool CanMoveRemote(Item src) => src.ItemId != null && !src.Dirty && !src.LocalOnly
        && !_handles.Values.Any(h => h.Lid == src.Lid && h.Wrote);

    public int Rename(string from, string to, uint flags)
    {
        if ((flags & RenameExchange) != 0) return -Errno.EINVAL;
        var s = Resolve(from);
        if (s == null) return -Errno.ENOENT;
        if (s.Value.IsVirtualRoot || s.Value.IsDriveRoot || _e.IsSharedTop(s.Value.Item!)) return -Errno.EPERM;
        var src = s.Value.Item!;
        var (dstParentPath, newName) = Split(to);
        if (TooLong(newName)) return -Errno.ENAMETOOLONG;
        var dp = Resolve(dstParentPath);
        if (dp == null) return -Errno.ENOENT;
        if (dp.Value.IsVirtualRoot) return -Errno.EACCES;
        var dstParent = dp.Value.Item!;
        if (!dstParent.IsDir) return -Errno.ENOTDIR;
        if (IsReadOnly(src) || IsReadOnly(dstParent)) return -Errno.EACCES;
        if (dstParent.DriveId != src.DriveId) return -Errno.EXDEV; // mv faz copiar+apagar entre drives
        if (Db.GetChild(dstParent.Lid, newName) is { } replaced && _e.IsSharedTop(replaced)) return -Errno.EPERM;

        if (src.IsDir && (dstParent.Lid == src.Lid || Db.GetAncestors(dstParent.Lid).Any(a => a.Lid == src.Lid)))
            return -Errno.EINVAL;

        // Conteúdo que precisa estar local antes de mexer no banco (downloads fora do lock):
        // - sobrescrever com origem local/alterada (vira nova versão do destino; origem remota limpa usa Move);
        // - passar a nome só local (deixa de sincronizar e é excluído da nuvem): a subárvore inteira.
        var becomesLocalOnly = !src.LocalOnly && src.ItemId != null && (dstParent.LocalOnly || _e.Names.IsLocalOnly(newName));
        var needed = becomesLocalOnly
            ? Db.GetSubtree(src.Lid).Where(x => !x.IsDir).ToList()
            : !src.IsDir && !CanMoveRemote(src)
                && Db.GetChild(dstParent.Lid, newName) is { IsDir: false, ItemId: not null } ? [src] : [];
        foreach (var x in needed) Reserve(x.Lid); // o LRU não despeja o que acabou de ser baixado
        try
        {
            foreach (var x in needed.Where(x => !SyncEngine.IsContentCurrent(x, _e.Cache)))
            {
                try { DownloadInterruptibly(x.Lid); }
                catch (FileNotFoundException) { }
                catch (InterruptedException) { return -Errno.EINTR; }
                catch (Exception ex)
                {
                    _e.Activity.Add(ActivityKind.Error, $"Não foi possível renomear '{src.Name}' para '{newName}': " +
                                                        $"falha ao baixar '{x.Name}' ({ex.Message}). Nada foi alterado.");
                    return -Errors.ErrnoForDownload(ex);
                }
            }
            return RenameLocked(src, dstParent, newName, flags);
        }
        finally
        {
            foreach (var x in needed) Unreserve(x.Lid);
        }
    }

    int RenameLocked(Item src, Item dstParent, string newName, uint flags)
    {
        MutationStepForTests?.Invoke("rename");
        lock (Db.Lock)
        {
            if (Db.Get(src.Lid) is not { } origem || Db.Get(dstParent.Lid) is not { IsDir: true } pai) return -Errno.ENOENT;
            src = origem;
            dstParent = pai;
            if (src.IsDir && (dstParent.Lid == src.Lid || Db.GetAncestors(dstParent.Lid).Any(a => a.Lid == src.Lid))) return -Errno.EINVAL;
            var target = Db.GetChild(dstParent.Lid, newName);
            var caseClash = Db.GetChildNoCase(dstParent.Lid, newName);
            if (target == null && caseClash != null && caseClash.Lid != src.Lid) return -Errno.EEXIST;
            if (target != null && target.Lid == src.Lid) return 0;
            if (target != null && (flags & RenameNoReplace) != 0) return -Errno.EEXIST;

            var newLocalOnly = dstParent.LocalOnly || _e.Names.IsLocalOnly(newName);
            if (!src.LocalOnly && newLocalOnly && Db.GetSubtree(src.Lid).FirstOrDefault(x => !x.IsDir && !_e.Cache.Exists(x.DriveId, x.Lid)) is { } missing)
            {
                // vai ser excluído da nuvem: sem a cópia local de tudo, isso perderia dados
                _e.Activity.Add(ActivityKind.Error, $"Não foi possível renomear '{src.Name}' para '{newName}': '{missing.Name}' não está baixado. Nada foi alterado.");
                return -Errno.EIO;
            }

            if (target != null)
            {
                if (target.IsDir)
                {
                    if (!src.IsDir) return -Errno.EISDIR;
                    if (TemFilhos(target)) return -Errno.ENOTEMPTY;
                    RemoveItem(target);
                }
                else
                {
                    if (src.IsDir) return -Errno.ENOTDIR;
                    if (target.ItemId != null && !target.LocalOnly && !newLocalOnly && !CanMoveRemote(src))
                    {
                        // "salvamento atômico" de editores (grava temp + renomeia por cima):
                        // vira nova versão do arquivo existente, preservando histórico de versões.
                        var srcPath = _e.Cache.PathFor(src.DriveId, src.Lid);
                        File.Move(srcPath, _e.Cache.PathFor(target.DriveId, target.Lid), true);
                        target.Dirty = true;
                        target.DirtyGen++;
                        target.Size = new FileInfo(_e.Cache.PathFor(target.DriveId, target.Lid)).Length;
                        target.MTime = src.MTime;
                        target.Mode = src.Mode; // como no rename POSIX: vale o modo do arquivo que chegou
                        Db.Update(target);
                        if (src.ItemId != null && !src.LocalOnly)
                            Db.Enqueue(src.DriveId, OpKind.Delete, src.Lid, src.ItemId, null, src.Name);
                        Db.RemoveOpsForLid(src.Lid);
                        Db.Delete(src.Lid);
                        MoveOpenHandles(src.Lid, target.Lid);
                        Db.Enqueue(target.DriveId, OpKind.Upload, target.Lid);
                        _e.NotifyLocalChange(target.DriveId);
                        return 0;
                    }
                    // mv remoto mantém id/histórico da ORIGEM; o destino é desvinculado (fds antigos continuam).
                    // A exclusão fica antes do Move na fila: não depende de conflictBehavior=replace da Graph.
                    RemoveItem(target);
                }
            }

            var wasLocalOnly = src.LocalOnly;
            src.ParentLid = dstParent.Lid;
            src.Name = newName;
            src.LocalOnly = newLocalOnly;
            Db.Update(src);

            if (!wasLocalOnly && newLocalOnly)
            {
                // passou a ser só local (ex.: renomeado para nome inválido): remove da nuvem
                if (src.ItemId != null) Db.Enqueue(src.DriveId, OpKind.Delete, src.Lid, src.ItemId, null, src.Name);
                foreach (var x in Db.GetSubtree(src.Lid))
                {
                    Db.RemoveOpsForLid(x.Lid);
                    x.LocalOnly = true;
                    x.ItemId = null;
                    if (!x.IsDir) x.Dirty = true; // conteúdo garantido no cache acima
                    Db.Update(x);
                }
            }
            else if (wasLocalOnly && !newLocalOnly)
            {
                // temporário renomeado para nome definitivo: começa a sincronizar
                foreach (var x in PreOrder(src))
                {
                    var parentLocal = x.Lid != src.Lid && Db.Get(x.ParentLid) is { LocalOnly: true };
                    x.LocalOnly = parentLocal || (x.Lid != src.Lid && _e.Names.IsLocalOnly(x.Name));
                    if (!x.LocalOnly)
                    {
                        if (x.IsDir) Db.Enqueue(x.DriveId, OpKind.Mkdir, x.Lid);
                        else
                        {
                            x.Dirty = true;
                            x.DirtyGen++;
                            Db.Enqueue(x.DriveId, OpKind.Upload, x.Lid);
                        }
                    }
                    Db.Update(x);
                }
            }
            else if (!newLocalOnly && src.ItemId != null)
            {
                Db.Enqueue(src.DriveId, OpKind.Move, src.Lid);
            }
        }
        _e.NotifyLocalChange(src.DriveId);
        return 0;
    }

    List<Item> PreOrder(Item root)
    {
        var list = new List<Item>();
        var stack = new Stack<Item>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var x = stack.Pop();
            list.Add(x);
            if (x.IsDir)
                foreach (var c in Db.GetChildren(x.Lid).AsEnumerable().Reverse())
                    stack.Push(c);
        }
        return list;
    }

    void MoveOpenHandles(long fromLid, long toLid)
    {
        foreach (var h in _handles.Values.Where(h => h.Lid == fromLid))
        {
            h.Lid = toLid;
            // a cópia pode já ter sido enviada quando vier outra escrita por este fd: ela precisa marcar Dirty de novo
            h.Wrote = false;
            Reserve(toLid);
            Unreserve(fromLid);
        }
    }

    public int Utimens(string? path, long? mtimeMs, ulong fh = 0, int subMtimeNs = 0)
    {
        long lid;
        string driveId;
        if (fh != 0 && _handles.TryGetValue(fh, out var h)) (lid, driveId) = (h.Lid, h.DriveId);
        else if (fh != 0 && _dirHandles.TryGetValue(fh, out var dirLid))
        {
            if (dirLid == 0) return 0;
            lid = dirLid;
            driveId = Db.Get(dirLid)?.DriveId ?? "";
        }
        else
        {
            if (path == null) return -Errno.EBADF;
            var n = Resolve(path);
            if (n == null) return -Errno.ENOENT;
            if (n.Value.IsVirtualRoot || n.Value.IsDriveRoot) return 0;
            (lid, driveId) = (n.Value.Item!.Lid, n.Value.Item.DriveId);
        }
        if (SyncEngine.IsSharedKey(driveId) && _e.IsReadOnly(driveId)) return -Errno.EACCES;
        var mtime = mtimeMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (Db.Lock)
        {
            var it = Db.Get(lid);
            if (it == null) return 0; // apagado com o fd aberto
            if (it.Lid == _e.GetDrive(it.DriveId)?.RootLid) return 0;
            it.MTime = mtime;
            Db.DefinirFracaoMtime(lid, mtime, subMtimeNs);
            Db.Update(it);
            // item novo ainda sem id remoto: o envio pendente já leva a data (e reenfileira se ela mudar durante o envio)
            if (!it.LocalOnly && !it.IsDir && it.ItemId != null) Db.Enqueue(it.DriveId, OpKind.SetTime, it.Lid);
        }
        if (mtimeMs != null && _handles.Values.Any(x => x.Lid == lid && x.Writable)) _mtimeOverride[lid] = mtime;
        else _mtimeOverride.TryRemove(lid, out _);
        _e.NotifyLocalChange(driveId);
        return 0;
    }

    /// <summary>Marca de modo explícito em <see cref="Item.Mode"/> (assim um chmod 000 não vira "padrão").</summary>
    public const uint ExplicitMode = 0x10000;

    /// <summary>Modo igual ao padrão vira 0 (padrão); os demais são guardados com <see cref="ExplicitMode"/>.</summary>
    static uint NormalizeMode(uint mode, bool isDir)
    {
        mode &= 0xFFF; // 07777
        return mode == (isDir ? 0x1EDu : 0x1A4u) ? 0 : mode | ExplicitMode;
    }

    /// <summary>
    /// chmod: o OneDrive não guarda permissões, então elas ficam só neste computador (útil para o bit de
    /// execução de scripts). Item novo vindo da nuvem usa o padrão.
    /// </summary>
    public int Chmod(string? path, uint mode, ulong fh = 0)
    {
        long lid;
        if (fh != 0 && _handles.TryGetValue(fh, out var h)) lid = h.Lid;
        else if (fh != 0 && _dirHandles.TryGetValue(fh, out var dl)) lid = dl;
        else
        {
            if (path == null) return -Errno.EBADF;
            var n = Resolve(path);
            if (n == null) return -Errno.ENOENT;
            if (n.Value.IsVirtualRoot) return -Errno.EPERM;
            lid = n.Value.Item!.Lid;
        }
        if (lid == 0) return -Errno.EPERM;
        lock (Db.Lock)
        {
            var it = Db.Get(lid);
            if (it == null) return 0; // apagado com o fd aberto
            if (IsReadOnly(it)) return -Errno.EPERM;
            it.Mode = NormalizeMode(mode, it.IsDir);
            Db.Update(it);
        }
        return 0;
    }

    public (long total, long free) StatFs(string path)
    {
        var n = Resolve(path);
        var drives = n?.Drive is { } d ? new[] { d } : MountedDrives.ToArray();
        long total = drives.Sum(x => x.QuotaTotal), used = drives.Sum(x => x.QuotaUsed);
        if (total <= 0) { total = 1L << 40; used = 0; }
        return (total, Math.Max(0, total - used));
    }

    // ---------- status / fixação (usado por IPC e xattr) ----------

    public ItemStatus StatusOf(Item it)
    {
        if (it.IsDir) return it.LocalOnly ? ItemStatus.LocalOnly : (Db.IsEffectivelyPinnedVfs(it.Lid) ? ItemStatus.AlwaysAvailable : ItemStatus.Folder);
        if (it.LocalOnly) return ItemStatus.LocalOnly;
        if (it.Dirty || it.ItemId == null) return ItemStatus.Syncing;
        if (Db.IsEffectivelyPinnedVfs(it.Lid)) return ItemStatus.AlwaysAvailable;
        return SyncEngine.IsContentCurrent(it, _e.Cache) ? ItemStatus.Available : ItemStatus.OnlineOnly;
    }

    public static string Describe(ItemStatus s) => s switch
    {
        ItemStatus.OnlineOnly => "somente online",
        ItemStatus.Available => "disponível neste dispositivo",
        ItemStatus.AlwaysAvailable => "sempre disponível neste dispositivo",
        ItemStatus.Syncing => "sincronizando",
        ItemStatus.LocalOnly => "somente local (não sincroniza)",
        _ => "pasta",
    };

    public string? GetXattr(string path, string name)
    {
        if (!XattrNames.Contains(name, StringComparer.Ordinal)) return null; // KIO pede user.xdg.*, ACL e SELinux
        var n = Resolve(path);
        if (n?.Item == null) return null;
        return name switch
        {
            "user.onedrive.status" => StatusOf(n.Value.Item).ToString(),
            "user.onedrive.id" => n.Value.Item.ItemId,
            "user.onedrive.drive" => n.Value.Drive?.Config.DisplayName,
            PinnedXattr => n.Value.Item.Pinned ? "1" : "0",
            _ => null,
        };
    }

    /// <summary>
    /// "1" = sempre manter neste dispositivo, "0" = não fixado aqui. É a marca do próprio item (como o
    /// "craftdriver pin"); um arquivo dentro de pasta fixada mostra "0", e o status efetivo continua em
    /// user.onedrive.status. Gravável: extensões e scripts fixam/liberam com setfattr.
    /// </summary>
    public const string PinnedXattr = "user.onedrive.pinned";

    const int XATTR_CREATE = 1;

    /// <summary>
    /// setxattr: só <see cref="PinnedXattr"/> é gravável ("1"/"0"/"true"/"false"). Liberar = "craftdriver unpin"
    /// (volta a ser só online). O atributo sempre existe, então XATTR_CREATE dá EEXIST.
    /// </summary>
    public int SetXattr(string path, string name, ReadOnlySpan<byte> value, int flags)
    {
        if (name != PinnedXattr) return -Errno.ENOTSUP;
        var n = Resolve(path);
        if (n == null) return -Errno.ENOENT;
        if (n.Value.Item == null) return -Errno.EPERM; // raiz virtual
        if ((flags & XATTR_CREATE) != 0) return -Errno.EEXIST;
        bool pinned;
        switch (System.Text.Encoding.UTF8.GetString(value).Trim().ToLowerInvariant())
        {
            case "1" or "true": pinned = true; break;
            case "0" or "false": pinned = false; break;
            default: return -Errno.EINVAL;
        }
        _e.SetPinned(n.Value.Item.Lid, pinned);
        return 0;
    }

    /// <summary>removexattr de <see cref="PinnedXattr"/> = liberar.</summary>
    public int RemoveXattr(string path, string name)
    {
        if (name != PinnedXattr) return -Errno.ENOTSUP;
        var n = Resolve(path);
        if (n == null) return -Errno.ENOENT;
        if (n.Value.Item == null) return -Errno.EPERM;
        _e.SetPinned(n.Value.Item.Lid, false);
        return 0;
    }

    public static readonly string[] XattrNames = ["user.onedrive.status", "user.onedrive.id", "user.onedrive.drive", PinnedXattr];
}
