using System.Net.Sockets;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CraftDriver.App.Services;
using CraftDriver.App.Views;
using CraftDriver.App.ViewModels;
using CraftDriver.Core.Auth;
using CraftDriver.Core.Config;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Tmds.DBus.Protocol;

namespace CraftDriver.UiTests;

static class ShellUi
{
    public static readonly string Out = Environment.GetEnvironmentVariable("CRAFTDRIVER_UI_OUT") ?? Path.Combine(Path.GetTempPath(), "craftdriver-ui");

    public static void Pump(int ms = 300)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
    }

    public static void PumpUntil(Func<bool> cond, string what, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException(what);
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
    }

    /// <summary>Espera uma Task na thread de UI rodando os jobs (o comando pode continuar nela depois de um await).</summary>
    public static void Wait(Task t, int ms = 15000)
    {
        PumpUntil(() => t.IsCompleted, "comando assíncrono", ms);
        t.GetAwaiter().GetResult();
    }

    public static void Save(Window w, string name)
    {
        Directory.CreateDirectory(Out);
        Pump();
        using var fs = File.Create(Path.Combine(Out, name + ".png"));
        w.CaptureRenderedFrame()!.Save(fs, PngBitmapEncoderOptions.Default);
    }

    public static string NewHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "craftdriver-ui-home-" + Guid.NewGuid().ToString("N")[..6]);
        Environment.SetEnvironmentVariable("CRAFTDRIVER_HOME", home);
        Directory.CreateDirectory(Path.Combine(home, "config"));
        File.WriteAllText(Path.Combine(home, "config", "config.json"),
            $$"""{ "MountPoint": "{{home}}/OneDrive", "CacheDir": "{{home}}/cache" }""");
        return home;
    }
}

/// <summary>Item 1: espera pelo chaveiro sem travar o início (AuthService com chaveiro falso).</summary>
[Collection("host-isolado")]
public class KeyringStartupTests
{
    static AppConfig ConfigWithAccount() => new() { Accounts = [new AccountConfig { Id = "a1", Username = "u@exemplo.com" }] };

    [Fact]
    public async Task Continuar_sem_chaveiro_libera_o_inicio_travado_e_cai_no_arquivo()
    {
        var home = ShellUi.NewHome();
        File.WriteAllText(Path.Combine(home, "config", "token-storage"), "keyring");
        var opened = 0;
        var keyring = new AuthService.KeyringAccess
        {
            EnsureRunning = (_, _) => Task.FromResult(true),
            // chaveiro travado (ex.: esperando a senha): nunca termina
            Open = _ => { Interlocked.Increment(ref opened); return new TaskCompletionSource<MsalCacheHelper>().Task; },
        };
        var statuses = new List<string>();
        using var skip = new CancellationTokenSource();
        var create = AuthService.CreateAsync(ConfigWithAccount(), s => { lock (statuses) statuses.Add(s); }, skip.Token, keyring);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.False(create.IsCompleted); // ainda esperando o chaveiro
        skip.Cancel();
        var auth = await create.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(auth.KeyringUnavailable);
        Assert.StartsWith("arquivo (", auth.CacheStorage);
        Assert.Contains(AuthService.OpeningKeyringStatus, statuses);
        Assert.Contains(auth.StartupNotes, n => n.Contains("sem esperar o chaveiro"));
        Assert.Equal(1, opened);
        // os tokens continuam esperados no chaveiro no próximo início
        Assert.Equal("keyring", File.ReadAllText(Path.Combine(home, "config", "token-storage")).Trim());
    }

    [Fact]
    public async Task Pular_sem_contas_nao_grava_a_preferencia_pelo_arquivo()
    {
        var home = ShellUi.NewHome();
        using var skip = new CancellationTokenSource();
        skip.Cancel();
        var keyring = new AuthService.KeyringAccess
        {
            EnsureRunning = (_, _) => new TaskCompletionSource<bool>().Task,
            Open = _ => throw new InvalidOperationException("não deveria abrir"),
        };
        var auth = await AuthService.CreateAsync(new AppConfig(), null, skip.Token, keyring).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(auth.KeyringUnavailable);
        Assert.False(File.Exists(Path.Combine(home, "config", "token-storage")));
    }

