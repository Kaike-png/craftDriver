using CraftDriver.Core.State;
using Microsoft.Win32.SafeHandles;

namespace CraftDriver.Core.Sync;

/// <summary>A versão que este handle lia mudou na nuvem: os blocos que faltam não podem mais ser baixados.</summary>
public sealed class StaleContentException(string message) : Exception(message);

/// <summary>
/// Leitura por faixas (streaming) de uma versão (cTag) de um arquivo só-na-nuvem. O conteúdo vai para um arquivo
/// esparso de trabalho em <see cref="ContentCache.PartialDir"/> (<c>{parcial}.stream</c>) e cada bloco baixado é
/// marcado num bitmap persistido ao lado (<c>{parcial}.blocks</c>: cabeçalho + 1 byte por bloco), então um reinício
/// reaproveita o que já veio. Um bloco só é marcado depois de gravado e sincronizado no disco; um bloco que falha não
/// é marcado (a leitura devolve erro, nunca zeros). Quando todos os blocos chegam, o arquivo inteiro é verificado
/// (tamanho/QuickXorHash, como no download normal) e só então vira o conteúdo atual do cache; se não conferir, todos
/// os blocos são descartados. Vários leitores do mesmo bloco esperam um único pedido Range.
/// </summary>
public sealed class StreamSession
{
    public const string DataSuffix = ".stream", MapSuffix = ".blocks";
    const int HeaderSize = 16; // "CDS1" + int32 tamanho do bloco + int64 tamanho do arquivo
    static ReadOnlySpan<byte> Magic => "CDS1"u8;

    sealed class Fetch
    {
        public Task Task = Task.CompletedTask;
        public readonly CancellationTokenSource Cts = new();
        public int Waiters;
    }

    sealed class TransferRef(Transfer t)
    {
        public readonly Transfer Transfer = t;
        public int Users;
        public bool Logged;
    }

    readonly SyncEngine _e;
    readonly object _lock = new();
    readonly byte[] _have;
    readonly Dictionary<int, Fetch> _fetching = new();
    SafeFileHandle _data = null!, _map = null!;
    string _currentPath;
    int _present, _generation, _refs;
    TransferRef? _transfer;
    CancellationTokenSource? _fill;
    bool _fillCancelledByUser;
    Task<bool>? _finish;
    bool _verified, _detached, _closed;

    public long Lid { get; }
    public string DriveId { get; }
    public string ItemId { get; }
    public string? CTag { get; }
    public string Name { get; }
    public long Size { get; }
    public int BlockSize { get; }
    public string DataPath { get; }
    public string MapPath { get; }
    public int Blocks => _have.Length;

    /// <summary>Bytes seguidos lidos por um handle a partir dos quais o resto do arquivo é baixado em segundo plano.</summary>
    public int ReadAheadTrigger => Math.Min(BlockSize, 1 << 20);

    internal StreamSession(SyncEngine engine, Item item, int blockSize, string partialBase)
    {
        _e = engine;
        Lid = item.Lid;
        DriveId = item.DriveId;
        ItemId = item.ItemId!;
        CTag = item.CTag;
        Name = item.Name;
        Size = item.Size;
        BlockSize = blockSize;
        DataPath = partialBase + DataSuffix;
        MapPath = partialBase + MapSuffix;
        _currentPath = DataPath;
        _have = new byte[(int)((Size + blockSize - 1) / blockSize)];
        OpenFiles(tryLoad: true);
    }

