using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CraftDriver.App.Services;
using CraftDriver.App.ViewModels;
using CraftDriver.App.Views;
using CraftDriver.Core.Config;
using CraftDriver.Core.Ipc;
using CraftDriver.Tests;

namespace CraftDriver.UiTests;

/// <summary>
/// CRAFTDRIVER_HOME é global ao processo e os outros testes o trocam em paralelo: estes (que dependem do
/// banco do host) rodam sozinhos, sem outra classe criando AppHost ao mesmo tempo.
/// </summary>
[CollectionDefinition("host-isolado", DisableParallelization = true)]
public sealed class HostIsoladoCollection;

/// <summary>IPC share/versions/trash do AppHost com a Graph falsa; área de transferência, navegador e notificações capturados.</summary>
[Collection("host-isolado")]
public class ShareVersionsHostTests
{
    internal sealed record Sent(string Title, string Body, NotificationAction[] Actions);

    internal sealed class Env : IDisposable
    {
        public readonly AppHost Host;
        public readonly FakeGraph Remote = new();
        public readonly ConcurrentQueue<Sent> Sent = new();
        public readonly List<string> Clipboard = [];
        public readonly List<string> Opened = [];
        public string? ClipboardError;
        public const string Drive = "OneDrive - Pessoal";

        public Env(string kind = "personal", string? webUrl = null)
        {
            var home = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "craftdriver-ui-home-" + Guid.NewGuid().ToString("N")[..6]);
            Environment.SetEnvironmentVariable("CRAFTDRIVER_HOME", home);
            Directory.CreateDirectory(System.IO.Path.Combine(home, "config"));
            File.WriteAllText(System.IO.Path.Combine(home, "config", "config.json"),
                $$"""{ "MountPoint": "{{home}}/OneDrive", "CacheDir": "{{home}}/cache" }""");
            Host = Task.Run(() => AppHost.CreateAsync()).GetAwaiter().GetResult();
            Host.Notify = (t, b, a) => Sent.Enqueue(new Sent(t, b, a));
            Host.CopyToClipboard = t => { Clipboard.Add(t); return Task.FromResult(ClipboardError); };
            Host.OpenUrl = Opened.Add;
            Host.GraphFactory = _ => Remote;
            Host.Config.Accounts.Add(new AccountConfig { Id = "a1", Username = "u@exemplo.com", Type = kind == "personal" ? "personal" : "business" });
            Host.Config.Drives.Add(new DriveConfig { DriveId = FakeGraph.DriveId, AccountId = "a1", DisplayName = Drive, Kind = kind, WebUrl = webUrl });
        }

        public void Start()
        {
            Host.Engine.Start();
            WaitUntil(() => Host.Engine.GetDrive(FakeGraph.DriveId)?.LastSync != null, "carga inicial");
        }

        /// <summary>Aplica as mudanças remotas (delta) e espera a fila esvaziar.</summary>
        public void Settle()
        {
            var d = Host.Engine.GetDrive(FakeGraph.DriveId)!;
            Host.Engine.SyncNow();
            WaitUntil(() => Host.Db.PendingOps() == 0 && !d.Busy, "fila vazia");
            d.RunDeltaAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public string Path(string rel) => System.IO.Path.Combine(Host.Config.MountPoint, Drive, rel);

        public Task<IpcResponse> Ipc(string cmd, params string[] args) => Host.HandleIpcAsync(new IpcRequest(cmd, args));

        public static void WaitUntil(Func<bool> cond, string what, int ms = 15000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (!cond())
            {
                if (DateTime.UtcNow > until) throw new TimeoutException("Esperando: " + what);
                Thread.Sleep(30);
            }
        }

        public void Dispose() => Host.Engine.Stop();
    }

    // ---------- share ----------

    [Fact]
    public async Task Share_copia_o_link_imprime_e_notifica_com_abrir_no_navegador()
    {
        using var env = new Env();
        var n = env.Remote.RemoteCreate("/", "foto.jpg", "x");
        env.Start();

        var resp = await env.Ipc("share", "--notify", env.Path("foto.jpg"));

        Assert.True(resp.Ok, resp.Text);
        var url = $"https://1drv.ms/fake/{n.Id}?view&anonymous";
        Assert.Equal([url], env.Clipboard);
        Assert.Contains(url, resp.Text);
        Assert.Contains("Link copiado para a área de transferência", resp.Text);
        var sent = Assert.Single(env.Sent);
        Assert.Equal("Link copiado", sent.Title);
        Assert.Contains("foto.jpg: qualquer pessoa com o link pode ver", sent.Body);
        Assert.Equal(["default", "open-link"], sent.Actions.Select(a => a.Id));
        sent.Actions.Single(a => a.Id == "open-link").OnInvoke();
        Assert.Equal([url], env.Opened);
    }