    [Fact]
    public async Task Chaveiro_que_abre_devagar_e_usado_quando_ninguem_pula()
    {
        var home = ShellUi.NewHome();
        File.WriteAllText(Path.Combine(home, "config", "token-storage"), "keyring");
        var keyring = new AuthService.KeyringAccess
        {
            EnsureRunning = (_, _) => Task.FromResult(true),
            Open = async name =>
            {
                await Task.Delay(400);
                // "chaveiro" falso: um cache em arquivo noutra pasta (nada toca o chaveiro real)
                var props = new StorageCreationPropertiesBuilder(name, Path.Combine(home, "fake-keyring")).WithLinuxUnprotectedFile().Build();
                return await MsalCacheHelper.CreateAsync(props);
            },
        };
        var auth = await AuthService.CreateAsync(ConfigWithAccount(), null, CancellationToken.None, keyring)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("chaveiro do sistema", auth.CacheStorage);
        Assert.False(auth.KeyringUnavailable);
    }
}

/// <summary>Item 1: janela de espera (StartupCoordinator) com fábrica lenta.</summary>
public class SplashTests
{
    sealed class SlowFactory
    {
        public readonly TaskCompletionSource<string> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Skip;
        public int Calls;

        public async Task<string> Create(Action<string> status, CancellationToken skip)
        {
            Interlocked.Increment(ref Calls);
            Skip = skip;
            status(AuthService.OpeningKeyringStatus);
            // como o AuthService: "continuar sem o chaveiro" encerra a espera
            using (skip.Register(() => Release.TrySetResult("sem-chaveiro")))
                return await Release.Task;
        }
    }

    [AvaloniaFact]
    public void Janela_de_espera_aparece_mostra_o_chaveiro_e_some_ao_terminar()
    {
        var f = new SlowFactory();
        var c = new StartupCoordinator<string>(f.Create);
        var result = c.RunAsync(showSplash: true, TimeSpan.FromMilliseconds(50));
        ShellUi.PumpUntil(() => c.Splash?.IsVisible == true, "janela de espera");
        var splash = c.Splash!;
        ShellUi.PumpUntil(() => splash.Status == AuthService.OpeningKeyringStatus, "status do chaveiro");
        Assert.True(splash.FindControl<Button>("SkipButton")!.IsVisible);
        ShellUi.Save(splash, "10a-inicio-abrindo-chaveiro");

        f.Release.SetResult("host");
        ShellUi.PumpUntil(() => result.IsCompleted, "resultado");
        Assert.Equal("host", result.Result);
        Assert.False(splash.IsVisible);
        Assert.Null(c.Splash);
    }

