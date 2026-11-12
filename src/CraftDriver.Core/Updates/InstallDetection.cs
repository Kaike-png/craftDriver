using System.ComponentModel;
using System.Diagnostics;

namespace CraftDriver.Core.Updates;

/// <summary>Como esta cópia do CraftDriver foi instalada (define o pacote a baixar e o instalador).</summary>
public enum InstallKind
{
    /// <summary>dotnet run, tarball solto, pacote de terceiros: só avisa, com o link da release.</summary>
    Unknown,
    /// <summary>Arch e derivados (pacote craftdriver-bin).</summary>
    Pacman,
    /// <summary>Debian/Ubuntu (.deb).</summary>
    Deb,
    /// <summary>Fedora/openSUSE (.rpm).</summary>
    Rpm,
    /// <summary>packaging/install-user.sh: ~/.local/opt/craftdriver, sem sudo.</summary>
    UserInstall,
}

/// <param name="Package">Nome do pacote dono do executável (pacman/dpkg/rpm).</param>
public sealed record InstallInfo(InstallKind Kind, string? Package, string? ExePath)
{
    public string Label => Kind switch
    {
        InstallKind.Pacman => $"pacote do pacman ({Package})",
        InstallKind.Deb => $"pacote .deb ({Package})",
        InstallKind.Rpm => $"pacote .rpm ({Package})",
        InstallKind.UserInstall => "instalação do usuário (~/.local/opt/craftdriver)",
        _ => "não identificada (execução de desenvolvimento ou tarball solto)",
    };
}

public sealed record CommandResult(int ExitCode, string StdOut, string StdErr)
{
    /// <summary>Código usado quando o programa não existe (como o shell).</summary>
    public const int NotFound = 127;
}

/// <summary>Executa programas externos (pacman, dpkg, pkexec…). Troque nos testes.</summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct = default);
    /// <summary>O programa está no PATH (ou o caminho existe e é executável)?</summary>
    bool Exists(string program);
}

/// <summary>Executor real: processo filho com saída capturada; programa ausente = código 127.</summary>
public sealed class ProcessRunner : ICommandRunner
{
    public static readonly ProcessRunner Instance = new();

    public async Task<CommandResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // mensagens dos gerenciadores de pacote em inglês: a saída é interpretada aqui
        psi.Environment["LC_ALL"] = "C";
        Process p;
        try { p = Process.Start(psi)!; }
        catch (Win32Exception ex) { return new CommandResult(CommandResult.NotFound, "", ex.Message); }
        using (p)
        {
            p.StandardInput.Close();
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            try { await p.WaitForExitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                throw;
            }
            return new CommandResult(p.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
    }

    public bool Exists(string program) => FindInPath(program) != null;

    public static string? FindInPath(string program)
    {
        if (program.Contains('/')) return File.Exists(program) ? program : null;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(dir, program);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}

/// <summary>Descobre o <see cref="InstallKind"/> pelo caminho do executável e pelo dono do arquivo.</summary>
public static class InstallDetector
{
    /// <param name="exePath">Executável em execução (Environment.ProcessPath); "dotnet" = desenvolvimento.</param>
    /// <param name="home">Pasta pessoal (~/.local/opt/craftdriver = install-user.sh).</param>
    public static async Task<InstallInfo> DetectAsync(string? exePath, string home, ICommandRunner run, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(exePath) || Path.GetFileNameWithoutExtension(exePath) == "dotnet")
            return new InstallInfo(InstallKind.Unknown, null, exePath);
        var exe = exePath;
        try
        {
            // /usr/bin/craftdriver é link para /usr/lib/craftdriver/craftdriver: o dono é o arquivo de verdade
            if (File.ResolveLinkTarget(exePath, true) is { } target) exe = target.FullName;
        }
        catch (IOException) { }
        var userDir = Path.Combine(home, ".local", "opt", "craftdriver") + "/";
        if (exe.StartsWith(userDir, StringComparison.Ordinal) || exePath.StartsWith(userDir, StringComparison.Ordinal))
            return new InstallInfo(InstallKind.UserInstall, null, exe);

        // pacman -Qqo: só o nome do pacote; dpkg -S: "pacote: caminho"; rpm -qf com --qf: só o nome
        var pac = await run.RunAsync("pacman", ["-Qqo", exe], ct).ConfigureAwait(false);
        if (pac.ExitCode == 0 && FirstLine(pac.StdOut) is { } pkg)
            return new InstallInfo(pkg == "craftdriver-bin" ? InstallKind.Pacman : InstallKind.Unknown, pkg, exe);
        var dpkg = await run.RunAsync("dpkg", ["-S", exe], ct).ConfigureAwait(false);
        if (dpkg.ExitCode == 0 && FirstLine(dpkg.StdOut) is { } line && line.IndexOf(':') is > 0 and var colon)
        {
            var name = line[..colon].Trim();
            var arch = name.IndexOf(':'); // craftdriver:amd64 (multiarch)
            if (arch > 0) name = name[..arch];
            return new InstallInfo(name == "craftdriver" ? InstallKind.Deb : InstallKind.Unknown, name, exe);
        }
        var rpm = await run.RunAsync("rpm", ["-qf", "--qf", "%{NAME}", exe], ct).ConfigureAwait(false);
        if (rpm.ExitCode == 0 && FirstLine(rpm.StdOut) is { } rpmName && !rpmName.Contains(' '))
            return new InstallInfo(rpmName == "craftdriver" ? InstallKind.Rpm : InstallKind.Unknown, rpmName, exe);
        return new InstallInfo(InstallKind.Unknown, null, exe);
    }

    static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
