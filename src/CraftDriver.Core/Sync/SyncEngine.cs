using System.Collections.Concurrent;
using System.Net;
using CraftDriver.Core.Auth;
using CraftDriver.Core.Config;
using CraftDriver.Core.Graph;
using CraftDriver.Core.State;

namespace CraftDriver.Core.Sync;

public enum EngineStatus { UpToDate, Syncing, Paused, Offline, AuthRequired, Error, NoAccounts, DeleteConfirmation }

/// <summary>O conteúdo local de um arquivo com alterações não enviadas (ou só local) sumiu do cache.</summary>
public sealed class ContentMissingException(string message) : Exception(message);

/// <summary>O download terminou, mas o conteúdo não confere com a nuvem (tamanho/hash) mesmo após novas tentativas.</summary>
public sealed class ContentIntegrityException(string message) : Exception(message);

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

    public DriveSync? GetDrive(string driveId) =>
        _drives.TryGetValue(driveId, out var d) ? d : _shared.TryGetValue(driveId, out var s) ? s : null;

    public CancellationToken Token => _cts.Token;

    public void Start()
    {
        try { Cache.CleanStale(Db.UploadSnapshots()); }
        catch (Exception ex) { Activity.Debug("Limpeza de temporários: " + ex.Message); }
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
        ReconcileShared();
    }

    /// <summary>Remove dados locais de uma drive (ao remover biblioteca/conta).</summary>
    public void ForgetDrive(string driveId)
    {
        if (_drives.TryRemove(driveId, out var ds)) ds.Stop();
        Db.DeleteDriveData(driveId);
        Cache.DeleteDrive(driveId);
        // itens de outras drives que apareciam nela (atalhos, "Compartilhados comigo")
        Db.InTransaction(() =>
        {
            foreach (var sr in Db.SharedRoots(driveId)) RemoveSharedRootRows(sr);
            Db.DeleteDriveRows(ContainerKey(driveId));
        });
        ReconcileShared();
    }

    public void SyncNow()
    {
        foreach (var d in _drives.Values) d.Wake();
        foreach (var d in _shared.Values) d.Wake();
        _maintenanceSignal.Release();
    }

    /// <summary>Chamado pelo sistema de arquivos após enfileirar operações.</summary>
    public void NotifyLocalChange(string driveId)
    {
        if (GetDrive(driveId) is { } d) d.WakeOps();
    }

    public void NotifyPinChanged() => _maintenanceSignal.Release();

    /// <summary>Aplica os limites de transferência da configuração a todas as drives, sem reiniciar.</summary>
    public void ApplyTransferLimits()
    {
        foreach (var d in _drives.Values.Concat(_shared.Values)) d.ApplyTransferLimits();
    }

    /// <summary>Disparado quando uma drive entra em retenção de exclusão em massa.</summary>
    public event Action<DriveSync>? MassDeleteDetected;
    /// <summary>Conflito resolvido com cópia. Chamado na thread do motor (às vezes com o lock do banco): não bloqueie.</summary>
    public event Action<ConflictRecord>? ConflictDetected;
    internal void RaiseConflict(ConflictRecord c) => ConflictDetected?.Invoke(c);
    internal void RaiseMassDelete(DriveSync d) => MassDeleteDetected?.Invoke(d);

    /// <summary>Chamado pelo sistema de arquivos ao apagar itens que existem na nuvem.</summary>
    public void RecordLocalDeletes(string driveId, int count)
    {
        if (count > 0 && GetDrive(driveId) is { } d) d.RecordLocalDeletes(count);
    }

    public EngineStatus Status
    {
        get
        {
            if (Config.Accounts.Count == 0) return EngineStatus.NoAccounts;
            if (Paused) return EngineStatus.Paused;
            var drives = _drives.Values.ToList();
            if (drives.Any(d => d.AuthRequired)) return EngineStatus.AuthRequired;
            if (drives.Any(d => d.DeleteHold) || _shared.Values.Any(d => d.DeleteHold)) return EngineStatus.DeleteConfirmation;
            if (drives.Count > 0 && drives.All(d => d.Offline)) return EngineStatus.Offline;
            if (Activity.Transfers.Count > 0 || drives.Any(d => d.Busy) || Db.PendingOps() > 0) return EngineStatus.Syncing;
            if (drives.Any(d => d.Offline)) return EngineStatus.Offline;
            if (drives.Any(d => d.LastError != null) || Db.FailureCount() > 0) return EngineStatus.Error;
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
            // o conteúdo só existe aqui: se o arquivo do cache sumiu, criar um vazio esconderia a perda
            if (!File.Exists(path))
                throw new ContentMissingException($"O conteúdo local de '{item.Name}' não está mais no cache ({path}).");
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
            // leitura por faixas em andamento (ou blocos desta versão no disco): completa os blocos que faltam
            if (ExistingStream(item) is { } stream)
            {
                try { await stream.CompleteAsync(ct); }
                finally { ReleaseStream(stream); }
                return;
            }
            // parcial por item+versão: sobrevive a quedas de conexão e a reinícios e é retomado com Range
            var part = Cache.PartialPathFor(item.DriveId, lid, item.CTag);
            Cache.DeleteOtherPartials(item.DriveId, lid, part);
            var attempt = 1;
            var resumes = 0;
            while (true)
            {
                var before = File.Exists(part) ? new FileInfo(part).Length : 0;
                var transfer = Activity.StartTransfer(item.Name, item.Size, upload: false);
                transfer.Done = before;
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, transfer.Token);
                    await using (var fs = new FileStream(part, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1 << 16, true))
                        await drive.DownloadAsync(item.ItemId!, fs, before, n => transfer.Done = n, linked.Token);
                }
                catch (OperationCanceledException) when (transfer.Cancelled && !ct.IsCancellationRequested)
                {
                    // o parcial fica: abrir de novo continua de onde parou
                    var cancelled = new TransferCancelledException(item.Name, upload: false);
                    Activity.Add(ActivityKind.Info, cancelled.Message);
                    throw cancelled;
                }
                catch (Exception ex) when (Errors.IsDiskFull(ex))
                {
                    // disco do cache cheio: o parcial não tem como continuar e só ocuparia o espaço que falta
                    try { File.Delete(part); } catch { }
                    throw;
                }
                catch (Exception ex) when (Errors.IsNetworkError(ex, ct) && resumes < MaxDownloadResumes
                                           && File.Exists(part) && new FileInfo(part).Length > before)
                {
                    // caiu no meio, mas avançou: continua de onde parou
                    resumes++;
                    Activity.Debug($"Download de {item.Name} interrompido em {new FileInfo(part).Length} bytes ({ex.Message}); retomando");
                    continue;
                }
                finally
                {
                    Activity.EndTransfer(transfer);
                }

                var len = new FileInfo(part).Length;
                var problem = VerifyDownload(item, drive.Config, part, len);
                if (problem != null)
                {
                    try { File.Delete(part); } catch { }
                    if (attempt++ < DownloadAttempts)
                    {
                        Activity.Debug($"Download de {item.Name} descartado ({problem}); tentativa {attempt} de {DownloadAttempts}");
                        continue;
                    }
                    throw new ContentIntegrityException(
                        $"O conteúdo baixado de '{item.Name}' não confere com a nuvem ({problem}) após {DownloadAttempts} tentativas.");
                }

                lock (Db.Lock)
                {
                    var cur = Db.Get(lid);
                    if (cur == null) { try { File.Delete(part); } catch { } throw new FileNotFoundException(); }
                    if (cur.Dirty) { try { File.Delete(part); } catch { } return; } // alterado localmente nesse meio tempo
                    File.Move(part, path, true);
                    cur.CachedCTag = item.CTag;
                    cur.LastAccess = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (cur.CTag == item.CTag) cur.Size = len;
                    Db.Update(cur);
                }
                Activity.Add(ActivityKind.Download, $"Baixado: {item.Name}");
                drive.MarkOnline();
                return;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Quedas de conexão no meio de um download retomadas na hora (com Range) antes de desistir.</summary>
    public static int MaxDownloadResumes { get; set; } = 5;

    /// <summary>Downloads com tamanho/hash divergente são refeitos até este número de vezes.</summary>
    public static int DownloadAttempts { get; set; } = 3;

    static readonly HashSet<string> OfficeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".docm", ".dot", ".dotx", ".xls", ".xlsx", ".xlsm", ".xlsb", ".xlt", ".xltx",
        ".ppt", ".pptx", ".pptm", ".pps", ".ppsx", ".pot", ".potx", ".vsd", ".vsdx", ".one",
    };

    /// <summary>Retorna o motivo da divergência entre o arquivo baixado e os metadados da nuvem, ou null se confere.</summary>
    internal string? VerifyDownload(Item item, DriveConfig drive, string file, long len)
    {
        if (len != item.Size)
        {
            // O SharePoint/OneDrive for Business injeta propriedades em documentos do Office no download:
            // o arquivo recebido difere legitimamente do tamanho (e do hash) informado.
            if (drive.Kind != "personal" && OfficeExtensions.Contains(Path.GetExtension(item.Name)))
            {
                Activity.Debug($"Tamanho diferente após download de {item.Name}: esperado {item.Size}, recebido {len} (normal para Office no SharePoint)");
                return null;
            }
            return $"tamanho {len} em vez de {item.Size}";
        }
        if (item.Hash != null && len < 512L * 1024 * 1024)
        {
            var h = QuickXorHash.ComputeFile(file);
            if (h != item.Hash) return $"QuickXorHash {h} em vez de {item.Hash}";
        }
        return null;
    }

    // ---------- leitura por faixas (streaming) ----------

    readonly object _streamsLock = new();
    /// <summary>Sessão de leitura por faixas da versão atual de cada item com handles abertos.</summary>
    readonly Dictionary<long, StreamSession> _streams = new();

    int StreamingBlockSize => Math.Clamp(Config.StreamingBlockKB, 64, 64 * 1024) * 1024;

    /// <summary>Arquivo grande só-na-nuvem que pode ser lido por faixas (em vez de baixar inteiro no open).</summary>
    public bool CanStream(Item item)
    {
        if (Config.StreamingThresholdMB <= 0 || item.IsDir || item.Dirty || item.LocalOnly || item.ItemId == null) return false;
        if (item.Size <= 0 || item.Size < Config.StreamingThresholdMB * 1024 * 1024) return false;
        var drive = GetDrive(item.DriveId);
        if (drive == null) return false;
        // Office no SharePoint chega com tamanho/hash diferentes do informado (propriedades injetadas): faixas não batem
        return drive.Config.Kind == "personal" || !OfficeExtensions.Contains(Path.GetExtension(item.Name));
    }

    /// <summary>
    /// Abre (ou reaproveita) a leitura por faixas do item para um handle de leitura; null se o item deve ser baixado
    /// inteiro. Não usa a rede. Cada chamada que devolve sessão exige um <see cref="ReleaseStream"/>.
    /// </summary>
    public StreamSession? TryOpenStream(Item item)
    {
        lock (_streamsLock)
        {
            var cur = Db.Get(item.Lid);
            if (cur == null || !CanStream(cur) || IsContentCurrent(cur, Cache)) return null;
            return AcquireStreamLocked(cur);
        }
    }

    /// <summary>Sessão da versão atual já aberta, ou retomada do disco se há bitmap desta versão; senão null.</summary>
    StreamSession? ExistingStream(Item cur)
    {
        lock (_streamsLock)
        {
            if (!CanStream(cur)) return null;
            if (_streams.TryGetValue(cur.Lid, out var s) && s.CTag == cur.CTag && !s.Detached && !s.Verified)
            {
                s.AddRef();
                return s;
            }
            var basePath = Cache.PartialPathFor(cur.DriveId, cur.Lid, cur.CTag);
            return File.Exists(basePath + StreamSession.MapSuffix) ? AcquireStreamLocked(cur) : null;
        }
    }

    StreamSession AcquireStreamLocked(Item cur)
    {
        if (_streams.TryGetValue(cur.Lid, out var s))
        {
            if (s.CTag == cur.CTag && !s.Detached && !s.Verified)
            {
                s.AddRef();
                return s;
            }
            s.Detach(); // versão velha: fds já abertos seguem com o que baixaram
            _streams.Remove(cur.Lid);
        }
        var basePath = Cache.PartialPathFor(cur.DriveId, cur.Lid, cur.CTag);
        Cache.DeleteOtherPartials(cur.DriveId, cur.Lid, basePath); // blocos de outras versões não servem mais
        s = new StreamSession(this, cur, StreamingBlockSize, basePath);
        s.AddRef();
        _streams[cur.Lid] = s;
        return s;
    }

    public void ReleaseStream(StreamSession s)
    {
        lock (_streamsLock)
        {
            if (!s.Release()) return;
            if (_streams.TryGetValue(s.Lid, out var cur) && cur == s) _streams.Remove(s.Lid);
            if (Db.Get(s.Lid) == null) s.DeleteFiles(); // apagado enquanto aberto: os blocos não servem mais
        }
    }

    /// <summary>Todos os blocos chegaram e conferem: <paramref name="move"/> põe o arquivo no cache (com Db.Lock).</summary>
    internal bool PromoteStream(StreamSession s, Func<bool> move)
    {
        lock (Db.Lock)
        {
            var cur = Db.Get(s.Lid);
            if (cur == null || cur.Dirty || cur.CTag != s.CTag)
            {
                s.DeleteFiles(); // apagado, alterado aqui ou versão nova: estes blocos não viram cache
                return false;
            }
            if (IsContentCurrent(cur, Cache))
            {
                s.DeleteFiles(); // já baixado por outro caminho
                return true;
            }
            if (!move()) return false;
            cur.CachedCTag = s.CTag;
            cur.LastAccess = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            cur.Size = s.Size;
            Db.Update(cur);
            return true;
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

    /// <summary>
    /// Sincronização seletiva ("escolher pastas"). Excluir: a pasta some da montagem, o conteúdo baixado dela sai
    /// do cache e a fixação de dentro é desfeita; nada é apagado na nuvem e os metadados continuam sendo atualizados
    /// (reincluir é imediato, sem baixar a lista de novo). Devolve null ou o motivo da recusa (raiz, arquivo, ou
    /// alterações ainda não enviadas, que ficariam escondidas sem poder ser enviadas nem vistas).
    /// </summary>
    public string? SetExcluded(long lid, bool excluded)
    {
        Item? folder;
        lock (Db.Lock)
        {
            folder = Db.Get(lid);
            if (folder == null) return "Pasta não encontrada.";
            if (!folder.IsDir) return "Só pastas podem ser excluídas da sincronização.";
            var drive = GetDrive(folder.DriveId);
            if (drive != null && drive.RootLid == lid) return "A pasta raiz da conta não pode ser excluída.";
            if (excluded)
            {
                var subtree = Db.GetSubtree(lid);
                var pending = subtree.FirstOrDefault(x => x.Dirty || x.ItemId == null || x.LocalOnly);
                if (pending != null)
                    return $"\"{folder.Name}\" tem alterações ainda não enviadas (por exemplo \"{pending.Name}\"). " +
                           "Espere a sincronização terminar ou mova esses arquivos para outra pasta.";
                foreach (var x in subtree.Where(x => x.Pinned))
                {
                    x.Pinned = false;
                    Db.Update(x);
                }
            }
            Db.SetExcluded(folder.DriveId, lid, excluded);
        }
        if (excluded)
        {
            FreeUpSpace(lid);
            Activity.Add(ActivityKind.Info, $"\"{folder.Name}\" não será mais sincronizada neste computador (continua na nuvem)");
        }
        else
        {
            Activity.Add(ActivityKind.Info, $"\"{folder.Name}\" voltou a ser sincronizada");
            foreach (var d in _drives.Values.Concat(_shared.Values)) d.WakePins();
        }
        NotifyPinChanged();
        return null;
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
            if (Db.IsEffectivelyPinned(x.Lid)) continue;
            lock (Db.Lock)
            {
                // IsOpen dentro do lock: o Open reserva o item antes de baixar e só abre o arquivo sob este lock
                if (IsOpen(x.Lid)) continue;
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
        foreach (var d in _shared.Values) d.Stop();
    }

    // ---------- itens de outras drives: atalhos e "Compartilhados comigo" (docs/compartilhados.md) ----------

    /// <summary>
    /// Uma "sub-drive" por raiz compartilhada: os itens ficam no banco com drive_id = chave interna (ver
    /// <see cref="SharedRoot"/>), pendurados na árvore da drive do usuário (atalho) ou na pasta virtual
    /// "Compartilhados comigo". Fora de <see cref="Drives"/> (não viram pasta na raiz nem entram no status).
    /// </summary>
    readonly ConcurrentDictionary<string, DriveSync> _shared = new();
    readonly object _sharedLock = new();

    /// <summary>Sufixo do nome da pasta virtual "Compartilhados comigo" de cada conta, irmã da pasta da drive.</summary>
    public const string SharedContainerSuffix = " - Compartilhados comigo";

    /// <summary>drive_id interno da pasta virtual "Compartilhados comigo" de uma drive (só pessoal/empresa).</summary>
    public static string ContainerKey(string hostDriveId) => hostDriveId + "|comigo";

    /// <summary>Ids de drive internos (sub-drives e pastas virtuais) têm "|", que não aparece em ids da Graph.</summary>
    public static bool IsSharedKey(string driveId) => driveId.Contains('|');

    public IReadOnlyList<DriveSync> SharedDrives => _shared.Values.ToList();

    /// <summary>Pastas "Compartilhados comigo" visíveis na raiz da montagem: (nome, lid, drive da conta).</summary>
    public IReadOnlyList<(string Name, long Lid, DriveSync Host)> SharedContainers =>
        _drives.Values.Where(d => d.Ready && d.SharedContainerLid != 0)
            .OrderBy(d => d.Config.DisplayName)
            .Select(d => (d.Config.DisplayName + SharedContainerSuffix, d.SharedContainerLid, d)).ToList();

    /// <summary>
    /// Itens dessa drive não podem ser alterados: pasta virtual, compartilhamento sem permissão de escrita, ou
    /// sub-drive ainda não verificada / parada. Escrita local seria só uma operação fadada a falhar na fila.
    /// </summary>
    public bool IsReadOnly(string driveId) =>
        IsSharedKey(driveId) && !(_shared.TryGetValue(driveId, out var s) && s.Shared is { Writable: true });

    /// <summary>Raiz de compartilhamento/atalho ou pasta virtual: não se apaga nem se move pelo Linux.</summary>
    public bool IsSharedTop(Item it) => IsSharedKey(it.DriveId) && Db.GetDrive(it.DriveId)?.RootLid == it.Lid;

    /// <summary>Insere a raiz compartilhada no banco (dentro da transação de quem chama).</summary>
    internal SharedRoot AddSharedRootRows(string hostDriveId, string source, string? hostItemId, RemoteItem r, long parentLid, string name)
    {
        var key = source == SharedSource.Shortcut ? $"{hostDriveId}|atalho|{hostItemId}" : $"{hostDriveId}|comigo|{r.RemoteDriveId}!{r.RemoteId}";
        var lid = Db.Insert(new Item
        {
            DriveId = key, ItemId = r.RemoteId, ParentLid = parentLid, Name = name, IsDir = r.RemoteIsFolder,
            Size = r.RemoteIsFolder ? 0 : r.Size, MTime = r.LastModified.ToUnixTimeMilliseconds(), CTime = r.Created.ToUnixTimeMilliseconds(),
        });
        Db.SaveDrive(new DriveState(key, lid, null, 0, 0));
        var sr = new SharedRoot(key, hostDriveId, source, hostItemId, r.RemoteDriveId!, r.RemoteId!, lid, Writable: false);
        Db.SaveSharedRoot(sr);
        Activity.Add(ActivityKind.Info, source == SharedSource.Shortcut ? $"Atalho para item compartilhado: {name}" : $"Compartilhado com você: {name}");
        return sr;
    }

    /// <summary>Remove do banco a raiz compartilhada e tudo dela (dentro da transação de quem chama).</summary>
    internal void RemoveSharedRootRows(SharedRoot sr)
    {
        var subtree = Db.GetSubtree(sr.RootLid);
        var dirty = subtree.Count(x => x.Dirty && !x.LocalOnly && !x.IsDir);
        if (dirty > 0)
            Activity.Add(ActivityKind.Error, $"{dirty} alteração(ões) não enviada(s) em '{subtree.FirstOrDefault()?.Name}' foram descartadas: " +
                                             "o compartilhamento ou atalho deixou de existir.");
        foreach (var x in subtree)
        {
            Db.RemoveOpsForLid(x.Lid);
            Db.Delete(x.Lid);
        }
        Db.DeleteDriveRows(sr.Key);
        Db.DeleteSharedRoot(sr.Key);
    }

    /// <summary>Liga/desliga as sub-drives conforme a tabela shared_roots e as drives ativas.</summary>
    internal void ReconcileShared()
    {
        lock (_sharedLock)
        {
            if (_cts.IsCancellationRequested) return;
            var rows = Db.SharedRoots().ToDictionary(r => r.Key);
            foreach (var (key, ds) in _shared)
            {
                if (rows.TryGetValue(key, out var row) && _drives.ContainsKey(row.HostDriveId)) continue;
                ds.Stop();
                _shared.TryRemove(key, out _);
                if (!rows.ContainsKey(key))
                {
                    Db.DeleteDriveData(key); // o que a varredura possa ter gravado depois da remoção
                    Cache.DeleteDrive(key);
                }
            }
            foreach (var row in rows.Values)
            {
                if (!_drives.TryGetValue(row.HostDriveId, out var host)) continue;
                if (_shared.TryGetValue(row.Key, out var existing))
                {
                    existing.Shared = row;
                    continue;
                }
                var dc = new DriveConfig
                {
                    DriveId = row.Key, AccountId = host.Config.AccountId, Kind = host.Config.Kind,
                    DisplayName = $"{host.Config.DisplayName}: {Db.Get(row.RootLid)?.Name}",
                };
                var ds = new DriveSync(this, dc, host.Api, row);
                _shared[row.Key] = ds;
                ds.Start(_cts.Token);
            }
        }
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
    /// <summary>Sub-drive de item de outra drive (atalho / compartilhado comigo); null = drive configurada.</summary>
    public SharedRoot? Shared { get; internal set; }
    /// <summary>Drive usada nas chamadas à Graph: a do dono, numa sub-drive.</summary>
    public string ApiDriveId => Shared?.RemoteDriveId ?? DriveId;
    /// <summary>lid da pasta virtual "Compartilhados comigo" (0 = não tem / ainda não criada).</summary>
    public long SharedContainerLid { get; private set; }
    /// <summary>A raiz compartilhada não está acessível (compartilhamento removido ou sem permissão).</summary>
    public bool Inaccessible { get; private set; }

    /// <summary>Intervalo de atualização das sub-drives (listagem completa, sem delta) e do "Compartilhados comigo".</summary>
    public static int SharedPollSeconds { get; set; } = 300;
    /// <summary>Limite de itens por varredura de pasta compartilhada (acima disso a varredura fica parcial e nada é removido).</summary>
    public static int SharedMaxItems { get; set; } = 50_000;
    DateTime _lastSharedWithMe = DateTime.MinValue;

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

    /// <summary>
    /// Espera depois de o arquivo ser fechado antes de enviar. Editores (LibreOffice, OnlyOffice, WPS…)
    /// gravam num temporário e o renomeiam por cima do original logo em seguida; enviar antes disso
    /// criaria o temporário na nuvem só para apagá-lo depois (indo parar na Lixeira).
    /// </summary>
    public static int UploadSettleMs { get; set; } = 3000;
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

    readonly AdjustableLimiter _downloadSlots;
    readonly TransferBandwidth _downloadBandwidth;
    StateDb Db => _e.Db;
    ActivityLog Log => _e.Activity;

    internal async Task DownloadAsync(string itemId, Stream destination, long offset, Action<long>? progress, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts?.Token ?? _e.Token);
        await _downloadSlots.WaitAsync(linked.Token);
        try
        {
            using var throttled = new BandwidthWriteStream(destination, _downloadBandwidth);
            await Api.DownloadAsync(ApiDriveId, itemId, _downloadBandwidth.Enabled ? throttled : destination, offset, progress, linked.Token);
        }
        finally { _downloadSlots.Release(); }
    }

    /// <summary>Uma faixa do conteúdo (leitura por faixas), com a mesma vaga e limite de banda dos downloads.</summary>
    internal async Task DownloadRangeAsync(string itemId, Stream destination, long offset, long length, Action<long>? progress, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts?.Token ?? _e.Token);
        await _downloadSlots.WaitAsync(linked.Token);
        try
        {
            using var throttled = new BandwidthWriteStream(destination, _downloadBandwidth);
            await Api.DownloadRangeAsync(ApiDriveId, itemId, _downloadBandwidth.Enabled ? throttled : destination, offset, length, progress, linked.Token);
        }
        finally { _downloadSlots.Release(); }
    }

    internal DriveSync(SyncEngine engine, DriveConfig config, IGraphApi api, SharedRoot? shared = null)
    {
        _e = engine;
        Config = config;
        Api = api;
        Shared = shared;
        _downloadSlots = new AdjustableLimiter(Math.Clamp(engine.Config.DownloadConcurrency, 1, 32));
        _downloadBandwidth = new TransferBandwidth(engine.Config.DownloadBytesPerSecond);
        api.ConfigureUploadBandwidth(ApiDriveId, engine.Config.UploadBytesPerSecond);
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

    public long DownloadBytesPerSecond => _downloadBandwidth.BytesPerSecond;

    /// <summary>
    /// Aplica DownloadConcurrency/DownloadBytesPerSecond/UploadBytesPerSecond da configuração sem reiniciar: downloads
    /// esperando vaga começam na hora, e a banda nova vale do próximo pedaço em diante. Limites são por drive.
    /// </summary>
    public void ApplyTransferLimits()
    {
        _downloadSlots.SetLimit(Math.Clamp(_e.Config.DownloadConcurrency, 1, 32));
        _downloadBandwidth.BytesPerSecond = Math.Max(0, _e.Config.DownloadBytesPerSecond);
        Api.ConfigureUploadBandwidth(ApiDriveId, Math.Max(0, _e.Config.UploadBytesPerSecond));
    }

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

    /// <summary>Item da lista "o que vai ser apagado" (caminho dentro da drive).</summary>
    /// <param name="Count">Itens que a exclusão leva: ela mesma mais as exclusões retidas de dentro dela.</param>
    public sealed record HeldDelete(string Path, bool IsFolder, int Count);

    /// <summary>
    /// O que está na fila para ser apagado na nuvem, para o usuário ver antes de confirmar. Uma pasta apagada
    /// aparece uma vez, com o total dentro, em vez de item a item. Caminhos remontados das próprias exclusões
    /// (num rm -rf a pasta pai também já sumiu do banco) e, acima delas, das pastas que ainda existem.
    /// Ordenado por caminho; <paramref name="limit"/> corta a lista (o total vem de <see cref="HeldDeleteCount"/>).
    /// </summary>
    public List<HeldDelete> HeldDeletes(int limit = 500)
    {
        var ops = Db.DeleteOps(DriveId);
        var byRemote = new Dictionary<string, (string? parent, string? name, bool isDir)>();
        var children = new Dictionary<string, int>();
        foreach (var (id, parent, name, isDir) in ops)
        {
            if (id != null) byRemote[id] = (parent, name, isDir);
        }
        string PathOf(string? parent, string? name)
        {
            var parts = new List<string> { name ?? "?" };
            for (var guard = 0; parent != null && guard < 256; guard++)
            {
                if (byRemote.TryGetValue(parent, out var p))
                {
                    parts.Add(p.name ?? "?");
                    parent = p.parent;
                    continue;
                }
                var cur = Db.GetByRemote(DriveId, parent);
                while (cur != null && cur.Lid != RootLid)
                {
                    parts.Add(cur.Name);
                    cur = Db.Get(cur.ParentLid);
                }
                break;
            }
            parts.Reverse();
            return "/" + string.Join('/', parts);
        }
        // conta cada exclusão no ancestral mais alto que também está sendo apagado
        string TopOf(string? id, string? parent)
        {
            var top = id;
            for (var guard = 0; parent != null && byRemote.TryGetValue(parent, out var p) && guard < 256; guard++)
            {
                top = parent;
                parent = p.parent;
            }
            return top ?? "";
        }
        var tops = new Dictionary<string, (string path, bool isDir, int count)>();
        var loose = new List<HeldDelete>();
        foreach (var (id, parent, name, isDir) in ops)
        {
            var top = TopOf(id, parent);
            if (top == "")
            {
                loose.Add(new HeldDelete(PathOf(parent, name), isDir, 1));
                continue;
            }
            if (!tops.TryGetValue(top, out var t))
            {
                var info = byRemote[top];
                t = (PathOf(info.parent, info.name), info.isDir, 0);
            }
            tops[top] = (t.path, t.isDir || top != id, t.count + 1);
        }
        return tops.Values.Select(t => new HeldDelete(t.path, t.isDir, t.count)).Concat(loose)
            .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Take(limit).ToList();
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
            case Exception when Errors.IsLocalDiskError(ex):
            {
                // disco cheio / sem permissão no cache: não é falta de conexão; tenta de novo mais tarde
                var msg = $"{what}: {(Errors.IsDiskFull(ex) ? "disco local cheio" : "erro no disco local")} ({ex.Message})";
                if (LastError != msg) Log.Add(ActivityKind.Error, $"[{Config.DisplayName}] {msg}");
                LastError = msg;
                return true;
            }
            case HttpRequestException or TaskCanceledException or TimeoutException or IOException:
                if (!Offline) Log.Add(ActivityKind.Info, $"[{Config.DisplayName}] sem conexão ({what}): {ex.Message}");
                Offline = true;
                return true;
            case TransferCancelledException:
                // pedido pelo usuário: vai para "Problemas" (permanente), sem marcar a drive com erro
                Log.Add(ActivityKind.Info, $"[{Config.DisplayName}] {ex.Message}; fica em Problemas para tentar de novo");
                return false;
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
        if (!Ready && Shared == null) await InitializeRootAsync(ct);
        EnsureSharedContainer();
    }

    /// <summary>Pessoal/empresa: cria a pasta virtual "Compartilhados comigo" (vazia até a primeira consulta).</summary>
    void EnsureSharedContainer()
    {
        if (Shared != null || Config.Kind is not ("personal" or "business") || SharedContainerLid != 0) return;
        var key = SyncEngine.ContainerKey(DriveId);
        lock (Db.Lock)
        {
            if (Db.GetDrive(key) is { } st) { SharedContainerLid = st.RootLid; return; }
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var lid = Db.Insert(new Item { DriveId = key, ParentLid = 0, Name = "", IsDir = true, MTime = now, CTime = now });
            Db.SaveDrive(new DriveState(key, lid, null, 0, 0));
            SharedContainerLid = lid;
        }
    }

    async Task InitializeRootAsync(CancellationToken ct)
    {
        var root = await Api.GetRootAsync(ApiDriveId, ct);
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
                await RefreshSharedWithMeAsync(ct);
                if (Shared == null) await RefreshQuotaAsync(ct);
                Offline = false;
                AuthRequired = false;
                LastError = null;
                LastSync = DateTime.Now;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { HandleError(ex, "buscar alterações"); }
            finally { Busy = false; }
        }
        var wait = TimeSpan.FromSeconds(Math.Max(5, Offline ? 30 : Shared != null ? SharedPollSeconds : _e.Config.PollIntervalSeconds));
        await _deltaSignal.WaitAsync(wait, ct);
        while (_deltaSignal.CurrentCount > 0) await _deltaSignal.WaitAsync(ct);
    }

    async Task RefreshQuotaAsync(CancellationToken ct)
    {
        if (DateTime.Now - _lastQuota < TimeSpan.FromMinutes(10)) return;
        var info = await Api.GetDriveAsync(ApiDriveId, ct);
        QuotaTotal = info.QuotaTotal;
        QuotaUsed = info.QuotaUsed;
        _lastQuota = DateTime.Now;
        if (Db.GetDrive(DriveId) is { } st) Db.SaveDrive(st with { QuotaTotal = QuotaTotal, QuotaUsed = QuotaUsed });
    }

    /// <summary>Itens do delta aplicados por transação (o lock do banco é liberado entre lotes).</summary>
    public static int DeltaBatchSize { get; set; } = 32;

    public async Task RunDeltaAsync(CancellationToken ct)
    {
        if (Shared != null)
        {
            await RunSharedScanAsync(ct);
            return;
        }
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
                page = await Api.GetDeltaAsync(ApiDriveId, link, ct);
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
                // Transações curtas (DeltaBatchSize itens): entre elas Db.Lock fica livre para o getattr/readdir
                // do FUSE. _mutate continua preso a página inteira (não intercala com a fila de operações) e o
                // deltaLink só é gravado no fim — se cair no meio, a página é reaplicada (ApplyRemote é idempotente).
                var pending = deferred.Concat(page.Items).ToList();
                deferred.Clear();
                // até não haver progresso (uma passagem por nível quando filhos chegam antes dos pais;
                // com limite fixo, uma árvore mais funda perdia os níveis restantes)
                while (pending.Count > 0)
                {
                    var next = new List<RemoteItem>();
                    foreach (var batch in pending.Chunk(DeltaBatchSize))
                    {
                        ct.ThrowIfCancellationRequested();
                        Db.InTransaction(() =>
                        {
                            foreach (var r in batch)
                            {
                                seen?.Add(r.Id);
                                if (ApplyRemote(r)) changes++;
                                else next.Add(r);
                            }
                        });
                        await Task.Yield();
                    }
                    var noProgress = next.Count == pending.Count;
                    pending = next;
                    if (noProgress) break;
                }
                // o que nenhuma passagem conseguiu aplicar (pai ainda não chegou) espera a próxima página
                deferred.AddRange(pending);
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

        _e.ReconcileShared(); // atalhos novos/removidos neste delta
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
            if (existing != null)
            {
                // excluído em outro lugar: dá para restaurar (só nas drives próprias — a API de restaurar é da própria drive)
                if (Shared == null) Db.AddTrash(DriveId, r.Id, existing.Name, existing.IsDir);
                RemoveLocal(existing);
            }
            else if (Shared == null) RemoveShortcut(r.Id);
            return true;
        }
        if (r.IsPackage) return true;
        if (r.IsRemoteShortcut) return ApplyShortcut(r);

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
                RecordConflict(clash.Lid, r.Name, clash.Name, "criado na nuvem e aqui ao mesmo tempo",
                    $"Conflito: '{r.Name}' foi criado na nuvem e aqui; a cópia local virou '{clash.Name}'");
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

    /// <summary>Registra para a aba Conflitos, escreve na atividade e avisa (notificação).</summary>
    void RecordConflict(long lid, string original, string copy, string reason, string logText)
    {
        Log.Add(ActivityKind.Conflict, logText);
        _e.RaiseConflict(Db.AddConflict(DriveId, lid, original, copy, reason));
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
        if (subtree.Any(x => x.DriveId != DriveId))
        {
            // pasta com atalho dentro: o atalho (sub-drive) sai inteiro; o resto segue o caminho normal
            var lids = subtree.Where(x => x.DriveId != DriveId).Select(x => x.Lid).ToHashSet();
            foreach (var sr in Db.SharedRoots(DriveId).Where(s => lids.Contains(s.RootLid))) _e.RemoveSharedRootRows(sr);
            subtree = subtree.Where(x => x.DriveId == DriveId).ToList();
        }
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
                RecordConflict(x.Lid, x.Name, x.Name, "excluído na nuvem, mas tinha alterações aqui: enviado de novo",
                    $"'{x.Name}' foi excluído na nuvem mas tinha alterações locais; será enviado novamente");
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
        if (Shared == null)
            foreach (var sr in Db.SharedRoots(DriveId))
                if (sr.Source == SharedSource.Shortcut && sr.HostItemId != null && !seen.Contains(sr.HostItemId))
                    _e.RemoveSharedRootRows(sr);
    }

    // ---------- atalhos e "Compartilhados comigo" (itens de outras drives) ----------

    /// <summary>
    /// Atalho ("Adicionar a Meus arquivos"): vira uma sub-drive pendurada onde o atalho está. Renomear/mover o
    /// atalho na nuvem move a raiz; trocar o alvo recria. Dentro de uma sub-drive, atalhos aninhados são ignorados.
    /// </summary>
    bool ApplyShortcut(RemoteItem r)
    {
        if (Shared != null || r.RemoteDriveId == null || r.RemoteId == null) return true;
        var parent = r.ParentId != null ? Db.GetByRemote(DriveId, r.ParentId) : null;
        if (parent == null) return false;
        var sr = Db.SharedRoots(DriveId).FirstOrDefault(s => s.Source == SharedSource.Shortcut && s.HostItemId == r.Id);
        if (sr != null && (sr.RemoteDriveId != r.RemoteDriveId || sr.RemoteItemId != r.RemoteId))
        {
            _e.RemoveSharedRootRows(sr);
            sr = null;
        }
        if (sr == null)
        {
            _e.AddSharedRootRows(DriveId, SharedSource.Shortcut, r.Id, r, parent.Lid, r.Name);
            return true;
        }
        var root = Db.Get(sr.RootLid);
        if (root != null && (root.ParentLid != parent.Lid || root.Name != r.Name))
        {
            root.ParentLid = parent.Lid;
            root.Name = r.Name;
            Db.Update(root);
        }
        return true;
    }

    void RemoveShortcut(string hostItemId)
    {
        foreach (var sr in Db.SharedRoots(DriveId))
            if (sr.Source == SharedSource.Shortcut && sr.HostItemId == hostItemId) _e.RemoveSharedRootRows(sr);
    }

    /// <summary>
    /// Atualiza a pasta virtual "Compartilhados comigo" com /me/drive/sharedWithMe (no máximo a cada
    /// <see cref="SharedPollSeconds"/>, ou já com <paramref name="force"/>). Falha de rede mantém o que havia.
    /// </summary>
    public async Task RefreshSharedWithMeAsync(CancellationToken ct, bool force = false)
    {
        EnsureSharedContainer();
        if (SharedContainerLid == 0) return;
        if (!force && DateTime.UtcNow - _lastSharedWithMe < TimeSpan.FromSeconds(SharedPollSeconds)) return;
        List<RemoteItem> list;
        try { list = await Api.GetSharedWithMeAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Debug($"[{Config.DisplayName}] compartilhados comigo: {ex.Message}");
            return;
        }
        _lastSharedWithMe = DateTime.UtcNow;
        await _mutate.WaitAsync(ct);
        try
        {
            Db.InTransaction(() =>
            {
                var existing = Db.SharedRoots(DriveId).Where(s => s.Source == SharedSource.SharedWithMe)
                    .ToDictionary(s => s.RemoteDriveId + "!" + s.RemoteItemId);
                var wanted = new HashSet<string>();
                foreach (var r in list)
                {
                    if (r.RemoteDriveId == null || r.RemoteId == null || r.IsPackage) continue;
                    var id = r.RemoteDriveId + "!" + r.RemoteId;
                    if (!wanted.Add(id) || existing.ContainsKey(id)) continue;
                    var name = UniqueName(SharedContainerLid, string.IsNullOrWhiteSpace(r.Name) ? r.RemoteId : r.Name.Replace('/', '-'));
                    _e.AddSharedRootRows(DriveId, SharedSource.SharedWithMe, null, r, SharedContainerLid, name);
                }
                foreach (var (id, sr) in existing)
                    if (!wanted.Contains(id)) _e.RemoveSharedRootRows(sr);
            });
        }
        finally { _mutate.Release(); }
        _e.ReconcileShared();
    }

    /// <summary>
    /// Sub-drive: não há delta fora da raiz da drive (OneDrive for Business/SharePoint), então a pasta compartilhada
    /// é listada inteira (children, paginado) e aplicada como uma enumeração completa: o que sumiu é removido.
    /// Raiz inacessível (404/403: compartilhamento removido) esvazia a sub-drive.
    /// </summary>
    async Task RunSharedScanAsync(CancellationToken ct)
    {
        var sr = Shared!;
        if (!Ready) return;
        RemoteItem? root;
        try { root = await Api.GetItemAsync(ApiDriveId, sr.RemoteItemId, ct); }
        catch (GraphException g) when (g.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) { root = null; }
        if (root == null)
        {
            if (!Inaccessible) Log.Add(ActivityKind.Info, $"[{Config.DisplayName}] o item compartilhado não está mais acessível");
            Inaccessible = true;
            await _mutate.WaitAsync(ct);
            try { Db.InTransaction(() => Sweep(new HashSet<string>())); }
            finally { _mutate.Release(); }
            return;
        }
        Inaccessible = false;

        var writable = await Api.CanWriteAsync(ApiDriveId, sr.RemoteItemId, ct);
        if (writable != sr.Writable)
        {
            Shared = sr = sr with { Writable = writable };
            Db.SaveSharedRoot(sr);
        }

        var items = new List<RemoteItem>();
        var truncated = false;
        if (root.IsFolder)
        {
            var queue = new Queue<string>();
            queue.Enqueue(sr.RemoteItemId);
            while (queue.Count > 0 && !truncated)
            {
                var folder = queue.Dequeue();
                string? next = null;
                do
                {
                    var page = await Api.GetChildrenAsync(ApiDriveId, folder, next, ct);
                    foreach (var c in page.Items)
                    {
                        c.ParentId = folder;
                        items.Add(c);
                        if (c.IsFolder && !c.IsPackage && !c.IsRemoteShortcut) queue.Enqueue(c.Id);
                    }
                    next = page.NextLink;
                } while (next != null);
                truncated = items.Count > SharedMaxItems;
            }
            if (truncated) Log.Add(ActivityKind.Info, $"[{Config.DisplayName}] pasta compartilhada com mais de {SharedMaxItems} itens: listagem parcial");
        }

        await _mutate.WaitAsync(ct);
        try
        {
            var seen = new HashSet<string>();
            var changes = 0;
            lock (Db.Lock) UpdateSharedRoot(root);
            var pending = items;
            while (pending.Count > 0)
            {
                var next = new List<RemoteItem>();
                foreach (var batch in pending.Chunk(DeltaBatchSize))
                {
                    ct.ThrowIfCancellationRequested();
                    Db.InTransaction(() =>
                    {
                        foreach (var r in batch)
                        {
                            seen.Add(r.Id);
                            if (ApplyRemote(r)) changes++;
                            else next.Add(r);
                        }
                    });
                    await Task.Yield();
                }
                if (next.Count == pending.Count) break;
                pending = next;
            }
            if (!truncated) Db.InTransaction(() => Sweep(seen));
            if (changes > 0) WakePins();
        }
        finally { _mutate.Release(); }
    }

    /// <summary>Metadados da raiz compartilhada (nome fica o do atalho/da lista); arquivo: versão do conteúdo.</summary>
    void UpdateSharedRoot(RemoteItem r)
    {
        var cur = Db.Get(RootLid);
        if (cur == null || cur.Dirty) return;
        var ctag = r.CTag ?? r.ETag;
        if (!cur.IsDir && ctag != null && cur.CTag != ctag)
        {
            cur.CTag = ctag;
            cur.Hash = r.QuickXorHash;
            if (cur.CachedCTag != null && !_e.Db.IsEffectivelyPinned(cur.Lid) && !_e.IsOpen(cur.Lid))
            {
                _e.Cache.Delete(DriveId, cur.Lid);
                cur.CachedCTag = null;
            }
        }
        if (!cur.IsDir) cur.Size = r.Size;
        cur.ETag = r.ETag;
        cur.MTime = r.LastModified.ToUnixTimeMilliseconds();
        Db.Update(cur);
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
                if (op.Kind == OpKind.Upload && now - op.Created < UploadSettleMs)
                {
                    wait = TimeSpan.FromMilliseconds(UploadSettleMs - (now - op.Created) + 50);
                    break;
                }
                Busy = true;
                try
                {
                    await _mutate.WaitAsync(ct);
                    try { await ExecuteAsync(op, ct); }
                    finally { _mutate.Release(); }
                    Db.CompleteOp(op.Id);
                    Db.ClearFailures(op.Lid, op.Kind);
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
                    // erro permanente: sai da fila (para não travá-la), mas fica registrado em "Problemas"
                    // para o usuário ver e tentar de novo — o item continua com as alterações locais
                    Db.FailPermanently(op, ex.Message);
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

    /// <summary>Sessão de upload guardada na própria operação da fila (sobrevive a reinícios).</summary>
    sealed class OpUploadSession(StateDb db, long opId, string? url, string snapshot, long gen) : IUploadSessionStore
    {
        public string? Url { get; private set; } = url;

        public void Save(string? newUrl)
        {
            Url = newUrl;
            db.SetOpUpload(opId, newUrl, snapshot, gen);
        }
    }

    /// <summary>Devolve à fila uma operação que tinha falhado de forma permanente.</summary>
    public bool RetryFailure(long failureId)
    {
        if (Db.RetryFailure(failureId) == null) return false;
        LastError = null;
        WakeOps();
        return true;
    }


    string ParentRemoteId(Item item)
    {
        var parent = Db.Get(item.ParentLid) ?? throw new InvalidOperationException($"Pasta pai de '{item.Name}' não existe mais");
        if (parent.ItemId != null) return parent.ItemId;
        // a nuvem recusou a pasta (ex.: caminho longo demais) e ela está em Problemas: esperar por ela (erro
        // transitório) prenderia a cabeça da fila — e a drive inteira — para sempre
        if (Db.HasFailure(parent.Lid))
            throw new GraphException(HttpStatusCode.FailedDependency, "parentFailed",
                $"A pasta '{parent.Name}' não pôde ser criada na nuvem (veja Problemas); '{item.Name}' não foi enviado");
        throw new GraphException(HttpStatusCode.ServiceUnavailable, "parentPending", $"A pasta de '{item.Name}' ainda não foi criada na nuvem");
    }

    async Task ExecuteAsync(Op op, CancellationToken ct)
    {
        switch (op.Kind)
        {
            case OpKind.Mkdir:
            {
                var item = Db.Get(op.Lid);
                if (item == null || item.ItemId != null || item.LocalOnly || !item.IsDir) return;
                var r = await Api.CreateFolderAsync(ApiDriveId, ParentRemoteId(item), item.Name, ct);
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
                await Api.DeleteAsync(ApiDriveId, op.RemoteId, ct);
                Log.Add(ActivityKind.Delete, $"Excluído na nuvem (vai para a Lixeira do OneDrive): {op.Name ?? op.RemoteId}");
                if (Shared == null) Db.AddTrash(DriveId, op.RemoteId, op.Name ?? op.RemoteId, op.IsDir);
                return;
            case OpKind.Move:
            {
                var item = Db.Get(op.Lid);
                if (item == null || item.ItemId == null || item.LocalOnly) return;
                var r = await Api.MoveAsync(ApiDriveId, item.ItemId, ParentRemoteId(item), item.Name, ct);
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
                var r = await Api.SetModifiedAsync(ApiDriveId, item.ItemId, DateTimeOffset.FromUnixTimeMilliseconds(item.MTime), ct);
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
                if (cur != null && cur.ItemId != null)
                {
                    // sem o conteúdo local não há o que enviar: volta para a versão da nuvem (baixa ao abrir)
                    cur.Dirty = false;
                    cur.CachedCTag = null;
                    Db.Update(cur);
                }
            }
            throw new ContentMissingException(item.ItemId != null
                ? $"As alterações locais de '{item.Name}' se perderam (o arquivo sumiu do cache); o item voltou à versão da nuvem."
                : $"O conteúdo de '{item.Name}' sumiu do cache antes de ser enviado.");
        }

        // Cópia congelada (no btrfs vira reflink): o usuário pode continuar editando durante o envio. Fica
        // registrada na operação com a sessão de upload: após queda de rede ou reinício, o envio continua de
        // onde parou — desde que o arquivo não tenha mudado (mesma geração).
        string snap;
        long gen;
        var resume = op.UploadSnapshot != null && op.UploadGen == item.DirtyGen && File.Exists(op.UploadSnapshot);
        if (resume)
        {
            snap = op.UploadSnapshot!;
            gen = op.UploadGen;
        }
        else
        {
            if (op.UploadSnapshot != null) try { File.Delete(op.UploadSnapshot); } catch { }
            snap = _e.Cache.NewUploadSnapshot();
            File.Copy(path, snap, true);
            gen = item.DirtyGen;
            Db.SetOpUpload(op.Id, null, snap, gen);
        }
        var session = new OpUploadSession(Db, op.Id, resume ? op.UploadUrl : null, snap, gen);
        var keepSnapshot = false;
        var transfer = Log.StartTransfer(item.Name, new FileInfo(snap).Length, upload: true);
        RemoteItem r;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, transfer.Token);
            r = await Api.UploadAsync(ApiDriveId, parentId, item.Name, item.ItemId, item.ItemId != null ? item.ETag : null,
                snap, DateTimeOffset.FromUnixTimeMilliseconds(item.MTime), n => transfer.Done = n, linked.Token, session);
        }
        catch (OperationCanceledException) when (transfer.Cancelled && !ct.IsCancellationRequested)
        {
            // vira erro permanente: sai da fila (que só avança pela cabeça) e fica em "Problemas" para
            // "Tentar de novo"; as alterações locais continuam, e editar o arquivo de novo gera outro envio
            throw new TransferCancelledException(item.Name, upload: true);
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
                    RecordConflict(cur.Lid, old, cur.Name, "já existia um arquivo com esse nome na nuvem",
                        $"Conflito: '{old}' já existe na nuvem; a versão local foi salva como '{cur.Name}'");
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
        catch (Exception ex) when (ex is GraphException { IsTransient: true } || ex is OperationCanceledException || Errors.IsNetworkError(ex, ct))
        {
            keepSnapshot = true; // a próxima tentativa (ou o próximo início do app) retoma com a mesma cópia
            throw;
        }
        finally
        {
            Log.EndTransfer(transfer);
            if (!keepSnapshot)
            {
                try { File.Delete(snap); } catch { }
                Db.SetOpUpload(op.Id, null, null, 0);
            }
        }

        lock (Db.Lock)
        {
            var cur = Db.Get(item.Lid);
            if (cur == null)
            {
                // excluído localmente durante o envio
                Db.Enqueue(DriveId, OpKind.Delete, 0, r.Id, null, item.Name);
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
            // editado durante o envio: o pedido de envio feito nesse meio tempo coalesceu com esta operação (que sai
            // da fila agora) — enfileira outro, senão a versão nova ficaria só aqui
            if (cur.Dirty) Db.Enqueue(DriveId, OpKind.Upload, cur.Lid, runningOp: op.Id);
            // data alterada durante o envio (ex.: utimens de "cp -p" num item ainda sem id remoto)
            else if (cur.MTime != item.MTime) Db.Enqueue(DriveId, OpKind.SetTime, cur.Lid);
        }
        Log.Add(ActivityKind.Upload, $"Enviado: {item.Name}");
    }

    /// <summary>Arquivo mudou na nuvem e aqui: mantém as duas versões (como o cliente do Windows).</summary>
    async Task HandleEditConflictAsync(Item item, CancellationToken ct)
    {
        var remote = item.ItemId != null ? await Api.GetItemAsync(ApiDriveId, item.ItemId, ct) : null;
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
            RecordConflict(copy.Lid, cur.Name, copy.Name, "edição nos dois lados (a versão da nuvem ficou com o nome original)",
                $"Conflito de edição em '{cur.Name}': sua versão foi salva como '{copy.Name}'");
        }
    }

    // ---------- itens fixados ("Sempre manter neste dispositivo") ----------

    /// <summary>Tamanho do lote de downloads de itens fixados (lotes cheios emendam sem esperar).</summary>
    public static int PinBatchSize { get; set; } = 500;

    async Task PinLoop(CancellationToken ct)
    {
        if (Ready && !Offline && !_e.Paused)
        {
            var batch = Db.PinnedNeedingDownload(DriveId, PinBatchSize);
            var failures = 0;
            var stopBatch = 0;
            await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = _downloadSlots.Limit, CancellationToken = ct }, async (it, token) =>
            {
                if (_e.Paused || Volatile.Read(ref stopBatch) != 0) return;
                try { await _e.EnsureContentAsync(it.Lid, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (FileNotFoundException) { }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failures);
                    if (HandleError(ex, $"baixar '{it.Name}'")) Interlocked.Exchange(ref stopBatch, 1);
                }
            });
            // lote cheio e sem falhas: ainda há o que baixar — continua já (antes esperava 5 min a cada 500).
            // Com falha, espera: um item que sempre falha voltaria no próximo lote e giraria sem parar.
            if (failures == 0 && !_e.Paused && batch.Count == PinBatchSize) return;
        }
        await _pinSignal.WaitAsync(TimeSpan.FromMinutes(5), ct);
        while (_pinSignal.CurrentCount > 0) await _pinSignal.WaitAsync(ct);
    }
}
