using System.Collections.Concurrent;
using System.Net;
using CraftDriver.Core.Auth;
using CraftDriver.Core.Config;
using CraftDriver.Core.Graph;
using CraftDriver.Core.State;

namespace CraftDriver.Core.Sync;

public enum EngineStatus { UpToDate, Syncing, Paused, Offline, AuthRequired, Error, NoAccounts, DeleteConfirmation }

/// <summary>Coordena todas as drives (OneDrive pessoal/empresa + bibliotecas do SharePoint).</summary>
public sealed class SyncEngine : IDisposable
{
    public AppConfig Config { get; }
    public StateDb Db { get; }
    public ContentCache Cache { get; }
    public ActivityLog Activity { get; }
    public NamePolicy Names { get; }

    readonly Func<DriveConfig, IGraphApi> _graphFactory;
    readonly ConcurrentDictionary<string, DriveSync> _drives = new();
    readonly ConcurrentDictionary<long, SemaphoreSlim> _contentLocks = new();
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _maintenanceSignal = new(0);
    Task? _maintenance;

    /// <summary>Informado pelo sistema de arquivos: itens com handles abertos não são despejados.</summary>
    public Func<long, bool> IsOpen { get; set; } = _ => false;

    public SyncEngine(AppConfig config, StateDb db, ContentCache cache, ActivityLog activity, Func<DriveConfig, IGraphApi> graphFactory)
    {
        Config = config;
        Db = db;
        Cache = cache;
        Activity = activity;
        Names = new NamePolicy(config);
        _graphFactory = graphFactory;
    }

    public bool Paused
    {
        get => Config.Paused;
        set
        {
            Config.Paused = value;
            if (!value) SyncNow();
        }
    }

    public IReadOnlyList<DriveSync> Drives => _drives.Values.OrderBy(d => d.Config.DisplayName).ToList();

    public DriveSync? GetDrive(string driveId) => _drives.TryGetValue(driveId, out var d) ? d : null;

    public CancellationToken Token => _cts.Token;

    public void Start()
    {
        ReloadDrives();
        _maintenance ??= Task.Run(MaintenanceLoop);
    }

    /// <summary>Sincroniza a lista de drives ativas com a configuração.</summary>
    public void ReloadDrives()
    {
        var wanted = Config.Drives.Where(d => d.Enabled).ToDictionary(d => d.DriveId);
        foreach (var (id, ds) in _drives)
            if (!wanted.ContainsKey(id))
            {
                ds.Stop();
                _drives.TryRemove(id, out _);
            }
        foreach (var (id, dc) in wanted)
        {
            if (_drives.TryGetValue(id, out var existing))
            {
                existing.Config = dc;
                continue;
            }
            var ds = new DriveSync(this, dc, _graphFactory(dc));
            _drives[id] = ds;
            ds.Start(_cts.Token);
        }
    }

    /// <summary>Remove dados locais de uma drive (ao remover biblioteca/conta).</summary>
    public void ForgetDrive(string driveId)
    {
        if (_drives.TryRemove(driveId, out var ds)) ds.Stop();
        Db.DeleteDriveData(driveId);
        Cache.DeleteDrive(driveId);
    }

    public void SyncNow()
    {
        foreach (var d in _drives.Values) d.Wake();
        _maintenanceSignal.Release();
    }

    /// <summary>Chamado pelo sistema de arquivos após enfileirar operações.</summary>
    public void NotifyLocalChange(string driveId)
    {
        if (_drives.TryGetValue(driveId, out var d)) d.WakeOps();
    }

    public void NotifyPinChanged() => _maintenanceSignal.Release();

    /// <summary>Disparado quando uma drive entra em retenção de exclusão em massa.</summary>
    public event Action<DriveSync>? MassDeleteDetected;
    internal void RaiseMassDelete(DriveSync d) => MassDeleteDetected?.Invoke(d);

    /// <summary>Chamado pelo sistema de arquivos ao apagar itens que existem na nuvem.</summary>
    public void RecordLocalDeletes(string driveId, int count)
    {
        if (count > 0 && _drives.TryGetValue(driveId, out var d)) d.RecordLocalDeletes(count);
    }

    public EngineStatus Status
    {
        get
        {
            if (Config.Accounts.Count == 0) return EngineStatus.NoAccounts;
            if (Paused) return EngineStatus.Paused;
            var drives = _drives.Values.ToList();
            if (drives.Any(d => d.AuthRequired)) return EngineStatus.AuthRequired;
            if (drives.Any(d => d.DeleteHold)) return EngineStatus.DeleteConfirmation;
            if (drives.Count > 0 && drives.All(d => d.Offline)) return EngineStatus.Offline;
            if (Activity.Transfers.Count > 0 || drives.Any(d => d.Busy) || Db.PendingOps() > 0) return EngineStatus.Syncing;
            if (drives.Any(d => d.Offline)) return EngineStatus.Offline;
            if (drives.Any(d => d.LastError != null)) return EngineStatus.Error;
            return EngineStatus.UpToDate;
        }
    }