    [AvaloniaFact]
    public void Continuar_sem_chaveiro_cancela_a_espera_e_segue()
    {
        var f = new SlowFactory();
        var c = new StartupCoordinator<string>(f.Create);
        var result = c.RunAsync(showSplash: true, TimeSpan.FromMilliseconds(50));
        ShellUi.PumpUntil(() => c.Splash?.IsVisible == true, "janela de espera");
        var splash = c.Splash!;
        splash.FindControl<Button>("SkipButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        ShellUi.PumpUntil(() => result.IsCompleted, "resultado");
        Assert.True(f.Skip.IsCancellationRequested);
        Assert.Equal("sem-chaveiro", result.Result);
        Assert.False(splash.IsVisible);
    }

    [AvaloniaFact]
    public void Esc_na_janela_de_espera_sai_sem_esperar_e_a_fabrica_termina()
    {
        var f = new SlowFactory();
        var c = new StartupCoordinator<string>(f.Create);
        var result = c.RunAsync(showSplash: true, TimeSpan.FromMilliseconds(50));
        ShellUi.PumpUntil(() => c.Splash?.IsVisible == true, "janela de espera");
        var splash = c.Splash!;
        splash.Activate();
        splash.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        ShellUi.PumpUntil(() => result.IsCompleted, "resultado após Esc");
        Assert.Null(result.Result); // App.StartAsync encerra o processo com null
        Assert.False(splash.IsVisible);
        Assert.True(f.Skip.IsCancellationRequested);
        // nada fica pendurado: a fábrica recebeu o cancelamento e terminou
        ShellUi.PumpUntil(() => c.FactoryTask!.IsCompleted, "fábrica terminar");
    }

    [AvaloniaFact]
    public void Inicio_rapido_nao_mostra_janela_de_espera()
    {
        var c = new StartupCoordinator<string>((_, _) => Task.FromResult("host"));
        var result = c.RunAsync(showSplash: true, TimeSpan.FromMilliseconds(300));
        ShellUi.Pump(600);
        Assert.True(result.IsCompleted);
        Assert.Null(c.Splash);
    }

    [AvaloniaFact]
    public void Minimizado_com_bandeja_nao_abre_a_espera_mas_o_clique_abre()
    {
        var f = new SlowFactory();
        var c = new StartupCoordinator<string>(f.Create);
        var result = c.RunAsync(showSplash: false, TimeSpan.FromMilliseconds(20));
        ShellUi.Pump(300);
        Assert.Null(c.Splash);
        c.ShowSplash(); // clique no ícone durante a inicialização
        Assert.True(c.Splash!.IsVisible);
        f.Release.SetResult("host");
        ShellUi.PumpUntil(() => result.IsCompleted, "resultado");
        Assert.Null(c.Splash);
    }

    [AvaloniaFact]
    public void Falha_na_inicializacao_fica_na_janela_ate_fechar()
    {
        var c = new StartupCoordinator<string>((_, _) => Task.FromException<string>(new IOException("banco de dados bloqueado por outro processo")));
        var result = c.RunAsync(showSplash: true, TimeSpan.FromMilliseconds(1000));
        ShellUi.PumpUntil(() => c.Splash?.IsVisible == true, "janela de erro");
        var splash = c.Splash!;
        Assert.Contains("banco de dados bloqueado", splash.FindControl<TextBlock>("ErrorText")!.Text);
        Assert.False(splash.FindControl<Button>("SkipButton")!.IsVisible);
        ShellUi.Save(splash, "10b-inicio-erro");
        Assert.False(result.IsCompleted);
        splash.Close();
        ShellUi.PumpUntil(() => result.IsCompleted, "resultado");
        Assert.Null(result.Result);
    }

    [Theory]
    [InlineData(false, 1, true, true)]
    [InlineData(true, 1, true, false)]  // minimizado com bandeja: só o ícone
    [InlineData(true, 0, true, true)]   // sem conta: precisa da janela para entrar
    [InlineData(true, 1, false, true)]  // sem bandeja: minimizado deixaria o app invisível
    public void Inicio_minimizado_nunca_deixa_o_app_invisivel(bool minimized, int accounts, bool tray, bool show)
        => Assert.Equal(show, StartupPlan.ShowWindowAtStart(minimized, accounts, tray));
}

/// <summary>Item 2: erros do MSAL na janela de login, "Tentar de novo" e Esc.</summary>
[Collection("host-isolado")]
public class LoginErrorUiTests
{
    static AppHost NewHost()
    {
        ShellUi.NewHome();
        var host = Task.Run(() => AppHost.CreateAsync()).GetAwaiter().GetResult();
        CraftDriver.App.App.Host = host;
        return host;
    }

