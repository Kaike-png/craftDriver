using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CraftDriver.App.Services;
using CraftDriver.Core.Config;
using CraftDriver.Core.Sync;

namespace CraftDriver.App.ViewModels;

public abstract class ViewModelBase : ObservableObject;

public static class Fmt
{
    public static string Bytes(long b)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        double v = b;
        var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }

    /// <summary>Tempo restante curto: "45 s", "3 min", "1 h 20 min".</summary>
    public static string Duration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{Math.Max(1, (int)t.TotalSeconds)} s"
        : t.TotalMinutes < 60 ? $"{(int)Math.Round(t.TotalMinutes)} min"
        : $"{(int)t.TotalHours} h {t.Minutes} min";

    /// <summary>"12,3 MB de 40 MB · 2,1 MB/s · faltam 13 s" (velocidade/tempo só quando há medida).</summary>
    public static string TransferDetail(long done, long total, double bytesPerSecond, TimeSpan? remaining)
    {
        var s = $"{Bytes(done)} de {Bytes(total)}";
        if (bytesPerSecond > 0) s += $" · {Bytes((long)bytesPerSecond)}/s";
        if (remaining is { } r) s += $" · faltam {Duration(r)}";
        return s;
    }
}

public sealed partial class TransferVm : ViewModelBase
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Direction { get; init; } = "";
    [ObservableProperty] public partial double Percent { get; set; }
    [ObservableProperty] public partial string Detail { get; set; } = "";
    public Action? CancelAction { get; init; }
    [ObservableProperty] public partial bool Cancelling { get; set; }
    [RelayCommand]
    void Cancel()
    {
        Cancelling = true;
        CancelAction?.Invoke();
    }
}

public sealed partial class ConflictVm(MainViewModel main, long id, string path, string original, string reason, string when) : ViewModelBase
{
    public long Id { get; } = id;
    /// <summary>Caminho local do item a conferir (a cópia criada pelo conflito).</summary>
    public string Path { get; } = path;
    public string Original { get; } = original;
    public string Reason { get; } = reason;
    public string When { get; } = when;
    /// <summary>Só quando o item ainda existe (caminho completo); senão Path é só o nome.</summary>
    public bool CanOpen => System.IO.Path.IsPathRooted(Path);
    [RelayCommand] void OpenFolder() { if (CanOpen) DesktopIntegration.OpenPath(System.IO.Path.GetDirectoryName(Path)!); }
    [RelayCommand] void Dismiss() => main.DismissConflict(this);
}

/// <param name="Icon">Chave de <see cref="Icons"/>.</param>
public sealed record ActivityVm(string Time, string Icon, string Text, bool IsError);

/// <summary>Item de uma exclusão retida (pasta com o total dentro, ou arquivo).</summary>
public sealed record DeletedEntryVm(string Icon, string Text);

public sealed partial class AccountVm(MainViewModel main, AccountConfig acc) : ViewModelBase
{
    public AccountConfig Account { get; } = acc;
    public string Username => Account.Username;
    public string TypeLabel => Account.IsPersonal ? "Conta pessoal (Microsoft)" : "Conta corporativa ou de estudante (Microsoft 365)";
    public bool IsBusiness => !Account.IsPersonal;
    [ObservableProperty] public partial string Drives { get; set; } = "";
    [ObservableProperty] public partial bool NeedsLogin { get; set; }

    [RelayCommand] Task Remove() => main.RemoveAccountAsync(this);
    [RelayCommand] Task Reauthenticate() => main.ReauthenticateAsync(this);
    [RelayCommand] void AddLibrary() => main.RequestAddLibrary(Account);
}

public sealed partial class DriveVm(MainViewModel main, DriveConfig cfg) : ViewModelBase
{
    public DriveConfig Config { get; } = cfg;
    public string DisplayName => Config.DisplayName;
    public string KindLabel => Config.Kind switch
    {
        "personal" => "OneDrive pessoal",
        "business" => "OneDrive for Business",
        _ => "SharePoint" + (Config.SiteName != null ? $" — site {Config.SiteName}" : ""),
    };
    public bool CanRemove => Config.Kind == "documentLibrary";
    [RelayCommand] void ChooseFolders() => main.RequestChooseFolders(Config.DriveId);
    [ObservableProperty] public partial string Account { get; set; } = "";
    [ObservableProperty] public partial bool Enabled { get; set; } = cfg.Enabled;
    [ObservableProperty] public partial string Stats { get; set; } = "";
    [ObservableProperty] public partial string Quota { get; set; } = "";
    [ObservableProperty] public partial double QuotaPercent { get; set; }
    [ObservableProperty] public partial string State { get; set; } = "";

    partial void OnEnabledChanged(bool value) => main.SetDriveEnabled(this, value);

    [RelayCommand] Task Remove() => main.RemoveDriveAsync(this);
    [RelayCommand] void OpenWeb() { if (Config.WebUrl != null) DesktopIntegration.OpenPath(Config.WebUrl); }
    [RelayCommand] void OpenFolder() => DesktopIntegration.OpenPath(Path.Combine(main.Host.Config.MountPoint, Config.DisplayName.Replace('/', '-')));
}

public sealed partial class DeleteAlertVm(MainViewModel main, DriveSync drive) : ViewModelBase
{
    public DriveSync Drive { get; } = drive;
    [ObservableProperty] public partial string Text { get; set; } = "";

    /// <summary>O que será apagado: pastas aparecem uma vez, com o total dentro.</summary>
    public ObservableCollection<DeletedEntryVm> Items { get; } = [];
    [ObservableProperty] public partial string More { get; set; } = "";
    const int Shown = 20;
    long _listedFor = -1;