    // ---------- conteúdo sob demanda ----------

    public static bool IsContentCurrent(Item item, ContentCache cache) =>
        item.IsDir || item.Dirty || item.LocalOnly || item.ItemId == null
        || (item.CachedCTag != null && item.CachedCTag == item.CTag && cache.Exists(item.DriveId, item.Lid));

    /// <summary>Garante que o conteúdo do arquivo está no cache local (baixa se necessário).</summary>
    public async Task EnsureContentAsync(long lid, CancellationToken ct)
    {
        var item = Db.Get(lid) ?? throw new FileNotFoundException();
        if (item.IsDir) return;
        var path = Cache.PathFor(item.DriveId, lid);

        if (item.Dirty || item.ItemId == null || item.LocalOnly)
        {
            if (!File.Exists(path)) await File.WriteAllBytesAsync(path, [], ct);
            return;
        }
        if (IsContentCurrent(item, Cache))
        {
            Db.Touch(lid);
            return;
        }

        var gate = _contentLocks.GetOrAdd(lid, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            item = Db.Get(lid) ?? throw new FileNotFoundException();
            if (IsContentCurrent(item, Cache)) return;
            var drive = GetDrive(item.DriveId) ?? throw new IOException("Drive não está ativa");
            var tmp = Cache.NewTempFile();
            var transfer = Activity.StartTransfer(item.Name, item.Size, upload: false);
            try
            {
                await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
                    await drive.Api.DownloadAsync(item.DriveId, item.ItemId!, fs, n => transfer.Done = n, ct);

                var len = new FileInfo(tmp).Length;
                if (len != item.Size)
                    Activity.Debug($"Tamanho diferente após download de {item.Name}: esperado {item.Size}, recebido {len} (normal para Office no SharePoint)");
                else if (item.Hash != null && len < 512L * 1024 * 1024)
                {
                    var h = QuickXorHash.ComputeFile(tmp);
                    if (h != item.Hash) Activity.Debug($"QuickXorHash diferente para {item.Name}: {h} != {item.Hash}");
                }

                lock (Db.Lock)
                {
                    var cur = Db.Get(lid);
                    if (cur == null) throw new FileNotFoundException();
                    if (cur.Dirty) return; // alterado localmente nesse meio tempo: não sobrescreve
                    File.Move(tmp, path, true);
                    cur.CachedCTag = item.CTag;
                    cur.LastAccess = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (cur.CTag == item.CTag) cur.Size = len;
                    Db.Update(cur);
                }
                Activity.Add(ActivityKind.Download, $"Baixado: {item.Name}");
                drive.MarkOnline();
            }
            finally
            {
                Activity.EndTransfer(transfer);
                try { File.Delete(tmp); } catch { }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    // ---------- fixar / liberar espaço ----------

    public void SetPinned(long lid, bool pinned)
    {
        lock (Db.Lock)
        {
            var it = Db.Get(lid);
            if (it == null) return;
            // ao fixar/desafixar uma pasta, a configuração dos filhos passa a ser herdada
            foreach (var x in Db.GetSubtree(lid))
            {
                if (x.Lid == lid) { x.Pinned = pinned; Db.Update(x); }
                else if (x.Pinned) { x.Pinned = false; Db.Update(x); }
            }
        }
        if (!pinned) FreeUpSpace(lid);
        NotifyPinChanged();
        foreach (var d in _drives.Values) d.WakePins();
    }

    /// <summary>"Liberar espaço": remove do cache os arquivos já sincronizados.</summary>
    public int FreeUpSpace(long lid)
    {
        var n = 0;
        lock (Db.Lock)
        {
            foreach (var x in Db.GetSubtree(lid))
            {
                if (x.IsDir || x.Dirty || x.ItemId == null || x.LocalOnly || x.CachedCTag == null) continue;
                if (IsOpen(x.Lid)) continue;
                Cache.Delete(x.DriveId, x.Lid);
                x.CachedCTag = null;
                if (x.Pinned) x.Pinned = false;
                Db.Update(x);
                n++;
            }
        }
        return n;
    }

    async Task MaintenanceLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                EnforceCacheLimit();
            }
            catch (Exception ex)
            {
                Activity.Debug("Manutenção do cache: " + ex.Message);
            }
            try { await _maintenanceSignal.WaitAsync(TimeSpan.FromMinutes(2), _cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void EnforceCacheLimit()
    {
        if (Config.CacheLimitMB <= 0) return;
        var limit = Config.CacheLimitMB * 1024 * 1024;
        var total = Cache.TotalBytes();
        if (total <= limit) return;
        foreach (var x in Db.CachedByLeastRecent())
        {
            if (total <= limit * 0.9) break;
            if (IsOpen(x.Lid) || Db.IsEffectivelyPinned(x.Lid)) continue;
            lock (Db.Lock)
            {
                var cur = Db.Get(x.Lid);
                if (cur == null || cur.Dirty) continue;
                var p = Cache.PathFor(cur.DriveId, cur.Lid);
                var len = File.Exists(p) ? new FileInfo(p).Length : 0;
                Cache.Delete(cur.DriveId, cur.Lid);
                cur.CachedCTag = null;
                Db.Update(cur);
                total -= len;
            }
        }
    }

    public void Stop()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        foreach (var d in _drives.Values) d.Stop();
    }

    public void Dispose() => Stop();
}

/// <summary>Sincronização de uma drive: delta remoto, fila de operações locais e downloads de itens fixados.</summary>
public sealed class DriveSync
{
    readonly SyncEngine _e;
    public DriveConfig Config { get; internal set; }
    public IGraphApi Api { get; }
    public string DriveId => Config.DriveId;

    public long RootLid { get; private set; }
    public bool Ready => RootLid != 0;
    public bool Offline { get; private set; }
    public bool AuthRequired { get; private set; }
    public bool Busy { get; private set; }
    public string? LastError { get; private set; }
    public DateTime? LastSync { get; private set; }
    public long QuotaTotal { get; private set; }
    public long QuotaUsed { get; private set; }

    /// <summary>Exclusões retidas aguardando confirmação do usuário.</summary>
    public bool DeleteHold { get; private set; }
    /// <summary>Itens apagados localmente desde que a retenção começou.</summary>
    public long HeldDeleteCount { get; private set; }

    /// <summary>Exclusões só vão para a nuvem depois desse tempo sem novas exclusões locais.</summary>
    public static int DeleteQuietMs { get; set; } = 3000;
    /// <summary>Janela usada para somar exclusões de uma mesma "rajada".</summary>
    public static TimeSpan MassDeleteWindow { get; set; } = TimeSpan.FromMinutes(2);

    readonly object _deleteLock = new();
    readonly Queue<(DateTime at, int n)> _recentDeletes = new();
    long _lastLocalDelete;

    readonly SemaphoreSlim _deltaSignal = new(0);
    readonly SemaphoreSlim _opsSignal = new(0);
    readonly SemaphoreSlim _pinSignal = new(0);
    /// <summary>Serializa aplicação do delta e a execução de operações (evita item duplicado em corrida upload×delta).</summary>
    readonly SemaphoreSlim _mutate = new(1, 1);
    CancellationTokenSource? _cts;
    DateTime _lastQuota = DateTime.MinValue;

    StateDb Db => _e.Db;
    ActivityLog Log => _e.Activity;

    internal DriveSync(SyncEngine engine, DriveConfig config, IGraphApi api)
    {
        _e = engine;
        Config = config;
        Api = api;
        var st = Db.GetDrive(config.DriveId);
        if (st != null)
        {
            RootLid = st.RootLid;
            QuotaTotal = st.QuotaTotal;
            QuotaUsed = st.QuotaUsed;
            (DeleteHold, HeldDeleteCount) = Db.GetDeleteHold(config.DriveId);
        }
    }

    internal void Start(CancellationToken parent)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        var ct = _cts.Token;
        _ = Task.Run(() => RunLoop(DeltaLoop, ct));
        _ = Task.Run(() => RunLoop(OpsLoop, ct));
        _ = Task.Run(() => RunLoop(PinLoop, ct));
    }

    async Task RunLoop(Func<CancellationToken, Task> loop, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await loop(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Log.Add(ActivityKind.Error, $"[{Config.DisplayName}] erro interno: {ex.Message}");
                Log.Debug(ex.ToString());
                try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch { return; }
            }
        }
    }

    internal void Stop() => _cts?.Cancel();

    // ---------- exclusão em massa ----------

    internal void RecordLocalDeletes(int count)
    {
        var raise = false;
        lock (_deleteLock)
        {
            Interlocked.Exchange(ref _lastLocalDelete, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var now = DateTime.UtcNow;
            _recentDeletes.Enqueue((now, count));
            while (_recentDeletes.Count > 0 && now - _recentDeletes.Peek().at > MassDeleteWindow) _recentDeletes.Dequeue();

            if (DeleteHold)
            {
                HeldDeleteCount += count;
            }
            else
            {
                var threshold = _e.Config.MassDeleteThreshold;
                var total = _recentDeletes.Sum(x => x.n);
                if (threshold > 0 && total >= threshold)
                {
                    DeleteHold = true;
                    HeldDeleteCount = total;
                    raise = true;
                }
            }
            if (DeleteHold) Db.SetDeleteHold(DriveId, true, HeldDeleteCount);
        }
        if (raise)
        {
            Log.Add(ActivityKind.Conflict, $"[{Config.DisplayName}] {HeldDeleteCount} itens foram apagados. As exclusões na nuvem estão retidas até você confirmar.");
            _e.RaiseMassDelete(this);
        }
    }

    /// <summary>Usuário confirmou: envia as exclusões retidas para a nuvem.</summary>
    public void ConfirmDeletes()
    {
        lock (_deleteLock)
        {
            var n = HeldDeleteCount;
            DeleteHold = false;
            HeldDeleteCount = 0;
            _recentDeletes.Clear();
            Db.SetDeleteHold(DriveId, false, 0);
            Log.Add(ActivityKind.Delete, $"[{Config.DisplayName}] exclusão de {n} itens confirmada; enviando para a nuvem (eles vão para a Lixeira do OneDrive)");
        }
        WakeOps();
    }

    /// <summary>
    /// Usuário desistiu: descarta as exclusões pendentes e refaz a listagem completa da nuvem,
    /// o que traz de volta os itens apagados (somente online; baixam ao abrir).
    /// </summary>
    public async Task RestoreDeletesAsync(CancellationToken ct = default)
    {
        await _mutate.WaitAsync(ct);
        try
        {
            int removed;
            lock (_deleteLock)
            {
                removed = Db.RemoveDeleteOps(DriveId);
                DeleteHold = false;
                HeldDeleteCount = 0;
                _recentDeletes.Clear();
                Db.SetDeleteHold(DriveId, false, 0);
                Db.SetDeltaLink(DriveId, null);
            }
            Log.Add(ActivityKind.Info, $"[{Config.DisplayName}] {removed} exclusão(ões) cancelada(s); restaurando os itens a partir da nuvem");
        }
        finally { _mutate.Release(); }
        _deltaSignal.Release();
    }
    public void Wake() { _deltaSignal.Release(); _opsSignal.Release(); _pinSignal.Release(); }
    public void WakeOps() => _opsSignal.Release();
    public void WakePins() => _pinSignal.Release();
    internal void MarkOnline() { Offline = false; }

    bool HandleError(Exception ex, string what)
    {
        switch (ex)
        {
            case AuthRequiredException:
                AuthRequired = true;
                LastError = ex.Message;
                Log.Add(ActivityKind.Error, $"[{Config.DisplayName}] {ex.Message}");
                return true;
            case HttpRequestException or TaskCanceledException or TimeoutException or IOException:
                if (!Offline) Log.Add(ActivityKind.Info, $"[{Config.DisplayName}] sem conexão ({what}): {ex.Message}");
                Offline = true;
                return true;
            case GraphException g when g.IsTransient:
                LastError = g.Message;
                Log.Debug($"[{Config.DisplayName}] {what}: {g.Message}");
                return true;
            default:
                LastError = ex.Message;
                Log.Add(ActivityKind.Error, $"[{Config.DisplayName}] {what}: {ex.Message}");
                return false;
        }
    }

    // ---------- inicialização ----------

    async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (Ready) return;
        var root = await Api.GetRootAsync(DriveId, ct);
        lock (Db.Lock)
        {
            if (Db.GetDrive(DriveId) is { } st) { RootLid = st.RootLid; return; }
            var lid = Db.Insert(new Item
            {
                DriveId = DriveId, ItemId = root.Id, ParentLid = 0, Name = "", IsDir = true,
                MTime = root.LastModified.ToUnixTimeMilliseconds(), CTime = root.Created.ToUnixTimeMilliseconds(), ETag = root.ETag,
            });
            Db.SaveDrive(new DriveState(DriveId, lid, null, 0, 0));
            RootLid = lid;
        }
    }

    // ---------- delta (nuvem → local) ----------

    async Task DeltaLoop(CancellationToken ct)
    {
        if (!_e.Paused)
        {
            try
            {
                Busy = true;
                await EnsureInitializedAsync(ct);
                await RunDeltaAsync(ct);
                await RefreshQuotaAsync(ct);
                Offline = false;
                AuthRequired = false;
                LastError = null;
                LastSync = DateTime.Now;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { HandleError(ex, "buscar alterações"); }
            finally { Busy = false; }
        }
        var wait = TimeSpan.FromSeconds(Math.Max(5, Offline ? 30 : _e.Config.PollIntervalSeconds));
        await _deltaSignal.WaitAsync(wait, ct);
        while (_deltaSignal.CurrentCount > 0) await _deltaSignal.WaitAsync(ct);
    }

    async Task RefreshQuotaAsync(CancellationToken ct)
    {
        if (DateTime.Now - _lastQuota < TimeSpan.FromMinutes(10)) return;
        var info = await Api.GetDriveAsync(DriveId, ct);
        QuotaTotal = info.QuotaTotal;
        QuotaUsed = info.QuotaUsed;
        _lastQuota = DateTime.Now;
        if (Db.GetDrive(DriveId) is { } st) Db.SaveDrive(st with { QuotaTotal = QuotaTotal, QuotaUsed = QuotaUsed });
    }

    public async Task RunDeltaAsync(CancellationToken ct)
    {
        var link = Db.GetDrive(DriveId)?.DeltaLink;
        var full = link == null;
        HashSet<string>? seen = full ? new() : null;
        var deferred = new List<RemoteItem>();
        var changes = 0;

        while (true)
        {
            DeltaPage page;
            try
            {
                page = await Api.GetDeltaAsync(DriveId, link, ct);
            }
            catch (GraphException g) when (g.Status == HttpStatusCode.Gone || g.Code is "resyncRequired")
            {
                Log.Add(ActivityKind.Info, $"[{Config.DisplayName}] token de alterações expirado; ressincronizando tudo");
                link = null;
                full = true;
                seen = new();
                deferred.Clear();
                continue;
            }

            await _mutate.WaitAsync(ct);
            try
            {
                Db.InTransaction(() =>
                {
                    var pending = deferred.Concat(page.Items).ToList();
                    deferred.Clear();
                    for (var pass = 0; pass < 4 && pending.Count > 0; pass++)
                    {
                        var next = new List<RemoteItem>();
                        foreach (var r in pending)
                        {
                            seen?.Add(r.Id);
                            if (ApplyRemote(r)) changes++;
                            else next.Add(r);
                        }
                        if (next.Count == pending.Count) { deferred.AddRange(next); break; }
                        pending = next;
                    }
                });
            }
            finally { _mutate.Release(); }

            if (page.NextLink != null)
            {
                link = page.NextLink;
                continue;
            }

            if (deferred.Count > 0)
                Log.Debug($"[{Config.DisplayName}] {deferred.Count} itens sem pasta pai conhecida foram ignorados");
            if (full && seen != null)
            {
                await _mutate.WaitAsync(ct);
                try { Db.InTransaction(() => Sweep(seen)); }
                finally { _mutate.Release(); }
            }
            Db.SetDeltaLink(DriveId, page.DeltaLink);
            if (full) Log.Add(ActivityKind.Info, $"[{Config.DisplayName}] lista de arquivos carregada");
            break;
        }

        if (changes > 0) WakePins();
    }

    /// <summary>Aplica um item do delta. Retorna false se a pasta pai ainda não é conhecida.</summary>
    bool ApplyRemote(RemoteItem r)
    {
        if (r.IsRoot)
        {
            var root = Db.Get(RootLid);
            if (root != null && root.ItemId != r.Id) { root.ItemId = r.Id; Db.Update(root); }
            return true;
        }

        var existing = Db.GetByRemote(DriveId, r.Id);
        if (r.IsDeleted)
        {
            if (existing != null) RemoveLocal(existing);
            return true;
        }
        if (r.IsPackage || r.IsRemoteShortcut) return true;

        var parent = r.ParentId != null ? Db.GetByRemote(DriveId, r.ParentId) : null;
        if (parent == null) return false; // pai ainda não chegou: adia

        var ctag = r.CTag ?? r.ETag;
        if (existing == null)
        {
            // item local ainda não enviado com o mesmo nome → conflito real
            var clash = Db.GetChildNoCase(parent.Lid, r.Name);
            if (clash != null && clash.ItemId == null && !clash.LocalOnly)
            {
                if (clash.IsDir && r.IsFolder)
                {
                    clash.ItemId = r.Id;
                    clash.ETag = r.ETag;
                    Db.Update(clash);
                    return true;
                }
                clash.Name = UniqueName(parent.Lid, NamePolicy.ConflictName(clash.Name));
                Db.Update(clash);
                Log.Add(ActivityKind.Conflict, $"Conflito: '{r.Name}' foi criado na nuvem e aqui; a cópia local virou '{clash.Name}'");
            }
            else if (clash != null && clash.ItemId == null && clash.LocalOnly)
            {
                // arquivo temporário local com o mesmo nome de um remoto novo: o remoto ganha
                RemoveLocal(clash);
            }
            Db.Insert(new Item
            {
                DriveId = DriveId, ItemId = r.Id, ParentLid = parent.Lid, Name = r.Name, IsDir = r.IsFolder,
                Size = r.IsFolder ? 0 : r.Size, MTime = r.LastModified.ToUnixTimeMilliseconds(),
                CTime = r.Created.ToUnixTimeMilliseconds(), ETag = r.ETag, CTag = ctag, Hash = r.QuickXorHash,
            });
            return true;
        }

        if (existing.Lid == RootLid) return true;

        // local tem renomeação/movimento pendente → a intenção local vence
        var pendingMove = HasPendingOp(existing.Lid, OpKind.Move);
        if (!pendingMove && (existing.ParentLid != parent.Lid || existing.Name != r.Name))
        {
            var clash = Db.GetChild(parent.Lid, r.Name);
            if (clash != null && clash.Lid != existing.Lid && clash.ItemId == null)
            {
                clash.Name = UniqueName(parent.Lid, NamePolicy.ConflictName(clash.Name));
                Db.Update(clash);
            }
            existing.ParentLid = parent.Lid;
            existing.Name = r.Name;
        }

        var contentChanged = !r.IsFolder && ctag != null && existing.CTag != ctag;
        if (existing.Dirty)
        {
            // não atualiza eTag/cTag: o upload falhará com 412 e gerará cópia de conflito
            if (!contentChanged) existing.ETag = r.ETag;
        }
        else
        {
            if (contentChanged)
            {
                existing.CTag = ctag;
                existing.Hash = r.QuickXorHash;
                existing.Size = r.Size;
                if (existing.CachedCTag != null && !_e.Db.IsEffectivelyPinned(existing.Lid) && !_e.IsOpen(existing.Lid))
                {
                    _e.Cache.Delete(DriveId, existing.Lid);
                    existing.CachedCTag = null;
                }
            }
            existing.ETag = r.ETag;
            existing.MTime = r.LastModified.ToUnixTimeMilliseconds();
            if (!r.IsFolder && !contentChanged) existing.Size = r.Size;
        }
        Db.Update(existing);
        return true;
    }

    bool HasPendingOp(long lid, string kind)
    {
        return Db.PendingOpsFor(lid, kind);
    }

    string UniqueName(long parentLid, string name)
    {
        var candidate = name;
        for (var i = 2; Db.GetChildNoCase(parentLid, candidate) != null; i++)
        {
            var ext = Path.GetExtension(name);
            candidate = $"{(string.IsNullOrEmpty(ext) ? name : name[..^ext.Length])} ({i}){ext}";
        }
        return candidate;
    }

    /// <summary>Remove um item excluído na nuvem, preservando alterações locais não enviadas.</summary>
    void RemoveLocal(Item item)
    {
        var subtree = Db.GetSubtree(item.Lid);
        foreach (var x in subtree.OrderByDescending(s => s.Lid == item.Lid ? 0 : 1))
        {
            if (!x.IsDir && x.Dirty && !x.LocalOnly)
            {
                x.ItemId = null;
                x.ETag = null;
                x.CTag = null;
                if (x.Lid != item.Lid)
                {
                    x.ParentLid = RootLid;
                    x.Name = UniqueName(RootLid, x.Name);
                }
                Db.Update(x);
                Db.Enqueue(DriveId, OpKind.Upload, x.Lid);
                Log.Add(ActivityKind.Conflict, $"'{x.Name}' foi excluído na nuvem mas tinha alterações locais; será enviado novamente");
                continue;
            }
        }
        foreach (var x in subtree)
        {
            var cur = Db.Get(x.Lid);
            if (cur == null || (cur.Dirty && !cur.IsDir && !cur.LocalOnly)) continue;
            if (_e.IsOpen(cur.Lid) && !cur.IsDir)
            {
                // arquivo aberto: o handle continua lendo o conteúdo antigo
            }
            _e.Cache.Delete(DriveId, cur.Lid);
            Db.RemoveOpsForLid(cur.Lid);
            Db.Delete(cur.Lid);
        }
    }

    /// <summary>Após enumeração completa: remove o que sumiu da nuvem.</summary>
    void Sweep(HashSet<string> seen)
    {
        foreach (var (lid, itemId) in Db.AllRemoteIds(DriveId))
        {
            if (lid == RootLid || seen.Contains(itemId)) continue;
            var it = Db.Get(lid);
            if (it != null) RemoveLocal(it);
        }
    }

    // ---------- operações (local → nuvem) ----------

    async Task OpsLoop(CancellationToken ct)
    {
        TimeSpan wait = TimeSpan.FromSeconds(30);
        if (!_e.Paused && Ready)
        {
            while (!ct.IsCancellationRequested)
            {
                var op = Db.PeekOp(DriveId);
                if (op == null) break;
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (op.NextTry > now)
                {
                    wait = TimeSpan.FromMilliseconds(Math.Min(op.NextTry - now, 300_000));
                    break;
                }
                if (op.Kind == OpKind.Delete)
                {
                    // exclusão em massa aguardando confirmação: a fila desta drive espera
                    if (DeleteHold)
                    {
                        wait = TimeSpan.FromMinutes(5);
                        break;
                    }
                    // "rm -r"/Dolphin apagam arquivo por arquivo e a pasta por último: só envia depois de
                    // um tempo sem novas exclusões; aí a exclusão da pasta já está na fila e cobre os filhos
                    var quietSince = Math.Max(op.Created, Interlocked.Read(ref _lastLocalDelete));
                    if (now - quietSince < DeleteQuietMs)
                    {
                        wait = TimeSpan.FromMilliseconds(DeleteQuietMs - (now - quietSince) + 50);
                        break;
                    }
                }
                Busy = true;
                try
                {
                    await _mutate.WaitAsync(ct);
                    try { await ExecuteAsync(op, ct); }
                    finally { _mutate.Release(); }
                    Db.CompleteOp(op.Id);
                    Offline = false;
                    AuthRequired = false;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (RetryNowException) { /* estado ajustado; tenta de novo imediatamente */ }
                catch (Exception ex)
                {
                    var transient = HandleError(ex, DescribeOp(op));
                    if (transient)
                    {
                        var backoff = TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(op.Attempts, 6))));
                        Db.FailOp(op.Id, ex.Message, backoff);
                        wait = backoff;
                        break;
                    }
                    Db.CompleteOp(op.Id); // erro permanente: descarta para não travar a fila
                }
                finally { Busy = false; }
            }
        }
        if (Db.PeekOp(DriveId) == null) wait = TimeSpan.FromSeconds(30);
        await _opsSignal.WaitAsync(wait, ct);
        while (_opsSignal.CurrentCount > 0) await _opsSignal.WaitAsync(ct);
    }

