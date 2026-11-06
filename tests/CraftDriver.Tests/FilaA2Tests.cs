using CraftDriver.Core.Config;
using CraftDriver.Core.State;
using CraftDriver.Core.Sync;
using CraftDriver.Core.Graph;
using Microsoft.Data.Sqlite;
using static CraftDriver.Tests.TestSupport;

namespace CraftDriver.Tests;

public class FilaA2Tests
{
    [Fact]
    public void Ajustes_do_motor_nao_sao_estaticos_mutaveis()
    {
        foreach (var (type, names) in new[]
        {
            (typeof(SyncEngine), new[] { "MaxDownloadResumes", "DownloadAttempts" }),
            (typeof(DriveSync), new[] { "SharedPollSeconds", "SharedMaxItems", "DeleteQuietMs", "UploadSettleMs", "MassDeleteWindow", "DeltaBatchSize", "PinBatchSize" }),
        })
            foreach (var name in names)
                Assert.False(type.GetProperty(name)!.SetMethod!.IsStatic, $"{type.Name}.{name} é estático mutável: testes/instâncias interferem uns nos outros");
    }

    [Fact]
    public void Claim_reserva_atomicamente_sem_coalescer_running_e_serializa_lid()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fila-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var db = new StateDb(Path.Combine(dir, "s.db"));
            var a = db.Insert(new Item { DriveId = "d", Name = "a", Dirty = true });
            var b = db.Insert(new Item { DriveId = "d", Name = "b", Dirty = true });
            db.Enqueue("d", OpKind.Upload, a);
            db.Enqueue("d", OpKind.Move, a);
            db.Enqueue("d", OpKind.Upload, b);
            var claimed = db.ClaimOps("d", 4, long.MaxValue);
            Assert.Equal(new[] { a, b }, claimed.Select(o => o.Lid));
            Assert.Empty(db.ClaimOps("d", 4, long.MaxValue)); // já reservados, Move espera o upload de a
            db.Enqueue("d", OpKind.Upload, a); // não pode coalescer com o upload em execução
            Assert.Equal(4, db.PendingOps());
            db.CompleteOp(claimed[0].Id);
            db.CompleteOp(claimed[1].Id);
            var move = Assert.Single(db.ClaimOps("d", 4, long.MaxValue));
            Assert.Equal(OpKind.Move, move.Kind);
            db.CompleteOp(move.Id);
            var nextUpload = Assert.Single(db.ClaimOps("d", 4, long.MaxValue));
            Assert.Equal(a, nextUpload.Lid);
            db.ResetRunningOps("d");
            Assert.Equal(nextUpload.Id, Assert.Single(db.ClaimOps("d", 1, long.MaxValue)).Id);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Claim_pula_backoff_mas_respeita_mkdir_e_ordem_da_mesma_lid()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fila-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var db = new StateDb(Path.Combine(dir, "s.db"));
            var parent = db.Insert(new Item { DriveId = "d", Name = "p", IsDir = true });
            var child = db.Insert(new Item { DriveId = "d", ParentLid = parent, Name = "filho", Dirty = true });
            var free = db.Insert(new Item { DriveId = "d", Name = "livre", Dirty = true });
            db.Enqueue("d", OpKind.Mkdir, parent);
            var mkdir = db.PeekOp("d")!;
            db.FailOp(mkdir.Id, "espera", TimeSpan.FromHours(1));
            db.Enqueue("d", OpKind.Upload, child);
            db.Enqueue("d", OpKind.Move, child);
            db.Enqueue("d", OpKind.Upload, free);
            var claimed = Assert.Single(db.ClaimOps("d", 4, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            Assert.Equal(free, claimed.Lid);
            db.CompleteOp(claimed.Id);
            Assert.Empty(db.ClaimOps("d", 4, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            db.CompleteOp(mkdir.Id);
            var p = db.Get(parent)!; p.ItemId = "remoto"; db.Update(p);
            Assert.Equal(child, Assert.Single(db.ClaimOps("d", 4, long.MaxValue)).Lid);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Uploads_paralelos_delta_rename_delete_e_edicao_na_mesma_lid()
    {
        using var h = new Harness();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        h.Remote.BeforeUpload = async (name, ct) =>
        {
            if (name is not ("a.txt" or "b.txt")) return;
            if (Interlocked.Increment(ref count) == 2) started.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        try
        {
            h.WriteFile("/a.txt", "a original");
            h.WriteFile("/b.txt", "b original");
            h.Engine.SyncNow();
            Assert.True(await started.Task.WithinAsync(6000), "dois envios independentes não começaram em paralelo");
            Assert.Equal(0, h.Fs.Rename(h.P("/a.txt"), h.P("/renomeado.txt"), 0));
            h.WriteFile("/renomeado.txt", "a novo", create: false);
            Assert.Equal(0, h.Fs.Unlink(h.P("/b.txt")));
            h.Remote.RemoteCreate("/", "nuvem.txt", "remoto");
            Assert.True(await h.Drive.RunDeltaAsync(Ct).WithinAsync(2000), "delta preso no upload");
        }
        finally { release.TrySetResult(); h.Remote.BeforeUpload = null; }
        await Task.Run(h.Settle, Ct);
        Assert.Equal("a novo", h.Remote.ReadText("/renomeado.txt"));
        Assert.Null(h.Remote.ReadText("/a.txt"));
        Assert.Null(h.Remote.ReadText("/b.txt"));
        Assert.Equal("remoto", h.ReadFile("/nuvem.txt"));
        Assert.Empty(h.Db.Failures());
        Assert.Empty(h.Db.Conflicts());
    }

    [Fact]
    public void Metadado_pronto_nao_pode_ser_ultrapassado_sem_fim_por_uploads_novos()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fila-justa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var db = new StateDb(Path.Combine(dir, "s.db"));
            var a = db.Insert(new Item { DriveId = "d", Name = "a" });
            var p = db.Insert(new Item { DriveId = "d", Name = "p", IsDir = true });
            var b = db.Insert(new Item { DriveId = "d", Name = "b" });
            db.Enqueue("d", OpKind.Upload, a);
            var first = Assert.Single(db.ClaimOps("d", 1, long.MaxValue));
            db.Enqueue("d", OpKind.Mkdir, p);
            db.Enqueue("d", OpKind.Upload, b);
            Assert.Empty(db.ClaimOps("d", 1, long.MaxValue)); // drena o upload atual antes do metadado
            db.CompleteOp(first.Id);
            Assert.Equal(OpKind.Mkdir, Assert.Single(db.ClaimOps("d", 1, long.MaxValue)).Kind);
        }
        finally { Directory.Delete(dir, true); }
    }

    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    [Fact]
    public async Task Enumeracao_inicial_retoma_nextLink_apos_reinicio_sem_perder_seen_e_filhos_adiados()
    {
        var dir = Path.Combine(Path.GetTempPath(), "delta-retoma-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        const string next = "https://graph.microsoft.com/v1.0/pagina2?opaco=a%2Bb";
        var urls = new List<string>();
        var secondRun = false;
        using var http = new HttpClient(new Handler((r, _) =>
        {
            var url = r.RequestUri!.OriginalString;
            urls.Add(url);
            string body;
            if (url == next)
            {
                if (!secondRun) return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden));
                body = "{\"value\":[{\"id\":\"p\",\"name\":\"Pasta\",\"folder\":{},\"parentReference\":{\"id\":\"root\"}}],\"@odata.deltaLink\":\"delta:fim\"}";
            }
            else body = $$$"""{"value":[{"id":"a","name":"a.txt","cTag":"v1","parentReference":{"id":"root"}},{"id":"f","name":"filho.txt","cTag":"v1","parentReference":{"id":"p"}}],"@odata.nextLink":"{{{next}}}"}""";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }));
        try
        {
            var cfg = new AppConfig { Paused = true, CacheDir = Path.Combine(dir, "cache"), Drives = [new DriveConfig { DriveId = "d", DisplayName = "d" }] };
            using (var db = new StateDb(Path.Combine(dir, "s.db")))
            {
                var root = db.Insert(new Item { DriveId = "d", ItemId = "root", IsDir = true });
                db.SaveDrive(new DriveState("d", root, null, 0, 0));
                db.Insert(new Item { DriveId = "d", ItemId = "sumiu", ParentLid = root, Name = "sumiu.txt" });
                using var e = new SyncEngine(cfg, db, new ContentCache(cfg.CacheDir), new ActivityLog(Path.Combine(dir, "log")), _ => new GraphClient((_, _) => Task.FromResult("tk"), http));
                e.Start();
                await Assert.ThrowsAsync<GraphException>(() => e.GetDrive("d")!.RunDeltaAsync(Ct));
                Assert.NotNull(db.GetByRemote("d", "a"));
            }
            urls.Clear(); secondRun = true;
            using (var db = new StateDb(Path.Combine(dir, "s.db")))
            using (var e = new SyncEngine(cfg, db, new ContentCache(cfg.CacheDir), new ActivityLog(Path.Combine(dir, "log")), _ => new GraphClient((_, _) => Task.FromResult("tk"), http)))
            {
                e.Start();
                await e.GetDrive("d")!.RunDeltaAsync(Ct);
                Assert.Equal(next, urls[0]);
                Assert.NotNull(db.GetByRemote("d", "a")); // visto antes de reiniciar: Sweep não apaga
                Assert.NotNull(db.GetByRemote("d", "f")); // filho adiado na primeira página sobrevive
                Assert.Null(db.GetByRemote("d", "sumiu")); // só remove o ausente de toda a enumeração
                Assert.Equal("delta:fim", db.GetDrive("d")!.DeltaLink);
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delta_durante_resposta_do_upload_nao_cria_conflito_nem_duplica_id_remoto(bool rename)
    {
        using var h = new Harness();
        var uploaded = new TaskCompletionSource<RemoteItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.AfterUpload = async (r, ct) => { uploaded.TrySetResult(r); await release.Task.WaitAsync(ct); };
        long lid;
        try
        {
            h.WriteFile("/a.txt", "local");
            lid = h.Fs.Resolve(h.P("/a.txt"))!.Value.Item!.Lid;
            h.Engine.SyncNow();
            Assert.True(await uploaded.Task.WithinAsync(6000), "upload não chegou ao servidor");
            if (rename) Assert.Equal(0, h.Fs.Rename(h.P("/a.txt"), h.P("/novo.txt"), 0));
            Assert.True(await h.Drive.RunDeltaAsync(Ct).WithinAsync(2000), "delta não passou durante resposta pendente");
            Assert.Empty(h.Db.Conflicts());
            Assert.Equal(lid, h.Fs.Resolve(h.P(rename ? "/novo.txt" : "/a.txt"))!.Value.Item!.Lid);
            if (rename) Assert.Null(h.Fs.Resolve(h.P("/a.txt")));
        }
        finally { release.TrySetResult(); h.Remote.AfterUpload = null; }
        await Task.Run(h.Settle, Ct);
        Assert.Equal(lid, h.Db.GetByRemote(FakeGraph.DriveId, (await uploaded.Task).Id)!.Lid);
        Assert.Equal("local", h.Remote.ReadText(rename ? "/novo.txt" : "/a.txt"));
        if (rename) Assert.Null(h.Fs.Resolve(h.P("/a.txt")));
        Assert.Empty(h.Db.Failures());
        Assert.Empty(h.Db.Conflicts());
    }

    [Fact]
    public void Cache_apaga_todas_as_versoes_parciais_da_drive_e_snapshots_orfaos_mas_preserva_vivos()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        var old = h.Engine.Cache.PartialPathFor("removida", 999, "antigo");
        var current = h.Engine.Cache.PartialPathFor("removida", 999, "novo");
        // parcial da versão atual de um item vivo (de outra versão também seria órfão: §23.11)
        var other = h.Engine.Cache.PartialPathFor(FakeGraph.DriveId, h.Drive.RootLid, h.Db.Get(h.Drive.RootLid)!.CTag);
        foreach (var path in new[] { old, old + ".stream", old + ".blocks", current, other }) File.WriteAllText(path, "x");
        h.Engine.Cache.DeleteDrive("removida");
        Assert.All(new[] { old, old + ".stream", old + ".blocks", current }, p => Assert.False(File.Exists(p), p));
        Assert.True(File.Exists(other));
        var dead = h.Engine.Cache.PartialPathFor(FakeGraph.DriveId, 99999, "morto");
        File.WriteAllText(dead, "lixo");
        h.Engine.Cache.CleanStale([], h.Db.Get);
        Assert.False(File.Exists(dead));
        Assert.True(File.Exists(other));
        var orphan = h.Engine.Cache.NewUploadSnapshot(); File.WriteAllText(orphan, "lixo");
        var live = h.Engine.Cache.NewUploadSnapshot(); File.WriteAllText(live, "retomável");
        h.Db.Enqueue(FakeGraph.DriveId, OpKind.Upload, h.Drive.RootLid);
        var op = h.Db.PeekOp(FakeGraph.DriveId)!;
        h.Db.SetOpUpload(op.Id, "sessao", live, 1);
        h.Engine.EnforceCacheLimit();
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(live));
        Assert.True(File.Exists(other));
    }

    // §22.2: 401/5xx agora têm teto (50) quando o servidor atende os outros pedidos — ver Rodada3MotorFilaTests.S22_2_*;
    // falta de rede continua sem teto.
    [Theory]
    [InlineData(0)]
    public async Task Rede_nao_vira_permanente_por_numero_de_tentativas(int status)
    {
        using var h = new Harness();
        h.Remote.UploadError = name => name != "bloqueado" ? null : status == 0
            ? new HttpRequestException("rede caiu")
            : new GraphException((System.Net.HttpStatusCode)status, "temporario", "falha temporária");
        h.WriteFile("/bloqueado", "aguarda");
        h.Engine.SyncNow();
        Harness.WaitUntil(() => h.Db.PeekOp(FakeGraph.DriveId) is { Attempts: >= 1 }, "primeira falha");
        using (var c = new SqliteConnection($"Data Source={h.DbPath};Pooling=False"))
        {
            c.Open(); using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE ops SET attempts=50,next_try=0 WHERE kind='upload' AND attempts>0";
            cmd.ExecuteNonQuery();
        }
        h.WriteFile("/livre", "enviado");
        h.Drive.WakeOps();
        Assert.True(await Task.Run(() =>
        {
            Harness.WaitUntil(() => h.Remote.ReadText("/livre") == "enviado", "upload livre passou pelo backoff");
            return true;
        }, Ct));
        Assert.Empty(h.Db.Failures());
        Assert.True(h.Db.PeekOp(FakeGraph.DriveId)!.Attempts >= 51);
    }

    [Fact]
    public async Task Sweep_nao_remove_commit_de_upload_que_caiu_na_janela_da_enumeracao()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "a.txt", "v1");
        h.Settle();
        var item = h.Fs.Resolve(h.P("/a.txt"))!.Value.Item!;
        Assert.Equal("v1", h.ReadFile("/a.txt"));
        var pageReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.AfterDelta = async (page, ct) =>
        {
            pageReady.TrySetResult();
            await releasePage.Task.WaitAsync(ct);
            return page with { Items = page.Items.Where(r => r.Id != item.ItemId).ToList() };
        };
        Task? delta = null;
        try
        {
            h.Db.SetDeltaLink(FakeGraph.DriveId, null);
            delta = h.Drive.RunDeltaAsync(Ct);
            Assert.True(await pageReady.Task.WithinAsync(3000), "página não chegou");
            h.WriteFile("/a.txt", "v2", create: false);
            h.Drive.WakeOps();
            await Task.Run(() => Harness.WaitUntil(() => h.Db.Get(item.Lid) is { Dirty: false } && h.Remote.ReadText("/a.txt") == "v2", "commit durante enumeração"), Ct);
        }
        finally
        {
            releasePage.TrySetResult();
            if (delta != null) await delta;
            h.Remote.AfterDelta = null;
        }
        Assert.NotNull(h.Db.Get(item.Lid));
        Assert.Equal("v2", h.ReadFile("/a.txt"));
        Assert.Empty(h.Db.Conflicts());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Commit_junta_id_remoto_que_delta_ja_inseriu_sem_violar_indice_unico(bool editedEcho)
    {
        using var h = new Harness();
        var uploaded = new TaskCompletionSource<RemoteItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.AfterUpload = async (r, ct) => { uploaded.TrySetResult(r); await release.Task.WaitAsync(ct); };
        long lid;
        long duplicate = 0;
        try
        {
            h.WriteFile("/a.txt", "local");
            lid = h.Fs.Resolve(h.P("/a.txt"))!.Value.Item!.Lid;
            h.Engine.SyncNow();
            Assert.True(await uploaded.Task.WithinAsync(6000), "upload não chegou");
            var remote = await uploaded.Task;
            // Simula a linha que o delta inseriu com outro nome enquanto a resposta estava a caminho.
            duplicate = h.Db.Insert(new Item
            {
                DriveId = FakeGraph.DriveId, ParentLid = h.Drive.RootLid, ItemId = remote.Id, Name = "eco.txt", CTag = remote.CTag,
                Dirty = editedEcho, DirtyGen = editedEcho ? 1 : 0, Size = editedEcho ? 16 : 0
            });
            if (editedEcho) File.WriteAllText(h.Engine.Cache.PathFor(FakeGraph.DriveId, duplicate), "alteracao no eco");
        }
        finally { release.TrySetResult(); h.Remote.AfterUpload = null; }
        await Task.Run(h.Settle, Ct);
        Assert.Equal(lid, h.Db.GetByRemote(FakeGraph.DriveId, (await uploaded.Task).Id)!.Lid);
        if (editedEcho)
        {
            var preserved = h.Db.Get(duplicate);
            Assert.NotNull(preserved);
            Assert.Equal("alteracao no eco", h.Remote.ReadText("/" + preserved.Name));
            Assert.Single(h.Db.Conflicts());
        }
        else
        {
            Assert.Null(h.Db.Get(duplicate));
            Assert.Empty(h.Db.Conflicts());
        }
        Assert.Empty(h.Db.Failures());
    }

    [Fact]
    public async Task Exclusao_remota_durante_resposta_do_upload_nao_se_perde_no_delta()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "a.txt", "v1");
        h.Settle();
        Assert.Equal("v1", h.ReadFile("/a.txt"));
        var uploaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.AfterUpload = async (_, ct) => { uploaded.TrySetResult(); await release.Task.WaitAsync(ct); };
        try
        {
            h.WriteFile("/a.txt", "v2", create: false);
            h.Engine.SyncNow();
            Assert.True(await uploaded.Task.WithinAsync(6000));
            h.Remote.RemoteDelete("/a.txt");
            await h.Drive.RunDeltaAsync(Ct); // exclusão chegou enquanto o commit ainda espera a resposta
        }
        finally { release.TrySetResult(); h.Remote.AfterUpload = null; }
        await Task.Run(h.Settle, Ct);
        Assert.Null(h.Fs.Resolve(h.P("/a.txt"))); // não pode ficar fantasma: o deltaLink já passou pela exclusão
        Assert.Empty(h.Db.Failures());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rename_remoto_durante_resposta_do_upload_e_reaplicado(bool newFile)
    {
        using var h = new Harness();
        if (!newFile)
        {
            h.Remote.RemoteCreate("/", "a.txt", "v1"); h.Settle();
            Assert.Equal("v1", h.ReadFile("/a.txt"));
        }
        var uploaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.AfterUpload = async (_, ct) => { uploaded.TrySetResult(); await release.Task.WaitAsync(ct); };
        try
        {
            h.WriteFile("/a.txt", "v2", create: newFile);
            h.Engine.SyncNow(); Assert.True(await uploaded.Task.WithinAsync(6000));
            h.Remote.RemoteRename("/a.txt", "renomeado-na-nuvem.txt");
            await h.Drive.RunDeltaAsync(Ct);
        }
        finally { release.TrySetResult(); h.Remote.AfterUpload = null; }
        await Task.Run(h.Settle, Ct);
        Assert.Null(h.Fs.Resolve(h.P("/a.txt")));
        Assert.Equal("v2", h.ReadFile("/renomeado-na-nuvem.txt"));
        Assert.Empty(h.Db.Failures());
        Assert.Empty(h.Db.Conflicts());
    }
}
