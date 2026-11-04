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
    const long SimpleUploadLimit = 4 * 1024 * 1024;
    const int ChunkSize = 320 * 1024 * 32; // 10 MiB, múltiplo de 320 KiB como exige a API

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
                if (completion == HttpCompletionOption.ResponseContentRead) timeout.CancelAfter(TimeSpan.FromMinutes(10));
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
        var url = link ?? $"{Base}/drives/{Esc(driveId)}/root/delta";
        using var doc = await GetJsonAsync(url, ct);
        var items = new List<RemoteItem>();
        if (doc.RootElement.TryGetProperty("value", out var arr))
            foreach (var e in arr.EnumerateArray())
                items.Add(RemoteItem.FromJson(e));
        return new DeltaPage(items, doc.RootElement.Str("@odata.nextLink"), doc.RootElement.Str("@odata.deltaLink"));
    }

    public async Task DownloadAsync(string driveId, string itemId, Stream destination, Action<long>? progress, CancellationToken ct)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, ItemUrl(driveId, itemId) + "/content"),
            ct, completion: HttpCompletionOption.ResponseHeadersRead);
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        var buf = new byte[256 * 1024];
        long total = 0;
        int n;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (true)
        {
            idle.CancelAfter(TimeSpan.FromSeconds(90)); // conexão travada
            n = await src.ReadAsync(buf, idle.Token);
            if (n == 0) break;
            await destination.WriteAsync(buf.AsMemory(0, n), ct);
            total += n;
            progress?.Invoke(total);
        }
    }

    public async Task<RemoteItem> UploadAsync(string driveId, string parentId, string name, string? itemId, string? ifMatchETag,
        string localFile, DateTimeOffset modified, Action<long>? progress, CancellationToken ct)
    {
        var length = new FileInfo(localFile).Length;
        RemoteItem result;
        if (length <= SimpleUploadLimit)
        {
            var bytes = await File.ReadAllBytesAsync(localFile, ct);
            var url = itemId != null
                ? ItemUrl(driveId, itemId) + "/content"
                : ChildPathUrl(driveId, parentId, name) + "/content?@microsoft.graph.conflictBehavior=fail";
            using var resp = await SendAsync(() =>
            {
                var r = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(bytes) };
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

        // sessão de upload para arquivos grandes
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
        string uploadUrl;
        using (var doc = await SendJsonAsync(HttpMethod.Post, sessionUrl, body, ct, itemId != null ? ifMatchETag : null))
            uploadUrl = doc.RootElement.Str("uploadUrl") ?? throw new GraphException(HttpStatusCode.BadGateway, null, "Sessão de upload sem uploadUrl");

        try
        {
            await using var fs = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[ChunkSize];
            long offset = 0;
            while (true)
            {
                var n = await fs.ReadAtLeastAsync(buf, (int)Math.Min(ChunkSize, length - offset), false, ct);
                var start = offset;
                var chunk = buf.AsMemory(0, n).ToArray();
                using var resp = await SendAsync(() =>
                {
                    var r = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ByteArrayContent(chunk) };
                    r.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + n - 1, length);
                    return r;
                }, ct, auth: false);
                offset += n;
                progress?.Invoke(offset);
                if (resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
                {
                    using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    return RemoteItem.FromJson(doc.RootElement);
                }
                if (offset >= length)
                    throw new GraphException(HttpStatusCode.BadGateway, null, "Upload terminou sem retorno do item");
            }
        }
        catch
        {
            try { using var _ = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, uploadUrl), CancellationToken.None, auth: false, throwOnError: false); }
            catch { /* melhor esforço */ }
            throw;
        }
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
}
