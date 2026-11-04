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
}

public sealed partial class TransferVm : ViewModelBase
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Direction { get; init; } = "";
    [ObservableProperty] public partial double Percent { get; set; }
    [ObservableProperty] public partial string Detail { get; set; } = "";
}

public sealed record ActivityVm(string Time, string Icon, string Text, bool IsError);

public sealed partial class AccountVm(MainViewModel main, AccountConfig acc) : ViewModelBase
{
    public AccountConfig Account { get; } = acc;
    public string Username => Account.Username;
    public string TypeLabel => Account.IsPersonal ? "Conta pessoal (Microsoft)" : "Conta corporativa ou de estudante (Microsoft 365)";
    public bool IsBusiness => !Account.IsPersonal;
    [ObservableProperty] public partial string Drives { get; set; } = "";
    [ObservableProperty] public partial bool NeedsLogin { get; set; }

    [RelayCommand] Task Remove() => main.RemoveAccountAsync(this);
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
    [ObservableProperty] public partial string Account { get; set; } = "";
    [ObservableProperty] public partial bool Enabled { get; set; } = cfg.Enabled;
    [ObservableProperty] public partial string Stats { get; set; } = "";
    [ObservableProperty] public partial string Quota { get; set; } = "";
    [ObservableProperty] public partial double QuotaPercent { get; set; }
    [ObservableProperty] public partial string State { get; set; } = "";

    partial void OnEnabledChanged(bool value) => main.SetDriveEnabled(this, value);

    [RelayCommand] void Remove() => main.RemoveDrive(this);
    [RelayCommand] void OpenWeb() { if (Config.WebUrl != null) DesktopIntegration.OpenPath(Config.WebUrl); }
    [RelayCommand] void OpenFolder() => DesktopIntegration.OpenPath(Path.Combine(main.Host.Config.MountPoint, Config.DisplayName.Replace('/', '-')));
}

public sealed partial class DeleteAlertVm(MainViewModel main, DriveSync drive) : ViewModelBase
{
    public DriveSync Drive { get; } = drive;
    [ObservableProperty] public partial string Text { get; set; } = "";

    public void Update() => Text =
        $"{Drive.HeldDeleteCount} itens foram apagados de \"{Drive.Config.DisplayName}\". Nada foi excluído da nuvem ainda. " +
        "Se confirmar, eles vão para a Lixeira do OneDrive (recuperáveis por 30 dias). Se restaurar, voltam para a pasta.";

    [RelayCommand] void Confirm() { Drive.ConfirmDeletes(); main.RefreshDeleteAlerts(); }
    [RelayCommand] async Task Restore() { await Drive.RestoreDeletesAsync(); main.RefreshDeleteAlerts(); }
}

public sealed partial class PinnedVm(MainViewModel main, long lid, string path, bool isDir) : ViewModelBase
{
    public long Lid { get; } = lid;
    public string Path { get; } = path;
    public string Icon { get; } = isDir ? "📁" : "📄";
    [RelayCommand] void Unpin() => main.Unpin(this);
}

public sealed partial class MainViewModel : ViewModelBase
{
    public AppHost Host { get; }
    public event Action? QuitRequested;
    public event Action<AccountConfig?>? AddLibraryRequested;
    public event Func<Task<bool>>? AddAccountRequested;
    public event Func<Task<string?>>? PickFolderRequested;

    public ObservableCollection<TransferVm> Transfers { get; } = [];
    public ObservableCollection<ActivityVm> Activity { get; } = [];
    public ObservableCollection<AccountVm> Accounts { get; } = [];
    public ObservableCollection<DriveVm> Drives { get; } = [];
    public ObservableCollection<PinnedVm> Pinned { get; } = [];
    public ObservableCollection<DeleteAlertVm> DeleteAlerts { get; } = [];
    [ObservableProperty] public partial bool HasDeleteAlerts { get; set; }
    [ObservableProperty] public partial decimal MassDeleteThreshold { get; set; }

    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial string StatusDetail { get; set; } = "";
    [ObservableProperty] public partial string StatusIcon { get; set; } = "☁";
    [ObservableProperty] public partial string MountText { get; set; } = "";
    [ObservableProperty] public partial bool IsMounted { get; set; }
    [ObservableProperty] public partial string PauseText { get; set; } = "Pausar";
    [ObservableProperty] public partial bool HasAccounts { get; set; }
    [ObservableProperty] public partial bool NeedsLogin { get; set; }
    [ObservableProperty] public partial string CacheText { get; set; } = "";
    [ObservableProperty] public partial string Message { get; set; } = "";
    [ObservableProperty] public partial bool HasTransfers { get; set; }

