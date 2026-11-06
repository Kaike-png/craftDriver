using Avalonia.Headless.XUnit;
using CraftDriver.App.Services;

namespace CraftDriver.UiTests;

/// <summary>
/// Início automático em Hyprland/Sway (systemd --user) e ícones SVG do modo usuário. Nada aqui toca no
/// systemd real: o executor do systemctl é injetado e o XDG_CONFIG_HOME é um diretório temporário.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public class AutostartTests
{
    static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    static T WithDir<T>(Func<string, T> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "craftdriver-auto-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try { return body(dir); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    static Func<string, string?> Env(params (string k, string v)[] vars) =>
        k => vars.FirstOrDefault(v => v.k == k).v;

    [Theory]
    [InlineData("Hyprland", null, null, true, AutostartMode.Systemd)]
    [InlineData("sway", null, null, true, AutostartMode.Systemd)]
    [InlineData("sway:wlroots", null, null, true, AutostartMode.Systemd)]
    [InlineData(null, "abc_123", null, true, AutostartMode.Systemd)]
    [InlineData(null, null, "/run/user/1000/sway-ipc.sock", true, AutostartMode.Systemd)]
    [InlineData("KDE", null, null, true, AutostartMode.Xdg)]
    [InlineData("GNOME", null, null, true, AutostartMode.Xdg)]
    [InlineData("Hyprland", "abc", null, false, AutostartMode.Xdg)] // sem systemctl não há o que habilitar
    public void Modo_recomendado(string? desktop, string? hypr, string? sway, bool hasSystemctl, AutostartMode expected)
    {
        var env = Env(("XDG_CURRENT_DESKTOP", desktop!), ("HYPRLAND_INSTANCE_SIGNATURE", hypr!), ("SWAYSOCK", sway!));
        Assert.Equal(expected, DesktopIntegration.RecommendAutostart(env, hasSystemctl));
    }

    [Fact]
    public void Systemd_grava_a_unit_headless_e_habilita_sem_deixar_o_autostart_xdg()
    {
        WithDir(cfg =>
        {
            var calls = new List<string>();
            int Fake(string[] a) { calls.Add(string.Join(' ', a)); return 0; }

            Assert.Null(DesktopIntegration.SetAutostart(true, AutostartMode.Xdg, cfg, Fake));
            Assert.True(File.Exists(Path.Combine(cfg, "autostart", "craftdriver.desktop")));
            Assert.Empty(calls);

            Assert.Null(DesktopIntegration.SetAutostart(true, AutostartMode.Systemd, cfg, Fake));
            var unit = File.ReadAllText(Path.Combine(cfg, "systemd", "user", "craftdriver.service"));
            Assert.Contains("\nExecStart=" + DesktopIntegration.ExecCommand + " --headless\n", unit);
            Assert.Contains("\nRestart=on-failure\n", unit);
            Assert.Contains("\nWantedBy=default.target\n", unit);
            // §19.7: encerra pelo IPC (envios em curso terminam) e espera o processo sair; 143 = SIGTERM não é falha
            Assert.Contains("\nExecStop=-/bin/sh -c '" + DesktopIntegration.ExecCommand + " quit; while kill -0 \"$$MAINPID\" 2>/dev/null; do sleep 0.5; done'\n", unit);
            Assert.Contains("\nSuccessExitStatus=143\n", unit);
            Assert.Contains("\nTimeoutStopSec=", unit);
            // sessão gráfica: ordem, parada junto com ela e o ambiente para notificações/navegador do login
            Assert.Contains("\nAfter=graphical-session.target\n", unit);
            Assert.Contains("\nPartOf=graphical-session.target\n", unit);
            Assert.Contains("\nPassEnvironment=WAYLAND_DISPLAY DISPLAY DBUS_SESSION_BUS_ADDRESS XDG_CURRENT_DESKTOP\n", unit);
            Assert.Equal(["daemon-reload", "enable craftdriver.service"], calls);
            Assert.False(File.Exists(Path.Combine(cfg, "autostart", "craftdriver.desktop")), "iniciaria duas vezes");

            calls.Clear();
            Assert.Null(DesktopIntegration.SetAutostart(false, AutostartMode.Systemd, cfg, Fake));
            Assert.False(File.Exists(Path.Combine(cfg, "systemd", "user", "craftdriver.service")));
            Assert.Equal(["disable craftdriver.service", "daemon-reload"], calls);

            // desligado: nada mais para desabilitar
            calls.Clear();
            DesktopIntegration.SetAutostart(false, AutostartMode.Systemd, cfg, Fake);
            Assert.Empty(calls);
            return 0;
        });
    }

    [Fact]
    public void Voltar_para_xdg_desabilita_o_servico()
    {
        WithDir(cfg =>
        {
            var calls = new List<string>();
            int Fake(string[] a) { calls.Add(string.Join(' ', a)); return 0; }
            DesktopIntegration.SetAutostart(true, AutostartMode.Systemd, cfg, Fake);
            calls.Clear();
            DesktopIntegration.SetAutostart(true, AutostartMode.Xdg, cfg, Fake);
            Assert.Equal(["disable craftdriver.service", "daemon-reload"], calls);
            Assert.False(File.Exists(Path.Combine(cfg, "systemd", "user", "craftdriver.service")));
            Assert.True(File.Exists(Path.Combine(cfg, "autostart", "craftdriver.desktop")));
            return 0;
        });
    }

    [Fact]
    public void Sem_systemctl_grava_a_unit_e_avisa()
    {
        WithDir(cfg =>
        {
            var warning = DesktopIntegration.SetAutostart(true, AutostartMode.Systemd, cfg, null);
            Assert.NotNull(warning);
            Assert.Contains("systemctl", warning);
            Assert.True(File.Exists(Path.Combine(cfg, "systemd", "user", "craftdriver.service")));
            return 0;
        });
    }

    [Fact]
    public void Falha_do_systemctl_vira_aviso()
    {
        WithDir(cfg =>
        {
            var warning = DesktopIntegration.SetAutostart(true, AutostartMode.Systemd, cfg, a => a[0] == "enable" ? 1 : 0);
            Assert.Contains("enable", warning);
            return 0;
        });
    }

    [Fact]
    public void Unit_valida_para_o_systemd_analyze()
    {
        // systemd-analyze verify só lê o arquivo (não fala com o gerenciador do usuário)
        var analyze = DesktopIntegration.FindInPath("systemd-analyze");
        Assert.SkipWhen(analyze == null, "systemd-analyze ausente");
        WithDir(cfg =>
        {
            var unit = Path.Combine(cfg, "craftdriver.service");
            // ExecStart precisa apontar para um executável existente; o comando real deste processo serve
            File.WriteAllText(unit, DesktopIntegration.SystemdUnitText());
            var psi = new System.Diagnostics.ProcessStartInfo(analyze!) { RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "--user", "verify", "--man=no", unit }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            p.WaitForExit(30_000);
            Assert.True(p.ExitCode == 0, err);
            return 0;
        });
    }

    [Fact]
    public void Desktop_tem_as_acoes_abrir_pasta_sincronizar_pausar_e_retomar()
    {
        var desktop = File.ReadAllText(Path.Combine(Repo(), "packaging", "share", "applications", "craftdriver.desktop"));
        Assert.Contains("\nActions=open-folder;sync;pause;resume;\n", desktop);
        Assert.Contains("[Desktop Action resume]\nName=Retomar sincronização\n", desktop);
        Assert.Contains("\nExec=craftdriver resume --notify\n", desktop);
        Assert.Contains("[Desktop Action open-folder]\nName=Abrir pasta\n", desktop);
        Assert.Contains("\nExec=craftdriver open\n", desktop);
        Assert.Contains("\nExec=craftdriver sync --notify\n", desktop);
        Assert.Contains("\nExec=craftdriver pause --notify\n", desktop);
    }

    [AvaloniaFact]
    public void Modo_usuario_instala_os_svgs_do_pacote()
    {
        WithDir(dir =>
        {
            DesktopIntegration.InstallIcon(dir);
            foreach (var rel in new[] { "scalable/apps/craftdriver.svg", "symbolic/apps/craftdriver-symbolic.svg" })
                Assert.Equal(File.ReadAllText(Path.Combine(Repo(), "packaging", "share", "icons", "hicolor", rel)),
                    File.ReadAllText(Path.Combine(dir, "icons", "hicolor", rel)));
            Assert.True(File.Exists(Path.Combine(dir, "icons", "hicolor", "256x256", "apps", "craftdriver.png")));
            return 0;
        });
    }
}
