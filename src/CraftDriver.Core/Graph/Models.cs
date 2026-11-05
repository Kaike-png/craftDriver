using System.Net;
using System.Text.Json;

namespace CraftDriver.Core.Graph;

public sealed class RemoteItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ParentId { get; set; }
    public bool IsFolder { get; set; }
    public bool IsRoot { get; set; }
    public bool IsDeleted { get; set; }
    /// <summary>Pacotes (ex.: blocos do OneNote) não são arquivos comuns e são ignorados.</summary>
    public bool IsPackage { get; set; }
    /// <summary>Atalho para item de outra drive ("Adicionar a Meus arquivos").</summary>
    public bool IsRemoteShortcut { get; set; }
    /// <summary>Drive dona do item apontado (remoteItem.parentReference.driveId), em atalhos e "Compartilhados comigo".</summary>
    public string? RemoteDriveId { get; set; }
    /// <summary>Id do item apontado na drive dona (remoteItem.id).</summary>
    public string? RemoteId { get; set; }
    /// <summary>O item apontado é pasta (remoteItem.folder).</summary>
    public bool RemoteIsFolder { get; set; }
    public long Size { get; set; }
    public DateTimeOffset LastModified { get; set; }
    public DateTimeOffset Created { get; set; }
    public string? ETag { get; set; }
    public string? CTag { get; set; }
    public string? QuickXorHash { get; set; }

    public static RemoteItem FromJson(JsonElement e)
    {
        var it = new RemoteItem
        {
            Id = e.Str("id") ?? "",
            Name = e.Str("name") ?? "",
            ETag = e.Str("eTag"),
            CTag = e.Str("cTag"),
            Size = e.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0,
            IsFolder = e.TryGetProperty("folder", out _),
            IsRoot = e.TryGetProperty("root", out _),
            IsDeleted = e.TryGetProperty("deleted", out _),
            IsPackage = e.TryGetProperty("package", out _),
            IsRemoteShortcut = e.TryGetProperty("remoteItem", out _),
        };
        if (e.TryGetProperty("parentReference", out var p)) it.ParentId = p.Str("id");
        if (e.TryGetProperty("remoteItem", out var ri) && ri.ValueKind == JsonValueKind.Object)
        {
            it.RemoteId = ri.Str("id");
            if (ri.TryGetProperty("parentReference", out var rp)) it.RemoteDriveId = rp.Str("driveId");
            it.RemoteIsFolder = ri.TryGetProperty("folder", out _) || (it.IsFolder && !ri.TryGetProperty("file", out _));
            if (string.IsNullOrEmpty(it.Name)) it.Name = ri.Str("name") ?? "";
            if (it.Size == 0 && ri.TryGetProperty("size", out var rs) && rs.ValueKind == JsonValueKind.Number) it.Size = rs.GetInt64();
        }
        if (e.TryGetProperty("file", out var f) && f.TryGetProperty("hashes", out var h))
            it.QuickXorHash = h.Str("quickXorHash");
        if (e.TryGetProperty("fileSystemInfo", out var fsi))
        {
            it.LastModified = fsi.Date("lastModifiedDateTime") ?? e.Date("lastModifiedDateTime") ?? DateTimeOffset.UtcNow;
            it.Created = fsi.Date("createdDateTime") ?? e.Date("createdDateTime") ?? it.LastModified;
        }
        else
        {
            it.LastModified = e.Date("lastModifiedDateTime") ?? DateTimeOffset.UtcNow;
            it.Created = e.Date("createdDateTime") ?? it.LastModified;
        }
        return it;
    }
}

public sealed record DeltaPage(IReadOnlyList<RemoteItem> Items, string? NextLink, string? DeltaLink);

public sealed record GraphDrive(string Id, string Name, string DriveType, string? WebUrl, string? OwnerName, long QuotaTotal, long QuotaUsed)
{
    public static GraphDrive FromJson(JsonElement e)
    {
        long total = 0, used = 0;
        if (e.TryGetProperty("quota", out var q))
        {
            total = q.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0;
            used = q.TryGetProperty("used", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt64() : 0;
        }
        string? owner = null;
        if (e.TryGetProperty("owner", out var o))
        {
            if (o.TryGetProperty("user", out var ou)) owner = ou.Str("displayName");
            else if (o.TryGetProperty("group", out var og)) owner = og.Str("displayName");
        }
        return new GraphDrive(e.Str("id") ?? "", e.Str("name") ?? "", e.Str("driveType") ?? "", e.Str("webUrl"), owner, total, used);
    }
}

public sealed record SiteInfo(string Id, string DisplayName, string? WebUrl)
{
    public static SiteInfo FromJson(JsonElement e) =>
        new(e.Str("id") ?? "", e.Str("displayName") ?? e.Str("name") ?? "(sem nome)", e.Str("webUrl"));

    public override string ToString() => DisplayName;
}

public class GraphException : Exception
{
    public HttpStatusCode Status { get; }
    public string? Code { get; }

    public GraphException(HttpStatusCode status, string? code, string message) : base(message)
    {
        Status = status;
        Code = code;
    }

    /// <summary>Erros que valem nova tentativa mais tarde (rede, throttling, servidor, token).</summary>
    public bool IsTransient => (int)Status is 408 or 401 or 423 or 429 or >= 500;
}

static class JsonExt
{
    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static DateTimeOffset? Date(this JsonElement e, string name) =>
        e.Str(name) is { } s && DateTimeOffset.TryParse(s, out var d) ? d : null;
}
