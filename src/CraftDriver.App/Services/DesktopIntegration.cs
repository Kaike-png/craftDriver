using System.Diagnostics;
using CraftDriver.Core.Config;

namespace CraftDriver.App.Services;

/// <summary>Xdg: .desktop em ~/.config/autostart (Plasma, GNOME…). Systemd: unit do systemd --user com --headless.</summary>
public enum AutostartMode { Xdg, Systemd }

/// <summary>Atalho no menu, início automático e menus de contexto (Dolphin / Nautilus / Nemo).</summary>
public static class DesktopIntegration
{
    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static string DataHome => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d ? d : Path.Combine(Home, ".local", "share");
    static string ConfigHome => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } d ? d : Path.Combine(Home, ".config");

    public static string AutostartFile => Path.Combine(ConfigHome, "autostart", "craftdriver.desktop");
    public static string LauncherFile => Path.Combine(DataHome, "applications", "craftdriver.desktop");
    public static string DolphinMenuFile => Path.Combine(DataHome, "kio", "servicemenus", "craftdriver.desktop");
    public static string IconFile => Path.Combine(DataHome, "icons", "hicolor", "256x256", "apps", "craftdriver.png");
    public static string ScalableIconFile => Path.Combine(DataHome, "icons", "hicolor", "scalable", "apps", "craftdriver.svg");
    public static string SymbolicIconFile => Path.Combine(DataHome, "icons", "hicolor", "symbolic", "apps", "craftdriver-symbolic.svg");

    /// <summary>Comando para executar este programa (binário publicado ou "dotnet app.dll").</summary>
    public static string ExecCommand
    {
        get
        {
            var exe = Environment.ProcessPath ?? "craftdriver";
            if (Path.GetFileNameWithoutExtension(exe) == "dotnet")
                return $"\"{exe}\" \"{typeof(DesktopIntegration).Assembly.Location}\"";
            return $"\"{exe}\"";
        }
    }

    /// <summary>Unit do systemd --user (Hyprland/Sway e afins, que não leem ~/.config/autostart).</summary>
    public static string SystemdUnitFile => SystemdUnitPath(ConfigHome);
    public const string SystemdUnitName = "craftdriver.service";

    static string AutostartPath(string configHome) => Path.Combine(configHome, "autostart", "craftdriver.desktop");
    static string SystemdUnitPath(string configHome) => Path.Combine(configHome, "systemd", "user", SystemdUnitName);

    public static bool AutostartEnabled => CurrentAutostartMode != null;

    /// <summary>Como o início automático está configurado agora (null = desligado).</summary>
    public static AutostartMode? CurrentAutostartMode => CurrentMode(ConfigHome);

    static AutostartMode? CurrentMode(string configHome) =>
        File.Exists(SystemdUnitPath(configHome)) ? AutostartMode.Systemd
        : File.Exists(AutostartPath(configHome)) ? AutostartMode.Xdg
        : null;

    /// <summary>
    /// Modo indicado para esta sessão: systemd --user em Hyprland/Sway (não processam ~/.config/autostart)
    /// quando há systemctl; nos demais ambientes, o .desktop em ~/.config/autostart.
    /// </summary>
    public static AutostartMode RecommendedAutostartMode =>
        RecommendAutostart(Environment.GetEnvironmentVariable, FindInPath("systemctl") != null);

    internal static AutostartMode RecommendAutostart(Func<string, string?> env, bool hasSystemctl)
    {
        if (!hasSystemctl) return AutostartMode.Xdg;
        var desktops = (env("XDG_CURRENT_DESKTOP") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tiling = desktops.Any(d => d.Equals("Hyprland", StringComparison.OrdinalIgnoreCase) || d.Equals("sway", StringComparison.OrdinalIgnoreCase))
                     || env("HYPRLAND_INSTANCE_SIGNATURE") is { Length: > 0 }
                     || env("SWAYSOCK") is { Length: > 0 };
        return tiling ? AutostartMode.Systemd : AutostartMode.Xdg;
    }

    /// <summary>
    /// Liga/desliga mantendo o modo já em uso (ou o .desktop do XDG, se nenhum). Para escolher o modo
    /// (ex.: <see cref="RecommendedAutostartMode"/>) use a sobrecarga com <see cref="AutostartMode"/>.
    /// </summary>
    public static string? SetAutostart(bool enabled) =>
        SetAutostart(enabled, CurrentAutostartMode ?? AutostartMode.Xdg);

    /// <summary>
    /// Liga o início automático no modo pedido (e remove o do outro modo, para não iniciar duas vezes) ou
    /// desliga os dois. Devolve um aviso para a interface, ou null se deu tudo certo.
    /// </summary>
    public static string? SetAutostart(bool enabled, AutostartMode mode) =>
        SetAutostart(enabled, mode, ConfigHome, FindInPath("systemctl") is { } sc ? args => RunCommand(sc, args) : null);

    /// <param name="systemctl">Executa "systemctl --user …" (recebe os argumentos depois de --user); null = não há systemctl.</param>
    internal static string? SetAutostart(bool enabled, AutostartMode mode, string configHome, Func<string[], int>? systemctl)
    {
        string? warning = null;
        var xdg = AutostartPath(configHome);
        var unit = SystemdUnitPath(configHome);

        if ((!enabled || mode != AutostartMode.Xdg) && File.Exists(xdg)) File.Delete(xdg);
        if ((!enabled || mode != AutostartMode.Systemd) && File.Exists(unit))
        {
            if (systemctl != null && systemctl(["disable", SystemdUnitName]) != 0)
                warning = "Não foi possível desabilitar o serviço craftdriver no systemd --user.";
            File.Delete(unit);
            systemctl?.Invoke(["daemon-reload"]);
        }
        if (!enabled) return warning;

        if (mode == AutostartMode.Xdg)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(xdg)!);
            File.WriteAllText(xdg, Packaged("share/applications/craftdriver.desktop", " --minimized") + "X-GNOME-Autostart-enabled=true\n");
            return warning;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(unit)!);
        File.WriteAllText(unit, SystemdUnitText());
        if (systemctl == null)
            return "systemctl não encontrado: o serviço foi gravado em " + unit + ", mas não foi habilitado.";
        if (systemctl(["daemon-reload"]) != 0 || systemctl(["enable", SystemdUnitName]) != 0)
            return "O serviço foi gravado em " + unit + ", mas \"systemctl --user enable " + SystemdUnitName + "\" falhou.";
        return warning;
    }

    /// <summary>
    /// Unit do systemd --user. Roda com --headless: um serviço do systemd não tem garantia de sessão
    /// gráfica pronta (Hyprland sem uwsm nem ativa graphical-session.target), então não há janela nem
    /// ícone na bandeja — o status fica no CLI (craftdriver status) e nas notificações.
    /// </summary>
    internal static string SystemdUnitText() => $"""
        [Unit]
        Description=craftDriver — cliente para OneDrive e SharePoint (sem interface)
        Documentation=https://github.com/Kaike-png/craftDriver

        [Service]
        Type=simple
        ExecStart={ExecCommand.Replace("%", "%%")} --headless
        Restart=on-failure
        RestartSec=10

        [Install]
        WantedBy=default.target

        """;

    internal static string? FindInPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = Path.Combine(dir, name);
            if (File.Exists(f)) return f;
        }
        return null;
    }

    static int RunCommand(string exe, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("--user");
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return -1;
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15_000)) { try { p.Kill(); } catch { } return -1; }
            return p.ExitCode;
        }
        catch { return -1; }
    }

    /// <summary>
    /// Arquivo de packaging/share (embutido no binário) com "Exec=craftdriver …" trocado pelo comando deste
    /// executável — o modo usuário não tem /usr/bin/craftdriver. Tudo o mais é idêntico ao do pacote.
    /// </summary>
    static string Packaged(string resource, string execSuffix = "")
    {
        var lines = Resource(resource).Replace("\r", "").TrimEnd('\n').Split('\n')
            .Select(l => l.StartsWith("Exec=craftdriver") ? "Exec=" + ExecCommand + l["Exec=craftdriver".Length..] + execSuffix : l);
        return string.Join('\n', lines) + "\n";
    }

    public static void InstallIcon() => InstallIcon(DataHome);

    /// <summary>PNG 256 px e os SVGs (os mesmos de packaging/share/icons) em &lt;dataHome&gt;/icons/hicolor.</summary>
    internal static void InstallIcon(string dataHome)
    {
        var hicolor = Path.Combine(dataHome, "icons", "hicolor");
        var png = Path.Combine(hicolor, "256x256", "apps", "craftdriver.png");
        Directory.CreateDirectory(Path.GetDirectoryName(png)!);
        using (var src = Avalonia.Platform.AssetLoader.Open(new Uri("avares://CraftDriver.App/Assets/app-256.png")))
        using (var dst = File.Create(png))
            src.CopyTo(dst);
        foreach (var rel in new[] { "scalable/apps/craftdriver.svg", "symbolic/apps/craftdriver-symbolic.svg" })
        {
            var file = Path.Combine(hicolor, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, Resource("share/icons/hicolor/" + rel));
        }
    }

    static string Resource(string name)
    {
        using var stream = typeof(DesktopIntegration).Assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException("Recurso ausente: " + name);
        return new StreamReader(stream).ReadToEnd();
    }

    /// <summary>Instalado por pacote (.deb/.rpm/AUR): atalho, ícone e menu do Dolphin já estão em /usr/share.</summary>
    public static bool InstalledBySystemPackage => File.Exists("/usr/share/applications/craftdriver.desktop");

    public static List<string> InstallAll()
    {
        var done = new List<string>();
        if (InstalledBySystemPackage)
        {
            // remove cópias do usuário para não duplicar o atalho e o submenu do Dolphin
            foreach (var f in new[] { LauncherFile, DolphinMenuFile, IconFile, ScalableIconFile, SymbolicIconFile })
                if (File.Exists(f)) File.Delete(f);
            done.Add("Atalho, ícone e menu do Dolphin já instalados pelo pacote do sistema");
        }
        else InstallUserLauncherAndMenus(done);

        // Nautilus e Nemo: scripts (o pacote não instala, pois ficam na pasta do usuário)
        foreach (var (dir, label) in new[]
                 {
                     (Path.Combine(DataHome, "nautilus", "scripts"), "Nautilus"),
                     (Path.Combine(DataHome, "nemo", "scripts"), "Nemo"),
                 })
        {
            if (!Directory.Exists(Path.GetDirectoryName(dir)!)) continue;
            Directory.CreateDirectory(dir);
            WriteScript(Path.Combine(dir, "craftDriver - Sempre manter neste dispositivo"), "pin");
            WriteScript(Path.Combine(dir, "craftDriver - Liberar espaço"), "unpin");
            done.Add($"Scripts do {label}");
        }
        return done;
    }

    internal static void InstallUserLauncherAndMenus(List<string> done)
    {
        InstallIcon();

        Directory.CreateDirectory(Path.GetDirectoryName(LauncherFile)!);
        File.WriteAllText(LauncherFile, Packaged("share/applications/craftdriver.desktop"));
        done.Add("Atalho no menu de aplicativos");

        // KDE Dolphin (KF5/KF6). Precisa ser executável no Plasma 6.
        Directory.CreateDirectory(Path.GetDirectoryName(DolphinMenuFile)!);
        File.WriteAllText(DolphinMenuFile, Packaged("share/kio/servicemenus/craftdriver.desktop"));
        File.SetUnixFileMode(DolphinMenuFile, (UnixFileMode)0b111_101_101);
        done.Add("Menu de contexto do Dolphin (craftDriver ▸ …)");

        try { Process.Start(new ProcessStartInfo("update-desktop-database", Path.GetDirectoryName(LauncherFile)!) { UseShellExecute = false })?.WaitForExit(5000); }
        catch { }
    }

    /// <summary>
    /// Script do Nautilus/Nemo. Os arquivos selecionados chegam como argumentos ("$@", sem problema com
    /// espaços, aspas ou * no nome); a variável com a lista separada por linhas só é usada se não houver
    /// argumentos, e aí sem expansão de curingas (set -f) e dividindo só por quebra de linha.
    /// </summary>
    internal static string ScriptText(string cmd) => $"""
        #!/bin/sh
        if [ "$#" -gt 0 ]; then
          exec {ExecCommand} {cmd} --notify "$@"
        fi
        set -f
        IFS='
        '
        exec {ExecCommand} {cmd} --notify $NAUTILUS_SCRIPT_SELECTED_FILE_PATHS $NEMO_SCRIPT_SELECTED_FILE_PATHS
        """;

    static void WriteScript(string path, string cmd)
    {
        File.WriteAllText(path, ScriptText(cmd));
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
    }

    /// <summary>
    /// Restos do nome antigo: o single-file extraía bibliotecas nativas em ~/.net/OneDriveLinux.App
    /// (dezenas de MB) e nada mais usa essa pasta.
    /// </summary>
    public static void RemoveLegacyLeftovers()
    {
        try
        {
            var dir = Path.Combine(Home, ".net", "OneDriveLinux.App");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch { /* melhor esforço */ }
    }

    public static void OpenPath(string pathOrUrl)
    {
        // Pastas: pede ao gerenciador de arquivos via D-Bus (org.freedesktop.FileManager1).
        // xdg-open em ponto de montagem usa o tipo inode/mount-point, que em várias
        // instalações aponta para o terminal (ex.: kitty-open) em vez do Dolphin/Nautilus.
        if (Directory.Exists(pathOrUrl) && ShowFolder(pathOrUrl)) return;
        try { Process.Start(new ProcessStartInfo("xdg-open", pathOrUrl) { UseShellExecute = false }); }
        catch { }
    }

    static bool ShowFolder(string dir)
    {
        var uri = new Uri(Path.GetFullPath(dir)).AbsoluteUri;
        try
        {
            var psi = new ProcessStartInfo("dbus-send")
            {
                UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true,
            };
            foreach (var a in new[]
                     {
                         "--session", "--print-reply", "--dest=org.freedesktop.FileManager1", "--type=method_call",
                         "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowFolders",
                         $"array:string:{uri}", "string:",
                     })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null || !p.WaitForExit(10_000)) return false;
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Ícone pelo nome do tema (o pacote instala em /usr/share/icons/hicolor e o modo usuário em
    /// ~/.local/share/icons/hicolor): o caminho fixo do modo usuário não existia em instalações por pacote.
    /// </summary>
    public static string NotificationIcon =>
        File.Exists(IconFile) || File.Exists(ScalableIconFile) || File.Exists("/usr/share/icons/hicolor/256x256/apps/craftdriver.png")
        || File.Exists("/usr/share/icons/hicolor/scalable/apps/craftdriver.svg") ? "craftdriver" : "folder-cloud";

    static readonly Lazy<DesktopNotifier> Notifier = new(() => new DesktopNotifier(DesktopNotifier.SessionBusAddress())
    {
        Log = s => Console.Error.WriteLine(s),
    });

    /// <summary>
    /// Notificação da área de trabalho (D-Bus org.freedesktop.Notifications; sem D-Bus, notify-send sem botões).
    /// Não bloqueia — é chamada da thread do motor (exclusão em massa); as ações rodam no pool de threads.
    /// O Task só serve a processos curtos (CLI) que precisam esperar a entrega antes de sair.
    /// CRAFTDRIVER_NO_NOTIFY=1 desliga tudo (testes).
    /// </summary>
    public static Task Notify(string title, string body, params NotificationAction[] actions)
    {
        if (Environment.GetEnvironmentVariable("CRAFTDRIVER_NO_NOTIFY") == "1") return Task.CompletedTask;
        var icon = NotificationIcon;
        return Task.Run(() => Notifier.Value.NotifyAsync(title, body, icon, actions));
    }
}
