using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using CraftDriver.App.Services;
using CraftDriver.App.ViewModels;
using CraftDriver.App.Views;
using CraftDriver.Core.Config;
using CraftDriver.Core.Sync;
using CraftDriver.Tests;
using Microsoft.Data.Sqlite;

namespace CraftDriver.UiTests;

/// <summary>PENDENCIAS §18 (refinamentos da interface) e §17 (limpezas do App).</summary>
[Collection("host-isolado")]
public class Secao18UiTests
{
    static AppHost NewHost()
    {
        ShellUi.NewHome();
        var host = Task.Run(() => AppHost.CreateAsync()).GetAwaiter().GetResult();
        CraftDriver.App.App.Host = host;
        host.GraphFactory = _ => new FakeGraph();
        host.Notify = (_, _, _) => { };
        return host;
    }

    static SqliteConnection Sql()
    {
        var c = new SqliteConnection("Data Source=" + AppPaths.DatabaseFile + ";Pooling=False");
        c.Open();
        return c;
    }

    static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static MainWindow Show(MainViewModel vm, int tab)
    {
        var w = new MainWindow { DataContext = vm, Width = 920, Height = 640 };
        w.Show();
        vm.SelectedTab = tab;
        ShellUi.Pump(300);
        return w;
    }

    // ---------- Ctrl+Q ----------

    [AvaloniaFact]
    public void Sair_com_transferencia_em_andamento_pede_confirmacao()
    {
        var host = NewHost();
        var vm = new MainViewModel(host);
        ConfirmRequest? asked = null;
        var answer = false;
        vm.ConfirmRequested += r => { asked = r; return Task.FromResult(answer); };
        var quits = 0;
        vm.QuitRequested += () => quits++;

        ShellUi.Wait(vm.QuitCommand.ExecuteAsync(null)); // sem transferência: sai direto
        Assert.Null(asked);
        Assert.Equal(1, quits);

        var t = host.Activity.StartTransfer("grande.iso", 1000, upload: true);
        ShellUi.Wait(vm.QuitCommand.ExecuteAsync(null));
        Assert.NotNull(asked);
        Assert.Contains("transferência", asked!.Text);
        Assert.Equal(1, quits); // cancelou
        answer = true;
        ShellUi.Wait(vm.QuitCommand.ExecuteAsync(null));
        Assert.Equal(2, quits);
        host.Activity.EndTransfer(t);
        vm.Dispose();
    }

    // ---------- listas ----------

    [AvaloniaFact]
    public void Listas_de_contas_e_de_fixados_rolam()
    {
        var host = NewHost();
        var vm = new MainViewModel(host);
        foreach (var (tab, source) in new (int, object)[] { (1, vm.Accounts), (3, vm.Pinned) })
        {
            var w = Show(vm, tab);
            var list = w.GetVisualDescendants().OfType<ItemsControl>().Single(i => ReferenceEquals(i.ItemsSource, source));
            Assert.NotNull(list.FindAncestorOfType<ScrollViewer>());
            w.Close();
        }
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Problemas_e_conflitos_com_muitos_itens_sao_virtualizados()
    {
        var host = NewHost();
        using (var c = Sql())
        {
            Exec(c, "BEGIN");
            for (var i = 0; i < 400; i++)
            {
                Exec(c, $"INSERT INTO failed_ops(drive_id,kind,lid,name,error,at) VALUES('d','upload',0,'arquivo-{i}.txt','HTTP 400',{i})");
                Exec(c, $"INSERT INTO conflicts(drive_id,lid,original,copy,reason,at) VALUES('d',0,'orig-{i}.txt','copia-{i}.txt','editado nos dois',{i})");
            }
            Exec(c, "COMMIT");
        }
        var vm = new MainViewModel(host);
        Assert.Equal(400, vm.Failures.Count);
        Assert.Equal(400, vm.Conflicts.Count);
        foreach (var (tab, source) in new (int, object)[] { (4, vm.Failures), (5, vm.Conflicts) })
        {
            var w = Show(vm, tab);
            var list = w.GetVisualDescendants().OfType<ItemsControl>().Single(i => ReferenceEquals(i.ItemsSource, source));
            var realized = list.GetRealizedContainers().Count();
            Assert.True(realized is > 0 and < 100, $"aba {tab}: {realized} itens criados de 400 (sem virtualização)");
            ShellUi.Save(w, $"s18-virtualizado-aba{tab}");
            w.Close();
        }
        vm.Dispose();
    }

    /// <summary>Todo texto cortado com reticências (caminhos) mostra o texto inteiro ao passar o mouse.</summary>
    [Fact]
    public void Textos_cortados_tem_dica_com_o_texto_inteiro()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CraftDriver.slnx"))) root = Path.GetDirectoryName(root)!;
        var views = Directory.GetFiles(Path.Combine(root, "src", "CraftDriver.App", "Views"), "*.axaml");
        var missing = new List<string>();
        foreach (var f in views)
            foreach (var e in XDocument.Load(f).Descendants())
                if (e.Attribute("TextTrimming") is { } trim && trim.Value != "None" && e.Attribute("ToolTip.Tip") == null)
                    missing.Add($"{Path.GetFileName(f)}: <{e.Name.LocalName} Text=\"{e.Attribute("Text")?.Value}\">");
        Assert.True(missing.Count == 0, "sem ToolTip:\n" + string.Join('\n', missing));
    }