    /// <summary>Retoma o bitmap gravado (se for desta versão e deste tamanho de bloco) ou cria arquivos novos.</summary>
    void OpenFiles(bool tryLoad)
    {
        if (tryLoad && File.Exists(DataPath) && File.Exists(MapPath))
        {
            try
            {
                var map = File.ReadAllBytes(MapPath);
                if (map.Length == HeaderSize + _have.Length && map.AsSpan(0, 4).SequenceEqual(Magic)
                    && BitConverter.ToInt32(map, 4) == BlockSize && BitConverter.ToInt64(map, 8) == Size
                    && new FileInfo(DataPath).Length == Size)
                {
                    map.AsSpan(HeaderSize).CopyTo(_have);
                    _present = _have.Count(b => b != 0);
                    _data = File.OpenHandle(DataPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                    _map = File.OpenHandle(MapPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                    return;
                }
            }
            catch (IOException) { }
        }
        try { File.Delete(MapPath); } catch (IOException) { }
        try { File.Delete(DataPath); } catch (IOException) { }
        Array.Clear(_have);
        _present = 0;
        // o arquivo de dados existe antes do bitmap: bitmap sem dados nunca acontece
        _data = File.OpenHandle(DataPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        RandomAccess.SetLength(_data, Size); // esparso: só os blocos baixados ocupam disco
        var header = new byte[HeaderSize + _have.Length];
        Magic.CopyTo(header);
        BitConverter.TryWriteBytes(header.AsSpan(4), BlockSize);
        BitConverter.TryWriteBytes(header.AsSpan(8), Size);
        _map = File.OpenHandle(MapPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        RandomAccess.Write(_map, header, 0);
    }

    /// <summary>Bytes ocupados no cache por um arquivo de trabalho (conta só os blocos baixados).</summary>
    public static long StoredBytes(string dataPath)
    {
        var mapPath = dataPath[..^DataSuffix.Length] + MapSuffix;
        try
        {
            var map = File.ReadAllBytes(mapPath);
            if (map.Length < HeaderSize || !map.AsSpan(0, 4).SequenceEqual(Magic)) return 0;
            var block = BitConverter.ToInt32(map, 4);
            var size = BitConverter.ToInt64(map, 8);
            long n = 0;
            for (var i = HeaderSize; i < map.Length; i++) if (map[i] != 0) n++;
            return Math.Min(size, n * block);
        }
        catch (IOException) { return 0; }
    }

    // ---------- estado ----------

    public bool Verified { get { lock (_lock) return _verified; } }
    public bool Detached { get { lock (_lock) return _detached; } }

    long PresentBytesLocked() =>
        _have.Length == 0 ? 0 : Math.Min(Size, (long)_present * BlockSize - (_have[^1] != 0 ? (long)_have.Length * BlockSize - Size : 0));

    (int first, int last) BlockRange(long offset, long length)
    {
        var end = Math.Min(Size, offset + Math.Max(1, length));
        return ((int)(offset / BlockSize), (int)((end - 1) / BlockSize));
    }

    bool HasRangeLocked(long offset, long length)
    {
        if (_verified || offset >= Size) return true;
        var (first, last) = BlockRange(offset, length);
        for (var b = first; b <= last; b++) if (_have[b] == 0) return false;
        return true;
    }

    /// <summary>true se todos os blocos dessa faixa já estão no disco (ler não precisa de rede).</summary>
    public bool HasRange(long offset, long length)
    {
        lock (_lock) return HasRangeLocked(offset, length);
    }

    /// <summary>Lê do arquivo de trabalho; -1 se falta algum bloco da faixa.</summary>
    public int TryRead(Span<byte> buffer, long offset)
    {
        lock (_lock)
        {
            if (_closed) throw new ObjectDisposedException(nameof(StreamSession));
            if (_verified) return RandomAccess.Read(_data, buffer, offset); // já é o arquivo do cache
            if (offset >= Size) return 0;
            var n = (int)Math.Min(buffer.Length, Size - offset);
            if (!HasRangeLocked(offset, n)) return -1;
            return RandomAccess.Read(_data, buffer[..n], offset);
        }
    }

    /// <summary>Handle de leitura para o fd do processo (fstat etc.); as leituras passam por <see cref="TryRead"/>.</summary>
    public SafeFileHandle OpenReadHandle()
    {
        lock (_lock) return File.OpenHandle(_currentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    internal void AddRef()
    {
        lock (_lock) _refs++;
    }

    /// <summary>Solta uma referência; na última para o download em segundo plano e fecha os arquivos (ficam para retomar).</summary>
    internal bool Release()
    {
        List<CancellationTokenSource> cancel;
        lock (_lock)
        {
            if (--_refs > 0) return false;
            _closed = true;
            cancel = _fetching.Values.Select(f => f.Cts).ToList();
            if (_fill != null) cancel.Add(_fill);
            _data.Dispose();
            _map.Dispose();
        }
        // fora do lock: Cancel roda continuações na hora, e elas também pegam o lock e mexem em _fetching
        foreach (var c in cancel) TryCancel(c);
        return true;
    }

    static void TryCancel(CancellationTokenSource? cts)
    {
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>A nuvem tem versão nova: esta sessão só serve o que já baixou e nunca vira cache.</summary>
    internal void Detach()
    {
        CancellationTokenSource? fill;
        lock (_lock)
        {
            _detached = true;
            fill = _fill;
        }
        TryCancel(fill);
    }

    // ---------- download dos blocos ----------

    /// <summary>Garante no disco os blocos da faixa (os que faltam são pedidos em paralelo).</summary>
    public Task EnsureRangeAsync(long offset, long length, CancellationToken ct)
    {
        if (offset >= Size) return Task.CompletedTask;
        var (first, last) = BlockRange(offset, length);
        if (first == last) return EnsureBlockAsync(first, ct);
        var tasks = new List<Task>();
        for (var b = first; b <= last; b++) tasks.Add(EnsureBlockAsync(b, ct));
        return Task.WhenAll(tasks);
    }

    async Task EnsureBlockAsync(int b, CancellationToken ct)
    {
        for (var tries = 0; ; tries++)
        {
            Fetch f;
            lock (_lock)
            {
                if (_have[b] != 0 || _verified) return;
                if (_closed) throw new ObjectDisposedException(nameof(StreamSession));
                if (_detached)
                    throw new StaleContentException($"'{Name}' mudou na nuvem enquanto estava aberto; reabra o arquivo para ler a versão nova.");
                if (!_fetching.TryGetValue(b, out f!))
                {
                    var fetch = f = new Fetch();
                    _fetching[b] = fetch;
                    fetch.Task = Task.Run(() => RunFetchAsync(b, fetch)); // fora do lock
                }
                f.Waiters++;
            }
            try
            {
                await f.Task.WaitAsync(ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException ex) when (ex is not TransferCancelledException && !_e.Token.IsCancellationRequested && tries < 3)
            {
                // outro leitor desistiu e o pedido foi cancelado logo antes de entrarmos: pede de novo
            }
            finally
            {
                bool abandon;
                lock (_lock) abandon = --f.Waiters == 0 && !f.Task.IsCompleted;
                // ninguém mais espera este bloco (Ctrl-C, fechou, parou o segundo plano): cancela o pedido
                if (abandon) TryCancel(f.Cts);
            }
        }
    }

    TransferRef AcquireTransfer()
    {
        lock (_lock)
        {
            if (_transfer == null || _transfer.Transfer.Cancelled)
            {
                _transfer = new TransferRef(_e.Activity.StartTransfer(Name, Size, upload: false));
                _transfer.Transfer.Done = PresentBytesLocked();
            }
            _transfer.Users++;
            return _transfer;
        }
    }

    void ReleaseTransfer(TransferRef t)
    {
        lock (_lock)
        {
            if (--t.Users > 0) return;
            if (_transfer == t) _transfer = null;
        }
        _e.Activity.EndTransfer(t.Transfer);
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "fallocate")]
    static extern int Fallocate(IntPtr fd, int mode, long offset, long length);

    /// <summary>Libera os blocos de disco da faixa sem mudar o tamanho (fallocate PUNCH_HOLE|KEEP_SIZE); melhor esforço.</summary>
    static void PunchHole(SafeFileHandle file, long offset, long length)
    {
        const int keepSize = 1, punchHole = 2;
        var added = false;
        try
        {
            file.DangerousAddRef(ref added);
            Fallocate(file.DangerousGetHandle(), keepSize | punchHole, offset, length);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or ObjectDisposedException) { }
        finally
        {
            if (added) file.DangerousRelease();
        }
    }

    async Task RunFetchAsync(int b, Fetch f)
    {
        var t = AcquireTransfer();
        try
        {
            var drive = _e.GetDrive(DriveId) ?? throw new IOException("Drive não está ativa");
            int gen;
            SafeFileHandle data;
            long baseDone;
            lock (_lock)
            {
                if (_closed) throw new ObjectDisposedException(nameof(StreamSession));
                (gen, data, baseDone) = (_generation, _data, PresentBytesLocked());
            }
            var off = (long)b * BlockSize;
            var len = (int)Math.Min(BlockSize, Size - off);
            // do pool: era um array novo do tamanho do bloco (4 MiB, no LOH) a cada bloco baixado
            var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(len);
            try
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(f.Cts.Token, t.Transfer.Token))
                using (var ms = new MemoryStream(buf, 0, len, writable: true))
                {
                    await drive.DownloadRangeAsync(ItemId, ms, off, len, n => t.Transfer.Done = Math.Min(Size, baseDone + n), linked.Token);
                    if (ms.Position != len) throw new IOException($"Bloco {b} de '{Name}' incompleto: {ms.Position} de {len} bytes");
                }
                // grava e sincroniza antes de marcar: depois de uma queda de energia, bloco marcado tem dados
                try
                {
                    RandomAccess.Write(data, buf.AsSpan(0, len), off);
                    RandomAccess.FlushToDisk(data);
                }
                catch (IOException ex) when (Errors.IsDiskFull(ex))
                {
                    // disco cheio no meio do bloco: o pedaço gravado não serve (o bloco não é marcado) e o LRU não o
                    // enxerga (conta só blocos marcados) — devolve o espaço em vez de deixá-lo preso no esparso
                    PunchHole(data, off, len);
                    throw;
                }
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }
            bool last;
            lock (_lock)
            {
                if (gen != _generation || _closed) return; // blocos descartados enquanto este baixava
                if (_have[b] == 0)
                {
                    _have[b] = 1;
                    _present++;
                    RandomAccess.Write(_map, [1], HeaderSize + b);
                }
                t.Transfer.Done = PresentBytesLocked();
                last = _present == _have.Length;
            }
            drive.MarkOnline();
            if (last) _ = FinishTask(); // verifica e promove fora deste pedido
        }
        catch (OperationCanceledException) when (t.Transfer.Cancelled && !_e.Token.IsCancellationRequested)
        {
            var cancelled = new TransferCancelledException(Name, upload: false);
            bool log;
            CancellationTokenSource? fill;
            lock (_lock)
            {
                log = !t.Logged;
                t.Logged = true;
                _fillCancelledByUser = true; // não recomeça sozinho: o usuário pediu para parar
                fill = _fill;
            }
            TryCancel(fill);
            if (log) _e.Activity.Add(ActivityKind.Info, cancelled.Message);
            throw cancelled;
        }
        finally
        {
            ReleaseTransfer(t);
            lock (_lock)
                if (_fetching.TryGetValue(b, out var cur) && cur == f) _fetching.Remove(b);
        }
    }

    /// <summary>Leitura sequencial: baixa o resto do arquivo em segundo plano, a partir de <paramref name="offset"/>.</summary>
    public void StartBackgroundFill(long offset)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (_fill != null || _closed || _detached || _verified || _fillCancelledByUser || _present == _have.Length) return;
            cts = _fill = CancellationTokenSource.CreateLinkedTokenSource(_e.Token);
        }
        var from = (int)Math.Min(_have.Length - 1, Math.Max(0, offset) / BlockSize);
        _ = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < _have.Length; i++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    await EnsureBlockAsync((from + i) % _have.Length, cts.Token);
                }
                await FinishTask();
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
            catch (Exception ex)
            {
                _e.Activity.Debug($"Download em segundo plano de {Name} parou: {ex.Message}");
            }
            finally
            {
                lock (_lock) if (_fill == cts) _fill = null;
                cts.Dispose();
            }
        });
    }