    [Fact]
    public async Task Share_edit_pede_link_de_edicao_e_sem_notify_nao_notifica()
    {
        using var env = new Env();
        var n = env.Remote.RemoteCreate("/", "planilha.xlsx", "x");
        env.Start();
        var resp = await env.Ipc("share", "--edit", env.Path("planilha.xlsx"));
        Assert.True(resp.Ok, resp.Text);
        Assert.Equal([(n.Id, "edit", "anonymous")], env.Remote.LinkRequests);
        Assert.Contains("pode editar", resp.Text);
        Assert.Empty(env.Sent);
    }

    [Fact]
    public async Task Share_corporativo_usa_organizacao_por_padrao_e_diz_o_escopo()
    {
        using var env = new Env(kind: "business");
        var n = env.Remote.RemoteCreate("/", "orçamento.xlsx", "x");
        env.Start();
        var resp = await env.Ipc("share", "--notify", env.Path("orçamento.xlsx"));
        Assert.True(resp.Ok, resp.Text);
        Assert.Equal([(n.Id, "view", "organization")], env.Remote.LinkRequests);
        Assert.Contains("orçamento.xlsx: pessoas da sua organização", resp.Text);
        Assert.Contains("orçamento.xlsx: pessoas da sua organização podem ver.", Assert.Single(env.Sent).Body);
        Assert.EndsWith("organization", Assert.Single(env.Clipboard));
    }

    [Fact]
    public async Task Share_corporativo_anyone_pede_qualquer_pessoa_e_recusa_da_erro_claro()
    {
        using var env = new Env(kind: "business");
        var n = env.Remote.RemoteCreate("/", "orçamento.xlsx", "x");
        env.Start();
        var ok = await env.Ipc("share", "--anyone", env.Path("orçamento.xlsx"));
        Assert.True(ok.Ok, ok.Text);
        Assert.Equal([(n.Id, "view", "anonymous")], env.Remote.LinkRequests);
        Assert.Contains("qualquer pessoa com o link", ok.Text);

        env.Remote.LinkRequests.Clear();
        env.Clipboard.Clear();
        env.Remote.RefuseAnonymousLinks = true;
        var resp = await env.Ipc("share", "--anyone", "--notify", env.Path("orçamento.xlsx"));
        Assert.False(resp.Ok, resp.Text);
        Assert.Contains("política da sua organização", resp.Text);
        Assert.Equal([(n.Id, "view", "anonymous")], env.Remote.LinkRequests); // não troca de escopo em silêncio
        Assert.Empty(env.Clipboard);
        Assert.Equal("CraftDriver — link não criado", Assert.Single(env.Sent).Title);
    }

    [Fact]
    public async Task Share_de_item_nao_enviado_falha_sem_tocar_na_area_de_transferencia()
    {
        using var env = new Env();
        env.Start();
        env.Remote.BeforeUpload = (_, ct) => Task.Delay(Timeout.Infinite, ct);
        Assert.Equal(0, env.Host.Fs.Create("/" + Env.Drive + "/novo.txt", 2, out var fh));
        env.Host.Fs.Release(fh);

        var resp = await env.Ipc("share", "--notify", env.Path("novo.txt"));
        Assert.False(resp.Ok);
        Assert.Contains("ainda não foi enviado", resp.Text);
        Assert.Empty(env.Clipboard);
        var sent = Assert.Single(env.Sent);
        Assert.Equal("CraftDriver — link não criado", sent.Title);
        Assert.Empty(sent.Actions);

        var outside = await env.Ipc("share", "/etc/passwd");
        Assert.False(outside.Ok);
        Assert.Contains("fora da pasta sincronizada", outside.Text);
    }

