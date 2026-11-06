using System.Collections.Concurrent;
using Stopwatch = System.Diagnostics.Stopwatch;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CraftDriver.App.Services;
using CraftDriver.App.ViewModels;
using CraftDriver.App.Views;
using CraftDriver.Core.Config;
using CraftDriver.Core.Ipc;
using CraftDriver.Core.Sync;
using CraftDriver.Tests;

namespace CraftDriver.UiTests;

/// <summary>
/// PENDENCIAS §15 — testes RED (reproduzem os defeitos; nenhuma correção de produção).
/// Trocam CRAFTDRIVER_HOME/PATH (globais ao processo): coleção sem paralelismo.
/// </summary>
[Collection("host-isolado")]
public class Secao15Tests
{
    static AppHost NewHost(string? configJson = null)
    {
        var home = ShellUi.NewHome();
        if (configJson != null) File.WriteAllText(Path.Combine(home, "config", "config.json"), configJson.Replace("{HOME}", home));
        var host = Task.Run(() => AppHost.CreateAsync()).GetAwaiter().GetResult();
        CraftDriver.App.App.Host = host;
        host.GraphFactory = _ => new FakeGraph();
        host.Notify = (_, _, _) => { };
        return host;
    }

    /// <summary>
    /// 15.1: janela principal já criada e escondida na bandeja; "Entrar novamente" da notificação de sessão
    /// expirada. O RequestShow só POSTA o ShowWindow e, no mesmo callback, o ShowDialog(this) roda com a
    /// dona invisível → InvalidOperationException num async void (em produção derruba o processo).
    /// </summary>
    [AvaloniaFact]
    public void S15_1_Entrar_novamente_com_janela_na_bandeja_abre_o_login_sem_excecao()
    {
        var host = NewHost();
        NotificationAction[] actions = [];
        host.Notify = (_, _, a) => { if (a.Length > 0) actions = a; };
        // conta corporativa: o login abre por código de dispositivo (Microsoft falsa), sem navegador
        host.Config.Accounts.Add(new AccountConfig { Id = "a1", Username = "u@empresa.exemplo", Type = "business" });
        var vm = new MainViewModel(host);
        var w = new MainWindow { DataContext = vm };
        // como o App: ShowRequested posta o ShowWindow para depois
        host.ShowRequested += () => Dispatcher.UIThread.Post(() => w.Show());
        w.Show();
        ShellUi.Pump(200);
        w.Hide(); // fechar = esconder na bandeja
        ShellUi.Pump(100);

        var errors = new List<Exception>();
        void OnUnhandled(object? s, DispatcherUnhandledExceptionEventArgs e) { errors.Add(e.Exception); e.Handled = true; }
        Dispatcher.UIThread.UnhandledException += OnUnhandled;
        try
        {
            host.CheckSessions([("a1", true)]);
            var relogin = actions.Single(a => a.Id == "relogin");
            Task.Run(relogin.OnInvoke).Wait(); // como o D-Bus: thread do pool
            var until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until && errors.Count == 0 && !w.OwnedWindows.OfType<LoginWindow>().Any())
            {
                try { Dispatcher.UIThread.RunJobs(); }
                catch (Exception ex) { errors.Add(ex); }
                Thread.Sleep(20);
            }
            Assert.True(errors.Count == 0, "exceção na thread de UI: " + string.Join(" | ", errors.Select(e => e.GetType().Name + ": " + e.Message)));
            Assert.Contains(w.OwnedWindows, x => x is LoginWindow);
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= OnUnhandled;
            foreach (var o in w.OwnedWindows.ToList()) o.Close();
            w.Close();
            vm.Dispose();
        }
    }

    /// <summary>
    /// 15.2 (caminho concreto): "Sair" → ShutdownAsync → Config.Save com a pasta de configuração sem escrita
    /// (= disco cheio/EROFS). A exceção sobe para App.Quit (async void) / RunHeadless e mata o processo sem
    /// _desktop.Shutdown nem linha no log. Esperado: o encerramento conclui e registra o erro.
    /// </summary>
    [AvaloniaFact]
    public void S15_2_Shutdown_com_config_sem_escrita_nao_lanca_e_registra_no_log()
    {
        if (Environment.UserName == "root") Assert.Skip("root ignora permissões");
        var host = NewHost();
        var dir = AppPaths.ConfigDir;
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        Exception? thrown = null;
        try { Task.Run(host.ShutdownAsync).GetAwaiter().GetResult(); }
        catch (Exception ex) { thrown = ex; }
        finally { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        Assert.True(thrown == null, "ShutdownAsync lançou: " + thrown?.GetType().Name + ": " + thrown?.Message);
        Assert.Contains(host.Activity.Entries, e => e.Kind == ActivityKind.Error);
    }

    /// <summary>
    /// 15.5: "Abrir pasta" chama DesktopIntegration.OpenPath → ShowFolder (dbus-send + WaitForExit 10 s) na
    /// thread de UI. Um dbus-send lento (ativação D-Bus do gerenciador de arquivos fechado) congela a janela.
    /// </summary>
    [AvaloniaFact]
    public void S15_5_Abrir_pasta_nao_bloqueia_a_thread_de_UI()
    {
        var host = NewHost();
        Directory.CreateDirectory(host.Config.MountPoint);
        var bin = Path.Combine(Path.GetDirectoryName(AppPaths.ConfigDir)!, "bin");
        Directory.CreateDirectory(bin);
        var fake = Path.Combine(bin, "dbus-send");
        File.WriteAllText(fake, "#!/bin/sh\nsleep 3\nexit 0\n");
        File.SetUnixFileMode(fake, (UnixFileMode)0b111_101_101);
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", bin + ":" + oldPath);
        var vm = new MainViewModel(host);
        try
        {
            var sw = Stopwatch.StartNew();
            vm.OpenFolderCommand.Execute(null);
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 500, $"a thread de UI ficou presa {sw.ElapsedMilliseconds} ms esperando o dbus-send");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            vm.Dispose();
        }
    }

    /// <summary>
    /// 15.7: configuração de versão mais nova (somente leitura). RegisterAccount (login pela janela e IPC
    /// add-account) "conecta" a conta, mas o Save retorna calado: nada no disco e nenhum aviso.
    /// </summary>
    [AvaloniaFact]
    public void S15_7_Conta_conectada_com_config_somente_leitura_avisa_que_nao_foi_salva()
    {
        var host = NewHost("""{ "SchemaVersion": 999, "MountPoint": "{HOME}/OneDrive", "CacheDir": "{HOME}/cache" }""");
        Assert.True(host.Config.ReadOnly);
        host.RegisterAccount(new AccountConfig { Id = "a1", Username = "u@exemplo.com", Type = "personal" }, "d1", null);
        Assert.Empty(AppConfig.Load().Accounts); // nada foi gravado (comportamento esperado do ReadOnly)
        Assert.Contains(host.Activity.Entries, e => e.Kind == ActivityKind.Error && e.Text.Contains("não"));
    }

    /// <summary>15.15: "craftdriver pin" (ou unpin/free) sem arquivos responde Ok com texto vazio.</summary>
    [AvaloniaTheory]
    [InlineData("pin")]
    [InlineData("unpin")]
    [InlineData("free")]
    public void S15_15_Comando_de_item_sem_argumentos_responde_erro_com_uso(string cmd)
    {
        var host = NewHost();
        var resp = Task.Run(() => host.HandleIpcAsync(new IpcRequest(cmd, []))).GetAwaiter().GetResult();
        Assert.False(resp.Ok, $"\"{cmd}\" sem arquivos respondeu Ok com \"{resp.Text}\"");
        Assert.False(string.IsNullOrWhiteSpace(resp.Text));
    }
}
