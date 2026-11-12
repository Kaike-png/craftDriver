using System.Diagnostics;
using System.Reflection;
using CraftDriver.Core.State;
using CraftDriver.Fuse;
using Microsoft.Data.Sqlite;

namespace CraftDriver.Tests;

/// <summary>
/// PENDENCIAS.md §16: MEDIÇÕES (não asserções). Explicit: não rodam na suíte; rodar com
/// <c>dotnet exec CraftDriver.Tests.dll -class CraftDriver.Tests.MedicoesDesempenhoTests -explicit only</c>.
/// Os números vão para o ITestOutputHelper e para $TMPDIR/an14-medicoes.txt.
/// </summary>
[Collection("fuse")]
public class MedicoesDesempenhoTests(ITestOutputHelper output)
{
    static readonly string Arquivo = Path.Combine(Path.GetTempPath(), "an14-medicoes.txt");

    void Out(string s)
    {
        output.WriteLine(s);
        File.AppendAllText(Arquivo, s + "\n");
    }

    /// <summary>Conta SELECT/WITH na conexão principal de TODAS as threads (os handlers do FUSE rodam no loop).</summary>
    sealed class Sql : IDisposable
    {
        readonly SqliteConnection _c;
        int _n;
        public int Count => Volatile.Read(ref _n);
        public Sql(StateDb db)
        {
            _c = (SqliteConnection)typeof(StateDb).GetField("_c", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(db)!;
            SQLitePCL.raw.sqlite3_trace(_c.Handle, (_, sql) =>
            {
                var t = sql.TrimStart();
                if (t.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || t.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
                    Interlocked.Increment(ref _n);
            }, null);
        }
        public void Reset() => Volatile.Write(ref _n, 0);
        public void Dispose() => SQLitePCL.raw.sqlite3_trace(_c.Handle, (SQLitePCL.strdelegate_trace)null!, null);
    }

    static (int code, string output) Sh(string script, string cwd)
    {
        var psi = new ProcessStartInfo("bash", ["-c", script]) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, (o + err.Result).Trim());
    }

    static string Py(string code, string cwd)
    {
        var psi = new ProcessStartInfo("/usr/bin/python3", ["-c", code]) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (o + err.Result).Trim();
    }

    /// <summary>Pasta com <paramref name="n"/> arquivos direto no banco (sem nuvem); <paramref name="cached"/> = conteúdo no cache.</summary>
    static long Pasta(Harness h, long parent, string name, int n, bool cached = false, string prefix = "f")
    {
        long dir = 0;
        h.Db.InTransaction(() =>
        {
            dir = h.Db.Insert(new Item { DriveId = FakeGraph.DriveId, ParentLid = parent, Name = name, IsDir = true, ItemId = "dir-" + name + Guid.NewGuid().ToString("N")[..6], MTime = 1_700_000_000_000 });
            for (var i = 0; i < n; i++)
            {
                var it = new Item
                {
                    DriveId = FakeGraph.DriveId, ParentLid = dir, Name = $"{prefix}{i:D5}.txt", Size = 10, MTime = 1_700_000_000_000,
                    ItemId = $"{name}-{i}-{Guid.NewGuid():N}", CTag = "c1", CachedCTag = cached ? "c1" : null, LastAccess = i,
                };
                h.Db.Insert(it);
                if (cached) File.WriteAllBytes(h.Engine.Cache.PathFor(it.DriveId, it.Lid), new byte[1024]);
            }
        });
        return dir;
    }

    static async Task Montado(Harness h, Action<string> body)
    {
        var mnt = Path.Combine(h.Dir, "mnt");
        var loop = FuseHost.MountAsync(h.Fs, mnt);
        try
        {
            var root = Path.Combine(mnt, Harness.Name);
            Harness.WaitUntil(() => Directory.Exists(root), "montagem visível");
            body(root);
        }
        finally
        {
            h.Engine.Stop();
            FuseHost.Unmount();
            await loop.WithinAsync(TimeSpan.FromSeconds(15));
        }
    }

