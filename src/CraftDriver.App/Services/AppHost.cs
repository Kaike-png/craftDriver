using CraftDriver.Core.Auth;
using CraftDriver.Core.Config;
using CraftDriver.Core.Graph;
using CraftDriver.Core.Ipc;
using CraftDriver.Core.State;
using CraftDriver.Core.Sync;
using CraftDriver.Core.Vfs;
using CraftDriver.Fuse;

namespace CraftDriver.App.Services;

/// <summary>Raiz de composição: liga autenticação, motor, montagem FUSE e IPC.</summary>
public sealed class AppHost
{
    public AppConfig Config { get; }
    public AuthService Auth { get; }
    public StateDb Db { get; }
    public ActivityLog Activity { get; }
    public SyncEngine Engine { get; }
    public VirtualFs Fs { get; }
    IpcServer? _ipc;
    Task? _mountTask;

    public string? MountError { get; private set; }
    public bool Mounted => FuseHost.IsMounted;
    public event Action? ShowRequested;
    public event Action? QuitRequested;

    AppHost(AppConfig cfg, AuthService auth)
    {
        Config = cfg;
        Auth = auth;
        Activity = new ActivityLog(AppPaths.LogFile);
        Db = new StateDb(AppPaths.DatabaseFile);
        Engine = new SyncEngine(cfg, Db, new ContentCache(cfg.CacheDir), Activity, dc => ClientFor(dc.AccountId));
        Fs = new VirtualFs(Engine);
        FuseHost.Log = s => Activity.Debug(s);
        Engine.MassDeleteDetected += d =>
        {
            DesktopIntegration.Notify("craftDriver — muitos arquivos apagados",
                $"{d.HeldDeleteCount} itens foram apagados de \"{d.Config.DisplayName}\". Nada foi excluído da nuvem ainda: abra o craftDriver para confirmar ou restaurar.");
            ShowRequested?.Invoke();
        };
    }

    public static async Task<AppHost> CreateAsync()
    {
        AppPaths.EnsureDirs();
        var cfg = AppConfig.Load();
        var auth = await AuthService.CreateAsync(cfg);
        return new AppHost(cfg, auth);
    }

    public GraphClient ClientFor(string accountId) =>
        new((force, ct) => Auth.GetTokenAsync(accountId, force, ct));

    public void Start()
    {
        Activity.Add(ActivityKind.Info, $"Iniciado. Tokens em: {Auth.CacheStorage}");
        Engine.Start();
        Mount();
        try
        {
            _ipc = new IpcServer(AppPaths.SocketPath, HandleIpcAsync);
        }
        catch (Exception ex)
        {
            Activity.Add(ActivityKind.Error, "IPC indisponível: " + ex.Message);
        }
    }

    public void Mount()
    {
        if (Mounted) return;
        try
        {
            _mountTask = FuseHost.MountAsync(Fs, Config.MountPoint);
            MountError = null;
            Activity.Add(ActivityKind.Info, $"Montado em {Config.MountPoint}");
            _mountTask.ContinueWith(_ => Activity.Add(ActivityKind.Info, "Ponto de montagem desconectado"));
        }
        catch (Exception ex)
        {
            MountError = ex.Message;
            Activity.Add(ActivityKind.Error, "Falha ao montar: " + ex.Message);
        }
    }

    public async Task UnmountAsync()
    {
        if (!Mounted) return;
        FuseHost.Unmount();
        if (_mountTask != null) await Task.WhenAny(_mountTask, Task.Delay(10_000));
        if (Fs.OpenHandles > 0) Activity.Add(ActivityKind.Info, "Havia arquivos abertos durante a desmontagem");
    }

    public async Task ShutdownAsync()
    {
        try { await UnmountAsync(); } catch { }
        Engine.Stop();
        _ipc?.Dispose();
        Config.Save();
    }

    // ---------- contas ----------

    public Task<AccountConfig> AddAccountAsync(bool personal, Func<string, string, string, Task> onCode, CancellationToken ct) =>
        AddAccountAsync(c => Auth.SignInWithDeviceCodeAsync(personal, onCode, c), ct);

    public Task<AccountConfig> AddAccountWithBrowserAsync(bool personal, Func<Uri, CancellationToken, Task<Uri>> getRedirect, CancellationToken ct) =>
        AddAccountAsync(c => Auth.SignInWithBrowserAsync(personal, getRedirect, c), ct);

    public Task<AccountConfig> AddAccountWithSystemBrowserAsync(bool personal, Func<Uri, Task> openBrowser, CancellationToken ct) =>
        AddAccountAsync(c => Auth.SignInWithSystemBrowserAsync(personal, openBrowser, c), ct);

