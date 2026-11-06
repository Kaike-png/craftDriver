namespace CraftDriver.Core.Updates;

/// <summary>
/// Comparação de versões no formato das tags do projeto (vX.Y.Z). Sufixos de pré-lançamento (-rc1, -beta) e de
/// compilação (+abc123) são descartados da versão ATUAL (build local "0.3.0-dev+abc" vale como 0.3.0); uma release
/// com sufixo de pré-lançamento nunca é oferecida (<see cref="UpdateCheck.Parse"/>).
/// </summary>
public static class SemVer
{
    /// <summary>X.Y.Z (sem "v", sufixos descartados); null se não for uma versão.</summary>
    public static Version? Core(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        var cut = t.IndexOfAny(['-', '+']);
        if (cut >= 0) t = t[..cut];
        var parts = t.Split('.');
        if (parts.Length is < 1 or > 3) return null;
        var n = new int[3];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out n[i]))
                return null;
        return new Version(n[0], n[1], n[2]);
    }

    /// <summary>A tag/versão <paramref name="candidate"/> é mais nova que <paramref name="current"/>?</summary>
    public static bool IsNewer(string candidate, string current) =>
        Core(candidate) is { } c && Core(current) is { } cur && c > cur;

    /// <summary>Tem sufixo de pré-lançamento ("1.0.0-rc1")? O sufixo de compilação (+...) não conta.</summary>
    public static bool IsPrerelease(string text)
    {
        var plus = text.IndexOf('+');
        return (plus >= 0 ? text[..plus] : text).Contains('-');
    }
}
