using System.Diagnostics;
using System.Net;
using System.Text;
using CraftDriver.Core.Config;
using CraftDriver.Core.Graph;
using CraftDriver.Core.State;
using CraftDriver.Core.Sync;
using CraftDriver.Core.Vfs;
using Microsoft.Data.Sqlite;
using static CraftDriver.Tests.TestSupport;

namespace CraftDriver.Tests;

/// <summary>
/// Reproduções (RED) da §13 do PENDENCIAS.md — comportamento errado do motor. Nenhum destes testes corrige
/// produção: cada um falha hoje pelo motivo descrito no item.
/// </summary>
[Collection(nameof(AlocacaoTests))]
public class Rodada2FilaTests(ITestOutputHelper output)
{
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    static GraphClient Client(HttpMessageHandler h) => new((_, _) => Task.FromResult("tk"), new HttpClient(h));

    static void Sql(Harness h, string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = h.DbPath, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ---------- 13.1 ----------

    [Fact]
    public async Task S13_1_delta_e_restaurar_nao_esperam_upload_lento()
    {
        using var h = new Harness();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.BeforeUpload = async (name, ct) =>
        {
            if (name != "grande.bin") return;
            started.TrySetResult();
            await gate.Task.WaitAsync(ct); // "upload de GBs": segura a transferência
        };
        try
        {
            h.WriteFile("/grande.bin", "conteúdo grande");
            h.Engine.SyncNow();
            Assert.True(await started.Task.WithinAsync(15000), "o envio não começou");

            h.Remote.RemoteCreate("/", "da-nuvem.txt", "novo na nuvem");
            var clock = Stopwatch.StartNew();
            var delta = h.Drive.RunDeltaAsync(Ct);
            var restore = h.Drive.RestoreDeletesAsync(Ct);
            var deltaOk = await delta.WithinAsync(3000);
            var restoreOk = await restore.WithinAsync(100);
            var visible = h.Fs.Resolve(h.P("/da-nuvem.txt")) != null;
            gate.TrySetResult();
            if (!deltaOk) await delta;
            var deltaMs = clock.ElapsedMilliseconds;
            if (!restoreOk) await restore;
            output.WriteLine($"delta terminou {deltaMs} ms depois (só após liberar o envio: {!deltaOk}); restaurar preso: {!restoreOk}");
            Assert.True(deltaOk && restoreOk && visible,
                $"com um envio em andamento: RunDeltaAsync concluiu em 3 s={deltaOk}, RestoreDeletesAsync={restoreOk}, " +
                $"item novo da nuvem visível={visible} — _mutate fica preso durante a transferência inteira");
        }
        finally { gate.TrySetResult(); }
    }

    // ---------- 13.2 ----------

    [Fact]
    public async Task S13_2_erro_423_persistente_nao_pode_travar_a_fila()
    {
        using var h = new Harness();
        h.Remote.UploadError = n => n == "planilha.xlsx"
            ? new GraphException((HttpStatusCode)423, "resourceLocked", "O arquivo está bloqueado (aberto no Excel Online)")
            : null;
        h.WriteFile("/planilha.xlsx", "aberta no Excel Online");
        h.Engine.SyncNow();
        Harness.WaitUntil(() => h.Db.PeekOp(FakeGraph.DriveId) is { Attempts: >= 1 }, "primeira falha 423");
        h.WriteFile("/outro.txt", "atrás na fila");
        // simula horas de tentativas: muitas falhas acumuladas, próxima tentativa já
        Sql(h, "UPDATE ops SET attempts=50, next_try=0 WHERE kind='upload' AND attempts>0");
        h.Drive.WakeOps();
        var sent = await Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 6000)
            {
                if (h.Remote.ReadText("/outro.txt") != null) return true;
                Thread.Sleep(100);
            }
            return false;
        });
        var head = h.Db.PeekOp(FakeGraph.DriveId);
        Assert.True(sent && h.Db.Failures().Count == 1,
            $"após 50 tentativas com 423: outro.txt enviado={sent}, Problemas={h.Db.Failures().Count}, " +
            $"cabeça da fila={head?.Kind} lid {head?.Lid} tentativas {head?.Attempts} (sem teto: a fila fica presa)");
    }

    // ---------- 13.3 ----------

    [Fact]
    public async Task S13_3_descartar_falha_do_mkdir_nao_pode_travar_a_fila()
    {
        using var h = new Harness();
        h.Remote.MaxPathChars = 12;
        Assert.Equal(0, h.Fs.Mkdir(h.P("/pasta-recusada-longa")));
        h.Engine.SyncNow();
        Harness.WaitUntil(() => h.Db.Failures().Count == 1, "mkdir recusado em Problemas");
        h.Db.DismissFailure(h.Db.Failures().Single().Id); // "Descartar aviso"
        h.Remote.MaxPathChars = null;
        h.WriteFile("/pasta-recusada-longa/f.txt", "filho");
        h.WriteFile("/livre.txt", "na raiz, depois na fila");
        h.Engine.SyncNow();
        var sent = await Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 9000)
            {
                if (h.Remote.ReadText("/livre.txt") != null) return true;
                Thread.Sleep(100);
            }
            return false;
        });
        var head = h.Db.PeekOp(FakeGraph.DriveId);
        Assert.True(sent,
            $"livre.txt não foi enviado em 9 s; cabeça da fila: {head?.Kind} '{h.Db.Get(head?.Lid ?? 0)?.Name}' " +
            $"tentativas {head?.Attempts} erro '{head?.LastError}'; Problemas={h.Db.Failures().Count}");
    }

    // ---------- 13.4 ----------

    [Fact]
    public void S13_4_parciais_orfaos_nao_podem_fazer_o_LRU_despejar_tudo()
    {
        using var h = new Harness(c => c.CacheLimitMB = 1);
        var text = new string('x', 40 * 1024);
        h.Remote.RemoteCreate("/", "a.txt", text);
        h.Remote.RemoteCreate("/", "b.txt", text);
        h.Settle();
        Assert.Equal(text, h.ReadFile("/a.txt"));
        Assert.Equal(text, h.ReadFile("/b.txt"));
        var a = h.Fs.Resolve(h.P("/a.txt"))!.Value.Item!;
        var b = h.Fs.Resolve(h.P("/b.txt"))!.Value.Item!;
        // parcial de um item que já não existe (excluído na nuvem no meio do download, conta removida...)
        // 960 KiB = 94 % do limite: com os 80 KiB de arquivos de verdade passa de 1 MiB
        var orphan = h.Engine.Cache.PartialPathFor(FakeGraph.DriveId, 999_999, "c-velho");
        File.WriteAllBytes(orphan, new byte[960 * 1024]);
        h.Engine.EnforceCacheLimit();
        var keptA = h.Db.Get(a.Lid)!.CachedCTag != null;
        var keptB = h.Db.Get(b.Lid)!.CachedCTag != null;
        Assert.True((keptA || keptB) && !File.Exists(orphan),
            $"parcial órfão de 960 KiB (limite 1 MiB, total {h.Engine.Cache.TotalBytes() / 1024} KiB depois): a.txt no cache={keptA}, " +
            $"b.txt no cache={keptB}, órfão apagado={!File.Exists(orphan)} — o LRU despejou TODOS os arquivos de verdade e o órfão ficou");
    }

    [Fact]
    public void S13_4_item_excluido_na_nuvem_deixa_o_parcial_para_tras()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "c.txt", "conteúdo C");
        h.Settle();
        var c = h.Fs.Resolve(h.P("/c.txt"))!.Value.Item!;
        var partial = h.Engine.Cache.PartialPathFor(FakeGraph.DriveId, c.Lid, c.CTag);
        File.WriteAllBytes(partial, new byte[4096]); // download interrompido
        File.WriteAllBytes(partial + ".stream", new byte[4096]);
        h.Remote.RemoteDelete("/c.txt");
        h.Settle();
        Assert.Null(h.Db.Get(c.Lid));
        Assert.False(File.Exists(partial) || File.Exists(partial + ".stream"),
            "o item saiu do banco mas .partial/.stream ficaram no cache para sempre (contam no limite)");
    }

    // ---------- 13.5 ----------

    [Theory]
    [InlineData(10)]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(100)]
    public async Task S13_5_limite_de_banda_alto_e_respeitado(int mibPerSecond)
    {
        long rate = mibPerSecond * 1024L * 1024;
        var length = (int)(rate * 3 / 2); // 1,5 s no ritmo pedido
        var received = 0L;
        var api = Client(new Handler(async (r, ct) =>
        {
            if (r.Method == HttpMethod.Post) return Json("{\"uploadUrl\":\"https://upload.example.invalid/s\"}");
            await r.Content!.CopyToAsync(Stream.Null, ct);
            received = r.Content.Headers.ContentRange!.To!.Value + 1;
            if (received < length)
            {
                var resp = Json($"{{\"nextExpectedRanges\":[\"{received}-\"]}}");
                resp.StatusCode = HttpStatusCode.Accepted;
                return resp;
            }
            return Json($"{{\"id\":\"x\",\"size\":{length}}}");
        }));
        api.ConfigureUploadBandwidth("d", rate);
        var file = Path.Combine(Path.GetTempPath(), "banda-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(file, new byte[length], Ct);
        try
        {
            var clock = Stopwatch.StartNew();
            await api.UploadAsync("d", "root", "x", null, null, file, DateTimeOffset.UtcNow, null, Ct);
            var measured = length / clock.Elapsed.TotalSeconds / 1024 / 1024;
            output.WriteLine($"limite {mibPerSecond} MiB/s → medido {measured:F1} MiB/s ({clock.ElapsedMilliseconds} ms para {length / 1024 / 1024.0:F1} MiB)");
            Assert.True(measured >= mibPerSecond * 0.9,
                $"limite {mibPerSecond} MiB/s entregou só {measured:F1} MiB/s ({measured / mibPerSecond:P0})");
            Assert.True(measured <= mibPerSecond * 1.1, $"limite {mibPerSecond} MiB/s não foi respeitado: {measured:F1} MiB/s");
        }
        finally { File.Delete(file); }
    }

    // ---------- 13.14 ----------

    [Fact]
    public async Task S13_14_throttling_vale_para_a_conta_e_nao_so_para_o_pedido()
    {
        var throttledAt = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        long otherArrived = 0;
        var clock = Stopwatch.StartNew();
        var api = Client(new Handler((r, _) =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path.EndsWith("/items/a"))
            {
                if (!throttledAt.Task.IsCompleted)
                {
                    var resp = Json("{\"error\":{\"code\":\"activityLimitReached\"}}", (HttpStatusCode)429);
                    resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
                    throttledAt.TrySetResult(clock.ElapsedMilliseconds);
                    return Task.FromResult(resp);
                }
                return Task.FromResult(Json("{\"id\":\"a\",\"name\":\"a\"}"));
            }
            Interlocked.CompareExchange(ref otherArrived, clock.ElapsedMilliseconds, 0);
            return Task.FromResult(Json("{\"id\":\"b\",\"name\":\"b\"}"));
        }));
        var first = api.GetItemAsync("d", "a", Ct);
        var at = await throttledAt.Task;
        await Task.Delay(200, Ct);
        await api.GetItemAsync("d", "b", Ct);
        await first;
        Assert.True(otherArrived >= at + 2500,
            $"429 com Retry-After 3 s em {at} ms, mas outro pedido da mesma conta chegou ao servidor em {otherArrived} ms");
    }

    [Fact]
    public async Task S13_14_clientes_da_mesma_conta_compartilham_o_bloqueio_e_outra_conta_nao()
    {
        var clock = Stopwatch.StartNew();
        long throttledAt = -1, sameAccount = 0, otherAccount = 0;
        HttpMessageHandler Server(Action<long> arrived) => new Handler((r, _) =>
        {
            if (r.RequestUri!.AbsolutePath.EndsWith("/items/a") && Interlocked.CompareExchange(ref throttledAt, clock.ElapsedMilliseconds, -1) == -1)
            {
                var resp = Json("{\"error\":{\"code\":\"serviceNotAvailable\"}}", HttpStatusCode.ServiceUnavailable);
                resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
                return Task.FromResult(resp);
            }
            arrived(clock.ElapsedMilliseconds);
            return Task.FromResult(Json("{\"id\":\"x\",\"name\":\"x\"}"));
        });
        var account = new GraphThrottle();
        var driveA = new GraphClient((_, _) => Task.FromResult("tk"), new HttpClient(Server(_ => { })), account);
        var driveB = new GraphClient((_, _) => Task.FromResult("tk"), new HttpClient(Server(t => Interlocked.CompareExchange(ref sameAccount, t, 0))), account);
        var other = new GraphClient((_, _) => Task.FromResult("tk"), new HttpClient(Server(t => Interlocked.CompareExchange(ref otherAccount, t, 0))));
        var first = driveA.GetItemAsync("d", "a", Ct);
        Harness.WaitUntil(() => Interlocked.Read(ref throttledAt) >= 0, "503");
        await Task.Delay(200, Ct); // o cliente A já processou a resposta
        await Task.WhenAll(driveB.GetItemAsync("d2", "b", Ct), other.GetItemAsync("d3", "c", Ct), first);
        var at = Interlocked.Read(ref throttledAt);
        Assert.True(sameAccount >= at + 1700 && otherAccount < at + 1000,
            $"503 com Retry-After 2 s em {at} ms: outra drive da mesma conta chegou em {sameAccount} ms (devia esperar), " +
            $"outra conta em {otherAccount} ms (não devia esperar)");
    }

    [Fact]
    public async Task Download_401_renova_e_503_bloqueia_tambem_pedidos_de_conteudo()
    {
        var tokenCalls = new List<bool>();
        var requests = 0;
        var throttle = new GraphThrottle();
        var api = new GraphClient((force, _) => { tokenCalls.Add(force); return Task.FromResult(force ? "novo" : "velho"); },
            new HttpClient(new Handler((_, _) =>
            {
                var n = Interlocked.Increment(ref requests);
                if (n == 1) return Task.FromResult(Json("{}", HttpStatusCode.Unauthorized));
                if (n == 2)
                {
                    var resp = Json("{}", HttpStatusCode.ServiceUnavailable);
                    resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                    return Task.FromResult(resp);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([42]) });
            })), throttle);
        var clock = Stopwatch.StartNew();
        await api.DownloadRangeAsync("d", "i", Stream.Null, 0, 1, null, Ct);
        Assert.Equal(3, requests);
        Assert.Equal(new[] { false, true, false }, tokenCalls);
        Assert.True(clock.ElapsedMilliseconds >= 900, $"download ignorou o Retry-After: {clock.ElapsedMilliseconds} ms");
    }

    // ---------- §16 token MSAL ----------

    [Fact]
    public async Task S16_token_e_reaproveitado_ate_perto_de_expirar_e_401_renova()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new CraftDriver.Core.Auth.AccessTokenCache { Now = () => now };
        var acquisitions = new List<bool>();
        var lifetime = TimeSpan.FromHours(1);
        Task<(string, DateTimeOffset)> Acquire(bool force, CancellationToken _)
        {
            lock (acquisitions)
            {
                acquisitions.Add(force);
                return Task.FromResult(($"tk{acquisitions.Count}", now + lifetime));
            }
        }
        var rejectOnce = 0;
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
        var api = new GraphClient((force, ct) => cache.GetAsync("conta", force, Acquire, ct), new HttpClient(new Handler((r, _) =>
        {
            var token = r.Headers.Authorization!.Parameter!;
            seen.Add(token);
            if (r.RequestUri!.AbsolutePath.EndsWith("/items/revogado") && token == "tk1" && Interlocked.Exchange(ref rejectOnce, 1) == 0)
                return Task.FromResult(Json("{\"error\":{\"code\":\"InvalidAuthenticationToken\"}}", HttpStatusCode.Unauthorized));
            return Task.FromResult(Json("{\"id\":\"x\",\"name\":\"x\"}"));
        })));

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => api.GetItemAsync("d", "i" + i, Ct)));
        Assert.True(acquisitions.Count == 1, $"20 pedidos (em paralelo) = {acquisitions.Count} aquisições de token (cada uma relê o chaveiro)");

        await api.GetItemAsync("d", "revogado", Ct); // 401 → renova à força e repete
        Assert.Equal([false, true], acquisitions);
        await api.GetItemAsync("d", "depois", Ct);
        Assert.Equal(2, acquisitions.Count);

        now += lifetime - CraftDriver.Core.Auth.AccessTokenCache.RefreshMargin + TimeSpan.FromSeconds(1); // perto de expirar
        await api.GetItemAsync("d", "renovado", Ct);
        Assert.Equal(3, acquisitions.Count);
        Assert.False(acquisitions[2], "renovação por expiração não precisa ser forçada");
    }
}