    /// <summary>
    /// Baixa todos os blocos que faltam, verifica e promove ao cache (open para escrita, truncate, fixar…).
    /// Hash divergente descarta os blocos e tenta de novo até <see cref="SyncEngine.DownloadAttempts"/> vezes.
    /// </summary>
    public async Task CompleteAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var missing = new List<int>();
            lock (_lock)
                for (var b = 0; b < _have.Length; b++) if (_have[b] == 0) missing.Add(b);
            // dois pedidos por vez: o motor já limita os downloads simultâneos da drive
            for (var i = 0; i < missing.Count; i += 2)
                await Task.WhenAll(missing.Skip(i).Take(2).Select(b => EnsureBlockAsync(b, ct)));
            if (await FinishTask().WaitAsync(ct)) return;
            if (Detached) throw new StaleContentException($"'{Name}' mudou na nuvem durante o download.");
            if (attempt >= _e.DownloadAttempts)
                throw new ContentIntegrityException(
                    $"O conteúdo baixado de '{Name}' não confere com a nuvem após {_e.DownloadAttempts} tentativas.");
        }
    }

    /// <summary>Verificação + promoção, uma vez por geração de blocos. true = virou (ou já era) o cache atual.</summary>
    Task<bool> FinishTask()
    {
        lock (_lock)
        {
            if (_verified) return Task.FromResult(true);
            if (_present < _have.Length) return Task.FromResult(false);
            return _finish ??= Task.Run(() =>
            {
                try { return VerifyAndPromote(); }
                catch (Exception ex)
                {
                    _e.Activity.Add(ActivityKind.Error, $"Não foi possível guardar '{Name}' no cache: {ex.Message}");
                    lock (_lock) _finish = null; // tenta de novo na próxima vez
                    return false;
                }
            });
        }
    }

    bool VerifyAndPromote()
    {
        int gen;
        lock (_lock)
        {
            if (_detached) return false;
            gen = _generation;
        }
        var item = _e.Db.Get(Lid);
        var drive = _e.GetDrive(DriveId);
        if (item == null || drive == null) return false;
        if (item.CTag != CTag)
        {
            Detach(); // versão nova na nuvem: o próximo open usa outra sessão
            return false;
        }
        string? problem;
        try
        {
            problem = _e.VerifyDownload(item, drive.Config, DataPath, Size);
        }
        catch (IOException ex) { problem = ex.Message; }
        if (problem != null)
        {
            // descarta antes de avisar: quem reage à mensagem (interface, testes) já encontra os blocos fora
            Discard(gen);
            _e.Activity.Add(ActivityKind.Error, $"O conteúdo baixado de '{Name}' não confere com a nuvem ({problem}); os blocos foram descartados.");
            return false;
        }
        if (!_e.PromoteStream(this, () =>
            {
                // chamado com Db.Lock: o arquivo de trabalho vira o do cache (os fds abertos seguem o mesmo inode)
                lock (_lock)
                {
                    if (gen != _generation || _detached) return false;
                    var dest = _e.Cache.PathFor(DriveId, Lid);
                    File.Move(DataPath, dest, true);
                    _currentPath = dest;
                    _verified = true;
                }
                try { File.Delete(MapPath); } catch (IOException) { }
                return true;
            }))
            return false;
        _e.Activity.Add(ActivityKind.Download, $"Baixado: {Name}");
        return true;
    }

    /// <summary>Descarta todos os blocos (arquivos novos, bitmap zerado); leituras seguintes baixam de novo.</summary>
    void Discard(int gen)
    {
        lock (_lock)
        {
            if (gen != _generation) return;
            _generation++;
            _finish = null;
            if (_closed)
            {
                try { File.Delete(MapPath); } catch (IOException) { }
                try { File.Delete(DataPath); } catch (IOException) { }
                return;
            }
            _data.Dispose();
            _map.Dispose();
            OpenFiles(tryLoad: false);
        }
    }

    /// <summary>Apaga os arquivos de trabalho (item removido ou alterado localmente).</summary>
    internal void DeleteFiles()
    {
        lock (_lock)
        {
            try { File.Delete(MapPath); } catch (IOException) { }
            try { File.Delete(DataPath); } catch (IOException) { }
        }
    }
}
