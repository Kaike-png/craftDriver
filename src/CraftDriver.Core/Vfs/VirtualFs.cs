using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;
using CraftDriver.Core.State;
using CraftDriver.Core.Sync;

namespace CraftDriver.Core.Vfs;

public static class Errno
{
    public const int EPERM = 1, ENOENT = 2, EIO = 5, EBADF = 9, EACCES = 13, EBUSY = 16, EEXIST = 17, EXDEV = 18,
        ENOTDIR = 20, EISDIR = 21, EINVAL = 22, ENOSPC = 28, EROFS = 30, ENAMETOOLONG = 36, ENOSYS = 38,
        ENOTEMPTY = 39, ENODATA = 61, ENOTSUP = 95, EHOSTUNREACH = 113;
}

public static class OpenFlags
{
    public const int AccMode = 3, WrOnly = 1, RdWr = 2, Creat = 0x40, Excl = 0x80, Trunc = 0x200, Append = 0x400;
}

public readonly record struct FileAttr(bool IsDir, long Size, long MTimeMs, long CTimeMs, long Blocks);

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
    }

    readonly ConcurrentDictionary<ulong, Handle> _handles = new();
    readonly ConcurrentDictionary<long, int> _openCount = new();
    readonly ConcurrentDictionary<int, (bool blocked, DateTime at)> _pidCache = new();
    long _nextFh;

    public VirtualFs(SyncEngine engine)
    {
        _e = engine;
        _e.IsOpen = lid => _openCount.TryGetValue(lid, out var n) && n > 0;
    }

    public int OpenHandles => _handles.Count;

    // ---------- resolução de caminhos ----------

    public readonly record struct Node(bool IsVirtualRoot, DriveSync? Drive, Item? Item)
    {
        public bool IsDir => IsVirtualRoot || Item is { IsDir: true };
        public bool IsDriveRoot => Drive != null && Item != null && Item.Lid == Drive.RootLid;
    }

    IEnumerable<DriveSync> MountedDrives => _e.Drives.Where(d => d.Ready);

    static string Sanitize(string name) => name.Replace('/', '-');

    public Node? Resolve(string path)
    {
        if (path == "/" || path == "") return new Node(true, null, null);
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var drive = MountedDrives.FirstOrDefault(d => Sanitize(d.Config.DisplayName) == parts[0]);
        if (drive == null) return null;
        var cur = Db.Get(drive.RootLid);
        for (var i = 1; i < parts.Length && cur != null; i++)
        {
            if (!cur.IsDir) return null;
            cur = Db.GetChild(cur.Lid, parts[i]);
        }
        return cur == null ? null : new Node(false, drive, cur);
    }

    static (string parent, string name) Split(string path)
    {
        var trimmed = path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return (idx <= 0 ? "/" : trimmed[..idx], trimmed[(idx + 1)..]);
    }

    // ---------- atributos ----------

    FileAttr AttrOf(Item it)
    {
        if (it.IsDir) return new FileAttr(true, 4096, it.MTime, it.CTime, 0);
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
        return new FileAttr(false, size, it.MTime, it.CTime, blocks);
    }

    public int GetAttr(string path, out FileAttr attr)
    {
        attr = default;
        var n = Resolve(path);
        if (n == null) return -Errno.ENOENT;
        if (n.Value.IsVirtualRoot)
        {
            attr = new FileAttr(true, 4096, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0, 0);
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
                entries.Add((Sanitize(d.Config.DisplayName), new FileAttr(true, 4096, 0, 0, 0)));
            return 0;
        }
        if (!n.Value.Item!.IsDir) return -Errno.ENOTDIR;
        foreach (var c in Db.GetChildren(n.Value.Item.Lid))
            entries.Add((c.Name, AttrOf(c)));
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
            var cmd = File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ');
            var comm = File.ReadAllText($"/proc/{pid}/comm").Trim();
            blocked = _e.Config.BlockedProcesses.Any(b =>
                cmd.Contains(b, StringComparison.OrdinalIgnoreCase) || comm.Contains(b, StringComparison.OrdinalIgnoreCase));
        }
        catch { }
        if (_pidCache.Count > 2000) _pidCache.Clear();
        _pidCache[pid] = (blocked, DateTime.UtcNow);
        return blocked;
    }

    // ---------- abrir / criar ----------

    ulong Register(Item it, SafeFileHandle fh, bool writable, bool wrote)
    {
        var id = (ulong)Interlocked.Increment(ref _nextFh);
        _handles[id] = new Handle { Lid = it.Lid, DriveId = it.DriveId, File = fh, Writable = writable, Wrote = wrote };
        _openCount.AddOrUpdate(it.Lid, 1, (_, v) => v + 1);
        return id;
    }

    public int Open(string path, int flags, int callerPid, out ulong fh)
    {
        fh = 0;
        var n = Resolve(path);
        if (n == null) return -Errno.ENOENT;
        if (n.Value.IsDir) return -Errno.EISDIR;
        var item = n.Value.Item!;
        var write = (flags & OpenFlags.AccMode) != 0;
        var trunc = write && (flags & OpenFlags.Trunc) != 0;

        if (trunc)
        {
            lock (Db.Lock)
            {
                File.WriteAllBytes(_e.Cache.PathFor(item.DriveId, item.Lid), []);
                MarkDirty(item.Lid, 0);
            }
        }
        else if (!SyncEngine.IsContentCurrent(item, _e.Cache))
        {
            if (IsBlockedProcess(callerPid)) return -Errno.EIO;
            try
            {
                _e.EnsureContentAsync(item.Lid, _e.Token).GetAwaiter().GetResult();
            }
            catch (FileNotFoundException) { return -Errno.ENOENT; }
            catch (Exception ex)
            {
                _e.Activity.Add(ActivityKind.Error, $"Não foi possível baixar '{item.Name}': {ex.Message}");
                return -Errno.EIO;
            }
        }
        else Db.Touch(item.Lid);

        var path2 = _e.Cache.PathFor(item.DriveId, item.Lid);
        if (!File.Exists(path2)) File.WriteAllBytes(path2, []);
        var handle = File.OpenHandle(path2, FileMode.Open, write ? FileAccess.ReadWrite : FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        fh = Register(item, handle, write, trunc);
        return 0;
    }

    public int Create(string path, int flags, out ulong fh)
    {
        fh = 0;
        var (parentPath, name) = Split(path);
        if (name.Length > 255) return -Errno.ENAMETOOLONG;
        var p = Resolve(parentPath);
        if (p == null) return -Errno.ENOENT;
        if (p.Value.IsVirtualRoot) return -Errno.EACCES;
        var parent = p.Value.Item!;
        if (!parent.IsDir) return -Errno.ENOTDIR;

        Item item;
        lock (Db.Lock)
        {
            var existing = Db.GetChildNoCase(parent.Lid, name);
            if (existing != null)
            {
                if (existing.Name == name && !existing.IsDir && (flags & OpenFlags.Excl) == 0)
                    return Open(path, flags | OpenFlags.Trunc, 0, out fh);
                return -Errno.EEXIST;
            }
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            item = new Item
            {
                DriveId = parent.DriveId, ParentLid = parent.Lid, Name = name, IsDir = false, MTime = now, CTime = now,
                Dirty = true, DirtyGen = 1, LocalOnly = parent.LocalOnly || _e.Names.IsLocalOnly(name), LastAccess = now,
            };
            Db.Insert(item);
            File.WriteAllBytes(_e.Cache.PathFor(item.DriveId, item.Lid), []);
        }
        var handle = File.OpenHandle(_e.Cache.PathFor(item.DriveId, item.Lid), FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        fh = Register(item, handle, true, wrote: true);
        return 0;
    }

    void MarkDirty(long lid, long? size)
    {
        lock (Db.Lock)
        {
            var cur = Db.Get(lid);
            if (cur == null) return;
            cur.Dirty = true;
            cur.DirtyGen++;
            cur.MTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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

    public int Read(ulong fh, Span<byte> buffer, long offset)
    {
        if (!_handles.TryGetValue(fh, out var h)) return -Errno.EBADF;
        try { return RandomAccess.Read(h.File, buffer, offset); }
        catch (IOException) { return -Errno.EIO; }
    }

    public int Write(ulong fh, ReadOnlySpan<byte> data, long offset)
    {
        if (!_handles.TryGetValue(fh, out var h)) return -Errno.EBADF;
        if (!h.Writable) return -Errno.EBADF;
        try
        {
            RandomAccess.Write(h.File, data, offset);
        }
        catch (IOException ex) when (ex.HResult == 28 || ex.Message.Contains("space", StringComparison.OrdinalIgnoreCase))
        {
            return -Errno.ENOSPC;
        }
        catch (IOException) { return -Errno.EIO; }
        if (!h.Wrote)
        {
            h.Wrote = true;
            MarkDirty(h.Lid, null);
        }
        return data.Length;
    }

    public int Truncate(string path, long size, ulong? fh)
    {
        Handle? h = null;
        if (fh is { } f && f != 0) _handles.TryGetValue(f, out h);
        long lid;
        if (h != null) lid = h.Lid;
        else
        {
            var n = Resolve(path);
            if (n == null) return -Errno.ENOENT;
            if (n.Value.IsDir) return -Errno.EISDIR;
            lid = n.Value.Item!.Lid;
        }
        var item = Db.Get(lid);
        if (item == null) return -Errno.ENOENT;
        var cachePath = _e.Cache.PathFor(item.DriveId, lid);
        try
        {
            if (size == 0) { if (!File.Exists(cachePath)) File.WriteAllBytes(cachePath, []); }
            else if (!SyncEngine.IsContentCurrent(item, _e.Cache))
                _e.EnsureContentAsync(lid, _e.Token).GetAwaiter().GetResult();

            if (h != null) RandomAccess.SetLength(h.File, size);
            else
            {
                using var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                fs.SetLength(size);
            }
        }
        catch (Exception ex)
        {
            _e.Activity.Debug($"truncate {path}: {ex.Message}");
            return -Errno.EIO;
        }
        MarkDirty(lid, size);
        if (h != null) h.Wrote = true;
        else EnqueueUpload(lid);
        return 0;
    }

    public int Flush(ulong fh) => _handles.ContainsKey(fh) ? 0 : -Errno.EBADF;

    public int Fsync(ulong fh)
    {
        if (!_handles.TryGetValue(fh, out var h)) return -Errno.EBADF;
        if (h.Wrote)
        {
            RandomAccess.FlushToDisk(h.File);
            CommitWrite(h);
        }
        return 0;
    }

    void CommitWrite(Handle h)
    {
        var p = _e.Cache.PathFor(h.DriveId, h.Lid);
        if (!File.Exists(p)) return;
        MarkDirty(h.Lid, new FileInfo(p).Length);
        EnqueueUpload(h.Lid);
    }

    public int Release(ulong fh)
    {
        if (!_handles.TryRemove(fh, out var h)) return 0;
        try { h.File.Dispose(); } catch { }
        _openCount.AddOrUpdate(h.Lid, 0, (_, v) => Math.Max(0, v - 1));
        if (h.Wrote) CommitWrite(h);
        return 0;
    }

    // ---------- pastas / exclusão ----------

    public int Mkdir(string path)
    {
        var (parentPath, name) = Split(path);
        if (name.Length > 255) return -Errno.ENAMETOOLONG;
        var p = Resolve(parentPath);
        if (p == null) return -Errno.ENOENT;
        if (p.Value.IsVirtualRoot) return -Errno.EACCES;
        var parent = p.Value.Item!;
        if (!parent.IsDir) return -Errno.ENOTDIR;
        lock (Db.Lock)
        {
            if (Db.GetChildNoCase(parent.Lid, name) != null) return -Errno.EEXIST;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var item = new Item
            {
                DriveId = parent.DriveId, ParentLid = parent.Lid, Name = name, IsDir = true, MTime = now, CTime = now,
                LocalOnly = parent.LocalOnly || _e.Names.IsLocalOnly(name),
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
                Db.Enqueue(item.DriveId, OpKind.Delete, item.Lid, item.ItemId, Db.Get(item.ParentLid)?.ItemId, item.Name);
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
        if (n.Value.IsVirtualRoot || n.Value.IsDriveRoot) return -Errno.EPERM;
        if (n.Value.Item!.IsDir) return -Errno.EISDIR;
        RemoveItem(n.Value.Item);
        return 0;
    }

    public int Rmdir(string path)
    {
        var n = Resolve(path);
        if (n == null) return -Errno.ENOENT;
        if (n.Value.IsVirtualRoot || n.Value.IsDriveRoot) return -Errno.EPERM;
        var item = n.Value.Item!;
        if (!item.IsDir) return -Errno.ENOTDIR;
        if (Db.HasChildren(item.Lid)) return -Errno.ENOTEMPTY;
        RemoveItem(item);
        return 0;
    }

    // ---------- renomear / mover ----------

    public const uint RenameNoReplace = 1, RenameExchange = 2;

    public int Rename(string from, string to, uint flags)
    {
        if ((flags & RenameExchange) != 0) return -Errno.EINVAL;
        var s = Resolve(from);
        if (s == null) return -Errno.ENOENT;
        if (s.Value.IsVirtualRoot || s.Value.IsDriveRoot) return -Errno.EPERM;
        var src = s.Value.Item!;
        var (dstParentPath, newName) = Split(to);
        if (newName.Length > 255) return -Errno.ENAMETOOLONG;
        var dp = Resolve(dstParentPath);
        if (dp == null) return -Errno.ENOENT;
        if (dp.Value.IsVirtualRoot) return -Errno.EACCES;
        var dstParent = dp.Value.Item!;
        if (!dstParent.IsDir) return -Errno.ENOTDIR;
        if (dstParent.DriveId != src.DriveId) return -Errno.EXDEV; // mv faz copiar+apagar entre drives

        if (src.IsDir && (dstParent.Lid == src.Lid || Db.GetAncestors(dstParent.Lid).Any(a => a.Lid == src.Lid)))
            return -Errno.EINVAL;

        // se for sobrescrever um arquivo sincronizado, o conteúdo da origem precisa estar local (fora do lock)
        if (!src.IsDir && Db.GetChild(dstParent.Lid, newName) is { IsDir: false, ItemId: not null } && !SyncEngine.IsContentCurrent(src, _e.Cache))
        {
            try { _e.EnsureContentAsync(src.Lid, _e.Token).GetAwaiter().GetResult(); }
            catch { return -Errno.EIO; }
        }

        lock (Db.Lock)
        {
            src = Db.Get(src.Lid)!;
            var target = Db.GetChild(dstParent.Lid, newName);
            var caseClash = Db.GetChildNoCase(dstParent.Lid, newName);
            if (target == null && caseClash != null && caseClash.Lid != src.Lid) return -Errno.EEXIST;
            if (target != null && target.Lid == src.Lid) return 0;
            if (target != null && (flags & RenameNoReplace) != 0) return -Errno.EEXIST;

            var newLocalOnly = dstParent.LocalOnly || _e.Names.IsLocalOnly(newName);

            if (target != null)
            {
                if (target.IsDir)
                {
                    if (!src.IsDir) return -Errno.EISDIR;
                    if (Db.HasChildren(target.Lid)) return -Errno.ENOTEMPTY;
                    RemoveItem(target);
                }
                else
                {
                    if (src.IsDir) return -Errno.ENOTDIR;
                    if (target.ItemId != null && !target.LocalOnly && !newLocalOnly)
                    {
                        // "salvamento atômico" de editores (grava temp + renomeia por cima):
                        // vira nova versão do arquivo existente, preservando histórico de versões.
                        var srcPath = _e.Cache.PathFor(src.DriveId, src.Lid);
                        File.Move(srcPath, _e.Cache.PathFor(target.DriveId, target.Lid), true);
                        target.Dirty = true;
                        target.DirtyGen++;
                        target.Size = new FileInfo(_e.Cache.PathFor(target.DriveId, target.Lid)).Length;
                        target.MTime = src.MTime;
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
                    if (!x.IsDir)
                    {
                        if (!SyncEngine.IsContentCurrent(x, _e.Cache)) { /* conteúdo só na nuvem se perde localmente */ }
                        x.Dirty = true;
                    }
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
            _openCount.AddOrUpdate(fromLid, 0, (_, v) => Math.Max(0, v - 1));
            _openCount.AddOrUpdate(toLid, 1, (_, v) => v + 1);
        }
    }

    public int Utimens(string path, long? mtimeMs)
    {
        var n = Resolve(path);
        if (n == null) return -Errno.ENOENT;
        if (n.Value.IsVirtualRoot || n.Value.IsDriveRoot) return 0;
        lock (Db.Lock)
        {
            var it = Db.Get(n.Value.Item!.Lid)!;
            it.MTime = mtimeMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Db.Update(it);
            if (!it.LocalOnly && !it.IsDir) Db.Enqueue(it.DriveId, OpKind.SetTime, it.Lid);
        }
        _e.NotifyLocalChange(n.Value.Item.DriveId);
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
        if (it.IsDir) return it.LocalOnly ? ItemStatus.LocalOnly : (Db.IsEffectivelyPinned(it.Lid) ? ItemStatus.AlwaysAvailable : ItemStatus.Folder);
        if (it.LocalOnly) return ItemStatus.LocalOnly;
        if (it.Dirty || it.ItemId == null) return ItemStatus.Syncing;
        if (Db.IsEffectivelyPinned(it.Lid)) return ItemStatus.AlwaysAvailable;
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
        var n = Resolve(path);
        if (n?.Item == null) return null;
        return name switch
        {
            "user.onedrive.status" => StatusOf(n.Value.Item).ToString(),
            "user.onedrive.id" => n.Value.Item.ItemId,
            "user.onedrive.drive" => n.Value.Drive?.Config.DisplayName,
            _ => null,
        };
    }

    public static readonly string[] XattrNames = ["user.onedrive.status", "user.onedrive.id", "user.onedrive.drive"];
}
