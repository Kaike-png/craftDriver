using System.Collections.Concurrent;
using System.Globalization;
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

    /// <summary>
    /// Informado pelo sistema de arquivos: passa os fds abertos de um item para outro (o arquivo do cache foi movido
    /// para ele — cópia de conflito). Chamado com <see cref="StateDb.Lock"/> preso.
    /// </summary>
    public Action<long, long> MoveOpenHandles { get; set; } = (_, _) => { };

    /// <summary>Mudanças do estado (inclui delta remoto); o consumidor nunca deve bloquear nem chamar o kernel aqui.</summary>
    public event Action<Item?, Item?> ItemChanged
    {
        add => Db.ItemChanged += value;
        remove => Db.ItemChanged -= value;
    }

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
        try { Cache.CleanStale(Db.UploadSnapshots(), Db.Get); }
        catch (Exception ex) { Activity.Debug("Limpeza de temporários: " + ex.Message); }
        // o envio só entra na fila no release do fd: escrita com o arquivo ainda aberto quando o app encerrou (desmontagem
        // sem release) ou caiu deixa o item "sincronizando" para sempre — e o delta passaria a ignorar as versões remotas dele
        foreach (var (driveId, lid) in Db.DirtyWithoutUpload())
        {
            Db.Enqueue(driveId, OpKind.Upload, lid);
            Activity.Debug($"Alteração não enviada do encerramento anterior (lid {lid}): envio reenfileirado");
        }
        ResetRunningOpsOnce();
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
                StopDrive(id, ds);
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
            StartDrive(id, ds); // depois que os laços da instância anterior desta drive terminarem (§23.4)
        }
        ReconcileShared();
    }

    /// <summary>Remove dados locais de uma drive (ao remover biblioteca/conta).</summary>
    public void ForgetDrive(string driveId)
    {
        if (_drives.TryRemove(driveId, out var ds)) StopDrive(driveId, ds);
        DeleteForgottenDrive(driveId);
        ReconcileShared();
        // Não espera aqui (quem chama pode estar com Db.Lock, que um trabalhador da drive aguarda): o que os laços
        // antigos ainda gravarem até parar (ex.: commit de envio tardio enfileirando a exclusão do item que sumiu —
        // que apagaria o arquivo na nuvem se a biblioteca fosse adicionada de novo) é apagado de novo quando terminarem.
        // Uma nova instância da mesma drive só começa depois disso (StartDrive espera a mesma cadeia).
        AfterDriveStopped(driveId, () => DeleteForgottenDrive(driveId));
    }

    void DeleteForgottenDrive(string driveId)
    {
        foreach (var sr in Db.SharedRoots(driveId)) Cache.DeleteDrive(sr.Key);
        Db.DeleteDriveData(driveId);
        Cache.DeleteDrive(driveId);
        // itens de outras drives que apareciam nela (atalhos, "Compartilhados comigo")
        Db.InTransaction(() =>
        {
            foreach (var sr in Db.SharedRoots(driveId)) RemoveSharedRootRows(sr);
            Db.DeleteDriveRows(ContainerKey(driveId));
        });
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
    public int MaxDownloadResumes { get => Config.MaxDownloadResumes; set => Config.MaxDownloadResumes = value; }

    /// <summary>Downloads com tamanho/hash divergente são refeitos até este número de vezes.</summary>
    public int DownloadAttempts { get => Config.DownloadAttempts; set => Config.DownloadAttempts = value; }

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
            // apagado enquanto aberto, ou versão nova na nuvem (sessão desligada): os blocos não servem mais
            if (s.Detached || Db.Get(s.Lid) is not { } atual || atual.CTag != s.CTag) s.DeleteFiles();
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
        // órfãos primeiro: não podem fazer o LRU despejar itens válidos para abrir espaço que eles ocupam
        Cache.DeleteOrphanPartials(Db.Get);
        Cache.DeleteOrphanUploads(Db.UploadSnapshots);
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
                StopDrive(key, ds);
                _shared.TryRemove(key, out _);
                if (!rows.ContainsKey(key))
                {
                    Db.DeleteDriveData(key); // o que a varredura possa ter gravado depois da remoção
                    Cache.DeleteDrive(key);
                    // e o que os laços ainda gravarem até pararem, se a raiz não voltou nesse meio tempo
                    AfterDriveStopped(key, () =>
                    {
                        if (Db.SharedRoots().Any(r => r.Key == key)) return;
                        Db.DeleteDriveData(key);
                        Cache.DeleteDrive(key);
                    });
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
                StartDrive(row.Key, ds);
            }
        }
    }

    // ---------- ciclo de vida das instâncias de DriveSync (§23.4) ----------

    /// <summary>
    /// Por drive (ou sub-drive): término dos laços das instâncias paradas + limpezas encadeadas depois delas. Uma
    /// instância nova da mesma drive só começa quando isto termina — senão ela reivindicava ops que o trabalhador
    /// antigo ainda executava (envio duplicado) ou gravava sobre dados que a remoção ainda ia apagar.
    /// </summary>
    readonly ConcurrentDictionary<string, Task> _stopping = new();
    int _runningOpsReset;

    /// <summary>
    /// Ops "running" de um encerramento anterior (queda do processo) voltam a pendentes — só no início do motor: ao
    /// religar uma drive, os trabalhadores antigos ainda podem estar com elas (são devolvidas por eles ao parar).
    /// </summary>
    void ResetRunningOpsOnce()
    {
        if (Interlocked.Exchange(ref _runningOpsReset, 1) != 0) return;
        foreach (var id in Config.Drives.Select(d => d.DriveId).Concat(Db.SharedRoots().Select(r => r.Key)).Distinct())
            Db.ResetRunningOps(id);
    }

    /// <summary>Para a instância (sem esperar) e registra o término dos laços dela na cadeia da drive.</summary>
    void StopDrive(string key, DriveSync ds)
    {
        var stopped = ds.Stop();
        _stopping.AddOrUpdate(key, stopped, (_, prev) => prev.IsCompleted ? stopped : Task.WhenAll(prev, stopped));
    }

    /// <summary>Executa <paramref name="action"/> quando os laços parados da drive terminarem (na hora, se não há nenhum).</summary>
    void AfterDriveStopped(string key, Action action)
    {
        Task Chain(Task prev) => prev.ContinueWith(_ =>
        {
            try { action(); }
            catch (Exception ex) { Activity.Debug($"Limpeza depois de parar a drive {key}: {ex.Message}"); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        _stopping.AddOrUpdate(key, _ => Chain(Task.CompletedTask), (_, prev) => Chain(prev));
    }

    /// <summary>Liga a instância já registrada; se uma anterior da mesma drive ainda está parando, liga depois dela.</summary>
    void StartDrive(string key, DriveSync ds)
    {
        if (!_stopping.TryGetValue(key, out var prev) || prev.IsCompleted)
        {
            ds.Start(_cts.Token);
            return;
        }
        Activity.Debug($"[{ds.Config.DisplayName}] esperando a instância anterior terminar antes de religar");
        // Start é ignorado se esta instância for parada antes (desligada de novo, motor encerrado)
        prev.ContinueWith(_ => ds.Start(_cts.Token), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
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
    public int SharedPollSeconds { get => _e.Config.SharedPollSeconds; set => _e.Config.SharedPollSeconds = value; }
    /// <summary>Limite de itens por varredura de pasta compartilhada (acima disso a varredura fica parcial e nada é removido).</summary>
    public int SharedMaxItems { get => _e.Config.SharedMaxItems; set => _e.Config.SharedMaxItems = value; }
    DateTime _lastSharedWithMe = DateTime.MinValue;

    public long RootLid { get; private set; }
    public bool Ready => RootLid != 0;
    public bool Offline { get; private set; }
    public bool AuthRequired { get; private set; }
    int _busy;
    public bool Busy => Volatile.Read(ref _busy) > 0;
    public string? LastError { get; private set; }
    public DateTime? LastSync { get; private set; }
    public long QuotaTotal { get; private set; }
    public long QuotaUsed { get; private set; }

    /// <summary>Exclusões retidas aguardando confirmação do usuário.</summary>
    public bool DeleteHold { get; private set; }
    /// <summary>Itens apagados localmente desde que a retenção começou.</summary>
    public long HeldDeleteCount { get; private set; }

    /// <summary>Exclusões só vão para a nuvem depois desse tempo sem novas exclusões locais.</summary>
    public int DeleteQuietMs { get => _e.Config.DeleteQuietMs; set => _e.Config.DeleteQuietMs = value; }

    /// <summary>
    /// Espera depois de o arquivo ser fechado antes de enviar. Editores (LibreOffice, OnlyOffice, WPS…)
    /// gravam num temporário e o renomeiam por cima do original logo em seguida; enviar antes disso
    /// criaria o temporário na nuvem só para apagá-lo depois (indo parar na Lixeira).
    /// </summary>
    public int UploadSettleMs { get => _e.Config.UploadSettleMs; set => _e.Config.UploadSettleMs = value; }
    /// <summary>Janela usada para somar exclusões de uma mesma "rajada".</summary>
    public TimeSpan MassDeleteWindow { get => _e.Config.MassDeleteWindow; set => _e.Config.MassDeleteWindow = value; }

    readonly object _deleteLock = new();
    long _retencaoGravada = -1;
    bool _gravacaoRetencaoAgendada;
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

    readonly object _lifecycle = new();
    bool _stopRequested;
    Task _loops = Task.CompletedTask;

    /// <summary>
    /// Liga os três laços (uma vez só; ignorado depois de <see cref="Stop"/>). Ops "running" de queda anterior são
    /// devolvidas no início do motor (<see cref="SyncEngine.Start"/>), não aqui: ao religar, os trabalhadores da
    /// instância anterior ainda podiam estar com elas.
    /// </summary>
    internal void Start(CancellationToken parent)
    {
        lock (_lifecycle)
        {
            if (_stopRequested || _cts != null || parent.IsCancellationRequested) return;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
            var ct = _cts.Token;
            _loops = Task.WhenAll(
                Task.Run(() => RunLoop(DeltaLoop, ct)),
                Task.Run(() => RunLoop(OpsLoop, ct)),
                Task.Run(() => RunLoop(PinLoop, ct)));
        }
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

    /// <summary>
    /// Cancela os laços e devolve a tarefa que termina quando os três (e os trabalhadores da fila) acabarem. Não
    /// espera: quem chama pode estar com Db.Lock, que um trabalhador aguarda (eventos do motor disparam sob ele).
    /// </summary>
    internal Task Stop()
    {
        CancellationTokenSource? cts;
        Task loops;
        lock (_lifecycle)
        {
            _stopRequested = true;
            cts = _cts;
            loops = _loops;
        }
        cts?.Cancel(); // fora do lock: continuações canceladas podem rodar aqui mesmo
        return loops;
    }

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
                var total = threshold > 0 ? _recentDeletes.Sum(x => x.n) : 0;
                if (threshold > 0 && total >= threshold)
                {
                    DeleteHold = true;
                    HeldDeleteCount = total;
                    raise = true;
                }
            }
            if (DeleteHold) GravarRetencao(agora: raise);
        }
        if (raise)
        {
            Log.Add(ActivityKind.Conflict, $"[{Config.DisplayName}] {HeldDeleteCount} itens foram apagados. As exclusões na nuvem estão retidas até você confirmar.");
            _e.RaiseMassDelete(this);
        }
    }

    /// <summary>
    /// Persiste a retenção (sob _deleteLock). Ao entrar em retenção grava na hora; depois o total só muda na memória
    /// e é gravado no máximo uma vez por segundo — um rm -rf com retenção ativa fazia um UPDATE drives por arquivo.
    /// </summary>
    void GravarRetencao(bool agora)
    {
        if (agora)
        {
            Db.SetDeleteHold(DriveId, true, HeldDeleteCount);
            _retencaoGravada = HeldDeleteCount;
            return;
        }
        if (_retencaoGravada == HeldDeleteCount || _gravacaoRetencaoAgendada) return;
        _gravacaoRetencaoAgendada = true;
        _ = Task.Delay(1000).ContinueWith(_ =>
        {
            lock (_deleteLock)
            {
                _gravacaoRetencaoAgendada = false;
                // confirmada/restaurada nesse meio-tempo: quem desfez a retenção já gravou (false, 0)
                if (DeleteHold && _retencaoGravada != HeldDeleteCount)
                    try { GravarRetencao(agora: true); }
                    catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException) { } // banco já fechado (encerrando)
            }
        }, TaskScheduler.Default);
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
    /// Usuário desistiu: busca cada id remoto das exclusões retidas, reinsere os itens e só então tira as exclusões da
    /// fila. Não zera o deltaLink nem reenumera a drive (envios novos durante a restauração não entram num Sweep).
    /// Falha de rede mantém a fila e a retenção para tentar de novo.
    /// </summary>
    public async Task RestoreDeletesAsync(CancellationToken ct = default)
    {
        await _mutate.WaitAsync(ct);
        try
        {
            var deletes = Db.DeleteOps(DriveId);
            var items = new Dictionary<string, RemoteItem>();
            var gone = new HashSet<string>(); // já excluídos na nuvem (outro dispositivo): vão para a Lixeira, não voltam
            async Task Fetch(string id, HashSet<string> chain)
            {
                if (id == (Shared?.RemoteItemId ?? Db.Get(RootLid)?.ItemId) || items.ContainsKey(id)) return;
                if (!chain.Add(id)) throw new InvalidOperationException("Ciclo nos ancestrais da exclusão a restaurar.");
                var r = await Api.GetItemAsync(ApiDriveId, id, ct);
                if (r == null) { gone.Add(id); return; } // já excluído por outro dispositivo: não há o que restaurar localmente
                items[id] = r;
                if (r.ParentId != null && Db.GetByRemote(DriveId, r.ParentId) == null)
                    await Fetch(r.ParentId, chain);
            }
            foreach (var (id, _, _, _) in deletes)
                if (id != null) await Fetch(id, new HashSet<string>());

            // mesma ordem de locks de RecordLocalDeletes/ConfirmDeletes: _deleteLock → Db.Lock
            lock (_deleteLock)
            {
                Db.InTransaction(() =>
                {
                    var pending = items.Values.ToList();
                    while (pending.Count > 0)
                    {
                        var next = new List<RemoteItem>();
                        foreach (var r in pending)
                            if (!ApplyRemote(r, restoring: true)) next.Add(r);
                        if (next.Count == pending.Count) throw new InvalidOperationException("Pasta pai não encontrada ao restaurar exclusões.");
                        pending = next;
                    }
                    // a exclusão foi feita (em outro dispositivo): fica restaurável na Lixeira como as daqui (§23.15);
                    // filhos de pasta já excluída vão junto com ela
                    if (Shared == null)
                        foreach (var (id, parentRemote, name, isDir) in deletes)
                            if (id != null && gone.Contains(id) && (parentRemote == null || !gone.Contains(parentRemote)))
                                Db.AddTrash(DriveId, id, name ?? id, isDir);
                    Db.RemoveDeleteOps(DriveId);
                    Db.SetDeleteHold(DriveId, false, 0);
                });
                DeleteHold = false;
                HeldDeleteCount = 0;
                _recentDeletes.Clear();
            }
            var restored = deletes.Count - deletes.Count(d => d.remoteId != null && gone.Contains(d.remoteId));
            Log.Add(ActivityKind.Info, $"[{Config.DisplayName}] {deletes.Count} exclusão(ões) cancelada(s): {restored} item(ns) restaurado(s) a partir da nuvem" +
                                       (restored < deletes.Count ? $"; {deletes.Count - restored} já tinha(m) sido excluído(s) na nuvem por outro dispositivo (na Lixeira)" : ""));
        }
        finally { _mutate.Release(); }
        WakePins();
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
                Interlocked.Increment(ref _busy);
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
            finally { Interlocked.Decrement(ref _busy); }
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
    public int DeltaBatchSize { get => _e.Config.DeltaBatchSize; set => _e.Config.DeltaBatchSize = value; }

    readonly SemaphoreSlim _deltaRun = new(1, 1);
    // Commits que aconteceram depois do início desta enumeração (inclusive scans de sub-drives).
    HashSet<long>? _scanProtected;
    readonly ConcurrentDictionary<long, (string Parent, string Name)> _uploadNames = new();
    // Exclusões remotas de PASTAS recebidas nesta rodada do delta, aplicadas só no fim dela (sob Db.Lock). O delta traz
    // só o item movido, não os filhos, e não garante a ordem: aplicando a exclusão do pai antigo primeiro, a pasta que
    // tinha saído dela voltava como nova e vazia, e os filhos nunca mais chegavam.
    Dictionary<string, RemoteItem>? _deferredFolderDeletes;

    public async Task RunDeltaAsync(CancellationToken ct)
    {
        await _deltaRun.WaitAsync(ct);
        lock (Db.Lock) _scanProtected = new();
        try { await RunDeltaCoreAsync(ct); }
        finally
        {
            lock (Db.Lock) _scanProtected = null;
            _deltaRun.Release();
        }
    }

    async Task RunDeltaCoreAsync(CancellationToken ct)
    {
        // Reinício/erro de upload: reaplica deltas adiados assim que o item não estiver mais em transferência.
        await _mutate.WaitAsync(ct);
        try { Db.InTransaction(() => ReplayUploadDeltas()); }
        finally { _mutate.Release(); }
        if (Shared != null)
        {
            await RunSharedScanAsync(ct);
            return;
        }
        lock (Db.Lock) _deferredFolderDeletes = new();
        try { await RunOwnDeltaAsync(ct); }
        finally { lock (Db.Lock) _deferredFolderDeletes = null; }
    }

    async Task RunOwnDeltaAsync(CancellationToken ct)
    {
        var link = Db.GetDrive(DriveId)?.DeltaLink;
        var full = link == null;
        if (full && Db.DeltaProgress(DriveId) == null) Db.BeginDeltaProgress(DriveId);
        var progress = full ? Db.DeltaProgress(DriveId) : null;
        if (progress != null) link = progress.Value.Link;
        HashSet<string>? seen = full ? progress!.Value.Seen : null;
        var deferred = progress?.Deferred ?? new List<RemoteItem>();
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
                lock (Db.Lock) _deferredFolderDeletes?.Clear(); // a enumeração completa + Sweep resolve as exclusões
                Db.BeginDeltaProgress(DriveId);
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
                // Só avança o nextLink depois de aplicar a página (e de guardar os filhos adiados). Se cair antes,
                // reaplica a página idempotente; depois, retoma exatamente deste link sem esquecer os ids já vistos.
                if (full) Db.SaveDeltaProgress(DriveId, page.NextLink, page.Items.Select(r => r.Id), deferred);
            }
            finally { _mutate.Release(); }

            if (page.NextLink != null)
            {
                link = page.NextLink;
                continue;
            }

            if (deferred.Count > 0)
                Log.Debug($"[{Config.DisplayName}] {deferred.Count} itens sem pasta pai conhecida foram ignorados");
            // antes de gravar o deltaLink: se cair aqui, a rodada inteira (com as exclusões) é reaplicada
            await _mutate.WaitAsync(ct);
            try { Db.InTransaction(ApplyDeferredFolderDeletes); }
            finally { _mutate.Release(); }
            if (full && seen != null)
            {
                await _mutate.WaitAsync(ct);
                try { Db.InTransaction(() => Sweep(Db.AllRemoteIds(DriveId), seen, Db.DeltaProgress(DriveId)?.Candidates)); }
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
    /// <param name="restoring">Versão da nuvem que volta por recusa/cancelamento de exclusão local (412, "restaurar
    /// exclusões"): a exclusão dela ainda está na fila e, se o nome estiver ocupado aqui, ela é que fica com nome de conflito.</param>
    bool ApplyRemote(RemoteItem r, bool replayUpload = false, bool restoring = false)
    {
        if (r.IsRoot)
        {
            var root = Db.Get(RootLid);
            if (root != null && root.ItemId != r.Id) { root.ItemId = r.Id; Db.Update(root); }
            return true;
        }

        var existing = Db.GetByRemote(DriveId, r.Id);
        // A resposta do envio fará o commit dos metadados; não interpreta o eco dele como conflito nem exclusão.
        if (!replayUpload && existing != null && Db.HasRunningUpload(existing.Lid))
        {
            Db.DeferUploadDelta(DriveId, existing.Lid, r);
            return true;
        }
        var before = existing?.Clone();
        if (r.IsDeleted)
        {
            if (existing is { IsDir: true } && _deferredFolderDeletes != null)
            {
                // o que saiu dela pode vir depois nesta rodada (ver _deferredFolderDeletes)
                _deferredFolderDeletes[r.Id] = r;
                return true;
            }
            if (existing != null) RemoveRemoteDeleted(existing, r);
            else if (Shared == null) RemoveShortcut(r.Id);
            return true;
        }
        // voltou a aparecer na mesma rodada (ex.: restaurada da Lixeira): a exclusão anterior não vale mais
        _deferredFolderDeletes?.Remove(r.Id);
        if (r.IsPackage) return true;
        if (r.IsRemoteShortcut) return ApplyShortcut(r);

        var parent = r.ParentId != null ? Db.GetByRemote(DriveId, r.ParentId) : null;
        if (parent == null) return false; // pai ainda não chegou: adia

        var ctag = r.CTag ?? r.ETag;
        if (existing == null)
        {
            // Exclusão local ainda na fila para este id (ex.: commit de um envio cujo arquivo foi apagado no meio): o delta
            // não o reinsere nem o adota num item novo de mesmo nome — a exclusão o apagaria depois (§23.1).
            if (!restoring && Db.PendingDeleteFor(DriveId, r.Id)) return true;
            // Eco de um upload novo cujo item local já foi renomeado/movido: o nome congelado identifica o envio,
            // não o nome atual no banco. O commit associará o id ao lid original.
            if (!replayUpload && _uploadNames.FirstOrDefault(x => x.Value.Parent == r.ParentId && x.Value.Name == r.Name) is var uploading && uploading.Key != 0)
            {
                Db.DeferUploadDelta(DriveId, uploading.Key, r);
                return true;
            }
            ReleaseNameFromDeferredDelete(parent.Lid, r);
            // item local ainda não enviado com o mesmo nome → conflito real
            var clash = Db.GetChildNoCase(parent.Lid, r.Name);
            if (!replayUpload && clash != null && Db.HasRunningUpload(clash.Lid))
            {
                Db.DeferUploadDelta(DriveId, clash.Lid, r);
                return true;
            }
            if (restoring && clash is { ItemId: null, LocalOnly: false, IsDir: false } && !r.IsFolder && TryAdoptSameContent(clash, r))
                return true;
            // Nome ocupado aqui por quem vai ficar com ele na nuvem — item com id movido/renomeado para ele (Move na fila)
            // ou, na volta de uma exclusão recusada/cancelada, qualquer item local: a versão da nuvem fica com nome de
            // conflito (aqui e na nuvem) em vez de duas linhas com o mesmo nome no mesmo pai (§23.3).
            if (clash != null && !clash.LocalOnly && clash.ItemId != r.Id && !(clash.IsDir && r.IsFolder && clash.ItemId == null)
                && (clash.ItemId == null ? restoring : HasPendingOp(clash.Lid, OpKind.Move)))
            {
                InsertCloudAsConflict(parent, r, ctag);
                return true;
            }
            if (clash != null && clash.ItemId == null && !clash.LocalOnly)
            {
                if (clash.IsDir && r.IsFolder)
                {
                    clash.ItemId = r.Id;
                    clash.ETag = r.ETag;
                    Db.Update(clash);
                    return true;
                }
                if (!clash.IsDir && !r.IsFolder && TryAdoptSameContent(clash, r)) return true;
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
            ReleaseNameFromDeferredDelete(parent.Lid, r);
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
        // varredura sem mudança (pasta compartilhada a cada 5 min, enumeração completa) não grava nem invalida o cache
        // de caminhos do FUSE (ItemsVersion)
        if (!SameRow(before!, existing)) Db.Update(existing);
        return true;
    }

    /// <summary>Exclusão remota: some daqui e vai para a lista de restauráveis (só nas drives próprias — a API de
    /// restaurar é da própria drive). Sob Db.Lock.</summary>
    void RemoveRemoteDeleted(Item existing, RemoteItem r)
    {
        if (Shared == null) Db.AddTrash(DriveId, r.Id, existing.Name, existing.IsDir);
        RemoveLocal(existing);
    }

    /// <summary>Fim da rodada do delta: aplica as exclusões de pasta adiadas (o que saiu delas já foi movido). Sob Db.Lock.</summary>
    void ApplyDeferredFolderDeletes()
    {
        if (_deferredFolderDeletes is { Count: > 0 } pending)
        {
            foreach (var r in pending.Values.ToList())
                if (Db.GetByRemote(DriveId, r.Id) is { } existing) RemoveRemoteDeleted(existing, r); // a pai pode ter levado junto
            pending.Clear();
        }
        // Sobras do limbo: rodada anterior interrompida cuja repetição não trouxe mais a exclusão (ex.: token expirado →
        // enumeração completa, que limpa as pendências). Tudo que ficou lá foi excluído na nuvem.
        foreach (var orphan in Db.GetChildren(LimboLid))
        {
            if (Shared == null && orphan.ItemId != null) Db.AddTrash(DriveId, orphan.ItemId, orphan.Name, orphan.IsDir);
            RemoveLocal(orphan);
        }
    }

    /// <summary>
    /// Pai "limbo" da drive (não existe como item, nunca é alcançado por caminho): onde fica a pasta excluída na nuvem cujo
    /// nome foi ocupado por outra na mesma rodada do delta, até o fim da rodada (§29).
    /// </summary>
    long LimboLid => -RootLid;

    /// <summary>
    /// Nome que <paramref name="r"/> vai ocupar está com uma pasta cuja exclusão foi adiada (ex.: excluída e recriada na
    /// nuvem com o mesmo nome): tira a pasta antiga do namespace (pai limbo) sem apagar a subárvore — o que for movido
    /// para fora dela ainda nesta rodada sobrevive; o resto sai em <see cref="ApplyDeferredFolderDeletes"/>. Sem op
    /// local (só banco). Se a rodada cair antes do fim, o deltaLink não avançou: a repetição traz a exclusão de novo. Sob Db.Lock.
    /// </summary>
    void ReleaseNameFromDeferredDelete(long parentLid, RemoteItem r)
    {
        if (_deferredFolderDeletes is not { Count: > 0 } pending) return;
        if (Db.GetChildNoCase(parentLid, r.Name) is { ItemId: { } id } occupant && id != r.Id && pending.ContainsKey(id))
        {
            occupant.ParentLid = LimboLid;
            Db.Update(occupant);
        }
    }

    static bool SameRow(Item a, Item b) =>
        a.DriveId == b.DriveId && a.ItemId == b.ItemId && a.ParentLid == b.ParentLid && a.Name == b.Name && a.IsDir == b.IsDir
        && a.Size == b.Size && a.MTime == b.MTime && a.CTime == b.CTime && a.ETag == b.ETag && a.CTag == b.CTag && a.Hash == b.Hash
        && a.CachedCTag == b.CachedCTag && a.Dirty == b.Dirty && a.DirtyGen == b.DirtyGen && a.Pinned == b.Pinned
        && a.LocalOnly == b.LocalOnly && a.LastAccess == b.LastAccess && a.Mode == b.Mode;

    /// <summary>
    /// Arquivo novo local (ainda sem id) e um remoto novo com o mesmo nome e o MESMO conteúdo (QuickXorHash): é o mesmo
    /// arquivo copiado nos dois lados — adota o id remoto em vez de criar cópia de conflito. Não adota com o arquivo
    /// aberto (o conteúdo pode mudar enquanto confere). Chamar com Db.Lock (transação do delta).
    /// </summary>
    bool TryAdoptSameContent(Item local, RemoteItem r)
    {
        if (r.QuickXorHash == null || local.Size != r.Size || _e.IsOpen(local.Lid)) return false;
        var path = _e.Cache.PathFor(local.DriveId, local.Lid);
        string hash;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length != r.Size) return false;
            hash = QuickXorHash.ComputeFile(path);
        }
        catch (IOException) { return false; }
        if (hash != r.QuickXorHash) return false;
        local.ItemId = r.Id;
        local.ETag = r.ETag;
        local.CTag = r.CTag ?? r.ETag;
        local.Hash = r.QuickXorHash;
        local.Dirty = false;
        local.CachedCTag = local.CTag;
        // vale a data da nuvem: um PATCH só para a data criaria uma versão a mais no OneDrive
        local.MTime = r.LastModified.ToUnixTimeMilliseconds();
        Db.Update(local); // o envio pendente vira no-op (item limpo)
        Log.Add(ActivityKind.Info, $"'{local.Name}' já estava na nuvem com o mesmo conteúdo: vinculado sem cópia de conflito");
        return true;
    }

    /// <summary>
    /// Insere a versão da nuvem com nome de conflito (o item local fica com o nome) e a renomeia também na nuvem: o Move
    /// leva o nome antigo em ops.name/parent_remote, e quem vai ocupar esse nome espera ele sair da fila
    /// (<c>StateDb.ClaimOps</c>) — sem isso o envio/Move local daria 409. Sob Db.Lock.
    /// </summary>
    void InsertCloudAsConflict(Item parent, RemoteItem r, string? ctag)
    {
        var item = new Item
        {
            DriveId = DriveId, ItemId = r.Id, ParentLid = parent.Lid, Name = UniqueName(parent.Lid, NamePolicy.ConflictName(r.Name)),
            IsDir = r.IsFolder, Size = r.IsFolder ? 0 : r.Size, MTime = r.LastModified.ToUnixTimeMilliseconds(),
            CTime = r.Created.ToUnixTimeMilliseconds(), ETag = r.ETag, CTag = ctag, Hash = r.QuickXorHash,
        };
        Db.Insert(item);
        Db.Enqueue(DriveId, OpKind.Move, item.Lid, parentRemote: r.ParentId, name: r.Name);
        RecordConflict(item.Lid, r.Name, item.Name, "nome já ocupado aqui por outro item",
            $"Conflito: '{r.Name}' voltou da nuvem, mas o nome já está ocupado aqui; a versão da nuvem virou '{item.Name}'");
    }

    /// <summary>
    /// Reaplica o que o delta recebeu durante uploads; ignorar e avançar o deltaLink perdia exclusões/edições que
    /// ocorreram DEPOIS do envio no servidor e ANTES da resposta chegar. Sob _mutate e Db.Lock; no commit pode
    /// reaplicar só a própria lid ainda reservada, nos outros casos espera os uploads terminarem.
    /// </summary>
    void ReplayUploadDeltas(long? committing = null)
    {
        foreach (var (lid, remote) in Db.DeferredUploadDeltas(DriveId, committing))
        {
            if (committing == null && Db.HasRunningUpload(lid)) continue;
            if (Db.Get(lid) == null)
            {
                Db.CompleteUploadDelta(DriveId, lid, remote.Id); // excluído localmente: não ressuscita o eco do upload
                continue;
            }
            if (!ApplyRemote(remote, replayUpload: true)) continue;
            Db.CompleteUploadDelta(DriveId, lid, remote.Id);
        }
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

    /// <summary>
    /// Após enumeração completa: remove o que sumiu da nuvem. <paramref name="known"/> = ids que o banco tinha antes de a
    /// listagem começar (ou todos, filtrados por <paramref name="candidates"/> — os conhecidos no início da enumeração,
    /// persistidos para retomar após reinício); o que ganhou id depois (enviado durante a listagem) não é tocado.
    /// </summary>
    void Sweep(List<(long lid, string itemId)> known, HashSet<string> seen, HashSet<long>? candidates = null)
    {
        foreach (var (lid, itemId) in known)
        {
            if (lid == RootLid || seen.Contains(itemId) || Db.HasRunningUpload(lid)
                || _scanProtected?.Contains(lid) == true
                || (candidates != null && !candidates.Contains(lid))) continue;
            var it = Db.Get(lid);
            // ainda o mesmo item remoto (recriado com outro id, ex. 404 no envio → id novo, não sumiu)
            if (it != null && it.DriveId == DriveId && it.ItemId == itemId) RemoveLocal(it);
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
            try { Db.InTransaction(() => Sweep(Db.AllRemoteIds(DriveId), new HashSet<string>())); }
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

        // OneDrive pessoal: delta da própria pasta (barato depois da 1ª vez); sem suporte, listagem completa por children
        if (root.IsFolder && Config.Kind == "personal" && !_folderDeltaUnsupported && await TryFolderDeltaAsync(root, ct)) return;

        // conhecidos ANTES da listagem (feita fora de _mutate): o que subir durante ela não é varrido
        var known = Db.AllRemoteIds(DriveId);
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
            if (!truncated) Db.InTransaction(() => Sweep(known, seen));
            if (changes > 0) WakePins();
        }
        finally { _mutate.Release(); }
    }

    /// <summary>A Graph recusou o delta por pasta nesta sub-drive (400/403/404/sem suporte): usa só children.</summary>
    bool _folderDeltaUnsupported;

    /// <summary>
    /// Sub-drive de OneDrive pessoal: GET /drives/{dono}/items/{pasta}/delta, com o deltaLink guardado na linha da
    /// sub-drive. HIPÓTESE (não verificado na Graph real): o endpoint funciona para pasta compartilhada de outro usuário
    /// do OneDrive pessoal (documentado só para o OneDrive pessoal). Recusa no 1º pedido → false (cai para children).
    /// </summary>
    async Task<bool> TryFolderDeltaAsync(RemoteItem root, CancellationToken ct)
    {
        var sr = Shared!;
        var link = Db.GetDrive(DriveId)?.DeltaLink;
        var full = link == null;
        var known = full ? Db.AllRemoteIds(DriveId) : null;
        var seen = new HashSet<string>();
        var deferred = new List<RemoteItem>();
        var changes = 0;
        while (true)
        {
            DeltaPage page;
            try { page = await Api.GetFolderDeltaAsync(ApiDriveId, sr.RemoteItemId, link, ct); }
            catch (GraphException g) when (g.Status == HttpStatusCode.Gone || g.Code is "resyncRequired")
            {
                link = null;
                full = true;
                known = Db.AllRemoteIds(DriveId);
                seen.Clear();
                deferred.Clear();
                continue;
            }
            catch (Exception ex) when (ex is NotSupportedException
                                           || ex is GraphException
                                           {
                                               // 403: pasta de outro usuário sem permissão para o delta (a listagem
                                               // por children ainda pode funcionar; se não, ela marca inacessível)
                                               Status: HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Forbidden
                                               or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented
                                           })
            {
                _folderDeltaUnsupported = true;
                Db.SetDeltaLink(DriveId, null);
                Log.Debug($"[{Config.DisplayName}] delta por pasta indisponível ({ex.Message}); usando a listagem completa");
                return false;
            }
            await _mutate.WaitAsync(ct);
            try
            {
                lock (Db.Lock) UpdateSharedRoot(root);
                // a própria raiz vem no delta da pasta com o pai da drive do dono (desconhecido aqui): já tratada acima
                var pending = deferred.Concat(page.Items.Where(r => r.Id != sr.RemoteItemId)).ToList();
                deferred.Clear();
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
                    var noProgress = next.Count == pending.Count;
                    pending = next;
                    if (noProgress) break;
                }
                deferred.AddRange(pending);
            }
            finally { _mutate.Release(); }

            if (page.NextLink != null)
            {
                link = page.NextLink;
                continue;
            }
            if (full && known != null)
            {
                await _mutate.WaitAsync(ct);
                try { Db.InTransaction(() => Sweep(known, seen)); }
                finally { _mutate.Release(); }
            }
            Db.SetDeltaLink(DriveId, page.DeltaLink);
            if (changes > 0) WakePins();
            return true;
        }
    }

    /// <summary>Metadados da raiz compartilhada (nome fica o do atalho/da lista); arquivo: versão do conteúdo.</summary>
    void UpdateSharedRoot(RemoteItem r)
    {
        var cur = Db.Get(RootLid);
        if (cur == null || cur.Dirty) return;
        var before = cur.Clone();
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
        if (!SameRow(before, cur)) Db.Update(cur);
    }

    // ---------- operações (local → nuvem) ----------

    async Task OpsLoop(CancellationToken ct)
    {
        var running = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                running.RemoveAll(t => t.IsCompleted);
                if (!_e.Paused && Ready)
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var slots = Math.Clamp(_e.Config.ParallelUploads, 1, 16) - running.Count;
                    var deleteQuiet = now - Interlocked.Read(ref _lastLocalDelete) >= DeleteQuietMs;
                    if (slots > 0)
                    {
                        var ops = Db.ClaimOps(DriveId, slots, now, now - UploadSettleMs, now - DeleteQuietMs,
                            allowDeletes: !DeleteHold && deleteQuiet);
                        foreach (var op in ops) running.Add(ProcessOpAsync(op, ct));
                        if (ops.Count > 0) continue;
                    }
                }
                // Uma operação em backoff, esperando o pai ou a janela de edição/exclusão não prende as demais.
                // O sinal é acordado também por cada conclusão; o tick curto cobre next_try/settle sem hot loop.
                await _opsSignal.WaitAsync(TimeSpan.FromMilliseconds(200), ct);
                while (_opsSignal.CurrentCount > 0) await _opsSignal.WaitAsync(ct);
            }
        }
        finally
        {
            // Não devolve enquanto houver trabalhadores usando Db/cache: quem encerra espera os laços terminarem antes de fechá-los.
            await Task.WhenAll(running);
        }
    }

    /// <summary>Teto para arquivo persistentemente bloqueado (423). Rede e 401 nunca viram permanentes por contagem.</summary>
    const int LockedAttempts = 10;

    async Task ProcessOpAsync(Op op, CancellationToken ct)
    {
        Interlocked.Increment(ref _busy);
        using var uploadCts = op.Kind == OpKind.Upload ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
        if (uploadCts != null) _uploadCancel[op.Id] = (op.Lid, uploadCts);
        try
        {
            // Upload não prende a aplicação do delta durante a rede: só o commit usa _mutate.
            if (op.Kind == OpKind.Upload) await ExecuteAsync(op, uploadCts!.Token);
            else
            {
                await _mutate.WaitAsync(ct);
                try { await ExecuteAsync(op, ct); }
                finally { _mutate.Release(); }
            }
            Db.CompleteOp(op.Id);
            Db.ClearFailures(op.Lid, op.Kind);
            Offline = false;
            AuthRequired = false;
            Interlocked.Exchange(ref _lastOpOk, Environment.TickCount64);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { Db.ReleaseOp(op.Id); }
        // pasta de destino excluída (§23.5): volta à fila — item apagado vira no-op; item movido para fora sobe no lugar novo
        catch (OperationCanceledException) when (_targetDeleted.ContainsKey(op.Id)) { Db.ReleaseOp(op.Id); }
        catch (RetryNowException) { Db.ReleaseOp(op.Id); }
        catch (Exception ex)
        {
            var transient = HandleError(ex, DescribeOp(op));
            if (ex is GraphException { Status: (HttpStatusCode)423 } && op.Attempts + 1 >= LockedAttempts)
                transient = false;
            var exhausted = transient && ex is GraphException { IsTransient: true } && op.Attempts + 1 >= TransientAttempts
                            && ServerAnswersOthers();
            if (exhausted) Log.Add(ActivityKind.Error, $"[{Config.DisplayName}] {DescribeOp(op)}: tentativas esgotadas ({op.Attempts + 1}); vai para Problemas");
            if (transient && !exhausted)
            {
                var backoff = TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(op.Attempts, 6))));
                Db.FailOp(op.Id, ex.Message, backoff);
            }
            else Db.FailPermanently(op, exhausted ? $"Tentativas esgotadas ({op.Attempts + 1}): {ex.Message}" : ex.Message);
        }
        finally
        {
            _uploadNames.TryRemove(op.Lid, out _);
            _uploadCancel.TryRemove(op.Id, out _);
            _targetDeleted.TryRemove(op.Id, out _);
            Interlocked.Decrement(ref _busy);
            WakeOps();
        }
    }

    /// <summary>
    /// Teto para erro "transitório" do servidor que só este item recebe (5xx/408/429/401 perene, §22.2): depois dele a
    /// operação vai para Problemas ("tentativas esgotadas") em vez de ficar invisível na fila segurando Move/SetTime do
    /// mesmo item e quem espera o nome. Com o backoff de até 5 min, são ~4 h de insistência.
    /// </summary>
    const int TransientAttempts = 50;

    long _lastOpOk;

    /// <summary>
    /// O servidor está atendendo outros pedidos desta drive (delta ou outra operação confirmados na última hora): o erro
    /// é deste item. Sem isso, throttling da conta inteira, queda do serviço ou credencial vencida mandariam a fila
    /// toda para Problemas depois do teto.
    /// </summary>
    bool ServerAnswersOthers()
    {
        var window = TimeSpan.FromHours(1) + TimeSpan.FromSeconds(Math.Max(0, _e.Config.PollIntervalSeconds));
        var lastOk = Interlocked.Read(ref _lastOpOk);
        return LastSync is { } sync && DateTime.Now - sync < window
               || lastOk != 0 && Environment.TickCount64 - lastOk < window.TotalMilliseconds;
    }

    readonly ConcurrentDictionary<long, (long Lid, CancellationTokenSource Cts)> _uploadCancel = new();
    readonly ConcurrentDictionary<long, bool> _targetDeleted = new();

    /// <summary>
    /// Envios em execução cuja pasta de destino saiu do banco (excluída aqui): cancela antes de excluir na nuvem — o que
    /// já tiver sido gravado vai junto com a pasta para a Lixeira do OneDrive (§23.5). Sob _mutate.
    /// </summary>
    void CancelUploadsIntoDeletedFolders()
    {
        foreach (var (opId, (lid, cts)) in _uploadCancel)
            if (_uploadNames.TryGetValue(lid, out var target) && Db.GetByRemote(DriveId, target.Parent) == null)
            {
                _targetDeleted[opId] = true;
                Log.Debug($"[{Config.DisplayName}] envio de '{target.Name}' cancelado: a pasta de destino foi excluída");
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { } // o envio terminou nesse meio tempo
            }
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
    sealed class OpUploadSession(StateDb db, long opId, string? url, string snapshot, long gen, Action<long>? resumed = null) : IUploadSessionStore
    {
        public string? Url { get; private set; } = url;

        public void Resumed(long offset) => resumed?.Invoke(offset);

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
        // O aviso do mkdir pode ter sido descartado. Recria o pedido do pai antes de devolver o filho à fila;
        // ClaimOps respeita a dependência, inclusive se a operação do pai ficou depois da do filho.
        if (!Db.PendingOpsFor(parent.Lid, OpKind.Mkdir)) Db.Enqueue(DriveId, OpKind.Mkdir, parent.Lid);
        throw new RetryNowException();
    }

    async Task ExecuteAsync(Op op, CancellationToken ct)
    {
        switch (op.Kind)
        {
            case OpKind.Mkdir:
            {
                var item = Db.Get(op.Lid);
                if (item == null || item.ItemId != null || item.LocalOnly || !item.IsDir) return;
                var parentRemote = ParentRemoteId(item);
                var r = await Api.CreateFolderAsync(ApiDriveId, parentRemote, item.Name, ct);
                lock (Db.Lock)
                {
                    var cur = Db.Get(op.Lid);
                    // pai e nome: quem recriar esse nome espera esta exclusão (§22.3/§23.1)
                    if (cur == null) { Db.Enqueue(DriveId, OpKind.Delete, 0, r.Id, parentRemote, item.Name, isDir: true); return; }
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
                CancelUploadsIntoDeletedFolders(); // §23.5: envio de filho já apagado não segura nem sobrevive à exclusão
                if (op.ParentRemote != null && Db.PendingDeleteFor(DriveId, op.ParentRemote))
                {
                    Log.Debug($"[{Config.DisplayName}] exclusão de '{op.Name}' coberta pela exclusão da pasta");
                    return;
                }
                try { await Api.DeleteAsync(ApiDriveId, op.RemoteId, op.IsDir ? null : op.ETag, ct); }
                catch (GraphException g) when (g.Status == HttpStatusCode.PreconditionFailed)
                {
                    // offline o usuário apagou uma versão que conhecia; outro dispositivo a editou desde então — não apaga
                    // a nova. Reinsere a versão da nuvem e deixa o aviso em Problemas (exclusão recusada). Se o nome já
                    // foi ocupado aqui, a versão da nuvem volta com nome de conflito (§23.3).
                    var remote = await Api.GetItemAsync(ApiDriveId, op.RemoteId, ct);
                    if (remote != null) Db.InTransaction(() => ApplyRemote(remote, restoring: true));
                    throw new GraphException(HttpStatusCode.PreconditionFailed, g.Code,
                        $"'{op.Name ?? op.RemoteId}' mudou na nuvem depois de ser apagado aqui. A versão mais nova foi preservada; confira antes de excluir de novo.");
                }
                // lid 0 = sobra na nuvem de um envio/mkdir cujo item já tinha sido apagado aqui: não era um item que o
                // usuário viu sincronizado — não entra na Lixeira (§23.5)
                if (op.Lid == 0)
                {
                    Log.Debug($"[{Config.DisplayName}] removida da nuvem a cópia enviada de '{op.Name ?? op.RemoteId}' (apagado durante o envio)");
                    return;
                }
                Log.Add(ActivityKind.Delete, $"Excluído na nuvem (vai para a Lixeira do OneDrive): {op.Name ?? op.RemoteId}");
                if (Shared == null) Db.AddTrash(DriveId, op.RemoteId, op.Name ?? op.RemoteId, op.IsDir);
                return;
            case OpKind.Move:
            {
                var item = Db.Get(op.Lid);
                if (item == null || item.ItemId == null || item.LocalOnly) return;
                RemoteItem r;
                try
                {
                    // sobrescrita intencional já tirou o destino conhecido da fila antes do Move (Delete seguido de Move).
                    // Se apareceu outro id remoto com esse nome depois, NÃO é aquele que o usuário quis substituir: fail.
                    r = await Api.MoveAsync(ApiDriveId, item.ItemId, ParentRemoteId(item), item.Name, replace: false, ct);
                }
                catch (GraphException g) when (g.Status == HttpStatusCode.Conflict)
                {
                    lock (Db.Lock)
                    {
                        var cur = Db.Get(item.Lid);
                        if (cur != null)
                        {
                            var old = cur.Name;
                            cur.Name = UniqueName(cur.ParentLid, NamePolicy.ConflictName(cur.Name));
                            Db.Update(cur);
                            RecordConflict(cur.Lid, old, cur.Name, "destino criado na nuvem antes de mover",
                                $"Conflito ao mover '{old}': já existe outro item na nuvem; sua versão foi salva como '{cur.Name}'");
                        }
                    }
                    throw new RetryNowException();
                }
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

    /// <summary>Geração gravada enquanto a cópia congelada ainda está sendo feita (nenhum item tem geração negativa).</summary>
    const long InvalidSnapshotGen = -1;

    async Task UploadAsync(Op op, CancellationToken ct)
    {
        var item = Db.Get(op.Lid);
        if (item == null || !item.Dirty || item.LocalOnly || item.IsDir)
        {
            // envio cancelado/interrompido de item que já não precisa subir: a cópia congelada não serve mais
            if (op.UploadSnapshot != null) try { File.Delete(op.UploadSnapshot); } catch { }
            return;
        }
        var parentId = ParentRemoteId(item);
        _uploadNames[op.Lid] = (parentId, item.Name);
        Db.SetUploadTarget(op.Id, parentId); // exclusão de outra pasta não espera este envio (ClaimOps)
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
            // o caminho vai para a operação antes de a cópia existir (a limpeza de órfãos não a apaga no meio), com
            // geração inválida até a cópia terminar (uma cópia pela metade nunca é retomada)
            Db.SetOpUpload(op.Id, null, snap, InvalidSnapshotGen);
            try { File.Copy(path, snap, true); }
            catch
            {
                try { File.Delete(snap); } catch { }
                Db.SetOpUpload(op.Id, null, null, 0);
                throw;
            }
            gen = item.DirtyGen;
            Db.SetOpUpload(op.Id, null, snap, gen);
        }
        // retomada sem sessão ("expirou": pode ter sido concluída com a resposta da última parte perdida) de arquivo novo:
        // se a nuvem já tem esse nome com o mesmo conteúdo, foi este envio — adota em vez de recomeçar (fail → 409 → cópia)
        if (resume && op.UploadUrl == null && op.Attempts > 0 && item.ItemId == null
            && await RemoteWithSameContentAsync(parentId, item.Name, snap, ct) is { } already)
        {
            try { File.Delete(snap); } catch { }
            Db.SetOpUpload(op.Id, null, null, 0);
            Log.Debug($"Envio de {item.Name} já tinha terminado na nuvem (resposta perdida): adotado");
            await CommitUploadAsync(item, op, already, gen, ct);
            return;
        }
        var snapLength = new FileInfo(snap).Length;
        var session = new OpUploadSession(Db, op.Id, resume ? op.UploadUrl : null, snap, gen, offset => Log.Debug(
            string.Create(CultureInfo.InvariantCulture, $"Retomando envio de {item.Name} a partir de {offset} bytes (de {snapLength})")));
        var keepSnapshot = false;
        var transfer = Log.StartTransfer(item.Name, snapLength, upload: true);
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
            var same = await RemoteWithSameContentAsync(parentId, item.Name, snap, ct);
            // id com exclusão na fila (o envio anterior deste nome, apagado no meio): não adota — espera a exclusão (§23.1)
            if (same != null && Db.PendingDeleteFor(DriveId, same.Id)) throw new RetryNowException();
            if (same != null)
            {
                Log.Debug($"'{item.Name}' já existia na nuvem com o mesmo conteúdo ({g.Message}): adotado sem cópia de conflito");
                r = same;
            }
            else
            {
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
        }
        catch (GraphException g) when (g.Status == HttpStatusCode.PreconditionFailed)
        {
            await HandleEditConflictAsync(item, op, snap, gen, ct);
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
            if (_targetDeleted.ContainsKey(op.Id)) keepSnapshot = false; // sessão era para a pasta excluída (§23.5)
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

        await CommitUploadAsync(item, op, r, gen, ct);
    }

    /// <summary>
    /// Envio confirmado (ou conteúdo idêntico adotado): grava id/eTag/cTag e limpa o Dirty da geração enviada. Só aqui o
    /// envio pega <c>_mutate</c> (a transferência roda sem ele): o delta pode ter inserido o mesmo id nesse meio tempo.
    /// </summary>
    async Task CommitUploadAsync(Item item, Op op, RemoteItem r, long gen, CancellationToken ct)
    {
        await _mutate.WaitAsync(ct);
        try
        {
            lock (Db.Lock)
            {
                var cur = Db.Get(item.Lid);
                if (cur == null)
                {
                    // excluído localmente durante o envio. Com pai e nome: um item novo com esse nome espera a exclusão
                    // em vez de adotar o id que ela vai apagar (§23.1). Se outro item já adotou o id (409 + mesmo
                    // conteúdo enquanto esta resposta vinha), o arquivo da nuvem é dele: não apaga.
                    if (Db.GetByRemote(DriveId, r.Id) == null)
                        Db.Enqueue(DriveId, OpKind.Delete, 0, r.Id, r.ParentId ?? _uploadNames.GetValueOrDefault(item.Lid).Parent, item.Name, etag: r.ETag);
                    return;
                }
                // Delta pode ter inserido o id enquanto a resposta do upload estava a caminho. Junta no lid local
                // (handles abertos continuam válidos), sem violar ix_items_remote.
                var duplicate = Db.GetByRemote(DriveId, r.Id);
                if (duplicate != null && duplicate.Lid != cur.Lid)
                {
                    if (duplicate.Dirty || _e.IsOpen(duplicate.Lid))
                    {
                        // Alguém já editou/abriu a linha que o delta criou: não a descarta nem seus handles. Ela
                        // vira uma cópia local independente; o lid do upload fica com o id remoto confirmado.
                        var oldName = duplicate.Name;
                        duplicate.ItemId = null;
                        duplicate.ETag = null;
                        duplicate.Name = UniqueName(duplicate.ParentLid, NamePolicy.ConflictName(duplicate.Name));
                        Db.Update(duplicate);
                        if (duplicate.Dirty) Db.Enqueue(DriveId, OpKind.Upload, duplicate.Lid);
                        RecordConflict(duplicate.Lid, oldName, duplicate.Name, "edição local no item recebido durante um envio",
                            $"'{oldName}' recebeu uma edição durante o envio; preservada em '{duplicate.Name}'");
                    }
                    else
                    {
                        // O servidor pode ter renomeado/editado o item DEPOIS de confirmar o envio e antes da
                        // resposta chegar. A linha do delta tem metadados mais novos que a resposta; reaplica-os
                        // depois de juntar os lids (sem perder a intenção de um Move local pendente).
                        if (duplicate.ETag != null && duplicate.ETag != r.ETag)
                            Db.DeferUploadDelta(DriveId, cur.Lid, new RemoteItem
                            {
                                Id = r.Id, Name = duplicate.Name, ParentId = Db.Get(duplicate.ParentLid)?.ItemId,
                                Size = duplicate.Size, ETag = duplicate.ETag, CTag = duplicate.CTag, QuickXorHash = duplicate.Hash,
                                LastModified = DateTimeOffset.FromUnixTimeMilliseconds(duplicate.MTime),
                                Created = DateTimeOffset.FromUnixTimeMilliseconds(duplicate.CTime),
                            });
                        Db.Delete(duplicate.Lid);
                        _e.Cache.Delete(DriveId, duplicate.Lid);
                    }
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
                Db.ProtectDeltaUpload(DriveId, cur.Lid); // commit caiu na janela da enumeração: Sweep não o apaga
                _scanProtected?.Add(cur.Lid);
                if (cur.Name != item.Name || cur.ParentLid != item.ParentLid) Db.Enqueue(DriveId, OpKind.Move, cur.Lid);
                // editado durante o envio: o pedido de envio feito nesse meio tempo coalesceu com esta operação (que sai
                // da fila agora) — enfileira outro, senão a versão nova ficaria só aqui
                if (cur.Dirty) Db.Enqueue(DriveId, OpKind.Upload, cur.Lid, runningOp: op.Id);
                // data alterada durante o envio (ex.: utimens de "cp -p" num item ainda sem id remoto)
                else if (cur.MTime != item.MTime) Db.Enqueue(DriveId, OpKind.SetTime, cur.Lid);
                Db.InTransaction(() => ReplayUploadDeltas(cur.Lid));
            }
        }
        finally { _mutate.Release(); }
        Log.Add(ActivityKind.Upload, $"Enviado: {item.Name}");
    }

    /// <summary>Nuvem já tem <paramref name="name"/> em <paramref name="parentId"/> com o conteúdo de <paramref name="file"/>?</summary>
    async Task<RemoteItem?> RemoteWithSameContentAsync(string parentId, string name, string file, CancellationToken ct)
    {
        var remote = await Api.GetChildByNameAsync(ApiDriveId, parentId, name, ct);
        if (remote is not { IsFolder: false, QuickXorHash: { } hash } || remote.Size != new FileInfo(file).Length) return null;
        return QuickXorHash.ComputeFile(file) == hash ? remote : null;
    }

    /// <summary>
    /// 412 no envio. Só metadados mudaram na nuvem (cTag igual: renomeação, propriedade): atualiza o eTag e tenta de
    /// novo. Conteúdo da nuvem igual ao enviado (hash): adota. Senão mudou nos dois lados: mantém as duas versões (como o
    /// cliente do Windows) — a nuvem fica com o nome original e a local vira cópia; fds abertos passam para a cópia.
    /// </summary>
    async Task HandleEditConflictAsync(Item item, Op op, string snap, long gen, CancellationToken ct)
    {
        var remote = item.ItemId != null ? await Api.GetItemAsync(ApiDriveId, item.ItemId, ct) : null;
        if (remote != null && !remote.IsFolder)
        {
            if (remote.CTag != null && remote.CTag == item.CTag && remote.ETag != item.ETag)
            {
                lock (Db.Lock)
                {
                    var cur = Db.Get(item.Lid);
                    if (cur == null) return;
                    cur.ETag = remote.ETag;
                    Db.Update(cur);
                }
                Log.Debug($"'{item.Name}' mudou só nos metadados na nuvem; enviando de novo com o eTag novo");
                throw new RetryNowException();
            }
            if (remote.QuickXorHash != null && remote.Size == new FileInfo(snap).Length && QuickXorHash.ComputeFile(snap) == remote.QuickXorHash)
            {
                Log.Debug($"'{item.Name}' já está na nuvem com o conteúdo enviado: adotado sem cópia de conflito");
                await CommitUploadAsync(item, op, remote, gen, ct);
                return;
            }
        }
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
            // os fds abertos apontam para o arquivo movido: passam a ser da cópia — escritas seguintes vão para ela (e sobem
            // nela), e o original fica com a versão da nuvem (File.Copy faria o original sujo sobrescrever a nuvem)
            _e.MoveOpenHandles(cur.Lid, copy.Lid);
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
    public int PinBatchSize { get => _e.Config.PinBatchSize; set => _e.Config.PinBatchSize = value; }

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
            // (lote vazio nunca emenda: com tamanho 0 isso virava laço quente)
            if (failures == 0 && !_e.Paused && batch.Count > 0 && batch.Count == PinBatchSize) return;
        }
        await _pinSignal.WaitAsync(TimeSpan.FromMinutes(5), ct);
        while (_pinSignal.CurrentCount > 0) await _pinSignal.WaitAsync(ct);
    }
}
