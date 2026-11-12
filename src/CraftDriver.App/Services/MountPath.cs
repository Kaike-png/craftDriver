namespace CraftDriver.App.Services;

/// <summary>
/// Caminho canônico (como o <c>realpath</c>) da pasta sincronizada e de caminhos dentro dela. O kernel registra a
/// montagem pelo caminho real (/proc/mounts), mas a configuração, o gerenciador de arquivos e o terminal podem usar
/// um caminho que passa por link simbólico (<c>~/OneDrive → /data/od</c>): o CLI dizia "fora da pasta sincronizada"
/// e as extensões não reconheciam a pasta.
/// <para>
/// Diferente do <c>realpath</c>, aceita componentes que ainda não existem (a pasta é criada ao montar) e não desce
/// dentro da montagem (<paramref name="stopAt"/>): conferir link a link lá dentro seria uma consulta ao próprio FUSE
/// por componente — e o OneDrive não tem links simbólicos.
/// </para>
/// </summary>
public static class MountPath
{
    const int MaxLinks = 40; // como o ELOOP do kernel

    /// <param name="path">Absoluto ou relativo ao diretório atual.</param>
    /// <param name="stopAt">Caminhos (já canônicos) a partir dos quais o resto é anexado sem conferir links.</param>
    public static string Canonical(string path, params string[] stopAt) => Resolve(Path.GetFullPath(path), stopAt, 0);

    static string Resolve(string full, string[] stopAt, int depth)
    {
        var parts = full.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var cur = "/";
        for (var i = 0; i < parts.Length; i++)
        {
            var next = cur == "/" ? "/" + parts[i] : cur + "/" + parts[i];
            if (Array.IndexOf(stopAt, next) >= 0 || depth > MaxLinks)
                return Join(next, parts, i + 1);
            string? target;
            try
            {
                var info = new FileInfo(next); // lstat: vale para arquivo, pasta e link
                if (!info.Exists && !Directory.Exists(next) && info.LinkTarget == null)
                    return Join(next, parts, i + 1); // daqui em diante não existe: resto como veio
                target = info.LinkTarget;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Join(next, parts, i + 1);
            }
            if (target == null)
            {
                cur = next;
                continue;
            }
            var resolved = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(cur, target));
            cur = Resolve(resolved, stopAt, depth + 1);
            if (Array.IndexOf(stopAt, cur) >= 0) return Join(cur, parts, i + 1);
        }
        return cur;
    }

    static string Join(string head, string[] parts, int from) =>
        from >= parts.Length ? head : (head == "/" ? "" : head) + "/" + string.Join('/', parts[from..]);

    /// <summary>
    /// Caminho relativo à montagem ("/" = raiz) de <paramref name="path"/>, aceitando tanto o caminho configurado
    /// quanto o real (links simbólicos dos dois lados); null = fora dela.
    /// </summary>
    public static string? RelativeToMount(string path, string mountPoint)
    {
        var lexMount = Trim(Path.GetFullPath(mountPoint));
        var full = Trim(Path.GetFullPath(path));
        if (Under(full, lexMount) is { } rel) return rel;
        var canMount = Trim(Canonical(mountPoint));
        if (Under(full, canMount) is { } rel2) return rel2;
        // o caminho dado passa por link (ex.: configuração no caminho real, terminal no link): resolve só até a montagem
        var can = Trim(Canonical(full, lexMount, canMount));
        return Under(can, canMount) ?? Under(can, lexMount);
    }

    static string Trim(string p) => p.Length > 1 ? p.TrimEnd('/') : p;

    static string? Under(string full, string mount) =>
        full == mount ? "/" : full.StartsWith(mount == "/" ? "/" : mount + "/", StringComparison.Ordinal) ? full[(mount == "/" ? 0 : mount.Length)..] : null;

    /// <summary>Aviso (não bloqueia) quando a pasta escolhida passa por link simbólico; null = caminho já é o real.</summary>
    public static string? SymlinkWarning(string mountPoint)
    {
        if (string.IsNullOrWhiteSpace(mountPoint) || !Path.IsPathFullyQualified(mountPoint)) return null;
        var lex = Trim(Path.GetFullPath(mountPoint));
        var can = Trim(Canonical(mountPoint));
        return lex == can ? null
            : $"{lex} passa por um link simbólico e a montagem fica em {can} (o caminho real). O CraftDriver aceita os dois, " +
              $"mas atalhos e extensões de gerenciadores de arquivos que comparam caminhos funcionam melhor com {can}.";
    }
}
