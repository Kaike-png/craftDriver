using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CraftDriver.Core.Vfs;

namespace CraftDriver.Fuse;

/// <summary>
/// Binding da API de baixo nível da libfuse3 (fuse_session_new / fuse_session_loop_mt / fuse_reply_*).
/// <para>
/// Por que baixo nível: na API de alto nível cada handler ocupa uma thread do loop até devolver o resultado, e um
/// open() de arquivo só-na-nuvem espera o download inteiro — com N downloads, N threads presas, e até um stat de
/// outro arquivo esperava (docs/desempenho-fuse.md). Aqui a resposta é separada da chamada: operações que podem ir
/// à rede (open, truncate, rename, fsync) rodam em <see cref="SlowOps"/> e respondem com fuse_reply_* quando
/// terminam, liberando a thread do loop na hora. O resto (lookup, getattr, readdir, read, write…) é local e
/// responde na própria thread.
/// </para>
/// <para>
/// A API de baixo nível fala em inodes, não em caminhos: <see cref="InodeTable"/> faz a tradução para o
/// <see cref="VirtualFs"/> (que continua por caminho). Layout de structs para Linux x86_64 e aarch64 (glibc),
/// conferido com offsetof na libfuse 3.18.
/// </para>
/// </summary>
public static unsafe class FuseHost
{
    const string Lib = "fuse3";

