using System.Reflection;
using CraftDriver.Core.State;
using Microsoft.Data.Sqlite;

namespace CraftDriver.Tests;

public class PerformanceCoreTests(ITestOutputHelper output)
{
    sealed class Queries : IDisposable
    {
        readonly SqliteConnection connection;
        public int Count;
        public Queries(StateDb db)
        {
            connection = (SqliteConnection)typeof(StateDb).GetField("_c", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(db)!;
            // só as consultas desta thread: o motor (mesmo pausado) consulta a mesma conexão em segundo plano, e uma
            // dessas caía na janela medida em máquina lenta (101 em vez de 100 no contêiner Ubuntu)
            var thread = Environment.CurrentManagedThreadId;
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (_, sql) =>
            {
                if (Environment.CurrentManagedThreadId != thread) return;
                if (sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                    || sql.TrimStart().StartsWith("WITH", StringComparison.OrdinalIgnoreCase)) Count++;
            }, null);
        }
        public void Dispose() => SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
    }

    [Fact]
    public void Fixacao_herdada_usa_um_select_mesmo_em_arvore_profunda()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        var parent = h.Drive.RootLid;
        var root = h.Db.Get(parent)!;
        root.Pinned = true;
        h.Db.Update(root);
        for (var i = 0; i < 30; i++)
            parent = h.Db.Insert(new Item { DriveId = FakeGraph.DriveId, ParentLid = parent, Name = $"d{i}", IsDir = true });
        using var q = new Queries(h.Db);
        Assert.True(h.Db.IsEffectivelyPinned(parent));
        output.WriteLine($"Pinned depth 31: {q.Count} SELECTs");
        Assert.Equal(1, q.Count);
        root.Pinned = false;
        h.Db.Update(root);
        Assert.False(h.Db.IsEffectivelyPinned(parent));
        Assert.False(h.Db.IsEffectivelyPinned(long.MaxValue));
        var leaf = h.Db.Get(parent)!;
        leaf.Pinned = true;
        h.Db.Update(leaf);
        Assert.True(h.Db.IsEffectivelyPinned(parent));
        leaf.Pinned = false;
        leaf.ParentLid = leaf.Lid; // estado danificado não pode deixar a consulta em loop
        h.Db.Update(leaf);
        Assert.False(h.Db.IsEffectivelyPinned(parent));
    }

    [Fact]
    public void PathFor_reutiliza_diretorio_sem_hash_e_criacao_por_item()
    {
        using var h = new Harness();
        var cache = h.Engine.Cache;
        var first = cache.PathFor("drive/perigosa", 42);
        Assert.Equal("42", Path.GetFileName(first));
        Assert.Equal(first, cache.PathFor("drive/perigosa", 42));
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) cache.PathFor("drive/perigosa", i);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        output.WriteLine($"PathFor 1000 warmed calls: {allocated} allocated bytes");
        Assert.True(allocated < 350_000, $"PathFor alocou {allocated} bytes; deve reutilizar o diretório da drive.");
        cache.DeleteDrive("drive/perigosa");
        Assert.Equal(first, cache.PathFor("drive/perigosa", 42));
        File.WriteAllText(first, "recriado");
        Assert.True(cache.Exists("drive/perigosa", 42));
    }

    [Fact]
    public void Total_do_cache_inclui_parciais_sem_valores_stale()
    {
        using var h = new Harness();
        var cache = h.Engine.Cache;
        File.WriteAllBytes(cache.PathFor("totals", 1), new byte[123]);
        File.WriteAllBytes(cache.NewTempFile(), new byte[77]);
        Assert.Equal(200, cache.TotalBytes());
        cache.Delete("totals", 1);
        Assert.Equal(77, cache.TotalBytes());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mover_sincronizado_sobre_destino_preserva_origem_sem_transferir_e_handles(bool cached)
    {
        using var h = new Harness();
        var source = h.Remote.RemoteCreate("/", "source.txt", "source-content");
        h.Remote.RemoteCreate("/", "target.txt", "old-content");
        h.Settle();
        h.Engine.Paused = true;
        var lid = h.Fs.Resolve(h.P("/source.txt"))!.Value.Item!.Lid;
        Assert.Equal(0, h.Fs.Open(h.P("/target.txt"), 0, 0, out var oldTarget));
        ulong sourceHandle = 0;
        if (cached) Assert.Equal(0, h.Fs.Open(h.P("/source.txt"), 0, 0, out sourceHandle));
        var downloads = h.Remote.Downloads;
        var uploads = h.Remote.Uploads;
        Assert.Equal(0, h.Fs.Rename(h.P("/source.txt"), h.P("/target.txt"), 0));
        Assert.Null(h.Fs.Resolve(h.P("/source.txt")));
        Assert.Equal(lid, h.Fs.Resolve(h.P("/target.txt"))!.Value.Item!.Lid);
        Assert.False(h.Db.PendingOpsFor(lid, OpKind.Upload));
        Assert.Equal(downloads, h.Remote.Downloads);
        var buffer = new byte[100];
        var count = h.Fs.Read(oldTarget, buffer, 0);
        Assert.Equal("old-content", System.Text.Encoding.UTF8.GetString(buffer, 0, count));
        Assert.Equal(0, h.Fs.Release(oldTarget));
        if (cached)
        {
            count = h.Fs.Read(sourceHandle, buffer, 0);
            Assert.Equal("source-content", System.Text.Encoding.UTF8.GetString(buffer, 0, count));
            Assert.Equal(0, h.Fs.Release(sourceHandle));
        }
        h.Engine.Paused = false;
        h.Settle();
        Assert.Equal(source.Id, h.Remote.FindByPath("/target.txt")!.Id);
        Assert.Equal("source-content", h.Remote.ReadText("/target.txt"));
        Assert.Equal(1, h.Remote.Moves);
        Assert.Equal(uploads, h.Remote.Uploads);
        Assert.Equal(downloads, h.Remote.Downloads);
        Assert.Equal(0, h.Fs.OpenHandles);
    }

    [Fact]
    public async Task Cache_de_caminhos_invalida_descendentes_em_rename_local_e_delta()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "Folder", null, folder: true);
        var remoteFile = h.Remote.RemoteCreate("/Folder", "file.txt", "before");
        h.Settle();
        h.Engine.Paused = true;
        var oldPath = h.P("/Folder/file.txt");
        var lid = h.Fs.Resolve(oldPath)!.Value.Item!.Lid;
        Assert.Equal(0, h.Fs.Rename(h.P("/Folder"), h.P("/Renamed"), 0));
        Assert.Null(h.Fs.Resolve(oldPath));
        Assert.Equal(lid, h.Fs.Resolve(h.P("/Renamed/file.txt"))!.Value.Item!.Lid);
        h.Engine.Paused = false;
        h.Settle();
        h.Engine.Paused = true;
        await h.Remote.MoveAsync(FakeGraph.DriveId, remoteFile.Id, "root", "remote.txt", CancellationToken.None);
        await h.Drive.RunDeltaAsync(CancellationToken.None);
        Assert.Null(h.Fs.Resolve(h.P("/Renamed/file.txt")));
        Assert.Equal(lid, h.Fs.Resolve(h.P("/remote.txt"))!.Value.Item!.Lid);
        h.Remote.RemoteDelete("/remote.txt");
        await h.Drive.RunDeltaAsync(CancellationToken.None);
        Assert.Null(h.Fs.Resolve(h.P("/remote.txt")));
        h.Remote.RemoteCreate("/", "remote.txt", "replacement");
        await h.Drive.RunDeltaAsync(CancellationToken.None);
        Assert.NotEqual(lid, h.Fs.Resolve(h.P("/remote.txt"))!.Value.Item!.Lid);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Replace_de_origem_local_preserva_id_do_destino_e_versao_nova(bool temporary)
    {
        using var h = new Harness();
        var target = h.Remote.RemoteCreate("/", "target.txt", "old");
        h.Settle();
        h.Engine.Paused = true;
        var sourceName = temporary ? "/.goutputstream-new" : "/source.txt";
        h.WriteFile(sourceName, "new-content");
        Assert.Equal(0, h.Fs.Rename(h.P(sourceName), h.P("/target.txt"), 0));
        h.Engine.Paused = false;
        h.Settle();
        Assert.Equal(target.Id, h.Remote.FindByPath("/target.txt")!.Id);
        Assert.Equal("new-content", h.Remote.ReadText("/target.txt"));
        Assert.Equal(0, h.Remote.Moves);
        Assert.Equal(1, h.Remote.Uploads);
    }

    [Fact]
    public void Rename_noreplace_nao_baixa_nem_modifica_origem_online()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "source.txt", "source");
        h.Remote.RemoteCreate("/", "target.txt", "target");
        h.Settle();
        h.Engine.Paused = true;
        Assert.Equal(-CraftDriver.Core.Vfs.Errno.EEXIST,
            h.Fs.Rename(h.P("/source.txt"), h.P("/target.txt"), CraftDriver.Core.Vfs.VirtualFs.RenameNoReplace));
        Assert.Equal(0, h.Remote.Downloads);
        Assert.Equal(0, h.Db.PendingOps());
        Assert.NotNull(h.Fs.Resolve(h.P("/source.txt")));
        Assert.NotNull(h.Fs.Resolve(h.P("/target.txt")));
    }

    [Fact]
    public void Resolve_repetido_usa_um_select_e_reflete_mutacoes_e_rollback()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        var parent = h.Drive.RootLid;
        foreach (var name in new[] { "A", "B", "C", "D" })
        {
            var item = new Item { DriveId = FakeGraph.DriveId, ParentLid = parent, Name = name, IsDir = true };
            parent = h.Db.Insert(item);
        }
        var path = h.P("/A/B/C/D");
        Assert.NotNull(h.Fs.Resolve(path));
        using var q = new Queries(h.Db);
        for (var i = 0; i < 100; i++) Assert.Equal(parent, h.Fs.Resolve(path)!.Value.Item!.Lid);
        output.WriteLine($"100 resolves depth 5: {q.Count} SELECTs");
        Assert.Equal(100, q.Count);
        var leaf = h.Db.Get(parent)!;
        leaf.Size = 91;
        h.Db.Update(leaf);
        Assert.Equal(91, h.Fs.Resolve(path)!.Value.Item!.Size);
        leaf.Name = "E";
        h.Db.Update(leaf);
        Assert.Null(h.Fs.Resolve(path));
        Assert.Equal(parent, h.Fs.Resolve(h.P("/A/B/C/E"))!.Value.Item!.Lid);
        Assert.Throws<InvalidOperationException>(() => h.Db.InTransaction(() =>
        {
            leaf.Name = "F";
            h.Db.Update(leaf);
            Assert.NotNull(h.Fs.Resolve(h.P("/A/B/C/F")));
            throw new InvalidOperationException();
        }));
        Assert.Null(h.Fs.Resolve(h.P("/A/B/C/F")));
        Assert.NotNull(h.Fs.Resolve(h.P("/A/B/C/E")));
        h.Db.Delete(parent);
        Assert.Null(h.Fs.Resolve(h.P("/A/B/C/E")));
    }
}
