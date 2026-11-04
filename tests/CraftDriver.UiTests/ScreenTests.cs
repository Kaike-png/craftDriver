using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CraftDriver.App.Services;
using CraftDriver.App.ViewModels;
using CraftDriver.App.Views;
using CraftDriver.Core.Config;
using CraftDriver.Core.Sync;

[assembly: AvaloniaTestApplication(typeof(CraftDriver.UiTests.TestApp))]

namespace CraftDriver.UiTests;

public class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<CraftDriver.App.App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Renderiza todas as telas offscreen (sem montar nada) e salva PNGs para inspeção.</summary>
public class ScreenTests
{
    static readonly string Out = Environment.GetEnvironmentVariable("ODL_UI_OUT") ?? Path.Combine(Path.GetTempPath(), "odl-ui");

    static AppHost NewHost()
    {
        var home = Path.Combine(Path.GetTempPath(), "odl-ui-home-" + Guid.NewGuid().ToString("N")[..6]);
        Environment.SetEnvironmentVariable("CRAFTDRIVER_HOME", home);
        Directory.CreateDirectory(Path.Combine(home, "config"));
        File.WriteAllText(Path.Combine(home, "config", "config.json"),
            $$"""{ "MountPoint": "{{home}}/OneDrive", "CacheDir": "{{home}}/cache" }""");
        var host = AppHost.CreateAsync().GetAwaiter().GetResult();
        CraftDriver.App.App.Host = host;
        return host;
    }

