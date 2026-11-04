using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CraftDriver.Core.Vfs;

namespace CraftDriver.Fuse;

/// <summary>
/// Binding mínimo da API de alto nível da libfuse3 (fuse_new / fuse_mount / fuse_loop_mt).
/// Layout de structs para Linux x86_64 e aarch64 (glibc).
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

    [DllImport(Lib, EntryPoint = "fuse_new_31")]
    static extern IntPtr fuse_new(FuseArgs* args, IntPtr ops, nuint opSize, IntPtr userData);

    [DllImport(Lib)] static extern int fuse_mount(IntPtr f, [MarshalAs(UnmanagedType.LPUTF8Str)] string mountpoint);
    [DllImport(Lib)] static extern void fuse_unmount(IntPtr f);
    [DllImport(Lib)] static extern void fuse_destroy(IntPtr f);
    [DllImport(Lib)] static extern void fuse_exit(IntPtr f);
    [DllImport(Lib, EntryPoint = "fuse_loop_mt_312")] static extern int fuse_loop_mt(IntPtr f, IntPtr config);
    [DllImport(Lib)] static extern IntPtr fuse_loop_cfg_create();
    [DllImport(Lib)] static extern void fuse_loop_cfg_destroy(IntPtr cfg);
    [DllImport(Lib)] static extern void fuse_loop_cfg_set_max_threads(IntPtr cfg, uint n);
    [DllImport(Lib)] static extern IntPtr fuse_get_context();

    [DllImport("libc", SetLastError = true)] static extern uint getuid();
    [DllImport("libc", SetLastError = true)] static extern uint getgid();

    // ---------- structs ----------

    [StructLayout(LayoutKind.Sequential)]
    struct Timespec { public long sec; public long nsec; }

    /// <summary>struct stat (x86_64 / aarch64 têm layouts diferentes; ver FillStat).</summary>
    const int StatSize = 144;

    const uint S_IFDIR = 0x4000, S_IFREG = 0x8000;

    static uint _uid, _gid;
    static VirtualFs _fs = null!;
    static IntPtr _fuse;
    static IntPtr _ops;
    static readonly bool Arm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    static void FillStat(byte* st, in FileAttr a)
    {
        new Span<byte>(st, StatSize).Clear();
        var mode = a.IsDir ? S_IFDIR | 0x1ED /*0755*/ : S_IFREG | 0x1A4 /*0644*/;
        var mtime = a.MTimeMs;
        var ctime = a.CTimeMs == 0 ? mtime : a.CTimeMs;
        if (!Arm64)
        {
            // x86_64: dev(8) ino(8) nlink(8) mode(4) uid(4) gid(4) pad(4) rdev(8) size(8) blksize(8) blocks(8) atim mtim ctim
            *(ulong*)(st + 16) = a.IsDir ? 2UL : 1UL;
            *(uint*)(st + 24) = mode;
            *(uint*)(st + 28) = _uid;
            *(uint*)(st + 32) = _gid;
            *(long*)(st + 48) = a.Size;
            *(long*)(st + 56) = 4096;
            *(long*)(st + 64) = a.Blocks;
            SetTime(st + 72, mtime);
            SetTime(st + 88, mtime);
            SetTime(st + 104, mtime);
        }
        else
        {
            // aarch64 (asm-generic): dev(8) ino(8) mode(4) nlink(4) uid(4) gid(4) rdev(8) pad(8) size(8) blksize(4) pad(4) blocks(8) atim mtim ctim
            *(uint*)(st + 16) = mode;
            *(uint*)(st + 20) = a.IsDir ? 2u : 1u;
            *(uint*)(st + 24) = _uid;
            *(uint*)(st + 28) = _gid;
            *(long*)(st + 48) = a.Size;
            *(int*)(st + 56) = 4096;
            *(long*)(st + 64) = a.Blocks;
            SetTime(st + 72, mtime);
            SetTime(st + 88, mtime);
            SetTime(st + 104, mtime);
        }
        _ = ctime;
    }

    static void SetTime(byte* p, long ms)
    {
        *(long*)p = Math.DivRem(ms, 1000, out var rem);
        *(long*)(p + 8) = rem * 1_000_000;
    }

    // fuse_file_info: flags(int32) @0, bitfields @4, pad @8, pad @12, fh(u64) @16
    static int FiFlags(IntPtr fi) => fi == IntPtr.Zero ? 0 : *(int*)fi;
    static ulong FiFh(IntPtr fi) => fi == IntPtr.Zero ? 0 : *(ulong*)((byte*)fi + 16);
    static void SetFiFh(IntPtr fi, ulong fh) { if (fi != IntPtr.Zero) *(ulong*)((byte*)fi + 16) = fh; }

    static int CallerPid()
    {
        var ctx = fuse_get_context();
        return ctx == IntPtr.Zero ? 0 : *(int*)((byte*)ctx + 16); // fuse*, uid, gid, pid
    }

    static string P(byte* s) => s == null ? "" : Marshal.PtrToStringUTF8((IntPtr)s) ?? "";

    static int Guard(Func<int> f, [CallerMemberName] string op = "")
    {
        try { return f(); }
        catch (Exception ex)
        {
            Log?.Invoke($"FUSE {op}: {ex}");
            return -Errno.EIO;
        }
    }

    public static Action<string>? Log { get; set; }

    // ---------- operações ----------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int GetAttr(byte* path, byte* st, IntPtr fi)
    {
        var p = P(path);
        return Guard(() =>
        {
            var r = _fs.GetAttr(p, out var a);
            if (r == 0) FillStat(st, a);
            return r;
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int Mkdir(byte* path, uint mode) { var p = P(path); return Guard(() => _fs.Mkdir(p)); }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int Unlink(byte* path) { var p = P(path); return Guard(() => _fs.Unlink(p)); }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int Rmdir(byte* path) { var p = P(path); return Guard(() => _fs.Rmdir(p)); }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int Rename(byte* from, byte* to, uint flags)
    {
        var f = P(from);
        var t = P(to);
        return Guard(() => _fs.Rename(f, t, flags));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int Chmod(byte* path, uint mode, IntPtr fi) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int Chown(byte* path, uint uid, uint gid, IntPtr fi) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int Truncate(byte* path, long size, IntPtr fi)
    {
        var p = P(path);
        var fh = FiFh(fi);
        return Guard(() => _fs.Truncate(p, size, fh == 0 ? null : fh));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int Open(byte* path, IntPtr fi)
    {
        var p = P(path);
        var flags = FiFlags(fi);
        var pid = CallerPid();
        return Guard(() =>
        {
            var r = _fs.Open(p, flags, pid, out var fh);
            if (r == 0) SetFiFh(fi, fh);
            return r;
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int Read(byte* path, byte* buf, nuint size, long off, IntPtr fi)
    {
        var fh = FiFh(fi);
        try { return _fs.Read(fh, new Span<byte>(buf, (int)size), off); }
        catch (Exception ex) { Log?.Invoke("FUSE read: " + ex.Message); return -Errno.EIO; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int Write(byte* path, byte* buf, nuint size, long off, IntPtr fi)
    {
        var fh = FiFh(fi);
        try { return _fs.Write(fh, new ReadOnlySpan<byte>(buf, (int)size), off); }
        catch (Exception ex) { Log?.Invoke("FUSE write: " + ex.Message); return -Errno.EIO; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int StatFs(byte* path, byte* st)
    {
        var p = P(path);
        return Guard(() =>
        {
            var (total, free) = _fs.StatFs(p);
            const ulong bsize = 4096;
            new Span<byte>(st, 112).Clear();
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
            return 0;
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int Flush(byte* path, IntPtr fi) { var fh = FiFh(fi); return Guard(() => _fs.Flush(fh)); }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int Release(byte* path, IntPtr fi) { var fh = FiFh(fi); return Guard(() => _fs.Release(fh)); }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int Fsync(byte* path, int datasync, IntPtr fi) { var fh = FiFh(fi); return Guard(() => _fs.Fsync(fh)); }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int SetXattr(byte* path, byte* name, byte* value, nuint size, int flags) => -Errno.ENOTSUP;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int GetXattr(byte* path, byte* name, byte* value, nuint size)
    {
        var p = P(path);
        var n = P(name);
        return Guard(() =>
        {
            var v = _fs.GetXattr(p, n);
            if (v == null) return -Errno.ENODATA;
            var bytes = Encoding.UTF8.GetBytes(v);
            if (size == 0) return bytes.Length;
            if ((int)size < bytes.Length) return -34; // ERANGE
            bytes.CopyTo(new Span<byte>(value, bytes.Length));
            return bytes.Length;
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int ListXattr(byte* path, byte* list, nuint size)
    {
        var p = P(path);
        return Guard(() =>
        {
            if (_fs.Resolve(p)?.Item == null) return 0;
            var bytes = Encoding.UTF8.GetBytes(string.Join('\0', VirtualFs.XattrNames) + '\0');
            if (size == 0) return bytes.Length;
            if ((int)size < bytes.Length) return -34;
            bytes.CopyTo(new Span<byte>(list, bytes.Length));
            return bytes.Length;
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int RemoveXattr(byte* path, byte* name) => -Errno.ENOTSUP;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OpenDir(byte* path, IntPtr fi)
    {
        var p = P(path);
        return Guard(() => _fs.Resolve(p) is { } n ? (n.IsDir ? 0 : -Errno.ENOTDIR) : -Errno.ENOENT);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int ReadDir(byte* path, IntPtr buf, IntPtr filler, long off, IntPtr fi, int flags)
    {
        var p = P(path);
        try
        {
            var entries = new List<(string name, FileAttr attr)>();
            var r = _fs.ReadDir(p, entries);
            if (r != 0) return r;
            var fill = (delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, long, int, int>)filler;
            var st = stackalloc byte[StatSize];
            var dot = stackalloc byte[3];
            dot[0] = (byte)'.'; dot[1] = 0;
            fill(buf, dot, null, 0, 0);
            dot[1] = (byte)'.'; dot[2] = 0;
            fill(buf, dot, null, 0, 0);
            var plus = (flags & 1) != 0; // FUSE_READDIR_PLUS
            foreach (var (name, attr) in entries)
            {
                var bytes = Encoding.UTF8.GetBytes(name + "\0");
                fixed (byte* n = bytes)
                {
                    FillStat(st, attr);
                    if (fill(buf, n, st, 0, plus ? 2 /*FUSE_FILL_DIR_PLUS*/ : 0) != 0) break;
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Log?.Invoke("FUSE readdir: " + ex);
            return -Errno.EIO;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int ReleaseDir(byte* path, IntPtr fi) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int Access(byte* path, int mask)
    {
        var p = P(path);
        return Guard(() => _fs.Resolve(p) != null ? 0 : -Errno.ENOENT);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int Create(byte* path, uint mode, IntPtr fi)
    {
        var p = P(path);
        var flags = FiFlags(fi);
        return Guard(() =>
        {
            var r = _fs.Create(p, flags, out var fh);
            if (r == 0) SetFiFh(fi, fh);
            return r;
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int Utimens(byte* path, Timespec* tv, IntPtr fi)
    {
        var p = P(path);
        long? mtime = null;
        if (tv != null)
        {
            const long UTIME_NOW = (1L << 30) - 1, UTIME_OMIT = (1L << 30) - 2;
            var m = tv[1];
            if (m.nsec == UTIME_OMIT) return 0;
            if (m.nsec != UTIME_NOW) mtime = m.sec * 1000 + m.nsec / 1_000_000;
        }
        return Guard(() => _fs.Utimens(p, mtime));
    }

    /// <summary>init(): ajusta struct fuse_config (offsets conferidos com offsetof na libfuse 3.18).</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static IntPtr Init(IntPtr conn, byte* cfg)
    {
        if (cfg != null)
        {
            *(int*)(cfg + 60) = 1; // hard_remove: não cria .fuse_hiddenXXXX ao apagar arquivo aberto
            *(int*)(cfg + 80) = 1; // auto_cache: invalida page cache se mtime/tamanho mudou (alteração na nuvem)
        }
        return IntPtr.Zero;
    }

    // ---------- montagem ----------

    // ordem dos campos de struct fuse_operations (fuse3)
    enum OpIndex
    {
        getattr, readlink, mknod, mkdir, unlink, rmdir, symlink, rename, link, chmod, chown, truncate, open, read, write,
        statfs, flush, release, fsync, setxattr, getxattr, listxattr, removexattr, opendir, readdir, releasedir, fsyncdir,
        init, destroy, access, create, @lock, utimens, bmap, ioctl, poll, write_buf, read_buf, flock, fallocate,
        copy_file_range, lseek, Count,
    }

    static IntPtr BuildOps()
    {
        var n = (int)OpIndex.Count;
        var ops = (IntPtr*)NativeMemory.AllocZeroed((nuint)(n * sizeof(IntPtr)));
        ops[(int)OpIndex.getattr] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, byte*, IntPtr, int>)&GetAttr;
        ops[(int)OpIndex.mkdir] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, uint, int>)&Mkdir;
        ops[(int)OpIndex.unlink] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, int>)&Unlink;
        ops[(int)OpIndex.rmdir] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, int>)&Rmdir;
        ops[(int)OpIndex.rename] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, byte*, uint, int>)&Rename;
        ops[(int)OpIndex.chmod] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, uint, IntPtr, int>)&Chmod;
        ops[(int)OpIndex.chown] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, uint, uint, IntPtr, int>)&Chown;
        ops[(int)OpIndex.truncate] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, long, IntPtr, int>)&Truncate;
        ops[(int)OpIndex.open] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, IntPtr, int>)&Open;
        ops[(int)OpIndex.read] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, byte*, nuint, long, IntPtr, int>)&Read;
        ops[(int)OpIndex.write] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, byte*, nuint, long, IntPtr, int>)&Write;
        ops[(int)OpIndex.statfs] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, byte*, int>)&StatFs;
        ops[(int)OpIndex.flush] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, IntPtr, int>)&Flush;
        ops[(int)OpIndex.release] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, IntPtr, int>)&Release;
        ops[(int)OpIndex.fsync] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, int, IntPtr, int>)&Fsync;
        ops[(int)OpIndex.setxattr] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, byte*, byte*, nuint, int, int>)&SetXattr;
        ops[(int)OpIndex.getxattr] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, byte*, byte*, nuint, int>)&GetXattr;
        ops[(int)OpIndex.listxattr] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, byte*, nuint, int>)&ListXattr;
        ops[(int)OpIndex.removexattr] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, byte*, int>)&RemoveXattr;
        ops[(int)OpIndex.opendir] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, IntPtr, int>)&OpenDir;
        ops[(int)OpIndex.readdir] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, IntPtr, IntPtr, long, IntPtr, int, int>)&ReadDir;
        ops[(int)OpIndex.releasedir] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, IntPtr, int>)&ReleaseDir;
        ops[(int)OpIndex.init] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr>)&Init;
        ops[(int)OpIndex.access] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, int, int>)&Access;
        ops[(int)OpIndex.create] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, uint, IntPtr, int>)&Create;
        ops[(int)OpIndex.utimens] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, Timespec*, IntPtr, int>)&Utimens;
        return (IntPtr)ops;
    }

    public static bool IsMounted => _fuse != IntPtr.Zero;
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

    /// <summary>Monta e executa o loop do FUSE numa thread dedicada. Retorna a Task que termina ao desmontar.</summary>
    public static Task MountAsync(VirtualFs fs, string mountPoint, int maxThreads = 16)
    {
        if (_fuse != IntPtr.Zero) throw new InvalidOperationException("Já montado");
        _fs = fs;
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

        var f = fuse_new(&args, _ops, (nuint)((int)OpIndex.Count * sizeof(IntPtr)), IntPtr.Zero);
        if (f == IntPtr.Zero) throw new IOException("fuse_new falhou (opções inválidas?)");
        if (fuse_mount(f, mountPoint) != 0)
        {
            fuse_destroy(f);
            throw new IOException($"Falha ao montar em {mountPoint}. /dev/fuse acessível e fusermount3 instalado?");
        }
        _fuse = f;
        MountPoint = mountPoint;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var cfg = fuse_loop_cfg_create();
            fuse_loop_cfg_set_max_threads(cfg, (uint)maxThreads);
            try
            {
                var rc = fuse_loop_mt(f, cfg);
                Log?.Invoke($"Loop FUSE terminou (rc={rc})");
            }
            finally
            {
                fuse_loop_cfg_destroy(cfg);
                fuse_unmount(f);
                fuse_destroy(f);
                _fuse = IntPtr.Zero;
                MountPoint = null;
                foreach (var p in ptrs) Marshal.FreeCoTaskMem(p);
                NativeMemory.Free(argvPtr);
                tcs.TrySetResult();
            }
        }) { IsBackground = true, Name = "fuse-loop" };
        thread.Start();
        return tcs.Task;
    }

    /// <summary>Desmonta (faz o loop terminar).</summary>
    public static void Unmount()
    {
        var mp = MountPoint;
        var f = _fuse;
        if (f == IntPtr.Zero || mp == null) return;
        fuse_exit(f);
        RunFusermount("-u", mp);
    }
}
