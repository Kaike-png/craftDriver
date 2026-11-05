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
    public void Inicio_automatico_usa_o_atalho_do_pacote_minimizado()
    {
        var autostart = WithXdg(_ =>
        {
            DesktopIntegration.SetAutostart(true);
            return File.ReadAllText(DesktopIntegration.AutostartFile);
        });
        Assert.Equal(Expected(Packaged("applications/craftdriver.desktop"), " --minimized") + "\nX-GNOME-Autostart-enabled=true",
            Norm(autostart));
    }
}