    // ---------- atividade ----------

    [AvaloniaFact]
    public void Atividade_filtra_por_texto_e_por_erros_e_copia_o_que_esta_visivel()
    {
        var host = NewHost();
        host.Activity.Add(ActivityKind.Upload, "Enviado: relatorio.docx");
        host.Activity.Add(ActivityKind.Download, "Baixado: foto.jpg");
        host.Activity.Add(ActivityKind.Error, "Falha ao enviar planilha.xlsx: HTTP 507");
        var vm = new MainViewModel(host);
        string? copied = null;
        vm.CopyText = t => { copied = t; return Task.FromResult<string?>(null); };
        ShellUi.Pump(100);
        var all = vm.Activity.Count;
        Assert.True(all >= 3);

        vm.ActivityFilter = "foto";
        Assert.Equal(["Baixado: foto.jpg"], vm.Activity.Select(a => a.Text));
        vm.ActivityFilter = "";
        vm.ActivityErrorsOnly = true;
        Assert.All(vm.Activity, a => Assert.True(a.IsError));
        Assert.Contains(vm.Activity, a => a.Text.Contains("planilha"));

        ShellUi.Wait(vm.CopyActivityCommand.ExecuteAsync(null));
        Assert.NotNull(copied);
        Assert.Contains("planilha.xlsx", copied);
        Assert.DoesNotContain("foto.jpg", copied);

        host.Activity.Add(ActivityKind.Download, "Baixado: outra-foto.jpg"); // chega com o filtro ligado: não aparece
        ShellUi.Pump(200);
        Assert.DoesNotContain(vm.Activity, a => a.Text.Contains("outra-foto"));
        vm.ActivityErrorsOnly = false;
        Assert.Contains(vm.Activity, a => a.Text.Contains("outra-foto"));
        vm.Dispose();
    }

    // ---------- diálogos e tema ----------

    [AvaloniaFact]
    public void Fechar_sem_bandeja_nao_tem_encerrar_como_padrao()
    {
        var dlg = new NoTrayCloseWindow("/home/maria/OneDrive");
        var quit = dlg.FindControl<Button>("QuitButton")!;
        Assert.False(quit.IsDefault); // Enter não pode encerrar (e desmontar) por engano
        Assert.True(dlg.FindControl<Button>("CancelButton")!.IsCancel);
    }

