using System.Net;
using System.Diagnostics;
using System.Text.Json;
using CraftDriver.Fuse;
using System.Text;
using CraftDriver.Core.Graph;
using CraftDriver.Core.State;
using CraftDriver.Core.Sync;
using CraftDriver.Core.Vfs;
using Microsoft.Data.Sqlite;
using static CraftDriver.Tests.TestSupport;

namespace CraftDriver.Tests;

[Collection("fuse")]
public class FrenteA1Tests
{
    [Fact]
    public void S12_3_commit_dos_fds_abertos_enfileira_sem_fechar()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        Assert.Equal(0, h.Fs.Create(h.P("aberto.txt"), OpenFlags.RdWr, out var fh));
        Assert.Equal(5, h.Fs.Write(fh, "local"u8, 0));
        // remove o envio do create: só o commit do fd pode reintroduzi-lo
        var item = h.Fs.Resolve(h.P("aberto.txt"))!.Value.Item!;
        h.Db.RemoveOpsForLid(item.Lid);
        h.Fs.CommitOpenWrites();
        Assert.True(h.Db.PendingOpsFor(item.Lid, OpKind.Upload));
        Assert.Equal(1, h.Fs.OpenHandles);
        Assert.Equal(5, h.Db.Get(item.Lid)!.Size);
        h.Fs.Release(fh);
    }

    [Fact]
    public void S12_6_preserva_nuvem_e_escritas_do_fd_que_ja_tinha_escrito_antes_do_412()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "doc.txt", "nuvem antiga");
        h.Settle();
        Assert.Equal(0, h.Fs.Open(h.P("doc.txt"), OpenFlags.RdWr | OpenFlags.Trunc, 0, out var fh));
        Assert.Equal(7, h.Fs.Write(fh, "local A"u8, 0));
        h.Fs.CommitOpenWrites();
        h.Remote.RemoteUpdate("/doc.txt", "versão de outro dispositivo");
        Harness.WaitUntil(() => h.Db.Conflicts().Count == 1 && h.Db.PendingOps() == 0 && !h.Drive.Busy, "cópia enviada");
        Assert.Equal(7, h.Fs.Write(fh, "local B"u8, 0));
        var copyLid = h.Db.Conflicts().Single().Lid;
        Assert.True(h.Db.Get(copyLid)!.Dirty, "a escrita após o conflito deve ficar Dirty mesmo com o fd ainda aberto (crash sem release)");
        h.Fs.Release(fh);
        h.Settle();
        Assert.Equal("versão de outro dispositivo", h.Remote.ReadText("/doc.txt"));
        var copy = h.Db.Conflicts().Single().Copy;
        Assert.Equal("local B", h.Remote.ReadText("/" + copy));
        Assert.False(h.Db.Get(h.Fs.Resolve(h.P("doc.txt"))!.Value.Item!.Lid)!.Dirty);
    }

    [Fact]
    public async Task S18_restaurar_exclusoes_busca_so_os_ids_e_nao_enumera_drive()
    {
        using var h = new Harness(c => c.MassDeleteThreshold = 1);
        h.Remote.RemoteCreate("/", "pasta", null, folder: true);
        h.Remote.RemoteCreate("/pasta", "filho.txt", "x");
        h.Settle();
        var link = h.Db.GetDrive(FakeGraph.DriveId)!.DeltaLink;
        h.Engine.Paused = true;
        Assert.Equal(0, h.Fs.Unlink(h.P("pasta/filho.txt")));
        Assert.Equal(0, h.Fs.Rmdir(h.P("pasta")));
        Assert.True(h.Drive.DeleteHold);
        var full = h.Remote.FullDeltaCalls;
        await h.Drive.RestoreDeletesAsync(Ct);
        Assert.NotNull(h.Fs.Resolve(h.P("pasta/filho.txt")));
        Assert.Equal(link, h.Db.GetDrive(FakeGraph.DriveId)!.DeltaLink);
        Assert.Equal(full, h.Remote.FullDeltaCalls);
        Assert.Equal(0, h.Db.PendingOps());
        Assert.False(h.Drive.DeleteHold);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task S18_delta_por_pasta_pessoal_ou_fallback_children(bool supported)
    {
        using var h = new Harness();
        var owner = h.Remote.AddOwnerDrive("dono");
        owner.SupportsFolderDelta = supported;
        owner.RemoteCreate("/", "Projeto", null, folder: true);
        owner.RemoteCreate("/Projeto", "a.txt", "v1");
        owner.Shares[owner.FindByPath("/Projeto")!.Id] = true;
        h.Remote.AddShortcut("/", "Projeto", owner, "/Projeto");
        h.Settle();
        Harness.WaitUntil(() => h.Engine.SharedDrives.Count == 1, "sub-drive");
        var sub = h.Engine.SharedDrives.Single();
        await sub.RunDeltaAsync(Ct);
        var before = owner.ChildrenCalls;
        owner.RemoteUpdate("/Projeto/a.txt", "v2");
        owner.RemoteCreate("/Projeto", "novo.txt", "novo");
        await sub.RunDeltaAsync(Ct);
        Assert.NotNull(h.Fs.Resolve(h.P("Projeto/novo.txt")));
        Assert.True(owner.FolderDeltaCalls > 0, "o endpoint por pasta não foi tentado");
        if (supported)
        {
            Assert.Equal(before, owner.ChildrenCalls);
            Assert.NotNull(h.Db.GetDrive(sub.DriveId)!.DeltaLink);
        }
        else
        {
            Assert.True(owner.ChildrenCalls > before);
            Assert.Null(h.Db.GetDrive(sub.DriveId)!.DeltaLink);
        }
    }

    [Fact]
    public async Task S16_delta_sem_mudanca_nao_invalida_ItemsVersion()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "pasta", null, folder: true);
        h.Remote.RemoteCreate("/pasta", "a.txt", "x");
        h.Settle();
        // full novamente: reaplica as mesmas linhas, mas nada muda (nem raiz)
        h.Engine.Paused = true;
        h.Db.SetDeltaLink(FakeGraph.DriveId, null);
        var version = h.Db.ItemsVersion;
        await h.Drive.RunDeltaAsync(Ct);
        Assert.Equal(version, h.Db.ItemsVersion);
    }

    [Fact]
    public async Task S16_lixeira_le_e_remove_sem_esperar_o_lock_do_motor()
    {
        using var h = new Harness();
        h.Db.AddTrash(FakeGraph.DriveId, "id", "a.txt", false);
        var rec = h.Db.Trash().Single();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim();
        var holding = Task.Run(() =>
        {
            lock (h.Db.Lock) { entered.SetResult(); release.Wait(TimeSpan.FromSeconds(10), Ct); }
        }, Ct);
        await entered.Task;
        var access = Task.Run(() => { Assert.Single(h.Db.Trash()); h.Db.RemoveTrash(rec.Id); }, Ct);
        try { Assert.True(await access.WithinAsync(2000), "a Lixeira esperou o Db.Lock (sem transação ativa)"); }
        finally { release.Set(); await holding; await access; }
        Assert.Empty(h.Db.Trash());
    }

    [Fact]
    public void S17_remover_drive_tambem_limpa_subdrives()
    {
        using var h = new Harness();
        var root = h.Db.Insert(new Item { DriveId = "host|atalho|id", ItemId = "remoto", IsDir = true });
        h.Db.SaveDrive(new DriveState("host|atalho|id", root, null, 0, 0));
        h.Db.SaveSharedRoot(new SharedRoot("host|atalho|id", "host", SharedSource.Shortcut, "id", "dono", "remoto", root, true));
        h.Db.SetExcluded("host|atalho|id", root, true);
        h.Db.AddConflict("host|atalho|id", root, "a", "cópia", "teste");
        h.Db.AddTrash("host|atalho|id", "apagado", "a", false);
        h.Db.DeleteDriveData("host");
        Assert.Null(h.Db.Get(root));
        Assert.Null(h.Db.GetDrive("host|atalho|id"));
        Assert.Empty(h.Db.SharedRoots("host"));
        Assert.Empty(h.Db.Conflicts());
        Assert.Empty(h.Db.Trash());
        Assert.False(h.Db.IsExcluded(root));
    }

    [Fact]
    public void S13_13_falha_e_retry_preservam_etag_da_exclusao()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        h.Db.Enqueue("d", OpKind.Delete, 0, "remoto", name: "a", etag: "e-antigo");
        var op = h.Db.PeekOp("d")!;
        h.Db.FailPermanently(op, "erro");
        h.Db.RetryFailure(h.Db.Failures("d").Single().Id);
        Assert.Equal("e-antigo", h.Db.PeekOp("d")!.ETag);
    }

    [Fact]
    public async Task S12_6_FUSE_real_fd_continua_escrevendo_na_copia_de_conflito()
    {
        RealFuse.Require();
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "doc.txt", "v1");
        h.Settle();
        var mnt = Path.Combine(h.Dir, "mnt");
        var loop = FuseHost.MountAsync(h.Fs, mnt);
        Process? editor = null;
        try
        {
            var path = Path.Combine(mnt, Harness.Name, "doc.txt");
            Harness.WaitUntil(() => File.Exists(path), "montagem visível");
            editor = Process.Start(new ProcessStartInfo("/usr/bin/python3", ["-u", "-c", """
                import os,sys
                f = open(sys.argv[1], 'r+b', buffering=0)
                f.truncate(0)
                f.write(b'local A')
                os.fsync(f.fileno())
                print('gravado', flush=True)
                sys.stdin.readline()
                f.seek(0)
                f.write(b'local B')
                f.close()
                """, path]) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            Assert.Equal("gravado", await editor.StandardOutput.ReadLineAsync(Ct));
            h.Fs.CommitOpenWrites();
            h.Remote.RemoteUpdate("/doc.txt", "versão de outro dispositivo");
            await Task.Run(() => Harness.WaitUntil(() => h.Db.Conflicts().Count == 1 && h.Db.PendingOps() == 0 && !h.Drive.Busy, "cópia enviada"), Ct);
            await editor.StandardInput.WriteLineAsync("seguir");
            Assert.True(await editor.WaitForExitAsync(Ct).WithinAsync(10000), "editor não fechou");
            Assert.Equal(0, editor.ExitCode);
            await Task.Run(h.Settle, Ct);
            Assert.Equal("versão de outro dispositivo", h.Remote.ReadText("/doc.txt"));
            Assert.Equal("local B", h.Remote.ReadText("/" + h.Db.Conflicts().Single().Copy));
        }
        finally
        {
            if (editor is { HasExited: false }) { editor.Kill(); await editor.WaitForExitAsync(Ct); }
            editor?.Dispose();
            h.Engine.Stop();
            FuseHost.Unmount();
            Assert.True(await loop.WithinAsync(15000), "FUSE não terminou");
        }
    }

    sealed class HttpFake(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(send(request));
    }

    [Fact]
    public async Task S12_5_e_S13_13_cliente_envia_conflictBehavior_e_IfMatch()
    {
        var behaviors = new List<string>();
        string? etag = null;
        var api = new GraphClient((_, _) => Task.FromResult("tk"), new HttpClient(new HttpFake(req =>
        {
            if (req.Method == HttpMethod.Patch)
            {
                using var doc = JsonDocument.Parse(req.Content!.ReadAsStringAsync(Ct).GetAwaiter().GetResult());
                behaviors.Add(doc.RootElement.GetProperty("@microsoft.graph.conflictBehavior").GetString()!);
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"i\",\"name\":\"novo\"}") };
            }
            etag = req.Headers.GetValues("If-Match").Single();
            return new(HttpStatusCode.NoContent);
        })));
        await api.MoveAsync("d", "i", "p", "novo", Ct);
        await api.MoveAsync("d", "i", "p", "novo", true, Ct);
        await api.DeleteAsync("d", "i", "etag-antigo", Ct);
        Assert.Equal(new[] { "fail", "replace" }, behaviors);
        Assert.Equal("etag-antigo", etag);
    }

    [Fact]
    public async Task S13_10_restoreVersion_nao_repete_apos_500_pos_processamento()
    {
        var posts = 0;
        var api = new GraphClient((_, _) => Task.FromResult("tk"), new HttpClient(new HttpFake(_ =>
        {
            if (++posts == 1) return new(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"error\":{\"code\":\"internalError\",\"message\":\"erro depois de restaurar\"}}"),
            };
            return new(HttpStatusCode.NoContent);
        })));
        var ex = await Record.ExceptionAsync(() => api.RestoreVersionAsync("d", "i", "v", Ct));
        Assert.IsType<GraphException>(ex);
        Assert.Equal(1, posts);
    }
}
