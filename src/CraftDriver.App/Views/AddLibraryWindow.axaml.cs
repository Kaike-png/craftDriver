using Avalonia.Controls;
using CraftDriver.App.Services;
using CraftDriver.Core.Config;
using CraftDriver.Core.Graph;

namespace CraftDriver.App.Views;

public partial class AddLibraryWindow : Window
{
    readonly AppHost _host;
    readonly List<AccountConfig> _accounts;
    List<GraphDrive> _libs = [];
    public bool Added { get; private set; }

    sealed record AccountItem(AccountConfig Account)
    {
        public override string ToString() => Account.Username + (Account.IsPersonal ? " (pessoal)" : "");
    }

    sealed record LibItem(GraphDrive Drive)
    {
        public override string ToString() => Drive.Name + (Drive.DriveType == "documentLibrary" ? "" : $" ({Drive.DriveType})");
    }

    public AddLibraryWindow() : this(App.Host, null) { }

    public AddLibraryWindow(AppHost host, AccountConfig? preferred)
    {
        InitializeComponent();
        Icon = App.LoadIcon("cloud");
        _host = host;
        _accounts = host.Config.Accounts.Where(a => !a.IsPersonal).ToList();
        AccountBox.ItemsSource = _accounts.Select(a => new AccountItem(a)).ToList();
        AccountBox.SelectedIndex = Math.Max(0, preferred == null ? 0 : _accounts.FindIndex(a => a.Id == preferred.Id));

        CancelButton.Click += (_, _) => Close();
        Closed += (_, _) => { _sitesCts?.Cancel(); _libsCts?.Cancel(); };
        SearchButton.Click += async (_, _) => await SearchAsync();
        AccountBox.SelectionChanged += async (_, _) => await LoadFollowedAsync();
        SitesList.SelectionChanged += async (_, _) => await LoadLibsAsync();
        LibsList.SelectionChanged += (_, _) => UpdateName();
        AddButton.Click += (_, _) => AddSelected();
        Opened += async (_, _) =>
        {
            if (_accounts.Count == 0)
            {
                StatusText.Text = "Adicione primeiro uma conta corporativa ou de estudante (Microsoft 365). Contas pessoais não têm SharePoint.";
                SearchButton.IsEnabled = false;
                return;
            }
            await LoadFollowedAsync();
        };
    }

    AccountConfig? Account => (AccountBox.SelectedItem as AccountItem)?.Account;
    GraphClient? Client => Account is { } a ? _host.ClientFor(a.Id) : null;

    // Cada carga cancela a anterior do mesmo tipo: trocar de conta/site ou pesquisar de novo enquanto uma
    // resposta lenta ainda chega não pode sobrescrever a lista nova com a antiga.
    CancellationTokenSource? _sitesCts, _libsCts;

    static CancellationToken Restart(ref CancellationTokenSource? cts)
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = new CancellationTokenSource();
        return cts.Token;
    }

    async Task Run(string busy, CancellationToken ct, Func<Task> action)
    {
        StatusText.Text = busy;
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* substituída por outra carga */ }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) StatusText.Text = "Erro: " + ex.Message;
        }
    }

    void ClearLibs()
    {
        _libsCts?.Cancel();
        _libs = [];
        LibsList.ItemsSource = null;
        UpdateName();
    }

    async Task LoadFollowedAsync()
    {
        if (Client is not { } c) return;
        var ct = Restart(ref _sitesCts);
        ClearLibs();
        await Run("Carregando sites seguidos…", ct, async () =>
        {
            var sites = await c.GetFollowedSitesAsync(ct);
            try
            {
                var root = await c.GetRootSiteAsync(ct);
                if (sites.All(s => s.Id != root.Id)) sites.Insert(0, root with { DisplayName = root.DisplayName + " (site raiz)" });
            }
            catch (GraphException) { }
            ct.ThrowIfCancellationRequested();
            SitesList.ItemsSource = sites;
            StatusText.Text = sites.Count == 0 ? "Nenhum site seguido. Pesquise pelo nome ou cole a URL." : $"{sites.Count} site(s).";
        });
    }

    async Task SearchAsync()
    {
        if (Client is not { } c) return;
        var q = SearchBox.Text?.Trim() ?? "";
        var ct = Restart(ref _sitesCts);
        ClearLibs();
        await Run("Pesquisando…", ct, async () =>
        {
            List<SiteInfo> sites;
            if (q.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var s = await c.GetSiteByUrlAsync(q, ct);
                sites = s == null ? [] : [s];
            }
            else sites = await c.SearchSitesAsync(q, ct);
            ct.ThrowIfCancellationRequested();
            SitesList.ItemsSource = sites;
            StatusText.Text = sites.Count == 0 ? "Nenhum site encontrado." : $"{sites.Count} site(s) encontrados.";
        });
    }

    async Task LoadLibsAsync()
    {
        if (Client is not { } c || SitesList.SelectedItem is not SiteInfo site)
        {
            ClearLibs();
            return;
        }
        var ct = Restart(ref _libsCts);
        LibsList.ItemsSource = null;
        await Run($"Carregando bibliotecas de {site.DisplayName}…", ct, async () =>
        {
            var libs = await c.GetSiteDrivesAsync(site.Id, ct);
            ct.ThrowIfCancellationRequested();
            _libs = libs;
            LibsList.ItemsSource = _libs.Select(l => new LibItem(l)).ToList();
            if (_libs.Count > 0) LibsList.SelectedIndex = 0;
            StatusText.Text = _libs.Count == 0 ? "Este site não tem bibliotecas acessíveis." : "Escolha a biblioteca.";
        });
    }

    void UpdateName()
    {
        var ok = SitesList.SelectedItem is SiteInfo && LibsList.SelectedItem is LibItem;
        AddButton.IsEnabled = ok;
        if (SitesList.SelectedItem is SiteInfo s && LibsList.SelectedItem is LibItem l)
        {
            var already = _host.Config.Drives.Any(d => d.DriveId == l.Drive.Id);
            NameBox.Text = $"{s.DisplayName.Replace(" (site raiz)", "")} - {l.Drive.Name}";
            StatusText.Text = already ? "Esta biblioteca já está sendo sincronizada." : "";
            AddButton.IsEnabled = !already;
        }
    }

    void AddSelected()
    {
        if (Account is not { } acc || SitesList.SelectedItem is not SiteInfo site || LibsList.SelectedItem is not LibItem lib) return;
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? $"{site.DisplayName} - {lib.Drive.Name}" : NameBox.Text.Trim();
        var dc = _host.AddLibrary(acc.Id, site, lib.Drive, name);
        Added = true;
        StatusText.Text = $"'{dc.DisplayName}' adicionada. Ela aparece em {_host.Config.MountPoint} em instantes.";
        AddButton.IsEnabled = false;
    }
}