    [AvaloniaFact]
    public void Erro_do_msal_traduzido_detalhe_recolhivel_e_tentar_de_novo()
    {
        var host = NewHost();
        var login = new LoginWindow(host) { Width = 580, Height = 520 };
        var calls = 0;
        login.SignInForTest = _ =>
        {
            calls++;
            if (calls == 1)
                throw new MsalServiceException("invalid_grant",
                    "AADSTS65001: The user or administrator has not consented to use the application with ID '979715f8'. Send an interactive authorization request for this user and resource.");
            return Task.FromResult(new AccountConfig { Id = "x", Username = "maria@empresa.exemplo", Type = "business" });
        };
        login.Show();
        _ = login.StartForTestAsync(personal: false);
        ShellUi.PumpUntil(() => login.LastError != null, "erro exibido");

        Assert.Equal(LoginErrorKind.AdminConsentRequired, login.LastError!.Kind);
        Assert.Contains("administrador", login.FindControl<TextBlock>("ErrorText")!.Text);
        Assert.StartsWith("Não foi possível entrar", login.FindControl<TextBlock>("StatusText")!.Text);
        var details = login.FindControl<Expander>("ErrorDetails")!;
        Assert.False(details.IsExpanded);
        Assert.Contains("AADSTS65001", login.FindControl<SelectableTextBlock>("ErrorDetailText")!.Text);
        var retry = login.FindControl<Button>("RetryButton")!;
        Assert.True(retry.IsVisible);
        ShellUi.Save(login, "6e-login-erro-consentimento");
        details.IsExpanded = true;
        ShellUi.Save(login, "6f-login-erro-detalhes");

        retry.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        ShellUi.PumpUntil(() => login.Success, "segunda tentativa conclui");
        Assert.Equal(2, calls);
        Assert.True(login.IsVisible); // a janela não fechou no erro nem no retry
        Assert.False(login.FindControl<Border>("ErrorPanel")!.IsVisible);
        Assert.False(retry.IsVisible);
        Assert.Contains("maria@empresa.exemplo", login.FindControl<TextBlock>("StatusText")!.Text);
        login.Close();
    }

    [AvaloniaFact]
    public void Esc_fecha_a_janela_de_login()
    {
        var host = NewHost();
        var login = new LoginWindow(host) { Width = 580, Height = 520 };
        var closed = false;
        login.Closed += (_, _) => closed = true;
        login.Show();
        login.Activate();
        ShellUi.Pump(100);
        login.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        ShellUi.PumpUntil(() => closed, "Esc fechar");
    }

    [AvaloniaFact]
    public void Codigo_expirado_oferece_novo_codigo()
    {
        var host = NewHost();
        var login = new LoginWindow(host) { Width = 580, Height = 520 };
        login.SignInForTest = _ => throw new MsalServiceException(MsalError.CodeExpired, "Verification code expired before contacting the server");
        login.Show();
        _ = login.StartForTestAsync(personal: false);
        ShellUi.PumpUntil(() => login.LastError != null, "erro exibido");
        Assert.Equal(LoginErrorKind.CodeExpired, login.LastError!.Kind);
        Assert.Contains("expirou", login.FindControl<TextBlock>("ErrorText")!.Text);
        Assert.True(login.FindControl<Button>("RetryButton")!.IsVisible);
        Assert.False(login.FindControl<Border>("CodePanel")!.IsVisible); // o código velho não fica na tela
        login.Close();
    }
}

/// <summary>Watcher StatusNotifier falso (org.kde.StatusNotifierWatcher) num barramento privado.</summary>
sealed class FakeSniWatcher : IPathMethodHandler, IDisposable
{
    readonly DBusConnection _conn;
    public bool HostRegistered { get; set; }
    public string Path => TrayHostProbe.WatcherPath;
    public bool HandlesChildPaths => false;

    FakeSniWatcher(DBusConnection conn, bool host) { _conn = conn; HostRegistered = host; }

    public static async Task<FakeSniWatcher> StartAsync(string address, bool hostRegistered)
    {
        var conn = new DBusConnection(address);
        await conn.ConnectAsync();
        var w = new FakeSniWatcher(conn, hostRegistered);
        conn.AddMethodHandler(w);
        await conn.RequestNameAsync(TrayHostProbe.WatcherName, RequestNameOptions.Default);
        return w;
    }