    [Fact(Explicit = true)]
    public async Task M16_ls_l_de_pasta_com_10k_entradas()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        Pasta(h, h.Drive.RootLid, "online", 10_000);
        Pasta(h, h.Drive.RootLid, "cache", 10_000, cached: true);
        await Montado(h, root =>
        {
            using var q = new Sql(h.Db);
            foreach (var dir in new[] { "online", "cache" })
            {
                Thread.Sleep(1200);
                q.Reset();
                var sw = Stopwatch.StartNew();
                var (c, o) = Sh($"ls -l {dir} | wc -l", root);
                var t1 = sw.ElapsedMilliseconds;
                var s1 = q.Count;
                q.Reset();
                sw.Restart();
                Sh($"ls -l {dir} | wc -l", root); // dentro do attr/entry timeout (1 s)
                var t2 = sw.ElapsedMilliseconds;
                Out($"[16 ls -l] {dir}: 10k entradas, 1º ls -l {t1} ms / {s1} SELECTs ({s1 / 10_000.0:F2}/entrada); 2º imediato {t2} ms / {q.Count} SELECTs (linhas={o}, rc={c})");
                q.Reset();
                sw.Restart();
                Sh($"ls -f {dir} | wc -l", root);
                Out($"[16 ls -f] {dir}: {sw.ElapsedMilliseconds} ms / {q.Count} SELECTs");
                foreach (var (nome, op) in new[] { ("lstat", "os.lstat(p)"), ("listxattr", "os.listxattr(p, follow_symlinks=False)"),
                             ("getxattr system.posix_acl_access", "os.getxattr(p, 'system.posix_acl_access', follow_symlinks=False)"),
                             ("getxattr security.selinux", "os.getxattr(p, 'security.selinux', follow_symlinks=False)") })
                {
                    Thread.Sleep(1200);
                    Sh($"ls -f {dir} >/dev/null", root);
                    q.Reset();
                    var r = Py($"import os\nfs = os.listdir('{dir}')\nfor f in fs:\n    p = '{dir}/' + f\n    try: {op}\n    except OSError as e: err = e.errno\nprint(len(fs))", root);
                    Out($"[16 ls -l parcela] {dir}: {nome} ×{r}: {q.Count} SELECTs");
                }
            }
        });
    }