    string DescribeOp(Op op)
    {
        var name = Db.Get(op.Lid)?.Name ?? op.RemoteId ?? op.Lid.ToString();
        return op.Kind switch
        {
            OpKind.Upload => $"enviar '{name}'",
            OpKind.Mkdir => $"criar pasta '{name}'",
            OpKind.Delete => "excluir item",
            OpKind.Move => $"mover/renomear '{name}'",
            _ => op.Kind,
        };
    }

    sealed class RetryNowException : Exception;


    string ParentRemoteId(Item item)
    {
        var parent = Db.Get(item.ParentLid) ?? throw new InvalidOperationException($"Pasta pai de '{item.Name}' não existe mais");
        return parent.ItemId ?? throw new GraphException(HttpStatusCode.ServiceUnavailable, "parentPending", $"A pasta de '{item.Name}' ainda não foi criada na nuvem");
    }

    async Task ExecuteAsync(Op op, CancellationToken ct)
    {
        switch (op.Kind)
        {
            case OpKind.Mkdir:
            {
                var item = Db.Get(op.Lid);
                if (item == null || item.ItemId != null || item.LocalOnly || !item.IsDir) return;
                var r = await Api.CreateFolderAsync(DriveId, ParentRemoteId(item), item.Name, ct);
                lock (Db.Lock)
                {
                    var cur = Db.Get(op.Lid);
                    if (cur == null) { Db.Enqueue(DriveId, OpKind.Delete, 0, r.Id); return; }
                    cur.ItemId = r.Id;
                    cur.ETag = r.ETag;
                    Db.Update(cur);
                    if (cur.Name != item.Name || cur.ParentLid != item.ParentLid) Db.Enqueue(DriveId, OpKind.Move, cur.Lid);
                }
                Log.Add(ActivityKind.Upload, $"Pasta criada: {item.Name}");
                return;
            }
            case OpKind.Upload:
                await UploadAsync(op, ct);
                return;
            case OpKind.Delete:
                if (op.RemoteId == null) return;
                if (op.ParentRemote != null && Db.PendingDeleteFor(DriveId, op.ParentRemote))
                {
                    Log.Debug($"[{Config.DisplayName}] exclusão de '{op.Name}' coberta pela exclusão da pasta");
                    return;
                }
                await Api.DeleteAsync(DriveId, op.RemoteId, ct);
                Log.Add(ActivityKind.Delete, $"Excluído na nuvem (vai para a Lixeira do OneDrive): {op.Name ?? op.RemoteId}");
                return;
            case OpKind.Move:
            {
                var item = Db.Get(op.Lid);
                if (item == null || item.ItemId == null || item.LocalOnly) return;
                var r = await Api.MoveAsync(DriveId, item.ItemId, ParentRemoteId(item), item.Name, ct);
                lock (Db.Lock)
                {
                    var cur = Db.Get(op.Lid);
                    if (cur == null) return;
                    if (!cur.Dirty) cur.ETag = r.ETag;
                    Db.Update(cur);
                }
                Log.Add(ActivityKind.Move, $"Movido/renomeado: {item.Name}");
                return;
            }
            case OpKind.SetTime:
            {
                var item = Db.Get(op.Lid);
                if (item == null || item.ItemId == null || item.LocalOnly) return;
                var r = await Api.SetModifiedAsync(DriveId, item.ItemId, DateTimeOffset.FromUnixTimeMilliseconds(item.MTime), ct);
                lock (Db.Lock)
                {
                    var cur = Db.Get(op.Lid);
                    if (cur is { Dirty: false }) { cur.ETag = r.ETag; Db.Update(cur); }
                }
                return;
            }
        }
    }

