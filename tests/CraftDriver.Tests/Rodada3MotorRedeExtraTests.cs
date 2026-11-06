using System.Diagnostics;
using System.Net;
using System.Text;
using CraftDriver.Core.Auth;
using CraftDriver.Core.Config;
using CraftDriver.Core.Graph;
using CraftDriver.Core.Sync;
using static CraftDriver.Tests.TestSupport;

namespace CraftDriver.Tests;

/// <summary>
/// Rodada 3, frente F2 (rede/config/ciclo de vida): provas extras das correções de §23.4/§23.8/§23.10/§23.13 que os
/// testes RED de <see cref="Rodada3MotorRedeTests"/> não cobrem (timeout de inatividade, carga do config.json,
/// retomada após queda, religar a drive continua sincronizando, renovação de token entre clientes).
/// </summary>
public class Rodada3MotorRedeExtraTests
{
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // ---------- §23.10: prazo do pedido com limite de banda ----------

    [Fact]
    public async Task S23_10_baixar_o_limite_no_meio_do_pedaco_nao_estoura_o_prazo_nem_repete_o_pedaco()
    {
        var puts = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (req, ct) =>
        {
            if (req.Method == HttpMethod.Post)
                return Json("""{"uploadUrl":"https://upload.example/s1"}""");
            Interlocked.Increment(ref puts);
            started.TrySetResult();
            await req.Content!.CopyToAsync(Stream.Null, ct); // consome no ritmo do BandwidthContent
            return Json("""{"id":"f1","name":"f.bin","size":131072}""", HttpStatusCode.Created);
        });
        // prazo de rede curto: com o prazo fixo antigo (prazo + tempo previsto com o limite do início = 1,5 + 1 s) o
        // pedaço, que passa a levar ~6 s depois de o limite baixar, era cancelado e repetido
        var g = new GraphClient((_, _) => Task.FromResult("tk"), new HttpClient(handler)) { NetworkTimeout = TimeSpan.FromSeconds(1.5) };
        var file = Path.Combine(Path.GetTempPath(), "craftdriver-f2-" + Guid.NewGuid().ToString("N")[..8]);
        await File.WriteAllBytesAsync(file, new byte[128 * 1024], Ct);
        try
        {
            g.ConfigureUploadBandwidth("d", 128 * 1024); // 128 KiB/s: 1 s previsto
            var upload = g.UploadAsync("d", "p", "f.bin", null, null, file, DateTimeOffset.UtcNow, null, Ct);
            Assert.True(await started.Task.WithinAsync(5000), "PUT do pedaço não começou");
            await Task.Delay(200, Ct);
            g.ConfigureUploadBandwidth("d", 16 * 1024); // usuário baixa o limite: o resto leva ~6 s, fatias de 1 s
            Assert.True(await upload.WithinAsync(20000), "envio não terminou");
            await upload; // propaga erro (prazo estourado)
            Assert.Equal(1, Volatile.Read(ref puts));
        }
        finally { File.Delete(file); }
    }

    // ---------- §23.8: config.json fora da faixa ----------

    [Fact]
    public void S23_8_config_json_com_tamanhos_e_contagens_fora_da_faixa_e_limitado_ao_carregar()
    {
        var dir = Path.Combine(Path.GetTempPath(), "craftdriver-f2cfg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, """
                { "DeltaBatchSize": 0, "PinBatchSize": -5, "DownloadAttempts": 0, "MaxDownloadResumes": -1,
                  "DeleteQuietMs": -3, "UploadSettleMs": -1, "SharedMaxItems": 0, "SharedPollSeconds": -9,
                  "MassDeleteThreshold": -2, "MassDeleteWindow": "-00:01:00" }
                """);
            var c = AppConfig.Load(path);
            Assert.Equal(1, c.DeltaBatchSize);
            Assert.Equal(1, c.PinBatchSize);
            Assert.Equal(1, c.DownloadAttempts);
            Assert.Equal(0, c.MaxDownloadResumes);
            Assert.Equal(0, c.DeleteQuietMs);
            Assert.Equal(0, c.UploadSettleMs);
            Assert.Equal(1, c.SharedMaxItems);
            Assert.Equal(0, c.SharedPollSeconds);
            Assert.Equal(0, c.MassDeleteThreshold);
            Assert.Equal(TimeSpan.Zero, c.MassDeleteWindow);
            // valores válidos (inclusive 0 onde 0 tem sentido) passam intactos
            var ok = new AppConfig { DeltaBatchSize = 3, PinBatchSize = 2, DownloadAttempts = 5, MaxDownloadResumes = 0, DeleteQuietMs = 0 };
            Assert.Equal((3, 2, 5, 0, 0), (ok.DeltaBatchSize, ok.PinBatchSize, ok.DownloadAttempts, ok.MaxDownloadResumes, ok.DeleteQuietMs));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- §23.13: Retry-After absurdo ----------

    [Fact]
    public void S23_13_Retry_After_absurdo_nao_estoura_o_horario_do_bloqueio()
    {
        var t = new GraphThrottle();
        t.Block(TimeSpan.MaxValue); // (long)(segundos × frequência) estourava e o bloqueio virava 0
        Assert.True(t.Remaining > TimeSpan.FromDays(300), $"Remaining={t.Remaining}");
    }

    [Fact]
    public async Task S23_13_401_em_clientes_diferentes_da_mesma_conta_com_o_mesmo_token_renova_uma_vez_so()
    {
        var cache = new AccessTokenCache();
        var acquires = 0;
        Task<(string, DateTimeOffset)> Acquire(bool force, CancellationToken ct) =>
            Task.FromResult(("T" + Interlocked.Increment(ref acquires), DateTimeOffset.UtcNow.AddHours(1)));
        var t1Arrivals = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewedRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (req, ct) =>
        {
            if (req.Headers.Authorization!.Parameter == "T1")
            {
                var n = Interlocked.Increment(ref t1Arrivals);
                if (n == 2) both.TrySetResult();
                await both.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                if (n == 2) await renewedRetry.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                return Json("""{"error":{"code":"InvalidAuthenticationToken"}}""", HttpStatusCode.Unauthorized);
            }
            renewedRetry.TrySetResult();
            return Json("""{"id":"i","name":"n"}""");
        });
        // um GraphClient por drive da mesma conta (como no AppHost), mesmo cache de token
        GraphClient Client() => new((force, ct) => cache.GetAsync("acc", force, Acquire, ct), new HttpClient(handler));
        var (a, b) = (Client(), Client());
        await Task.WhenAll(a.GetItemAsync("d", "i", Ct), b.GetItemAsync("d", "i", Ct));
        Assert.Equal(2, acquires);
    }

    // ---------- §23.4: ciclo de vida das drives ----------

    [Fact]
    public async Task S23_4_drive_religada_volta_a_sincronizar_depois_que_a_instancia_antiga_termina()
    {
        using var h = new Harness();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.BeforeUpload = async (name, _) =>
        {
            if (name != "a.txt") return;
            first.TrySetResult();
            await release.Task;
        };
        h.WriteFile("a.txt", "conteudo");
        Assert.True(await first.Task.WithinAsync(10000), "envio não começou");
        var old = h.Drive;
        h.Config.Drives[0].Enabled = false;
        h.Engine.ReloadDrives();
        h.Config.Drives[0].Enabled = true;
        h.Engine.ReloadDrives();
        Assert.NotSame(old, h.Drive);
        release.TrySetResult(); // o trabalhador antigo termina; só então a instância nova liga
        Harness.WaitUntil(() => h.Remote.FindByPath("/a.txt") != null && h.Db.PendingOps() == 0, "a.txt enviado");
        h.WriteFile("b.txt", "depois"); // a instância nova está viva: envia o que vier depois
        Harness.WaitUntil(() => h.Remote.FindByPath("/b.txt") != null, "b.txt enviado pela instância nova");
    }

    [Fact]
    public async Task S23_4_remover_e_readicionar_a_biblioteca_com_envio_preso_nao_apaga_a_drive_nova_nem_o_arquivo_na_nuvem()
    {
        using var h = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.BeforeUpload = async (name, _) =>
        {
            if (name != "a.txt") return;
            entered.TrySetResult();
            await release.Task; // resposta a caminho: o trabalhador antigo ainda vai fazer o commit
        };
        h.WriteFile("a.txt", "conteudo");
        Assert.True(await entered.Task.WithinAsync(10000), "envio não começou");
        h.Engine.ForgetDrive(FakeGraph.DriveId); // biblioteca removida com o envio em voo…
        h.Engine.ReloadDrives();                 // …e adicionada de novo (a configuração ainda a tem)
        var fresh = h.Drive;
        await Task.Delay(500, Ct);
        release.TrySetResult(); // o commit tardio do antigo não acha o item: enfileira a exclusão de a.txt na nuvem
        Harness.WaitUntil(() => fresh.LastSync != null, "carga inicial da drive readicionada");
        await Task.Delay(1500, Ct); // tempo para uma limpeza tardia (fora de ordem) apagar a drive nova
        Assert.NotNull(h.Db.GetDrive(FakeGraph.DriveId));
        h.WriteFile("b.txt", "novo");
        Harness.WaitUntil(() => h.Remote.FindByPath("/b.txt") != null, "b.txt enviado pela drive readicionada");
        h.Settle();
        Assert.True(h.Remote.FindByPath("/a.txt") is { Deleted: false }, "a exclusão órfã da drive removida apagou a.txt na nuvem");
        Assert.NotNull(h.Fs.Resolve(h.P("a.txt")));
    }

    [Fact]
    public void S23_4_op_em_execucao_de_queda_anterior_volta_para_a_fila_no_inicio_do_motor()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        h.WriteFile("r.txt", "retomar");
        h.Engine.Stop();
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + h.DbPath))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE ops SET state='running'"; // processo caiu com o envio em andamento
            Assert.True(cmd.ExecuteNonQuery() > 0, "nenhuma op na fila");
        }
        var e2 = new SyncEngine(h.Config, h.Db, new ContentCache(h.Config.CacheDir),
            new ActivityLog(Path.Combine(h.Dir, "log2.txt")), _ => h.Remote);
        try
        {
            h.Config.Paused = false;
            e2.Start();
            Harness.WaitUntil(() => h.Remote.FindByPath("/r.txt") != null, "envio retomado após a queda");
        }
        finally { e2.Stop(); }
    }

    [Fact]
    public async Task S23_4_parar_a_drive_devolve_a_tarefa_dos_lacos()
    {
        using var h = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.BeforeUpload = async (name, _) =>
        {
            if (name != "c.txt") return;
            entered.TrySetResult();
            await release.Task; // ignora o cancelamento: a resposta já está a caminho
        };
        h.WriteFile("c.txt", "x");
        Assert.True(await entered.Task.WithinAsync(10000), "envio não começou");
        var stop = (Task)typeof(DriveSync).GetMethod("Stop", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(h.Drive, null)!;
        await Task.Delay(300, Ct);
        Assert.False(stop.IsCompleted, "Stop terminou com um trabalhador ainda no envio");
        release.TrySetResult();
        Assert.True(await stop.WithinAsync(10000), "laços não terminaram depois de o envio soltar");
    }
}