    [Fact]
    public async Task Share_sem_area_de_transferencia_ainda_mostra_o_link()
    {
        using var env = new Env();
        var n = env.Remote.RemoteCreate("/", "a.pdf", "x");
        env.Start();
        env.ClipboardError = "wl-copy não instalado (instale o pacote wl-clipboard)";
        var resp = await env.Ipc("share", "--notify", env.Path("a.pdf"));
        Assert.True(resp.Ok);
        Assert.Contains("Não foi possível copiar", resp.Text);
        var sent = Assert.Single(env.Sent);
        Assert.Equal("CraftDriver — link criado", sent.Title);
        Assert.Contains($"https://1drv.ms/fake/{n.Id}", sent.Body);
        Assert.Contains("wl-clipboard", sent.Body);
    }

    // ---------- versions ----------

    [Fact]
    public async Task Versions_com_janela_abre_a_janela_e_sem_janela_lista_no_terminal()
    {
        using var env = new Env();
        var n = env.Remote.RemoteCreate("/", "notas.md", "um");
        env.Remote.RemoteUpdate("/notas.md", "dois");
        env.Start();

        var headless = await env.Ipc("versions", env.Path("notas.md"));
        Assert.True(headless.Ok, headless.Text);
        Assert.Contains("[id 2.0]", headless.Text);
        Assert.Contains("(atual)", headless.Text);
        Assert.Contains("[id 1.0]", headless.Text);

        var requested = new List<long>();
        env.Host.VersionsRequested += requested.Add;
        var gui = await env.Ipc("versions", env.Path("notas.md"));
        Assert.True(gui.Ok);
        Assert.Contains("Abrindo as versões", gui.Text);
        Assert.Equal([env.Host.Db.GetByRemote(FakeGraph.DriveId, n.Id)!.Lid], requested);

        var list = await env.Ipc("versions", "--list", env.Path("notas.md"));
        Assert.Contains("[id 1.0]", list.Text);
        Assert.Single(requested);
    }

    [Fact]
    public async Task Versions_restore_e_copy_pelo_terminal()
    {
        using var env = new Env();
        env.Remote.RemoteCreate("/", "texto.txt", "primeiro");
        env.Remote.RemoteUpdate("/texto.txt", "segundo");
        env.Start();

        var copy = await env.Ipc("versions", env.Path("texto.txt"), "--copy=1.0");
        Assert.True(copy.Ok, copy.Text);
        Assert.Contains("(versão ", copy.Text);

        var restore = await env.Ipc("versions", env.Path("texto.txt"), "--restore=1.0");
        Assert.True(restore.Ok, restore.Text);
        Assert.Equal("primeiro", env.Remote.ReadText("/texto.txt"));

        var folder = await env.Ipc("versions", System.IO.Path.Combine(env.Host.Config.MountPoint, Env.Drive));
        Assert.False(folder.Ok);
        Assert.Contains("pasta", folder.Text);
    }

    // ---------- trash ----------

    [Fact]
    public async Task Trash_lista_restaura_e_abre_o_navegador()
    {
        using var env = new Env();
        env.Remote.RemoteCreate("/", "sumiu.txt", "x");
        env.Start();
        env.Remote.RemoteDelete("/sumiu.txt");
        env.Settle();

        var list = await env.Ipc("trash");
        Assert.True(list.Ok);
        var rec = Assert.Single(env.Host.Db.Trash());
        Assert.Contains("sumiu.txt", list.Text);
        Assert.Contains("craftdriver trash restore " + rec.Id, list.Text);

        var restored = await env.Ipc("trash", "restore", rec.Id.ToString());
        Assert.True(restored.Ok, restored.Text);
        Assert.Equal(1, env.Remote.TrashRestores);
        Assert.Empty(env.Host.Db.Trash());

        var open = await env.Ipc("trash", "open");
        Assert.True(open.Ok);
        Assert.Equal(["https://onedrive.live.com/?v=recyclebin"], env.Opened);

        var requested = 0;
        env.Host.TrashRequested += () => requested++;
        Assert.Contains("Abrindo a Lixeira", (await env.Ipc("trash")).Text);
        Assert.Equal(1, requested);
    }
}

/// <summary>Escolha do comando da área de transferência (sem tocar na área de transferência real).</summary>
public class ClipboardTests
{
    static Func<string, string?> Env(params (string, string)[] vars) => n => vars.FirstOrDefault(v => v.Item1 == n).Item2;

