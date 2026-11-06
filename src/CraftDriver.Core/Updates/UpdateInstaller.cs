namespace CraftDriver.Core.Updates;

/// <summary>Comando a executar (programa + argumentos).</summary>
public sealed record InstallCommand(string File, IReadOnlyList<string> Args)
{
    /// <summary>Para mostrar/colar no terminal (argumentos com aspas simples quando preciso).</summary>
    public string Display => string.Join(' ', new[] { File }.Concat(Args).Select(Quote));

    public static string Quote(string s) =>
        s.Length > 0 && s.All(c => char.IsLetterOrDigit(c) || "-_./=:+@%,".Contains(c)) ? s : "'" + s.Replace("'", "'\\''") + "'";
}

/// <summary>Monta o comando de instalação de um pacote baixado.</summary>
public static class UpdateInstaller
{
    /// <summary>
    /// Comando para instalar o pacote com elevação (<paramref name="elevate"/> = pkexec ou sudo), escolhendo o
    /// gerenciador presente (deb: apt-get, senão dpkg; rpm: dnf, zypper, senão rpm). Null para install-user/desconhecido
    /// ou quando nenhum gerenciador existe.
    /// </summary>
    public static InstallCommand? PackageCommand(InstallKind kind, string file, Func<string, bool> exists, string elevate = "pkexec")
    {
        var abs = Path.GetFullPath(file);
        // sudo no terminal mostra as perguntas do gerenciador (sem --noconfirm/-y: o usuário confirma ali)
        var interactive = elevate == "sudo";
        string[]? cmd = kind switch
        {
            InstallKind.Pacman when exists("pacman") => interactive ? ["pacman", "-U", abs] : ["pacman", "-U", "--noconfirm", abs],
            InstallKind.Deb when exists("apt-get") => interactive ? ["apt-get", "install", abs] : ["apt-get", "install", "-y", abs],
            InstallKind.Deb when exists("dpkg") => ["dpkg", "-i", abs],
            InstallKind.Rpm when exists("dnf") => interactive ? ["dnf", "install", abs] : ["dnf", "install", "-y", abs],
            InstallKind.Rpm when exists("zypper") => interactive
                ? ["zypper", "install", "--allow-unsigned-rpm", abs]
                : ["zypper", "--non-interactive", "install", "--allow-unsigned-rpm", abs],
            InstallKind.Rpm when exists("rpm") => ["rpm", "-U", abs],
            _ => null,
        };
        return cmd == null ? null : new InstallCommand(elevate, cmd);
    }

    /// <summary>pkexec: 126 = o usuário fechou/cancelou o pedido de senha; 127 = não autorizado.</summary>
    public static bool IsAuthCancelled(int exitCode) => exitCode is 126 or 127;
}
