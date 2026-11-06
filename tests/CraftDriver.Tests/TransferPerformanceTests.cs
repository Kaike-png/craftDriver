using System.Diagnostics;
using System.Net;
using System.Text;
using CraftDriver.Core.Config;
using CraftDriver.Core.Graph;
using CraftDriver.Core.State;
using CraftDriver.Core.Sync;

namespace CraftDriver.Tests;

[Collection(nameof(AlocacaoTests))]
public class TransferPerformanceTests
{
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    static GraphClient Client(HttpClient http) => new((_, _) => Task.FromResult("fake-token"), http);

    sealed class DownloadFixture : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "transfer-perf-" + Guid.NewGuid().ToString("N"));
        public readonly StateDb Db;
        public readonly SyncEngine Engine;
        public readonly AppConfig Config;
        public readonly HttpClient Http;
        public readonly List<long> Lids = [];
        public DownloadFixture(HttpMessageHandler handler, string config = "{\"DownloadConcurrency\":2}", bool pinned = false, int itemCount = 4)
        {
            Directory.CreateDirectory(Dir);
            Config = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(config)!;
            Config.Paused = true;
            Config.CacheDir = Path.Combine(Dir, "cache");
            Config.Drives = [new DriveConfig { DriveId = "drive", DisplayName = "Test" }];
            Db = new StateDb(Path.Combine(Dir, "state.db"));
            var root = Db.Insert(new Item { DriveId = "drive", ItemId = "root", Name = "", IsDir = true });
            Db.SaveDrive(new DriveState("drive", root, "delta", 0, 0));
            for (var i = 0; i < itemCount; i++)
                Lids.Add(Db.Insert(new Item { DriveId = "drive", ParentLid = root, ItemId = "i" + i, Name = "file" + i, Size = 1, CTag = "v1", Pinned = pinned }));
            Http = new HttpClient(handler);
            var api = Client(Http);
            Engine = new SyncEngine(Config, Db, new ContentCache(Config.CacheDir), new ActivityLog(Path.Combine(Dir, "log")), _ => api);
            Engine.Start();
        }
        public void Dispose()
        {
            Engine.Stop();
            Http.Dispose();
            Db.Dispose();
            try { Directory.Delete(Dir, true); } catch { }
        }
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(40, 32)]
    public async Task Downloads_sob_demanda_respeitam_limite_por_drive(int configured, int expected)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        using var f = new DownloadFixture(new Handler(async (_, ct) =>
        {
            Interlocked.Increment(ref started);
            await release.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([42]) };
        }), $"{{\"DownloadConcurrency\":{configured}}}", itemCount: configured == 40 ? 40 : 4);
        var tasks = f.Lids.Select(l => f.Engine.EnsureContentAsync(l, default)).ToArray();
        try
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.Equal(expected, Volatile.Read(ref started));
        }
        finally { release.TrySetResult(); await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); }
        Assert.All(f.Lids, l => Assert.Equal("v1", f.Db.Get(l)!.CachedCTag));
    }

    [Fact]
    public async Task Fixados_baixam_em_paralelo_sem_exceder_limite()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        using var f = new DownloadFixture(new Handler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref started) == 2) second.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([42]) };
        }), pinned: true);
        // Deixa os loops iniciarem pausados; só acorda o de fixados, sem rede de metadados.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        f.Config.Paused = false;
        f.Engine.GetDrive("drive")!.WakePins();
        try
        {
            await second.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(2, Volatile.Read(ref started));
        }
        finally
        {
            release.TrySetResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (f.Lids.Any(l => f.Db.Get(l)!.CachedCTag != "v1")) await Task.Delay(20, timeout.Token);
        }
    }

    [Fact]
    public async Task Banda_download_e_agregada_por_drive()
    {
        using var f = new DownloadFixture(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([42]) })),
            "{\"DownloadConcurrency\":4,\"DownloadBytesPerSecond\":10}");
        var clock = Stopwatch.StartNew();
        await Task.WhenAll(f.Lids.Select(l => f.Engine.EnsureContentAsync(l, default)));
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(350), $"4 bytes a 10 B/s levaram {clock.Elapsed}");
    }

    /// <summary>§13.5 no download: limites altos entregam ≥ 90 % do configurado (antes saturava em ~13 MiB/s).</summary>
    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    public async Task Banda_download_alta_e_respeitada(int mibPerSecond)
    {
        long rate = mibPerSecond * 1024L * 1024;
        var length = (int)(rate * 3 / 2); // 1,5 s no ritmo pedido
        var body = new byte[length];
        using var f = new DownloadFixture(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) })),
            $"{{\"DownloadConcurrency\":1,\"DownloadBytesPerSecond\":{rate}}}", itemCount: 0);
        var root = f.Db.GetDrive("drive")!.RootLid;
        var lid = f.Db.Insert(new Item { DriveId = "drive", ParentLid = root, ItemId = "grande", Name = "grande.bin", Size = length, CTag = "v1" });
        await f.Engine.EnsureContentAsync(lid, TestContext.Current.CancellationToken);
        Assert.Equal("v1", f.Db.Get(lid)!.CachedCTag);
        // Mede a entrega do transporte + limitador, sem misturar fsync/cache/disputa de disco entre processos da
        // suíte. A integração do cache continua provada acima. Usa a mesma DriveSync.DownloadAsync que o cache usa.
        var download = typeof(DriveSync).GetMethod("DownloadAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        long delivered = 0;
        Action<long> progress = n => delivered = n;
        var clock = Stopwatch.StartNew();
        await (Task)download.Invoke(f.Engine.GetDrive("drive"), ["grande", Stream.Null, 0L, progress, TestContext.Current.CancellationToken])!;
        var measured = length / clock.Elapsed.TotalSeconds / 1024 / 1024;
        Assert.Equal(length, delivered);
        Assert.True(measured >= mibPerSecond * 0.9 && measured <= mibPerSecond * 1.1,
            $"download com limite {mibPerSecond} MiB/s entregou {measured:F1} MiB/s ({measured / mibPerSecond:P0})");
    }

    [Fact]
    public async Task Banda_upload_pequeno_limita_corpo_e_leva_a_data_na_sessao()
    {
        // arquivo pequeno sobe por sessão (POST com a data + PUT), sem PATCH: no OneDrive real o PATCH criava uma versão
        var methods = new List<HttpMethod>();
        var expected = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        byte[]? sent = null;
        using var f = new DownloadFixture(new Handler(async (r, ct) =>
        {
            methods.Add(r.Method);
            if (r.Method == HttpMethod.Put)
            {
                using var destination = new MemoryStream();
                await r.Content!.CopyToAsync(destination, ct);
                sent = destination.ToArray();
            }
            else
            {
                Assert.Contains(expected.UtcDateTime.ToString("o"), await r.Content!.ReadAsStringAsync(ct));
                return Json("{\"uploadUrl\":\"https://upload.example.invalid/pequeno\"}");
            }
            return Json("{\"id\":\"uploaded\",\"name\":\"small\",\"size\":4,\"fileSystemInfo\":{\"lastModifiedDateTime\":\"2020-01-02T03:04:05Z\"}}");
        }), "{\"UploadBytesPerSecond\":10}");
        var file = Path.Combine(f.Dir, "small");
        await File.WriteAllBytesAsync(file, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        var clock = Stopwatch.StartNew();
        var remote = await f.Engine.GetDrive("drive")!.Api.UploadAsync("drive", "root", "small", null, null, file, expected, null, TestContext.Current.CancellationToken);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(350), $"4 bytes a 10 B/s levaram {clock.Elapsed}");
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, sent);
        Assert.Equal(new[] { HttpMethod.Post, HttpMethod.Put }, methods);
        Assert.Equal(expected, remote.LastModified);
    }

    [Fact]
    public async Task Banda_upload_sessao_limita_chunks_sem_alterar_content_range()
    {
        const int length = 5 * 1024 * 1024;
        var received = 0L;
        using var f = new DownloadFixture(new Handler(async (r, ct) =>
        {
            if (r.Method == HttpMethod.Post)
            {
                Assert.Contains("fileSystemInfo", await r.Content!.ReadAsStringAsync(ct));
                return Json("{\"uploadUrl\":\"https://upload.example.invalid/session\"}");
            }
            Assert.Null(r.Headers.Authorization);
            Assert.Equal(received, r.Content!.Headers.ContentRange!.From);
            Assert.Equal(length, r.Content.Headers.ContentRange.Length);
            using var destination = new MemoryStream();
            await r.Content.CopyToAsync(destination, ct);
            received += destination.Length;
            if (received < length)
            {
                var response = Json($"{{\"nextExpectedRanges\":[\"{received}-\"]}}");
                response.StatusCode = HttpStatusCode.Accepted;
                return response;
            }
            return Json("{\"id\":\"uploaded\",\"size\":5242880}");
        }), "{\"UploadBytesPerSecond\":10485760}");
        var file = Path.Combine(f.Dir, "large");
        await File.WriteAllBytesAsync(file, new byte[length], TestContext.Current.CancellationToken);
        var clock = Stopwatch.StartNew();
        await f.Engine.GetDrive("drive")!.Api.UploadAsync("drive", "root", "large", null, null, file, DateTimeOffset.UtcNow, null, TestContext.Current.CancellationToken);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(450), $"5 MiB a 10 MiB/s levaram {clock.Elapsed}");
        Assert.Equal(length, received);
    }

    [Fact]
    public async Task Cancelar_espera_nao_vaza_slot_e_mesmo_item_coalesce()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        using var f = new DownloadFixture(new Handler(async (_, ct) =>
        {
            Interlocked.Increment(ref started);
            await release.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([42]) };
        }), "{\"DownloadConcurrency\":1}");
        var first = f.Engine.EnsureContentAsync(f.Lids[0], TestContext.Current.CancellationToken);
        var duplicate = f.Engine.EnsureContentAsync(f.Lids[0], TestContext.Current.CancellationToken);
        using var canceled = new CancellationTokenSource();
        var queued = f.Engine.EnsureContentAsync(f.Lids[1], canceled.Token);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        release.TrySetResult();
        await Task.WhenAll(first, duplicate).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref started));
        await f.Engine.EnsureContentAsync(f.Lids[1], TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(2, Volatile.Read(ref started));
    }

    [Fact]
    public async Task Cancelar_banda_libera_orcamento_para_proximo_download()
    {
        using var f = new DownloadFixture(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([42]) })),
            "{\"DownloadConcurrency\":1,\"DownloadBytesPerSecond\":10}");
        using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Engine.EnsureContentAsync(f.Lids[0], canceled.Token));
        Assert.Null(f.Db.Get(f.Lids[0])!.CachedCTag);
        await f.Engine.EnsureContentAsync(f.Lids[0], TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal("v1", f.Db.Get(f.Lids[0])!.CachedCTag);
    }

    [Fact]
    public async Task Drives_diferentes_nao_compartilham_slots_de_download()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        using var f = new DownloadFixture(new Handler(async (_, ct) =>
        {
            Interlocked.Increment(ref started);
            await release.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([42]) };
        }), "{\"DownloadConcurrency\":1}");
        var root = f.Db.Insert(new Item { DriveId = "other", ItemId = "root", Name = "", IsDir = true });
        f.Db.SaveDrive(new DriveState("other", root, "delta", 0, 0));
        var lid = f.Db.Insert(new Item { DriveId = "other", ParentLid = root, ItemId = "other-file", Name = "file", Size = 1, CTag = "v1" });
        f.Config.Drives.Add(new DriveConfig { DriveId = "other", DisplayName = "Other" });
        f.Engine.ReloadDrives();
        var first = f.Engine.EnsureContentAsync(f.Lids[0], TestContext.Current.CancellationToken);
        var other = f.Engine.EnsureContentAsync(lid, TestContext.Current.CancellationToken);
        try { Assert.Equal(2, Volatile.Read(ref started)); }
        finally { release.TrySetResult(); await Task.WhenAll(first, other).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task Delta_select_preserva_todos_campos_do_model_e_links_opacos()
    {
        var urls = new List<string>();
        using var http = new HttpClient(new Handler((r, _) =>
        {
            urls.Add(r.RequestUri!.OriginalString);
            return Task.FromResult(Json("{\"value\":[],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/opaque?token=abc%2Bdef\"}"));
        }));
        var api = Client(http);
        var page = await api.GetDeltaAsync("drive", null, TestContext.Current.CancellationToken);
        var query = Uri.UnescapeDataString(new Uri(urls[0]).Query);
        Assert.Contains("$select=", query);
        foreach (var field in new[] { "id", "name", "parentReference", "folder", "root", "deleted", "package", "remoteItem", "size", "eTag", "cTag", "file", "fileSystemInfo", "createdDateTime", "lastModifiedDateTime" })
            Assert.Contains(field, query.Split("$select=")[1].Split(','));
        await api.GetDeltaAsync("drive", page.NextLink, TestContext.Current.CancellationToken);
        Assert.Equal(page.NextLink, urls[1]);
    }
}
