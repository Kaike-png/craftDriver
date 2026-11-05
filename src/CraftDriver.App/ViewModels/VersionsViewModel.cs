using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CraftDriver.App.Services;
using CraftDriver.Core.Graph;
using CraftDriver.Core.State;
using CraftDriver.Core.Sync;

namespace CraftDriver.App.ViewModels;

public sealed class VersionRowVm(DriveItemVersion version, bool current)
{
    public DriveItemVersion Version { get; } = version;
    public bool IsCurrent { get; } = current;
    public string Label => IsCurrent ? "Atual" : "Versão " + Version.Id;
    public string When => Version.Modified.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
    public string Size => Fmt.Bytes(Version.Size);
    public string Author => Version.ModifiedBy ?? "—";
}

/// <summary>Janela "Versões" de um arquivo: lista da nuvem, restaurar uma versão ou baixar como cópia ao lado.</summary>
public sealed partial class VersionsViewModel : ViewModelBase
{
    readonly AppHost _host;
    public long Lid { get; }
    public string FileName { get; }
    public string FilePath { get; }
    public ObservableCollection<VersionRowVm> Versions { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand), nameof(DownloadCopyCommand))]
    public partial VersionRowVm? Selected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand), nameof(DownloadCopyCommand), nameof(ReloadCommand))]
    public partial bool Busy { get; set; }

    [ObservableProperty] public partial string Status { get; set; } = "";

    public VersionsViewModel(AppHost host, long lid)
    {
        _host = host;
        Lid = lid;
        var item = host.Db.Get(lid);
        FileName = item?.Name ?? "(arquivo removido)";
        FilePath = item != null ? host.LocalPathOf(item) : "";
    }

    async Task Run(string busy, Func<Task<string?>> action)
    {
        Busy = true;
        Status = busy;
        try { Status = await action() ?? ""; }
        catch (ItemActionException e) { Status = e.Message; }
        catch (Exception e) { Status = "Erro: " + e.Message; }
        finally { Busy = false; }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    public Task ReloadAsync() => Run("Carregando versões…", async () =>
    {
        var list = await _host.Actions.ListVersionsAsync(Lid, _host.Engine.Token);
        Versions.Clear();
        for (var i = 0; i < list.Count; i++) Versions.Add(new VersionRowVm(list[i], i == 0));
        Selected = Versions.FirstOrDefault(v => !v.IsCurrent);
        return list.Count switch
        {
            0 => "Nenhuma versão guardada na nuvem para este arquivo.",
            1 => "Só existe a versão atual.",
            _ => $"{list.Count} versões (a mais nova primeiro). Restaurar cria uma versão nova; nenhuma é apagada.",
        };
    });

    bool NotBusy() => !Busy;
    bool CanRestore() => !Busy && Selected is { IsCurrent: false };
    bool CanCopy() => !Busy && Selected != null;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    Task RestoreAsync() => Run("Restaurando…", async () =>
    {
        var v = Selected!.Version;
        await _host.Actions.RestoreVersionAsync(Lid, v.Id, _host.Engine.Token);
        var done = $"Versão de {v.Modified.ToLocalTime():dd/MM/yyyy HH:mm} restaurada como a atual.";
        await ReloadListQuietly();
        return done;
    });

    [RelayCommand(CanExecute = nameof(CanCopy))]
    Task DownloadCopyAsync() => Run("Baixando a versão…", async () =>
    {
        var name = await _host.Actions.DownloadVersionCopyAsync(Lid, Selected!.Version, _host.Engine.Token);
        return $"Salvo como \"{name}\" na mesma pasta (o original não foi alterado).";
    });

    async Task ReloadListQuietly()
    {
        try
        {
            var list = await _host.Actions.ListVersionsAsync(Lid, _host.Engine.Token);
            Versions.Clear();
            for (var i = 0; i < list.Count; i++) Versions.Add(new VersionRowVm(list[i], i == 0));
            Selected = null;
        }
        catch (ItemActionException) { /* a mensagem de sucesso fica */ }
    }
}

public sealed class TrashRowVm(TrashRecord rec, string driveName, bool canRestore, string browserUrl)
{
    public TrashRecord Record { get; } = rec;
    public string Name => Record.Name + (Record.IsDir ? "/" : "");
    public string Drive { get; } = driveName;
    public string When => DateTimeOffset.FromUnixTimeMilliseconds(Record.At).ToLocalTime().ToString("dd/MM/yyyy HH:mm");
    public bool CanRestore { get; } = canRestore;
    public string BrowserUrl { get; } = browserUrl;
    public string How => CanRestore ? "" : "restaurar pelo navegador";
}

/// <summary>Janela "Lixeira": excluídos que o craftDriver viu; restaura pela API no OneDrive pessoal, senão abre o navegador.</summary>
public sealed partial class TrashViewModel(AppHost host) : ViewModelBase
{
    public ObservableCollection<TrashRowVm> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand), nameof(OpenInBrowserCommand))]
    public partial TrashRowVm? Selected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    public partial bool Busy { get; set; }

    [ObservableProperty] public partial string Status { get; set; } = "";

    public void Reload()
    {
        Items.Clear();
        foreach (var t in host.Db.Trash())
        {
            var d = host.Engine.GetDrive(t.DriveId)?.Config;
            if (d == null) continue;
            Items.Add(new TrashRowVm(t, d.DisplayName, ItemActions.CanRestoreFromTrash(d), ItemActions.RecycleBinUrl(d)));
        }
        Status = Items.Count == 0
            ? "Nenhum item excluído recentemente. A lista mostra o que o craftDriver viu ser excluído (aqui ou em outro dispositivo)."
            : Items.Any(i => !i.CanRestore)
                ? "Contas corporativas e do SharePoint só restauram pela Lixeira no navegador (a API ainda é beta)."
                : "";
    }

    bool CanRestore() => !Busy && Selected is { CanRestore: true };

    [RelayCommand(CanExecute = nameof(CanRestore))]
    async Task RestoreAsync()
    {
        var row = Selected!;
        Busy = true;
        try
        {
            var name = await host.Actions.RestoreFromTrashAsync(row.Record.Id, host.Engine.Token);
            Reload();
            Status = $"\"{name}\" restaurado; ele volta para a pasta na próxima sincronização.";
        }
        catch (ItemActionException e)
        {
            Reload();
            Status = e.Message;
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    void OpenInBrowser()
    {
        List<string> urls = Selected != null
            ? [Selected.BrowserUrl]
            : host.Engine.Drives.Select(d => ItemActions.RecycleBinUrl(d.Config)).Distinct().ToList();
        foreach (var u in urls) host.OpenUrl(u);
        Status = urls.Count == 0 ? "Nenhuma conta conectada." : "Abrindo no navegador: " + string.Join(' ', urls);
    }
}

public static class VersionsWindowConverters
{
    /// <summary>Versão atual em negrito.</summary>
    public static readonly Avalonia.Data.Converters.IValueConverter Bold =
        new Avalonia.Data.Converters.FuncValueConverter<bool, Avalonia.Media.FontWeight>(b => b ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal);
}
