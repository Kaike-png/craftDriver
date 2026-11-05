using System.Text.Json;

namespace CraftDriver.Core.Graph;

/// <summary>Link de compartilhamento devolvido por <c>createLink</c> (permission.link).</summary>
/// <param name="Type">view | edit.</param>
/// <param name="Scope">anonymous | organization | users (o que o servidor efetivamente criou).</param>
public sealed record SharingLink(string WebUrl, string Type, string Scope)
{
    public static SharingLink FromJson(JsonElement e, string requestedType, string requestedScope)
    {
        var link = e.TryGetProperty("link", out var l) ? l : default;
        var url = link.ValueKind == JsonValueKind.Object ? link.Str("webUrl") : null;
        if (string.IsNullOrEmpty(url))
            throw new GraphException(System.Net.HttpStatusCode.BadGateway, null, "createLink respondeu sem link.webUrl");
        return new SharingLink(url, link.Str("type") ?? requestedType, link.Str("scope") ?? requestedScope);
    }
}

/// <summary>Versão anterior de um arquivo (driveItemVersion). A lista vem da mais nova (a atual) para a mais antiga.</summary>
public sealed record DriveItemVersion(string Id, DateTimeOffset Modified, long Size, string? ModifiedBy)
{
    public static DriveItemVersion FromJson(JsonElement e)
    {
        string? who = null;
        if (e.TryGetProperty("lastModifiedBy", out var by))
        {
            if (by.TryGetProperty("user", out var u)) who = u.Str("displayName") ?? u.Str("email");
            else if (by.TryGetProperty("application", out var a)) who = a.Str("displayName");
        }
        return new DriveItemVersion(e.Str("id") ?? "", e.Date("lastModifiedDateTime") ?? DateTimeOffset.MinValue,
            e.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0, who);
    }
}