    async Task<AccountConfig> AddAccountAsync(Func<CancellationToken, Task<AccountConfig>> signIn, CancellationToken ct)
    {
        var acc = await signIn(ct);
        var existing = Config.Accounts.FirstOrDefault(a => a.Id == acc.Id);
        if (existing != null) Config.Accounts.Remove(existing);
        Config.Accounts.Add(acc);

        var client = ClientFor(acc.Id);
        var drive = await client.GetMyDriveAsync(ct);
        if (Config.Drives.All(d => d.DriveId != drive.Id))
        {
            var label = acc.IsPersonal ? "OneDrive - Pessoal" : "OneDrive - " + Domain(acc.Username);
            Config.Drives.Add(new DriveConfig
            {
                DriveId = drive.Id, AccountId = acc.Id, DisplayName = UniqueDisplayName(label),
                Kind = acc.IsPersonal ? "personal" : "business", WebUrl = drive.WebUrl,
            });
        }
        Config.Save();
        Engine.ReloadDrives();
        Activity.Add(ActivityKind.Info, $"Conta adicionada: {acc.Username}");
        return acc;
    }

    static string Domain(string username)
    {
        var at = username.IndexOf('@');
        if (at < 0) return username;
        var domain = username[(at + 1)..];
        var dot = domain.IndexOf('.');
        return dot > 0 ? domain[..dot] : domain;
    }