    async Task UploadAsync(Op op, CancellationToken ct)
    {
        var item = Db.Get(op.Lid);
        if (item == null || !item.Dirty || item.LocalOnly || item.IsDir) return;
        var parentId = ParentRemoteId(item);
        var path = _e.Cache.PathFor(DriveId, item.Lid);
        if (!File.Exists(path))
        {
            lock (Db.Lock)
            {
                var cur = Db.Get(item.Lid);
                if (cur != null) { cur.Dirty = false; Db.Update(cur); }
            }
            return;
        }

        // snapshot (no btrfs vira reflink): o usuário pode continuar editando durante o envio
        var snap = _e.Cache.NewTempFile();
        File.Copy(path, snap, true);
        var gen = item.DirtyGen;
        var transfer = Log.StartTransfer(item.Name, new FileInfo(snap).Length, upload: true);
        RemoteItem r;
        try
        {
            r = await Api.UploadAsync(DriveId, parentId, item.Name, item.ItemId, item.ItemId != null ? item.ETag : null,
                snap, DateTimeOffset.FromUnixTimeMilliseconds(item.MTime), n => transfer.Done = n, ct);
        }
        catch (GraphException g) when (g.Status == HttpStatusCode.Conflict && item.ItemId == null)
        {
            // já existe arquivo com esse nome na nuvem: a versão local é renomeada
            lock (Db.Lock)
            {
                var cur = Db.Get(item.Lid);
                if (cur != null)
                {
                    var old = cur.Name;
                    cur.Name = UniqueName(cur.ParentLid, NamePolicy.ConflictName(cur.Name));
                    Db.Update(cur);
                    Log.Add(ActivityKind.Conflict, $"Conflito: '{old}' já existe na nuvem; a versão local foi salva como '{cur.Name}'");
                }
            }
            _deltaSignal.Release();
            throw new RetryNowException();
        }
        catch (GraphException g) when (g.Status == HttpStatusCode.PreconditionFailed)
        {
            await HandleEditConflictAsync(item, ct);
            return;
        }
        catch (GraphException g) when (g.Status == HttpStatusCode.NotFound && item.ItemId != null)
        {
            // excluído na nuvem enquanto era editado: recria
            lock (Db.Lock)
            {
                var cur = Db.Get(item.Lid);
                if (cur != null) { cur.ItemId = null; cur.ETag = null; Db.Update(cur); }
            }
            throw new RetryNowException();
        }
        finally
        {
            Log.EndTransfer(transfer);
            try { File.Delete(snap); } catch { }
        }

        lock (Db.Lock)
        {
            var cur = Db.Get(item.Lid);
            if (cur == null)
            {
                // excluído localmente durante o envio
                Db.Enqueue(DriveId, OpKind.Delete, 0, r.Id);
                return;
            }
            cur.ItemId = r.Id;
            cur.ETag = r.ETag;
            cur.CTag = r.CTag ?? r.ETag;
            cur.Hash = r.QuickXorHash;
            if (cur.DirtyGen == gen)
            {
                cur.Dirty = false;
                cur.CachedCTag = cur.CTag;
                cur.Size = r.Size;
            }
            Db.Update(cur);
            if (cur.Name != item.Name || cur.ParentLid != item.ParentLid) Db.Enqueue(DriveId, OpKind.Move, cur.Lid);
            if (cur.Dirty) Db.Enqueue(DriveId, OpKind.Upload, cur.Lid);
        }
        Log.Add(ActivityKind.Upload, $"Enviado: {item.Name}");
    }