    // configurações
    [ObservableProperty] public partial string MountPoint { get; set; }
    [ObservableProperty] public partial string CacheDir { get; set; }
    [ObservableProperty] public partial decimal CacheLimitGB { get; set; }
    [ObservableProperty] public partial decimal PollInterval { get; set; }
    [ObservableProperty] public partial bool StartWithSystem { get; set; }
    [ObservableProperty] public partial bool StartMinimized { get; set; }
    [ObservableProperty] public partial string BlockedProcesses { get; set; }
    [ObservableProperty] public partial string LocalOnlyPatterns { get; set; }
    [ObservableProperty] public partial string ClientId { get; set; }
    public string TokenStorage => Host.Auth.CacheStorage;
    public string LogFile => AppPaths.LogFile;

    long _lastActivityCount = -1;
    int _tick;

    public MainViewModel(AppHost host)
    {
        Host = host;
        var c = host.Config;
        MountPoint = c.MountPoint;
        CacheDir = c.CacheDir;
        CacheLimitGB = c.CacheLimitMB / 1024m;
        PollInterval = c.PollIntervalSeconds;
        StartWithSystem = DesktopIntegration.AutostartEnabled;
        StartMinimized = c.StartMinimized;
        BlockedProcesses = string.Join(", ", c.BlockedProcesses);
        LocalOnlyPatterns = string.Join(", ", c.LocalOnlyPatterns);
        ClientId = c.ClientId;
        MassDeleteThreshold = c.MassDeleteThreshold;

        RebuildAccounts();
        RebuildPinned();
        Refresh();
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Refresh();
        timer.Start();
    }

    // ---------- atualização periódica ----------