    public ValueTask HandleMethodAsync(MethodContext ctx)
    {
        if (ctx.Request.InterfaceAsString == "org.freedesktop.DBus.Properties" && ctx.Request.MemberAsString == "Get")
        {
            var r = ctx.Request.GetBodyReader();
            var iface = r.ReadString();
            var prop = r.ReadString();
            if (iface == TrayHostProbe.WatcherName && prop == "IsStatusNotifierHostRegistered")
            {
                using var w = ctx.CreateReplyWriter("v");
                w.WriteVariantBool(HostRegistered);
                ctx.Reply(w.CreateMessage());
                return ValueTask.CompletedTask;
            }
        }
        ctx.ReplyUnknownMethodError();
        return ValueTask.CompletedTask;
    }

    public void Dispose() => _conn.Dispose();
}

/// <summary>Item 4: detecção do host de bandeja e o que fechar a janela faz (grava config: CRAFTDRIVER_HOME isolado).</summary>
[Collection("host-isolado")]
public class TrayHostTests
{
    static readonly TimeSpan T = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task Sem_watcher_nao_ha_bandeja()
    {
        using var bus = new PrivateBus();
        Assert.False(await TrayHostProbe.IsHostAvailableAsync(bus.Address, T));
        Assert.False(await TrayHostProbe.IsHostAvailableAsync(null, T));
        Assert.False(await TrayHostProbe.IsHostAvailableAsync("unix:path=/nonexistent/cd-bus", T));
    }

    [Fact]
    public async Task Watcher_sem_host_registrado_nao_conta_como_bandeja()
    {
        using var bus = new PrivateBus();
        using var watcher = await FakeSniWatcher.StartAsync(bus.Address, hostRegistered: false);
        Assert.False(await TrayHostProbe.IsHostAvailableAsync(bus.Address, T));
        watcher.HostRegistered = true;
        Assert.True(await TrayHostProbe.IsHostAvailableAsync(bus.Address, T));
    }

    [Fact]
    public async Task Espera_o_painel_subir_depois_do_app()
    {
        using var bus = new PrivateBus();
        var wait = TrayHostProbe.WaitForHostAsync(bus.Address, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100));
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.False(wait.IsCompleted);
        using var watcher = await FakeSniWatcher.StartAsync(bus.Address, hostRegistered: true);
        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    sealed class Probe
    {
        public readonly AppConfig Cfg = new();
        public readonly List<(string, string)> Notices = [];
        public readonly List<string> Asked = [];
        public int Quits;
        public NoTrayChoice Answer = NoTrayChoice.Cancel;
        public string ConfigPath = Path.Combine(Path.GetTempPath(), "cd-tray-" + Guid.NewGuid().ToString("N")[..6], "config.json");

        public CloseToTrayBehavior Behavior(string address) => new(Cfg,
            () => TrayHostProbe.IsHostAvailableAsync(address, T),
            (t, b) => Notices.Add((t, b)),
            w => { Asked.Add(w.Title ?? ""); return Task.FromResult(Answer); },
            () => Quits++);
    }

    static void WaitPending(CloseToTrayBehavior b) => ShellUi.PumpUntil(() => b.Pending?.IsCompleted == true, "decisão ao fechar", 8000);