    static FuseHost()
    {
        NativeLibrary.SetDllImportResolver(typeof(FuseHost).Assembly, (name, asm, path) =>
        {
            if (name != Lib) return IntPtr.Zero;
            foreach (var cand in new[] { "libfuse3.so.4", "libfuse3.so.3", "libfuse3.so" })
                if (NativeLibrary.TryLoad(cand, out var h)) return h;
            throw new DllNotFoundException("libfuse3 não encontrada. Instale o pacote fuse3.");
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FuseArgs
    {
        public int argc;
        public IntPtr argv;
        public int allocated;
    }

    // sessão (símbolos da ABI 3.0, exceto o loop com config: 3.12)
    [DllImport(Lib)][return: MarshalAs(UnmanagedType.U1)] static extern bool fuse_set_feature_flag(IntPtr conn, ulong flag); // libfuse ≥ 3.17.3
    [DllImport(Lib)] static extern IntPtr fuse_session_new(FuseArgs* args, IntPtr ops, nuint opSize, IntPtr userData);
    [DllImport(Lib)] static extern int fuse_session_mount(IntPtr se, [MarshalAs(UnmanagedType.LPUTF8Str)] string mountpoint);
    [DllImport(Lib)] static extern void fuse_session_unmount(IntPtr se);
    [DllImport(Lib)] static extern void fuse_session_destroy(IntPtr se);
    [DllImport(Lib)] static extern void fuse_session_exit(IntPtr se);
    [DllImport(Lib, EntryPoint = "fuse_session_loop_mt_312")] static extern int fuse_session_loop_mt(IntPtr se, IntPtr config);
    [DllImport(Lib)] static extern IntPtr fuse_loop_cfg_create();
    [DllImport(Lib)] static extern void fuse_loop_cfg_destroy(IntPtr cfg);
    [DllImport(Lib)] static extern void fuse_loop_cfg_set_max_threads(IntPtr cfg, uint n);

    // requisições (todas podem ser chamadas de qualquer thread)
    [DllImport(Lib)] static extern int fuse_reply_err(IntPtr req, int err);
    [DllImport(Lib)] static extern void fuse_reply_none(IntPtr req);
    [DllImport(Lib)] static extern int fuse_reply_entry(IntPtr req, byte* entry);
    [DllImport(Lib)] static extern int fuse_reply_create(IntPtr req, byte* entry, byte* fi);
    [DllImport(Lib)] static extern int fuse_reply_attr(IntPtr req, byte* stat, double timeout);
    [DllImport(Lib)] static extern int fuse_reply_open(IntPtr req, byte* fi);
    [DllImport(Lib)] static extern int fuse_reply_write(IntPtr req, nuint count);
    [DllImport(Lib)] static extern int fuse_reply_buf(IntPtr req, byte* buf, nuint size);
    [DllImport(Lib)] static extern int fuse_reply_statfs(IntPtr req, byte* st);
    [DllImport(Lib)] static extern int fuse_reply_xattr(IntPtr req, nuint count);
    [DllImport(Lib)] static extern nuint fuse_add_direntry(IntPtr req, byte* buf, nuint size, byte* name, byte* st, long off);
    [DllImport(Lib)] static extern nuint fuse_add_direntry_plus(IntPtr req, byte* buf, nuint size, byte* name, byte* entry, long off);
    [DllImport(Lib)] static extern IntPtr fuse_req_ctx(IntPtr req);
    /// <summary>1 se o kernel interrompeu a requisição (sinal no processo que chamou). Válido até a resposta.</summary>
    [DllImport(Lib)] static extern int fuse_req_interrupted(IntPtr req);

    [DllImport("libc", SetLastError = true)] static extern uint getuid();
    [DllImport("libc", SetLastError = true)] static extern uint getgid();

    // ---------- structs ----------

    [StructLayout(LayoutKind.Sequential)]
    struct Timespec { public long sec; public long nsec; }

    // Offsets por arquitetura em FuseLayout (medidos com tools/fuse-layout/probe.c). Buffers alocados com o
    // maior tamanho (x86_64); só o tamanho real da arquitetura é preenchido.
    static readonly FuseLayout L = FuseLayout.Current;
    const int StatSize = 144, StatVfsSize = 112;
    const int EntrySize = 176, EntryAttr = FuseLayout.EntryAttr;
    /// <summary>Mesmos prazos de cache do kernel que a API de alto nível usava por padrão.</summary>
    static double AttrTimeout = 1.0, EntryTimeout = 1.0;
    const ulong UnknownIno = 0xffffffff;

    const uint S_IFDIR = 0x4000, S_IFREG = 0x8000;
    // to_set do setattr
    const int SetMode = 1 << 0, SetUid = 1 << 1, SetGid = 1 << 2, SetSize = 1 << 3, SetMtime = 1 << 5, SetMtimeNow = 1 << 8;

    static uint _uid, _gid;
    /// <summary>Protege _session entre o Unmount e o fim do loop (que destrói a sessão).</summary>
    static readonly object MountLock = new();
    static VirtualFs _fs = null!;
    static IntPtr _session;
    static IntPtr _ops;
    static InodeTable _inodes = new();

    const ulong FUSE_CAP_DIRECT_IO_ALLOW_MMAP = 1UL << 28;

    /// <summary>
    /// Leitura por faixas com direct_io (true) ou pelo cache de página (false). direct_io mantém o errno real do read
    /// (ENOSPC, EHOSTUNREACH, EINTR; pelo cache de página o kernel converte tudo em EIO) e não deixa um open esperar
    /// a página que outro processo está baixando; mas só aceita mmap se a libfuse (≥ 3.17.3) e o kernel (≥ 6.6)
    /// negociarem DIRECT_IO_ALLOW_MMAP — sem isso o mmap daria ENODEV (git, SQLite, visualizadores), pior que o EIO.
    /// Decidido no init da sessão; <see cref="ForcePageCacheStreaming"/> força o modo antigo (testes).
    /// </summary>
    public static bool StreamingDirectIo { get; private set; }

    /// <summary>
    /// A libfuse carregada tem <c>fuse_set_feature_flag</c> (≥ 3.17.3), sem a qual o modo direct_io não é usado.
    /// O kernel ainda precisa aceitar (≥ 6.6); o resultado final é <see cref="StreamingDirectIo"/> depois de montar.
    /// </summary>
    public static bool LibfuseCanAllowDirectIoMmap
    {
        get
        {
            foreach (var name in new[] { "libfuse3.so.4", "libfuse3.so.3", "libfuse3.so" })
                if (NativeLibrary.TryLoad(name, out var h))
                    return NativeLibrary.TryGetExport(h, "fuse_set_feature_flag", out _);
            return false;
        }
    }

    /// <summary>Testes: simula libfuse/kernel sem DIRECT_IO_ALLOW_MMAP (Ubuntu 24.04, Debian 12).</summary>
    public static bool ForcePageCacheStreaming { get; set; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Init(IntPtr userdata, IntPtr conn)
    {
        var allowMmap = false;
        if (!ForcePageCacheStreaming)
        {
            try { allowMmap = fuse_set_feature_flag(conn, FUSE_CAP_DIRECT_IO_ALLOW_MMAP); }
            catch (EntryPointNotFoundException) { } // libfuse < 3.17.3
        }
        StreamingDirectIo = allowMmap;
    }
    static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, List<(string name, FileAttr attr)>> DirSnapshots = new();

    /// <summary>Requisição que a thread atual está tratando (para o VirtualFs perguntar se houve Ctrl-C).</summary>
    [ThreadStatic] static IntPtr _currentReq;
    /// <summary>Respostas assíncronas ainda pendentes: a sessão só pode ser destruída quando zerar.</summary>
    static int _inflight;

    static void FillStat(byte* st, in FileAttr a, ulong ino)
    {
        new Span<byte>(st, L.StatSize).Clear();
        var perm = a.Mode != 0 ? a.Mode & 0xFFF : a.IsDir ? 0x1EDu /*0755*/ : 0x1A4u /*0644*/;
        var mode = (a.IsDir ? S_IFDIR : S_IFREG) | perm;
        var mtime = a.MTimeMs; // atime, mtime e ctime (ver FileAttr: sem ctime próprio)
        *(ulong*)(st + FuseLayout.StIno) = ino;
        // st_nlink: 8 bytes no x86_64, 4 no aarch64 (gravar 4 bytes basta: o resto já foi zerado)
        *(uint*)(st + L.StNlink) = a.IsDir ? 2u : 1u;
        *(uint*)(st + L.StMode) = mode;
        *(uint*)(st + L.StUid) = _uid;
        *(uint*)(st + L.StGid) = _gid;
        if (L.BlksizeIs32Bit) *(int*)(st + FuseLayout.StBlksize) = 4096;
        else *(long*)(st + FuseLayout.StBlksize) = 4096;
        *(long*)(st + FuseLayout.StSize) = a.Size;
        *(long*)(st + FuseLayout.StBlocks) = a.Blocks;
        SetTime(st + FuseLayout.StAtim, mtime);
        SetTime(st + FuseLayout.StMtim, mtime);
        *(long*)(st + FuseLayout.StMtim + 8) += a.SubMtimeNs;
        SetTime(st + FuseLayout.StCtim, mtime);
    }

    static void SetTime(byte* p, long ms)
    {
        var sec = Math.DivRem(ms, 1000, out var rem);
        if (rem < 0) { sec--; rem += 1000; } // timespec usa floor: nanossegundos nunca negativos
        *(long*)p = sec;
        *(long*)(p + 8) = rem * 1_000_000;
    }

    // campos de struct stat que o setattr lê (mode/uid/gid mudam de lugar entre as arquiteturas)
    static uint StMode(byte* st) => *(uint*)(st + L.StMode);
    static uint StUid(byte* st) => *(uint*)(st + L.StUid);
    static uint StGid(byte* st) => *(uint*)(st + L.StGid);
    static long StSize(byte* st) => *(long*)(st + FuseLayout.StSize);
    static Timespec StMtime(byte* st) => *(Timespec*)(st + FuseLayout.StMtim);

    // fuse_file_info: flags(int32) @0, bitfields @4 (direct_io 0x2, keep_cache 0x4), fh(u64) @16
    static int FiFlags(byte* fi) => fi == null ? 0 : *(int*)fi;
    static ulong FiFh(byte* fi) => fi == null ? 0 : *(ulong*)(fi + FuseLayout.FiFh);

    /// <summary>
    /// Cópia do fuse_file_info para responder depois: o da libfuse está na pilha da thread do loop e deixa de
    /// valer quando o handler retorna.
    /// </summary>
    static byte* CopyFi(byte* fi)
    {
        // aloca o maior tamanho (3.17+) zerado e copia só o prefixo comum: a struct da 3.14 tem 40 bytes
        var copy = (byte*)NativeMemory.AllocZeroed(FuseLayout.FileInfoAlloc);
        if (fi != null) Buffer.MemoryCopy(fi, copy, FuseLayout.FileInfoAlloc, FuseLayout.FileInfoCopy);
        return copy;
    }

    static int CallerPid(IntPtr req)
    {
        var ctx = fuse_req_ctx(req);
        return ctx == IntPtr.Zero ? 0 : *(int*)((byte*)ctx + 8); // uid, gid, pid, umask
    }

    static readonly UTF8Encoding Utf8Estrito = new(false, true);
    static string S(byte* s)
    {
        if (s == null) return "";
        var len = 0;
        while (s[len] != 0) len++;
        return Utf8Estrito.GetString(new ReadOnlySpan<byte>(s, len));
    }

    public static Action<string>? Log { get; set; }

    /// <summary>
    /// Executa o handler e responde: <paramref name="body"/> devolve 0 quando já respondeu (fuse_reply_*), ou um
    /// errno negativo, que vira fuse_reply_err. Exceção → EIO. Com <paramref name="slow"/> roda em
    /// <see cref="SlowOps"/> e a thread do loop volta na hora para atender outras requisições.
    /// </summary>
    static void Handle(IntPtr req, Func<int> body, bool slow = false, Action? cleanup = null, bool network = false, [CallerMemberName] string op = "")
    {
        if (!slow)
        {
            Execute(req, body, cleanup, op);
            return;
        }
        Interlocked.Increment(ref _inflight);
        SlowOps.Run(() =>
        {
            try { Execute(req, body, cleanup, op); }
            finally { Interlocked.Decrement(ref _inflight); }
        }, network);
    }

    static void Execute(IntPtr req, Func<int> body, Action? cleanup, string op)
    {
        _currentReq = req;
        int r;
        try
        {
            using var local = _fs.MutacaoLocal();
            r = body();
        }
        catch (DecoderFallbackException) { r = -Errno.EILSEQ; }
        catch (Exception ex)
        {
            Log?.Invoke($"FUSE {op}: {ex}");
            r = -Errno.EIO;
        }
        finally
        {
            _currentReq = IntPtr.Zero;
        }
        try
        {
            if (r != 0) fuse_reply_err(req, -r);
        }
        finally { cleanup?.Invoke(); }
    }

    /// <summary>Caminho de um inode; null se o kernel citou um inode que não existe mais (apagado ainda aberto).</summary>
    static string? PathOf(ulong ino) => _inodes.PathOf(ino);

    static bool Nome(IntPtr req, byte* name, out string nome)
    {
        nome = "";
        try { nome = S(name); }
        catch (DecoderFallbackException) { fuse_reply_err(req, Errno.EILSEQ); return false; }
        if (Encoding.UTF8.GetByteCount(nome) <= 255) return true;
        fuse_reply_err(req, Errno.ENAMETOOLONG);
        return false;
    }

    static bool ChildPath(IntPtr req, ulong parent, byte* name, out string? path)
    {
        path = null;
        if (!Nome(req, name, out var nome)) return false;
        var dir = PathOf(parent);
        path = dir == null ? null : InodeTable.Combine(dir, nome);
        return true;
    }

    /// <summary>Preenche um fuse_entry_param para o caminho, contando a referência do kernel.</summary>
    static void FillEntry(byte* e, string path, in FileAttr a) => FillEntry(e, _inodes.Lookup(path, a.Lid == 0 ? null : a.Lid), a);

    static void FillEntry(byte* e, ulong ino, in FileAttr a)
    {
        new Span<byte>(e, EntrySize).Clear();
        *(ulong*)e = ino;
        FillStat(e + EntryAttr, a, ino);
        *(double*)(e + L.EntryAttrTimeout) = AttrTimeout;
        *(double*)(e + L.EntryEntryTimeout) = EntryTimeout;
    }

    static int ReplyEntry(IntPtr req, string path)
    {
        var r = _fs.GetAttr(path, out var a);
        if (r == -Errno.ENOENT)
        {
            var negativo = stackalloc byte[EntrySize];
            new Span<byte>(negativo, EntrySize).Clear(); // nodeid 0 + prazo: cache negativo do protocolo
            *(double*)(negativo + L.EntryEntryTimeout) = EntryTimeout;
            fuse_reply_entry(req, negativo);
            return 0;
        }
        if (r != 0) return r;
        var e = stackalloc byte[EntrySize];
        FillEntry(e, path, a);
        if (fuse_reply_entry(req, e) != 0) _inodes.Forget(*(ulong*)e, 1); // o kernel não recebeu: não vai dar forget
        return 0;
    }

    static int ReplyAttr(IntPtr req, ulong ino, string? path, ulong fh)
    {
        FileAttr a;
        var r = path == null && fh == 0
            ? (InodeTable.IsStable(ino) ? _fs.GetAttrByLid((long)ino - 1, out a) : AtributoAusente(out a))
            : fh != 0 ? _fs.GetAttr(path, fh, out a) : _fs.GetAttr(path!, out a);
        if (r != 0) return r;
        var st = stackalloc byte[StatSize];
        FillStat(st, a, ino);
        fuse_reply_attr(req, st, AttrTimeout);
        return 0;
    }

    static int AtributoAusente(out FileAttr a) { a = default; return -Errno.ENOENT; }

    /// <summary>Resultado 0 → responde sucesso; negativo → o Handle responde o erro.</summary>
    static int Ok(IntPtr req, int r)
    {
        if (r == 0) fuse_reply_err(req, 0);
        return r;
    }

    // ---------- operações ----------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Lookup(IntPtr req, ulong parent, byte* name)
    {
        if (!ChildPath(req, parent, name, out var p)) return;
        Handle(req, () => p == null ? -Errno.ENOENT : ReplyEntry(req, p));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Forget(IntPtr req, ulong ino, ulong nlookup)
    {
        _inodes.Forget(ino, (long)nlookup);
        fuse_reply_none(req);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void GetAttr(IntPtr req, ulong ino, byte* fi)
    {
        var fh = FiFh(fi);
        var p = PathOf(ino);
        Handle(req, () => ReplyAttr(req, ino, p, fh));
    }

    /// <summary>
    /// chmod/chown/truncate/utimens num handler só. Com tamanho vai para <see cref="SlowOps"/>: truncar um arquivo
    /// só-na-nuvem baixa o conteúdo antes.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void SetAttr(IntPtr req, ulong ino, byte* attr, int toSet, byte* fi)
    {
        var p = PathOf(ino);
        var fh = FiFh(fi);
        var mode = StMode(attr);
        var (uid, gid) = (StUid(attr), StGid(attr));
        var size = StSize(attr);
        var m = StMtime(attr);
        Handle(req, () =>
        {
            if (p == null && fh == 0) return -Errno.ENOENT;
            int r;
            if ((toSet & SetMode) != 0 && (r = _fs.Chmod(p, mode, fh)) != 0) return r;
            // Tudo pertence a quem montou (o OneDrive não tem dono/grupo POSIX): só aceita "trocar" para os mesmos
            // ids (cp -a / rsync -o fazem isso) e recusa o resto em vez de fingir que mudou.
            if ((toSet & SetUid) != 0 && uid != _uid) return -Errno.EPERM;
            if ((toSet & SetGid) != 0 && gid != _gid) return -Errno.EPERM;
            if ((toSet & SetSize) != 0 && (r = _fs.Truncate(p, size, fh == 0 ? null : fh)) != 0) return r;
            if ((toSet & (SetMtime | SetMtimeNow)) != 0)
            {
                long? mtime = (toSet & SetMtimeNow) != 0 ? null : m.sec * 1000 + m.nsec / 1_000_000;
                if ((r = _fs.Utimens(p, mtime, fh, (toSet & SetMtimeNow) != 0 ? 0 : (int)(m.nsec % 1_000_000))) != 0) return r;
            }
            return ReplyAttr(req, ino, p, fh);
        }, slow: (toSet & SetSize) != 0, network: size > 0 && _fs.OpenNeedsNetwork(p, 0));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Mkdir(IntPtr req, ulong parent, byte* name, uint mode)
    {
        if (!ChildPath(req, parent, name, out var p)) return;
        Handle(req, () =>
        {
            if (p == null) return -Errno.ENOENT;
            var r = _fs.Mkdir(p, mode);
            return r != 0 ? r : ReplyEntry(req, p);
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Unlink(IntPtr req, ulong parent, byte* name)
    {
        if (!ChildPath(req, parent, name, out var p)) return;
        Handle(req, () =>
        {
            if (p == null) return -Errno.ENOENT;
            var r = _fs.Unlink(p);
            if (r == 0) _inodes.Detach(p); // fds abertos seguem pelo fh; o inode fica sem caminho até o forget
            return Ok(req, r);
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Rmdir(IntPtr req, ulong parent, byte* name)
    {
        if (!ChildPath(req, parent, name, out var p)) return;
        Handle(req, () =>
        {
            if (p == null) return -Errno.ENOENT;
            var r = _fs.Rmdir(p);
            if (r == 0) _inodes.Detach(p);
            return Ok(req, r);
        });
    }

    /// <summary>Em <see cref="SlowOps"/>: renomear para nome local-only baixa o conteúdo antes, e mv vai ao servidor.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Rename(IntPtr req, ulong parent, byte* name, ulong newParent, byte* newName, uint flags)
    {
        if (!ChildPath(req, parent, name, out var from) || !ChildPath(req, newParent, newName, out var to)) return;
        Handle(req, () =>
        {
            if (from == null || to == null) return -Errno.ENOENT;
            var r = _fs.Rename(from, to, flags);
            const uint RENAME_EXCHANGE = 2;
            if (r == 0) _inodes.Move(from, to, exchange: (flags & RENAME_EXCHANGE) != 0);
            return Ok(req, r);
        }, slow: true, network: _fs.RenameNeedsNetwork(from, to));
    }

    /// <summary>
    /// Em <see cref="SlowOps"/>: abrir arquivo só-na-nuvem espera o download (cancelável com Ctrl-C), exceto leitura
    /// por faixas de arquivo grande, que responde já, sem esperar a rede (docs/streaming.md). Cache de página como o
    /// auto_cache da API de alto nível: mantém entre opens se mtime e tamanho não mudaram.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Open(IntPtr req, ulong ino, byte* fi)
    {
        var p = PathOf(ino);
        var flags = FiFlags(fi);
        var pid = CallerPid(req);
        var copy = CopyFi(fi);
        Handle(req, () =>
        {
            if (p == null) return -Errno.ENOENT;
            var r = _fs.Open(p, flags, pid, out var fh);
            if (r != 0) return r;
            *(ulong*)(copy + FuseLayout.FiFh) = fh;
            if (_fs.IsStreaming(fh))
            {
                // Leitura por faixas: direct_io quando o mmap continua possível (ver StreamingDirectIo); senão cache
                // de página. Nos dois casos o próximo open não recebe keep_cache (registro invalidado abaixo), então
                // páginas de blocos ainda não verificados não passam para outro open. Ver docs/streaming.md.
                if (StreamingDirectIo) *(uint*)(copy + FuseLayout.FiBits) |= FuseLayout.FiDirectIo;
                _inodes.SameContentAsLastOpen(ino, long.MinValue, -1);
            }
            else if (_fs.GetAttr(p, fh, out var a) == 0 && _inodes.SameContentAsLastOpen(ino, a.MTimeMs, a.Size))
                *(uint*)(copy + FuseLayout.FiBits) |= FuseLayout.FiKeepCache;
            if (fuse_reply_open(req, copy) != 0) _fs.Release(fh); // processo morreu antes da resposta
            return 0;
        }, slow: true, cleanup: () => NativeMemory.Free(copy), network: _fs.OpenNeedsNetwork(p, flags));
    }

    /// <summary>
    /// Local na thread do loop; bloco da leitura por faixas que ainda não chegou vai para <see cref="SlowOps"/>
    /// (espera a rede sem ocupar o loop).
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Read(IntPtr req, ulong ino, nuint size, long off, byte* fi)
    {
        var fh = FiFh(fi);
        var slow = _fs.ReadOutsideLoop(fh);
        Handle(req, () =>
        {
            var buf = (byte*)NativeMemory.Alloc(size == 0 ? 1 : size);
            try
            {
                var n = _fs.Read(fh, new Span<byte>(buf, (int)size), off);
                if (n < 0) return n;
                fuse_reply_buf(req, buf, (nuint)n);
                return 0;
            }
            finally { NativeMemory.Free(buf); }
        }, slow, network: slow);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Write(IntPtr req, ulong ino, byte* buf, nuint size, long off, byte* fi)
    {
        var fh = FiFh(fi);
        Handle(req, () =>
        {
            var n = _fs.Write(fh, new ReadOnlySpan<byte>(buf, (int)size), off);
            if (n < 0) return n;
            fuse_reply_write(req, (nuint)n);
            return 0;
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Flush(IntPtr req, ulong ino, byte* fi)
    {
        var fh = FiFh(fi);
        Handle(req, () => Ok(req, _fs.Flush(fh)));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Release(IntPtr req, ulong ino, byte* fi)
    {
        var fh = FiFh(fi);
        Handle(req, () => Ok(req, _fs.Release(fh)));
    }

    /// <summary>Em <see cref="SlowOps"/>: grava em disco (fsync do cache).</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Fsync(IntPtr req, ulong ino, int datasync, byte* fi)
    {
        var fh = FiFh(fi);
        Handle(req, () => Ok(req, _fs.Fsync(fh)), slow: true);
    }

    /// <summary>
    /// copy_file_range: copia entre dois arquivos abertos pelo cache. Em <see cref="SlowOps"/> (a origem pode ainda
    /// depender da rede, e copiar arquivos grandes não deve ocupar o loop).
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void CopyFileRange(IntPtr req, ulong inoIn, long offIn, byte* fiIn, ulong inoOut, long offOut, byte* fiOut, nuint len, int flags)
    {
        var (fhIn, fhOut) = (FiFh(fiIn), FiFh(fiOut));
        Handle(req, () =>
        {
            if (flags != 0) return -Errno.EINVAL;
            var n = _fs.CopyRange(fhIn, offIn, fhOut, offOut, (long)Math.Min((ulong)len, (ulong)long.MaxValue));
            if (n < 0) return (int)n;
            fuse_reply_write(req, (nuint)n);
            return 0;
        }, slow: true, network: _fs.IsStreaming(fhIn));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OpenDir(IntPtr req, ulong ino, byte* fi)
    {
        var p = PathOf(ino);
        Handle(req, () =>
        {
            if (p == null) return -Errno.ENOENT;
            var r = _fs.OpenDir(p, out var fh);
            if (r != 0) return r;
            *(ulong*)(fi + FuseLayout.FiFh) = fh; // síncrono: o fi da libfuse ainda vale
            if (fuse_reply_open(req, fi) != 0) _fs.ReleaseDir(fh);
            return 0;
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void ReadDir(IntPtr req, ulong ino, nuint size, long off, byte* fi)
    {
        var fh = FiFh(fi);
        Handle(req, () => ReplyDir(req, ino, fh, size, off, plus: false));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void ReadDirPlus(IntPtr req, ulong ino, nuint size, long off, byte* fi)
    {
        var fh = FiFh(fi);
        Handle(req, () => ReplyDir(req, ino, fh, size, off, plus: true));
    }

    /// <summary>
    /// Lista um pedaço do diretório. A lista é tirada uma vez por handle (no offset 0) e paginada pelo índice
    /// ("." e ".." primeiro; o offset de cada entrada é o índice seguinte). No readdirplus cada entrada, menos
    /// "." e "..", conta um lookup — só as que couberam no buffer.
    /// </summary>
    static ulong InodeNaoLigado() => 0;

    static int ReplyDir(IntPtr req, ulong ino, ulong fh, nuint size, long off, bool plus)
    {
        if (off == 0 || !DirSnapshots.ContainsKey(fh))
        {
            var list = new List<(string name, FileAttr attr)>();
            var r = _fs.ReadDir(fh, list);
            if (r != 0) return r;
            DirSnapshots[fh] = list;
        }
        var entries = DirSnapshots[fh];
        var dir = PathOf(ino);
        var buf = (byte*)NativeMemory.Alloc(size == 0 ? 1 : size);
        try
        {
            nuint used = 0;
            var st = stackalloc byte[StatSize];
            var e = stackalloc byte[EntrySize];
            var self = new FileAttr(true, 4096, 0, 0);
            for (var i = (int)off; i < entries.Count + 2; i++)
            {
                var (name, attr) = i switch { 0 => (".", self), 1 => ("..", self), _ => entries[i - 2] };
                var path = i >= 2 && dir != null ? InodeTable.Combine(dir, name) : null;
                var bytes = Encoding.UTF8.GetBytes(name + "\0");
                nuint need;
                fixed (byte* n = bytes)
                {
                    if (plus && path != null)
                    {
                        // conta a referência antes de pôr no buffer; se não couber, devolve (ver InodeTable.ReaddirEntry)
                        var room = size - used;
                        var (at, ep, np, next) = ((IntPtr)(buf + used), (IntPtr)e, (IntPtr)n, i + 1);
                        nuint got = 0;
                        var a = attr;
                        _inodes.ReaddirEntry(path, attr.Lid == 0 ? null : attr.Lid, entryIno =>
                        {
                            FillEntry((byte*)ep, entryIno, a);
                            got = fuse_add_direntry_plus(req, (byte*)at, room, (byte*)np, (byte*)ep, next);
                            return got <= room;
                        });
                        need = got;
                    }
                    else if (plus)
                    {
                        new Span<byte>(e, EntrySize).Clear();
                        *(ulong*)e = InodeNaoLigado(); // nodeid 0, não FUSE_UNKNOWN_INO
                        FillStat(e + EntryAttr, attr, *(ulong*)e);
                        need = fuse_add_direntry_plus(req, buf + used, size - used, n, e, i + 1);
                    }
                    else
                    {
                        FillStat(st, attr, path != null ? _inodes.Known(path) : i == 0 ? ino : UnknownIno);
                        need = fuse_add_direntry(req, buf + used, size - used, n, st, i + 1);
                    }
                }
                if (need > size - used) break; // não coube: fica para o próximo readdir
                used += need;
            }
            fuse_reply_buf(req, buf, used);
            return 0;
        }
        finally { NativeMemory.Free(buf); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void ReleaseDir(IntPtr req, ulong ino, byte* fi)
    {
        var fh = FiFh(fi);
        DirSnapshots.TryRemove(fh, out _);
        Handle(req, () => Ok(req, _fs.ReleaseDir(fh)));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void StatFs(IntPtr req, ulong ino)
    {
        var p = PathOf(ino) ?? "/";
        Handle(req, () =>
        {
            var (total, free) = _fs.StatFs(p);
            const ulong bsize = 4096;
            var st = stackalloc byte[StatVfsSize];
            new Span<byte>(st, StatVfsSize).Clear();
            // struct statvfs: bsize, frsize, blocks, bfree, bavail, files, ffree, favail, fsid, flag, namemax
            var u = (ulong*)st;
            u[0] = bsize;
            u[1] = bsize;
            u[2] = (ulong)total / bsize;
            u[3] = (ulong)free / bsize;
            u[4] = (ulong)free / bsize;
            u[5] = 1_000_000;
            u[6] = 1_000_000;
            u[7] = 1_000_000;
            u[10] = 255;
            fuse_reply_statfs(req, st);
            return 0;
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void SetXattr(IntPtr req, ulong ino, byte* name, byte* value, nuint size, int flags)
    {
        var p = PathOf(ino);
        if (!Nome(req, name, out var n)) return;
        var v = new ReadOnlySpan<byte>(value, (int)size).ToArray();
        Handle(req, () => Ok(req, p == null ? -Errno.ENOENT : _fs.SetXattr(p, n, v, flags)));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void RemoveXattr(IntPtr req, ulong ino, byte* name)
    {
        var p = PathOf(ino);
        if (!Nome(req, name, out var n)) return;
        Handle(req, () => Ok(req, p == null ? -Errno.ENOENT : _fs.RemoveXattr(p, n)));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void GetXattr(IntPtr req, ulong ino, byte* name, nuint size)
    {
        var p = PathOf(ino);
        if (!Nome(req, name, out var n)) return;
        Handle(req, () =>
        {
            var v = p == null ? null : _fs.GetXattr(p, n);
            return v == null ? -Errno.ENODATA : ReplyXattr(req, Encoding.UTF8.GetBytes(v), size);
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void ListXattr(IntPtr req, ulong ino, nuint size)
    {
        var p = PathOf(ino);
        Handle(req, () =>
        {
            if (p == null) return ReplyXattr(req, [], size);
            var node = _fs.Resolve(p);
            if (node == null) return -Errno.ENOENT;
            // raiz virtual: sem atributos
            var names = node.Value.Item == null ? [] : Encoding.UTF8.GetBytes(string.Join('\0', VirtualFs.XattrNames) + '\0');
            return ReplyXattr(req, names, size);
        });
    }

    /// <summary>size 0 pergunta o tamanho; buffer pequeno → ERANGE.</summary>
    static int ReplyXattr(IntPtr req, byte[] bytes, nuint size)
    {
        if (size == 0)
        {
            fuse_reply_xattr(req, (nuint)bytes.Length);
            return 0;
        }
        if ((int)size < bytes.Length) return -34; // ERANGE
        fixed (byte* b = bytes) fuse_reply_buf(req, b, (nuint)bytes.Length);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Access(IntPtr req, ulong ino, int mask)
    {
        var p = PathOf(ino);
        Handle(req, () => Ok(req, p == null || _fs.Resolve(p) != null ? 0 : -Errno.ENOENT));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Create(IntPtr req, ulong parent, byte* name, uint mode, byte* fi)
    {
        if (!ChildPath(req, parent, name, out var p)) return;
        var flags = FiFlags(fi);
        var copy = CopyFi(fi);
        Handle(req, () =>
        {
            if (p == null) return -Errno.ENOENT;
            var r = _fs.Create(p, flags, mode, out var fh);
            if (r != 0) return r;
            *(ulong*)(copy + FuseLayout.FiFh) = fh; // a libfuse já pode ter liberado o fi original
            if ((r = _fs.GetAttr(p, fh, out var a)) != 0)
            {
                _fs.Release(fh);
                return r;
            }
            var e = stackalloc byte[EntrySize];
            FillEntry(e, p, a);
            _inodes.SameContentAsLastOpen(*(ulong*)e, a.MTimeMs, a.Size);
            if (fuse_reply_create(req, e, copy) != 0)
            {
                _inodes.Forget(*(ulong*)e, 1);
                _fs.Release(fh);
            }
            return 0;
        }, slow: true, cleanup: () => NativeMemory.Free(copy), network: _fs.OpenNeedsNetwork(p, flags));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Mknod(IntPtr req, ulong parent, byte* name, uint mode, ulong rdev) => fuse_reply_err(req, Errno.EPERM);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Symlink(IntPtr req, byte* link, ulong parent, byte* name) => fuse_reply_err(req, Errno.EPERM);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Link(IntPtr req, ulong ino, ulong parent, byte* name) => fuse_reply_err(req, Errno.EPERM);

    // ---------- montagem ----------

    /// <summary>Índice do campo em fuse_lowlevel_ops (FuseLayoutTests confere com os cabeçalhos).</summary>
    public static int OpIndexOf(string op) => (int)Enum.Parse<OpIndex>(op);

    // ordem dos campos de struct fuse_lowlevel_ops (fuse3; conferida com offsetof na 3.14 e 3.18, x86_64 e aarch64)
    enum OpIndex
    {
        init, destroy, lookup, forget, getattr, setattr, readlink, mknod, mkdir, unlink, rmdir, symlink, rename, link,
        open, read, write, flush, release, fsync, opendir, readdir, releasedir, fsyncdir, statfs, setxattr, getxattr,
        listxattr, removexattr, access, create, getlk, setlk, bmap, ioctl, poll, write_buf, retrieve_reply,
        forget_multi, flock, fallocate, readdirplus, copy_file_range, lseek,
        /// <summary>Tamanho informado à libfuse: até lseek (3.0–3.17); campos mais novos (tmpfile, statx) ficam de fora.</summary>
        Count,
    }

    static IntPtr BuildOps()
    {
        var n = (int)OpIndex.Count;
        var ops = (IntPtr*)NativeMemory.AllocZeroed((nuint)(n * sizeof(IntPtr)));
        ops[(int)OpIndex.init] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&Init;
        ops[(int)OpIndex.lookup] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, void>)&Lookup;
        ops[(int)OpIndex.forget] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, ulong, void>)&Forget;
        ops[(int)OpIndex.getattr] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, void>)&GetAttr;
        ops[(int)OpIndex.setattr] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, int, byte*, void>)&SetAttr;
        ops[(int)OpIndex.mknod] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, uint, ulong, void>)&Mknod;
        ops[(int)OpIndex.symlink] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, ulong, byte*, void>)&Symlink;
        ops[(int)OpIndex.link] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, ulong, byte*, void>)&Link;
        ops[(int)OpIndex.mkdir] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, uint, void>)&Mkdir;
        ops[(int)OpIndex.unlink] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, void>)&Unlink;
        ops[(int)OpIndex.rmdir] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, void>)&Rmdir;
        ops[(int)OpIndex.rename] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, ulong, byte*, uint, void>)&Rename;
        ops[(int)OpIndex.open] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, void>)&Open;
        ops[(int)OpIndex.read] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, nuint, long, byte*, void>)&Read;
        ops[(int)OpIndex.write] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, nuint, long, byte*, void>)&Write;
        ops[(int)OpIndex.flush] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, void>)&Flush;
        ops[(int)OpIndex.release] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, void>)&Release;
        ops[(int)OpIndex.fsync] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, int, byte*, void>)&Fsync;
        ops[(int)OpIndex.opendir] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, void>)&OpenDir;
        ops[(int)OpIndex.readdir] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, nuint, long, byte*, void>)&ReadDir;
        ops[(int)OpIndex.readdirplus] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, nuint, long, byte*, void>)&ReadDirPlus;
        ops[(int)OpIndex.releasedir] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, void>)&ReleaseDir;
        ops[(int)OpIndex.statfs] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, void>)&StatFs;
        ops[(int)OpIndex.setxattr] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, byte*, nuint, int, void>)&SetXattr;
        ops[(int)OpIndex.getxattr] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, nuint, void>)&GetXattr;
        ops[(int)OpIndex.listxattr] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, nuint, void>)&ListXattr;
        ops[(int)OpIndex.removexattr] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, void>)&RemoveXattr;
        ops[(int)OpIndex.access] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, int, void>)&Access;
        ops[(int)OpIndex.create] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, uint, byte*, void>)&Create;
        ops[(int)OpIndex.copy_file_range] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, long, byte*, ulong, long, byte*, nuint, int, void>)&CopyFileRange;
        return (IntPtr)ops;
    }

    public static bool IsMounted => _session != IntPtr.Zero;
    public static string? MountPoint { get; private set; }

    /// <summary>Desmonta resto de execução anterior ("Transport endpoint is not connected").</summary>
    public static void CleanupStaleMount(string mountPoint)
    {
        try
        {
            var mounts = File.ReadAllLines("/proc/self/mounts");
            var full = Path.GetFullPath(mountPoint).TrimEnd('/');
            if (mounts.Any(l => l.Split(' ') is { Length: > 2 } p && p[1].Replace("\\040", " ") == full))
                RunFusermount("-uz", full);
        }
        catch { }
    }

    static void RunFusermount(string flag, string path)
    {
        foreach (var bin in new[] { "fusermount3", "fusermount" })
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(bin) { RedirectStandardError = true, RedirectStandardOutput = true };
                psi.ArgumentList.Add(flag);
                psi.ArgumentList.Add(path);
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(10_000);
                return;
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    /// <summary>
    /// Monta e executa o loop do FUSE numa thread dedicada. Retorna a Task que termina ao desmontar.
    /// <paramref name="maxThreads"/> limita as threads do loop; downloads não as ocupam (ver <see cref="SlowOps"/>).
    /// </summary>
    public static Task MountAsync(VirtualFs fs, string mountPoint, int maxThreads = 16, double cacheTimeout = 1.0)
    {
        if (_session != IntPtr.Zero) throw new InvalidOperationException("Já montado");
        AttrTimeout = EntryTimeout = cacheTimeout;
        _fs = fs;
        _inodes = new InodeTable(path => fs.Resolve(path)?.Item?.Lid);
        DirSnapshots.Clear();
        fs.InterruptRequested = () => _currentReq != IntPtr.Zero && fuse_req_interrupted(_currentReq) != 0;
        _uid = getuid();
        _gid = getgid();
        mountPoint = Path.GetFullPath(mountPoint);
        CleanupStaleMount(mountPoint);
        Directory.CreateDirectory(mountPoint);
        if (Directory.EnumerateFileSystemEntries(mountPoint).Any())
            throw new IOException($"O ponto de montagem {mountPoint} não está vazio.");

        if (_ops == IntPtr.Zero) _ops = BuildOps();

        var argv = new[] { "craftdriver", "-o", "fsname=craftdriver,subtype=onedrive,default_permissions,auto_unmount" };
        var ptrs = argv.Select(a => Marshal.StringToCoTaskMemUTF8(a)).ToArray();
        var argvPtr = (IntPtr*)NativeMemory.Alloc((nuint)((ptrs.Length + 1) * sizeof(IntPtr)));
        for (var i = 0; i < ptrs.Length; i++) argvPtr[i] = ptrs[i];
        argvPtr[ptrs.Length] = IntPtr.Zero;
        var args = new FuseArgs { argc = ptrs.Length, argv = (IntPtr)argvPtr, allocated = 0 };

        void FreeArgs()
        {
            foreach (var p in ptrs) Marshal.FreeCoTaskMem(p);
            NativeMemory.Free(argvPtr);
        }

        var se = fuse_session_new(&args, _ops, (nuint)((int)OpIndex.Count * sizeof(IntPtr)), IntPtr.Zero);
        if (se == IntPtr.Zero)
        {
            FreeArgs();
            throw new IOException("fuse_session_new falhou (opções inválidas?)");
        }
        if (fuse_session_mount(se, mountPoint) != 0)
        {
            fuse_session_destroy(se);
            FreeArgs();
            throw new IOException($"Falha ao montar em {mountPoint}. /dev/fuse acessível e fusermount3 instalado?");
        }
        lock (MountLock)
        {
            _session = se;
            MountPoint = mountPoint;
        }

        var notificacoes = new KernelNotifications(fs, _inodes, se);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var cfg = fuse_loop_cfg_create();
            fuse_loop_cfg_set_max_threads(cfg, (uint)maxThreads);
            try
            {
                var rc = fuse_session_loop_mt(se, cfg);
                Log?.Invoke($"Loop FUSE terminou (rc={rc})");
            }
            finally
            {
                fuse_loop_cfg_destroy(cfg);
                // Respostas assíncronas ainda pendentes escrevem no fd da sessão: destruir antes delas seria
                // escrever num fd fechado (ou reaproveitado por outro arquivo).
                var deadline = Environment.TickCount64 + 15_000;
                while (Volatile.Read(ref _inflight) > 0 && Environment.TickCount64 < deadline) Thread.Sleep(20);
                var pending = Volatile.Read(ref _inflight);
                var notificacoesEncerradas = notificacoes.Stop();
                lock (MountLock)
                {
                    // zera antes de destruir: Unmount não pode chamar fuse_session_exit numa sessão liberada
                    _session = IntPtr.Zero;
                    MountPoint = null;
                    if (pending > 0 || !notificacoesEncerradas)
                        Log?.Invoke($"FUSE: {pending} respostas pendentes ao desmontar; sessão não destruída");
                    else
                    {
                        fuse_session_unmount(se);
                        fuse_session_destroy(se);
                    }
                }
                FreeArgs();
                tcs.TrySetResult();
            }
        })
        { IsBackground = true, Name = "fuse-loop" };
        thread.Start();
        return tcs.Task;
    }

    /// <summary>Desmonta (faz o loop terminar).</summary>
    public static void Unmount()
    {
        string? mp;
        lock (MountLock)
        {
            mp = MountPoint;
            if (_session == IntPtr.Zero || mp == null) return;
            fuse_session_exit(_session); // só marca o loop para sair; a destruição acontece na thread do loop
        }
        RunFusermount("-u", mp);
    }
}
