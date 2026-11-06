using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CraftDriver.Core.Config;
using CraftDriver.Core.Graph;
using CraftDriver.Core.Ipc;
using CraftDriver.Core.State;
using CraftDriver.Core.Sync;
using CraftDriver.Core.Vfs;

namespace CraftDriver.Tests;

/// <summary>Regressões da seção 2 do PENDENCIAS.md (comportamento errado) sem montagem.</summary>
public class BehaviorTests
{
    static Item Get(Harness h, string rel) => h.Fs.Resolve(h.P(rel))!.Value.Item!;

    [Fact]
    public void Chmod_fica_guardado_e_sobrevive_a_atualizacao_remota()
    {
        using var h = new Harness();
        h.WriteFile("/script.sh", "#!/bin/sh");
        h.Settle();
        Assert.Equal(0, h.Fs.Chmod(h.P("/script.sh"), 0x1ED /*0755*/));
        Assert.Equal(0, h.Fs.GetAttr(h.P("/script.sh"), out var a));
        Assert.Equal(0x1EDu, a.Mode & 0xFFF);
        h.Remote.RemoteUpdate("/script.sh", "#!/bin/sh\necho novo");
        h.Settle();
        Assert.Equal(0, h.Fs.GetAttr(h.P("/script.sh"), out a));
        Assert.Equal(0x1EDu, a.Mode & 0xFFF);
        // chmod 000 explícito não vira "padrão"; voltar ao padrão zera
        Assert.Equal(0, h.Fs.Chmod(h.P("/script.sh"), 0));
        h.Fs.GetAttr(h.P("/script.sh"), out a);
        Assert.Equal(VirtualFs.ExplicitMode, a.Mode);
        Assert.Equal(0, h.Fs.Chmod(h.P("/script.sh"), 0x1A4));
        Assert.Equal(0u, Get(h, "/script.sh").Mode);
    }

