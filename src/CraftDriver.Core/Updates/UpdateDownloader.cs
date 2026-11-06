using System.Security.Cryptography;

namespace CraftDriver.Core.Updates;

public readonly record struct DownloadProgress(long Done, long Total);

/// <summary>Baixa o pacote da release e confere o SHA256 contra o SHA256SUMS da mesma release.</summary>
public static class UpdateDownloader
{
    /// <summary>Hash (minúsculo) do arquivo no conteúdo de um SHA256SUMS ("hash  nome" ou "hash *nome"); null se ausente.</summary>
    public static string? HashFromSums(string sums, string fileName)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            var sp = line.IndexOf(' ');
            if (sp != 64) continue;
            var name = line[sp..].TrimStart(' ', '*');
            var hash = line[..64];
            if (name == fileName && hash.All(Uri.IsHexDigit)) return hash.ToLowerInvariant();
        }
        return null;
    }

    /// <summary>
    /// Baixa <paramref name="asset"/> para <c>{updatesDir}/{versão}/</c> e confere o SHA256. Hash diferente ou ausente:
    /// apaga o arquivo e lança <see cref="UpdateException"/>. Pastas de outras versões são removidas.
    /// </summary>
    public static async Task<string> DownloadAsync(HttpClient http, ReleaseInfo release, ReleaseAsset asset, string updatesDir,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (release.Asset(UpdateAssets.SumsName) is not { } sumsAsset)
            throw new UpdateException($"A release {release.Tag} não tem o arquivo {UpdateAssets.SumsName}: sem ele não dá para conferir o download. Atualize à mão.");
        var dir = Path.Combine(updatesDir, release.Version);
        CleanOtherVersions(updatesDir, release.Version);
        Directory.CreateDirectory(dir);
        var final = Path.Combine(dir, asset.Name);
        var part = final + ".part";
        try
        {
            var sums = await GetStringAsync(http, sumsAsset.Url, ct).ConfigureAwait(false);
            var expected = HashFromSums(sums, asset.Name)
                ?? throw new UpdateException($"O {UpdateAssets.SumsName} da release {release.Tag} não lista {asset.Name}: download não conferido, atualização cancelada.");
            using (var resp = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode)
                    throw new UpdateException($"Falha ao baixar {asset.Name}: o servidor respondeu {(int)resp.StatusCode} ({resp.ReasonPhrase}).");
                var total = resp.Content.Headers.ContentLength ?? asset.Size;
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buf = new byte[81920];
                    long done = 0;
                    var lastReport = 0L;
                    int n;
                    while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        sha.AppendData(buf, 0, n);
                        done += n;
                        if (done - lastReport >= 256 * 1024)
                        {
                            lastReport = done;
                            progress?.Report(new DownloadProgress(done, Math.Max(total, done)));
                        }
                    }
                    progress?.Report(new DownloadProgress(done, Math.Max(total, done)));
                }
                var actual = Convert.ToHexStringLower(sha.GetHashAndReset());
                if (actual != expected)
                    throw new UpdateException($"O SHA256 de {asset.Name} não confere com o {UpdateAssets.SumsName} da release (esperado {expected[..12]}…, " +
                                              $"baixado {actual[..12]}…). O arquivo foi apagado; nada foi instalado.");
            }
            File.Move(part, final, true);
            return final;
        }
        catch (Exception ex)
        {
            TryDelete(part);
            TryDelete(final);
            if (ex is UpdateException or OperationCanceledException) throw;
            throw new UpdateException($"Falha ao baixar {asset.Name}: {ex.Message}", ex);
        }
    }

    static async Task<string> GetStringAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new UpdateException($"Falha ao baixar {UpdateAssets.SumsName}: o servidor respondeu {(int)resp.StatusCode} ({resp.ReasonPhrase}).");
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Downloads de outras versões (já instaladas ou abandonadas) não ficam ocupando o disco.</summary>
    static void CleanOtherVersions(string updatesDir, string keep)
    {
        if (!Directory.Exists(updatesDir)) return;
        foreach (var d in Directory.GetDirectories(updatesDir))
            if (Path.GetFileName(d) != keep)
                try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
