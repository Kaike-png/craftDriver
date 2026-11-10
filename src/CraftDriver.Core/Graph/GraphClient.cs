using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CraftDriver.Core.Graph;

/// <summary>Cliente REST mínimo da Microsoft Graph (v1.0) para OneDrive/SharePoint.</summary>
public sealed class GraphClient : IGraphApi
{
    public const string Base = "https://graph.microsoft.com/v1.0";

    static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true, // downloads redirecionam para URL pré-autenticada; o .NET remove o Authorization no redirect
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    readonly Func<bool, CancellationToken, Task<string>> _token;
    readonly HttpClient _http;
    readonly ConcurrentDictionary<string, TransferBandwidth> _uploadBandwidth = new();

    /// <summary>
    /// Muda o limite de envio da drive. Altera o objeto existente (não troca por outro): o pedaço em voo (até 10 MiB)
    /// segura a referência ao limitador, e o novo limite vale já na próxima fatia de 16 KiB dele.
    /// </summary>
    public void ConfigureUploadBandwidth(string driveId, long bytesPerSecond) =>
        _uploadBandwidth.AddOrUpdate(driveId, _ => new TransferBandwidth(bytesPerSecond), (_, existing) =>
        {
            existing.BytesPerSecond = bytesPerSecond;
            return existing;
        });

    /// <summary>
    /// Sem resposta nem progresso por este tempo, o pedido é abortado (e repetido, se idempotente). Num envio com limite
    /// de banda conta só o tempo usando a rede (o tempo dormindo no limitador não conta). Por instância (testes reduzem).
    /// </summary>
    public TimeSpan NetworkTimeout { get; init; } = TimeSpan.FromMinutes(10);

    HttpContent UploadContent(string driveId, byte[] bytes, int count) =>
        _uploadBandwidth.TryGetValue(driveId, out var bandwidth) && bandwidth.Enabled
            ? new BandwidthContent(bytes, count, bandwidth)
            : new ByteArrayContent(bytes, 0, count);

    /// <summary>Bloqueio por throttling (429/503) da conta; compartilhado entre os clientes das drives da mesma conta.</summary>
    public GraphThrottle Throttle { get; }

    /// <param name="tokenProvider">(forçarRenovação, ct) → access token.</param>
    /// <param name="throttle">Estado de throttling da conta (null = próprio deste cliente).</param>
    public GraphClient(Func<bool, CancellationToken, Task<string>> tokenProvider, HttpClient? http = null, GraphThrottle? throttle = null)
    {
        _token = tokenProvider;
        _http = http ?? SharedHttp;
        Throttle = throttle ?? new GraphThrottle();
    }

    // ---------- infraestrutura ----------