    [Fact]
    public void Processo_bloqueado_e_offline_tem_erros_diferentes()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "foto.jpg", "img");
        h.Settle();
        h.Config.BlockedProcesses = [System.Diagnostics.Process.GetCurrentProcess().ProcessName];
        Assert.Equal(-Errno.EACCES, h.Fs.Open(h.P("/foto.jpg"), 0, Environment.ProcessId, out _));
        h.Remote.OfflineMode = true;
        Assert.Equal(-Errno.EHOSTUNREACH, h.Fs.Open(h.P("/foto.jpg"), 0, 0, out _));
    }

    [Fact]
    public void Disco_local_cheio_nao_e_sem_conexao()
    {
        var full = new IOException("No space left on device", 28);
        Assert.True(Errors.IsDiskFull(full));
        Assert.True(Errors.IsLocalDiskError(full));
        Assert.False(Errors.IsNetworkError(full, TestContext.Current.CancellationToken));
        Assert.Equal(Errno.ENOSPC, Errors.ErrnoForDownload(full));
        var net = new HttpIOException(HttpRequestError.ResponseEnded, "caiu");
        Assert.False(Errors.IsLocalDiskError(net));
        Assert.True(Errors.IsNetworkError(net, TestContext.Current.CancellationToken));
        Assert.Equal(Errno.EHOSTUNREACH, Errors.ErrnoForDownload(new HttpRequestException("offline")));
        Assert.True(Errors.IsNetworkError(new IOException("reset", new SocketException(104)), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Download_que_cai_no_meio_e_retomado_com_range()
    {
        using var h = new Harness();
        var text = new string('x', 10_000) + "fim";
        h.Remote.RemoteCreate("/", "grande.txt", text);
        h.Settle();
        h.Remote.DropDownloadAfter = 4000;
        Assert.Equal(text, h.ReadFile("/grande.txt"));
        Assert.Equal([0L, 4000L], h.Remote.DownloadOffsets);
    }

    [Fact]
    public void Download_parcial_sobrevive_a_falha_e_continua_depois()
    {
        using var h = new Harness();
        var text = new string('y', 10_000);
        h.Remote.RemoteCreate("/", "g.txt", text);
        h.Settle();
        h.Engine.MaxDownloadResumes = 0; // simula queda sem retomada imediata (ex.: app fechado)
        try
        {
            h.Remote.DropDownloadAfter = 2500;
            Assert.Equal(-Errno.EHOSTUNREACH, h.Fs.Open(h.P("/g.txt"), 0, 0, out _));
        }
        finally { h.Engine.MaxDownloadResumes = 5; }
        Assert.Equal(text, h.ReadFile("/g.txt"));
        Assert.Equal([0L, 2500L], h.Remote.DownloadOffsets);
        Assert.Empty(Directory.EnumerateFiles(h.Engine.Cache.PartialDir));
    }

    [Fact]
    public void Envio_que_cai_retoma_com_a_mesma_copia_e_sessao()
    {
        using var h = new Harness();
        h.Remote.FailUploadAfterSession = "https://upload.exemplo/sessao-1";
        h.WriteFile("/doc.txt", "conteúdo");
        Harness.WaitUntil(() => h.Remote.UploadSessionsSeen.Count >= 1, "primeira tentativa");
        Harness.WaitUntil(() => h.Db.PeekOp(FakeGraph.DriveId) is { Attempts: > 0 }, "falha registrada");
        var op = h.Db.PeekOp(FakeGraph.DriveId)!;
        Assert.Equal("https://upload.exemplo/sessao-1", op.UploadUrl);
        Assert.True(File.Exists(op.UploadSnapshot), "a cópia do envio foi apagada");

        h.Engine.Cache.CleanStale(h.Db.UploadSnapshots()); // como num reinício: a cópia em uso fica
        Assert.True(File.Exists(op.UploadSnapshot));
        h.Settle();
        Assert.Equal("conteúdo", h.Remote.ReadText("/doc.txt"));
        Assert.Equal("https://upload.exemplo/sessao-1", h.Remote.UploadSessionsSeen.Last());
        Assert.False(File.Exists(op.UploadSnapshot));
    }

    [Fact]
    public void Itens_fixados_alem_do_lote_nao_esperam_5_minutos()
    {
        using var h = new Harness();
        h.Drive.PinBatchSize = 2;
        try
        {
            h.Remote.RemoteCreate("/", "Fotos", null, folder: true);
            for (var i = 0; i < 5; i++) h.Remote.RemoteCreate("/Fotos", $"{i}.jpg", "img" + i);
            h.Settle();
            h.Engine.SetPinned(Get(h, "/Fotos").Lid, true);
            Harness.WaitUntil(() => h.Remote.Downloads == 5, "baixar os 5 fixados", 10_000);
        }
        finally { h.Drive.PinBatchSize = 500; }
    }

    [Fact]
    public void Config_invalida_avisa_e_guarda_o_original()
    {
        var dir = Path.Combine(Path.GetTempPath(), "craftdriver-cfg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, "{ \"Accounts\": [ { \"Id\": \"a\" ");
            var cfg = AppConfig.Load(path);
            Assert.NotNull(cfg.LoadWarning);
            Assert.Contains("inválida", cfg.LoadWarning);
            var backup = Assert.Single(Directory.GetFiles(dir, "config.json.corrompido-*"));
            Assert.Equal(File.ReadAllText(path), File.ReadAllText(backup));
            Assert.Null(AppConfig.Load(Path.Combine(dir, "nao-existe.json")).LoadWarning);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Drives_com_o_mesmo_nome_ficam_acessiveis()
    {
        var dir = Path.Combine(Path.GetTempPath(), "craftdriver-cfg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.json");
            new AppConfig
            {
                Drives =
                [
                    new DriveConfig { DriveId = "1", DisplayName = "Equipe/Docs" },
                    new DriveConfig { DriveId = "2", DisplayName = "Equipe-Docs" },
                    new DriveConfig { DriveId = "3", DisplayName = "equipe-docs" },
                    new DriveConfig { DriveId = "4", DisplayName = ".." },
                ],
            }.Save(path);
            var cfg = AppConfig.Load(path);
            Assert.Equal(["Equipe-Docs", "Equipe-Docs (2)", "equipe-docs (3)", "Drive"], cfg.Drives.Select(d => d.DisplayName));
            Assert.Contains("ajustados", cfg.LoadWarning);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Ipc_com_json_malformado_responde_erro()
    {
        var sock = Path.Combine(Path.GetTempPath(), "cd-ipc-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        using var server = new IpcServer(sock, req => Task.FromResult(new IpcResponse(true, "ok " + req.Cmd)));
        foreach (var bad in new[] { "isto não é json", "{}", "null" })
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await s.ConnectAsync(new UnixDomainSocketEndPoint(sock), TestContext.Current.CancellationToken);
            await using var stream = new NetworkStream(s, true);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(bad + "\n"), TestContext.Current.CancellationToken);
            var line = await new StreamReader(stream).ReadLineAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(line);
            var resp = JsonSerializer.Deserialize<IpcResponse>(line)!;
            Assert.False(resp.Ok);
            Assert.Contains("inválido", resp.Text);
        }
        Assert.Equal("ok ping", (await IpcClient.SendAsync(sock, "ping", []))!.Text);
    }
}

/// <summary>Protocolo de upload/download da Graph contra um servidor HTTP falso (sessões, Range, 416).</summary>
[Collection(UploadResumeTests.ChunkSizeCollection)]
public class GraphProtocolTests
{
    /// <summary>Servidor falso com sessão de upload que guarda partes; pode "perder" a resposta de uma parte.</summary>
    sealed class FakeServer : HttpMessageHandler
    {
        public byte[] Stored = [];
        public long Received;
        public long TotalLength = -1;
        public int SessionsCreated;
        public bool SessionGone;
        public long? LoseResponseAtOffset; // grava a parte mas responde 500
        public readonly List<string> Log = [];
        public byte[] DownloadContent = [];
        public long? TruncateDownloadAt;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            if (url.EndsWith("/createUploadSession"))
            {
                SessionsCreated++;
                Received = 0;
                SessionGone = false;
                Log.Add("create");
                return Json(HttpStatusCode.OK, """{"uploadUrl":"https://upload.exemplo/s"}""");
            }
            if (url == "https://upload.exemplo/s" && req.Method == HttpMethod.Get)
            {
                Log.Add("status " + Received);
                return SessionGone ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(HttpStatusCode.OK, $$"""{"nextExpectedRanges":["{{Received}}-"]}""");
            }
            if (url == "https://upload.exemplo/s" && req.Method == HttpMethod.Put)
            {
                var range = req.Content!.Headers.ContentRange!;
                var data = await req.Content.ReadAsByteArrayAsync(ct);
                Log.Add($"put {range.From}");
                if (SessionGone) return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (range.From != Received) return Json(HttpStatusCode.RequestedRangeNotSatisfiable, """{"error":{"code":"invalidRange"}}""");
                TotalLength = range.Length!.Value;
                if (Stored.Length < TotalLength) Array.Resize(ref Stored, (int)TotalLength);
                data.CopyTo(Stored, range.From!.Value);
                Received = range.To!.Value + 1;
                if (LoseResponseAtOffset == range.From) { LoseResponseAtOffset = null; return new HttpResponseMessage(HttpStatusCode.InternalServerError); }
                return Received == TotalLength
                    ? Json(HttpStatusCode.Created, """{"id":"novo","name":"f.bin","size":1,"file":{}}""")
                    : Json(HttpStatusCode.Accepted, $$"""{"nextExpectedRanges":["{{Received}}-"]}""");
            }
            if (url.EndsWith("/content") && req.Method == HttpMethod.Get)
            {
                var from = req.Headers.Range?.Ranges.First().From ?? 0;
                if (!IgnoreRange && req.Headers.Range?.Ranges.First().To is { } to)
                {
                    // faixa com fim (leitura por faixas): 206 só com esses bytes
                    Log.Add($"get {from}-{to}");
                    var part = DownloadContent[(int)from..(int)(to + 1)];
                    if (TruncateDownloadAt is { } c) { TruncateDownloadAt = null; part = part[..(int)c]; }
                    var r206 = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(part) };
                    r206.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, DownloadContent.Length);
                    return r206;
                }
                if (IgnoreRange) from = 0;
                Log.Add("get " + from);
                var body = DownloadContent[(int)from..];
                if (TruncateDownloadAt is { } cut) { TruncateDownloadAt = null; body = body[..(int)cut]; }
                var resp = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(body),
                };
                resp.Content.Headers.ContentLength = DownloadContent.Length - from; // anuncia o tamanho certo
                return resp;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        /// <summary>Servidor que ignora Range e manda sempre o arquivo inteiro (200).</summary>
        public bool IgnoreRange;

        static HttpResponseMessage Json(HttpStatusCode code, string json) =>
            new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    sealed class Store(string? url = null) : IUploadSessionStore
    {
        public string? Url { get; private set; } = url;
        public void Save(string? u) => Url = u;
    }

    static (GraphClient client, FakeServer server, string file, byte[] data) Setup(int size)
    {
        var server = new FakeServer();
        var client = new GraphClient((_, _) => Task.FromResult("token"), new HttpClient(server)) { ChunkSize = Chunk };
        var data = new byte[size];
        new Random(1).NextBytes(data);
        var file = Path.Combine(Path.GetTempPath(), "cd-up-" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllBytes(file, data);
        return (client, server, file, data);
    }

    const int Chunk = 320 * 1024;

    [Fact]
    public async Task Parte_reenviada_apos_resposta_perdida_consulta_a_sessao_em_vez_de_falhar()
    {
        var (client, server, file, data) = Setup(5 * 1024 * 1024);
        try
        {
            server.LoseResponseAtOffset = Chunk * 3; // servidor gravou, mas respondeu 500 → reenvio recebe 416
            var r = await client.UploadAsync("d", "p", "f.bin", null, null, file, DateTimeOffset.UtcNow, null, CancellationToken.None, new Store());
            Assert.Equal("novo", r.Id);
            Assert.Equal(data, server.Stored);
            Assert.Contains("status " + Chunk * 4, server.Log);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Sessao_guardada_retoma_de_onde_parou()
    {
        var (client, server, file, data) = Setup(5 * 1024 * 1024);
        try
        {
            // primeira execução: cai depois de 6 partes (o processo "morre")
            var store = new Store();
            using var cts = new CancellationTokenSource();
            var progress = new Action<long>(n => { if (n >= Chunk * 6) cts.Cancel(); });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                client.UploadAsync("d", "p", "f.bin", null, null, file, DateTimeOffset.UtcNow, progress, cts.Token, store));
            Assert.Equal("https://upload.exemplo/s", store.Url);
            var sentBefore = server.Received;

            // segunda execução com a URL guardada: não cria sessão nova nem reenvia o que já foi
            server.Log.Clear();
            var r = await client.UploadAsync("d", "p", "f.bin", null, null, file, DateTimeOffset.UtcNow, null, CancellationToken.None, store);
            Assert.Equal("novo", r.Id);
            Assert.Equal(1, server.SessionsCreated);
            Assert.Equal("put " + sentBefore, server.Log.First(l => l.StartsWith("put")));
            Assert.Equal(data, server.Stored);
            Assert.Null(store.Url);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Sessao_expirada_recomeca_com_sessao_nova()
    {
        var (client, server, file, data) = Setup(5 * 1024 * 1024);
        try
        {
            server.SessionGone = true;
            var r = await client.UploadAsync("d", "p", "f.bin", null, null, file, DateTimeOffset.UtcNow, null, CancellationToken.None,
                new Store("https://upload.exemplo/s"));
            Assert.Equal("novo", r.Id);
            Assert.Equal(1, server.SessionsCreated);
            Assert.Equal(data, server.Stored);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Download_truncado_e_rejeitado_e_continua_com_range()
    {
        var server = new FakeServer { DownloadContent = Encoding.UTF8.GetBytes(new string('z', 5000) + "FIM") };
        var client = new GraphClient((_, _) => Task.FromResult("token"), new HttpClient(server));
        using var ms = new MemoryStream();
        server.TruncateDownloadAt = 1200;
        await Assert.ThrowsAnyAsync<IOException>(() => client.DownloadAsync("d", "i", ms, 0, null, CancellationToken.None));
        Assert.Equal(1200, ms.Length);
        await client.DownloadAsync("d", "i", ms, ms.Length, null, CancellationToken.None);
        Assert.Equal(server.DownloadContent, ms.ToArray());
        Assert.Equal(["get 0", "get 1200"], server.Log);
    }

    [Fact]
    public async Task Download_de_faixa_pede_Range_com_fim_e_rejeita_servidor_que_ignora()
    {
        var content = new byte[10_000];
        new Random(3).NextBytes(content);
        var server = new FakeServer { DownloadContent = content };
        var client = new GraphClient((_, _) => Task.FromResult("token"), new HttpClient(server));

        using var ms = new MemoryStream();
        long progress = 0;
        await client.DownloadRangeAsync("d", "i", ms, 4000, 1500, n => progress = n, CancellationToken.None);
        Assert.Equal(content[4000..5500], ms.ToArray());
        Assert.Equal(1500, progress);
        Assert.Equal(["get 4000-5499"], server.Log);

        // conexão caiu no meio da faixa: erro, não bloco curto
        server.TruncateDownloadAt = 700;
        using var cut = new MemoryStream();
        await Assert.ThrowsAnyAsync<IOException>(() => client.DownloadRangeAsync("d", "i", cut, 0, 1500, null, CancellationToken.None));

        // servidor que ignora Range mandaria o arquivo inteiro: nada gravado
        server.IgnoreRange = true;
        using var whole = new MemoryStream();
        await Assert.ThrowsAnyAsync<IOException>(() => client.DownloadRangeAsync("d", "i", whole, 4000, 1500, null, CancellationToken.None));
        Assert.Equal(0, whole.Length);
    }
}
