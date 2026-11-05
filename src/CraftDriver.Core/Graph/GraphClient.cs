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

    public void ConfigureUploadBandwidth(string driveId, long bytesPerSecond) =>
        _uploadBandwidth[driveId] = new TransferBandwidth(bytesPerSecond);

    HttpContent UploadContent(string driveId, byte[] bytes) =>
        _uploadBandwidth.TryGetValue(driveId, out var bandwidth) && bandwidth.Enabled
            ? new BandwidthContent(bytes, bandwidth)
            : new ByteArrayContent(bytes);

    /// <param name="tokenProvider">(forçarRenovação, ct) → access token.</param>
    public GraphClient(Func<bool, CancellationToken, Task<string>> tokenProvider, HttpClient? http = null)
    {
        _token = tokenProvider;
        _http = http ?? SharedHttp;
    }

    // ---------- infraestrutura ----------

    async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct,
        bool auth = true, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        bool throwOnError = true)
    {
        var refreshed = false;
        for (var attempt = 0; ; attempt++)
        {
            using var req = make();
            if (auth)
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _token(refreshed, ct));

            HttpResponseMessage resp;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (completion == HttpCompletionOption.ResponseContentRead)
                {
                    // O tempo deliberadamente gasto no rate limit não consome os 10 min de rede.
                    var milliseconds = 600_000 + (req.Content is BandwidthContent paced ? paced.PacingSeconds * 1000 : 0);
                    if (milliseconds < uint.MaxValue - 1) timeout.CancelAfter(TimeSpan.FromMilliseconds(milliseconds));
                    // Limites extremos (>49 dias por pedido) ficam só com o cancelamento do chamador.
                }
                resp = await _http.SendAsync(req, completion, timeout.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 << attempt), ct);
                continue;
            }

            if (resp.IsSuccessStatusCode || !throwOnError) return resp;

            var status = (int)resp.StatusCode;
            if (status == 401 && auth && !refreshed)
            {
                refreshed = true;
                resp.Dispose();
                continue;
            }
            if ((status is 429 or 503 or 502 or 504 or 500) && attempt < 5)
            {
                var wait = resp.Headers.RetryAfter?.Delta
                           ?? (resp.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : (TimeSpan?)null)
                           ?? TimeSpan.FromSeconds(Math.Min(60, 2 << attempt));
                if (wait < TimeSpan.Zero) wait = TimeSpan.FromSeconds(1);
                resp.Dispose();
                await Task.Delay(wait, ct);
                continue;
            }
            throw await ToException(resp);
        }
    }

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
        // Inclui todas as propriedades lidas por RemoteItem.FromJson; links retornados são opacos.
        const string fields = "id,name,parentReference,folder,root,deleted,package,remoteItem,size,eTag,cTag,file,fileSystemInfo,createdDateTime,lastModifiedDateTime";
        var url = link ?? $"{Base}/drives/{Esc(driveId)}/root/delta?$select={fields}";
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
        var buf = new byte[256 * 1024];
        int n;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (true)
        {
            idle.CancelAfter(TimeSpan.FromSeconds(90)); // conexão travada
            n = await src.ReadAsync(buf, idle.Token);
            idle.CancelAfter(Timeout.InfiniteTimeSpan); // escrita/limitação de banda não é conexão ociosa
            if (n == 0) break;
            await destination.WriteAsync(buf.AsMemory(0, n), ct);
            total += n;
            progress?.Invoke(total);
        }
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
        var buf = new byte[256 * 1024];
        long got = 0;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (got < length)
        {
            idle.CancelAfter(TimeSpan.FromSeconds(90));
            var n = await src.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, length - got)), idle.Token);
            idle.CancelAfter(Timeout.InfiniteTimeSpan);
            if (n == 0) break;
            await destination.WriteAsync(buf.AsMemory(0, n), ct);
            got += n;
            progress?.Invoke(got);
        }
        await destination.FlushAsync(ct);
        if (got != length)
            throw new HttpIOException(HttpRequestError.ResponseEnded, $"Faixa incompleta: {got} de {length} bytes");
    }

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
            using var resp = await SendAsync(() =>
            {
                var r = new HttpRequestMessage(HttpMethod.Put, url) { Content = UploadContent(driveId, bytes) };
                r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                if (itemId != null && ifMatchETag != null) r.Headers.TryAddWithoutValidation("If-Match", ifMatchETag);
                return r;
            }, ct);
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            result = RemoteItem.FromJson(doc.RootElement);
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
            if (await NextExpectedOffsetAsync(uploadUrl, ct) is { } next) offset = next;
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

        try
        {
            await using var fs = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[ChunkSize]; // lido uma vez: mudar ChunkSize durante o envio não pode passar do buffer
            while (true)
            {
                if (offset >= length)
                    throw new GraphException(HttpStatusCode.BadGateway, null, "Upload terminou sem retorno do item");
                fs.Position = offset;
                var n = await fs.ReadAtLeastAsync(buf, (int)Math.Min(buf.Length, length - offset), false, ct);
                var start = offset;
                var chunk = buf.AsMemory(0, n).ToArray();
                HttpResponseMessage resp;
                try
                {
                    resp = await SendAsync(() =>
                    {
                        var r = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = UploadContent(driveId, chunk) };
                        r.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + n - 1, length);
                        return r;
                    }, ct, auth: false);
                }
                catch (GraphException g) when (g.Status == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // a parte já tinha chegado (a resposta se perdeu e o reenvio foi recusado): pergunta onde seguir
                    offset = await NextExpectedOffsetAsync(uploadUrl, ct)
                             ?? throw new GraphException(HttpStatusCode.ServiceUnavailable, "uploadSessionLost", "Sessão de upload perdida; recomeçando");
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

    public async Task DeleteAsync(string driveId, string itemId, CancellationToken ct)
    {
        try
        {
            using var _ = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, ItemUrl(driveId, itemId)), ct);
        }
        catch (GraphException e) when (e.Status == HttpStatusCode.NotFound) { }
    }

    public async Task<RemoteItem> MoveAsync(string driveId, string itemId, string newParentId, string newName, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["parentReference"] = new JsonObject { ["id"] = newParentId },
            ["name"] = newName,
            ["@microsoft.graph.conflictBehavior"] = "replace",
        };
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
            using var doc = await GetJsonAsync($"{ItemUrl(driveId, itemId)}/permissions", ct);
            return ParseCanWrite(doc.RootElement);
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return false; // sem acesso à lista de permissões: trata como somente leitura
        }
    }

    /// <summary>Alguma permissão com papel write/owner (para quem não é dono vêm só as que valem para ele).</summary>
    internal static bool ParseCanWrite(JsonElement root)
    {
        if (!root.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array) return false;
        foreach (var p in arr.EnumerateArray())
            if (p.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array)
                foreach (var r in roles.EnumerateArray())
                    if (r.GetString() is "write" or "owner") return true;
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
        using var _ = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{ItemUrl(driveId, itemId)}/versions/{Esc(versionId)}/restoreVersion")
        {
            Content = new StringContent("", Encoding.UTF8, "application/json"),
        }, ct);
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
}
