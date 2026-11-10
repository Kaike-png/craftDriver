using Avalonia.Headless.XUnit;
using CraftDriver.App.Services;
using CraftDriver.App.ViewModels;
using CraftDriver.Core.Config;
using CraftDriver.Core.State;
using CraftDriver.Tests;
using Xunit;

namespace CraftDriver.UiTests;

/// <summary>
/// Lista "Arquivos sob demanda": a janela é criada ANTES de Host.Start (App.InitWithHost), com o motor ainda sem
/// drives. O caminho do fixado não pode depender da drive iniciada — vinha vazio a cada abertura do app.
/// </summary>
[Collection("host-isolado")]
public class PinnedPathTests
{
    [AvaloniaFact]
    public void Caminho_dos_fixados_aparece_antes_de_o_motor_iniciar()
    {
        ShellUi.NewHome();
        var host = Task.Run(() => AppHost.CreateAsync()).GetAwaiter().GetResult();
        CraftDriver.App.App.Host = host;
        host.GraphFactory = _ => new FakeGraph();
        host.Notify = (_, _, _) => { };
        host.MountFunc = (_, _) => new TaskCompletionSource().Task;
        host.Config.Accounts.Add(new AccountConfig { Id = "a1", Username = "u@exemplo.com" });
        host.Config.Drives.Add(new DriveConfig { DriveId = FakeGraph.DriveId, AccountId = "a1", DisplayName = "OneDrive - Pessoal" });
        // estado de uma abertura anterior: raiz da drive + pasta, ambas fixadas
        var root = host.Db.Insert(new Item { DriveId = FakeGraph.DriveId, ItemId = "root", ParentLid = 0, Name = "", IsDir = true, Pinned = true });
        host.Db.SaveDrive(new DriveState(FakeGraph.DriveId, root, null, 0, 0));
        host.Db.Insert(new Item { DriveId = FakeGraph.DriveId, ItemId = "p1", ParentLid = root, Name = "Projetos", IsDir = true, Pinned = true });

        var vm = new MainViewModel(host); // como o InitWithHost: antes do Start
        try
        {
            var paths = vm.Pinned.Select(p => p.Path).OrderBy(p => p, StringComparer.Ordinal).ToList();
            var mount = host.Config.MountPoint;
            Assert.Equal([Path.Combine(mount, "OneDrive - Pessoal"), Path.Combine(mount, "OneDrive - Pessoal", "Projetos")], paths);
        }
        finally
        {
            vm.Dispose();
            ShellUi.Wait(Task.Run(host.ShutdownAsync), 20000);
        }
    }
}
