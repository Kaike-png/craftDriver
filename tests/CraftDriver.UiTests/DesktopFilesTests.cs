using Avalonia.Headless.XUnit;
using CraftDriver.App.Services;

namespace CraftDriver.UiTests;

/// <summary>
/// Os .desktop instalados pelo app (modo usuário) e pelos pacotes têm uma fonte só: packaging/share.
/// Só a linha Exec muda (caminho do binário em vez de "craftdriver").
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public class DesktopFilesTests
{
    static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    static string Packaged(string rel) => File.ReadAllText(Path.Combine(Repo(), "packaging", "share", rel));

    /// <summary>Conteúdo do pacote com o Exec trocado pelo comando deste processo (como o modo usuário deve gravar).</summary>
    static string Expected(string packaged, string execSuffix = "") =>
        string.Join('\n', packaged.Replace("\r", "").Split('\n')
            .Select(l => l.StartsWith("Exec=craftdriver") ? "Exec=" + DesktopIntegration.ExecCommand + l["Exec=craftdriver".Length..] + execSuffix : l))
            .TrimEnd('\n');

    static string Norm(string s) => s.Replace("\r", "").TrimEnd('\n');

    static T WithXdg<T>(Func<string, T> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "craftdriver-xdg-" + Guid.NewGuid().ToString("N")[..6]);
        var (data, config) = (Environment.GetEnvironmentVariable("XDG_DATA_HOME"), Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"));
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(dir, "data"));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(dir, "config"));
        try { return body(dir); }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [AvaloniaFact]
    public void Atalho_e_menu_do_dolphin_do_modo_usuario_sao_os_do_pacote()
    {
        var (launcher, menu, menuMode) = WithXdg(_ =>
        {
            DesktopIntegration.InstallUserLauncherAndMenus([]);
            return (File.ReadAllText(DesktopIntegration.LauncherFile), File.ReadAllText(DesktopIntegration.DolphinMenuFile),
                File.GetUnixFileMode(DesktopIntegration.DolphinMenuFile));
        });
        Assert.Equal(Expected(Packaged("applications/craftdriver.desktop")), Norm(launcher));
        Assert.Equal(Expected(Packaged("kio/servicemenus/craftdriver.desktop")), Norm(menu));
        Assert.True(menuMode.HasFlag(UnixFileMode.UserExecute), "o Plasma 6 exige o servicemenu executável");
    }

    [Fact]
    public void Scripts_do_nautilus_so_quando_a_extensao_nao_esta_ativa()
    {
        var dir = Path.Combine(Path.GetTempPath(), "craftdriver-scripts-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "nautilus"));
            Directory.CreateDirectory(Path.Combine(dir, "nemo"));
            string Script(string fm) => Path.Combine(dir, fm, "scripts", "CraftDriver - Liberar espaço");
            // scripts com o nome antigo ("craftDriver - …", até a 0.4.0) não podem ficar duplicando o menu
            string Old(string fm) => Path.Combine(dir, fm, "scripts", "craftDriver - Liberar espaço");
            foreach (var fm in new[] { "nautilus", "nemo" })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Old(fm))!);
                File.WriteAllText(Old(fm), "#!/bin/sh\n");
            }

            var done = new List<string>();
            DesktopIntegration.InstallFileManagerScripts(done, dir, nautilusExtension: false);
            Assert.True(File.Exists(Script("nautilus")));
            Assert.True(File.Exists(Script("nemo")));
            Assert.False(File.Exists(Old("nautilus")), "script com o nome antigo ficou no Nautilus");
            Assert.False(File.Exists(Old("nemo")), "script com o nome antigo ficou no Nemo");
            Assert.Equal(["Scripts do Nautilus", "Scripts do Nemo"], done);

            // com a extensão (menu CraftDriver próprio) os scripts duplicariam o menu: não grava e apaga os antigos
            done.Clear();
            DesktopIntegration.InstallFileManagerScripts(done, dir, nautilusExtension: true);
            Assert.False(File.Exists(Script("nautilus")));
            Assert.False(File.Exists(Path.Combine(dir, "nautilus", "scripts", "CraftDriver - Sempre manter neste dispositivo")));
            Assert.True(File.Exists(Script("nemo")), "o Nemo não usa a extensão do Nautilus");
            Assert.Equal(["Nautilus: menu da extensão CraftDriver (sem scripts)", "Scripts do Nemo"], done);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Extensao_do_nautilus_ativa_exige_o_arquivo_e_o_nautilus_python()
    {
        var dir = Path.Combine(Path.GetTempPath(), "craftdriver-nx-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            var (user, system, lib) = (Path.Combine(dir, "user"), Path.Combine(dir, "usr-share"), Path.Combine(dir, "usr-lib"));
            bool Active() => DesktopIntegration.NautilusExtensionActive([user, system], [lib]);
            Assert.False(Active());
            var py = Path.Combine(system, "nautilus-python", "extensions", "craftdriver.py");
            Directory.CreateDirectory(Path.GetDirectoryName(py)!);
            File.WriteAllText(py, "");
            Assert.False(Active()); // pacote instalou a extensão, mas sem python3-nautilus ela não carrega
            var so = Path.Combine(lib, "nautilus", "extensions-4", "libnautilus-python.so");
            Directory.CreateDirectory(Path.GetDirectoryName(so)!);
            File.WriteAllText(so, "");
            Assert.True(Active());
            File.Delete(py);
            var userPy = Path.Combine(user, "nautilus-python", "extensions", "craftdriver.py");
            Directory.CreateDirectory(Path.GetDirectoryName(userPy)!);
            File.WriteAllText(userPy, "");
            Assert.True(Active());
            // Nautilus 42 (Ubuntu 22.04): nautilus-python na API 3.0
            Directory.Move(Path.Combine(lib, "nautilus", "extensions-4"), Path.Combine(lib, "nautilus", "extensions-3.0"));
            Assert.True(Active());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Inicio_automatico_usa_o_atalho_do_pacote_minimizado()
    {
        var autostart = WithXdg(_ =>
        {
            DesktopIntegration.SetAutostart(true);
            return File.ReadAllText(DesktopIntegration.AutostartFile);
        });
        // só o [Desktop Entry] do pacote, sem a linha Actions= (num autostart ações não têm função) e com
        // --minimized apenas no Exec principal
        var entry = Packaged("applications/craftdriver.desktop").Replace("\r", "").Split("\n\n[Desktop Action")[0];
        entry = string.Join('\n', entry.Split('\n').Where(l => !l.StartsWith("Actions=")));
        Assert.Equal(Expected(entry, " --minimized") + "\nX-GNOME-Autostart-enabled=true", Norm(autostart));
        var execs = Norm(autostart).Split('\n').Where(l => l.StartsWith("Exec=")).ToList();
        Assert.Equal([$"Exec={DesktopIntegration.ExecCommand} --minimized"], execs);
        Assert.DoesNotContain("[Desktop Action", autostart);
        Assert.DoesNotContain("Actions=", autostart);
    }
}