/// <summary>§16: alocações por transferência (sem outros testes em paralelo: o contador de alocação é do processo).</summary>
[CollectionDefinition(nameof(AlocacaoTests), DisableParallelization = true)]
public class AlocacaoCollection;

[Collection(nameof(AlocacaoTests))]
public class AlocacaoTests(ITestOutputHelper output)
{
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task S16_upload_e_download_nao_alocam_buffer_novo_por_parte()
    {
        const int chunk = 320 * 1024 * 8; // 2,5 MiB
        const int length = chunk * 8;     // 20 MiB = 8 partes
        var block = new byte[64 * 1024];
        var api = new GraphClient((_, _) => Task.FromResult("tk"), new HttpClient(new Handler(async (r, ct) =>
        {
            if (r.Method == HttpMethod.Post) return Json("{\"uploadUrl\":\"https://upload.example.invalid/s\"}");
            if (r.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(block) };
            await r.Content!.CopyToAsync(Stream.Null, ct);
            var end = r.Content.Headers.ContentRange!.To!.Value + 1;
            if (end < length)
            {
                var resp = Json($"{{\"nextExpectedRanges\":[\"{end}-\"]}}");
                resp.StatusCode = HttpStatusCode.Accepted;
                return resp;
            }
            return Json($"{{\"id\":\"x\",\"size\":{length}}}");
        })))
        { ChunkSize = chunk };
        var file = Path.Combine(Path.GetTempPath(), "aloc-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(file, new byte[length], TestContext.Current.CancellationToken);
        try
        {
            async Task Round()
            {
                await api.UploadAsync("d", "root", "x", null, null, file, DateTimeOffset.UtcNow, null, TestContext.Current.CancellationToken);
                for (var i = 0; i < 40; i++)
                    await api.DownloadRangeAsync("d", "i", Stream.Null, 0, block.Length, null, TestContext.Current.CancellationToken);
            }
            await Round(); // aquece (o pool aloca na primeira vez)
            var before = GC.GetTotalAllocatedBytes(true);
            await Round();
            var mib = (GC.GetTotalAllocatedBytes(true) - before) / 1024.0 / 1024;
            output.WriteLine($"alocado numa rodada (8 partes de 2,5 MiB + 40 faixas): {mib:F1} MiB");
            // antes: buffer da parte + cópia por parte (9 × 2,5 MiB) + 256 KiB por faixa (10 MiB) ≈ 32 MiB
            Assert.True(mib < 6, $"envio de 8 partes + 40 faixas alocou {mib:F1} MiB (buffers novos por parte/chamada)");
        }
        finally { File.Delete(file); }
    }
}

