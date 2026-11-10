using System.Runtime.InteropServices;
using CraftDriver.Core.State;
using CraftDriver.Core.Vfs;
using Microsoft.Win32.SafeHandles;

namespace CraftDriver.Core.Sync;

public sealed record OfflineMaterializeResult(int Files, int Dirs, int Skipped);

public sealed record OfflineImportResult(int Edited, int Created, int Moved, int Unpinned, int Recovered, string? RecoveryDir)
{
    public bool AnyChange => Edited + Created + Moved + Unpinned + Recovered > 0;
}

/// <summary>
/// Itens fixados disponíveis com o app encerrado (como o OneDrive do Windows, que deixa os arquivos no NTFS).
/// Ao encerrar, depois de desmontar, <see cref="Materialize"/> grava cópias dos fixados na pasta REAL do ponto de
/// montagem (reflink no btrfs/XFS: sem gastar espaço) e um manifesto no banco. Na abertura seguinte, ANTES de o motor
/// começar (o delta não pode adotar a versão da nuvem antes: o envio com o eTag antigo é o que gera a cópia de
/// conflito), <see cref="Import"/> compara com o manifesto: edições e arquivos/pastas novos entram no cache como
/// alterações locais a enviar; arquivo movido/renomeado vira mover; apagado NÃO apaga na nuvem (só desfixa se era ele o
/// fixado). Depois as cópias saem da pasta — a montagem volta a mostrar o conteúdo. O que não dá para importar (fora
/// de uma pasta conhecida, compartilhado só leitura, link simbólico) vai para "&lt;montagem&gt; (recuperados)".
/// </summary>
public static class OfflineCopies
{
    // ---------- encerrar: gravar as cópias ----------

    public static OfflineMaterializeResult Materialize(SyncEngine e, VirtualFs fs, string root)
    {
        var db = e.Db;
        if (db.OfflineCopies().Count > 0) return new(0, 0, 0); // cópias anteriores ainda não importadas: não misturar
        Directory.CreateDirectory(root);
        var entries = new Dictionary<string, OfflineCopy>(StringComparer.Ordinal);
        int files = 0, dirs = 0, skipped = 0;

        void EnsureDir(string rel, long lid)
        {
            if (entries.ContainsKey(rel)) return;
            var full = Full(root, rel);
            Directory.CreateDirectory(full);
            entries[rel] = new OfflineCopy(root, rel, lid, true, 0, 0);
            dirs++;
        }

        void EnsureAncestors(string rel)
        {
            var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i < parts.Length; i++)
            {
                var prefix = "/" + string.Join('/', parts.Take(i));
                if (entries.ContainsKey(prefix)) continue;
                EnsureDir(prefix, fs.Resolve(prefix) is { Item: { } it } ? it.Lid : 0);
            }
        }