    [AvaloniaFact]
    public void Com_bandeja_fechar_esconde_e_avisa_uma_vez_so()
    {
        using var bus = new PrivateBus();
        using var watcher = Task.Run(() => FakeSniWatcher.StartAsync(bus.Address, true)).GetAwaiter().GetResult();
        var home = ShellUi.NewHome(); // Save() grava em CRAFTDRIVER_HOME
        var p = new Probe();
        var b = p.Behavior(bus.Address);
        var w = new Window { Title = "principal", Width = 400, Height = 300 };
        b.Attach(w, () => false);
        w.Show();
        w.Close();
        WaitPending(b);
        Assert.False(w.IsVisible);
        Assert.Equal("bandeja", b.LastOutcome);
        var notice = Assert.Single(p.Notices);
        Assert.Equal(CloseToTrayBehavior.NoticeTitle, notice.Item1);
        Assert.True(p.Cfg.TrayNoticeShown);
        Assert.Contains("\"TrayNoticeShown\": true", File.ReadAllText(Path.Combine(home, "config", "config.json")));

        w.Show();
        w.Close();
        WaitPending(b);
        Assert.False(w.IsVisible);
        Assert.Single(p.Notices); // só na primeira vez
        Assert.Empty(p.Asked);
        Assert.Equal(0, p.Quits);
    }

    [AvaloniaFact]
    public void Sem_bandeja_fechar_nao_esconde_pergunta_e_respeita_a_escolha()
    {
        using var bus = new PrivateBus(); // sem watcher
        var p = new Probe { Answer = NoTrayChoice.Minimize };
        var b = p.Behavior(bus.Address);
        var w = new Window { Title = "principal", Width = 400, Height = 300 };
        var quitting = false;
        b.Attach(w, () => quitting);
        w.Show();

        w.Close();
        WaitPending(b);
        Assert.True(w.IsVisible); // nunca some para lugar nenhum
        Assert.Equal(WindowState.Minimized, w.WindowState);
        Assert.Single(p.Asked);
        Assert.Empty(p.Notices);

        p.Answer = NoTrayChoice.Cancel;
        w.WindowState = WindowState.Normal;
        w.Close();
        WaitPending(b);
        Assert.True(w.IsVisible);
        Assert.Equal(WindowState.Normal, w.WindowState);
        Assert.Equal(0, p.Quits);

        p.Answer = NoTrayChoice.Quit;
        w.Close();
        WaitPending(b);
        Assert.Equal(1, p.Quits);
        quitting = true; // App.Quit marca _quitting e fecha de verdade
        w.Close();
        Assert.False(w.IsVisible);
    }

    [AvaloniaFact]
    public void Dialogo_sem_bandeja_renderiza_e_esc_cancela()
    {
        var owner = new Window { Width = 600, Height = 400 };
        owner.Show();
        var dlg = new NoTrayCloseWindow("/home/maria/OneDrive") { Width = 500 };
        var shown = dlg.ShowDialog(owner);
        ShellUi.PumpUntil(() => dlg.IsVisible, "diálogo");
        ShellUi.Save(dlg, "12-fechar-sem-bandeja");
        Assert.Contains("/home/maria/OneDrive", dlg.FindControl<TextBlock>("BodyText")!.Text);
        dlg.Activate();
        dlg.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        ShellUi.PumpUntil(() => shown.IsCompleted, "Esc fechar");
        Assert.Equal(NoTrayChoice.Cancel, dlg.Choice);
        owner.Close();
    }
}

/// <summary>Item 4: tamanho/posição/maximizado da janela principal.</summary>
[Collection("host-isolado")]
public class WindowPlacementTests
{
    static readonly PixelRect[] TwoScreens = [new(0, 0, 1920, 1080), new(1920, 0, 1920, 1080)];

    [Theory]
    [InlineData(100, 80, true)]
    [InlineData(2000, 100, true)]     // segundo monitor
    [InlineData(4000, 100, false)]    // monitor que foi desconectado
    [InlineData(-900, 100, false)]    // só uma beirada visível à esquerda
    [InlineData(1850, 100, true)]     // atravessa os dois monitores
    [InlineData(100, -200, false)]    // título acima da tela
    [InlineData(100, 1070, false)]    // só 10 px do topo visíveis embaixo
    public void Posicao_salva_so_vale_se_o_titulo_cair_numa_tela(int x, int y, bool ok)
        => Assert.Equal(ok, WindowPlacement.IsPositionVisible(new PixelPoint(x, y), new PixelSize(920, 640), TwoScreens));