    void Refresh()
    {
        var e = Host.Engine;
        var st = e.Status;
        StatusText = AppHost.StatusText(st);
        StatusIcon = st switch
        {
            EngineStatus.UpToDate => "✔",
            EngineStatus.Syncing => "⟳",
            EngineStatus.Paused => "⏸",
            EngineStatus.Offline => "⚠",
            EngineStatus.AuthRequired or EngineStatus.Error => "✖",
            EngineStatus.DeleteConfirmation => "⚠",
            _ => "☁",
        };
        var pending = Host.Db.PendingOps();
        var lastSync = e.Drives.Select(d => d.LastSync).Where(t => t != null).DefaultIfEmpty().Max();
        StatusDetail = st switch
        {
            EngineStatus.Syncing => $"{pending} alteração(ões) na fila, {Host.Activity.Transfers.Count} transferência(s) em andamento",
            EngineStatus.UpToDate when lastSync != null => $"Última verificação às {lastSync:HH:mm:ss}",
            EngineStatus.NoAccounts => "Adicione uma conta Microsoft para começar.",
            EngineStatus.AuthRequired => "A sessão expirou. Use \"Entrar novamente\" na aba Contas.",
            EngineStatus.DeleteConfirmation => "Muitos arquivos foram apagados. Confirme ou restaure abaixo.",
            EngineStatus.Offline => pending > 0 ? $"{pending} alteração(ões) aguardando conexão" : "Os arquivos disponíveis offline continuam acessíveis.",
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
        foreach (var gone in Transfers.Where(t => current.All(c => c.Id != t.Id)).ToList()) Transfers.Remove(gone);
        foreach (var t in current)
        {
            var vm = Transfers.FirstOrDefault(x => x.Id == t.Id);
            if (vm == null)
            {
                vm = new TransferVm { Id = t.Id, Name = t.Name, Direction = t.Upload ? "↑" : "↓" };
                Transfers.Add(vm);
            }
            vm.Percent = t.Percent;
            vm.Detail = $"{Fmt.Bytes(t.Done)} de {Fmt.Bytes(t.Total)}";
        }

        HasTransfers = Transfers.Count > 0;
        RefreshDeleteAlerts();

        // atividade
        var entries = Host.Activity.Entries;
        if (entries.Count != _lastActivityCount || (entries.Count > 0 && Activity.Count > 0 && entries[0].Text != Activity[0].Text))
        {
            _lastActivityCount = entries.Count;
            Activity.Clear();
            foreach (var a in entries.Take(200))
                Activity.Add(new ActivityVm(a.Time.ToString("HH:mm:ss"), a.Kind switch
                {
                    ActivityKind.Upload => "↑",
                    ActivityKind.Download => "↓",
                    ActivityKind.Delete => "🗑",
                    ActivityKind.Move => "➜",
                    ActivityKind.Conflict => "⚠",
                    ActivityKind.Error => "✖",
                    _ => "•",
                }, a.Text, a.Kind == ActivityKind.Error));
        }

        if (_tick++ % 5 == 0) RefreshDrives();
    }

    void RefreshDrives()
    {
        // garante que a lista reflete a configuração
        var cfgDrives = Host.Config.Drives;
        if (Drives.Count != cfgDrives.Count || Drives.Zip(cfgDrives).Any(p => p.First.Config != p.Second))
        {
            Drives.Clear();
            foreach (var d in cfgDrives) Drives.Add(new DriveVm(this, d));
        }
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
        try { CacheText = $"Usando {Fmt.Bytes(Host.Engine.Cache.TotalBytes())} neste dispositivo (limite para arquivos não fixados: {(Host.Config.CacheLimitMB > 0 ? Fmt.Bytes(Host.Config.CacheLimitMB * 1024 * 1024) : "sem limite")})"; }
        catch { }
    }

    public void RefreshDeleteAlerts()
    {
        var held = Host.Engine.Drives.Where(d => d.DeleteHold).ToList();
        foreach (var gone in DeleteAlerts.Where(a => !held.Contains(a.Drive)).ToList()) DeleteAlerts.Remove(gone);
        foreach (var d in held)
            if (DeleteAlerts.All(a => a.Drive != d)) DeleteAlerts.Add(new DeleteAlertVm(this, d));
        foreach (var a in DeleteAlerts) a.Update();
        HasDeleteAlerts = DeleteAlerts.Count > 0;
    }

    void RebuildAccounts()
    {
        Accounts.Clear();
        foreach (var a in Host.Config.Accounts) Accounts.Add(new AccountVm(this, a));
        RefreshDrives();
    }

    public void RebuildPinned()
    {
        Pinned.Clear();
        foreach (var it in Host.Db.AllPinned())
            Pinned.Add(new PinnedVm(this, it.Lid, Host.LocalPathOf(it), it.IsDir));
    }

    // ---------- comandos ----------

    [RelayCommand] void OpenFolder() => DesktopIntegration.OpenPath(Host.Config.MountPoint);
    [RelayCommand] void SyncNow() { Host.Engine.SyncNow(); Message = "Verificando alterações…"; }
    [RelayCommand] void TogglePause() { Host.Engine.Paused = !Host.Engine.Paused; Host.Config.Save(); Refresh(); }
    [RelayCommand] void Quit() => QuitRequested?.Invoke();
    [RelayCommand] void OpenLog() => DesktopIntegration.OpenPath(AppPaths.LogFile);

    [RelayCommand]
    async Task AddAccount()
    {
        if (AddAccountRequested == null) return;
        if (await AddAccountRequested())
        {
            RebuildAccounts();
            Message = "Conta adicionada. A lista de arquivos está sendo carregada.";
        }
    }

    public async Task RemoveAccountAsync(AccountVm vm)
    {
        await Host.RemoveAccountAsync(vm.Account.Id);
        RebuildAccounts();
        Message = $"Conta {vm.Username} removida deste computador.";
    }

    public void RequestAddLibrary(AccountConfig? acc) => AddLibraryRequested?.Invoke(acc);

    [RelayCommand]
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

    public void RemoveDrive(DriveVm vm)
    {
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
    void FreeAll()
    {
        var n = 0;
        foreach (var d in Host.Engine.Drives.Where(d => d.Ready))
            n += Host.Engine.FreeUpSpace(d.RootLid);
        RebuildPinned();
        Message = $"{n} arquivo(s) removido(s) deste dispositivo; continuam na nuvem.";
    }

    [RelayCommand]
    async Task SaveSettings()
    {
        var c = Host.Config;
        var remount = Path.GetFullPath(MountPoint) != Path.GetFullPath(c.MountPoint);
        c.CacheLimitMB = (long)(CacheLimitGB * 1024);
        c.PollIntervalSeconds = (int)Math.Max(10, PollInterval);
        c.StartMinimized = StartMinimized;
        c.MassDeleteThreshold = (int)Math.Max(0, MassDeleteThreshold);
        c.BlockedProcesses = Split(BlockedProcesses);
        c.LocalOnlyPatterns = Split(LocalOnlyPatterns);
        var clientChanged = ClientId.Trim() != c.ClientId;
        c.ClientId = ClientId.Trim();
        DesktopIntegration.SetAutostart(StartWithSystem);
        if (remount)
        {
            await Host.UnmountAsync();
            c.MountPoint = MountPoint;
            Host.Mount();
        }
        c.Save();
        Host.Engine.NotifyPinChanged();
        Message = clientChanged
            ? "Configurações salvas. Reinicie o app e entre novamente para usar o novo Client ID."
            : "Configurações salvas.";
    }

    static List<string> Split(string s) =>
        s.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();

    [RelayCommand]
    void InstallIntegration()
    {
        try
        {
            var done = DesktopIntegration.InstallAll();
            Message = "Instalado: " + string.Join("; ", done) + ".";
        }
        catch (Exception ex) { Message = "Falha: " + ex.Message; }
    }

    [RelayCommand]
    async Task Remount()
    {
        await Host.UnmountAsync();
        Host.Mount();
        Refresh();
    }
}