        foreach (var pinned in db.AllPinned())
        {
            if (!db.IsEffectivelyPinned(pinned.Lid)) continue; // dentro de pasta excluída
            var subtree = pinned.IsDir ? db.GetSubtree(pinned.Lid) : [pinned];
            var withPath = new List<(string rel, Item item)>();
            foreach (var x in subtree)
                if (fs.PathOfLid(x.Lid) is { } rel) withPath.Add((rel, x));
            foreach (var (rel, x) in withPath.OrderBy(p => p.rel, StringComparer.Ordinal))
            {
                try
                {
                    EnsureAncestors(rel);
                    if (x.IsDir) { EnsureDir(rel, x.Lid); continue; }
                    if (x.LocalOnly) { skipped++; continue; }
                    var cache = e.Cache.PathFor(x.DriveId, x.Lid);
                    if (!SyncEngine.IsContentCurrent(x, e.Cache) || !File.Exists(cache)) { skipped++; continue; }
                    var full = Full(root, rel);
                    if (Path.Exists(full)) { skipped++; continue; } // nunca sobrescrever algo que já está na pasta real
                    CloneOrCopy(cache, full);
                    var readOnly = e.IsReadOnly(x.DriveId);
                    File.SetUnixFileMode(full, (UnixFileMode)(readOnly ? 0x124 : x.Mode != 0 ? x.Mode & 0x1FF : 0x1A4));
                    var mtime = DateTimeOffset.FromUnixTimeMilliseconds(x.MTime).UtcDateTime
                        .AddTicks(db.FracaoMtime(x.Lid, x.MTime) / 100);
                    File.SetLastWriteTimeUtc(full, mtime);
                    var fi = new FileInfo(full);
                    entries[rel] = new OfflineCopy(root, rel, x.Lid, false, fi.Length, fi.LastWriteTimeUtc.Ticks);
                    files++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    e.Activity.Debug($"Cópia offline de '{rel}' não gravada: {ex.Message}");
                    skipped++;
                }
            }
        }
        if (files > 0) db.SaveOfflineCopies(entries.Values.ToList());
        else
        {
            // nada a deixar: não sobra árvore de pastas vazias no lugar da montagem
            foreach (var d in entries.Values.Where(c => c.IsDir).OrderByDescending(c => c.Rel.Length))
                try { Directory.Delete(Full(root, d.Rel)); } catch (IOException) { }
            dirs = 0;
        }
        return new(files, dirs, skipped);
    }

    // ---------- abrir: importar o que mudou ----------

    /// <param name="isMountPoint">Teste/injeção; padrão: /proc/self/mountinfo.</param>
    public static OfflineImportResult? Import(SyncEngine e, Func<string, bool>? isMountPoint = null)
    {
        // com o motor rodando o delta já pode ter adotado a versão da nuvem: o envio sobrescreveria sem cópia de conflito
        if (e.Drives.Count > 0) throw new InvalidOperationException("importar as cópias offline antes de SyncEngine.Start");
        var db = e.Db;
        var manifest = db.OfflineCopies();
        if (manifest.Count == 0) return null;
        var root = manifest[0].Root;
        if (!Directory.Exists(root)) { db.ClearOfflineCopies(); return new(0, 0, 0, 0, 0, null); }
        if ((isMountPoint ?? IsMountPoint)(root)) return null; // outra montagem por cima: tenta na próxima abertura

        var byRel = manifest.ToDictionary(m => m.Rel, StringComparer.Ordinal);
        var dirLids = manifest.Where(m => m.IsDir).ToDictionary(m => m.Rel, m => m.Lid, StringComparer.Ordinal);
        var onDisk = new List<(string rel, FileSystemInfo info)>();
        Walk(new DirectoryInfo(root), "", onDisk);
        var present = onDisk.Select(d => d.rel).ToHashSet(StringComparer.Ordinal);
        var missingFiles = manifest.Where(m => !m.IsDir && !present.Contains(m.Rel)).ToList();

        int edited = 0, created = 0, moved = 0, unpinned = 0;
        var done = new List<string>();      // importados ou iguais: saem da pasta real
        var recover = new List<string>();   // não importáveis: vão para a pasta de recuperados

        foreach (var (rel, info) in onDisk)
        {
            try
            {
                var (parentRel, name) = SplitRel(rel);
                if (info.LinkTarget != null) { recover.Add(rel); continue; }
                if (info is DirectoryInfo)
                {
                    if (byRel.TryGetValue(rel, out var m) && m.IsDir) { done.Add(rel); continue; }
                    if (Parent(e, dirLids, parentRel) is not { } parent) continue; // filhos sem pai vão para recuperados
                    lock (db.Lock)
                    {
                        var existing = db.GetChildNoCase(parent.Lid, name);
                        if (existing is { IsDir: true }) { dirLids[rel] = existing.Lid; done.Add(rel); continue; }
                        var item = NewItem(e, parent, existing != null ? UniqueName(db, parent.Lid, name) : name, true, info);
                        db.Insert(item);
                        if (!item.LocalOnly) db.Enqueue(item.DriveId, OpKind.Mkdir, item.Lid);
                        dirLids[rel] = item.Lid;
                        if (existing != null)
                            db.AddConflict(item.DriveId, item.Lid, name, item.Name, "pasta criada com o craftDriver fechado; o nome já existia");
                    }
                    created++;
                    done.Add(rel);
                    continue;
                }
                if (info is not FileInfo fi || !IsRegularFile(fi)) { recover.Add(rel); continue; }

                if (byRel.TryGetValue(rel, out var mf) && !mf.IsDir && db.Get(mf.Lid) is { IsDir: false } it)
                {
                    var cache = e.Cache.PathFor(it.DriveId, it.Lid);
                    if (fi.Length == mf.Size && fi.LastWriteTimeUtc.Ticks == mf.MTimeTicks) { done.Add(rel); continue; }
                    if (File.Exists(cache) && SameContent(fi.FullName, cache)) { done.Add(rel); continue; }
                    if (e.IsReadOnly(it.DriveId)) { recover.Add(rel); continue; }
                    ReplaceContent(e, it.Lid, fi);
                    edited++;
                    done.Add(rel);
                    continue;
                }

                if (Parent(e, dirLids, parentRel) is not { } p) { recover.Add(rel); continue; }
                // movido/renomeado com o app fechado: mesmo conteúdo de um fixado que sumiu do lugar original
                var origin = missingFiles.FirstOrDefault(m => m.Size == fi.Length && db.Get(m.Lid) is { } src
                    && src.DriveId == p.DriveId && !e.IsReadOnly(src.DriveId)
                    && File.Exists(e.Cache.PathFor(src.DriveId, src.Lid))
                    && SameContent(fi.FullName, e.Cache.PathFor(src.DriveId, src.Lid)));
                if (origin != null)
                {
                    missingFiles.Remove(origin);
                    lock (db.Lock)
                    {
                        var src = db.Get(origin.Lid)!;
                        var clash = db.GetChildNoCase(p.Lid, name);
                        src.ParentLid = p.Lid;
                        src.Name = clash != null && clash.Lid != src.Lid ? UniqueName(db, p.Lid, name) : name;
                        db.Update(src);
                        if (src.ItemId != null && !src.LocalOnly) db.Enqueue(src.DriveId, OpKind.Move, src.Lid);
                    }
                    moved++;
                    done.Add(rel);
                    continue;
                }

                lock (db.Lock)
                {
                    var existing = db.GetChildNoCase(p.Lid, name);
                    var item = NewItem(e, p, existing != null ? UniqueName(db, p.Lid, name) : name, false, fi);
                    item.Dirty = true;
                    item.DirtyGen = 1;
                    item.Size = fi.Length;
                    db.Insert(item);
                    CloneOrCopy(fi.FullName, e.Cache.PathFor(item.DriveId, item.Lid));
                    db.DefinirFracaoMtime(item.Lid, item.MTime, Nanos(fi));
                    if (!item.LocalOnly) db.Enqueue(item.DriveId, OpKind.Upload, item.Lid);
                    if (existing != null)
                        db.AddConflict(item.DriveId, item.Lid, name, item.Name, "arquivo criado com o craftDriver fechado; o nome já existia");
                }
                created++;
                done.Add(rel);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                e.Activity.Add(ActivityKind.Error, $"Cópia offline '{rel}' não importada: {ex.Message}");
                recover.Add(rel);
            }
        }

        // apagado com o app fechado: a nuvem fica como está; só deixa de estar fixado se era ele o item fixado
        foreach (var m in manifest.Where(m => !present.Contains(m.Rel) && (m.IsDir || missingFiles.Contains(m))))
            lock (db.Lock)
                if (db.Get(m.Lid) is { Pinned: true } it)
                {
                    it.Pinned = false;
                    db.Update(it);
                    unpinned++;
                }

        // não importáveis (sem pasta conhecida acima, compartilhado só leitura, link simbólico, erro): saem do caminho
        // da montagem para uma pasta ao lado, em vez de ficarem escondidos debaixo dela
        string? recoveryDir = null;
        foreach (var rel in recover)
        {
            var full = Full(root, rel);
            var info = new FileInfo(full);
            if (!info.Exists && info.LinkTarget == null && !Directory.Exists(full)) continue;
            recoveryDir ??= Path.Combine(Path.GetDirectoryName(root.TrimEnd('/'))!,
                Path.GetFileName(root.TrimEnd('/')) + " (recuperados)", DateTime.Now.ToString("yyyy-MM-dd HHmmss"));
            var target = Full(recoveryDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (Directory.Exists(full) && info.LinkTarget == null) Directory.Move(full, target);
            else File.Move(full, target);
        }

        // tudo dentro do banco/cache: tira as cópias da pasta real (de baixo para cima)
        foreach (var rel in done.Where(r => File.Exists(Full(root, r)))) File.Delete(Full(root, rel));
        foreach (var (rel, _) in onDisk.Where(o => o.info is DirectoryInfo).OrderByDescending(o => o.rel.Length))
            try { Directory.Delete(Full(root, rel)); } catch (IOException) { /* sobrou algo: fica */ }
        db.ClearOfflineCopies();
        return new(edited, created, moved, unpinned, recover.Count, recoveryDir);
    }

    // ---------- auxiliares ----------

    static string Full(string root, string rel) => Path.Join(root, rel.TrimStart('/'));

    static (string parent, string name) SplitRel(string rel)
    {
        var i = rel.LastIndexOf('/');
        return (i <= 0 ? "/" : rel[..i], rel[(i + 1)..]);
    }

    static void Walk(DirectoryInfo dir, string rel, List<(string, FileSystemInfo)> into)
    {
        foreach (var x in dir.EnumerateFileSystemInfos().OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            var r = rel + "/" + x.Name;
            into.Add((r, x));
            if (x is DirectoryInfo d && d.LinkTarget == null) Walk(d, r, into);
        }
    }

    static bool IsRegularFile(FileInfo fi) =>
        (fi.Attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) == 0 && fi.LinkTarget == null;

    static Item? Parent(SyncEngine e, Dictionary<string, long> dirLids, string parentRel)
    {
        if (!dirLids.TryGetValue(parentRel, out var lid) || lid == 0) return null;
        return e.Db.Get(lid) is { IsDir: true } p && !e.IsReadOnly(p.DriveId) ? p : null;
    }

    static Item NewItem(SyncEngine e, Item parent, string name, bool isDir, FileSystemInfo info)
    {
        var mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new Item
        {
            DriveId = parent.DriveId, ParentLid = parent.Lid, Name = name, IsDir = isDir, MTime = mtime, CTime = now,
            LocalOnly = parent.LocalOnly || e.Names.IsLocalOnly(name), LastAccess = now,
            Mode = ModeOf(info, isDir),
        };
    }

    /// <summary>Como <c>VirtualFs.NormalizeMode</c>: 0 = padrão (0644/0755).</summary>
    static uint ModeOf(FileSystemInfo info, bool isDir)
    {
        var bits = (uint)info.UnixFileMode & 0x1FF;
        return bits == (isDir ? 0x1EDu : 0x1A4u) ? 0 : bits | VirtualFs.ExplicitMode;
    }

    static int Nanos(FileInfo fi) => (int)(fi.LastWriteTimeUtc.Ticks % TimeSpan.TicksPerMillisecond) * 100;

    /// <summary>Edição feita com o app fechado: vira alteração local a enviar (412 → cópia de conflito, como sempre).</summary>
    static void ReplaceContent(SyncEngine e, long lid, FileInfo fi)
    {
        var tmp = e.Cache.NewTempFile();
        CloneOrCopy(fi.FullName, tmp);
        lock (e.Db.Lock)
        {
            var cur = e.Db.Get(lid);
            if (cur == null) { File.Delete(tmp); throw new IOException("o item sumiu do banco"); }
            File.Move(tmp, e.Cache.PathFor(cur.DriveId, cur.Lid), overwrite: true);
            cur.Dirty = true;
            cur.DirtyGen++;
            cur.Size = fi.Length;
            cur.MTime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            e.Db.Update(cur);
            e.Db.DefinirFracaoMtime(lid, cur.MTime, Nanos(fi));
            if (!cur.LocalOnly) e.Db.Enqueue(cur.DriveId, OpKind.Upload, cur.Lid);
        }
    }

    static string UniqueName(StateDb db, long parentLid, string name)
    {
        var candidate = NamePolicy.ConflictName(name);
        for (var i = 2; db.GetChildNoCase(parentLid, candidate) != null; i++)
        {
            var ext = Path.GetExtension(name);
            candidate = NamePolicy.ConflictName((ext.Length > 0 ? name[..^ext.Length] : name) + $" {i}" + ext);
        }
        return candidate;
    }

    static bool SameContent(string a, string b)
    {
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        if (fa.Length != fb.Length) return false;
        var ba = new byte[1 << 16];
        var bb = new byte[1 << 16];
        while (true)
        {
            var na = fa.ReadAtLeast(ba, ba.Length, throwOnEndOfStream: false);
            var nb = fb.ReadAtLeast(bb, bb.Length, throwOnEndOfStream: false);
            if (na != nb || !ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
            if (na == 0) return true;
        }
    }

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    static extern int Ioctl(IntPtr fd, ulong request, IntPtr arg);

    const ulong FICLONE = 0x40049409;

    /// <summary>Conta reflinks feitos (testes).</summary>
    public static int ReflinkCount;

    /// <summary>Reflink (btrfs/XFS: as duas cópias dividem os blocos até alguém escrever); senão cópia comum.</summary>
    public static void CloneOrCopy(string source, string target)
    {
        try
        {
            using (var src = File.OpenHandle(source, FileMode.Open, FileAccess.Read))
            using (var dst = File.OpenHandle(target, FileMode.Create, FileAccess.Write))
            {
                if (TryClone(src, dst)) { Interlocked.Increment(ref ReflinkCount); return; }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        File.Copy(source, target, overwrite: true);
    }

    static bool TryClone(SafeFileHandle src, SafeFileHandle dst)
    {
        bool a = false, b = false;
        try
        {
            src.DangerousAddRef(ref a);
            dst.DangerousAddRef(ref b);
            return Ioctl(dst.DangerousGetHandle(), FICLONE, src.DangerousGetHandle()) == 0;
        }
        finally
        {
            if (a) src.DangerousRelease();
            if (b) dst.DangerousRelease();
        }
    }

    /// <summary>O caminho é ponto de montagem agora? (/proc/self/mountinfo, campo 5, com \040 etc.)</summary>
    public static bool IsMountPoint(string path)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd('/');
            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
            {
                var f = line.Split(' ');
                if (f.Length > 4 && Unescape(f[4]).TrimEnd('/') == full) return true;
            }
        }
        catch (IOException) { }
        return false;
    }

    static string Unescape(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\\([0-7]{3})",
        m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());
}
