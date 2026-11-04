using System.Diagnostics;
using CraftDriver.Core.Config;

namespace CraftDriver.App.Services;

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

    public static bool AutostartEnabled => File.Exists(AutostartFile);

    public static void SetAutostart(bool enabled)
    {
        if (!enabled)
        {
            File.Delete(AutostartFile);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile)!);
        File.WriteAllText(AutostartFile, Desktop($"{ExecCommand} --minimized", extra: "X-GNOME-Autostart-enabled=true\n"));
    }

    static string Desktop(string exec, string extra = "") => $"""
        [Desktop Entry]
        Type=Application
        Name=craftDriver
        GenericName=Cliente para OneDrive e SharePoint
        Comment=Sincroniza OneDrive e SharePoint com arquivos sob demanda
        Exec={exec}
        Icon=craftdriver
        Terminal=false
        Categories=Network;FileTransfer;
        StartupWMClass=CraftDriver.App
        {extra}
        """;

    public static void InstallIcon()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IconFile)!);
        using var src = Avalonia.Platform.AssetLoader.Open(new Uri("avares://CraftDriver.App/Assets/app-256.png"));
        using var dst = File.Create(IconFile);
        src.CopyTo(dst);
    }

    /// <summary>Instala atalho no menu de aplicativos e itens de menu de contexto.</summary>
    public static List<string> InstallAll()
    {
        var done = new List<string>();
        InstallIcon();

        Directory.CreateDirectory(Path.GetDirectoryName(LauncherFile)!);
        File.WriteAllText(LauncherFile, Desktop(ExecCommand));
        done.Add("Atalho no menu de aplicativos");

        // KDE Dolphin (KF5/KF6). Precisa ser executável no Plasma 6.
        Directory.CreateDirectory(Path.GetDirectoryName(DolphinMenuFile)!);
        File.WriteAllText(DolphinMenuFile, $"""
            [Desktop Entry]
            Type=Service
            MimeType=all/all;
            X-KDE-ServiceTypes=KonqPopupMenu/Plugin
            X-KDE-Submenu=craftDriver
            X-KDE-Priority=TopLevel
            Actions=pin;free;status;

            [Desktop Action pin]
            Name=Sempre manter neste dispositivo
            Icon=emblem-downloads
            Exec={ExecCommand} pin --notify %F

            [Desktop Action free]
            Name=Liberar espaço
            Icon=edit-clear
            Exec={ExecCommand} unpin --notify %F

            [Desktop Action status]
            Name=Status no craftDriver
            Icon=help-about
            Exec={ExecCommand} status --notify %F
            """);
        File.SetUnixFileMode(DolphinMenuFile, (UnixFileMode)0b111_101_101);
        done.Add("Menu de contexto do Dolphin (craftDriver ▸ …)");

        // Nautilus e Nemo: scripts
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

        try { Process.Start(new ProcessStartInfo("update-desktop-database", Path.GetDirectoryName(LauncherFile)!) { UseShellExecute = false })?.WaitForExit(5000); }
        catch { }
        return done;
    }

    static void WriteScript(string path, string cmd)
    {
        File.WriteAllText(path, $"""
            #!/bin/sh
            IFS='
            '
            exec {ExecCommand} {cmd} --notify $NAUTILUS_SCRIPT_SELECTED_FILE_PATHS $NEMO_SCRIPT_SELECTED_FILE_PATHS
            """);
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
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

    public static void Notify(string title, string body)
    {
        if (Environment.GetEnvironmentVariable("ONEDRIVE_LINUX_NO_NOTIFY") == "1") return;
        try
        {
            var psi = new ProcessStartInfo("notify-send") { UseShellExecute = false };
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add("craftDriver");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(File.Exists(IconFile) ? IconFile : "folder-cloud");
            psi.ArgumentList.Add(title);
            psi.ArgumentList.Add(body);
            Process.Start(psi)?.WaitForExit(3000);
        }
        catch { Console.WriteLine($"{title}: {body}"); }
    }
}
