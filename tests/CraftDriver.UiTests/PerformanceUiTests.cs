using System.Collections.Specialized;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CraftDriver.App.Services;
using CraftDriver.App.ViewModels;
using CraftDriver.Core.Sync;

namespace CraftDriver.UiTests;

// CRAFTDRIVER_HOME é global ao processo: fora da coleção, dois AppHost abriam o mesmo banco ao mesmo tempo
// ("duplicate column name" na migração, visto no CI)
[Collection("host-isolado")]
public class PerformanceUiTests
{
    static AppHost NewHost()
    {
        var home = Path.Combine(Path.GetTempPath(), "craftdriver-performance-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CRAFTDRIVER_HOME", home);
        Directory.CreateDirectory(Path.Combine(home, "config"));
        File.WriteAllText(Path.Combine(home, "config", "config.json"),
            System.Text.Json.JsonSerializer.Serialize(new { MountPoint = home + "/mount", CacheDir = home + "/cache" }));
        return Task.Run(AppHost.CreateAsync).GetAwaiter().GetResult();
    }

    [AvaloniaFact]
    public void Cache_scan_runs_off_ui_is_throttled_and_shared_across_recreated_viewmodels()
    {
        var host = NewHost();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var uiThread = Environment.CurrentManagedThreadId;
        var scanThread = uiThread;
        var now = DateTimeOffset.UtcNow;
        Func<long> read = () =>
        {
            Interlocked.Increment(ref calls);
            scanThread = Environment.CurrentManagedThreadId;
            started.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return 4096;
        };
        MainViewModel Create() => new(host, read, () => now);
        var first = Create();
        MainViewModel? second = null;
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(2)), "background cache reader was not invoked");
            Assert.NotEqual(uiThread, scanThread);
            for (var i = 0; i < 10; i++) first.LibraryAdded();
            ((IDisposable)first).Dispose();
            second = Create();
            Assert.Equal(1, Volatile.Read(ref calls));
            release.Set();
            PumpUntil(() => second.CacheText.Contains("4 KB"));
            Assert.Equal("", first.CacheText);
            for (var i = 0; i < 10; i++) second.LibraryAdded();
            Assert.Equal(1, Volatile.Read(ref calls));
            Dispatcher.UIThread.RunJobs();
            now = now.AddSeconds(31);
            second.LibraryAdded();
            PumpUntil(() => Volatile.Read(ref calls) == 2);
        }
        finally
        {
            release.Set();
            ((IDisposable)first).Dispose();
            second?.Dispose();
        }
    }

    static void PumpUntil(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Assert.True(ready());
    }

    [AvaloniaFact]
    public void Disposing_stops_coordinated_refresh_and_unsubscribes_before_recreation()
    {
        var host = NewHost();
        var vm = new MainViewModel(host);
        var disposable = Assert.IsAssignableFrom<IDisposable>(vm);
        var refreshes = 0;
        vm.Refreshed += () => refreshes++;
        vm.TogglePauseCommand.Execute(null);
        Assert.Equal(1, refreshes);
        host.Activity.Add(ActivityKind.Info, "queued before dispose");
        disposable.Dispose();
        disposable.Dispose();
        host.Config.Accounts.Add(new() { Id = "local", Username = "local@example.test" });
        var accountsChanged = typeof(AppHost).GetField("AccountsChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        ((Action?)accountsChanged!.GetValue(host))?.Invoke();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.Activity);
        Assert.Empty(vm.Accounts);
        Assert.Null(accountsChanged.GetValue(host));
        var activityChanged = typeof(ActivityLog).GetField("Changed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.Null(activityChanged!.GetValue(host.Activity));
        var until = DateTime.UtcNow.AddMilliseconds(1150);
        while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Assert.Equal(1, refreshes);
        using var recreated = (IDisposable)(object)new MainViewModel(host);
        host.Engine.Stop();
    }

    [AvaloniaFact]
    public void Activity_changed_updates_immediately_without_reset_and_preserves_existing_rows()
    {
        var host = NewHost();
        for (var i = 0; i < 205; i++) host.Activity.Add(ActivityKind.Info, "entry " + i);
        var vm = new MainViewModel(host);
        try
        {
            var existing = vm.Activity[0];
            var changes = new List<NotifyCollectionChangedAction>();
            vm.Activity.CollectionChanged += (_, e) => changes.Add(e.Action);
            Task.Run(() => host.Activity.Add(ActivityKind.Error, "new entry")).GetAwaiter().GetResult();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("new entry", vm.Activity[0].Text);
            Assert.True(vm.Activity[0].IsError);
            Assert.Same(existing, vm.Activity[1]);
            Assert.Equal(200, vm.Activity.Count);
            Assert.Equal(new[] { NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add }, changes);
        }
        finally { vm.Dispose(); }
    }
}