    [Fact]
    public void Tamanho_salvo_fica_entre_o_minimo_e_a_maior_tela()
    {
        Assert.Equal(new Size(1000, 700), WindowPlacement.ClampSize(new Size(1000, 700), new Size(720, 480), new Size(1920, 1050)));
        Assert.Equal(new Size(720, 480), WindowPlacement.ClampSize(new Size(100, 50), new Size(720, 480), new Size(1920, 1050)));
        Assert.Equal(new Size(1920, 1050), WindowPlacement.ClampSize(new Size(5000, 3000), new Size(720, 480), new Size(1920, 1050)));
    }

    [AvaloniaFact]
    public void Janela_principal_restaura_e_grava_tamanho_e_maximizado()
    {
        ShellUi.NewHome();
        var host = Task.Run(() => AppHost.CreateAsync()).GetAwaiter().GetResult();
        CraftDriver.App.App.Host = host;
        host.Config.WindowWidth = 1000;
        host.Config.WindowHeight = 700;
        host.Config.WindowX = 100; // posição pode não existir no headless: o teste não depende dela
        host.Config.WindowY = 80;
        var vm = new MainViewModel(host);
        var w = new MainWindow { DataContext = vm };
        Assert.Equal(1000, w.Width);
        Assert.Equal(700, w.Height);
        w.Show();
        ShellUi.Pump(300);
        w.Width = 1100;
        w.Height = 750;
        ShellUi.Pump(300);
        w.SavePlacement();
        var saved = AppConfig.Load();
        Assert.Equal(1100, saved.WindowWidth);
        Assert.Equal(750, saved.WindowHeight);
        Assert.False(saved.WindowMaximized);

        w.WindowState = WindowState.Maximized;
        ShellUi.Pump(200);
        w.SavePlacement();
        saved = AppConfig.Load();
        Assert.True(saved.WindowMaximized);
        Assert.Equal(1100, saved.WindowWidth); // o tamanho normal não é trocado pelo maximizado
        w.Close();
        vm.Dispose();

        // nova janela com o config salvo: abre maximizada
        var vm2 = new MainViewModel(host);
        host.Config.WindowMaximized = true;
        var w2 = new MainWindow { DataContext = vm2 };
        Assert.Equal(WindowState.Maximized, w2.WindowState);
        Assert.Equal(1100, w2.Width);
        vm2.Dispose();
    }
}

/// <summary>Item 3: decisão de escala sob XWayland/Hyprland.</summary>
public class DisplayScalingTests
{
    static Func<string, string?> Env(params (string, string)[] vars)
    {
        var d = new Dictionary<string, string>(vars.Select(v => KeyValuePair.Create(v.Item1, v.Item2)));
        d.TryAdd("WAYLAND_DISPLAY", "wayland-1");
        d.TryAdd("DISPLAY", ":1");
        return k => d.TryGetValue(k, out var v) ? v : null;
    }

    static readonly HyprMonitor[] HiDpi = [new("eDP-1", 1.5), new("HDMI-A-1", 1)];

    [Fact]
    public void Force_zero_scaling_ativo_aplica_a_escala_de_cada_monitor()
    {
        var d = DisplayScaling.Decide(Env(), HiDpi, forceZeroScaling: true);
        Assert.Equal("eDP-1=1.5;HDMI-A-1=1", d.Set["AVALONIA_SCREEN_SCALE_FACTORS"]);
        Assert.Contains("force_zero_scaling", d.Note);
    }

    [Fact]
    public void Sem_force_zero_scaling_nao_mexe_e_explica_o_borrado()
    {
        var d = DisplayScaling.Decide(Env(), HiDpi, forceZeroScaling: false);
        Assert.Empty(d.Set); // escalar aqui somaria com a ampliação do compositor
        Assert.Contains("force_zero_scaling = true", d.Note);
        Assert.Empty(DisplayScaling.Decide(Env(), HiDpi, forceZeroScaling: null).Set);
    }

