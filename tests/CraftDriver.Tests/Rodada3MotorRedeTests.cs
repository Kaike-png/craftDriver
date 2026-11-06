using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using CraftDriver.Core.Auth;
using CraftDriver.Core.Graph;
using CraftDriver.Core.Sync;
using static CraftDriver.Tests.TestSupport;

namespace CraftDriver.Tests;

/// <summary>
/// Rodada 3, frente B (motor/rede/config): reproduções das §22.4 e §23.4/5/8/9/10/13 do PENDENCIAS.md.
/// Todos FALHAM hoje pelo motivo descrito no item (análise, sem correção de produção).
/// </summary>
public class Rodada3MotorRedeTests
{
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    static HttpResponseMessage Status(int status, int? retryAfterSeconds = null)
    {
        var r = Json("""{"error":{"code":"x","message":"simulado"}}""", (HttpStatusCode)status);
        if (retryAfterSeconds is { } s) r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(s));
        return r;
    }

    static GraphClient Client(HttpMessageHandler h) => new((_, _) => Task.FromResult("tk"), new HttpClient(h));

    static SemaphoreSlim Mutate(DriveSync d) =>
        (SemaphoreSlim)typeof(DriveSync).GetField("_mutate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(d)!;

    // ---------- §22.4: throttling da conta ----------

    [Fact]
    public async Task S22_4_503_em_pedido_nao_idempotente_marca_a_conta_bloqueada()
    {
        var g = Client(new Handler((_, _) => Task.FromResult(Status(503, 120))));
        await Assert.ThrowsAsync<GraphException>(() => g.RestoreVersionAsync("d", "i", "v1", Ct));
        // o servidor pediu 120 s: os outros pedidos da conta deviam esperar
        Assert.True(g.Throttle.Remaining > TimeSpan.FromSeconds(60),
            $"503 com Retry-After 120 em restoreVersion não bloqueou a conta (Remaining={g.Throttle.Remaining})");
    }

    [Fact]
    public async Task S22_4_429_esgotado_marca_a_conta_bloqueada_pelo_ultimo_Retry_After()
    {
        var calls = 0;
        var g = Client(new Handler((_, _) =>
            Task.FromResult(Status(429, Interlocked.Increment(ref calls) <= 5 ? 1 : 120))));
        await Assert.ThrowsAsync<GraphException>(() => g.GetItemAsync("d", "i", Ct));
        Assert.Equal(6, calls);
        Assert.True(g.Throttle.Remaining > TimeSpan.FromSeconds(60),
            $"6º 429 (Retry-After 120) virou exceção sem Throttle.Block (Remaining={g.Throttle.Remaining})");
    }

    // ---------- §23.13: token e Retry-After enorme ----------

    [Fact]
    public async Task S23_13_dois_401_simultaneos_com_o_mesmo_token_renovam_uma_vez_so()
    {
        var cache = new AccessTokenCache();
        var acquires = 0;
        Task<(string, DateTimeOffset)> Acquire(bool force, CancellationToken ct) =>
            Task.FromResult(("T" + Interlocked.Increment(ref acquires), DateTimeOffset.UtcNow.AddHours(1)));
        Assert.Equal("T1", await cache.GetAsync("acc", false, Acquire, Ct));

        var t1Arrivals = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewedRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (req, ct) =>
        {
            var token = req.Headers.Authorization!.Parameter;
            if (token == "T1")
            {
                var n = Interlocked.Increment(ref t1Arrivals);
                if (n == 2) both.TrySetResult();
                await both.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                // o 2º pedido só recebe o 401 depois que o 1º já renovou e repetiu com o token novo
                if (n == 2) await renewedRetry.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                return Status(401);
            }
            renewedRetry.TrySetResult();
            return Json("""{"id":"i","name":"n"}""");
        });
        var g = new GraphClient((force, ct) => cache.GetAsync("acc", force, Acquire, ct), new HttpClient(handler));
        await Task.WhenAll(g.GetItemAsync("d", "i", Ct), g.GetItemAsync("d", "i", Ct));
        // T1 + uma renovação (T2) bastam: os dois falharam com o MESMO token
        Assert.Equal(2, acquires);
    }

    [Fact]
    public async Task S23_13_Retry_After_maior_que_49_dias_espera_e_cancela_sem_lancar_ArgumentOutOfRange()
    {
        // Task.Delay aceita até uint.MaxValue-1 ms (~49,7 dias) no .NET 6+; 24,8 dias (int.MaxValue) era o limite antigo
        var t = new GraphThrottle();
        t.Block(TimeSpan.FromDays(60));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(300);
        var ex = await Record.ExceptionAsync(() => t.WaitAsync(cts.Token));
        Assert.True(ex is OperationCanceledException, $"esperado cancelamento, veio {ex?.GetType().Name}: {ex?.Message}");
    }

    // ---------- §23.10: limite de upload do próximo pedaço ----------

    [Fact]
    public async Task S23_10_novo_limite_de_upload_vale_para_o_pedaco_em_voo()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (req, ct) =>
        {
            if (req.Method == HttpMethod.Post)
                return Json("""{"uploadUrl":"https://upload.example/s1"}""");
            started.TrySetResult();
            await req.Content!.CopyToAsync(Stream.Null, ct); // consome no ritmo do BandwidthContent
            return Json("""{"id":"f1","name":"f.bin","size":131072}""", HttpStatusCode.Created);
        });
        var g = Client(handler);
        var file = Path.Combine(Path.GetTempPath(), "craftdriver-r3b-" + Guid.NewGuid().ToString("N")[..8]);
        await File.WriteAllBytesAsync(file, new byte[128 * 1024], Ct);
        try
        {
            g.ConfigureUploadBandwidth("d", 8 * 1024); // 8 KiB/s: 128 KiB = 16 s
            var sw = Stopwatch.StartNew();
            var upload = g.UploadAsync("d", "p", "f.bin", null, null, file, DateTimeOffset.UtcNow, null, Ct);
            Assert.True(await started.Task.WithinAsync(5000), "PUT do pedaço não começou");
            await Task.Delay(300, Ct);
            g.ConfigureUploadBandwidth("d", 100L * 1024 * 1024); // usuário tira o limite
            // fatia de 16 KiB já agendada (2 s) + o resto quase instantâneo
            var done = await upload.WithinAsync(8000);
            Assert.True(done, $"limite novo não valeu no pedaço em voo: {sw.Elapsed.TotalSeconds:F1} s e ainda enviando a 8 KiB/s");
        }
        finally { File.Delete(file); }
    }

    // ---------- §23.8: configuração sem validação ----------

    [Fact]
    public async Task S23_8_DeltaBatchSize_zero_nao_quebra_o_delta()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        h.Config.DeltaBatchSize = 0; // config.json com 0
        h.Remote.RemoteCreate("/", "novo.txt", "x");
        var ex = await Record.ExceptionAsync(() => h.Drive.RunDeltaAsync(Ct));
        Assert.True(ex == null, $"delta lançou a cada ciclo: {ex}");
        Assert.NotNull(h.Fs.Resolve(h.P("novo.txt")));
    }

    [Fact]
    public void S23_8_PinBatchSize_zero_ainda_baixa_os_fixados()
    {
        using var h = new Harness(c => c.PinBatchSize = 0);
        h.Remote.RemoteCreate("/", "Fotos", null, folder: true);
        h.Remote.RemoteCreate("/Fotos", "1.jpg", "img1");
        h.Settle();
        h.Engine.SetPinned(h.Fs.Resolve(h.P("/Fotos"))!.Value.Item!.Lid, true);
        // LIMIT 0 → lote vazio == PinBatchSize → PinLoop volta na hora (laço quente) e nunca baixa
        Harness.WaitUntil(() => h.Remote.Downloads == 1, "baixar fixado com PinBatchSize=0", 6000);
    }

    // ---------- §23.9: delta por pasta com 403 ----------

    [Fact]
    public async Task S23_9_delta_por_pasta_403_cai_para_children()
    {
        using var h = new Harness();
        var owner = h.Remote.AddOwnerDrive("dono");
        owner.SupportsFolderDelta = false;
        owner.FolderDeltaRefusal = HttpStatusCode.Forbidden;
        owner.RemoteCreate("/", "Projeto", null, folder: true);
        owner.RemoteCreate("/Projeto", "a.txt", "v1");
        owner.Shares[owner.FindByPath("/Projeto")!.Id] = true;
        h.Remote.AddShortcut("/", "Projeto", owner, "/Projeto");
        h.Settle();
        Harness.WaitUntil(() => h.Engine.SharedDrives.Count == 1, "sub-drive");
        var sub = h.Engine.SharedDrives.Single();
        owner.RemoteCreate("/Projeto", "novo.txt", "novo");
        var ex = await Record.ExceptionAsync(() => sub.RunDeltaAsync(Ct));
        Assert.True(ex == null, $"403 do delta por pasta virou erro a cada poll: {ex?.Message}");
        Assert.NotNull(h.Fs.Resolve(h.P("Projeto/novo.txt")));
    }

    // ---------- §23.4: ReloadDrives/ForgetDrive × workers vivos ----------

    [Fact]
    public async Task S23_4_desligar_e_religar_a_drive_nao_envia_o_mesmo_op_duas_vezes()
    {
        using var h = new Harness();
        var entries = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.BeforeUpload = async (name, _) =>
        {
            if (name != "a.txt") return;
            Interlocked.Increment(ref entries);
            first.TrySetResult();
            await release.Task; // resposta a caminho: o cancelamento do worker antigo ainda não chegou aqui
        };
        try
        {
            h.WriteFile("a.txt", "conteudo");
            Assert.True(await first.Task.WithinAsync(10000), "envio não começou");
            h.Config.Drives[0].Enabled = false;
            h.Engine.ReloadDrives();
            h.Config.Drives[0].Enabled = true;
            h.Engine.ReloadDrives(); // novo DriveSync: ResetRunningOps + ClaimOps do op que o antigo ainda executa
            await Task.Delay(3000, Ct);
            Assert.Equal(1, Volatile.Read(ref entries));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task S23_4_ForgetDrive_com_commit_tardio_nao_deixa_op_orfa()
    {
        using var h = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.BeforeUpload = async (name, _) =>
        {
            if (name != "b.txt") return;
            entered.TrySetResult();
            await release.Task;
        };
        var old = h.Drive;
        var mutate = Mutate(old);
        h.WriteFile("b.txt", "conteudo");
        Assert.True(await entered.Task.WithinAsync(10000), "envio não começou");
        await mutate.WaitAsync(Ct); // segura o commit do envio na entrada (_mutate)
        release.TrySetResult();
        Harness.WaitUntil(() => h.Remote.FindByPath("/b.txt") != null, "envio gravado na nuvem");
        await Task.Delay(300, Ct); // worker parado em CommitUploadAsync: _mutate.WaitAsync
        Monitor.Enter(h.Db.Lock);
        try
        {
            mutate.Release();
            Harness.WaitUntil(() => mutate.CurrentCount == 0, "commit pegou _mutate e espera Db.Lock", 5000);
            h.Engine.ForgetDrive(FakeGraph.DriveId); // remoção da biblioteca no meio do commit
        }
        finally { Monitor.Exit(h.Db.Lock); }
        await Task.Delay(500, Ct);
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + h.DbPath);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT kind || ':' || COALESCE(name,'') || ':' || COALESCE(remote_id,'') FROM ops WHERE drive_id=$d";
        cmd.Parameters.AddWithValue("$d", FakeGraph.DriveId);
        var orphans = new List<string>();
        using (var r = cmd.ExecuteReader()) while (r.Read()) orphans.Add(r.GetString(0));
        Assert.True(orphans.Count == 0, "ops órfãs da drive removida: " + string.Join(", ", orphans));
    }

    // ---------- §23.5: Delete de pasta com upload de filho em voo ----------

    static string DumpOps(Harness h)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + h.DbPath);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT kind || '/' || state || '/' || COALESCE(name,'') || '/att=' || attempts FROM ops ORDER BY id";
        var list = new List<string>();
        using (var r = cmd.ExecuteReader()) while (r.Read()) list.Add(r.GetString(0));
        return string.Join(", ", list);
    }

    static (TaskCompletionSource entered, TaskCompletionSource release) HoldUpload(Harness h, string file)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.BeforeUpload = async (name, ct) =>
        {
            if (name != file) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        return (entered, release);
    }

    [Fact]
    public async Task S23_5_rm_rf_de_pasta_com_envio_de_filho_em_voo_nao_espera_o_envio_drenar()
    {
        using var h = new Harness(c => c.DeleteQuietMs = 100);
        h.Remote.RemoteCreate("/", "pasta", null, folder: true);
        h.Settle();
        var (entered, release) = HoldUpload(h, "grande.bin");
        try
        {
            h.WriteFile("pasta/grande.bin", "GBs");
            Assert.True(await entered.Task.WithinAsync(10000), "envio não começou");
            Assert.Equal(0, h.Fs.Unlink(h.P("pasta/grande.bin")));
            Assert.Equal(0, h.Fs.Rmdir(h.P("pasta")));
            h.Engine.NotifyLocalChange(FakeGraph.DriveId);
            var deleted = false;
            try
            {
                Harness.WaitUntil(() => h.Remote.FindByPath("/pasta") is not { Deleted: false }, "pasta", 6000);
                deleted = true;
            }
            catch (TimeoutException) { }
            var ops = DumpOps(h);
            Assert.True(deleted, "exclusão da pasta não roda enquanto o envio do filho (já apagado) segue preso; fila: " + ops);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task S23_5_envio_de_filho_concluido_depois_do_rm_rf_nao_cria_entrada_fantasma_na_Lixeira()
    {
        using var h = new Harness(c => c.DeleteQuietMs = 100);
        h.Remote.RemoteCreate("/", "pasta", null, folder: true);
        h.Settle();
        var (entered, release) = HoldUpload(h, "grande.bin");
        h.WriteFile("pasta/grande.bin", "GBs");
        Assert.True(await entered.Task.WithinAsync(10000), "envio não começou");
        Assert.Equal(0, h.Fs.Unlink(h.P("pasta/grande.bin")));
        Assert.Equal(0, h.Fs.Rmdir(h.P("pasta")));
        release.TrySetResult(); // o envio termina e grava o filho dentro da pasta já apagada localmente
        h.Settle();
        Harness.WaitUntil(() => h.Remote.FindByPath("/pasta") is not { Deleted: false }, "pasta excluída na nuvem");
        h.Settle();
        var trash = h.Db.Trash(FakeGraph.DriveId).Select(t => t.Name).ToList();
        Assert.Contains("pasta", trash);
        Assert.DoesNotContain("grande.bin", trash);
    }
}