    /// <param name="idempotent">false = pedido que não pode ser repetido às cegas (POST restoreVersion, PUT com
    /// conflictBehavior=fail): depois de uma falha de rede/timeout que pode ter acontecido com o pedido já no servidor,
    /// o erro sobe em vez de reenviar. Falha de conexão/DNS (pedido nunca saiu) continua sendo repetida.</param>
    async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct,
        bool auth = true, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        bool throwOnError = true, bool idempotent = true)
    {
        var refreshed = false;
        string? rejectedToken = null; // token recusado (401): a repetição pede renovação forçada dele
        for (var attempt = 0; ; attempt++)
        {
            await Throttle.WaitAsync(ct); // 429/503 recente em qualquer drive da conta
            using var req = make();
            if (auth)
            {
                string token;
                if (rejectedToken == null) token = await _token(false, ct);
                else
                {
                    // o cache só renova se o token atual da conta ainda for o recusado (401 simultâneos renovam 1 vez)
                    var previous = Auth.AccessTokenCache.RejectedToken.Value;
                    Auth.AccessTokenCache.RejectedToken.Value = rejectedToken;
                    try { token = await _token(true, ct); }
                    finally { Auth.AccessTokenCache.RejectedToken.Value = previous; }
                    rejectedToken = null; // só a repetição após o 401 renova à força; retries de 503 reutilizam o token
                }
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            HttpResponseMessage resp;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (completion == HttpCompletionOption.ResponseContentRead)
                {
                    if (req.Content is BandwidthContent paced)
                    {
                        // Envio com limite de banda: timeout de INATIVIDADE (10 min sem entregar bytes nem receber a
                        // resposta), não um prazo total calculado com o limite do início — o limite pode baixar no
                        // meio do pedaço, e um prazo fixo estouraria e repetiria o pedaço em laço. O tempo dormindo no
                        // limitador não conta.
                        paced.Watchdog = timeout;
                        paced.IdleTimeout = NetworkTimeout;
                    }
                    timeout.CancelAfter(NetworkTimeout);
                }
                resp = await _http.SendAsync(req, completion, timeout.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested && attempt < 3
                                       && (idempotent || NeverReachedServer(ex)))
            {
                await Task.Delay(TimeSpan.FromSeconds(2 << attempt), ct);
                continue;
            }

            if (resp.IsSuccessStatusCode) return resp;

            var status = (int)resp.StatusCode;
            if (status == 401 && auth && !refreshed)
            {
                refreshed = true;
                rejectedToken = req.Headers.Authorization?.Parameter ?? "";
                resp.Dispose();
                continue;
            }
            if (status is 429 or 503 or 502 or 504 or 500)
            {
                var wait = resp.Headers.RetryAfter?.Delta
                           ?? (resp.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : (TimeSpan?)null)
                           ?? TimeSpan.FromSeconds(Math.Min(60, 2 << attempt));
                if (wait < TimeSpan.Zero) wait = TimeSpan.FromSeconds(1);
                // throttling vale para a conta inteira, inclusive quando ESTE pedido não vai repetir (não idempotente,
                // tentativas esgotadas): os outros pedidos da conta esperam o último Retry-After (no início do laço)
                if (status is 429 or 503) Throttle.Block(wait);
                if (attempt < 5 && (idempotent || status == 429))
                {
                    resp.Dispose();
                    if (status is not (429 or 503)) await Task.Delay(wait < GraphThrottle.MaxSingleDelay ? wait : GraphThrottle.MaxSingleDelay, ct);
                    continue;
                }
            }
            if (!throwOnError) return resp; // 404/416 do download/sessão são tratados pelo chamador, depois de 401/throttling
            throw await ToException(resp);
        }
    }

    /// <summary>Falha antes de o pedido sair (conexão recusada, DNS): reenviar não duplica nada.</summary>
    static bool NeverReachedServer(Exception ex) =>
        ex is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError };

    static async Task<GraphException> ToException(HttpResponseMessage resp)
    {
        string? code = null, msg = null;
        try
        {
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                code = err.Str("code");
                msg = err.Str("message");
            }
        }
        catch { /* corpo não-JSON */ }
        var e = new GraphException(resp.StatusCode, code, $"Graph {(int)resp.StatusCode} {code}: {msg ?? resp.ReasonPhrase}");
        resp.Dispose();
        return e;
    }

    async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    async Task<JsonDocument> SendJsonAsync(HttpMethod method, string url, JsonNode body, CancellationToken ct, string? ifMatch = null)
    {
        var text = body.ToJsonString();
        using var resp = await SendAsync(() =>
        {
            var r = new HttpRequestMessage(method, url) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
            if (ifMatch != null) r.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            return r;
        }, ct);
        return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    static string Esc(string s) => Uri.EscapeDataString(s);
    static string ItemUrl(string driveId, string itemId) => $"{Base}/drives/{Esc(driveId)}/items/{Esc(itemId)}";
    static string ChildPathUrl(string driveId, string parentId, string name) => $"{ItemUrl(driveId, parentId)}:/{Esc(name)}:";

    // ---------- IGraphApi ----------

    public async Task<GraphDrive> GetDriveAsync(string driveId, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{Base}/drives/{Esc(driveId)}", ct);
        return GraphDrive.FromJson(doc.RootElement);
    }

    public async Task<RemoteItem> GetRootAsync(string driveId, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{Base}/drives/{Esc(driveId)}/root", ct);
        return RemoteItem.FromJson(doc.RootElement);
    }

    public async Task<RemoteItem?> GetItemAsync(string driveId, string itemId, CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync(ItemUrl(driveId, itemId), ct);
            return RemoteItem.FromJson(doc.RootElement);
        }
        catch (GraphException e) when (e.Status == HttpStatusCode.NotFound) { return null; }
    }

    public async Task<RemoteItem?> GetChildByNameAsync(string driveId, string parentId, string name, CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync(ChildPathUrl(driveId, parentId, name), ct);
            return RemoteItem.FromJson(doc.RootElement);
        }
        catch (GraphException e) when (e.Status == HttpStatusCode.NotFound) { return null; }
    }

    public async Task<DeltaPage> GetDeltaAsync(string driveId, string? link, CancellationToken ct)
    {
        // ItemFields inclui todas as propriedades lidas por RemoteItem.FromJson; links retornados são opacos.
        var url = link ?? $"{Base}/drives/{Esc(driveId)}/root/delta?$select={ItemFields}";
        using var doc = await GetJsonAsync(url, ct);
        var items = new List<RemoteItem>();
        if (doc.RootElement.TryGetProperty("value", out var arr))
            foreach (var e in arr.EnumerateArray())
                items.Add(RemoteItem.FromJson(e));
        return new DeltaPage(items, doc.RootElement.Str("@odata.nextLink"), doc.RootElement.Str("@odata.deltaLink"));
    }

    public async Task DownloadAsync(string driveId, string itemId, Stream destination, long offset, Action<long>? progress, CancellationToken ct)
    {
        var url = ItemUrl(driveId, itemId) + "/content";
        // o Range sobrevive ao redirecionamento para a URL pré-autenticada (o .NET só remove o Authorization)
        using var resp = await SendAsync(() =>
            {
                var r = new HttpRequestMessage(HttpMethod.Get, url);
                if (offset > 0) r.Headers.Range = new RangeHeaderValue(offset, null);
                return r;
            }, ct, completion: HttpCompletionOption.ResponseHeadersRead, throwOnError: false);
        if (offset > 0 && resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // parcial maior/igual ao arquivo (já completo, ou a versão mudou): recomeça do zero
            resp.Dispose();
            await DownloadAsync(driveId, itemId, destination, 0, progress, ct);
            return;
        }
        if (!resp.IsSuccessStatusCode) throw await ToException(resp);

        long total;
        if (offset > 0 && resp.StatusCode == HttpStatusCode.PartialContent)
        {
            destination.Seek(offset, SeekOrigin.Begin);
            total = offset;
        }
        else
        {
            destination.SetLength(0); // servidor ignorou o Range: veio o arquivo inteiro
            destination.Seek(0, SeekOrigin.Begin);
            total = 0;
        }
        var start = total;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        var buf = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            int n;
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            while (true)
            {
                idle.CancelAfter(TimeSpan.FromSeconds(90)); // conexão travada
                n = await src.ReadAsync(buf.AsMemory(0, CopyBufferSize), idle.Token);
                idle.CancelAfter(Timeout.InfiniteTimeSpan); // escrita/limitação de banda não é conexão ociosa
                if (n == 0) break;
                await destination.WriteAsync(buf.AsMemory(0, n), ct);
                total += n;
                progress?.Invoke(total);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
        await destination.FlushAsync(ct);
        // conexão encerrada no meio (sem erro em respostas chunked/HTTP2): não aceitar arquivo truncado
        if (resp.Content.Headers.ContentLength is { } expected && total - start != expected)
            throw new HttpIOException(HttpRequestError.ResponseEnded, $"Download incompleto: {total - start} de {expected} bytes");
    }

    public async Task DownloadRangeAsync(string driveId, string itemId, Stream destination, long offset, long length,
        Action<long>? progress, CancellationToken ct)
    {
        if (length <= 0) return;
        var url = ItemUrl(driveId, itemId) + "/content";
        using var resp = await SendAsync(() =>
            {
                var r = new HttpRequestMessage(HttpMethod.Get, url);
                r.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);
                return r;
            }, ct, completion: HttpCompletionOption.ResponseHeadersRead, throwOnError: false);
        if (!resp.IsSuccessStatusCode) throw await ToException(resp);
        // 200 = o servidor ignorou o Range e mandaria o arquivo inteiro: não serve para um bloco
        if (resp.StatusCode != HttpStatusCode.PartialContent)
            throw new HttpIOException(HttpRequestError.InvalidResponse, $"O servidor ignorou o Range (HTTP {(int)resp.StatusCode})");
        if (resp.Content.Headers.ContentRange is { From: { } from } && from != offset)
            throw new HttpIOException(HttpRequestError.InvalidResponse, $"Faixa recebida começa em {from}, pedida em {offset}");

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        var buf = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long got = 0;
        try
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            while (got < length)
            {
                idle.CancelAfter(TimeSpan.FromSeconds(90));
                var n = await src.ReadAsync(buf.AsMemory(0, (int)Math.Min(CopyBufferSize, length - got)), idle.Token);
                idle.CancelAfter(Timeout.InfiniteTimeSpan);
                if (n == 0) break;
                await destination.WriteAsync(buf.AsMemory(0, n), ct);
                got += n;
                progress?.Invoke(got);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
        await destination.FlushAsync(ct);
        if (got != length)
            throw new HttpIOException(HttpRequestError.ResponseEnded, $"Faixa incompleta: {got} de {length} bytes");
    }

    /// <summary>Buffer de cópia dos downloads (do pool: era um novo de 256 KiB por chamada, um por bloco de streaming).</summary>
    const int CopyBufferSize = 256 * 1024;

    /// <summary>Tamanho das partes da sessão de upload (múltiplo de 320 KiB, como exige a API).</summary>
    public const int DefaultChunkSize = 320 * 1024 * 32; // 10 MiB (múltiplo de 320 KiB, como a Graph exige)

    /// <summary>
    /// Tamanho de cada parte do envio em sessão. Por instância e fixo depois de criada: era estático e mutável, e
    /// testes que o trocavam corrompiam envios de outros testes em paralelo.
    /// </summary>
    public int ChunkSize { get; init; } = DefaultChunkSize;

    public async Task<RemoteItem> UploadAsync(string driveId, string parentId, string name, string? itemId, string? ifMatchETag,
        string localFile, DateTimeOffset modified, Action<long>? progress, CancellationToken ct, IUploadSessionStore? session = null)
    {
        var length = new FileInfo(localFile).Length;
        RemoteItem result;
        // Só o arquivo vazio vai por PUT simples (a sessão não aceita 0 bytes). Os demais sobem por sessão, que leva a
        // data no createUploadSession: no OneDrive real o PUT + PATCH da data criava duas versões por envio. Mesmo
        // número de idas e voltas (POST + PUT contra PUT + PATCH).
        if (length == 0)
        {
            var bytes = await File.ReadAllBytesAsync(localFile, ct);
            var url = itemId != null
                ? ItemUrl(driveId, itemId) + "/content"
                : ChildPathUrl(driveId, parentId, name) + "/content?@microsoft.graph.conflictBehavior=fail";
            HttpResponseMessage resp;
            try
            {
                // arquivo novo (conflictBehavior=fail): reenviar depois de o 1º PUT ter gravado daria 409 (cópia de conflito)
                resp = await SendAsync(() =>
                {
                    var r = new HttpRequestMessage(HttpMethod.Put, url) { Content = UploadContent(driveId, bytes, bytes.Length) };
                    r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    if (itemId != null && ifMatchETag != null) r.Headers.TryAddWithoutValidation("If-Match", ifMatchETag);
                    return r;
                }, ct, idempotent: itemId != null);
            }
            catch (Exception ex) when (itemId == null && ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // a resposta se perdeu: se o arquivo vazio está lá, foi este envio (ou outro idêntico) — adota
                var existing = await GetChildByNameAsync(driveId, parentId, name, ct);
                if (existing is not { IsFolder: false, Size: 0 }) throw;
                result = existing;
                progress?.Invoke(length);
                try { result = await SetModifiedAsync(driveId, result.Id, modified, ct); } catch (GraphException) { }
                return result;
            }
            using (resp)
            {
                using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                result = RemoteItem.FromJson(doc.RootElement);
            }
            progress?.Invoke(length);
            // preserva a data de modificação do Linux
            try { result = await SetModifiedAsync(driveId, result.Id, modified, ct); } catch (GraphException) { }
            return result;
        }

        // sessão de upload para arquivos grandes; retomada a partir de nextExpectedRanges se já existir
        var uploadUrl = session?.Url;
        long offset = 0;
        if (uploadUrl != null)
        {
            if (await NextExpectedOffsetAsync(uploadUrl, ct) is { } next)
            {
                offset = next;
                session!.Resumed(offset);
            }
            else
            {
                uploadUrl = null; // expirou ou já terminou
                session!.Save(null);
            }
        }
        if (uploadUrl == null)
        {
            var sessionUrl = itemId != null
                ? ItemUrl(driveId, itemId) + "/createUploadSession"
                : ChildPathUrl(driveId, parentId, name) + "/createUploadSession";
            var body = new JsonObject
            {
                ["item"] = new JsonObject
                {
                    ["@microsoft.graph.conflictBehavior"] = itemId != null ? "replace" : "fail",
                    ["fileSystemInfo"] = new JsonObject { ["lastModifiedDateTime"] = modified.UtcDateTime.ToString("o") },
                },
            };
            using (var doc = await SendJsonAsync(HttpMethod.Post, sessionUrl, body, ct, itemId != null ? ifMatchETag : null))
                uploadUrl = doc.RootElement.Str("uploadUrl") ?? throw new GraphException(HttpStatusCode.BadGateway, null, "Sessão de upload sem uploadUrl");
            session?.Save(uploadUrl);
        }

        // Um buffer por envio, do pool (era um novo de ChunkSize e mais uma cópia por parte: 20 MiB no LOH a cada
        // 10 MiB enviados). Só volta ao pool no fim do envio, quando nenhum pedido usa mais o conteúdo.
        var chunkSize = ChunkSize; // lido uma vez: o tamanho das partes não muda no meio do envio
        var buf = ArrayPool<byte>.Shared.Rent(chunkSize);
        try
        {
            await using var fs = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            while (true)
            {
                if (offset >= length)
                    throw new GraphException(HttpStatusCode.BadGateway, null, "Upload terminou sem retorno do item");
                fs.Position = offset;
                var n = await fs.ReadAtLeastAsync(buf.AsMemory(0, chunkSize), (int)Math.Min(chunkSize, length - offset), false, ct);
                var start = offset;
                HttpResponseMessage resp;
                try
                {
                    resp = await SendAsync(() =>
                    {
                        var r = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = UploadContent(driveId, buf, n) };
                        r.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + n - 1, length);
                        return r;
                    }, ct, auth: false);
                }
                catch (GraphException g) when (g.Status == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // a parte já tinha chegado (a resposta se perdeu e o reenvio foi recusado): pergunta onde seguir
                    offset = await NextExpectedOffsetAsync(uploadUrl, ct)
                             ?? throw new GraphException(HttpStatusCode.ServiceUnavailable, "uploadSessionLost", "Sessão de upload perdida; recomeçando");
                    session?.Resumed(offset);
                    continue;
                }
                using (resp)
                {
                    if (resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
                    {
                        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                        session?.Save(null);
                        progress?.Invoke(length);
                        return RemoteItem.FromJson(doc.RootElement);
                    }
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    offset = ParseNextExpected(body) ?? start + n;
                }
                progress?.Invoke(offset);
            }
        }
        catch (GraphException g) when (g.Status == HttpStatusCode.NotFound)
        {
            // sessão expirou no servidor: a próxima tentativa cria outra
            session?.Save(null);
            throw new GraphException(HttpStatusCode.ServiceUnavailable, "uploadSessionExpired", "Sessão de upload expirou; recomeçando");
        }
        catch (GraphException g) when (!g.IsTransient)
        {
            // erro permanente: descarta a sessão (rede/5xx/cancelamento mantêm a sessão para retomar)
            try { using var _ = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, uploadUrl), CancellationToken.None, auth: false, throwOnError: false); }
            catch { /* melhor esforço */ }
            session?.Save(null);
            throw;
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    /// <summary>Consulta a sessão de upload: próximo byte esperado, ou null se ela não existe mais.</summary>
    async Task<long?> NextExpectedOffsetAsync(string uploadUrl, CancellationToken ct)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, uploadUrl), ct, auth: false, throwOnError: false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode) throw await ToException(resp);
        return ParseNextExpected(await resp.Content.ReadAsStringAsync(ct)) ?? 0;
    }

    /// <summary>{"nextExpectedRanges":["12345-"]} → 12345 (primeiro intervalo que falta).</summary>
    internal static long? ParseNextExpected(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("nextExpectedRanges", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
            foreach (var r in arr.EnumerateArray())
                if (r.GetString() is { } range && long.TryParse(range.Split('-')[0], out var startAt)) return startAt;
        }
        catch (JsonException) { }
        return null;
    }

    public async Task<RemoteItem> CreateFolderAsync(string driveId, string parentId, string name, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["name"] = name,
            ["folder"] = new JsonObject(),
            ["@microsoft.graph.conflictBehavior"] = "fail",
        };
        try
        {
            using var doc = await SendJsonAsync(HttpMethod.Post, ItemUrl(driveId, parentId) + "/children", body, ct);
            return RemoteItem.FromJson(doc.RootElement);
        }
        catch (GraphException e) when (e.Status == HttpStatusCode.Conflict)
        {
            // já existe: se for pasta, adota
            var existing = await GetChildByNameAsync(driveId, parentId, name, ct);
            if (existing is { IsFolder: true }) return existing;
            throw;
        }
    }

    public Task DeleteAsync(string driveId, string itemId, CancellationToken ct) => DeleteAsync(driveId, itemId, null, ct);

    public async Task DeleteAsync(string driveId, string itemId, string? ifMatchETag, CancellationToken ct)
    {
        try
        {
            using var _ = await SendAsync(() =>
            {
                var r = new HttpRequestMessage(HttpMethod.Delete, ItemUrl(driveId, itemId));
                if (ifMatchETag != null) r.Headers.TryAddWithoutValidation("If-Match", ifMatchETag);
                return r;
            }, ct);
        }
        catch (GraphException e) when (e.Status == HttpStatusCode.NotFound) { }
    }

    public Task<RemoteItem> MoveAsync(string driveId, string itemId, string newParentId, string newName, CancellationToken ct) =>
        MoveAsync(driveId, itemId, newParentId, newName, replace: false, ct);

    public async Task<RemoteItem> MoveAsync(string driveId, string itemId, string newParentId, string newName, bool replace, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["parentReference"] = new JsonObject { ["id"] = newParentId },
            ["name"] = newName,
            // HIPÓTESE: o SharePoint aceita conflictBehavior no PATCH como o OneDrive (documentado só para o OneDrive)
            ["@microsoft.graph.conflictBehavior"] = replace ? "replace" : "fail",
        };
        // Sem If-Match de propósito (§23.7): o eTag de uma pasta muda a cada alteração dos filhos, então mover/renomear
        // pasta com If-Match daria 412 sem conflito real. O 412 nunca ocorre aqui; um Move recusado de vez (400/403)
        // deixa a origem renomeada só localmente e vai para Problemas (o destino que ele substituiria já pode estar na
        // Lixeira, sem menção em Problemas) — aceito e documentado.
        using var doc = await SendJsonAsync(HttpMethod.Patch, ItemUrl(driveId, itemId), body, ct);
        return RemoteItem.FromJson(doc.RootElement);
    }

    public async Task<RemoteItem> SetModifiedAsync(string driveId, string itemId, DateTimeOffset modified, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["fileSystemInfo"] = new JsonObject { ["lastModifiedDateTime"] = modified.UtcDateTime.ToString("o") },
        };
        using var doc = await SendJsonAsync(HttpMethod.Patch, ItemUrl(driveId, itemId), body, ct);
        return RemoteItem.FromJson(doc.RootElement);
    }

    // ---------- descoberta (contas / SharePoint) ----------

    public async Task<GraphDrive> GetMyDriveAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{Base}/me/drive", ct);
        return GraphDrive.FromJson(doc.RootElement);
    }

    async Task<List<T>> GetPagedAsync<T>(string url, Func<JsonElement, T> map, CancellationToken ct, int max = 500)
    {
        var list = new List<T>();
        string? next = url;
        while (next != null && list.Count < max)
        {
            using var doc = await GetJsonAsync(next, ct);
            if (doc.RootElement.TryGetProperty("value", out var arr))
                foreach (var e in arr.EnumerateArray()) list.Add(map(e));
            next = doc.RootElement.Str("@odata.nextLink");
        }
        return list;
    }

    public Task<List<SiteInfo>> GetFollowedSitesAsync(CancellationToken ct) =>
        GetPagedAsync($"{Base}/me/followedSites", SiteInfo.FromJson, ct);

    public Task<List<SiteInfo>> SearchSitesAsync(string query, CancellationToken ct) =>
        GetPagedAsync($"{Base}/sites?search={Esc(string.IsNullOrWhiteSpace(query) ? "*" : query)}", SiteInfo.FromJson, ct, 200);

    public async Task<SiteInfo> GetRootSiteAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{Base}/sites/root", ct);
        return SiteInfo.FromJson(doc.RootElement);
    }

    /// <summary>Aceita URL como https://empresa.sharepoint.com/sites/Financeiro.</summary>
    public async Task<SiteInfo?> GetSiteByUrlAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return null;
        var path = u.AbsolutePath.TrimEnd('/');
        var addr = string.IsNullOrEmpty(path) ? $"{Base}/sites/{u.Host}" : $"{Base}/sites/{u.Host}:{path}";
        try
        {
            using var doc = await GetJsonAsync(addr, ct);
            return SiteInfo.FromJson(doc.RootElement);
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) { return null; }
    }

    public Task<List<GraphDrive>> GetSiteDrivesAsync(string siteId, CancellationToken ct) =>
        GetPagedAsync($"{Base}/sites/{Esc(siteId)}/drives", GraphDrive.FromJson, ct);

    // ---------- compartilhados comigo / atalhos ----------

    /// <summary>Campos lidos por <see cref="RemoteItem.FromJson"/> (inclui remoteItem).</summary>
    const string ItemFields = "id,name,parentReference,folder,root,deleted,package,remoteItem,size,eTag,cTag,file,fileSystemInfo,createdDateTime,lastModifiedDateTime";

    public Task<List<RemoteItem>> GetSharedWithMeAsync(CancellationToken ct) =>
        GetPagedAsync($"{Base}/me/drive/sharedWithMe", RemoteItem.FromJson, ct, 2000);

    public async Task<DeltaPage> GetChildrenAsync(string driveId, string itemId, string? next, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(next ?? $"{ItemUrl(driveId, itemId)}/children?$select={ItemFields}&$top=1000", ct);
        var items = new List<RemoteItem>();
        if (doc.RootElement.TryGetProperty("value", out var arr))
            foreach (var e in arr.EnumerateArray())
                items.Add(RemoteItem.FromJson(e));
        return new DeltaPage(items, doc.RootElement.Str("@odata.nextLink"), null);
    }

    public async Task<bool> CanWriteAsync(string driveId, string itemId, CancellationToken ct)
    {
        try
        {
            // paginado: a permissão que dá escrita pode vir depois da 1ª página
            var perms = await GetPagedAsync($"{ItemUrl(driveId, itemId)}/permissions", PermissionCanWrite, ct, 2000);
            return perms.Any(x => x);
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return false; // sem acesso à lista de permissões: trata como somente leitura
        }
    }

    /// <summary>
    /// Papéis que permitem alterar: write/owner (OneDrive) e, em bibliotecas do SharePoint, sp.owner/sp.member e
    /// fullcontrol (HIPÓTESE: nomes como a Graph os devolveria; não visto em conta real). sp.visitor/read = só leitura.
    /// </summary>
    static bool PermissionCanWrite(JsonElement p)
    {
        if (!p.TryGetProperty("roles", out var roles) || roles.ValueKind != JsonValueKind.Array) return false;
        foreach (var r in roles.EnumerateArray())
            if (r.GetString()?.ToLowerInvariant() is "write" or "owner" or "sp.owner" or "sp.member" or "fullcontrol") return true;
        return false;
    }

    // ---------- compartilhamento, versões e Lixeira ----------

    public async Task<SharingLink> CreateLinkAsync(string driveId, string itemId, string type, string scope, CancellationToken ct)
    {
        var body = new JsonObject { ["type"] = type, ["scope"] = scope };
        using var doc = await SendJsonAsync(HttpMethod.Post, ItemUrl(driveId, itemId) + "/createLink", body, ct);
        return SharingLink.FromJson(doc.RootElement, type, scope);
    }

    public async Task<IReadOnlyList<DriveItemVersion>> ListVersionsAsync(string driveId, string itemId, CancellationToken ct) =>
        await GetPagedAsync(ItemUrl(driveId, itemId) + "/versions", DriveItemVersion.FromJson, ct);

    public async Task RestoreVersionAsync(string driveId, string itemId, string versionId, CancellationToken ct)
    {
        // sem corpo (a documentação pede Content-Type application/json): 204 No Content
        // não idempotente: cada POST que chega cria uma versão nova — falha depois de enviar sobe para o usuário decidir
        using var _ = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{ItemUrl(driveId, itemId)}/versions/{Esc(versionId)}/restoreVersion")
        {
            Content = new StringContent("", Encoding.UTF8, "application/json"),
        }, ct, idempotent: false);
    }

    public async Task DownloadVersionAsync(string driveId, string itemId, string versionId, Stream destination, CancellationToken ct)
    {
        // 302 para URL pré-autenticada; o .NET segue e remove o Authorization
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{ItemUrl(driveId, itemId)}/versions/{Esc(versionId)}/content"),
            ct, completion: HttpCompletionOption.ResponseHeadersRead);
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        var before = destination.CanSeek ? destination.Position : 0;
        await src.CopyToAsync(destination, 256 * 1024, ct);
        await destination.FlushAsync(ct);
        if (destination.CanSeek && resp.Content.Headers.ContentLength is { } expected && destination.Position - before != expected)
            throw new HttpIOException(HttpRequestError.ResponseEnded, $"Download incompleto: {destination.Position - before} de {expected} bytes");
    }

    public async Task<RemoteItem> RestoreDeletedAsync(string driveId, string itemId, CancellationToken ct)
    {
        // Documentado na v1.0 só como /me/drive/items/{id}/restore e só para o OneDrive pessoal.
        using var doc = await SendJsonAsync(HttpMethod.Post, $"{Base}/me/drive/items/{Esc(itemId)}/restore", new JsonObject(), ct);
        return RemoteItem.FromJson(doc.RootElement);
    }

    public async Task<DeltaPage> GetFolderDeltaAsync(string driveId, string itemId, string? link, CancellationToken ct)
    {
        // HIPÓTESE (não verificado na Graph real): /items/{id}/delta numa pasta compartilhada de OneDrive pessoal de outro
        // usuário. Contas corporativas/SharePoint respondem 400/404; o motor cai para a listagem por children.
        var url = link ?? $"{ItemUrl(driveId, itemId)}/delta?$select={ItemFields}";
        using var doc = await GetJsonAsync(url, ct);
        var items = new List<RemoteItem>();
        if (doc.RootElement.TryGetProperty("value", out var arr))
            foreach (var e in arr.EnumerateArray())
                items.Add(RemoteItem.FromJson(e));
        return new DeltaPage(items, doc.RootElement.Str("@odata.nextLink"), doc.RootElement.Str("@odata.deltaLink"));
    }
}