    /// <summary>O cache caminho→lid do Resolve é limpo ao passar de 4096 (VirtualFs.cs:128): pasta maior refaz a descida.</summary>
    [Fact(Explicit = true)]
    public async Task M16_ls_l_abaixo_e_acima_do_limite_do_cache_de_caminhos()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        Pasta(h, h.Drive.RootLid, "p3k", 3000);
        Pasta(h, h.Drive.RootLid, "p10k", 10_000);
        await Montado(h, root =>
        {
            using var q = new Sql(h.Db);
            foreach (var (dir, n) in new[] { ("p3k", 3000), ("p10k", 10_000) })
            {
                Sh($"ls -l {dir} >/dev/null", root);
                Thread.Sleep(1200);
                q.Reset();
                var sw = Stopwatch.StartNew();
                Sh($"ls -l {dir} >/dev/null", root);
                Out($"[16 ls -l repetido após 1,2 s] {dir}: {sw.ElapsedMilliseconds} ms, {q.Count} SELECTs ({(double)q.Count / n:F2}/entrada, {(double)sw.ElapsedMilliseconds * 1000 / n:F0} µs/entrada)");
            }
        });
    }

    [Fact(Explicit = true)]
    public async Task M16_xattr_padrao_Dolphin_1000_arquivos()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        Pasta(h, h.Drive.RootLid, "fotos", 1000);
        Pasta(h, h.Drive.RootLid, "fotosCache", 1000, cached: true);
        await Montado(h, root =>
        {
            using var q = new Sql(h.Db);
            foreach (var dir in new[] { "fotos", "fotosCache" })
            {
                Sh($"ls -l {dir} >/dev/null", root);
                var py = $$"""
                    import os, time
                    fs = sorted(os.listdir('{{dir}}'))
                    t = time.perf_counter()
                    for f in fs:
                        p = '{{dir}}/' + f
                        os.getxattr(p, 'user.onedrive.status', follow_symlinks=False)  # overlay do CraftDriver
                        os.listxattr(p)                                                    # KIO
                        try: os.getxattr(p, 'user.xdg.origin.url')
                        except OSError: pass
                    print(f'{(time.perf_counter() - t) * 1000:.0f}')
                    """;
                q.Reset();
                var ms = Py(py, root);
                var s = q.Count;
                Out($"[16 xattr] {dir}: 1000 arquivos × (getxattr status + listxattr + getxattr user.xdg) = {ms} ms, {s} SELECTs ({s / 1000.0:F1}/arquivo)");
                q.Reset();
                ms = Py($$"""
                    import os, time
                    fs = sorted(os.listdir('{{dir}}'))
                    t = time.perf_counter()
                    for f in fs: os.getxattr('{{dir}}/' + f, 'user.onedrive.status', follow_symlinks=False)
                    print(f'{(time.perf_counter() - t) * 1000:.0f}')
                    """, root);
                Out($"[16 xattr] {dir}: só getxattr status ×1000 = {ms} ms, {q.Count} SELECTs ({q.Count / 1000.0:F1}/arquivo)");
            }
        });
    }

    [Fact(Explicit = true)]
    public void M16_Counts_com_50k_itens()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        for (var i = 0; i < 5; i++) Pasta(h, h.Drive.RootLid, "p" + i, 10_000);
        // ruído realista: outra drive (compartilhados) com mais 50k itens na mesma tabela
        h.Db.InTransaction(() =>
        {
            for (var i = 0; i < 50_000; i++)
                h.Db.Insert(new Item { DriveId = "outra|x", ParentLid = 1, Name = "o" + i, ItemId = "o" + i });
        });
        h.Db.Counts(FakeGraph.DriveId);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) h.Db.Counts(FakeGraph.DriveId);
        var semIndice = sw.Elapsed.TotalMilliseconds / 20;
        string Plano()
        {
            using var c = new SqliteConnection("Data Source=" + h.DbPath);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN SELECT COALESCE(SUM(is_dir=0),0) FROM items WHERE drive_id='x'";
            using var r = cmd.ExecuteReader();
            var s = new List<string>();
            while (r.Read()) s.Add(r.GetString(3));
            return string.Join("; ", s);
        }
        var plano1 = Plano();
        using (var c = new SqliteConnection("Data Source=" + h.DbPath))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE INDEX ix_med_drive ON items(drive_id)"; // só para medir o ganho
            cmd.ExecuteNonQuery();
        }
        h.Db.Counts(FakeGraph.DriveId);
        sw.Restart();
        for (var i = 0; i < 20; i++) h.Db.Counts(FakeGraph.DriveId);
        var comIndice = sw.Elapsed.TotalMilliseconds / 20;
        Out($"[16 Counts] 50k itens na drive + 50k de outra: {semIndice:F2} ms/chamada ({plano1}); com índice items(drive_id): {comIndice:F2} ms ({Plano()}). A cada 5 s = {semIndice / 5000 * 100:F3}% de 1 núcleo");
    }

    [Fact(Explicit = true)]
    public void M16_EnforceCacheLimit()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        var dir = Pasta(h, h.Drive.RootLid, "cacheado", 5000, cached: true); // 5000 × 1 KiB ≈ 4,9 MiB
        Pasta(h, h.Drive.RootLid, "online", 45_000); // resto da drive, só nuvem
        h.Config.CacheLimitMB = 4; // acima do limite: despeja até 90% (3,6 MiB) ≈ 1300 arquivos
        using var q = new Sql(h.Db);
        var sw = Stopwatch.StartNew();
        h.Engine.EnforceCacheLimit();
        Out($"[16 EnforceCacheLimit] 5000 em cache (50k itens), despeja até 90%: {sw.ElapsedMilliseconds} ms, {q.Count} SELECTs");
        // pior caso: tudo o que está em cache é fixado → nada sai, e a cada 2 min percorre todos com 1 CTE cada
        var pasta = h.Db.Get(dir)!;
        pasta.Pinned = true;
        h.Db.Update(pasta);
        foreach (var x in h.Db.GetChildren(dir).Where(x => x.CachedCTag == null))
        {
            File.WriteAllBytes(h.Engine.Cache.PathFor(x.DriveId, x.Lid), new byte[1024]);
            x.CachedCTag = x.CTag;
            h.Db.Update(x);
        }
        q.Reset();
        sw.Restart();
        h.Engine.EnforceCacheLimit();
        Out($"[16 EnforceCacheLimit] pior caso 5000 fixados acima do limite: {sw.ElapsedMilliseconds} ms, {q.Count} SELECTs (repetido a cada 2 min)");
    }

    [Fact(Explicit = true)]
    public void M16_InodeTable_Detach_com_tabela_grande()
    {
        foreach (var total in new[] { 10_000, 100_000 })
        {
            var t = new InodeTable();
            for (var d = 0; d < total / 1000; d++)
                for (var f = 0; f < 1000; f++)
                    t.Lookup($"/D/d{d}/f{f}");
            var sw = Stopwatch.StartNew();
            for (var d = 0; d < 10 && d < total / 1000; d++)
            {
                for (var f = 0; f < 1000; f++) t.Detach($"/D/d{d}/f{f}"); // rm -rf: unlink de cada arquivo
                t.Detach($"/D/d{d}");                                     // e rmdir
            }
            Out($"[16 InodeTable] tabela com {total} caminhos: rm -rf de 10k arquivos = {sw.ElapsedMilliseconds} ms (só Subtree/Detach, sob o lock da tabela)");
        }
    }

    [Fact(Explicit = true)]
    public async Task M16_rm_rf_depois_de_find_grande()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        var big = h.Db.Insert(new Item { DriveId = FakeGraph.DriveId, ParentLid = h.Drive.RootLid, Name = "big", IsDir = true, ItemId = "big" });
        for (var d = 0; d < 100; d++) Pasta(h, big, "d" + d, 1000); // 100k caminhos
        Pasta(h, h.Drive.RootLid, "alvoA", 10_000);
        Pasta(h, h.Drive.RootLid, "alvoB", 10_000);
        var tab = () => ((InodeTable)typeof(FuseHost).GetField("_inodes", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Count;
        await Montado(h, root =>
        {
            var sw = Stopwatch.StartNew();
            Sh("ls alvoA >/dev/null; rm -rf alvoA", root);
            var a = sw.ElapsedMilliseconds;
            sw.Restart();
            var n = Sh("find . | wc -l", root).output;
            var tf = sw.ElapsedMilliseconds;
            Sh("ls alvoB >/dev/null", root);
            Out($"[16 rm -rf] InodeTable.Count antes do 2º rm = {tab()}");
            sw.Restart();
            Sh("rm -rf alvoB", root);
            var b = sw.ElapsedMilliseconds;
            Out($"[16 rm -rf] 10k arquivos: tabela pequena {a} ms; depois de find ({n} caminhos, {tf} ms, InodeTable.Count={tab()}) {b} ms");
        });
    }

    [Fact(Explicit = true)]
    public async Task M16_lookup_negativo_repetido()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        Pasta(h, h.Drive.RootLid, "proj", 10);
        await Montado(h, root =>
        {
            using var q = new Sql(h.Db);
            Sh("[ -e proj/f00001.txt ]", root);
            foreach (var (nome, alvo) in new[] { ("positivo", "proj/f00001.txt"), ("negativo", "proj/.git") })
            {
                q.Reset();
                var sw = Stopwatch.StartNew();
                Sh($"for i in $(seq 1000); do [ -e {alvo} ]; done", root);
                Out($"[16 lookup] {nome}: 1000 stat em {sw.ElapsedMilliseconds} ms, {q.Count} SELECTs");
            }
            // custo por ida: Resolve de um nome que não existe
            q.Reset();
            Sh("[ -e proj/nao-existe ]", root);
            Out($"[16 lookup] 1 lookup negativo = {q.Count} SELECTs");
        });
    }

    /// <summary>create/mkdir numa pasta com 10 k nomes não-ASCII: a checagem de colisão "como a nuvem" (GetChildNoCase).</summary>
    [Fact(Explicit = true)]
    public void M16_create_em_pasta_com_10k_nomes_nao_ascii()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        Pasta(h, h.Drive.RootLid, "rel", 10_000, prefix: "Relatório ");
        using var q = new Sql(h.Db);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 200; i++)
        {
            Assert.Equal(0, h.Fs.Create(h.P($"rel/Ação {i}.txt"), CraftDriver.Core.Vfs.OpenFlags.WrOnly | CraftDriver.Core.Vfs.OpenFlags.Creat, out var fh));
            h.Fs.Release(fh);
        }
        Out($"[16 GetChildNoCase] 200 create não-ASCII em pasta com 10k 'Relatório…': {sw.ElapsedMilliseconds} ms ({sw.ElapsedMilliseconds / 200.0:F2} ms/create), {q.Count} SELECTs");
    }

    [Fact(Explicit = true)]
    public async Task M16_MSAL_leituras_do_cache_por_GetToken()
    {
        var dir = Path.Combine(Path.GetTempPath(), "an14-msal-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var ts = new TraceSource("an14", SourceLevels.All);
        ts.Listeners.Clear();
        ts.Listeners.Add(new Ouvinte(lines));
        // cache não vazio (estrutura MSAL v3 válida): a releitura não depende de o arquivo estar vazio
        File.WriteAllText(Path.Combine(dir, "cache.bin"), "{\"Account\":{},\"AccessToken\":{},\"RefreshToken\":{},\"IdToken\":{},\"AppMetadata\":{}}");
        var props = new Microsoft.Identity.Client.Extensions.Msal.StorageCreationPropertiesBuilder("cache.bin", dir).WithLinuxUnprotectedFile().Build();
        var helper = await Microsoft.Identity.Client.Extensions.Msal.MsalCacheHelper.CreateAsync(props, ts);
        var app = Microsoft.Identity.Client.PublicClientApplicationBuilder.Create(Guid.NewGuid().ToString()).Build();
        helper.RegisterCache(app.UserTokenCache);
        await app.GetAccountAsync("x");
        lines.Clear();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++) await app.GetAccountAsync("x"); // GetTokenAsync = GetAccountAsync + AcquireTokenSilent
        var ms = sw.Elapsed.TotalMilliseconds;
        var all = lines.ToList();
        int C(string s) => all.Count(l => l.Contains(s, StringComparison.OrdinalIgnoreCase));
        Out($"[16 MSAL] 100 acessos ao cache (arquivo): {ms:F0} ms; 'Before access'={C("Before access")}, 'store has changed'={C("store has changed")}, 'Reading'={C("Reading")}, lock={C("Acquiring lock")}, bytes lidos não vazios={all.Count(l => l.Contains("Got '") && !l.Contains("Got '0'"))}");
        Out("[16 MSAL] mensagens distintas: " + string.Join(" | ", all.Select(l => l.Length > 60 ? l[..60] : l).Distinct().Take(12)));
        Directory.Delete(dir, true);
    }

    sealed class Ouvinte(System.Collections.Concurrent.ConcurrentQueue<string> q) : TraceListener
    {
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (message != null) q.Enqueue(message); }
        public override void TraceEvent(TraceEventCache? c, string source, TraceEventType t, int id, string? message) { if (message != null) q.Enqueue(message); }
        public override void TraceEvent(TraceEventCache? c, string source, TraceEventType t, int id, string? format, params object?[]? args) =>
            q.Enqueue(args == null ? format ?? "" : string.Format(format ?? "", args));
    }
}