    [AvaloniaFact]
    public void Tema_aplica_na_hora_sem_salvar()
    {
        var host = NewHost();
        var vm = new MainViewModel(host);
        try
        {
            vm.ThemeIndex = 2;
            Assert.Equal(Avalonia.Styling.ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
            Assert.Equal("dark", AppConfig.Load().Theme);
            vm.ThemeIndex = 1;
            Assert.Equal(Avalonia.Styling.ThemeVariant.Light, Application.Current!.RequestedThemeVariant);
        }
        finally
        {
            vm.ThemeIndex = 0;
            vm.Dispose();
        }
    }

    // ---------- ícones ----------

    static readonly string[] EmojiGlyphs = ["☁", "✔", "⟳", "📁", "📄", "🗑", "⏸", "⚠", "✖", "➜"];

    /// <summary>Os glifos dependiam de fonte de emoji (só "recommends" nos pacotes): sem ela viravam quadrados.</summary>
    [AvaloniaFact]
    public void Icones_sao_vetoriais_sem_depender_de_fonte_emoji()
    {
        var host = NewHost();
        host.Activity.Add(ActivityKind.Delete, "Apagado: velho.txt");
        host.Activity.Add(ActivityKind.Move, "Movido: a → b");
        host.Activity.Add(ActivityKind.Error, "Falha");
        var vm = new MainViewModel(host);
        foreach (var tab in new[] { 0, 3 })
        {
            var w = Show(vm, tab);
            var texts = w.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            var bad = texts.Where(t => EmojiGlyphs.Any(t.Contains)).ToList();
            Assert.True(bad.Count == 0, $"aba {tab}: " + string.Join(" | ", bad));
            ShellUi.Save(w, $"s18-icones-aba{tab}");
            if (tab == 0)
            {
                Assert.Contains(w.GetVisualDescendants().OfType<PathIcon>(), p => p.Name == "StatusIcon");
                Assert.True(w.GetVisualDescendants().OfType<PathIcon>().Count() >= 4, "ícones da atividade");
            }
            w.Close();
        }
        vm.Dispose();
    }

    // ---------- Adicionar biblioteca ----------

    [AvaloniaFact]
    public void Adicionar_biblioteca_so_com_conta_corporativa()
    {
        var host = NewHost();
        host.Config.Accounts.Add(new AccountConfig { Id = "p", Username = "eu@outlook.com", Type = "personal" });
        var vm = new MainViewModel(host);
        Assert.False(vm.AddLibraryCommand.CanExecute(null));
        var changed = 0;
        vm.AddLibraryCommand.CanExecuteChanged += (_, _) => changed++;
        host.RegisterAccount(new AccountConfig { Id = "b", Username = "eu@empresa.com", Type = "business" }, "drive-b", null);
        ShellUi.PumpUntil(() => changed > 0, "CanExecuteChanged (botão volta a ficar ativo)");
        Assert.True(vm.AddLibraryCommand.CanExecute(null));
        vm.Dispose();
    }

    // ---------- fila ----------

    /// <summary>A fila anda só pela cabeça: quando ela falha e espera, a interface diz o que é e por quê.</summary>
    [AvaloniaFact]
    public void Painel_mostra_o_que_trava_a_cabeca_da_fila()
    {
        var host = NewHost();
        host.Config.Drives.Add(new DriveConfig { DriveId = "d1", AccountId = "a", DisplayName = "OneDrive - Pessoal" });
        var next = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds();
        using (var c = Sql())
        {
            Exec(c, $"INSERT INTO ops(drive_id,kind,lid,name,attempts,next_try,last_error,created) VALUES('d1','upload',0,'planilha.xlsx',4,{next},'HTTP 503 Service Unavailable',1)");
            Exec(c, "INSERT INTO ops(drive_id,kind,lid,name,attempts,next_try,created) VALUES('d1','upload',0,'depois.txt',0,0,2)");
        }
        var vm = new MainViewModel(host);
        ShellUi.PumpUntil(() => vm.QueueBlocked.Count > 0, "painel da fila", 8000);
        var line = Assert.Single(vm.QueueBlocked);
        Assert.Contains("planilha.xlsx", line);
        Assert.Contains("4 tentativas", line);
        Assert.Contains("HTTP 503", line);
        Assert.Contains("OneDrive - Pessoal", line);
        Assert.Contains("1 ", line[line.IndexOf("atrás dela", StringComparison.Ordinal)..]); // 1 esperando atrás dela
        Assert.True(vm.HasQueueBlocked);
        host.Activity.Add(ActivityKind.Error, "Falha ao enviar planilha.xlsx: HTTP 503");
        vm.ActivityFilter = "planilha";
        var w = Show(vm, 0);
        ShellUi.Save(w, "s18-fila-parada-e-busca-na-atividade");
        w.Close();
        vm.ActivityFilter = "";

        using (var c = Sql()) Exec(c, "UPDATE ops SET attempts=0, last_error=NULL, next_try=0");
        vm.RefreshQueue(); // o timer de 1 s chama a cada 5 ciclos (junto com Problemas/Conflitos)
        Assert.Empty(vm.QueueBlocked);
        Assert.False(vm.HasQueueBlocked);
        vm.Dispose();
    }

    // ---------- §17 ----------

    /// <summary>Caracterização: tabela comando → formato dos argumentos dá o mesmo que as cadeias de if antigas.</summary>
    [Fact]
    public void Cli_monta_os_argumentos_de_cada_comando_como_antes()
    {
        var cwd = Environment.CurrentDirectory;
        string Abs(string p) => Path.GetFullPath(p);
        Assert.Equal([Abs("a.txt"), Abs("/x/b c")], CraftDriver.App.Program.BuildCliArgs(["pin", "a.txt", "--notify", "/x/b c"]));
        Assert.Equal([Abs("p")], CraftDriver.App.Program.BuildCliArgs(["exclude", "p"]));
        Assert.Equal([Abs("s")], CraftDriver.App.Program.BuildCliArgs(["status", "s"]));
        Assert.Empty(CraftDriver.App.Program.BuildCliArgs(["open", "--notify", "lixo"]));
        Assert.Empty(CraftDriver.App.Program.BuildCliArgs(["quit"]));
        Assert.Equal(["restore", "3"], CraftDriver.App.Program.BuildCliArgs(["trash", "restore", "3", "--notify"]));
        Assert.Equal(["confirm"], CraftDriver.App.Program.BuildCliArgs(["deletes", "confirm"]));
        Assert.Equal(["--edit", Abs("f.txt"), "--notify"], CraftDriver.App.Program.BuildCliArgs(["share", "--edit", "f.txt", "--notify"]));
        Assert.Equal([Abs("f.txt"), "--restore=7"], CraftDriver.App.Program.BuildCliArgs(["versions", "f.txt", "--restore=7"]));
        Assert.Equal(cwd, Environment.CurrentDirectory);
        foreach (var cmd in new[] { "pin", "unpin", "free", "status", "sync", "pause", "resume", "quit", "deletes", "retry", "open", "exclude", "include", "share", "versions", "trash" })
            Assert.True(CraftDriver.App.Program.CliCommands.ContainsKey(cmd), cmd);
    }
}
