using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CraftDriver.Core.Config;

namespace CraftDriver.Core.Updates;

/// <summary>Consulta da última release no GitHub.</summary>
public static class UpdateCheck
{
    public const string DefaultApiBase = "https://api.github.com";
    public const string Repository = "Kaike-png/craftDriver";
    /// <summary>Troca a base da API (só para testes: servidor HTTP falso local).</summary>
    public const string ApiBaseVariable = "CRAFTDRIVER_UPDATE_URL";
    /// <summary>"1" desliga a verificação automática (testes, pacotes de distribuição que se atualizam pelo sistema).</summary>
    public const string DisableVariable = "CRAFTDRIVER_NO_UPDATE_CHECK";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static string ApiBase(Func<string, string?> env) =>
        env(ApiBaseVariable) is { Length: > 0 } b ? b.TrimEnd('/') : DefaultApiBase;

    public static string LatestUrl(string apiBase) => $"{apiBase.TrimEnd('/')}/repos/{Repository}/releases/latest";

    /// <summary>JSON da API → release; null quando é rascunho, pré-lançamento ou a tag não é uma versão.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        GitHubRelease? r;
        try { r = JsonSerializer.Deserialize(json, UpdateJsonContext.Default.GitHubRelease); }
        catch (JsonException ex) { throw new UpdateException("O GitHub devolveu uma resposta que não é a release esperada.", ex); }
        if (r == null || r.Draft || r.Prerelease || SemVer.IsPrerelease(r.TagName) || SemVer.Core(r.TagName) is not { } v) return null;
        var version = $"{v.Major}.{v.Minor}.{v.Build}";
        return new ReleaseInfo(version, r.TagName, string.IsNullOrWhiteSpace(r.Name) ? "CraftDriver " + r.TagName : r.Name!,
            r.Body ?? "", r.HtmlUrl ?? $"https://github.com/{Repository}/releases/tag/{r.TagName}",
            r.Assets.Where(a => a.Name.Length > 0 && a.BrowserDownloadUrl.Length > 0)
                .Select(a => new ReleaseAsset(a.Name, a.BrowserDownloadUrl, a.Size)).ToList());
    }

    /// <summary>
    /// Busca a última release. Lança <see cref="UpdateException"/> (rede, HTTP, JSON); null = não há release estável.
    /// </summary>
    public static async Task<ReleaseInfo?> FetchLatestAsync(HttpClient http, string apiBase, string currentVersion, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, LatestUrl(apiBase));
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("CraftDriver", currentVersion));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        string json;
        try
        {
            using var resp = await http.SendAsync(req, timeout.Token).ConfigureAwait(false);
            // 404 = o repositório ainda não tem release publicada
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            if (!resp.IsSuccessStatusCode)
                throw new UpdateException($"O GitHub respondeu {(int)resp.StatusCode} ({resp.ReasonPhrase}) ao procurar atualizações.");
            json = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) { throw new UpdateException("Sem conexão com o GitHub para procurar atualizações: " + ex.Message, ex); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException("O GitHub não respondeu a tempo ao procurar atualizações.", ex);
        }
        return Parse(json);
    }

    /// <summary>
    /// Resumo das notas em texto simples: tira cabeçalhos, negrito, código e links do markdown; para antes da
    /// tabela "Instalar" (acrescentada pelo CI) e corta em <paramref name="maxLines"/> linhas.
    /// </summary>
    public static string Summarize(string markdown, int maxLines = 12)
    {
        var lines = new List<string>();
        var truncated = false;
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith('#') && line.Contains("Instalar", StringComparison.OrdinalIgnoreCase)) break;
            if (line.TrimStart().StartsWith('|')) continue; // tabelas
            line = System.Text.RegularExpressions.Regex.Replace(line, @"^\s*#+\s*", "");
            line = System.Text.RegularExpressions.Regex.Replace(line, @"!?\[([^\]]*)\]\([^)]*\)", "$1");
            line = line.Replace("**", "").Replace("__", "").Replace("`", "");
            line = System.Text.RegularExpressions.Regex.Replace(line, @"^(\s*)[-*] ", "$1• ");
            if (line.Length == 0 && (lines.Count == 0 || lines[^1].Length == 0)) continue;
            if (lines.Count == maxLines)
            {
                truncated = true;
                break;
            }
            lines.Add(line);
        }
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var text = string.Join('\n', lines);
        return truncated ? text + "\n…" : text;
    }
}
