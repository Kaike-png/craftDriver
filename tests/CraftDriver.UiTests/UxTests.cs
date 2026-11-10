using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CraftDriver.App.Services;
using CraftDriver.App.ViewModels;
using CraftDriver.App.Views;
using CraftDriver.Core.Config;
using CraftDriver.Core.State;
using CraftDriver.Core.Sync;

namespace CraftDriver.UiTests;

/// <summary>Seção 8 do PENDENCIAS.md: confirmações, estados vazios, mensagens, teclado, tema e configurações.</summary>
[Collection("host-isolado")]
public class UxTests
{
    static readonly string Out = Environment.GetEnvironmentVariable("CRAFTDRIVER_UI_OUT") ?? Path.Combine(Path.GetTempPath(), "craftdriver-ui");

    static AppHost NewHost(Action<AppConfig>? setup = null)
    {
        var home = Path.Combine(Path.GetTempPath(), "craftdriver-ui-home-" + Guid.NewGuid().ToString("N")[..6]);
        Environment.SetEnvironmentVariable("CRAFTDRIVER_HOME", home);
        Directory.CreateDirectory(Path.Combine(home, "config"));
        File.WriteAllText(Path.Combine(home, "config", "config.json"),
            $$"""{ "MountPoint": "{{home}}/OneDrive", "CacheDir": "{{home}}/cache" }""");
        var host = Task.Run(() => AppHost.CreateAsync()).GetAwaiter().GetResult();
        CraftDriver.App.App.Host = host;
        setup?.Invoke(host.Config);
        return host;
    }

