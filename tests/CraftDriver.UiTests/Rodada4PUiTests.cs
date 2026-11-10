using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CraftDriver.App.Services;
using CraftDriver.App.ViewModels;
using CraftDriver.App.Views;
using CraftDriver.Core.Sync;
using CraftDriver.Tests;

namespace CraftDriver.UiTests;

/// <summary>
/// Rodada 4, frente P, §30(a): arquivos apagados das cópias offline com o app fechado. Na abertura o app PERGUNTA se
/// apaga também no OneDrive (Lixeira) ou mantém; fechar sem decidir deixa a pergunta para a próxima abertura.
/// O provedor é falso: a UI não depende da tabela real (frente do motor).
/// </summary>
[Collection("host-isolado")]
public class Rodada4PUiTests
{
    sealed class FakeDeletions(params OfflineDeletion[] items) : IOfflineDeletionsProvider
    {
        public readonly List<OfflineDeletion> Items = [.. items];
        public readonly List<long[]> Confirmed = [];
        public readonly List<long[]> Kept = [];
        public int PendingCalls;

        public IReadOnlyList<OfflineDeletion> Pending()
        {
            lock (Items) { PendingCalls++; return Items.ToList(); }
        }

        public int ConfirmDelete(IReadOnlyCollection<long> lids)
        {
            lock (Items) { Confirmed.Add(lids.ToArray()); return Items.RemoveAll(i => lids.Contains(i.Lid)); }
        }

        public void Keep(IReadOnlyCollection<long> lids)
        {
            lock (Items) { Kept.Add(lids.ToArray()); Items.RemoveAll(i => lids.Contains(i.Lid)); }
        }
    }

    static readonly OfflineDeletion[] Dois = [new(11, "OneDrive - Pessoal/Fixada/a.txt"), new(12, "OneDrive - Pessoal/Fixada/sub/b.txt")];

    static AppHost NewHost(FakeDeletions fake, List<(string Title, string Body, NotificationAction[] Actions)>? sent = null)
    {
        ShellUi.NewHome();
        var host = Task.Run(() => AppHost.CreateAsync()).GetAwaiter().GetResult();
        CraftDriver.App.App.Host = host;
        host.GraphFactory = _ => new FakeGraph();
        host.Notify = (t, b, a) => sent?.Add((t, b, a));
        host.OfflineDeletions = fake;
        return host;
    }

