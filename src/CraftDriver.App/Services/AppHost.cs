using CraftDriver.Core.Auth;
using CraftDriver.Core.Config;
using CraftDriver.Core.Graph;
using CraftDriver.Core.Ipc;
using CraftDriver.Core.State;
using CraftDriver.Core.Sync;
using CraftDriver.Core.Updates;
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

    /// <summary>Link de compartilhamento, versões anteriores e Lixeira.</summary>
    public ItemActions Actions { get; }

    /// <summary>Verificação e instalação de versões novas (docs/atualizacoes.md).</summary>
    public UpdateService Updates { get; }

    /// <summary>
    /// "Atualizar agora" pedido de fora da janela (notificação, IPC "update" do CLI). Disparado em qualquer thread: o
    /// assinante (App) mostra a janela e inicia o mesmo fluxo do botão. Sem assinante (--headless), não é oferecido.
    /// </summary>
    public event Action? UpdateNowRequested;

    /// <summary>Troca a Graph das drives (testes: Graph falsa). Lido quando o motor cria cada drive.</summary>
    internal Func<DriveConfig, IGraphApi>? GraphFactory { get; set; }

    public string? MountError { get; private set; }
    public bool Mounted => IsMountedProbe();

    // costuras da montagem (testes: montagem ocupada / que falha depois de começar, sem FUSE de verdade)
    internal Func<VirtualFs, string, Task> MountFunc { get; set; } = (fs, mp) => FuseHost.MountAsync(fs, mp);
    internal Func<bool> IsMountedProbe { get; set; } = () => FuseHost.IsMounted;
    internal Action UnmountFunc { get; set; } = FuseHost.Unmount;
    /// <summary>Quanto esperar o FUSE desmontar (com EBUSY o fusermount falha e a montagem continua).</summary>
    internal TimeSpan UnmountTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public event Action? ShowRequested;
    public event Action? QuitRequested;

    /// <summary>
    /// "Entrar novamente" pedido de fora da janela (botão da notificação de sessão expirada). Disparado
    /// numa thread do pool: o assinante despacha para a UI e abre o mesmo fluxo do botão da aba Contas.
    /// Sem assinante (--headless), a notificação não oferece o botão e indica o <c>craftdriver login</c>.
    /// </summary>
    public event Action<AccountConfig>? ReauthRequested;

    /// <summary>Envia notificações da área de trabalho (troque nos testes para capturar).</summary>
    public Action<string, string, NotificationAction[]> Notify
    {
        get => _notify;
        set { _notify = value; _notifyReplaced = true; }
    }
    Action<string, string, NotificationAction[]> _notify = (t, b, a) => DesktopIntegration.Notify(t, b, a);
    bool _notifyReplaced;

    /// <summary>
    /// Notificação com etiqueta: a seguinte com a mesma etiqueta substitui a anterior (replaces_id) e
    /// <see cref="CloseNotification"/> a fecha (ex.: "sessão expirada" some ao entrar de novo). Com
    /// <see cref="Notify"/> trocado (testes), vai por ele.
    /// </summary>
    public Action<string, string, string, NotificationAction[]> NotifyTagged { get; set; }

    /// <summary>Fecha a notificação com a etiqueta (se ainda estiver na tela). Troque nos testes.</summary>
    public Action<string> CloseNotification { get; set; }

    /// <summary>Ambiente da sessão gráfica desta instância (área de transferência). Troque nos testes.</summary>
    internal Func<string, string?> ClipboardEnv { get; set; } = Environment.GetEnvironmentVariable;

    readonly SessionExpiryTracker _sessions = new();
    Timer? _sessionTimer;

    AppHost(AppConfig cfg, AuthService auth)
    {
        NotifyTagged = (tag, t, b, a) =>
        {
            if (_notifyReplaced) Notify(t, b, a);
            else DesktopIntegration.NotifyTagged(tag, t, b, a);
        };
        CloseNotification = tag => { if (!_notifyReplaced) DesktopIntegration.CloseNotification(tag); };
        Config = cfg;
        Auth = auth;
        Activity = new ActivityLog(AppPaths.LogFile);
        Db = new StateDb(AppPaths.DatabaseFile);
        Engine = new SyncEngine(cfg, Db, new ContentCache(cfg.CacheDir), Activity, dc => GraphFactory?.Invoke(dc) ?? ClientFor(dc.AccountId));
        Fs = new VirtualFs(Engine);
        Actions = new ItemActions(Engine, Fs);
        FuseHost.Log = s => Activity.Debug(s);
        Engine.MassDeleteDetected += d =>
        {
            Notify("craftDriver — muitos arquivos apagados",
                $"{d.HeldDeleteCount} itens foram apagados de \"{d.Config.DisplayName}\". Nada foi excluído da nuvem ainda: abra o craftDriver para confirmar ou restaurar.",
                MassDeleteActions(d.DriveId));
            ShowRequested?.Invoke();
        };
        Engine.ConflictDetected += OnConflict;
        Updates = new UpdateService(cfg, Activity, () => TrySaveConfig("Atualizações"));
        Updates.Discovered += OnUpdateDiscovered;
    }

    /// <summary>Etiqueta da notificação de versão nova (a de uma versão mais nova substitui a anterior).</summary>
    internal const string UpdateTag = "atualizacao";

    /// <summary>Versão nova encontrada (uma vez por versão): notificação com "Atualizar agora" e "Ver novidades".</summary>
    internal void OnUpdateDiscovered(ReleaseInfo release)
    {
        const string hint = "Para atualizar: abra o craftDriver (aba Visão geral) ou rode \"craftdriver update\" no terminal.";
        var notes = new NotificationAction("notes", "Ver novidades", () => OpenUrl(release.HtmlUrl)) { HintWithoutButtons = hint };
        NotificationAction[] actions = UpdateNowRequested == null
            ? [notes]
            : [new(NotificationAction.Default, "Atualizar agora", RequestUpdateNow) { HintWithoutButtons = hint },
               new("update", "Atualizar agora", RequestUpdateNow) { HintWithoutButtons = hint }, notes];
        NotifyTagged(UpdateTag, "craftDriver — nova versão " + release.Version,
            $"A versão {release.Version} do craftDriver está disponível (você usa a {Updates.CurrentVersion}).", actions);
    }

    /// <summary>Mesmo fluxo do botão "Atualizar agora" (janela).</summary>
    public void RequestUpdateNow() => UpdateNowRequested?.Invoke();

    /// <summary>Pede para mostrar a janela (ex.: ação de notificação tratada pelo ViewModel).</summary>
    public void RequestShow() => ShowRequested?.Invoke();

    long _lastConflictNotice;
    /// <summary>Intervalo mínimo entre notificações de conflito: numa rajada, o resto fica só na aba Conflitos.</summary>
    internal TimeSpan ConflictNoticeInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Chamado na thread do motor (às vezes com o lock do banco): só agenda a notificação.</summary>
    internal void OnConflict(ConflictRecord c)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastConflictNotice);
        if (last != 0 && now - last < ConflictNoticeInterval.TotalMilliseconds) return;
        if (Interlocked.CompareExchange(ref _lastConflictNotice, now, last) != last) return;
        NotificationAction[] actions = ShowRequested == null
            ? []
            : [new(NotificationAction.Default, "Ver conflitos", () => ShowRequested?.Invoke()), new("show", "Ver conflitos", () => ShowRequested?.Invoke())];
        Notify("craftDriver — conflito", $"{c.Original}: {c.Reason}. As duas versões foram mantidas; a sua está em \"{c.Copy}\".", actions);
    }

    NotificationAction[] MassDeleteActions(string driveId)
    {
        var restore = new NotificationAction("restore", "Restaurar", () =>
        {
            var n = RestoreHeldDeletesAsync(driveId).GetAwaiter().GetResult();
            if (n > 0) Activity.Add(ActivityKind.Info, "Exclusões canceladas pela notificação; os itens estão sendo restaurados.");
        });
        if (ShowRequested == null) return [restore]; // --headless: não há janela para abrir
        return
        [
            new(NotificationAction.Default, "Abrir craftDriver", () => ShowRequested?.Invoke()),
            new("open", "Abrir craftDriver", () => ShowRequested?.Invoke()),
            restore,
        ];
    }

    /// <summary>Cancela as exclusões retidas (de uma drive ou de todas) e restaura os itens. Devolve quantas drives.</summary>
    internal async Task<int> RestoreHeldDeletesAsync(string? driveId = null)
    {
        var held = Engine.Drives.Concat(Engine.SharedDrives).Where(d => d.DeleteHold && (driveId == null || d.DriveId == driveId)).ToList();
        foreach (var d in held) await d.RestoreDeletesAsync();
        return held.Count;
    }

    // ---------- sessão expirada ----------

    /// <summary>Verifica as drives do motor (chamado a cada 5 s pelo timer, na thread dona da configuração).</summary>
    internal void CheckSessions() => CheckSessions(Engine.Drives.Select(d => (d.Config.AccountId, d.AuthRequired)));

    /// <summary>Notifica uma vez por conta cada vez que ela passa a pedir login (não a cada ciclo de sincronização).</summary>
    internal void CheckSessions(IEnumerable<(string AccountId, bool AuthRequired)> drives)
    {
        var fresh = _sessions.Update(drives, out var recovered);
        foreach (var id in recovered) CloseNotification(SessionTag(id)); // voltou a sincronizar: tira o aviso da tela
        foreach (var id in fresh)
        {
            var acc = Config.Accounts.FirstOrDefault(a => a.Id == id);
            if (acc == null) continue;
            if (ReauthRequested == null)
            {
                NotifyTagged(SessionTag(id), "craftDriver — sessão expirada",
                    $"A sessão de {acc.Username} expirou e a sincronização dessa conta está parada. Entre novamente com \"craftdriver login\".", []);
                continue;
            }
            void Reauth() => ReauthRequested?.Invoke(acc);
            // servidor sem botões (sem a capacidade "actions") ou notify-send: a dica vai no texto
            const string hint = "Para entrar: abra o craftDriver, aba Contas, ou rode \"craftdriver login\" no terminal.";
            NotifyTagged(SessionTag(id), "craftDriver — sessão expirada",
                $"A sessão de {acc.Username} expirou e a sincronização dessa conta está parada até você entrar novamente.",
                [new(NotificationAction.Default, "Entrar novamente", Reauth) { HintWithoutButtons = hint },
                 new("relogin", "Entrar novamente", Reauth) { HintWithoutButtons = hint }]);
        }
    }

    /// <summary>Etiqueta da notificação "sessão expirada" de uma conta (substituída/fechada, não empilhada).</summary>
    internal static string SessionTag(string accountId) => "sessao-" + accountId;

    public static Task<AppHost> CreateAsync() => CreateAsync(null, null, CancellationToken.None);

    /// <summary>
    /// Com a configuração já carregada (o Program a lê antes do Avalonia para aplicar tema/escala) e com o
    /// progresso/desistência da espera pelo chaveiro (janela de espera; ver AuthService.CreateAsync).
    /// </summary>
    public static async Task<AppHost> CreateAsync(AppConfig? cfg, Action<string>? status, CancellationToken skipKeyring)
    {
        AppPaths.EnsureDirs();
        cfg ??= AppConfig.Load();
        var auth = await AuthService.CreateAsync(cfg, status, skipKeyring);
        return new AppHost(cfg, auth);
    }

    /// <summary>Throttling (429/503) por conta: as drives da mesma conta respeitam juntas o Retry-After.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, GraphThrottle> _throttles = new();

    public GraphClient ClientFor(string accountId) =>
        new((force, ct) => Auth.GetTokenAsync(accountId, force, ct), throttle: _throttles.GetOrAdd(accountId, _ => new GraphThrottle()));

    /// <param name="existingIpc">
    /// Servidor aberto pelo Program antes do AppHost existir (respondendo "ainda iniciando"): é assumido em vez de
    /// criar outro — recriar apagaria e trocaria o socket que a 2ª execução já pode ter achado.
    /// </param>
    public void Start(IpcServer? existingIpc = null)
    {
        foreach (var note in Auth.StartupNotes)
            Activity.Add(Auth.KeyringUnavailable ? ActivityKind.Error : ActivityKind.Info, note);
        if (Config.LoadWarning is { } warning)
        {
            Activity.Add(ActivityKind.Error, warning);
            // arquivo inválido: grava já os padrões (o original ficou salvo) para não repetir o aviso a cada início
            if (!Config.ReadOnly) TrySaveConfig("Configuração padrão");
        }
        Activity.Add(ActivityKind.Info, $"Iniciado. Tokens em: {Auth.CacheStorage}");
        ImportOfflineCopies(); // antes do motor: o delta não pode adotar a versão da nuvem antes do envio do que mudou
        Engine.Start();
        Mount();
        Updates.Start();
        _sessionTimer = new Timer(_ =>
        {
            try { RunOnMain(CheckSessions).ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); }
            catch { /* melhor esforço */ }
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        if (existingIpc != null)
        {
            existingIpc.Handler = HandleIpcAsync;
            _ipc = existingIpc;
            return;
        }
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
            var mountPoint = Config.MountPoint;
            var task = MountFunc(Fs, mountPoint);
            _mountTask = task;
            MountError = null;
            Activity.Add(ActivityKind.Info, $"Montado em {mountPoint}");
            task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    // o loop do FUSE caiu com erro: "Não montado" precisa dizer o porquê
                    MountError = t.Exception!.GetBaseException().Message;
                    Activity.Add(ActivityKind.Error, $"A montagem em {mountPoint} caiu: {MountError}");
                }
                else Activity.Add(ActivityKind.Info, "Ponto de montagem desconectado");
            }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            MountError = ex.Message;
            Activity.Add(ActivityKind.Error, "Falha ao montar: " + ex.Message);
        }
    }

    /// <summary>Desmonta; devolve false se a montagem continua (pasta em uso).</summary>
    public async Task<bool> UnmountAsync()
    {
        if (!Mounted) return true;
        var mountPoint = Config.MountPoint;
        UnmountFunc();
        var deadline = Environment.TickCount64 + (long)UnmountTimeout.TotalMilliseconds;
        if (_mountTask != null) await Task.WhenAny(_mountTask, Task.Delay(UnmountTimeout));
        // sem a task (ou ela terminou mas o kernel ainda não soltou): confere o estado real até o prazo
        while (Mounted && Environment.TickCount64 < deadline) await Task.Delay(50);
        Fs.CommitOpenWrites(); // fuse_session_exit não garante release dos fds abertos; antes de Engine.Stop no shutdown
        if (Fs.OpenHandles > 0) Activity.Add(ActivityKind.Info, "Havia arquivos abertos durante a desmontagem");
        if (!Mounted) return true;
        Activity.Add(ActivityKind.Error, $"Não foi possível desmontar {mountPoint}: {BusyMountText}");
        return false;
    }

    /// <summary>
    /// Ao encerrar, já desmontado: deixa os fixados na pasta real do ponto de montagem, para continuarem disponíveis
    /// com o app fechado (docs/copias-offline.md). Montagem presa (pasta em uso) ou de outro processo: não grava.
    /// </summary>
    void MaterializeOfflineCopies()
    {
        if (Mounted || OfflineCopies.IsMountPoint(Config.MountPoint)) return;
        var r = OfflineCopies.Materialize(Engine, Fs, Config.MountPoint);
        if (r.Files > 0)
            Activity.Log("INFO", $"Cópias offline: {r.Files} arquivo(s) fixado(s) em {Config.MountPoint}" + (r.Skipped > 0 ? $" ({r.Skipped} ainda não baixado(s) ficaram de fora)" : ""));
    }

    /// <summary>Na abertura: importa o que mudou nas cópias offline com o app fechado e tira as cópias da pasta.</summary>
    void ImportOfflineCopies()
    {
        try
        {
            if (OfflineCopies.Import(Engine) is not { AnyChange: true } r) return;
            var parts = new List<string>();
            if (r.Edited > 0) parts.Add($"{r.Edited} editado(s)");
            if (r.Created > 0) parts.Add($"{r.Created} novo(s)");
            if (r.Moved > 0) parts.Add($"{r.Moved} movido(s)/renomeado(s)");
            if (r.Unpinned > 0) parts.Add($"{r.Unpinned} apagado(s) da cópia (só desfixado(s); nada apagado na nuvem)");
            if (parts.Count > 0) Activity.Add(ActivityKind.Info, "Alterações feitas com o craftDriver fechado: " + string.Join(", ", parts));
            if (r.Recovered > 0)
                Activity.Add(ActivityKind.Error, $"{r.Recovered} item(ns) da pasta offline não puderam ser sincronizados e foram movidos para {r.RecoveryDir}");
        }
        catch (Exception ex)
        {
            Activity.Add(ActivityKind.Error, "Não foi possível importar as alterações feitas com o app fechado: " + ex.Message);
        }
    }

    /// <summary>Motivo mostrado quando o fusermount -u recusa (EBUSY).</summary>
    public const string BusyMountText =
        "a pasta está em uso (um programa com arquivo aberto ou um terminal dentro dela). Feche-os e tente de novo";

    /// <summary>
    /// Grava a configuração e avisa (log de atividades, como erro) quando não gravou: somente leitura (config de
    /// versão mais nova) ou erro de disco. Devolve se gravou.
    /// </summary>
    public bool TrySaveConfig(string what)
    {
        try
        {
            if (Config.Save()) return true;
            Activity.Add(ActivityKind.Error, $"{what}: não foi gravado no disco — {Config.LoadWarning}");
        }
        catch (Exception ex)
        {
            Activity.Add(ActivityKind.Error, $"{what}: não foi possível gravar a configuração ({ex.Message}).");
        }
        return false;
    }

    /// <summary>
    /// Encerra tudo; cada passo isolado: um falhar (ex.: disco cheio ao gravar a configuração) não impede os
    /// outros (desmontar, parar o motor, apagar o socket). Nunca lança; erros vão para o log.
    /// </summary>
    public async Task ShutdownAsync()
    {
        async Task Step(string what, Func<Task> step)
        {
            try { await step(); }
            catch (Exception ex) { Activity.Add(ActivityKind.Error, $"Ao encerrar ({what}): {ex.Message}"); }
        }
        await Step("timer", async () => { if (_sessionTimer != null) await _sessionTimer.DisposeAsync(); });
        await Step("atualizações", () => { Updates.Dispose(); return Task.CompletedTask; });
        var wasMounted = Mounted; // só quem montou de verdade deixa cópias (testes/instância sem FUSE não mexem na pasta)
        await Step("desmontar", UnmountAsync);
        if (wasMounted) await Step("cópias offline", () => { MaterializeOfflineCopies(); return Task.CompletedTask; });
        await Step("motor", () => { Engine.Stop(); return Task.CompletedTask; });
        await Step("IPC", () => { _ipc?.Dispose(); return Task.CompletedTask; });
        if (!Config.ReadOnly) TrySaveConfig("Configuração ao encerrar"); // somente leitura: já avisado ao iniciar
        Activity.Log("INFO", "Encerrado.");
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
        var drive = await ClientFor(acc.Id).GetMyDriveAsync(ct);
        RegisterAccount(acc, drive.Id, drive.WebUrl);
        return acc;
    }

    /// <summary>
    /// Grava na configuração em memória (a desta instância é a que vale enquanto ela roda) uma conta
    /// cujos tokens já estão no cache, e liga a drive dela. Chamado após o login pela janela e pelo
    /// comando IPC "add-account" que o <c>craftdriver login</c> envia.
    /// Devolve false quando a conta funciona nesta execução mas não foi gravada (configuração somente leitura
    /// ou erro de disco): o erro já está no log de atividades.
    /// </summary>
    public bool RegisterAccount(AccountConfig acc, string driveId, string? driveWebUrl)
    {
        Config.UpsertAccount(acc, driveId, driveWebUrl);
        var saved = TrySaveConfig($"Conta {acc.Username} conectada só nesta execução");
        Engine.ReloadDrives();
        Engine.SyncNow(); // drives que estavam pedindo login voltam a tentar
        Activity.Add(ActivityKind.Info, $"Conta adicionada: {acc.Username}");
        CloseNotification(SessionTag(acc.Id)); // "sessão expirada" dessa conta não vale mais
        AccountsChanged?.Invoke();
        return saved;
    }

    /// <summary>Contas/drives mudaram fora da janela (ex.: <c>craftdriver login</c> no terminal).</summary>
    public event Action? AccountsChanged;

    /// <summary>
    /// Executa alterações de configuração na thread dona do estado (a de UI, quando há janela),
    /// para que comandos IPC não alterem as listas enquanto a interface as percorre.
    /// </summary>
    public Func<Action, Task> RunOnMain { get; set; } = a => { a(); return Task.CompletedTask; };

    /// <summary>Abre uma pasta no gerenciador de arquivos (comando IPC "open"; troque nos testes).</summary>
    public Action<string> OpenFolder { get; set; } = DesktopIntegration.OpenPath;

    public string UniqueDisplayName(string wanted) => Config.UniqueDisplayName(wanted);

    public async Task RemoveAccountAsync(string accountId)
    {
        foreach (var d in Config.Drives.Where(d => d.AccountId == accountId).ToList())
            RemoveDrive(d.DriveId, save: false);
        Config.Accounts.RemoveAll(a => a.Id == accountId);
        TrySaveConfig("Remoção da conta");
        CloseNotification(SessionTag(accountId));
        await Auth.SignOutAsync(accountId);
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
        TrySaveConfig($"Biblioteca {dc.DisplayName}");
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
        if (save) TrySaveConfig("Remoção da pasta");
    }

    public void SetDriveEnabled(string driveId, bool enabled)
    {
        var d = Config.Drives.FirstOrDefault(x => x.DriveId == driveId);
        if (d == null) return;
        d.Enabled = enabled;
        TrySaveConfig(enabled ? "Ativação da pasta" : "Desativação da pasta");
        Engine.ReloadDrives();
    }

    // ---------- problemas (operações que falharam de forma permanente) ----------

    public static string KindLabel(string kind) => kind switch
    {
        OpKind.Upload => "enviar",
        OpKind.Mkdir => "criar pasta",
        OpKind.Delete => "excluir na nuvem",
        OpKind.Move => "mover/renomear",
        OpKind.SetTime => "atualizar data",
        _ => kind,
    };

    /// <summary>Caminho local do item da falha (ou só o nome, se o item não existe mais).</summary>
    public string FailurePath(FailedOp f) =>
        f.Lid != 0 && Db.Get(f.Lid) is { } it ? LocalPathOf(it) : f.Name ?? f.RemoteId ?? "?";

    public bool RetryFailure(long id)
    {
        var f = Db.Failures().FirstOrDefault(x => x.Id == id);
        return f != null && Engine.GetDrive(f.DriveId)?.RetryFailure(id) == true;
    }

    public int RetryAllFailures() => Db.Failures().Count(f => Engine.GetDrive(f.DriveId)?.RetryFailure(f.Id) == true);

    /// <summary>
    /// Descarta o registro da falha (as alterações locais do item continuam lá). Acorda a fila da drive: uma exclusão
    /// descartada pode estar segurando o envio de outro item com o mesmo nome (§23.6).
    /// </summary>
    public void DismissFailure(long id)
    {
        var drive = Db.Failures().FirstOrDefault(f => f.Id == id)?.DriveId;
        Db.DismissFailure(id);
        if (drive != null) Engine.NotifyLocalChange(drive);
    }

    // ---------- caminhos locais ----------

    /// <summary>
    /// Item de um caminho local (CLI, menus de contexto, seletor de pasta). Aceita o caminho configurado e o real:
    /// com a pasta sob link simbólico (<c>~/OneDrive → /data/od</c>) o kernel, o /proc/mounts e o gerenciador de arquivos
    /// podem usar um e a configuração o outro — antes o CLI dizia "fora da pasta sincronizada".
    /// </summary>
    public Item? ItemFromLocalPath(string path, bool includeExcluded = false)
    {
        var rel = MountPath.RelativeToMount(path, Config.MountPoint);
        return rel == null ? null : Fs.Resolve(rel, includeExcluded)?.Item;
    }

    /// <summary>
    /// Caminho na montagem. Drives da conta saem da configuração + raiz gravada no banco: a janela monta a lista de
    /// fixados antes do Host.Start (motor sem drives) e o caminho vinha vazio a cada abertura. Sub-drives
    /// (compartilhados) só existem com o motor iniciado: "" até lá.
    /// </summary>
    public string LocalPathOf(Item item)
    {
        string? name = null;
        long rootLid = 0;
        if (Engine.GetDrive(item.DriveId) is { } drive) (name, rootLid) = (drive.Config.DisplayName, drive.RootLid);
        if ((name == null || rootLid == 0) && Config.Drives.FirstOrDefault(d => d.DriveId == item.DriveId) is { } dc
            && Db.GetDrive(item.DriveId) is { RootLid: > 0 } st)
            (name, rootLid) = (dc.DisplayName, st.RootLid);
        if (name == null || rootLid == 0) return "";
        var parts = new List<string>();
        var cur = item;
        var seen = new HashSet<long>();
        while (cur != null && cur.Lid != rootLid && seen.Add(cur.Lid))
        {
            parts.Add(cur.Name);
            cur = Db.Get(cur.ParentLid);
        }
        if (cur == null || cur.Lid != rootLid) return "";
        parts.Add(name.Replace('/', '-'));
        parts.Reverse();
        return Path.Combine([Config.MountPoint, .. parts]);
    }

    // ---------- comandos IPC (CLI e menus do gerenciador de arquivos) ----------

    internal async Task<IpcResponse> HandleIpcAsync(IpcRequest req)
    {
        switch (req.Cmd)
        {
            case "ping":
                return new(true, "pong");
            case "add-account":
            {
                var add = req.Args.Length == 1 ? IpcJson.DeserializeAddAccount(req.Args[0]) : null;
                if (add?.Account is not { Id.Length: > 0 } acc || string.IsNullOrEmpty(add.DriveId))
                    return new(false, "Pedido add-account inválido");
                // o login gravou os tokens no cache compartilhado; confere que esta instância os enxerga
                if (!await Auth.HasAccountAsync(acc.Id))
                    return new(false, $"A conta {acc.Username} entrou, mas o craftDriver em execução não encontrou os tokens dela " +
                                      $"({Auth.CacheStorage}). Reinicie o craftDriver.");
                var saved = true;
                await RunOnMain(() => saved = RegisterAccount(acc, add.DriveId, add.DriveWebUrl));
                var connected = $"Conta conectada: {acc.Username} ({(acc.IsPersonal ? "pessoal" : "corporativa/escolar")})";
                return saved ? new(true, connected)
                    : new(false, connected + $", mas NÃO foi salva na configuração (vale só até fechar o craftDriver): {Config.LoadWarning ?? "veja o log"}");
            }
            case "show":
                // instância --headless não tem janela (nenhum assinante no evento)
                if (ShowRequested == null)
                    return new(false, "O craftDriver já está rodando sem interface (--headless). Encerre-o com \"craftdriver quit\" e abra de novo.");
                ShowRequested.Invoke();
                return new(true, "ok");
            case "open":
                if (!Mounted)
                    return new(false, "A pasta do craftDriver não está montada" + (MountError != null ? $" ({MountError})" : "") + ".");
                OpenFolder(Config.MountPoint);
                return new(true, "Abrindo " + Config.MountPoint);
            case "quit":
                QuitRequested?.Invoke();
                return new(true, "encerrando");
            case "update":
                return await UpdateFromCliAsync();
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
                var held = Engine.Drives.Concat(Engine.SharedDrives).Where(d => d.DeleteHold).ToList();
                if (held.Count == 0) return new(true, "Nenhuma exclusão aguardando confirmação.");
                switch (req.Args.FirstOrDefault())
                {
                    case "confirm":
                        foreach (var d in held) d.ConfirmDeletes();
                        return new(true, "Exclusões confirmadas; enviando para a nuvem.");
                    case "restore":
                        await RestoreHeldDeletesAsync();
                        return new(true, "Exclusões canceladas; os itens estão sendo restaurados.");
                    default:
                        return new(true, string.Join('\n', held.Select(d => $"{d.Config.DisplayName}: {d.HeldDeleteCount} itens apagados aguardando confirmação")) +
                                         "\nUse: craftdriver deletes confirm | restore");
                }
            }
            case "retry":
            {
                var n = RetryAllFailures();
                return new(true, n == 0 ? "Nenhum problema para tentar de novo." : $"{n} operação(ões) devolvida(s) à fila.");
            }
            case "status" when req.Args.Length == 0:
                return new(true, StatusSummary());
            case "exclude":
            case "include":
            {
                if (req.Args.Length == 0) return new(false, $"Informe as pastas: craftdriver {req.Cmd} <pastas…>");
                // incluir aceita o caminho de uma pasta já excluída (escondida da montagem)
                var lines = new List<string>();
                var ok = true;
                foreach (var p in req.Args)
                {
                    var item = ItemFromLocalPath(p, includeExcluded: req.Cmd == "include");
                    var error = item == null ? "fora da pasta sincronizada ou não encontrado" : Engine.SetExcluded(item.Lid, req.Cmd == "exclude");
                    ok &= error == null;
                    lines.Add(error != null ? $"{p}: {error}"
                        : req.Cmd == "exclude" ? $"{p}: não será mais sincronizada neste computador (continua na nuvem)"
                        : $"{p}: voltou a ser sincronizada");
                }
                return new(ok, string.Join('\n', lines));
            }
            case "pin" or "unpin" or "free" when req.Args.Length == 0:
                return new(false, $"Informe os arquivos ou pastas: craftdriver {req.Cmd} <caminhos…>");
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
            case "share":
                return await ShareAsync(req.Args);
            case "versions":
                return await VersionsAsync(req.Args);
            case "trash":
                return await TrashAsync(req.Args);
            default:
                return new(false, "Comando desconhecido: " + req.Cmd);
        }
    }

    /// <summary>
    /// IPC "update" (craftdriver update com o app aberto): procura a versão nova se ainda não souber e inicia o mesmo
    /// fluxo do botão na janela. Sem janela (--headless), devolve <see cref="UpdateCli.HeadlessReply"/>: o CLI atualiza
    /// no terminal (pkexec sem agente gráfico não pediria a senha).
    /// </summary>
    async Task<IpcResponse> UpdateFromCliAsync()
    {
        if (UpdateNowRequested == null) return new(false, UpdateCli.HeadlessReply);
        if (Updates.IsBusy) return new(false, "Já há uma atualização em andamento na janela do craftDriver.");
        var release = Updates.Available ?? await Updates.CheckAsync(manual: true);
        if (release == null)
            return Updates.LastCheckFailed
                ? new(false, Updates.LastCheckText ?? "Não foi possível procurar atualizações.")
                : new(true, $"O craftDriver já está na versão mais recente ({Updates.CurrentVersion}).");
        RequestUpdateNow();
        return new(true, $"Atualização para a versão {release.Version} iniciada na janela do craftDriver.");
    }

    // ---------- link de compartilhamento, versões e Lixeira ----------

    /// <summary>Copia texto para a área de transferência; devolve null ou o motivo da falha (troque nos testes).</summary>
    public Func<string, Task<string?>> CopyToClipboard { get; set; } = text => ClipboardService.CopyAsync(text);

    /// <summary>Abre uma URL no navegador (troque nos testes).</summary>
    public Action<string> OpenUrl { get; set; } = DesktopIntegration.OpenPath;

    /// <summary>Pedido de abrir a janela "Versões" de um arquivo (lid). Sem assinante (--headless), o IPC responde em texto.</summary>
    public event Action<long>? VersionsRequested;

    /// <summary>Pedido de abrir a janela "Lixeira". Sem assinante (--headless), o IPC responde em texto.</summary>
    public event Action? TrashRequested;

    static string[] Paths(string[] args) => args.Where(a => !a.StartsWith("--")).ToArray();
    static string? Flag(string[] args, string name) =>
        args.FirstOrDefault(a => a.StartsWith(name + "="))?[(name.Length + 1)..];

    public static string ScopeText(string scope) => scope switch
    {
        "anonymous" => "qualquer pessoa com o link",
        "organization" => "pessoas da sua organização",
        "users" => "só as pessoas escolhidas",
        _ => scope,
    };

    /// <summary>
    /// "craftdriver share [--edit] [--notify] arquivos…": cria o link (view, ou edit), copia para a área de
    /// transferência e imprime. Com --notify (menus) a notificação sai daqui, com "Abrir no navegador".
    /// </summary>
    async Task<IpcResponse> ShareAsync(string[] args)
    {
        var edit = args.Contains("--edit");
        var notify = args.Contains("--notify");
        // sem sessão gráfica (serviço do systemd) a cópia fica para o CLI, que roda na sessão do usuário
        var cliCopies = args.Contains(CliShare.CliCopiesFlag) && ClipboardService.Plan(ClipboardEnv).Count == 0;
        var paths = Paths(args);
        if (paths.Length == 0) return new(false, "Informe o arquivo ou a pasta: craftdriver share [--edit] <caminho>");
        var lines = new List<string>();
        var links = new List<(string Name, ShareResult Link)>();
        var errors = new List<string>();
        foreach (var p in paths)
        {
            var item = ItemFromLocalPath(p);
            if (item == null) { errors.Add($"{p}: fora da pasta sincronizada ou não encontrado"); continue; }
            try
            {
                var r = await Actions.CreateShareLinkAsync(item.Lid, edit, Engine.Token);
                links.Add((item.Name, r));
                lines.Add($"{r.Url}  ({item.Name}: {ScopeText(r.Scope)}, {(r.Type == "edit" ? "pode editar" : "somente leitura")})" +
                          (r.FellBackToOrganization ? " — a política da organização não permite links para qualquer pessoa" : ""));
            }
            catch (ItemActionException e) { errors.Add($"{p}: {e.Message}"); }
        }
        lines.AddRange(errors);
        if (cliCopies)
            return new(links.Count == paths.Length, string.Join('\n', lines), links.Count > 0 ? links.Select(l => l.Link.Url).ToArray() : null);
        string? clipError = null;
        if (links.Count > 0)
        {
            clipError = await CopyToClipboard(string.Join('\n', links.Select(l => l.Link.Url)));
            lines.Add(clipError == null
                ? (links.Count == 1 ? "Link copiado para a área de transferência." : $"{links.Count} links copiados para a área de transferência.")
                : "Não foi possível copiar para a área de transferência: " + clipError);
        }
        var ok = links.Count == paths.Length;
        var text = string.Join('\n', lines);
        if (notify)
        {
            if (links.Count == 0) Notify("craftDriver — link não criado", text, []);
            else
            {
                var (name, link) = links[0];
                void Open() => OpenUrl(link.Url);
                var body = links.Count == 1
                    ? $"{name}: {ScopeText(link.Scope)} {(link.Type == "edit" ? "pode editar" : "pode ver")}." +
                      (link.FellBackToOrganization ? " Links para qualquer pessoa estão desativados pela sua organização." : "")
                    : $"{links.Count} links criados.";
                if (clipError != null) body += $"\nNão copiado ({clipError}): {string.Join(' ', links.Select(l => l.Link.Url))}";
                if (errors.Count > 0) body += "\n" + string.Join('\n', errors);
                Notify(clipError == null ? "Link copiado" : "craftDriver — link criado", body,
                    [new(NotificationAction.Default, "Abrir no navegador", Open), new("open-link", "Abrir no navegador", Open)]);
            }
        }
        return new(ok, text);
    }

    public static string VersionLine(DriveItemVersion v, bool current) =>
        $"{v.Modified.ToLocalTime():yyyy-MM-dd HH:mm}  {CraftDriver.App.ViewModels.Fmt.Bytes(v.Size),9}  {v.ModifiedBy ?? "—"}" +
        (current ? "  (atual)" : "") + $"  [id {v.Id}]";

    /// <summary>
    /// "craftdriver versions arquivo": abre a janela Versões; com --list (ou --headless) lista no terminal;
    /// --restore=ID restaura; --copy=ID baixa a versão como cópia ao lado.
    /// </summary>
    async Task<IpcResponse> VersionsAsync(string[] args)
    {
        var paths = Paths(args);
        if (paths.Length != 1) return new(false, "Informe um arquivo: craftdriver versions <arquivo> [--list | --restore=ID | --copy=ID]");
        var item = ItemFromLocalPath(paths[0]);
        if (item == null) return new(false, $"{paths[0]}: fora da pasta sincronizada ou não encontrado");
        if (item.IsDir) return new(false, $"\"{item.Name}\" é uma pasta; versões anteriores existem só para arquivos.");
        try
        {
            if (Flag(args, "--restore") is { } restore)
            {
                await Actions.RestoreVersionAsync(item.Lid, restore, Engine.Token);
                return new(true, $"Versão {restore} restaurada como a atual de \"{item.Name}\".");
            }
            if (Flag(args, "--copy") is { } copy)
            {
                var v = (await Actions.ListVersionsAsync(item.Lid, Engine.Token)).FirstOrDefault(x => x.Id == copy)
                        ?? throw new ItemActionException($"Versão {copy} não encontrada.");
                var name = await Actions.DownloadVersionCopyAsync(item.Lid, v, Engine.Token);
                return new(true, $"Versão salva como \"{name}\" ao lado do original.");
            }
            if (!args.Contains("--list") && VersionsRequested != null)
            {
                VersionsRequested.Invoke(item.Lid);
                return new(true, $"Abrindo as versões de \"{item.Name}\".");
            }
            var list = await Actions.ListVersionsAsync(item.Lid, Engine.Token);
            if (list.Count == 0) return new(true, $"\"{item.Name}\" não tem versões anteriores guardadas.");
            return new(true, $"Versões de \"{item.Name}\" (mais nova primeiro):\n" +
                             string.Join('\n', list.Select((v, i) => "  " + VersionLine(v, i == 0))) +
                             "\nRestaurar: craftdriver versions <arquivo> --restore=ID · baixar cópia: --copy=ID");
        }
        catch (ItemActionException e) { return new(false, e.Message); }
    }

    /// <summary>"craftdriver trash [--list | restore ID | open]": excluídos recentes e restauração.</summary>
    async Task<IpcResponse> TrashAsync(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "restore":
                if (args.Length < 2 || !long.TryParse(args[1], out var id)) return new(false, "Use: craftdriver trash restore <número da lista>");
                try { return new(true, $"\"{await Actions.RestoreFromTrashAsync(id, Engine.Token)}\" restaurado da Lixeira; voltando para a pasta."); }
                catch (ItemActionException e) { return new(false, e.Message); }
            case "open":
            {
                var urls = Engine.Drives.Select(d => ItemActions.RecycleBinUrl(d.Config)).Distinct().ToList();
                if (urls.Count == 0) return new(false, "Nenhuma conta conectada.");
                foreach (var u in urls) OpenUrl(u);
                return new(true, "Abrindo no navegador: " + string.Join(' ', urls));
            }
            case null when TrashRequested != null:
                TrashRequested.Invoke();
                return new(true, "Abrindo a Lixeira.");
        }
        var list = Db.Trash();
        if (list.Count == 0) return new(true, "Nenhum item excluído recentemente (o craftDriver lista o que viu ser excluído desde a instalação).");
        var lines = new List<string> { "Excluídos recentemente (mais novos primeiro):" };
        foreach (var t in list)
        {
            var d = Engine.GetDrive(t.DriveId)?.Config;
            var how = d == null ? "drive não conectada" : ItemActions.CanRestoreFromTrash(d) ? "craftdriver trash restore " + t.Id : "só pelo navegador: " + ItemActions.RecycleBinUrl(d);
            lines.Add($"  {t.Id,4}  {DateTimeOffset.FromUnixTimeMilliseconds(t.At).ToLocalTime():yyyy-MM-dd HH:mm}  {t.Name}{(t.IsDir ? "/" : "")}  ({d?.DisplayName ?? t.DriveId}; {how})");
        }
        return new(true, string.Join('\n', lines));
    }

    public string StatusSummary()
    {
        var lines = new List<string>
        {
            $"Status: {StatusText(Engine.Status)}",
            $"Montagem: {(Mounted ? Config.MountPoint : "não montado" + (MountError != null ? " (" + MountError + ")" : ""))}",
            $"Operações pendentes: {Db.PendingOps()}",
        };
        var failures = Db.Failures();
        if (failures.Count > 0)
        {
            lines.Add($"Problemas ({failures.Count}) — tente de novo com \"craftdriver retry\":");
            lines.AddRange(failures.Select(f => $"  ✖ {KindLabel(f.Kind)} {FailurePath(f)}: {f.Error}"));
        }
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