    public string UniqueDisplayName(string wanted)
    {
        wanted = wanted.Replace('/', '-').Trim();
        var name = wanted;
        for (var i = 2; Config.Drives.Any(d => d.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)); i++)
            name = $"{wanted} ({i})";
        return name;
    }

    public async Task RemoveAccountAsync(string accountId)
    {
        foreach (var d in Config.Drives.Where(d => d.AccountId == accountId).ToList())
            RemoveDrive(d.DriveId, save: false);
        Config.Accounts.RemoveAll(a => a.Id == accountId);
        Config.Save();
        await Auth.SignOutAsync(accountId);
    }

    /// <summary>Refaz o login de uma conta cuja sessão expirou.</summary>
    public async Task ReauthenticateAsync(bool personal, Func<string, string, string, Task> onCode, CancellationToken ct)
    {
        await AddAccountAsync(personal, onCode, ct);
        Engine.SyncNow();
    }

    // ---------- bibliotecas (SharePoint) ----------

    public DriveConfig AddLibrary(string accountId, SiteInfo site, GraphDrive drive, string displayName)
    {
        var existing = Config.Drives.FirstOrDefault(d => d.DriveId == drive.Id);
        if (existing != null) return existing;
        var dc = new DriveConfig
        {
            DriveId = drive.Id, AccountId = accountId, DisplayName = UniqueDisplayName(displayName),
            Kind = "documentLibrary", SiteName = site.DisplayName, WebUrl = drive.WebUrl ?? site.WebUrl,
        };
        Config.Drives.Add(dc);
        Config.Save();
        Engine.ReloadDrives();
        Activity.Add(ActivityKind.Info, $"Biblioteca adicionada: {dc.DisplayName}");
        return dc;
    }

    public void RemoveDrive(string driveId, bool save = true)
    {
        var pending = Db.PendingOps(driveId);
        if (pending > 0) Activity.Add(ActivityKind.Info, $"{pending} alterações não enviadas foram descartadas");
        Engine.ForgetDrive(driveId);
        Config.Drives.RemoveAll(d => d.DriveId == driveId);
        if (save) Config.Save();
    }

    public void SetDriveEnabled(string driveId, bool enabled)
    {
        var d = Config.Drives.FirstOrDefault(x => x.DriveId == driveId);
        if (d == null) return;
        d.Enabled = enabled;
        Config.Save();
        Engine.ReloadDrives();
    }

    // ---------- caminhos locais ----------

    public Item? ItemFromLocalPath(string path)
    {
        var mount = Path.GetFullPath(Config.MountPoint).TrimEnd('/');
        var full = Path.GetFullPath(path).TrimEnd('/');
        if (full != mount && !full.StartsWith(mount + "/")) return null;
        var rel = full.Length == mount.Length ? "/" : full[mount.Length..];
        return Fs.Resolve(rel)?.Item;
    }

    public string LocalPathOf(Item item)
    {
        var drive = Engine.GetDrive(item.DriveId);
        if (drive == null) return "";
        var parts = new List<string>();
        var cur = item;
        while (cur != null && cur.Lid != drive.RootLid)
        {
            parts.Add(cur.Name);
            cur = Db.Get(cur.ParentLid);
        }
        parts.Add(drive.Config.DisplayName.Replace('/', '-'));
        parts.Reverse();
        return Path.Combine([Config.MountPoint, .. parts]);
    }

    // ---------- comandos IPC (CLI e menus do gerenciador de arquivos) ----------

    async Task<IpcResponse> HandleIpcAsync(IpcRequest req)
    {
        switch (req.Cmd)
        {
            case "ping":
                return new(true, "pong");
            case "show":
                ShowRequested?.Invoke();
                return new(true, "ok");
            case "quit":
                QuitRequested?.Invoke();
                return new(true, "encerrando");
            case "sync":
                Engine.SyncNow();
                return new(true, "Sincronização solicitada");
            case "pause":
                Engine.Paused = true;
                return new(true, "Pausado");
            case "resume":
                Engine.Paused = false;
                return new(true, "Retomado");
            case "deletes":
            {
                var held = Engine.Drives.Where(d => d.DeleteHold).ToList();
                if (held.Count == 0) return new(true, "Nenhuma exclusão aguardando confirmação.");
                switch (req.Args.FirstOrDefault())
                {
                    case "confirm":
                        foreach (var d in held) d.ConfirmDeletes();
                        return new(true, "Exclusões confirmadas; enviando para a nuvem.");
                    case "restore":
                        foreach (var d in held) await d.RestoreDeletesAsync();
                        return new(true, "Exclusões canceladas; os itens estão sendo restaurados.");
                    default:
                        return new(true, string.Join('\n', held.Select(d => $"{d.Config.DisplayName}: {d.HeldDeleteCount} itens apagados aguardando confirmação")) +
                                         "\nUse: craftdriver deletes confirm | restore");
                }
            }
            case "status" when req.Args.Length == 0:
                return new(true, StatusSummary());
            case "status":
            case "pin":
            case "unpin":
            case "free":
            {
                var lines = new List<string>();
                var ok = true;
                foreach (var p in req.Args)
                {
                    var item = ItemFromLocalPath(p);
                    if (item == null) { ok = false; lines.Add($"{p}: fora da pasta sincronizada ou não encontrado"); continue; }
                    switch (req.Cmd)
                    {
                        case "pin":
                            Engine.SetPinned(item.Lid, true);
                            lines.Add($"{p}: sempre disponível neste dispositivo (baixando em segundo plano)");
                            break;
                        case "unpin":
                            Engine.SetPinned(item.Lid, false);
                            lines.Add($"{p}: somente online");
                            break;
                        case "free":
                            var n = Engine.FreeUpSpace(item.Lid);
                            lines.Add($"{p}: {n} arquivo(s) liberado(s)");
                            break;
                        default:
                            lines.Add($"{p}: {VirtualFs.Describe(Fs.StatusOf(item))}");
                            break;
                    }
                }
                await Task.CompletedTask;
                return new(ok, string.Join('\n', lines));
            }
            default:
                return new(false, "Comando desconhecido: " + req.Cmd);
        }
    }

    public string StatusSummary()
    {
        var lines = new List<string>
        {
            $"Status: {StatusText(Engine.Status)}",
            $"Montagem: {(Mounted ? Config.MountPoint : "não montado" + (MountError != null ? " (" + MountError + ")" : ""))}",
            $"Operações pendentes: {Db.PendingOps()}",
        };
        foreach (var d in Engine.Drives)
        {
            var (files, dirs, cached, dirty) = Db.Counts(d.DriveId);
            lines.Add($"- {d.Config.DisplayName}: {files} arquivos, {dirs} pastas, {cached} em cache, {dirty} a enviar" +
                      (d.LastSync is { } t ? $", última verificação {t:HH:mm:ss}" : "") +
                      (d.Offline ? " [offline]" : "") + (d.AuthRequired ? " [login necessário]" : "") +
                      (d.DeleteHold ? $" [{d.HeldDeleteCount} exclusões aguardando confirmação]" : "") +
                      (d.LastError is { } e ? $" [erro: {e}]" : ""));
        }
        return string.Join('\n', lines);
    }

    public static string StatusText(EngineStatus s) => s switch
    {
        EngineStatus.UpToDate => "Atualizado",
        EngineStatus.Syncing => "Sincronizando…",
        EngineStatus.Paused => "Sincronização pausada",
        EngineStatus.Offline => "Sem conexão",
        EngineStatus.AuthRequired => "Entre novamente na sua conta",
        EngineStatus.Error => "Há problemas de sincronização",
        EngineStatus.NoAccounts => "Nenhuma conta conectada",
        EngineStatus.DeleteConfirmation => "Confirme a exclusão de arquivos",
        _ => s.ToString(),
    };
}
