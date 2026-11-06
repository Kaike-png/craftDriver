using System.Collections.Concurrent;
using CraftDriver.Core.Config;

namespace CraftDriver.Core.Sync;

public enum ActivityKind { Info, Upload, Download, Delete, Move, Conflict, Error }

public sealed record ActivityEntry(DateTime Time, ActivityKind Kind, string Text);

public sealed class Transfer(Func<long>? clockMs = null)
{
    /// <summary>Velocidade medida só nos últimos segundos, para acompanhar mudanças de ritmo.</summary>
    const long WindowMs = 5000;

    readonly Func<long> _now = clockMs ?? (() => Environment.TickCount64);
    readonly CancellationTokenSource _cts = new();
    readonly Queue<(long at, long bytes)> _samples = new();
    long _done;

    public long Id { get; init; }
    public string Name { get; init; } = "";
    public bool Upload { get; init; }
    public long Total { get; init; }

    /// <summary>Bytes já transferidos. Atribuições alimentam a medição de velocidade.</summary>
    public long Done
    {
        get => Interlocked.Read(ref _done);
        set
        {
            Interlocked.Exchange(ref _done, value);
            lock (_samples)
            {
                var now = _now();
                _samples.Enqueue((now, value));
                while (_samples.Count > 2 && now - _samples.Peek().at > WindowMs) _samples.Dequeue();
            }
        }
    }

    public double Percent => Total <= 0 ? 0 : Math.Min(100, Done * 100.0 / Total);

    /// <summary>Bytes por segundo na janela recente; 0 sem medida ou se parou há mais que a janela.</summary>
    public double BytesPerSecond
    {
        get
        {
            lock (_samples)
            {
                if (_samples.Count < 2) return 0;
                var now = _now();
                var last = _samples.Last();
                if (now - last.at > WindowMs) return 0;
                var first = _samples.Peek();
                var ms = last.at - first.at;
                return ms <= 0 ? 0 : (last.bytes - first.bytes) * 1000.0 / ms;
            }
        }
    }

    /// <summary>Tempo restante estimado pela velocidade recente; null sem estimativa.</summary>
    public TimeSpan? Remaining
    {
        get
        {
            var rate = BytesPerSecond;
            if (rate <= 0 || Total <= 0) return null;
            return TimeSpan.FromSeconds(Math.Round(Math.Max(0, Total - Done) / rate));
        }
    }

    /// <summary>Cancelamento pedido pelo usuário (só esta transferência).</summary>
    public CancellationToken Token => _cts.Token;
    public bool Cancelled => _cts.IsCancellationRequested;
    internal void Cancel() => _cts.Cancel();
}

/// <summary>O usuário cancelou a transferência (botão na janela).</summary>
public sealed class TransferCancelledException(string name, bool upload)
    : OperationCanceledException(upload ? $"Envio de '{name}' cancelado por você" : $"Download de '{name}' cancelado por você");

/// <summary>Registro de atividade (para a interface) + log em arquivo.</summary>
public sealed class ActivityLog
{
    readonly ConcurrentQueue<ActivityEntry> _entries = new();
    readonly ConcurrentDictionary<long, Transfer> _transfers = new();
    long _nextTransfer;
    readonly object _fileLock = new();
    readonly string? _logFile;

    public event Action? Changed;

