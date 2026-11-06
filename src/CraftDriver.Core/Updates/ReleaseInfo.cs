using System.Text.Json.Serialization;

namespace CraftDriver.Core.Updates;

/// <summary>Resposta de <c>GET /repos/{dono}/{repo}/releases/latest</c> da API do GitHub (só o que o app usa).</summary>
public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = [];
}

public sealed class GitHubAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

/// <summary>Arquivo anexado à release.</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size);

/// <summary>Release publicada (estável), já validada.</summary>
/// <param name="Version">Versão sem o "v" (ex.: 0.3.0).</param>
/// <param name="Notes">Texto da release (markdown do GitHub, sem renderizar).</param>
public sealed record ReleaseInfo(string Version, string Tag, string Name, string Notes, string HtmlUrl, IReadOnlyList<ReleaseAsset> Assets)
{
    public ReleaseAsset? Asset(string name) => Assets.FirstOrDefault(a => a.Name == name);
}

/// <summary>Erro de atualização com mensagem pronta para o usuário (pt-BR).</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);