    [Fact]
    public void Wayland_usa_wl_copy_e_x11_usa_xclip_depois_xsel()
    {
        Assert.Equal(["wl-copy"], ClipboardService.Plan(Env(("WAYLAND_DISPLAY", "wayland-1"))).Select(c => c.Exe));
        Assert.Equal(["xclip", "xsel"], ClipboardService.Plan(Env(("DISPLAY", ":0"))).Select(c => c.Exe));
        Assert.Equal(["wl-copy", "xclip", "xsel"], ClipboardService.Plan(Env(("WAYLAND_DISPLAY", "w"), ("DISPLAY", ":0"))).Select(c => c.Exe));
        Assert.Empty(ClipboardService.Plan(Env()));
    }

    [Fact]
    public async Task Copia_pela_entrada_padrao_e_tenta_o_proximo_quando_falta_o_comando()
    {
        var runs = new List<(string, string[], string)>();
        var err = await ClipboardService.CopyAsync("https://link", Env(("DISPLAY", ":0")), (cmd, stdin) =>
        {
            runs.Add((cmd.Exe, cmd.Args, stdin));
            return Task.FromResult<int?>(cmd.Exe == "xclip" ? null : 0); // xclip não instalado
        });
        Assert.Null(err);
        Assert.Equal([("xclip", ["-selection", "clipboard", "-in"], "https://link"), ("xsel", ["--clipboard", "--input"], "https://link")],
            runs.Select(r => (r.Item1, r.Item2, r.Item3)).ToList(), new RunComparer());
    }

    sealed class RunComparer : IEqualityComparer<(string, string[], string)>
    {
        public bool Equals((string, string[], string) a, (string, string[], string) b) => a.Item1 == b.Item1 && a.Item2.SequenceEqual(b.Item2) && a.Item3 == b.Item3;
        public int GetHashCode((string, string[], string) o) => o.Item1.GetHashCode();
    }

    [Fact]
    public async Task Sem_comando_disponivel_explica_o_que_instalar()
    {
        var err = await ClipboardService.CopyAsync("x", Env(("WAYLAND_DISPLAY", "w")), (_, _) => Task.FromResult<int?>(null));
        Assert.Equal("wl-copy não instalado (instale o pacote wl-clipboard)", err);
        Assert.NotNull(await ClipboardService.CopyAsync("x", Env(), (_, _) => Task.FromResult<int?>(0)));
    }
}

/// <summary>Janelas Versões e Lixeira renderizadas offscreen (PNGs em $TMPDIR/craftdriver-ui/).</summary>
[Collection("host-isolado")]
public class VersionsWindowTests
{
    static readonly string Out = Environment.GetEnvironmentVariable("CRAFTDRIVER_UI_OUT") ?? Path.Combine(Path.GetTempPath(), "craftdriver-ui");

    /// <summary>Para o motor fora da thread de UI (Stop espera os laços, que podem estar presos num envio).</summary>
    sealed class Stopper(ShareVersionsHostTests.Env env) : IDisposable
    {
        public void Dispose() => ShellUi.Wait(Task.Run(env.Dispose), 30000);
    }