    public ActivityLog(string? logFile = null)
    {
        _logFile = logFile;
        if (_logFile != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!);
            try
            {
                if (File.Exists(_logFile) && new FileInfo(_logFile).Length > 5_000_000)
                    File.Move(_logFile, _logFile + ".1", true);
            }
            catch { }
        }
    }

    public IReadOnlyList<ActivityEntry> Entries => _entries.Reverse().ToList();
    public IReadOnlyList<Transfer> Transfers => _transfers.Values.OrderBy(t => t.Id).ToList();

    public void Add(ActivityKind kind, string text)
    {
        var e = new ActivityEntry(DateTime.Now, kind, text);
        _entries.Enqueue(e);
        while (_entries.Count > 500 && _entries.TryDequeue(out _)) { }
        Log(kind.ToString().ToUpperInvariant(), text);
        Changed?.Invoke();
    }

    public void Log(string level, string text)
    {
        if (_logFile == null) return;
        lock (_fileLock)
        {
            try { File.AppendAllText(_logFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {text}\n"); }
            catch { }
        }
    }

    public void Debug(string text) => Log("DEBUG", text);

    public Transfer StartTransfer(string name, long total, bool upload)
    {
        var t = new Transfer { Id = Interlocked.Increment(ref _nextTransfer), Name = name, Total = total, Upload = upload };
        _transfers[t.Id] = t;
        Changed?.Invoke();
        return t;
    }

    /// <summary>Cancela uma transferência em andamento; false se ela já terminou.</summary>
    public bool CancelTransfer(long id)
    {
        if (!_transfers.TryGetValue(id, out var t)) return false;
        t.Cancel();
        return true;
    }

    public void EndTransfer(Transfer t)
    {
        _transfers.TryRemove(t.Id, out _);
        Changed?.Invoke();
    }
}

/// <summary>Regras de nomes do OneDrive/SharePoint e arquivos temporários que ficam só no Linux.</summary>
public sealed class NamePolicy(AppConfig cfg)
{
    static readonly char[] Invalid = ['"', '*', ':', '<', '>', '?', '\\', '|'];
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lock", "CON", "PRN", "AUX", "NUL", "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "desktop.ini", "_vti_",
    };

    /// <summary>true se o nome não pode ir para a nuvem.</summary>
    public bool IsLocalOnly(string name) => !IsValidRemoteName(name) || MatchesTempPattern(name);

    public static bool IsValidRemoteName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 255) return false;
        if (name.IndexOfAny(Invalid) >= 0) return false;
        if (name.EndsWith('.') || name.EndsWith(' ') || name.StartsWith(' ')) return false;
        if (name.Any(c => c < 32)) return false;
        var stem = name.Contains('.') ? name[..name.IndexOf('.')] : name;
        if (Reserved.Contains(name) || Reserved.Contains(stem)) return false;
        if (name.Contains("_vti_", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public bool MatchesTempPattern(string name)
    {
        foreach (var p in cfg.LocalOnlyPatterns)
            if (Glob(p, name)) return true;
        return false;
    }

    public static bool Glob(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t])) { p++; t++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
            else if (star >= 0) { p = star + 1; t = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    /// <summary>"relatorio.docx" → "relatorio (conflito - host 2026-11-04 1530).docx"</summary>
    public static string ConflictName(string name)
    {
        var ext = Path.GetExtension(name);
        var stem = string.IsNullOrEmpty(ext) ? name : name[..^ext.Length];
        return $"{stem} (conflito - {Environment.MachineName} {DateTime.Now:yyyy-MM-dd HHmmss}){ext}";
    }
}

/// <summary>Conteúdo dos arquivos baixados/editados: {cache}/content/{hash da drive}/{lid}.</summary>
public sealed class ContentCache
{
    readonly string _root;
    readonly object _directoriesLock = new();
    readonly Dictionary<string, string> _directories = new(StringComparer.Ordinal);

    public ContentCache(string cacheDir)
    {
        _root = Path.Combine(cacheDir, "content");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(TempDir);
    }

    public string TempDir => Path.Combine(_root, ".tmp");

    static string DriveKey(string driveId) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(driveId)))[..16].ToLowerInvariant();

    public string PathFor(string driveId, long lid)
    {
        lock (_directoriesLock)
        {
            if (!_directories.TryGetValue(driveId, out var dir))
            {
                dir = Path.Combine(_root, DriveKey(driveId));
                Directory.CreateDirectory(dir);
                _directories.Add(driveId, dir);
            }
            return Path.Combine(dir, lid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    public string NewTempFile() => Path.Combine(TempDir, Guid.NewGuid().ToString("N"));

    /// <summary>Downloads parciais (retomados com Range): um por item e versão (cTag).</summary>
    public string PartialDir => Path.Combine(_root, ".partial");

    /// <summary>Cópias congeladas de arquivos em envio (retomadas pela sessão de upload após reiniciar).</summary>
    public string UploadsDir => Path.Combine(_root, ".uploads");

    static string Short(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..12].ToLowerInvariant();

    public string PartialPathFor(string driveId, long lid, string? ctag)
    {
        Directory.CreateDirectory(PartialDir);
        return Path.Combine(PartialDir, $"{DriveKey(driveId)}-{lid}-{Short(ctag ?? "")}");
    }

    /// <summary>
    /// Apaga downloads parciais de outras versões do item (a nuvem mudou desde então). Os arquivos da leitura por
    /// faixas da mesma versão (<c>{keep}.stream</c>/<c>{keep}.blocks</c>) ficam.
    /// </summary>
    public void DeleteOtherPartials(string driveId, long lid, string keep)
    {
        if (!Directory.Exists(PartialDir)) return;
        foreach (var f in Directory.EnumerateFiles(PartialDir, $"{DriveKey(driveId)}-{lid}-*"))
            if (!f.StartsWith(keep, StringComparison.Ordinal)) try { File.Delete(f); } catch (IOException) { }
    }

    public string NewUploadSnapshot()
    {
        Directory.CreateDirectory(UploadsDir);
        return Path.Combine(UploadsDir, Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// Limpeza ao iniciar: temporários de execuções anteriores e cópias de envio que nenhuma operação usa mais.
    /// Downloads parciais ficam (são retomados).
    /// </summary>
    public void CleanStale(IEnumerable<string> liveSnapshots, Func<long, string?>? driveOfLid = null)
    {
        if (driveOfLid != null) DeleteOrphanPartials(driveOfLid);
        foreach (var f in Directory.EnumerateFiles(TempDir)) try { File.Delete(f); } catch (IOException) { }
        if (!Directory.Exists(UploadsDir)) return;
        var keep = new HashSet<string>(liveSnapshots);
        foreach (var f in Directory.EnumerateFiles(UploadsDir))
            if (!keep.Contains(f)) try { File.Delete(f); } catch (IOException) { }
    }

    public bool Exists(string driveId, long lid) => File.Exists(PathFor(driveId, lid));

    /// <summary>Apaga o conteúdo do item e os downloads parciais dele (<c>.partial</c>, <c>.stream</c>, <c>.blocks</c>, de qualquer versão).</summary>
    public void Delete(string driveId, long lid)
    {
        try { File.Delete(PathFor(driveId, lid)); } catch (IOException) { }
        DeletePartials($"{DriveKey(driveId)}-{lid}-*");
    }

    public void DeleteDrive(string driveId)
    {
        lock (_directoriesLock)
        {
            _directories.Remove(driveId, out var dir);
            try { Directory.Delete(dir ?? Path.Combine(_root, DriveKey(driveId)), true); }
            catch (DirectoryNotFoundException) { }
        }
        DeletePartials($"{DriveKey(driveId)}-*");
    }

    void DeletePartials(string pattern)
    {
        if (!Directory.Exists(PartialDir)) return;
        foreach (var f in Directory.EnumerateFiles(PartialDir, pattern))
            try { File.Delete(f); } catch (IOException) { }
    }

    /// <summary>
    /// Apaga downloads parciais cujo item não existe mais (excluído no meio do download, drive removida, banco
    /// recriado). Eles contavam no limite do cache sem que o LRU pudesse despejá-los: com órfãos perto do limite,
    /// o LRU apagava todo o cache não fixado a cada passada. <paramref name="driveOfLid"/> devolve a drive do item
    /// (null = não existe). Devolve os bytes liberados.
    /// </summary>
    public long DeleteOrphanPartials(Func<long, string?> driveOfLid)
    {
        if (!Directory.Exists(PartialDir)) return 0;
        long freed = 0;
        foreach (var f in new DirectoryInfo(PartialDir).EnumerateFiles())
        {
            // {chave da drive}-{lid}-{versão}[.stream|.blocks]
            var parts = f.Name.Split('-', 3);
            if (parts.Length == 3 && long.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var lid)
                && driveOfLid(lid) is { } drive && DriveKey(drive) == parts[0]) continue;
            try
            {
                var len = f.Name.EndsWith(StreamSession.DataSuffix, StringComparison.Ordinal) ? StreamSession.StoredBytes(f.FullName) : f.Length;
                f.Delete();
                freed += len;
            }
            catch (IOException) { }
        }
        return freed;
    }

    /// <summary>
    /// Cópias congeladas de envio que nenhuma operação usa mais (operação removida porque o item foi apagado ou
    /// substituído). A lista de vivas é lida DEPOIS de enumerar os arquivos: o envio grava o caminho na operação antes
    /// de criar a cópia, então uma cópia nova nunca parece órfã.
    /// </summary>
    public void DeleteOrphanUploads(Func<IEnumerable<string>> liveSnapshots)
    {
        if (!Directory.Exists(UploadsDir)) return;
        var files = Directory.EnumerateFiles(UploadsDir).ToList();
        if (files.Count == 0) return;
        var keep = new HashSet<string>(liveSnapshots());
        foreach (var f in files)
            if (!keep.Contains(f)) try { File.Delete(f); } catch (IOException) { }
    }

    /// <summary>Varredura fora da thread chamadora (UI). O resultado inclui parciais e snapshots, sem total obsoleto.</summary>
    public Task<long> TotalBytesAsync(CancellationToken ct = default) => Task.Run(() => ScanBytes(ct), ct);

    public long TotalBytes() => ScanBytes(CancellationToken.None);

    long ScanBytes(CancellationToken ct)
    {
        long total = 0;
        foreach (var f in new DirectoryInfo(_root).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            // arquivo esparso da leitura por faixas: conta só os blocos já baixados, não o tamanho aparente
            try { total += f.Name.EndsWith(StreamSession.DataSuffix, StringComparison.Ordinal) ? StreamSession.StoredBytes(f.FullName) : f.Length; }
            catch (FileNotFoundException) { } // upload/download terminou durante a varredura
        }
        return total;
    }
}
