using System.Runtime.InteropServices;

namespace CraftDriver.Core.Updates;

public enum UpdateArch { Other, X64, Arm64 }

/// <summary>Qual arquivo da release serve para cada jeito de instalação × arquitetura (nomes do job release do CI).</summary>
public static class UpdateAssets
{
    public const string SumsName = "SHA256SUMS";

    public static UpdateArch CurrentArch => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => UpdateArch.X64,
        Architecture.Arm64 => UpdateArch.Arm64,
        _ => UpdateArch.Other,
    };

    /// <summary>Nome do arquivo esperado; null quando a combinação não tem pacote (ex.: desconhecido).</summary>
    public static string? ExpectedName(InstallKind kind, UpdateArch arch, string version) => (kind, arch) switch
    {
        (_, UpdateArch.Other) => null,
        (InstallKind.Pacman, UpdateArch.X64) => $"craftdriver-bin-{version}-1-x86_64.pkg.tar.zst",
        (InstallKind.Pacman, UpdateArch.Arm64) => $"craftdriver-bin-{version}-1-aarch64.pkg.tar.zst",
        (InstallKind.Deb, _) => $"craftdriver_{version}-1_{(arch == UpdateArch.X64 ? "amd64" : "arm64")}.deb",
        (InstallKind.Rpm, _) => $"craftdriver-{version}-1.{(arch == UpdateArch.X64 ? "x86_64" : "aarch64")}.rpm",
        (InstallKind.UserInstall, _) => $"craftdriver-{version}-linux-{(arch == UpdateArch.X64 ? "x64" : "arm64")}.tar.gz",
        _ => null,
    };

    /// <summary>O arquivo da release para esta instalação; null = sem pacote (cai para "só avisar").</summary>
    public static ReleaseAsset? Choose(ReleaseInfo release, InstallKind kind, UpdateArch arch)
    {
        if (ExpectedName(kind, arch, release.Version) is not { } expected) return null;
        if (release.Asset(expected) is { } exact) return exact;
        // revisão do pacote (pkgrel) diferente de 1: mesmo nome com outro número
        var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(expected).Replace("-1", @"-\d+").Replace("_1", @"_\d+") + "$";
        return release.Assets.FirstOrDefault(a => System.Text.RegularExpressions.Regex.IsMatch(a.Name, pattern));
    }

    /// <summary>Comando sugerido para atualizar à mão quando não há pacote automático.</summary>
    public static string ManualHint(InstallKind kind, ReleaseInfo release)
    {
        var how = kind switch
        {
            InstallKind.Pacman => "Arch: compile o PKGBUILD da release (makepkg -si) ou baixe o pacote e rode \"sudo pacman -U <arquivo>\".",
            InstallKind.Deb => "Debian/Ubuntu: baixe o .deb e rode \"sudo apt install ./<arquivo>.deb\".",
            InstallKind.Rpm => "Fedora/openSUSE: baixe o .rpm e rode \"sudo dnf install ./<arquivo>.rpm\".",
            InstallKind.UserInstall => "Baixe o tarball, extraia e rode \"./install-user.sh\".",
            _ => "Atualize pelo mesmo meio em que instalou (gerenciador de pacotes, tarball ou código-fonte).",
        };
        return $"Baixe em {release.HtmlUrl}\n{how}";
    }
}