    [AvaloniaFact]
    public void Abertura_pergunta_e_apaga_no_OneDrive_ao_confirmar()
    {
        var fake = new FakeDeletions(Dois);
        var host = NewHost(fake);
        var vm = new MainViewModel(host); // criado ANTES do Host.Start (App.InitWithHost): não consulta nada
        Assert.Equal(0, fake.PendingCalls);
        OfflineDeletionsRequest? asked = null;
        vm.OfflineDeletionsRequested += r => { asked = r; return Task.FromResult<bool?>(true); };

        var t = vm.OfferOfflineDeletionsAsync();
        ShellUi.Wait(t);

        Assert.True(t.Result);
        Assert.NotNull(asked);
        Assert.Contains("2 arquivos foram apagados da pasta com o app fechado", asked.Text);
        Assert.Contains("Apagar também no OneDrive? (vão para a Lixeira)", asked.Text);
        Assert.Equal(Dois.Select(d => d.Path), asked.Paths);
        Assert.Equal([[11L, 12L]], fake.Confirmed);
        Assert.Empty(fake.Kept);
        Assert.Contains("2 arquivos apagados no OneDrive", vm.Message);

        // uma pergunta por abertura
        asked = null;
        ShellUi.Wait(vm.OfferOfflineDeletionsAsync());
        Assert.Null(asked);
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Manter_mantem_na_nuvem_e_limpa_a_pendencia()
    {
        var fake = new FakeDeletions(Dois[0]);
        var vm = new MainViewModel(NewHost(fake));
        OfflineDeletionsRequest? asked = null;
        vm.OfflineDeletionsRequested += r => { asked = r; return Task.FromResult<bool?>(false); };
        ShellUi.Wait(vm.OfferOfflineDeletionsAsync());
        Assert.Contains("1 arquivo foi apagado da pasta com o app fechado", asked!.Text);
        Assert.Equal([[11L]], fake.Kept);
        Assert.Empty(fake.Confirmed);
        Assert.Contains("mantido no OneDrive", vm.Message);
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Fechar_sem_decidir_nao_apaga_nem_mantem_e_pergunta_de_novo_na_proxima_abertura()
    {
        var fake = new FakeDeletions(Dois);
        var host = NewHost(fake);
        var vm = new MainViewModel(host);
        var perguntas = 0;
        vm.OfflineDeletionsRequested += _ => { perguntas++; return Task.FromResult<bool?>(null); };
        ShellUi.Wait(vm.OfferOfflineDeletionsAsync());
        Assert.Equal(1, perguntas);
        Assert.Empty(fake.Confirmed);
        Assert.Empty(fake.Kept);
        // mostrar a janela de novo (bandeja) na mesma abertura não repete a pergunta
        ShellUi.Wait(vm.OfferOfflineDeletionsAsync());
        Assert.Equal(1, perguntas);
        vm.Dispose();

        var vm2 = new MainViewModel(host); // próxima abertura
        vm2.OfflineDeletionsRequested += _ => { perguntas++; return Task.FromResult<bool?>(null); };
        ShellUi.Wait(vm2.OfferOfflineDeletionsAsync());
        Assert.Equal(2, perguntas);
        vm2.Dispose();
    }

    [AvaloniaFact]
    public void Sem_janela_que_responda_nada_e_decidido()
    {
        var fake = new FakeDeletions(Dois);
        var vm = new MainViewModel(NewHost(fake));
        var t = vm.OfferOfflineDeletionsAsync();
        ShellUi.Wait(t);
        Assert.False(t.Result);
        Assert.Empty(fake.Confirmed);
        Assert.Empty(fake.Kept);
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Iniciado_na_bandeja_notifica_e_a_notificacao_abre_a_janela()
    {
        var sent = new List<(string Title, string Body, NotificationAction[] Actions)>();
        var fake = new FakeDeletions(Dois);
        var vm = new MainViewModel(NewHost(fake, sent));
        var abriu = 0;
        vm.NotifyOfflineDeletionsAtStart(() => abriu++);
        var n = Assert.Single(sent);
        Assert.Contains("2 exclusões aguardando confirmação", n.Body);
        n.Actions.Single(a => a.Id == NotificationAction.Default).OnInvoke();
        Assert.Equal(1, abriu);
        Assert.Empty(fake.Confirmed);

        fake.Items.Clear();
        sent.Clear();
        vm.NotifyOfflineDeletionsAtStart(() => abriu++);
        Assert.Empty(sent);
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Status_do_CLI_mostra_exclusoes_aguardando_confirmacao()
    {
        var fake = new FakeDeletions(Dois);
        var host = NewHost(fake);
        Assert.Contains("Exclusões aguardando confirmação: 2 (abra o app)", host.StatusSummary().Split('\n'));
        fake.Items.Clear();
        Assert.DoesNotContain("Exclusões aguardando confirmação", host.StatusSummary());
    }

    static OfflineDeletionsRequest Pedido(int n) => new(
        "Arquivos apagados com o app fechado",
        $"{n} arquivos foram apagados da pasta com o app fechado. Apagar também no OneDrive? (vão para a Lixeira)",
        Enumerable.Range(1, n).Select(i => $"OneDrive - Pessoal/Fotos/2026/viagem/IMG_{i:0000}.jpg").ToList());

    [AvaloniaFact]
    public void Janela_lista_rolavel_Manter_e_o_padrao_e_Esc_fecha_sem_decidir()
    {
        var w = new OfflineDeletionsWindow(Pedido(30));
        w.Show();
        ShellUi.Pump();
        ShellUi.Save(w, "r4p-exclusoes-offline");
        var texts = w.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        Assert.Contains(texts, t => t.Contains("30 arquivos foram apagados"));
        var scroll = w.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "PathsScroll");
        Assert.Contains("OneDrive - Pessoal/Fotos/2026/viagem/IMG_0030.jpg",
            scroll.GetLogicalDescendantsText());
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height, "lista longa precisa rolar dentro da janela");
        Assert.True(w.Bounds.Height < 700, $"a janela não pode crescer com a lista: {w.Bounds.Height}");

        var buttons = w.GetVisualDescendants().OfType<Button>().ToList();
        var manter = buttons.Single(b => b.Content as string == "Manter");
        var apagar = buttons.Single(b => b.Content as string == "Apagar no OneDrive");
        Assert.True(manter.IsDefault);
        Assert.False(apagar.IsDefault);
        Assert.False(manter.IsCancel || apagar.IsCancel, "Esc não pode valer como Manter");
        Assert.True(manter.IsFocused, "o foco começa no botão seguro");

        w.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        ShellUi.Pump();
        Assert.Null(w.Result);
        Assert.False(w.IsVisible);

        var w2 = new OfflineDeletionsWindow(Pedido(2));
        w2.Show();
        ShellUi.Pump();
        w2.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        ShellUi.Pump();
        Assert.False(w2.Result); // Enter = Manter

        var w3 = new OfflineDeletionsWindow(Pedido(2));
        w3.Show();
        ShellUi.Pump();
        w3.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Apagar no OneDrive")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ShellUi.Pump();
        Assert.True(w3.Result);
        Assert.False(w3.IsVisible);
    }

    [AvaloniaFact]
    public void Janela_principal_abre_a_pergunta_como_dialogo()
    {
        var fake = new FakeDeletions(Dois);
        var vm = new MainViewModel(NewHost(fake));
        var w = new MainWindow { DataContext = vm, Width = 920, Height = 640 };
        w.Show();
        ShellUi.Pump();
        try
        {
            var t = vm.OfferOfflineDeletionsAsync();
            ShellUi.PumpUntil(() => w.OwnedWindows.OfType<OfflineDeletionsWindow>().Any(), "diálogo de exclusões offline");
            var dlg = w.OwnedWindows.OfType<OfflineDeletionsWindow>().Single();
            Assert.Contains(dlg.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == Dois[1].Path);
            ShellUi.Save(dlg, "r4p-exclusoes-offline-dialogo");
            dlg.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Apagar no OneDrive")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ShellUi.Wait(t);
            Assert.Equal([[11L, 12L]], fake.Confirmed);
        }
        finally
        {
            w.Close();
            vm.Dispose();
        }
    }
}

static class LogicalText
{
    public static string GetLogicalDescendantsText(this Control c) =>
        string.Join('\n', c.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
}