    /// <summary>Arquivo mudou na nuvem e aqui: mantém as duas versões (como o cliente do Windows).</summary>
    async Task HandleEditConflictAsync(Item item, CancellationToken ct)
    {
        var remote = item.ItemId != null ? await Api.GetItemAsync(DriveId, item.ItemId, ct) : null;
        lock (Db.Lock)
        {
            var cur = Db.Get(item.Lid);
            if (cur == null) return;
            var copy = new Item
            {
                DriveId = DriveId, ParentLid = cur.ParentLid, Name = UniqueName(cur.ParentLid, NamePolicy.ConflictName(cur.Name)),
                IsDir = false, Size = cur.Size, MTime = cur.MTime, CTime = cur.CTime, Dirty = true, DirtyGen = 1,
                LastAccess = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            Db.Insert(copy);
            File.Move(_e.Cache.PathFor(DriveId, cur.Lid), _e.Cache.PathFor(DriveId, copy.Lid), true);
            Db.Enqueue(DriveId, OpKind.Upload, copy.Lid);

            cur.Dirty = false;
            cur.CachedCTag = null;
            if (remote != null)
            {
                cur.ETag = remote.ETag;
                cur.CTag = remote.CTag ?? remote.ETag;
                cur.Size = remote.Size;
                cur.Hash = remote.QuickXorHash;
                cur.MTime = remote.LastModified.ToUnixTimeMilliseconds();
            }
            Db.Update(cur);
            Log.Add(ActivityKind.Conflict, $"Conflito de edição em '{cur.Name}': sua versão foi salva como '{copy.Name}'");
        }
    }

    // ---------- itens fixados ("Sempre manter neste dispositivo") ----------

    async Task PinLoop(CancellationToken ct)
    {
        if (Ready && !Offline)
        {
            foreach (var it in Db.PinnedNeedingDownload(DriveId))
            {
                if (ct.IsCancellationRequested) break;
                try { await _e.EnsureContentAsync(it.Lid, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (FileNotFoundException) { }
                catch (Exception ex)
                {
                    if (HandleError(ex, $"baixar '{it.Name}'")) break;
                }
            }
        }
        await _pinSignal.WaitAsync(TimeSpan.FromMinutes(5), ct);
        while (_pinSignal.CurrentCount > 0) await _pinSignal.WaitAsync(ct);
    }
}