    public void Update()
    {
        Text = $"{Drive.HeldDeleteCount} itens foram apagados de \"{Drive.Config.DisplayName}\". Nada foi excluído da nuvem ainda. " +
               "Se confirmar, eles vão para a Lixeira do OneDrive (recuperáveis por 30 dias). Se restaurar, voltam para a pasta.";
        if (_listedFor == Drive.HeldDeleteCount) return; // a lista só muda quando o total muda
        _listedFor = Drive.HeldDeleteCount;
        var all = Drive.HeldDeletes();
        Items.Clear();
        foreach (var d in all.Take(Shown))
            Items.Add(d.IsFolder ? new("folder", $"{d.Path}  ({d.Count} {(d.Count == 1 ? "item" : "itens")})") : new("file", d.Path));
        More = all.Count > Shown ? $"… e mais {all.Count - Shown}" : "";
    }

    [RelayCommand] void Confirm() { Drive.ConfirmDeletes(); main.RefreshDeleteAlerts(); }
    [RelayCommand] async Task Restore() { await Drive.RestoreDeletesAsync(); main.RefreshDeleteAlerts(); }
}

public sealed partial class FailureVm(MainViewModel main, long id, string what, string path, string error, string when) : ViewModelBase
{
    public long Id { get; } = id;
    public string What { get; } = what;
    public string Path { get; } = path;
    public string Error { get; } = error;
    public string When { get; } = when;
    [RelayCommand] void Retry() => main.RetryFailure(this);
    [RelayCommand] void Dismiss() => main.DismissFailure(this);
}

public sealed partial class PinnedVm(MainViewModel main, long lid, string path, bool isDir) : ViewModelBase
{
    public long Lid { get; } = lid;
    public string Path { get; } = path;
    public string Icon { get; } = isDir ? "folder" : "file";
    [RelayCommand] void Unpin() => main.Unpin(this);
}

