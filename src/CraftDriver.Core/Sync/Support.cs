using System.Collections.Concurrent;
using CraftDriver.Core.Config;

namespace CraftDriver.Core.Sync;

public enum ActivityKind { Info, Upload, Download, Delete, Move, Conflict, Error }

public sealed record ActivityEntry(DateTime Time, ActivityKind Kind, string Text);

public sealed class Transfer
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public bool Upload { get; init; }
    public long Total { get; init; }
    public long Done;
    public double Percent => Total <= 0 ? 0 : Math.Min(100, Done * 100.0 / Total);
}

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
        var dir = Path.Combine(_root, DriveKey(driveId));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, lid.ToString());
    }

    public string NewTempFile() => Path.Combine(TempDir, Guid.NewGuid().ToString("N"));

    public bool Exists(string driveId, long lid) => File.Exists(PathFor(driveId, lid));

    public void Delete(string driveId, long lid)
    {
        try { File.Delete(PathFor(driveId, lid)); } catch (IOException) { }
    }

    public void DeleteDrive(string driveId)
    {
        try { Directory.Delete(Path.Combine(_root, DriveKey(driveId)), true); } catch (DirectoryNotFoundException) { }
    }

    public long TotalBytes()
    {
        long total = 0;
        foreach (var f in new DirectoryInfo(_root).EnumerateFiles("*", SearchOption.AllDirectories))
            total += f.Length;
        return total;
    }
}
