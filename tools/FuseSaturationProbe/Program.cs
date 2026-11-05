using CraftDriver.Tests;
using CraftDriver.Core.Config;
using CraftDriver.Core.Graph;
using CraftDriver.Core.State;
using CraftDriver.Core.Sync;
using CraftDriver.Core.Vfs;
using CraftDriver.Fuse;
using System.Diagnostics;

var dir=Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(),"fuse-saturation-"+Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir);
var remote = new FakeGraph();
for(int i=0;i<16;i++) remote.RemoteCreate("/",$"f{i}.txt","content");
var api=new SlowGraph(remote);
var config =new AppConfig {CacheDir=Path.Combine(dir,"cache"),Accounts=[new(){Id="a"}],Drives=[new(){DriveId=FakeGraph.DriveId,AccountId="a",DisplayName="Drive"}],PollIntervalSeconds=300};
using var db=new StateDb(Path.Combine(dir,"state.db"));
using var engine=new SyncEngine(config,db,new ContentCache(config.CacheDir),new ActivityLog(),_=>api);
var vfs=new VirtualFs(engine);
engine.Start();
Harness.WaitUntil(()=>engine.GetDrive(FakeGraph.DriveId)?.LastSync!=null,"sync");
var loop=FuseHost.MountAsync(vfs,Path.Combine(dir,"mount"),16);
try {
 var root=Path.Combine(dir,"mount","Drive");
 Harness.WaitUntil(()=>Directory.Exists(root),"mount");
 using var process=Process.Start(new ProcessStartInfo("python3"){RedirectStandardOutput=true,RedirectStandardError=true,ArgumentList={"-c","import concurrent.futures,os,time,sys\npool=concurrent.futures.ThreadPoolExecutor(max_workers=17)\nfs=[pool.submit(lambda p:open(p).read(),sys.argv[1]+'/f'+str(i)+'.txt') for i in range(16)]\ntime.sleep(2)\nt=time.monotonic()\ntry: os.stat(sys.argv[1]+'/missing')\nexcept FileNotFoundError: pass\nprint('stat_seconds='+str(time.monotonic()-t),flush=True)",root}})!;
 var deadline=DateTime.UtcNow.AddSeconds(6);
 while(Volatile.Read(ref api.Entered)<16&&DateTime.UtcNow<deadline) await Task.Delay(50);
 Console.WriteLine($"downloads_entered={api.Entered}; child_still_blocked={!process.HasExited}");
 await Task.Delay(4000);
 api.Release.TrySetResult();
 await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
 Console.WriteLine(await process.StandardOutput.ReadToEndAsync());
 Console.WriteLine("python_exit="+process.ExitCode+" stderr="+await process.StandardError.ReadToEndAsync());
} finally {api.Release.TrySetResult();FuseHost.Unmount();await loop.WaitAsync(TimeSpan.FromSeconds(15));engine.Stop();}

class SlowGraph(FakeGraph inner):IGraphApi {
 public int Entered;
 public TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
 public Task<GraphDrive> GetDriveAsync(string d,CancellationToken c)=>inner.GetDriveAsync(d,c);
 public Task<RemoteItem> GetRootAsync(string d,CancellationToken c)=>inner.GetRootAsync(d,c);
 public Task<RemoteItem?> GetItemAsync(string d,string i,CancellationToken c)=>inner.GetItemAsync(d,i,c);
 public Task<RemoteItem?> GetChildByNameAsync(string d,string p,string n,CancellationToken c)=>inner.GetChildByNameAsync(d,p,n,c);
 public Task<DeltaPage> GetDeltaAsync(string d,string? l,CancellationToken c)=>inner.GetDeltaAsync(d,l,c);
 public async Task DownloadAsync(string d,string i,Stream s,long o,Action<long>? p,CancellationToken c){Interlocked.Increment(ref Entered);await Release.Task.WaitAsync(c);await inner.DownloadAsync(d,i,s,o,p,c);}
 public Task<RemoteItem> UploadAsync(string d,string p,string n,string? i,string? e,string f,DateTimeOffset m,Action<long>? a,CancellationToken c,IUploadSessionStore? store=null)=>inner.UploadAsync(d,p,n,i,e,f,m,a,c,store);
 public Task<RemoteItem> CreateFolderAsync(string d,string p,string n,CancellationToken c)=>inner.CreateFolderAsync(d,p,n,c);
 public Task DeleteAsync(string d,string i,CancellationToken c)=>inner.DeleteAsync(d,i,c);
 public Task<RemoteItem> MoveAsync(string d,string i,string p,string n,CancellationToken c)=>inner.MoveAsync(d,i,p,n,c);
 public Task<RemoteItem> SetModifiedAsync(string d,string i,DateTimeOffset m,CancellationToken c)=>inner.SetModifiedAsync(d,i,m,c);
}