    static void Pump(Func<bool>? until = null, int ms = 400)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(until == null ? ms : 15000);
        while (DateTime.UtcNow < deadline && (until == null || !until()))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    static void Save(Window w, string name)
    {
        Directory.CreateDirectory(Out);
        Pump();
        w.CaptureRenderedFrame()!.Save(Path.Combine(Out, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    [AvaloniaFact]
    public void Janela_versoes_lista_restaura_e_baixa_copia()
    {
        var env = new ShareVersionsHostTests.Env();
        using var stop = new Stopper(env);
        CraftDriver.App.App.Host = env.Host;
        env.Remote.RemoteCreate("/", "Relatório trimestral.docx", "primeira versão do relatório");
        env.Remote.RemoteUpdate("/Relatório trimestral.docx", "segunda versão, revisada");
        env.Remote.RemoteUpdate("/Relatório trimestral.docx", "terceira (atual) com gráficos");
        ShellUi.Wait(Task.Run(env.Start), 30000); // motor fora da thread de UI (os laços não podem capturar o contexto dela)
        var item = env.Host.ItemFromLocalPath(env.Path("Relatório trimestral.docx"))!;

        var vm = new VersionsViewModel(env.Host, item.Lid);
        var w = new VersionsWindow(vm) { Width = 680, Height = 460 };
        w.Show();
        Pump(() => vm.Versions.Count == 3 && !vm.Busy);
        Assert.Equal(["Atual", "Versão 2.0", "Versão 1.0"], vm.Versions.Select(v => v.Label));
        Assert.Equal("Versão 2.0", vm.Selected?.Label); // a mais recente que não é a atual
        Save(w, "8-versoes");

        vm.Selected = vm.Versions[0];
        Assert.False(vm.RestoreCommand.CanExecute(null)); // restaurar a atual não faz sentido
        Assert.True(vm.DownloadCopyCommand.CanExecute(null));

        vm.Selected = vm.Versions.Single(v => v.Label == "Versão 1.0");
        vm.DownloadCopyCommand.Execute(null);
        Pump(() => !vm.Busy && vm.Status.StartsWith("Salvo"));
        Assert.Contains("Relatório trimestral (versão ", vm.Status);

        vm.Selected = vm.Versions.Single(v => v.Label == "Versão 1.0");
        vm.RestoreCommand.Execute(null);
        Pump(() => !vm.Busy && vm.Status.Contains("restaurada"));
        Assert.Equal("primeira versão do relatório", env.Remote.ReadText("/Relatório trimestral.docx"));
        Assert.Equal(4, vm.Versions.Count); // a restauração criou uma versão nova
        Save(w, "8b-versoes-restaurada");
        w.Close();
    }

    [AvaloniaFact]
    public void Janela_versoes_mostra_recusa_com_alteracoes_locais()
    {
        var env = new ShareVersionsHostTests.Env();
        using var stop = new Stopper(env);
        CraftDriver.App.App.Host = env.Host;
        env.Remote.RemoteCreate("/", "notas.txt", "v1");
        env.Remote.RemoteUpdate("/notas.txt", "v2");
        ShellUi.Wait(Task.Run(env.Start), 30000); // motor fora da thread de UI (os laços não podem capturar o contexto dela)
        env.Remote.BeforeUpload = (_, ct) => Task.Delay(Timeout.Infinite, ct);
        var path = "/" + ShareVersionsHostTests.Env.Drive + "/notas.txt";
        // fora da thread de UI: o Open baixa o conteúdo esperando de forma síncrona (como o FUSE)
        Task.Run(() =>
        {
            Assert.Equal(0, env.Host.Fs.Open(path, 2 | 0x200 /*O_TRUNC*/, 0, out var fh));
            env.Host.Fs.Write(fh, "edição local"u8, 0);
            env.Host.Fs.Release(fh);
        }).Wait();

        var vm = new VersionsViewModel(env.Host, env.Host.Fs.Resolve(path)!.Value.Item!.Lid);
        var w = new VersionsWindow(vm) { Width = 680, Height = 460 };
        w.Show();
        Pump(() => vm.Versions.Count == 2 && !vm.Busy);
        vm.RestoreCommand.Execute(null);
        Pump(() => !vm.Busy && vm.Status.Contains("ainda não foram enviadas"));
        Assert.Contains("ainda não foram enviadas", vm.Status);
        Assert.Equal(0, env.Remote.VersionRestores);
        Save(w, "8c-versoes-recusa");
        w.Close();
    }

    [AvaloniaFact]
    public void Janela_lixeira_lista_e_restaura()
    {
        var env = new ShareVersionsHostTests.Env();
        using var stop = new Stopper(env);
        CraftDriver.App.App.Host = env.Host;
        env.Remote.RemoteCreate("/", "Fotos da viagem", null, folder: true);
        env.Remote.RemoteCreate("/", "orçamento antigo.xlsx", "x");
        ShellUi.Wait(Task.Run(env.Start), 30000); // motor fora da thread de UI (os laços não podem capturar o contexto dela)
        env.Remote.RemoteDelete("/orçamento antigo.xlsx");
        env.Remote.RemoteDelete("/Fotos da viagem");
        ShellUi.Wait(Task.Run(env.Settle), 30000); // fora da thread de UI (espera síncrona do delta)

        var vm = new TrashViewModel(env.Host);
        var w = new TrashWindow(vm) { Width = 720, Height = 460 };
        w.Show();
        vm.Reload();
        Pump();
        Assert.Equal(["Fotos da viagem/", "orçamento antigo.xlsx"], vm.Items.Select(i => i.Name).OrderBy(x => x));
        vm.Selected = vm.Items.Single(i => i.Name == "orçamento antigo.xlsx");
        Save(w, "9-lixeira");
        vm.RestoreCommand.Execute(null);
        Pump(() => !vm.Busy && vm.Status.Contains("restaurado"));
        Assert.Equal(1, env.Remote.TrashRestores);
        Assert.Single(vm.Items);
        w.Close();
    }
}