public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    public AppHost Host { get; }
    public event Action? QuitRequested;
    public event Action<AccountConfig?>? AddLibraryRequested;
    /// <summary>Abre o login; com uma conta, é "Entrar novamente" para ela (sessão expirada).</summary>
    public event Func<AccountConfig?, Task<bool>>? AddAccountRequested;
    public event Func<Task<string?>>? PickFolderRequested;

    public ObservableCollection<TransferVm> Transfers { get; } = [];
    public ObservableCollection<ActivityVm> Activity { get; } = [];
    public ObservableCollection<AccountVm> Accounts { get; } = [];
    public ObservableCollection<DriveVm> Drives { get; } = [];
    public ObservableCollection<FailureVm> Failures { get; } = [];
    public ObservableCollection<ConflictVm> Conflicts { get; } = [];
    [ObservableProperty] public partial bool HasConflicts { get; set; }
    [ObservableProperty] public partial string ConflictsHeader { get; set; } = "Conflitos";
    [ObservableProperty] public partial bool HasFailures { get; set; }
    [ObservableProperty] public partial string FailuresHeader { get; set; } = "Problemas";
    public ObservableCollection<PinnedVm> Pinned { get; } = [];
    public ObservableCollection<DeleteAlertVm> DeleteAlerts { get; } = [];
    [ObservableProperty] public partial bool HasDeleteAlerts { get; set; }
    [ObservableProperty] public partial decimal MassDeleteThreshold { get; set; }

    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial string StatusDetail { get; set; } = "";
    /// <summary>Chave de <see cref="Icons"/> do estado geral.</summary>
    [ObservableProperty] public partial string StatusIcon { get; set; } = "cloud";
    [ObservableProperty] public partial string MountText { get; set; } = "";
    [ObservableProperty] public partial bool IsMounted { get; set; }
    [ObservableProperty] public partial string PauseText { get; set; } = "Pausar";
    [ObservableProperty] public partial bool HasAccounts { get; set; }
    [ObservableProperty] public partial bool NeedsLogin { get; set; }
    [ObservableProperty] public partial string CacheText { get; set; } = "";
    [ObservableProperty] public partial string Message { get; set; } = "";
    [ObservableProperty] public partial bool HasTransfers { get; set; }
    /// <summary>A mensagem atual é um erro (barra com estilo próprio). Trocar <see cref="Message"/> zera.</summary>
    [ObservableProperty] public partial bool MessageIsError { get; set; }
    [ObservableProperty] public partial bool HasActivity { get; set; }
    [ObservableProperty] public partial bool HasDrives { get; set; }
    [ObservableProperty] public partial bool HasPinned { get; set; }
    /// <summary>Aba aberta na janela principal (Ctrl+, abre Configurações).</summary>
    [ObservableProperty] public partial int SelectedTab { get; set; }
    public const int SettingsTab = 6;
    /// <summary>0 = seguir o sistema, 1 = claro, 2 = escuro (<see cref="ThemeService.Values"/>).</summary>
    [ObservableProperty] public partial int ThemeIndex { get; set; }
    /// <summary>Há configuração salva que só vale depois de reiniciar (Client ID, pasta do cache).</summary>
    [ObservableProperty] public partial bool RestartNeeded { get; set; }

    /// <summary>Pede confirmação de uma ação destrutiva; sem quem responda (ex.: sem janela), a ação não acontece.</summary>
    public event Func<ConfirmRequest, Task<bool>>? ConfirmRequested;
    public event Action? RestartRequested;
    public event Func<string, Task<string?>>? PickSettingsFolderRequested;

    /// <summary>Copia texto (atividade); devolve null ou o motivo da falha. Troque nos testes.</summary>
    internal Func<string, Task<string?>> CopyText { get; set; }
    /// <summary>Busca na atividade recente (texto contido, sem diferenciar maiúsculas).</summary>
    [ObservableProperty] public partial string ActivityFilter { get; set; } = "";
    [ObservableProperty] public partial bool ActivityErrorsOnly { get; set; }

    /// <summary>
    /// Por drive, a operação na cabeça da fila que está falhando e esperando nova tentativa. A fila anda só pela
    /// cabeça: enquanto ela espera, nada atrás dela é enviado — sem isto a tela só dizia "N alterações na fila".
    /// </summary>
    public ObservableCollection<string> QueueBlocked { get; } = [];
    [ObservableProperty] public partial bool HasQueueBlocked { get; set; }

    /// <summary>Liga/desliga o início automático; devolve um aviso para mostrar (troque nos testes).</summary>
    internal Func<bool, AutostartMode, string?> SetAutostart { get; set; } = DesktopIntegration.SetAutostart;

    partial void OnMessageChanged(string value) => MessageIsError = false;

    bool _themeLoaded;

    /// <summary>Tema muda na hora (antes só ao Salvar) e fica gravado.</summary>
    partial void OnThemeIndexChanged(int value)
    {
        if (!_themeLoaded) return; // valor inicial lido da configuração
        var theme = ThemeService.Values[Math.Clamp(value, 0, ThemeService.Values.Length - 1)];
        if (Host.Config.Theme == theme) return;
        Host.Config.Theme = theme;
        ThemeService.Apply(Host.Config);
        Host.TrySaveConfig("Aparência");
    }

    public void ShowError(string text)
    {
        Message = text;
        MessageIsError = true;
    }

    [RelayCommand] void ClearMessage() => Message = "";
    [RelayCommand] void Restart() => RestartRequested?.Invoke();
    [RelayCommand] void ShowSettings() => SelectedTab = SettingsTab;

    Task<bool> ConfirmAsync(ConfirmRequest request) =>
        ConfirmRequested?.Invoke(request) ?? Task.FromResult(false);

    static string Pending(long n) => n == 1 ? "1 alteração ainda não enviada" : $"{n} alterações ainda não enviadas";

    // configurações
    [ObservableProperty] public partial string MountPoint { get; set; }
    [ObservableProperty] public partial string CacheDir { get; set; }
    [ObservableProperty] public partial decimal CacheLimitGB { get; set; }
    [ObservableProperty] public partial decimal PollInterval { get; set; }
    [ObservableProperty] public partial decimal DownloadConcurrency { get; set; }
    /// <summary>MB/s (1 MB = 1 000 000 bytes, como os navegadores mostram); 0 = sem limite.</summary>
    [ObservableProperty] public partial decimal DownloadLimitMBps { get; set; }
    [ObservableProperty] public partial decimal UploadLimitMBps { get; set; }
    [ObservableProperty] public partial bool StartWithSystem { get; set; }
    /// <summary>Hyprland/Sway não leem ~/.config/autostart: lá a alternativa é o serviço systemd --user.</summary>
    public bool CanUseSystemdAutostart { get; }
    [ObservableProperty] public partial bool UseSystemdAutostart { get; set; }
    [ObservableProperty] public partial bool StartMinimized { get; set; }
    [ObservableProperty] public partial string BlockedProcesses { get; set; }
    [ObservableProperty] public partial string LocalOnlyPatterns { get; set; }
    [ObservableProperty] public partial string ClientId { get; set; }
    public string TokenStorage => Host.Auth.CacheStorage;
    public string LogFile => AppPaths.LogFile;

    ActivityEntry? _newestActivity;
    int _activityQueued;
    int _tick;
    volatile bool _disposed;
    readonly Avalonia.Threading.DispatcherTimer _timer;
    public event Action? Refreshed;
    public EngineStatus CurrentStatus { get; private set; }
    public long PendingCount { get; private set; }
    public int TransferCount { get; private set; }

    // Compartilhado por host: recriar a janela/VM não inicia outra varredura concorrente.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AppHost, CacheUsageSampler> CacheSamplers = new();
    readonly CacheUsageSampler _cacheSampler;
    bool _cacheRefreshPending;

    public MainViewModel(AppHost host, Func<long>? readCacheBytes = null, Func<DateTimeOffset>? clock = null)
    {
        Host = host;
        CopyText = t => Host.CopyToClipboard(t);
        _cacheSampler = CacheSamplers.GetValue(host, h => new CacheUsageSampler(
            readCacheBytes ?? h.Engine.Cache.TotalBytes, clock ?? (() => DateTimeOffset.UtcNow)));
        var c = host.Config;
        MountPoint = c.MountPoint;
        CacheDir = c.CacheDir;
        CacheLimitGB = c.CacheLimitMB / 1024m;
        PollInterval = c.PollIntervalSeconds;
        DownloadConcurrency = c.DownloadConcurrency;
        DownloadLimitMBps = Math.Max(0, c.DownloadBytesPerSecond) / 1_000_000m;
        UploadLimitMBps = Math.Max(0, c.UploadBytesPerSecond) / 1_000_000m;
        StartWithSystem = DesktopIntegration.AutostartEnabled;
        StartMinimized = c.StartMinimized;
        BlockedProcesses = string.Join(", ", c.BlockedProcesses);
        LocalOnlyPatterns = string.Join(", ", c.LocalOnlyPatterns);
        ClientId = c.ClientId;
        MassDeleteThreshold = c.MassDeleteThreshold;
        ThemeIndex = ThemeService.IndexOf(c.Theme);
        _themeLoaded = true;

        RebuildAccounts();
        RebuildPinned();
        RefreshFailures();
        host.Activity.Changed += OnActivityChanged;
        RefreshActivity();
        Refresh();
        if (host.Config.LoadWarning is { } warning) Message = warning;
        host.AccountsChanged += OnAccountsChanged;
        host.ReauthRequested += OnReauthRequested;
        CanUseSystemdAutostart = DesktopIntegration.RecommendedAutostartMode == AutostartMode.Systemd
                                 || DesktopIntegration.CurrentAutostartMode == AutostartMode.Systemd;
        UseSystemdAutostart = DesktopIntegration.CurrentAutostartMode == AutostartMode.Systemd;
        _timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    // ---------- atualização periódica ----------

    void Refresh()
    {
        if (_disposed) return;
        var e = Host.Engine;
        var st = e.Status;
        StatusText = AppHost.StatusText(st);
        StatusIcon = st switch
        {
            EngineStatus.UpToDate => "ok",
            EngineStatus.Syncing => "sync",
            EngineStatus.Paused => "pause",
            EngineStatus.Offline => "offline",
            EngineStatus.AuthRequired or EngineStatus.Error => "error",
            EngineStatus.DeleteConfirmation => "warning",
            _ => "cloud",
        };
        var pending = Host.Db.PendingOps();
        CurrentStatus = st;
        PendingCount = pending;
        var lastSync = e.Drives.Select(d => d.LastSync).Where(t => t != null).DefaultIfEmpty().Max();
        StatusDetail = st switch
        {
            EngineStatus.Syncing => $"{pending} alteração(ões) na fila, {Host.Activity.Transfers.Count} transferência(s) em andamento",
            EngineStatus.UpToDate when lastSync != null => $"Última verificação às {lastSync:HH:mm:ss}",
            EngineStatus.NoAccounts => "Adicione uma conta Microsoft para começar.",
            EngineStatus.AuthRequired => "A sessão expirou. Use \"Entrar novamente\" na aba Contas.",
            EngineStatus.DeleteConfirmation => "Muitos arquivos foram apagados. Confirme ou restaure abaixo.",
            EngineStatus.Offline => pending > 0 ? $"{pending} alteração(ões) aguardando conexão" : "Os arquivos disponíveis offline continuam acessíveis.",
            EngineStatus.Error when Failures.Count > 0 => $"{Failures.Count} item(ns) não sincronizado(s). Veja a aba Problemas.",
            EngineStatus.Error => e.Drives.FirstOrDefault(d => d.LastError != null)?.LastError ?? "",
            _ => "",
        };
        IsMounted = Host.Mounted;
        MountText = Host.Mounted ? Host.Config.MountPoint : "Não montado" + (Host.MountError != null ? $": {Host.MountError}" : "");
        PauseText = e.Paused ? "Retomar" : "Pausar";
        HasAccounts = Host.Config.Accounts.Count > 0;
        NeedsLogin = st == EngineStatus.AuthRequired;

        // transferências
        var current = Host.Activity.Transfers;
        TransferCount = current.Count;
        foreach (var gone in Transfers.Where(t => current.All(c => c.Id != t.Id)).ToList()) Transfers.Remove(gone);
        foreach (var t in current)
        {
            var vm = Transfers.FirstOrDefault(x => x.Id == t.Id);
            if (vm == null)
            {
                var id = t.Id;
                vm = new TransferVm
                {
                    Id = t.Id, Name = t.Name, Direction = t.Upload ? "upload" : "download",
                    CancelAction = () => Host.Activity.CancelTransfer(id),
                };
                Transfers.Add(vm);
            }
            vm.Percent = t.Percent;
            vm.Detail = vm.Cancelling ? "cancelando…" : Fmt.TransferDetail(t.Done, t.Total, t.BytesPerSecond, t.Remaining);
        }

        HasTransfers = Transfers.Count > 0;
        RefreshDeleteAlerts();

        if (_tick++ % 5 == 0)
        {
            RefreshDrives();
            RefreshFailures();
            RefreshConflicts();
            RefreshQueue();
        }
        Refreshed?.Invoke();
    }

    void OnTick(object? sender, EventArgs e) => Refresh();

    void OnAccountsChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed) RebuildAccounts();
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        Host.Activity.Changed -= OnActivityChanged;
        Host.AccountsChanged -= OnAccountsChanged;
        Host.ReauthRequested -= OnReauthRequested;
        Refreshed = null;
    }

    void OnActivityChanged()
    {
        if (_disposed || Interlocked.Exchange(ref _activityQueued, 1) != 0) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _activityQueued, 0);
            if (!_disposed) RefreshActivity();
        });
    }

    partial void OnActivityFilterChanged(string value) => RebuildActivity();
    partial void OnActivityErrorsOnlyChanged(bool value) => RebuildActivity();

    bool ActivityMatches(ActivityEntry a) =>
        (!ActivityErrorsOnly || a.Kind == ActivityKind.Error) &&
        (string.IsNullOrWhiteSpace(ActivityFilter) || a.Text.Contains(ActivityFilter.Trim(), StringComparison.CurrentCultureIgnoreCase));

    /// <summary>Filtro mudou: refaz a lista inteira (até 200 linhas).</summary>
    void RebuildActivity()
    {
        if (_disposed) return;
        _newestActivity = null;
        Activity.Clear();
        RefreshActivity();
    }

    /// <summary>Copia as linhas visíveis (com o filtro aplicado) — para colar num relato de problema.</summary>
    [RelayCommand]
    async Task CopyActivity()
    {
        var text = string.Join('\n', Activity.Select(a => $"{a.Time}  {(a.IsError ? "ERRO  " : "")}{a.Text}"));
        if (text.Length == 0) { Message = "Nada para copiar."; return; }
        var error = await CopyText(text);
        if (error == null) Message = Activity.Count == 1 ? "1 linha copiada." : $"{Activity.Count} linhas copiadas.";
        else ShowError("Não foi possível copiar: " + error);
    }

    void RefreshActivity()
    {
        var entries = Host.Activity.Entries.Where(ActivityMatches).Take(200).ToList();
        var added = _newestActivity == null ? entries.Count
            : entries.FindIndex(a => ReferenceEquals(a, _newestActivity));
        if (added < 0) added = entries.Count;
        var retained = Math.Min(Activity.Count, entries.Count - added);
        while (Activity.Count > retained) Activity.RemoveAt(Activity.Count - 1);
        for (var i = added - 1; i >= 0; i--)
        {
            var a = entries[i];
            Activity.Insert(0, new ActivityVm(a.Time.ToString("HH:mm:ss"), a.Kind switch
            {
                ActivityKind.Upload => "upload",
                ActivityKind.Download => "download",
                ActivityKind.Delete => "delete",
                ActivityKind.Move => "move",
                ActivityKind.Conflict => "warning",
                ActivityKind.Error => "error",
                _ => "info",
            }, a.Text, a.Kind == ActivityKind.Error));
        }
        _newestActivity = entries.FirstOrDefault();
        HasActivity = Activity.Count > 0;
    }

    public void RefreshDrives()
    {
        // garante que a lista reflete a configuração
        var cfgDrives = Host.Config.Drives;
        if (Drives.Count != cfgDrives.Count || Drives.Zip(cfgDrives).Any(p => p.First.Config != p.Second))
        {
            Drives.Clear();
            foreach (var d in cfgDrives) Drives.Add(new DriveVm(this, d));
        }
        HasDrives = Drives.Count > 0;
        foreach (var vm in Drives)
        {
            var acc = Host.Config.Accounts.FirstOrDefault(a => a.Id == vm.Config.AccountId);
            vm.Account = acc?.Username ?? "(conta removida)";
            var ds = Host.Engine.GetDrive(vm.Config.DriveId);
            if (ds == null)
            {
                vm.State = vm.Config.Enabled ? "Iniciando…" : "Desativada";
                continue;
            }
            var (files, dirs, cached, dirty) = Host.Db.Counts(ds.DriveId);
            vm.Stats = $"{files:N0} arquivos · {dirs:N0} pastas · {cached:N0} neste dispositivo" + (dirty > 0 ? $" · {dirty} a enviar" : "");
            vm.Quota = ds.QuotaTotal > 0 ? $"{Fmt.Bytes(ds.QuotaUsed)} de {Fmt.Bytes(ds.QuotaTotal)} usados" : "";
            vm.QuotaPercent = ds.QuotaTotal > 0 ? ds.QuotaUsed * 100.0 / ds.QuotaTotal : 0;
            vm.State = !ds.Ready ? "Carregando lista de arquivos…"
                : ds.AuthRequired ? "Login necessário"
                : ds.Offline ? "Sem conexão"
                : ds.LastError != null ? "Erro: " + ds.LastError
                : ds.LastSync is { } t ? $"Atualizado ({t:HH:mm:ss})" : "Sincronizando…";
        }
        foreach (var a in Accounts)
        {
            a.Drives = string.Join(", ", Host.Config.Drives.Where(d => d.AccountId == a.Account.Id).Select(d => d.DisplayName));
            a.NeedsLogin = Host.Engine.Drives.Any(d => d.Config.AccountId == a.Account.Id && d.AuthRequired);
        }
        if (!_disposed && !_cacheRefreshPending) _ = RefreshCacheAsync();
    }

    async Task RefreshCacheAsync()
    {
        _cacheRefreshPending = true;
        try
        {
            var bytes = await _cacheSampler.ReadAsync().ConfigureAwait(false);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed)
                    CacheText = $"Usando {Fmt.Bytes(bytes)} neste dispositivo (limite para arquivos não fixados: {(Host.Config.CacheLimitMB > 0 ? Fmt.Bytes(Host.Config.CacheLimitMB * 1024 * 1024) : "sem limite")})";
                _cacheRefreshPending = false;
            });
        }
        catch
        {
            // Cache pode desaparecer durante a limpeza; mantém a última leitura válida.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _cacheRefreshPending = false);
        }
    }

    sealed class CacheUsageSampler(Func<long> readBytes, Func<DateTimeOffset> clock)
    {
        readonly object _gate = new();
        Task<long>? _scan;
        DateTimeOffset _nextScan;

        public Task<long> ReadAsync()
        {
            lock (_gate)
            {
                if (_scan != null && (!_scan.IsCompleted || clock() < _nextScan)) return _scan;
                _nextScan = clock().AddSeconds(30);
                return _scan = Task.Run(readBytes);
            }
        }
    }

    public void RefreshDeleteAlerts()
    {
        var held = Host.Engine.Drives.Concat(Host.Engine.SharedDrives).Where(d => d.DeleteHold).ToList();
        foreach (var gone in DeleteAlerts.Where(a => !held.Contains(a.Drive)).ToList()) DeleteAlerts.Remove(gone);
        foreach (var d in held)
            if (DeleteAlerts.All(a => a.Drive != d)) DeleteAlerts.Add(new DeleteAlertVm(this, d));
        foreach (var a in DeleteAlerts) a.Update();
        HasDeleteAlerts = DeleteAlerts.Count > 0;
    }

    public void RefreshFailures()
    {
        var list = Host.Db.Failures();
        if (list.Count == Failures.Count && list.Select(f => f.Id).SequenceEqual(Failures.Select(f => f.Id))) return;
        Failures.Clear();
        foreach (var f in list)
            Failures.Add(new FailureVm(this, f.Id, AppHost.KindLabel(f.Kind), Host.FailurePath(f), f.Error,
                DateTimeOffset.FromUnixTimeMilliseconds(f.At).ToLocalTime().ToString("dd/MM HH:mm")));
        HasFailures = Failures.Count > 0;
        FailuresHeader = HasFailures ? $"Problemas ({Failures.Count})" : "Problemas";
    }

    /// <summary>Atualiza <see cref="QueueBlocked"/>: cabeças de fila que já falharam e estão esperando.</summary>
    public void RefreshQueue()
    {
        var lines = new List<string>();
        foreach (var h in Host.Db.QueueHeads().Where(h => h.Attempts > 0))
        {
            var drive = Host.Config.Drives.FirstOrDefault(d => d.DriveId == h.DriveId)?.DisplayName
                        ?? Host.Engine.GetDrive(h.DriveId)?.Config.DisplayName ?? h.DriveId;
            var path = h.Lid != 0 && Host.Db.Get(h.Lid) is { } it && Host.LocalPathOf(it) is { Length: > 0 } p ? p : h.Name ?? "?";
            var when = h.NextTry > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                ? $"próxima tentativa às {DateTimeOffset.FromUnixTimeMilliseconds(h.NextTry).ToLocalTime():HH:mm:ss}"
                : "tentando de novo agora";
            lines.Add($"Fila de \"{drive}\" parada em {AppHost.KindLabel(h.Kind)} \"{path}\": {h.Attempts} tentativas" +
                      (h.LastError != null ? $", último erro: {h.LastError}" : "") + $"; {when}. " +
                      $"Esperando atrás dela: {h.Behind} {(h.Behind == 1 ? "alteração" : "alterações")}.");
        }
        if (lines.SequenceEqual(QueueBlocked)) return;
        QueueBlocked.Clear();
        foreach (var l in lines) QueueBlocked.Add(l);
        HasQueueBlocked = QueueBlocked.Count > 0;
    }

    public void RefreshConflicts()
    {
        var list = Host.Db.Conflicts();
        if (list.Select(c => c.Id).SequenceEqual(Conflicts.Select(c => c.Id))) return;
        Conflicts.Clear();
        foreach (var c in list)
        {
            var it = Host.Db.Get(c.Lid);
            Conflicts.Add(new ConflictVm(this, c.Id, it != null ? Host.LocalPathOf(it) : c.Copy, c.Original, c.Reason,
                DateTimeOffset.FromUnixTimeMilliseconds(c.At).ToLocalTime().ToString("dd/MM HH:mm")));
        }
        HasConflicts = Conflicts.Count > 0;
        ConflictsHeader = HasConflicts ? $"Conflitos ({Conflicts.Count})" : "Conflitos";
    }

    public void DismissConflict(ConflictVm vm)
    {
        Host.Db.DismissConflict(vm.Id);
        RefreshConflicts();
    }

    [RelayCommand]
    void DismissAllConflicts()
    {
        foreach (var c in Conflicts.ToList()) Host.Db.DismissConflict(c.Id);
        RefreshConflicts();
    }

    public void RetryFailure(FailureVm vm)
    {
        Message = Host.RetryFailure(vm.Id) ? $"Tentando de novo: {vm.Path}" : "Essa operação não está mais pendente.";
        RefreshFailures();
    }

    public void DismissFailure(FailureVm vm)
    {
        Host.DismissFailure(vm.Id);
        RefreshFailures();
    }

    [RelayCommand]
    void RetryAll()
    {
        var n = Host.RetryAllFailures();
        Message = $"{n} operação(ões) devolvida(s) à fila.";
        RefreshFailures();
    }

    void RebuildAccounts()
    {
        Accounts.Clear();
        foreach (var a in Host.Config.Accounts) Accounts.Add(new AccountVm(this, a));
        AddLibraryCommand.NotifyCanExecuteChanged();
        RefreshDrives();
    }

    public void RebuildPinned()
    {
        Pinned.Clear();
        foreach (var it in Host.Db.AllPinned())
            Pinned.Add(new PinnedVm(this, it.Lid, Host.LocalPathOf(it), it.IsDir));
        HasPinned = Pinned.Count > 0;
    }

    // ---------- comandos ----------

    [RelayCommand] void OpenFolder() => Host.OpenFolder(Host.Config.MountPoint);
    [RelayCommand] void SyncNow() { Host.Engine.SyncNow(); Message = "Verificando alterações…"; }
    [RelayCommand] void TogglePause() { Host.Engine.Paused = !Host.Engine.Paused; Host.TrySaveConfig("Pausa da sincronização"); Refresh(); }

    /// <summary>Sair (Ctrl+Q, botão): com transferências em andamento, confirma antes — elas são interrompidas.</summary>
    [RelayCommand]
    async Task Quit()
    {
        var transfers = Host.Activity.Transfers.Count;
        if (transfers > 0 && !await ConfirmAsync(new ConfirmRequest("Sair do craftDriver?",
                $"{(transfers == 1 ? "Há 1 transferência" : $"Há {transfers} transferências")} em andamento. Elas são interrompidas e " +
                "continuam na próxima vez que o craftDriver abrir; até lá, a pasta sincronizada fica indisponível.", "Sair")))
            return;
        QuitRequested?.Invoke();
    }
    [RelayCommand] void OpenLog() => DesktopIntegration.OpenPath(AppPaths.LogFile);

    [RelayCommand]
    async Task AddAccount()
    {
        if (AddAccountRequested == null) return;
        if (await AddAccountRequested(null))
        {
            RebuildAccounts();
            Message = "Conta adicionada. A lista de arquivos está sendo carregada.";
        }
    }

    /// <summary>
    /// "Entrar novamente" da notificação de sessão expirada (chega numa thread do pool). O RequestShow só posta o
    /// ShowWindow (que cria a janela, se o app iniciou minimizado, e liga o AddAccountRequested): o login vai num
    /// job seguinte, de prioridade menor, para rodar depois dele.
    /// </summary>
    void OnReauthRequested(AccountConfig acc) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        Host.RequestShow();
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            if (!_disposed && Accounts.FirstOrDefault(a => a.Account.Id == acc.Id) is { } vm) await ReauthenticateAsync(vm);
        }, Avalonia.Threading.DispatcherPriority.Background);
    });

    public async Task ReauthenticateAsync(AccountVm vm)
    {
        if (AddAccountRequested == null) return;
        if (await AddAccountRequested(vm.Account))
        {
            Host.Engine.SyncNow();
            RebuildAccounts();
            Refresh();
        }
    }

    public async Task RemoveAccountAsync(AccountVm vm)
    {
        var drives = Host.Config.Drives.Where(d => d.AccountId == vm.Account.Id).ToList();
        var pending = drives.Sum(d => Host.Db.PendingOps(d.DriveId));
        var text = $"As pastas de {vm.Username} ({(drives.Count == 0 ? "nenhuma" : string.Join(", ", drives.Select(d => d.DisplayName)))}) " +
                   "somem deste computador e o conteúdo baixado é apagado daqui. Nada é apagado na nuvem; para voltar, entre de novo.";
        if (pending > 0) text += $"\n\nAtenção: {Pending(pending)} serão descartadas.";
        if (!await ConfirmAsync(new ConfirmRequest($"Desvincular {vm.Username}?", text, "Desvincular", Danger: true))) return;
        await Host.RemoveAccountAsync(vm.Account.Id);
        RebuildAccounts();
        Message = $"Conta {vm.Username} removida deste computador.";
    }

    public void RequestAddLibrary(AccountConfig? acc) => AddLibraryRequested?.Invoke(acc);

    /// <summary>Abre "Escolher pastas" (sincronização seletiva) da drive; a janela é criada pela view.</summary>
    public event Action<ChooseFoldersViewModel>? ChooseFoldersRequested;

    public void RequestChooseFolders(string driveId)
    {
        if (Host.Engine.GetDrive(driveId) == null)
        {
            Message = "Essa conta/biblioteca ainda não está sincronizando.";
            return;
        }
        ChooseFoldersRequested?.Invoke(new ChooseFoldersViewModel(Host.Engine, driveId));
    }

    /// <summary>Bibliotecas do SharePoint só existem em conta corporativa/escolar.</summary>
    bool CanAddLibrary() => Host.Config.Accounts.Any(a => !a.IsPersonal);

    [RelayCommand(CanExecute = nameof(CanAddLibrary))]
    void AddLibrary() => RequestAddLibrary(Host.Config.Accounts.FirstOrDefault(a => !a.IsPersonal));

    public void LibraryAdded()
    {
        RefreshDrives();
        RebuildAccounts();
    }

    public void SetDriveEnabled(DriveVm vm, bool enabled)
    {
        if (vm.Config.Enabled == enabled) return;
        Host.SetDriveEnabled(vm.Config.DriveId, enabled);
    }

    public async Task RemoveDriveAsync(DriveVm vm)
    {
        var pending = Host.Db.PendingOps(vm.Config.DriveId);
        var text = $"A pasta \"{vm.DisplayName}\" some deste computador e o conteúdo baixado é apagado daqui. " +
                   "Nada é apagado na nuvem; dá para adicionar de novo depois.";
        if (pending > 0) text += $"\n\nAtenção: {Pending(pending)} serão descartadas.";
        if (!await ConfirmAsync(new ConfirmRequest($"Parar de sincronizar {vm.DisplayName}?", text, "Parar de sincronizar", Danger: true))) return;
        Host.RemoveDrive(vm.Config.DriveId);
        RefreshDrives();
        Message = $"{vm.DisplayName} removida deste computador (nada foi apagado na nuvem).";
    }

    [RelayCommand]
    async Task PinFolder()
    {
        if (PickFolderRequested == null) return;
        var path = await PickFolderRequested();
        if (path == null) return;
        var item = Host.ItemFromLocalPath(path);
        if (item == null)
        {
            Message = "Escolha uma pasta dentro de " + Host.Config.MountPoint;
            return;
        }
        Host.Engine.SetPinned(item.Lid, true);
        RebuildPinned();
        Message = $"'{item.Name}' ficará sempre disponível neste dispositivo.";
    }

    public void Unpin(PinnedVm vm)
    {
        Host.Engine.SetPinned(vm.Lid, false);
        RebuildPinned();
    }

    [RelayCommand]
    async Task FreeAll()
    {
        // inclui as sub-drives de "Compartilhados comigo"/atalhos (antes ficavam com o cache cheio)
        var ready = Host.Engine.Drives.Concat(Host.Engine.SharedDrives).Where(d => d.Ready).ToList();
        var cached = ready.Sum(d => Host.Db.Counts(d.DriveId).cached);
        if (cached == 0)
        {
            Message = "Nenhum arquivo baixado para liberar.";
            return;
        }
        if (!await ConfirmAsync(new ConfirmRequest("Liberar espaço de tudo?",
                $"{(cached == 1 ? "1 arquivo baixado" : $"{cached:N0} arquivos baixados")} deixam de ocupar espaço neste dispositivo e as pastas " +
                "fixadas deixam de ficar sempre disponíveis. Tudo continua na nuvem e é baixado de novo ao abrir (precisa de internet).",
                "Liberar espaço"))) return;
        var n = 0;
        foreach (var d in ready)
            n += Host.Engine.FreeUpSpace(d.RootLid);
        RebuildPinned();
        Message = $"{n} arquivo(s) removido(s) deste dispositivo; continuam na nuvem.";
    }

    [RelayCommand]
    async Task SaveSettings()
    {
        var c = Host.Config;
        var mount = (MountPoint ?? "").Trim();
        var remount = !Path.IsPathFullyQualified(mount) || Path.GetFullPath(mount) != Path.GetFullPath(c.MountPoint);
        if (remount && ValidateMountPoint(mount, c.CacheDir) is { } mountError)
        {
            ShowError(mountError);
            return;
        }
        var cacheDir = (CacheDir ?? "").Trim();
        var cacheChanged = !Path.IsPathFullyQualified(cacheDir) || Path.GetFullPath(cacheDir) != Path.GetFullPath(c.CacheDir);
        if (cacheChanged && ValidateCacheDir(cacheDir, remount ? mount : c.MountPoint) is { } cacheError)
        {
            ShowError(cacheError);
            return;
        }
        var clientId = (ClientId ?? "").Trim();
        if (!Guid.TryParse(clientId, out _))
        {
            ShowError("O Client ID precisa ser um identificador do Entra ID (ex.: 00000000-0000-0000-0000-000000000000).");
            return;
        }
        if (remount && !await ConfirmAsync(new ConfirmRequest("Mudar a pasta sincronizada?",
                $"A pasta sai de {c.MountPoint} e passa para {mount}. Programas com arquivos abertos na pasta atual perdem o acesso a eles; " +
                "atalhos e favoritos que apontam para o caminho antigo deixam de funcionar.", "Mudar")))
            return;
        MountPoint = mount;
        c.CacheLimitMB = (long)(CacheLimitGB * 1024);
        c.DownloadConcurrency = (int)Math.Clamp(DownloadConcurrency, 1, 32);
        c.DownloadBytesPerSecond = (long)(Math.Max(0, DownloadLimitMBps) * 1_000_000);
        c.UploadBytesPerSecond = (long)(Math.Max(0, UploadLimitMBps) * 1_000_000);
        Host.Engine.ApplyTransferLimits();
        c.PollIntervalSeconds = (int)Math.Max(10, PollInterval);
        c.StartMinimized = StartMinimized;
        c.MassDeleteThreshold = (int)Math.Max(0, MassDeleteThreshold);
        c.BlockedProcesses = Split(BlockedProcesses);
        c.LocalOnlyPatterns = Split(LocalOnlyPatterns);
        var clientChanged = clientId != c.ClientId;
        c.ClientId = clientId;
        if (cacheChanged) c.CacheDir = Path.GetFullPath(cacheDir); // só vale ao reiniciar: o motor segura o cache atual
        c.Theme = ThemeService.Values[Math.Clamp(ThemeIndex, 0, ThemeService.Values.Length - 1)];
        ThemeService.Apply(c);
        // systemctl --user (até 3 × 15 s) fora da thread de UI
        bool startWithSystem = StartWithSystem, systemd = UseSystemdAutostart;
        var autostartNote = await Task.Run(() => SetAutostart(startWithSystem, systemd ? AutostartMode.Systemd : AutostartMode.Xdg));
        string? mountProblem = null;
        if (remount)
        {
            // pasta ocupada (EBUSY): a montagem continua na antiga; gravar a nova faria a tela e a configuração mentirem
            if (await Host.UnmountAsync())
            {
                c.MountPoint = MountPoint;
                Host.Mount();
            }
            else
            {
                mountProblem = $"A pasta sincronizada continua em {c.MountPoint}: {AppHost.BusyMountText}.";
                MountPoint = c.MountPoint;
            }
        }
        var saved = Host.TrySaveConfig("Configurações");
        Host.Engine.NotifyPinChanged();
        if (clientChanged || cacheChanged) RestartNeeded = true;
        var notes = new List<string>();
        if (clientChanged) notes.Add("o novo Client ID vale ao reiniciar (depois, entre novamente nas contas)");
        if (cacheChanged) notes.Add($"o cache passa para {c.CacheDir} ao reiniciar; os arquivos baixados são baixados de novo quando forem abertos, e a pasta antiga pode ser apagada");
        var text = !saved ? "As configurações valem só até fechar o craftDriver: não foram gravadas no disco. " +
                            (c.ReadOnly ? c.LoadWarning : "Veja o log (aba Visão geral).")
            : notes.Count == 0 ? "Configurações salvas."
            : "Configurações salvas — " + string.Join("; ", notes) + ". Use \"Reiniciar agora\".";
        if (mountProblem != null) text = mountProblem + " " + text;
        if (autostartNote != null) text += " " + autostartNote; // aviso do início automático não pode sumir
        if (!saved || mountProblem != null) ShowError(text);
        else Message = text;
    }

    static readonly string[] SystemDirs = ["/", "/bin", "/boot", "/dev", "/etc", "/home", "/lib", "/lib64", "/media", "/mnt",
        "/opt", "/proc", "/root", "/run", "/sbin", "/srv", "/sys", "/tmp", "/usr", "/var"];

    static bool IsInside(string path, string dir)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var d = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        return p == d || p.StartsWith(d + "/", StringComparison.Ordinal);
    }

    static bool IsSystemDir(string path)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var home = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return SystemDirs.Contains(p) || p == home;
    }

    /// <summary>Motivo para recusar <paramref name="path"/> como ponto de montagem; null = aceito.</summary>
    public static string? ValidateMountPoint(string path, string cacheDir)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return "A pasta sincronizada precisa de um caminho absoluto (ex.: /home/voce/OneDrive).";
        if (IsSystemDir(path))
            return $"{path} é uma pasta do sistema ou a sua pasta pessoal; escolha uma subpasta, como ~/OneDrive.";
        if (File.Exists(path))
            return $"{path} é um arquivo, não uma pasta.";
        if (IsInside(path, cacheDir) || IsInside(cacheDir, path))
            return "A pasta sincronizada não pode ficar dentro do cache de conteúdo (nem o cache dentro dela).";
        try
        {
            if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                return $"{path} precisa estar vazia: o conteúdo dela ficaria escondido sob os arquivos da nuvem.";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return $"Sem acesso a {path}: {ex.Message}";
        }
        return null;
    }

    /// <summary>Motivo para recusar <paramref name="path"/> como pasta do cache; null = aceito.</summary>
    public string? ValidateCacheDir(string path, string mountPoint)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return "A pasta do cache precisa de um caminho absoluto.";
        if (IsSystemDir(path))
            return $"{path} é uma pasta do sistema ou a sua pasta pessoal; escolha uma subpasta.";
        if (File.Exists(path))
            return $"{path} é um arquivo, não uma pasta.";
        if (IsInside(path, mountPoint) || IsInside(mountPoint, path))
            return "O cache não pode ficar dentro da pasta sincronizada (nem ela dentro do cache).";
        var pending = Host.Db.PendingOps();
        if (pending > 0)
            return $"Há {Pending(pending)}: elas só existem no cache atual. Espere o envio terminar (ou resolva a aba Problemas) e tente de novo.";
        return null;
    }

    [RelayCommand]
    async Task BrowseMountPoint()
    {
        if (PickSettingsFolderRequested != null && await PickSettingsFolderRequested("Pasta sincronizada") is { } p) MountPoint = p;
    }

    [RelayCommand]
    async Task BrowseCacheDir()
    {
        if (PickSettingsFolderRequested != null && await PickSettingsFolderRequested("Pasta do cache de conteúdo") is { } p) CacheDir = p;
    }

    static List<string> Split(string s) =>
        s.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();

    [RelayCommand]
    async Task InstallIntegration()
    {
        try
        {
            // update-desktop-database e afins esperam processos externos: fora da thread de UI
            var done = await Task.Run(DesktopIntegration.InstallAll);
            Message = "Instalado: " + string.Join("; ", done) + ".";
        }
        catch (Exception ex) { ShowError("Falha ao instalar a integração: " + ex.Message); }
    }

    [RelayCommand]
    async Task Remount()
    {
        if (!await Host.UnmountAsync())
        {
            ShowError($"Não foi possível remontar {Host.Config.MountPoint}: {AppHost.BusyMountText}.");
            Refresh();
            return;
        }
        Host.Mount();
        Refresh();
    }
}
