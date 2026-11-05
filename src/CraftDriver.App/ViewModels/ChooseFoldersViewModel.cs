using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CraftDriver.Core.State;
using CraftDriver.Core.Sync;

namespace CraftDriver.App.ViewModels;

public sealed partial class FolderChoiceVm(long lid, string path, bool sync) : ViewModelBase
{
    public long Lid { get; } = lid;
    /// <summary>Caminho dentro da conta/biblioteca, ex. "/Fotos/2019".</summary>
    public string Path { get; } = path;
    public bool Original { get; } = sync;
    [ObservableProperty] public partial bool Sync { get; set; } = sync;
}

/// <summary>
/// "Escolher pastas" (sincronização seletiva) de uma drive: as pastas do primeiro nível e as que já estão
/// excluídas em qualquer nível. Desmarcar = a pasta some da montagem (continua na nuvem).
/// </summary>
public sealed partial class ChooseFoldersViewModel : ViewModelBase
{
    readonly SyncEngine _engine;
    public string DriveName { get; }
    public ObservableCollection<FolderChoiceVm> Folders { get; } = [];
    [ObservableProperty] public partial string Message { get; set; } = "";
    public event Action? CloseRequested;

    public ChooseFoldersViewModel(SyncEngine engine, string driveId)
    {
        _engine = engine;
        var drive = engine.GetDrive(driveId) ?? throw new ArgumentException("drive não está ativa", nameof(driveId));
        DriveName = drive.Config.DisplayName;
        var db = engine.Db;
        var excluded = db.ExcludedFolders(driveId);
        var excludedIds = excluded.Select(x => x.Lid).ToHashSet();
        var rows = db.GetChildren(drive.RootLid).Where(c => c.IsDir)
            .Select(c => new FolderChoiceVm(c.Lid, "/" + c.Name, !excludedIds.Contains(c.Lid)))
            .Concat(excluded.Where(x => x.ParentLid != drive.RootLid)
                .Select(x => new FolderChoiceVm(x.Lid, RelativePath(db, drive.RootLid, x), false)))
            .OrderBy(f => f.Path, StringComparer.CurrentCultureIgnoreCase);
        foreach (var r in rows) Folders.Add(r);
    }

    static string RelativePath(StateDb db, long rootLid, Item item)
    {
        var parts = new List<string>();
        for (var cur = item; cur != null && cur.Lid != rootLid; cur = db.Get(cur.ParentLid)) parts.Add(cur.Name);
        parts.Reverse();
        return "/" + string.Join('/', parts);
    }

    /// <summary>Aplica só o que mudou. Recusas (ex.: alterações não enviadas) ficam marcadas como antes e aparecem em Message.</summary>
    [RelayCommand]
    void Save()
    {
        var errors = new List<string>();
        foreach (var f in Folders.Where(f => f.Sync != f.Original).ToList())
        {
            if (_engine.SetExcluded(f.Lid, !f.Sync) is { } error)
            {
                errors.Add($"{f.Path}: {error}");
                f.Sync = f.Original;
            }
        }
        if (errors.Count == 0) CloseRequested?.Invoke();
        else Message = string.Join("\n", errors);
    }

    [RelayCommand] void Cancel() => CloseRequested?.Invoke();
}
