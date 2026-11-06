using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
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
// CRAFTDRIVER_HOME é global ao processo: fora da coleção, dois AppHost abriam o mesmo banco ao mesmo tempo
// ("duplicate column name" na migração, visto no CI)
[Collection("host-isolado")]
public class ScreenTests
{
    static readonly string Out = Environment.GetEnvironmentVariable("CRAFTDRIVER_UI_OUT") ?? Path.Combine(Path.GetTempPath(), "craftdriver-ui");

    static AppHost NewHost()
    {
        var home = Path.Combine(Path.GetTempPath(), "craftdriver-ui-home-" + Guid.NewGuid().ToString("N")[..6]);
        Environment.SetEnvironmentVariable("CRAFTDRIVER_HOME", home);
        Directory.CreateDirectory(Path.Combine(home, "config"));
        File.WriteAllText(Path.Combine(home, "config", "config.json"),
            $$"""{ "MountPoint": "{{home}}/OneDrive", "CacheDir": "{{home}}/cache" }""");
        // fora do contexto da UI, como em Program.Main (antes de o Avalonia iniciar)
        var host = Task.Run(() => AppHost.CreateAsync()).GetAwaiter().GetResult();
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
        w.CaptureRenderedFrame()!.Save(Path.Combine(Out, name + ".png"), PngBitmapEncoderOptions.Default);
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
        string[] names = ["1-visao-geral", "2-contas", "3-onedrive-sharepoint", "4-sob-demanda", "4b-problemas", "4d-conflitos", "5-configuracoes"];
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
            // no redirect_uri exato: com uma barra a mais o MSAL recusava antes da troca do código
            // (loopback_response_uri_mismatch) e o teste passava sem chegar ao endpoint de token
            var r = await http.PostAsync(redirect, form);
            return (r, await r.Content.ReadAsStringAsync());
        });
        while (!req.IsCompleted && DateTime.UtcNow < deadline.AddSeconds(20)) Pump(100);
        var (resp, body) = req.GetAwaiter().GetResult();
        Assert.Contains("craftDriver", body);

        // a troca do código falso é recusada pela Microsoft (falsa, em memória: FakeMicrosoftLogin) e o
        // diálogo mostra o erro
        var status = login.FindControl<TextBlock>("StatusText")!;
        deadline = DateTime.UtcNow.AddSeconds(20);
        while (!(status.Text ?? "").StartsWith("Não foi possível") && DateTime.UtcNow < deadline) Pump(200);
        Save(login, "6d-login-auto-erro");
        Assert.StartsWith("Não foi possível entrar", status.Text);
        login.Close();
        File.WriteAllText(Path.Combine(Out, "msal-falsa-auto.txt"), FakeMicrosoftLogin.Instance.Dump() + "\nerro: " + login.LastError);
        // o código recebido no localhost foi mesmo trocado no endpoint de token de contas pessoais (sem rede,
        // "Não foi possível" também apareceria — por falha de conexão, sem provar a troca)
        Assert.Contains(FakeMicrosoftLogin.Instance.Requests, r => r.Method == "POST"
            && r.Uri.AbsolutePath == "/consumers/oauth2/v2.0/token"
            && r.Body.Contains("grant_type=authorization_code") && r.Body.Contains("code=codigo-falso")
            && r.Body.Contains("code_verifier="));
        Assert.Contains("AADSTS70000", login.LastError?.Detail); // a recusa veio da troca, não de outro erro
        Assert.Empty(FakeMicrosoftLogin.Instance.Blocked);
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
        Assert.Empty(FakeMicrosoftLogin.Instance.Blocked);
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
        var code = login.FindControl<TextBlock>("CodeText")!.Text;
        login.Close();
        File.WriteAllText(Path.Combine(Out, "msal-falsa-dispositivo.txt"), FakeMicrosoftLogin.Instance.Dump());
        Assert.True(panel.IsVisible, "o código de dispositivo não chegou");
        Assert.Contains("login.microsoft.com/device", url);
        // código vindo da Microsoft falsa (FakeMicrosoftLogin), com os escopos de trabalho/escola
        Assert.Equal(FakeMicrosoftLogin.UserCode, code);
        Assert.Contains(FakeMicrosoftLogin.Instance.Requests, r => r.Method == "POST"
            && r.Uri.AbsolutePath.EndsWith("/oauth2/v2.0/devicecode") && r.Body.Contains("Sites.ReadWrite.All"));
        Assert.Empty(FakeMicrosoftLogin.Instance.Blocked);
    }

    [AvaloniaFact]
    public void Alerta_de_exclusao_em_massa()
    {
        Environment.SetEnvironmentVariable("CRAFTDRIVER_NO_NOTIFY", "1");
        var host = NewHost();
        host.Config.Accounts.Add(new AccountConfig { Id = "a2", Username = "usuario@exemplo.com", Type = "personal" });
        host.Config.Drives.Add(new DriveConfig { DriveId = "d1", AccountId = "a2", DisplayName = "OneDrive - Pessoal", Kind = "personal" });
        host.Engine.Start();
        // o que está retido: uma pasta com 30 arquivos dentro e 25 arquivos soltos
        host.Db.Enqueue("d1", CraftDriver.Core.State.OpKind.Delete, 1, "rFotos", null, "Fotos", isDir: true);
        for (var i = 0; i < 30; i++) host.Db.Enqueue("d1", CraftDriver.Core.State.OpKind.Delete, 0, $"rf{i}", "rFotos", $"f{i}.jpg");
        for (var i = 0; i < 25; i++) host.Db.Enqueue("d1", CraftDriver.Core.State.OpKind.Delete, 0, $"rs{i}", null, $"solto{i:D2}.txt");
        host.Engine.RecordLocalDeletes("d1", 1989);
        var vm = new MainViewModel(host);
        var w = new MainWindow { DataContext = vm, Width = 920, Height = 640 };
        w.Show();
        Pump(1500);
        foreach (var e in w.GetVisualDescendants().OfType<Expander>()) e.IsExpanded = true;
        Save(w, "8-exclusao-em-massa");
        Assert.True(vm.HasDeleteAlerts);
        Assert.Contains("1989 itens", vm.DeleteAlerts[0].Text);
        var alert = vm.DeleteAlerts[0];
        Assert.Equal(20, alert.Items.Count);
        Assert.Equal(new DeletedEntryVm("folder", "/Fotos  (31 itens)"), alert.Items[0]);
        Assert.Equal(new DeletedEntryVm("file", "/solto00.txt"), alert.Items[1]);
        Assert.Equal("… e mais 6", alert.More);
        vm.DeleteAlerts[0].ConfirmCommand.Execute(null);
        Pump(300);
        Assert.False(vm.HasDeleteAlerts);
        w.Close();
        host.Engine.Stop();
    }

    [AvaloniaFact]
    public void Aba_conflitos_e_cancelar_transferencia()
    {
        var host = NewHost();
        host.Config.Accounts.Add(new AccountConfig { Id = "a2", Username = "usuario@exemplo.com", Type = "personal" });
        host.Config.Drives.Add(new DriveConfig { DriveId = "d1", AccountId = "a2", DisplayName = "OneDrive - Pessoal", Kind = "personal" });
        host.Engine.ReloadDrives();
        host.Db.AddConflict("d1", 999, "notas.md", "notas (conflito - notebook 2026-11-05 101500).md",
            "edição nos dois lados (a versão da nuvem ficou com o nome original)");
        var t = host.Activity.StartTransfer("video-reuniao.mp4", 300_000_000, upload: false);
        t.Done = 100_000_000;
        Thread.Sleep(1100);
        t.Done = 120_000_000; // ~20 MB/s

        var vm = new MainViewModel(host);
        var w = new MainWindow { DataContext = vm, Width = 920, Height = 640 };
        w.Show();
        Pump(800);
        Assert.Equal("Conflitos (1)", vm.ConflictsHeader);
        var c = Assert.Single(vm.Conflicts);
        Assert.Equal("notas (conflito - notebook 2026-11-05 101500).md", c.Path); // item não existe mais: mostra o nome
        Assert.Contains("MB/s", vm.Transfers[0].Detail);
        Assert.Contains("faltam", vm.Transfers[0].Detail);
        var tabs = w.GetVisualDescendants().OfType<TabControl>().First();
        tabs.SelectedIndex = 0;
        Save(w, "1b-transferencia-com-velocidade");
        tabs.SelectedIndex = 5;
        Save(w, "4e-conflitos-com-item");

        vm.Transfers[0].CancelCommand.Execute(null);
        Assert.True(t.Cancelled);
        Assert.True(vm.Transfers[0].Cancelling); // botão desabilitado, "cancelando…" até a transferência sair
        c.DismissCommand.Execute(null);
        Assert.Empty(vm.Conflicts);
        Assert.Equal("Conflitos", vm.ConflictsHeader);
        Assert.Empty(host.Db.Conflicts());
        w.Close();
        host.Engine.Stop();
    }

    [AvaloniaFact]
    public void Configuracoes_de_rede_valem_sem_reiniciar()
    {
        var host = NewHost();
        host.Config.Accounts.Add(new AccountConfig { Id = "a2", Username = "usuario@exemplo.com", Type = "personal" });
        host.Config.Drives.Add(new DriveConfig { DriveId = "d1", AccountId = "a2", DisplayName = "OneDrive - Pessoal", Kind = "personal" });
        host.Engine.ReloadDrives();
        var vm = new MainViewModel(host);
        Assert.Equal(4, vm.DownloadConcurrency);
        Assert.Equal(0, vm.DownloadLimitMBps);

        vm.DownloadConcurrency = 2;
        vm.DownloadLimitMBps = 1.5m;
        vm.UploadLimitMBps = 0.25m;
        ShellUi.Wait(vm.SaveSettingsCommand.ExecuteAsync(null));

        Assert.Equal(2, host.Config.DownloadConcurrency);
        Assert.Equal(1_500_000, host.Config.DownloadBytesPerSecond);
        Assert.Equal(250_000, host.Config.UploadBytesPerSecond);
        Assert.Equal(1_500_000, host.Engine.GetDrive("d1")!.DownloadBytesPerSecond); // drive já rodando
        Assert.Equal(1_500_000, AppConfig.Load().DownloadBytesPerSecond);
        host.Engine.Stop();
    }

    [AvaloniaFact]
    public async Task Escolher_pastas_exclui_reinclui_e_mostra_recusa()
    {
        var host = NewHost();
        host.Config.Accounts.Add(new AccountConfig { Id = "a2", Username = "usuario@exemplo.com", Type = "personal" });
        host.Config.Drives.Add(new DriveConfig { DriveId = "d1", AccountId = "a2", DisplayName = "OneDrive - Pessoal", Kind = "personal" });
        // árvore direto no banco (sem rede): raiz, Fotos/2019, Docs (com um arquivo ainda não enviado)
        var db = host.Db;
        long Add(long parent, string name, bool dir, bool dirty = false) => db.Insert(new CraftDriver.Core.State.Item
        {
            DriveId = "d1", ParentLid = parent, Name = name, IsDir = dir, ItemId = dirty ? null : "r" + name, Dirty = dirty,
        });
        var root = Add(0, "", true);
        db.SaveDrive(new CraftDriver.Core.State.DriveState("d1", root, "delta", 0, 0));
        var fotos = Add(root, "Fotos", true);
        var y2019 = Add(fotos, "2019", true);
        var docs = Add(root, "Docs", true);
        Add(docs, "rascunho.txt", false, dirty: true);
        host.Engine.ReloadDrives();
        Assert.Null(host.Engine.SetExcluded(y2019, true)); // excluída num nível abaixo: aparece na lista também

        var vm = new MainViewModel(host);
        ChooseFoldersViewModel? opened = null;
        vm.ChooseFoldersRequested += f => opened = f;
        vm.Drives.Single().ChooseFoldersCommand.Execute(null);
        Assert.NotNull(opened);
        Assert.Equal(["/Docs", "/Fotos", "/Fotos/2019"], opened.Folders.Select(f => f.Path));
        Assert.Equal([true, true, false], opened.Folders.Select(f => f.Sync));

        var w = new ChooseFoldersWindow(opened) { Width = 560, Height = 520 };
        w.Show();
        opened.Folders.Single(f => f.Path == "/Docs").Sync = false; // recusa: alteração não enviada
        opened.Folders.Single(f => f.Path == "/Fotos").Sync = false;
        var closed = false;
        opened.CloseRequested += () => closed = true;
        opened.SaveCommand.Execute(null);
        Pump(300);
        Save(w, "9-escolher-pastas");
        Assert.False(closed);
        Assert.Contains("/Docs", opened.Message);
        Assert.Contains("não enviad", opened.Message);
        Assert.True(opened.Folders.Single(f => f.Path == "/Docs").Sync); // voltou a marcada
        Assert.True(db.IsExcluded(fotos));
        Assert.False(db.IsExcluded(docs));
        w.Close();

        // reincluir pelo terminal com o caminho da pasta escondida
        var mount = host.Config.MountPoint;
        var resp = await host.HandleIpcAsync(new("include", [Path.Combine(mount, "OneDrive - Pessoal", "Fotos")]));
        Assert.True(resp.Ok, resp.Text);
        Assert.False(db.IsExcluded(fotos));
        Assert.True(db.IsExcluded(y2019));
        Assert.False((await host.HandleIpcAsync(new("exclude", [Path.Combine(mount, "OneDrive - Pessoal", "Docs")]))).Ok);
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

    [AvaloniaFact]
    public async Task Login_pelo_terminal_sem_tokens_nao_altera_config()
    {
        var host = NewHost();
        var payload = System.Text.Json.JsonSerializer.Serialize(new CraftDriver.Core.Ipc.AddAccountRequest(
            new AccountConfig { Id = "inexistente", Username = "x@exemplo.com", Type = "personal" }, "d9", null));
        var resp = await host.HandleIpcAsync(new("add-account", [payload]));
        Assert.False(resp.Ok);
        Assert.Contains("tokens", resp.Text);
        Assert.Empty(host.Config.Accounts);
        Assert.False((await host.HandleIpcAsync(new("add-account", ["{}"]))).Ok);
    }

    [AvaloniaFact]
    public void Conta_do_terminal_entra_na_instancia_aberta_e_sobrevive_ao_salvar()
    {
        var host = NewHost();
        host.RunOnMain = a => Dispatcher.UIThread.InvokeAsync(a).GetTask();
        var vm = new MainViewModel(host);
        Assert.Empty(vm.Accounts);

        // o IPC chega numa thread do pool, como no IpcServer
        var acc = new AccountConfig { Id = "a3", Username = "nova@exemplo.com", Type = "personal" };
        var done = Task.Run(() => host.RunOnMain(() => host.RegisterAccount(acc, "d3", null)));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done.IsCompleted && DateTime.UtcNow < deadline) Pump(50);
        Pump(100);

        Assert.Single(vm.Accounts);
        Assert.Equal("nova@exemplo.com", vm.Accounts[0].Username);
        // o bug: a instância regravava o config antigo (ex.: TogglePause/Sair) e a conta sumia
        host.Config.Save();
        var disk = AppConfig.Load();
        Assert.Contains(disk.Accounts, a => a.Id == "a3");
        Assert.Contains(disk.Drives, d => d.DriveId == "d3" && d.DisplayName == "OneDrive - Pessoal");
        host.Engine.Stop();
    }

    [AvaloniaFact]
    public void Aba_problemas_lista_e_devolve_falha_a_fila()
    {
        var host = NewHost();
        host.Config.Accounts.Add(new AccountConfig { Id = "a2", Username = "usuario@exemplo.com", Type = "personal" });
        host.Config.Drives.Add(new DriveConfig { DriveId = "d1", AccountId = "a2", DisplayName = "OneDrive - Pessoal", Kind = "personal" });
        host.Engine.ReloadDrives();
        host.Db.Enqueue("d1", CraftDriver.Core.State.OpKind.Upload, 42, name: "planilha:custos.xlsx");
        host.Db.FailPermanently(host.Db.PeekOp("d1")!, "O nome contém caracteres inválidos");

        var vm = new MainViewModel(host);
        var w = new MainWindow { DataContext = vm, Width = 920, Height = 640 };
        w.Show();
        Pump(600);
        Assert.True(vm.HasFailures);
        Assert.Equal("Problemas (1)", vm.FailuresHeader);
        var f = Assert.Single(vm.Failures);
        Assert.Equal("planilha:custos.xlsx", f.Path);
        Assert.Contains("Problemas (1)", host.StatusSummary());

        var tabs = w.GetVisualDescendants().OfType<TabControl>().First();
        tabs.SelectedIndex = 4;
        Save(w, "4c-problemas-com-item");

        f.RetryCommand.Execute(null);
        Pump(100);
        Assert.False(vm.HasFailures);
        Assert.Equal(1, host.Db.PendingOps("d1"));
        w.Close();
        host.Engine.Stop();
    }

    [Fact]
    public void Script_do_nautilus_passa_nomes_com_espacos_e_curingas_intactos()
    {
        var dir = Path.Combine(Path.GetTempPath(), "craftdriver-script-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "casa.txt"), ""); // casaria com o curinga se ele fosse expandido
            var script = Path.Combine(dir, "s.sh");
            File.WriteAllText(script, DesktopIntegration.ScriptText("pin").Replace(DesktopIntegration.ExecCommand, "printf '[%s]\\n'"));
            string Run(string args, string? env = null)
            {
                var psi = new System.Diagnostics.ProcessStartInfo("sh", ["-c", $"{(env != null ? $"NAUTILUS_SCRIPT_SELECTED_FILE_PATHS='{env}' " : "")}sh '{script}' {args}"])
                { RedirectStandardOutput = true, WorkingDirectory = dir };
                using var p = System.Diagnostics.Process.Start(psi)!;
                var o = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return o.Trim();
            }
            Assert.Equal("[pin]\n[--notify]\n[meu arquivo.txt]\n[c*.txt]", Run("'meu arquivo.txt' 'c*.txt'"));
            Assert.Equal("[pin]\n[--notify]\n[/a/meu arquivo.txt]\n[/a/c*.txt]", Run("", "/a/meu arquivo.txt\n/a/c*.txt"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