    static void Pump(int ms = 200)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }
    }

    static void Save(Window w, string name)
    {
        Directory.CreateDirectory(Out);
        Pump();
        w.CaptureRenderedFrame()!.Save(Path.Combine(Out, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    /// <summary>Conta com uma drive montada só no banco (sem rede) e um arquivo ainda não enviado.</summary>
    static (AppHost host, long root) HostWithDrive(int dirty = 1)
    {
        var host = NewHost();
        host.Config.Accounts.Add(new AccountConfig { Id = "a2", Username = "usuario@exemplo.com", Type = "personal" });
        host.Config.Drives.Add(new DriveConfig { DriveId = "d1", AccountId = "a2", DisplayName = "OneDrive - Pessoal", Kind = "personal" });
        var root = host.Db.Insert(new Item { DriveId = "d1", ParentLid = 0, Name = "", IsDir = true, ItemId = "rroot" });
        host.Db.SaveDrive(new DriveState("d1", root, "delta", 0, 0));
        for (var i = 0; i < dirty; i++)
        {
            var lid = host.Db.Insert(new Item { DriveId = "d1", ParentLid = root, Name = $"rascunho{i}.txt", Dirty = true });
            host.Db.Enqueue("d1", OpKind.Upload, lid);
        }
        host.Engine.ReloadDrives();
        return (host, root);
    }

    // ---------- confirmações ----------

    [AvaloniaFact]
    public async Task Desvincular_conta_pede_confirmacao_e_avisa_o_que_sera_descartado()
    {
        var (host, _) = HostWithDrive(dirty: 2);
        var vm = new MainViewModel(host);
        var asked = new List<ConfirmRequest>();
        vm.ConfirmRequested += r => { asked.Add(r); return Task.FromResult(false); };

        await vm.Accounts.Single().RemoveCommand.ExecuteAsync(null);
        var req = Assert.Single(asked);
        Assert.Contains("usuario@exemplo.com", req.Text);
        Assert.Contains("2 alterações ainda não enviadas", req.Text);
        Assert.True(req.Danger);
        Assert.Single(host.Config.Accounts); // recusou: nada mudou

        vm.ConfirmRequested -= _ => Task.FromResult(false);
        vm.ConfirmRequested += _ => Task.FromResult(true);
        await vm.Accounts.Single().RemoveCommand.ExecuteAsync(null);
        Assert.Empty(host.Config.Accounts);
        host.Engine.Stop();
    }

    [AvaloniaFact]
    public async Task Sem_quem_confirme_acoes_destrutivas_nao_acontecem()
    {
        var (host, _) = HostWithDrive();
        host.Config.Drives.Add(new DriveConfig { DriveId = "lib", AccountId = "a2", DisplayName = "Financeiro", Kind = "documentLibrary" });
        var vm = new MainViewModel(host);
        vm.RefreshDrives();

        await vm.Accounts.Single().RemoveCommand.ExecuteAsync(null);
        await vm.Drives.Single(d => d.Config.DriveId == "lib").RemoveCommand.ExecuteAsync(null);
        await vm.FreeAllCommand.ExecuteAsync(null);
        Assert.Single(host.Config.Accounts);
        Assert.Contains(host.Config.Drives, d => d.DriveId == "lib");
        host.Engine.Stop();
    }

    [AvaloniaFact]
    public async Task Parar_de_sincronizar_e_liberar_tudo_confirmam_com_contagem()
    {
        var (host, root) = HostWithDrive(dirty: 0);
        host.Config.Drives.Add(new DriveConfig { DriveId = "lib", AccountId = "a2", DisplayName = "Financeiro", Kind = "documentLibrary" });
        var cached = host.Db.Insert(new Item { DriveId = "d1", ParentLid = root, Name = "foto.jpg", ItemId = "rfoto", CTag = "c1", CachedCTag = "c1" });
        Directory.CreateDirectory(Path.GetDirectoryName(host.Engine.Cache.PathFor("d1", cached))!);
        File.WriteAllText(host.Engine.Cache.PathFor("d1", cached), "jpeg");
        var vm = new MainViewModel(host);
        vm.RefreshDrives();
        var asked = new List<ConfirmRequest>();
        vm.ConfirmRequested += r => { asked.Add(r); return Task.FromResult(true); };

        await vm.FreeAllCommand.ExecuteAsync(null);
        Assert.Contains("1 arquivo", asked[^1].Text);
        Assert.False(File.Exists(host.Engine.Cache.PathFor("d1", cached)));

        await vm.Drives.Single(d => d.Config.DriveId == "lib").RemoveCommand.ExecuteAsync(null);
        Assert.Contains("Financeiro", asked[^1].Text);
        Assert.DoesNotContain(host.Config.Drives, d => d.DriveId == "lib");
        host.Engine.Stop();
    }

    [AvaloniaFact]
    public void Janela_de_confirmacao_esc_cancela_e_enter_confirma()
    {
        var w = new ConfirmWindow(new ConfirmRequest("Desvincular conta?", "Texto explicativo.", "Desvincular", Danger: true));
        w.Show();
        Pump();
        Save(w, "10-confirmacao");
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump();
        Assert.False(w.Result);
        Assert.False(w.IsVisible);

        var w2 = new ConfirmWindow(new ConfirmRequest("Liberar espaço?", "Texto.", "Liberar"));
        w2.Show();
        Pump();
        w2.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Pump();
        Assert.True(w2.Result);
    }

    // ---------- estados vazios e mensagens ----------

    [AvaloniaFact]
    public void Listas_vazias_explicam_o_que_fazer()
    {
        var host = NewHost();
        var vm = new MainViewModel(host);
        var w = new MainWindow { DataContext = vm, Width = 920, Height = 640 };
        w.Show();
        Pump(600);
        Assert.False(vm.HasActivity);
        Assert.False(vm.HasDrives);
        Assert.False(vm.HasPinned);
        var texts = w.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains(texts, t => t?.Contains("Nenhuma atividade") == true);
        var tabs = w.GetVisualDescendants().OfType<TabControl>().First();
        tabs.SelectedIndex = 1;
        Pump();
        texts = w.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains(texts, t => t?.Contains("Ainda não há contas neste computador") == true);
        tabs.SelectedIndex = 2;
        Pump();
        texts = w.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains(texts, t => t?.Contains("Nada sendo sincronizado") == true);
        tabs.SelectedIndex = 3;
        Pump();
        Save(w, "11-sob-demanda-vazio");
        texts = w.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains(texts, t => t?.Contains("Nenhuma pasta fixada") == true);
        w.Close();
    }

    [AvaloniaFact]
    public void Mensagem_de_erro_tem_estilo_proprio_e_pode_ser_fechada()
    {
        var host = NewHost();
        var vm = new MainViewModel(host);
        var w = new MainWindow { DataContext = vm, Width = 920, Height = 640 };
        w.Show();
        vm.ShowError("Não foi possível montar: pasta não está vazia.");
        Pump();
        Save(w, "12-mensagem-erro");
        Assert.True(vm.MessageIsError);
        var bar = w.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MessageBar");
        Assert.Contains("error", bar.Classes);
        vm.ClearMessageCommand.Execute(null);
        Assert.Equal("", vm.Message);
        Assert.False(vm.MessageIsError);

        vm.Message = "Configurações salvas."; // mensagem comum: sem estilo de erro
        Pump();
        Assert.DoesNotContain("error", bar.Classes);
        w.Close();
    }

    [AvaloniaFact]
    public void Atividade_com_erro_aparece_destacada()
    {
        var host = NewHost();
        host.Activity.Add(ActivityKind.Error, "Falha ao enviar planilha.xlsx");
        host.Activity.Add(ActivityKind.Upload, "Enviado: notas.md");
        var vm = new MainViewModel(host);
        var w = new MainWindow { DataContext = vm, Width = 920, Height = 640 };
        w.Show();
        Pump(600);
        var error = w.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Falha ao enviar planilha.xlsx");
        var normal = w.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Enviado: notas.md");
        Assert.Contains("error", error.Classes);
        Assert.DoesNotContain("error", normal.Classes);
        Assert.NotEqual(error.Foreground?.ToString(), normal.Foreground?.ToString());
        w.Close();
    }

    // ---------- teclado e acessibilidade ----------

    /// <summary>As abas ficam na barra lateral (painel próprio, não WrapPanel): as setas, Home e End continuam trocando de aba.</summary>
    [AvaloniaFact]
    public void Abas_da_barra_lateral_respondem_ao_teclado()
    {
        var host = NewHost();
        var vm = new MainViewModel(host);
        var w = new MainWindow { DataContext = vm, Width = 920, Height = 640 };
        w.Show();
        Pump();
        var tabs = w.GetVisualDescendants().OfType<TabControl>().First();
        w.GetVisualDescendants().OfType<TabItem>().First().Focus(NavigationMethod.Tab);
        Pump(100);
        var seen = new List<int>();
        foreach (var key in new[] { Key.Down, Key.Down, Key.Up, Key.End, Key.Home })
        {
            w.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
            Pump(100);
            seen.Add(tabs.SelectedIndex);
        }
        Assert.Equal([1, 2, 1, MainViewModel.SettingsTab, 0], seen);
        w.Close();
    }

    [AvaloniaFact]
    public void Atalhos_da_janela_principal()
    {
        var host = NewHost();
        var vm = new MainViewModel(host);
        var w = new MainWindow { DataContext = vm, Width = 920, Height = 640 };
        w.Show();
        Pump();
        w.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
        Pump();
        Assert.Contains("Verificando", vm.Message);
        var tabs = w.GetVisualDescendants().OfType<TabControl>().First();
        w.KeyPress(Key.OemComma, RawInputModifiers.Control, PhysicalKey.Comma, null);
        Pump();
        Assert.Equal("Configurações", ((TabItem)tabs.SelectedItem!).Header);
        w.Close();
    }

    [AvaloniaFact]
    public void Controles_sem_texto_tem_nome_acessivel()
    {
        var (host, _) = HostWithDrive(dirty: 0);
        var vm = new MainViewModel(host);
        vm.RefreshDrives();
        var w = new MainWindow { DataContext = vm, Width = 920, Height = 640 };
        w.Show();
        var tabs = w.GetVisualDescendants().OfType<TabControl>().First();
        var missing = new List<string>();
        for (var i = 0; i < tabs.ItemCount; i++)
        {
            tabs.SelectedIndex = i;
            Pump(150);
            // partes de template (o TextBox e os botões +/- do NumericUpDown) são lidas pelo controle que as contém
            foreach (var c in w.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.TemplatedParent == null))
            {
                var needsName = c is CheckBox { Content: null } or NumericUpDown or TextBox or ComboBox or Image
                                || (c is Button b && b.Content is not string);
                if (needsName && string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(c))
                              && Avalonia.Automation.AutomationProperties.GetLabeledBy(c) == null)
                    missing.Add($"aba {i}: {c.GetType().Name} {c.Name}");
            }
        }
        Assert.Empty(missing);
        w.Close();
        host.Engine.Stop();
    }

    [AvaloniaFact]
    public void Dialogos_fecham_com_esc()
    {
        var (host, _) = HostWithDrive(dirty: 0);
        var folders = new ChooseFoldersWindow(new ChooseFoldersViewModel(host.Engine, "d1"));
        folders.Show();
        Pump();
        folders.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump();
        Assert.False(folders.IsVisible);

        var lib = new AddLibraryWindow(host, null);
        lib.Show();
        Pump();
        lib.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump();
        Assert.False(lib.IsVisible);
        host.Engine.Stop();
    }

    [Fact]
    public void Reiniciar_espera_o_processo_antigo_terminar_e_abre_com_os_mesmos_argumentos()
    {
        var dir = Directory.CreateTempSubdirectory("craftdriver-restart-").FullName;
        var log = Path.Combine(dir, "log");
        using var old = System.Diagnostics.Process.Start("sleep", "1.5");
        var started = DateTime.UtcNow;
        var psi = AppRestart.BuildRelaunch(old.Id, "/bin/sh",
            ["-c", $"date +%s.%N > '{log}'; echo \"$@\" >> '{log}'", "x", "a b", "--minimized", "--headless"]);
        using var relaunch = System.Diagnostics.Process.Start(psi)!;
        Assert.True(relaunch.WaitForExit(10_000));
        var lines = File.ReadAllLines(log);
        var ranAt = DateTime.UnixEpoch.AddSeconds(double.Parse(lines[0], System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(old.HasExited);
        Assert.True(ranAt - started > TimeSpan.FromSeconds(1.2), $"abriu {(ranAt - started).TotalSeconds:0.00}s depois, antes do antigo sair");
        Assert.Equal("a b --headless", lines[1]); // --minimized cai: quem pediu para reiniciar quer ver a janela
        Directory.Delete(dir, true);
    }

    // ---------- tema ----------

    [AvaloniaFact]
    public async Task Tema_claro_escuro_ou_do_sistema()
    {
        var host = NewHost();
        var vm = new MainViewModel(host);
        Assert.Equal(0, vm.ThemeIndex); // sistema
        vm.ThemeIndex = 2;
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Equal("dark", host.Config.Theme);
        Assert.Equal(Avalonia.Styling.ThemeVariant.Dark, Avalonia.Application.Current!.RequestedThemeVariant);
        var w = new MainWindow { DataContext = vm, Width = 920, Height = 640 };
        w.Show();
        Save(w, "13-tema-escuro");
        w.Close();
        vm.ThemeIndex = 0;
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Equal(Avalonia.Styling.ThemeVariant.Default, Avalonia.Application.Current!.RequestedThemeVariant);
        Assert.Equal("system", AppConfig.Load().Theme);
    }

    // ---------- ponto de montagem, cache e Client ID ----------

    [AvaloniaFact]
    public async Task Ponto_de_montagem_invalido_nao_e_salvo()
    {
        var host = NewHost();
        var vm = new MainViewModel(host);
        var original = host.Config.MountPoint;
        var file = Path.Combine(Path.GetTempPath(), "craftdriver-arquivo-" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(file, "x");
        var nonEmpty = Directory.CreateTempSubdirectory("craftdriver-cheia-").FullName;
        File.WriteAllText(Path.Combine(nonEmpty, "a.txt"), "x");
        try
        {
            foreach (var (value, why) in new[]
                     {
                         ("relativo/OneDrive", "absoluto"), (file, "arquivo"), (nonEmpty, "vazia"),
                         ("/", "sistema"), (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "sistema"),
                         (Path.Combine(host.Config.CacheDir, "mnt"), "cache"),
                     })
            {
                vm.MountPoint = value;
                await vm.SaveSettingsCommand.ExecuteAsync(null);
                Assert.True(vm.MessageIsError, value);
                Assert.Contains(why, vm.Message);
                Assert.Equal(original, host.Config.MountPoint);
            }
        }
        finally
        {
            File.Delete(file);
            Directory.Delete(nonEmpty, true);
        }
    }

    [AvaloniaFact]
    public async Task Trocar_ponto_de_montagem_pede_confirmacao()
    {
        var host = NewHost();
        var vm = new MainViewModel(host);
        var asked = 0;
        vm.ConfirmRequested += _ => { asked++; return Task.FromResult(false); };
        var target = Path.Combine(Path.GetTempPath(), "craftdriver-novo-mnt-" + Guid.NewGuid().ToString("N")[..6]);
        vm.MountPoint = target;
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Equal(1, asked);
        Assert.NotEqual(target, host.Config.MountPoint); // recusou: nada salvo
    }

    [AvaloniaFact]
    public async Task Cache_editavel_vale_ao_reiniciar_e_recusa_com_alteracoes_pendentes()
    {
        var (host, _) = HostWithDrive(dirty: 1);
        var vm = new MainViewModel(host);
        vm.ConfirmRequested += _ => Task.FromResult(true);
        var oldCache = host.Config.CacheDir;
        var newCache = Path.Combine(Path.GetTempPath(), "craftdriver-novo-cache-" + Guid.NewGuid().ToString("N")[..6]);
        vm.CacheDir = newCache;
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.True(vm.MessageIsError);
        Assert.Contains("não enviad", vm.Message);
        Assert.Equal(oldCache, host.Config.CacheDir);

        while (host.Db.PeekOp("d1") is { } op) host.Db.CompleteOp(op.Id); // envio concluído
        foreach (var it in host.Db.GetChildren(host.Engine.GetDrive("d1")!.RootLid)) { it.Dirty = false; it.ItemId = "r" + it.Name; host.Db.Update(it); }
        var restarts = 0;
        vm.RestartRequested += () => restarts++;
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.False(vm.MessageIsError, vm.Message);
        Assert.Equal(newCache, host.Config.CacheDir);
        Assert.Equal(newCache, AppConfig.Load().CacheDir);
        Assert.True(vm.RestartNeeded);
        Assert.Contains("reiniciar", vm.Message, StringComparison.OrdinalIgnoreCase);
        vm.RestartCommand.Execute(null);
        Assert.Equal(1, restarts);
        host.Engine.Stop();
    }

    [AvaloniaFact]
    public async Task Client_id_alterado_oferece_reiniciar()
    {
        var host = NewHost();
        var vm = new MainViewModel(host);
        Assert.False(vm.RestartNeeded);
        vm.ClientId = "00000000-1111-2222-3333-444444444444";
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.True(vm.RestartNeeded);
        Assert.Contains("Client ID", vm.Message);

        vm.ClientId = "isto não é um guid";
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.True(vm.MessageIsError);
        Assert.Equal("00000000-1111-2222-3333-444444444444", host.Config.ClientId);
    }
}