    [Theory]
    [InlineData("AVALONIA_SCREEN_SCALE_FACTORS", "eDP-1=2")]
    [InlineData("AVALONIA_GLOBAL_SCALE_FACTOR", "2")]
    [InlineData("QT_SCALE_FACTOR", "1.25")]
    [InlineData("QT_AUTO_SCREEN_SCALE_FACTOR", "1")]
    public void Variavel_do_usuario_prevalece(string var, string value)
    {
        var d = DisplayScaling.Decide(Env((var, value)), HiDpi, true);
        Assert.Empty(d.Set);
        Assert.Contains(var, d.Note);
    }

    [Fact]
    public void Qt_ignorado_quando_o_avalonia_ignora_qt()
    {
        var d = DisplayScaling.Decide(Env(("QT_SCALE_FACTOR", "2"), ("AVALONIA_SCREEN_SCALE_IGNORE_QT", "1")), HiDpi, true);
        Assert.True(d.Set.ContainsKey("AVALONIA_SCREEN_SCALE_FACTORS"));
    }

    [Fact]
    public void Nada_muda_fora_do_xwayland_sem_hyprland_escala_1_ou_desligado()
    {
        Assert.Empty(DisplayScaling.Decide(k => k == "DISPLAY" ? ":0" : null, HiDpi, true).Set); // X11 puro
        Assert.Empty(DisplayScaling.Decide(Env(), null, null).Set);                                  // não é Hyprland
        Assert.Empty(DisplayScaling.Decide(Env(), [new("eDP-1", 1), new("HDMI-A-1", 1)], true).Set);
        Assert.Empty(DisplayScaling.Decide(Env(("CRAFTDRIVER_AUTO_SCALE", "0")), HiDpi, true).Set);
    }

    [Fact]
    public async Task Le_monitores_e_opcao_do_hyprland_pelo_soquete()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cd-hypr-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ".socket.sock");
        using var server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        server.Bind(new UnixDomainSocketEndPoint(path));
        server.Listen(4);
        var received = new List<string>();
        var serve = Task.Run(() =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var c = server.Accept();
                var buf = new byte[1024];
                var cmd = Encoding.UTF8.GetString(buf, 0, c.Receive(buf));
                lock (received) received.Add(cmd);
                var reply = cmd == "j/monitors"
                    // formato real do Hyprland 0.5x (campos extras omitidos)
                    ? """[{"id":0,"name":"eDP-1","width":2880,"height":1800,"scale":1.80,"x":0,"y":0},{"id":1,"name":"HDMI-A-1","width":1920,"height":1080,"scale":1.00,"x":1600,"y":0}]"""
                    : """{"option": "xwayland:force_zero_scaling", "bool": true, "set": true }""";
                c.Send(Encoding.UTF8.GetBytes(reply));
                c.Shutdown(SocketShutdown.Both);
            }
        }, TestContext.Current.CancellationToken);
        var monitors = DisplayScaling.ParseMonitors(DisplayScaling.HyprRequest(path, "j/monitors"));
        var fz = DisplayScaling.ParseBoolOption(DisplayScaling.HyprRequest(path, "j/getoption xwayland:force_zero_scaling"));
        await serve.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(["j/monitors", "j/getoption xwayland:force_zero_scaling"], received);
        Assert.Equal([new HyprMonitor("eDP-1", 1.8), new HyprMonitor("HDMI-A-1", 1)], monitors);
        Assert.True(fz);
        Assert.False(DisplayScaling.ParseBoolOption("""{"option": "xwayland:force_zero_scaling", "int": 0, "set": false }"""));
        Assert.Equal("eDP-1=1.8;HDMI-A-1=1", DisplayScaling.Decide(Env(), monitors, fz).Set["AVALONIA_SCREEN_SCALE_FACTORS"]);
        Directory.Delete(dir, true);
    }
}