    static void Pump(int ms = 300)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
    }

    static void Save(Window w, string name)
    {
        Directory.CreateDirectory(Out);
        Pump();
        w.CaptureRenderedFrame()!.Save(Path.Combine(Out, name + ".png"));
    }

    [AvaloniaFact]
    public void Janela_principal_todas_as_abas()
    {
        var host = NewHost();
        var c = host.Config;
        c.Accounts.Add(new AccountConfig { Id = "a1", Username = "usuario@empresa.exemplo", Type = "business" });
        c.Accounts.Add(new AccountConfig { Id = "a2", Username = "usuario@exemplo.com", Type = "personal" });
        c.Drives.Add(new DriveConfig { DriveId = "d1", AccountId = "a2", DisplayName = "OneDrive - Pessoal", Kind = "personal" });
        c.Drives.Add(new DriveConfig { DriveId = "d2", AccountId = "a1", DisplayName = "OneDrive - empresa", Kind = "business" });
        c.Drives.Add(new DriveConfig { DriveId = "d3", AccountId = "a1", DisplayName = "Financeiro - Documentos", Kind = "documentLibrary", SiteName = "Financeiro", WebUrl = "https://empresa.sharepoint.com/sites/Financeiro" });
        host.Activity.Add(ActivityKind.Download, "Baixado: Relatório anual.pdf");
        host.Activity.Add(ActivityKind.Upload, "Enviado: planilha de custos.xlsx");
        host.Activity.Add(ActivityKind.Conflict, "Conflito de edição em 'notas.md': sua versão foi salva como 'notas (conflito - notebook 2026-11-04 153000).md'");
        var t = host.Activity.StartTransfer("video-reuniao.mp4", 300_000_000, upload: false);
        t.Done = 120_000_000;

        var vm = new MainViewModel(host);
        var w = new MainWindow { DataContext = vm, Width = 920, Height = 640 };
        w.Show();
        Pump(1200);
        var tabs = w.GetVisualDescendants().OfType<TabControl>().First();
        string[] names = ["1-visao-geral", "2-contas", "3-onedrive-sharepoint", "4-sob-demanda", "5-configuracoes"];
        for (var i = 0; i < names.Length; i++)
        {
            tabs.SelectedIndex = i;
            Save(w, names[i]);
        }
        Assert.Equal(3, vm.Drives.Count);
        Assert.Equal(2, vm.Accounts.Count);
        Assert.Single(vm.Transfers);
        w.Close();
    }

    [AvaloniaFact]
    public void Login_pessoal_automatico_recebe_retorno_em_localhost()
    {
        var host = NewHost();
        var login = new LoginWindow(host) { Width = 580, Height = 520 };
        login.Show();
        _ = login.StartForTestAsync(personal: true);
        var panel = login.FindControl<Border>("AutoPanel")!;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while ((!panel.IsVisible || login.AutoAuthorizationUri == null) && DateTime.UtcNow < deadline) Pump(200);
        Save(login, "6b-login-pessoal-auto");
        var uri = login.AutoAuthorizationUri!;
        File.WriteAllText(Path.Combine(Out, "auto-url.txt"), uri.AbsoluteUri);

        var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var redirect = new Uri(q["redirect_uri"]!);
        Assert.Equal("localhost", redirect.Host);
        Assert.True(redirect.Port > 0);
        Assert.Contains("/consumers/", uri.AbsolutePath);
        Assert.DoesNotContain("Sites.ReadWrite", q["scope"]);

        // simula a Microsoft redirecionando o navegador para o app (código falso): o app precisa
        // capturar sozinho e responder com a página dele
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        // fora da thread de UI (o MSAL continua o fluxo no contexto dela)
        var req = Task.Run(async () =>
        {
            // response_mode=form_post: a Microsoft faz o navegador enviar um POST com code/state
            Assert.Equal("form_post", q["response_mode"]);
            var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = "codigo-falso", ["state"] = q["state"]! });
            var r = await http.PostAsync(redirect.GetLeftPart(UriPartial.Path) + "/", form);
            return (r, await r.Content.ReadAsStringAsync());
        });
        while (!req.IsCompleted && DateTime.UtcNow < deadline.AddSeconds(20)) Pump(100);
        var (resp, body) = req.GetAwaiter().GetResult();
        Assert.Contains("craftDriver", body);

        // a troca do código falso falha na Microsoft e o diálogo mostra o erro
        var status = login.FindControl<TextBlock>("StatusText")!;
        deadline = DateTime.UtcNow.AddSeconds(20);
        while (!(status.Text ?? "").StartsWith("Não foi possível") && DateTime.UtcNow < deadline) Pump(200);
        Save(login, "6d-login-auto-erro");
        Assert.StartsWith("Não foi possível entrar", status.Text);
        login.Close();
    }

    [AvaloniaFact]
    public void Login_pessoal_gera_url_de_autorizacao_com_redirect_nativeclient()
    {
        var host = NewHost();
        var login = new LoginWindow(host) { Width = 580, Height = 560 };
        login.Show();
        Pump(300);
        Save(login, "6a-login-escolha");
        _ = login.StartForTestAsync(personal: true, manual: true);
        var panel = login.FindControl<Border>("BrowserPanel")!;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while ((!panel.IsVisible || login.AuthorizationUri == null) && DateTime.UtcNow < deadline) Pump(200);
        Save(login, "6b-login-pessoal");
        var uri = login.AuthorizationUri?.AbsoluteUri ?? "";
        File.WriteAllText(Path.Combine(Out, "auth-url.txt"), uri);

        // endereço inválido mostra erro; o fluxo continua aguardando
        login.FindControl<TextBox>("PasteBox")!.Text = "https://exemplo.com/?code=abc";
        login.FindControl<Button>("FinishBrowser")!.Command?.Execute(null);
        Pump(200);
        login.Close();

        Assert.True(panel.IsVisible);
        Assert.Contains("login.microsoftonline.com/consumers/oauth2/v2.0/authorize", uri);
        Assert.Contains("redirect_uri=https%3A%2F%2Flogin.microsoftonline.com%2Fcommon%2Foauth2%2Fnativeclient", uri);
        Assert.Contains("code_challenge=", uri);
        Assert.DoesNotContain("Sites.ReadWrite", uri);
    }

    [AvaloniaFact]
    public void Login_trabalho_usa_codigo_de_dispositivo()
    {
        var host = NewHost();
        var login = new LoginWindow(host) { Width = 580, Height = 480 };
        login.Show();
        _ = login.StartForTestAsync(personal: false);
        var panel = login.FindControl<Border>("CodePanel")!;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!panel.IsVisible && DateTime.UtcNow < deadline) Pump(200);
        Save(login, "6c-login-trabalho");
        var url = login.FindControl<TextBlock>("UrlText")!.Text;
        login.Close();
        Assert.True(panel.IsVisible, "o código de dispositivo não chegou");
        Assert.Contains("login.microsoft.com/device", url);
    }

    [AvaloniaFact]
    public void Alerta_de_exclusao_em_massa()
    {
        Environment.SetEnvironmentVariable("ONEDRIVE_LINUX_NO_NOTIFY", "1");
        var host = NewHost();
        host.Config.Accounts.Add(new AccountConfig { Id = "a2", Username = "usuario@exemplo.com", Type = "personal" });
        host.Config.Drives.Add(new DriveConfig { DriveId = "d1", AccountId = "a2", DisplayName = "OneDrive - Pessoal", Kind = "personal" });
        host.Engine.Start();
        host.Engine.RecordLocalDeletes("d1", 1989);
        var vm = new MainViewModel(host);
        var w = new MainWindow { DataContext = vm, Width = 920, Height = 640 };
        w.Show();
        Pump(1500);
        Save(w, "8-exclusao-em-massa");
        Assert.True(vm.HasDeleteAlerts);
        Assert.Contains("1989 itens", vm.DeleteAlerts[0].Text);
        vm.DeleteAlerts[0].ConfirmCommand.Execute(null);
        Pump(300);
        Assert.False(vm.HasDeleteAlerts);
        w.Close();
        host.Engine.Stop();
    }

    [AvaloniaFact]
    public void Dialogo_de_biblioteca_sem_conta_corporativa()
    {
        var host = NewHost();
        var dlg = new AddLibraryWindow(host, null);
        dlg.Show();
        Pump(500);
        Save(dlg, "7-sharepoint");
        Assert.Contains("corporativa", dlg.FindControl<TextBlock>("StatusText")!.Text);
        dlg.Close();
    }
}
