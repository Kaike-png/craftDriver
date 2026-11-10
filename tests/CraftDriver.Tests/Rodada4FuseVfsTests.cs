using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using CraftDriver.Core.Config;
using CraftDriver.Core.State;
using CraftDriver.Core.Vfs;
using CraftDriver.Fuse;
using Microsoft.Data.Sqlite;

namespace CraftDriver.Tests;

/// <summary>
/// PENDENCIAS.md rodada 4, frente B (FUSE/VFS): §24.9 (Resolve extra no loop), §24.10 (Write com Db.Lock no loop),
/// §28 (loop do FUSE não termina após setattr + desmontar no arm64). Testes RED de propósito (afirmam o comportamento
/// alvo); medições em <c>[Fact(Explicit = true)]</c> — rodar com
/// <c>dotnet exec CraftDriver.Tests.dll -method CraftDriver.Tests.Rodada4FuseVfsTests.&lt;Metodo&gt; -explicit only -parallel none</c>.
/// </summary>
[Collection("fuse")]
public class Rodada4FuseVfsTests(ITestOutputHelper output)
{
    static readonly string Arquivo = Path.Combine(Path.GetTempPath(), "cd-B-medicoes.txt");

    void Out(string s)
    {
        output.WriteLine(s);
        File.AppendAllText(Arquivo, s + "\n");
    }

    /// <summary>Conta SELECT/WITH na conexão principal de TODAS as threads (handlers do FUSE rodam no loop e no SlowOps).</summary>
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
        var psi = new ProcessStartInfo("bash", ["-c", "umask 022; " + script]) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, (o + err.Result).Trim());
    }

    static Process PyProc(string code, string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("/usr/bin/python3") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(code);
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }

    static string Py(string code, string cwd)
    {
        using var p = PyProc(code, cwd);
        var err = p.StandardError.ReadToEndAsync();
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (o + err.Result).Trim();
    }

    static async Task Montado(Harness h, Action<string> body, double timeout = 1.0)
    {
        RealFuse.Require();
        var mnt = Path.Combine(h.Dir, "mnt");
        var loop = FuseHost.MountAsync(h.Fs, mnt, cacheTimeout: timeout);
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
            Assert.True(await loop.WithinAsync(TimeSpan.FromSeconds(15)), "loop do FUSE não terminou após desmontar: " + FuseHost.Diagnostico());
        }
    }

    /// <summary>Cadeia de pastas a/b/c/d direto no banco; devolve o lid da última.</summary>
    static long Cadeia(Harness h, params string[] nomes)
    {
        var pai = h.Drive.RootLid;
        foreach (var n in nomes)
            pai = h.Db.Insert(new Item { DriveId = FakeGraph.DriveId, ParentLid = pai, Name = n, IsDir = true, ItemId = "dir-" + n + Guid.NewGuid().ToString("N")[..6], MTime = 1_700_000_000_000 });
        return pai;
    }

    static long ArquivoNoCache(Harness h, long pai, string nome)
    {
        var it = new Item
        {
            DriveId = FakeGraph.DriveId, ParentLid = pai, Name = nome, Size = 1024, MTime = 1_700_000_000_000,
            ItemId = "f-" + Guid.NewGuid().ToString("N"), CTag = "c1", CachedCTag = "c1",
        };
        h.Db.Insert(it);
        File.WriteAllBytes(h.Engine.Cache.PathFor(it.DriveId, it.Lid), new byte[1024]);
        return it.Lid;
    }

    // ---------- §24.9 OpenNeedsNetwork/RenameNeedsNetwork na thread do loop ----------

    /// <summary>
    /// O handler Create do FUSE avalia <c>_fs.OpenNeedsNetwork(p, flags)</c> NA THREAD DO LOOP, antes de mandar o
    /// trabalho ao SlowOps. Em create o caminho não existe (o kernel só chama create depois de um lookup negativo), mas
    /// OpenNeedsNetwork faz um Resolve completo: depois de qualquer Insert (o create anterior sobe ItemsVersion) o
    /// cache de caminhos está vazio e a caminhada desce do topo, um SELECT por nível, sob Db.Lock — e o resultado
    /// (null) não é cacheado. Alvo: decidir "precisa de rede" para create sem tocar no banco.
    /// </summary>
    [Fact]
    public void R24_9_OpenNeedsNetwork_de_create_nao_consulta_o_banco()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        var d = Cadeia(h, "a", "b", "c", "d");
        ArquivoNoCache(h, d, "existente.txt");
        Assert.NotNull(h.Fs.Resolve(h.P("/a/b/c/d/existente.txt"))); // pai no cache de caminhos
        ArquivoNoCache(h, d, "outro.txt"); // Insert = create anterior: sobe a geração do cache de caminhos

        using var q = new Sql(h.Db);
        var rede = h.Fs.OpenNeedsNetwork(h.P("/a/b/c/d/novo.txt"), OpenFlags.Creat | OpenFlags.WrOnly | OpenFlags.Excl);
        var create = q.Count;
        q.Reset();
        h.Fs.OpenNeedsNetwork(h.P("/a/b/c/d/existente.txt"), 0);
        var open = q.Count;
        Assert.False(rede);
        Assert.True(create == 0,
            $"OpenNeedsNetwork de create (caminho inexistente, profundidade 5) fez {create} SELECTs na thread do loop " +
            $"(caminhada completa não cacheada); open de arquivo existente fez {open}");
    }

    /// <summary>
    /// Insert não invalida o cache de caminhos (só guarda mapeamentos positivos caminho → lid): criar um arquivo
    /// numa pasta não pode obrigar o próximo lookup de um vizinho a descer do topo.
    /// </summary>
    [Fact]
    public void R24_9_Insert_nao_sobe_a_geracao_do_cache_de_caminhos()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        var d = Cadeia(h, "a", "b", "c", "d");
        ArquivoNoCache(h, d, "vizinho.txt");
        Assert.NotNull(h.Fs.Resolve(h.P("/a/b/c/d"))); // o kernel resolve a pasta (lookup) antes dos filhos
        Assert.NotNull(h.Fs.Resolve(h.P("/a/b/c/d/vizinho.txt")));
        var versao = h.Db.ItemsVersion;
        ArquivoNoCache(h, d, "novo.txt");
        Assert.Equal(versao, h.Db.ItemsVersion);
        using var q = new Sql(h.Db);
        Assert.NotNull(h.Fs.Resolve(h.P("/a/b/c/d/vizinho.txt")));
        Assert.NotNull(h.Fs.Resolve(h.P("/a/b/c/d/novo.txt")));
        Assert.True(q.Count <= 2, $"vizinho (acerto) + novo (pai no cache) fizeram {q.Count} SELECTs (alvo 2)");
    }

    /// <summary>
    /// Sem a geração subir no Insert, o cache pode guardar o lid de um item já apagado (Delete também não sobe):
    /// apagar e recriar no MESMO caminho — arquivo e pasta-pai — tem de achar o item novo, não ENOENT.
    /// </summary>
    [Fact]
    public void R24_9_apagar_e_recriar_no_mesmo_caminho_acha_o_item_novo()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        var d = Cadeia(h, "a", "b");
        var velho = ArquivoNoCache(h, d, "x.txt");
        Assert.Equal(velho, h.Fs.Resolve(h.P("/a/b/x.txt"))?.Item?.Lid);
        h.Db.Delete(velho);
        var novo = ArquivoNoCache(h, d, "x.txt");
        Assert.Equal(novo, h.Fs.Resolve(h.P("/a/b/x.txt"))?.Item?.Lid);

        // pasta-pai apagada e recriada (lid novo): o atalho "pai já resolvido" não pode usar o lid antigo
        Assert.NotNull(h.Fs.Resolve(h.P("/a/b")));
        foreach (var x in h.Db.GetSubtree(d).OrderByDescending(x => x.Lid)) h.Db.Delete(x.Lid);
        Assert.Null(h.Fs.Resolve(h.P("/a/b/x.txt")));
        var a = h.Fs.Resolve(h.P("/a"))!.Value.Item!.Lid;
        var b2 = h.Db.Insert(new Item { DriveId = FakeGraph.DriveId, ParentLid = a, Name = "b", IsDir = true, ItemId = "dir-b2", MTime = 1_700_000_000_000 });
        var y = ArquivoNoCache(h, b2, "x.txt");
        Assert.Equal(y, h.Fs.Resolve(h.P("/a/b/x.txt"))?.Item?.Lid);
        Assert.Equal(b2, h.Fs.Resolve(h.P("/a/b"))?.Item?.Lid);
    }

    /// <summary>Medição: SELECTs por create e por open pela montagem real (todas as threads), e só o OpenNeedsNetwork.</summary>
    [Fact(Explicit = true)]
    public async Task M24_9_selects_por_open_e_create()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        var d = Cadeia(h, "a", "b", "c", "d");
        const int N = 200;
        for (var i = 0; i < N; i++) ArquivoNoCache(h, d, $"e{i:D4}.txt");

        using (var q = new Sql(h.Db))
        {
            h.Fs.Resolve(h.P("/a/b/c/d")); // o kernel resolve a pasta (lookup) antes do open/create
            h.Fs.Resolve(h.P("/a/b/c/d/e0001.txt"));
            q.Reset();
            h.Fs.OpenNeedsNetwork(h.P("/a/b/c/d/e0001.txt"), 0);
            var openHit = q.Count;
            q.Reset();
            h.Fs.OpenNeedsNetwork(h.P("/a/b/c/d/novo.txt"), OpenFlags.Creat | OpenFlags.WrOnly);
            var createPaiCache = q.Count;
            ArquivoNoCache(h, d, "bump.txt");
            q.Reset();
            h.Fs.OpenNeedsNetwork(h.P("/a/b/c/d/novo.txt"), OpenFlags.Creat | OpenFlags.WrOnly);
            var createFrio = q.Count;
            Out($"[24.9 in-proc] OpenNeedsNetwork: open existente (cache hit) {openHit} SELECT; create com pai no cache {createPaiCache}; create após Insert {createFrio}");
        }

        await Montado(h, root =>
        {
            using var q = new Sql(h.Db);
            Thread.Sleep(1200);
            Sh("ls -l a/b/c/d > /dev/null", root);
            q.Reset();
            var sw = Stopwatch.StartNew();
            var r = Py($"import os\nfor i in range({N}):\n    os.close(os.open('a/b/c/d/e%04d.txt' % i, os.O_RDONLY))\nprint('ok')", root);
            Out($"[24.9 FUSE] open+close de {N} arquivos em cache (profundidade 5, dentries quentes): {q.Count} SELECTs ({q.Count / (double)N:F2}/open), {sw.ElapsedMilliseconds} ms ({r})");
            q.Reset();
            sw.Restart();
            r = Py($"import os\nfor i in range({N}):\n    os.close(os.open('a/b/c/d/n%04d.txt' % i, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o644))\nprint('ok')", root);
            Out($"[24.9 FUSE] create+close de {N} arquivos novos (profundidade 5): {q.Count} SELECTs ({q.Count / (double)N:F2}/create), {sw.ElapsedMilliseconds} ms ({r})");
        });
    }

    // ---------- §24.10 Write inline no loop com Db.Lock ----------

    /// <summary>
    /// Write roda inline na thread do loop e, depois do pwrite no arquivo do cache, faz <c>lock (Db.Lock)</c> +
    /// MarkDirty (Get + UPDATE da fração do mtime + Update). Uma transação longa do motor (aqui: o próprio Db.Lock
    /// preso 3 s por outra thread) segura o write() de um arquivo JÁ ABERTO e em cache — e o lookup/getattr de outros
    /// caminhos (Resolve também passa pelo Db.Lock). A leitura de outro fd já aberto (RandomAccess.Read, sem lock)
    /// é a referência de que o loop em si está livre.
    /// </summary>
    /// <remarks>
    /// §24.10 (a) NÃO implementado nesta rodada (Explicit para não deixar a suíte vermelha): tirar o Db.Lock do write
    /// exige o motor enxergar a geração pendente dos handles antes do snapshot e no commit do envio, o ApplyRemote
    /// (região da frente M) tratar escrita pendente como Dirty e o getattr sobrepor o mtime pendente. Ver relatório.
    /// </remarks>
    [Fact(Explicit = true)]
    public async Task R24_10_write_em_arquivo_aberto_nao_espera_Db_Lock()
    {
        RealFuse.Require();
        using var h = new Harness();
        h.Engine.Paused = true;
        var pastaLer = Cadeia(h, "ler");
        ArquivoNoCache(h, pastaLer, "ler.bin");
        var pastaStat = Cadeia(h, "nunca-vista");
        ArquivoNoCache(h, pastaStat, "x.txt");
        const int Segura = 3000;
        string relato = "";
        long msWrite = -1;

        await Montado(h, root =>
        {
            Assert.Equal(0, Sh("printf abc > w.txt", root).code);
            const string escritor = "import os, sys, time\nfd = os.open(sys.argv[1], os.O_RDWR)\nprint('aberto', flush=True)\nsys.stdin.readline()\n" +
                                    "t = time.monotonic()\nos.pwrite(fd, b'x' * 4096, 0)\nprint(int((time.monotonic() - t) * 1000), flush=True)\n";
            const string leitor = "import os, sys, time\nfd = os.open(sys.argv[1], os.O_RDONLY)\nprint('aberto', flush=True)\nsys.stdin.readline()\n" +
                                  "t = time.monotonic()\nos.pread(fd, 512, 0)\nprint(int((time.monotonic() - t) * 1000), flush=True)\n";
            using var w = PyProc(escritor, root, Path.Combine(root, "w.txt"));
            using var r = PyProc(leitor, root, Path.Combine(root, "ler", "ler.bin"));
            Assert.Equal("aberto", w.StandardOutput.ReadLine());
            Assert.Equal("aberto", r.StandardOutput.ReadLine());

            using var preso = new ManualResetEventSlim();
            var dono = new Thread(() =>
            {
                lock (h.Db.Lock) // imita uma transação longa do delta (InTransaction segura este mesmo monitor)
                {
                    preso.Set();
                    Thread.Sleep(Segura);
                }
            })
            { IsBackground = true };
            dono.Start();
            preso.Wait();
            var sw = Stopwatch.StartNew();
            w.StandardInput.WriteLine();
            w.StandardInput.Flush();
            r.StandardInput.WriteLine();
            r.StandardInput.Flush();
            using var st = PyProc("import os, sys, time\nt = time.monotonic()\nos.stat(sys.argv[1])\nprint(int((time.monotonic() - t) * 1000), flush=True)\n",
                h.Dir, Path.Combine(root, "nunca-vista", "x.txt"));
            Thread.Sleep(700);
            var amostras = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                try { amostras.Add(File.ReadAllText($"/proc/{w.Id}/syscall").Split(' ')[0]); }
                catch (IOException) { amostras.Add("?"); }
                Thread.Sleep(100);
            }
            var lerMs = r.StandardOutput.ReadLine();
            var statMs = st.StandardOutput.ReadLine();
            var wMs = w.StandardOutput.ReadLine();
            var total = sw.ElapsedMilliseconds;
            dono.Join();
            msWrite = long.TryParse(wMs, out var x) ? x : -1;
            var nrWrite = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "68" : "18"; // pwrite64
            relato = $"Db.Lock preso {Segura} ms por outra thread: pwrite(4 KiB) em fd já aberto e em cache = {wMs} ms " +
                     $"(/proc/PID/syscall 700 ms depois: [{string.Join(",", amostras)}], pwrite64={nrWrite}); " +
                     $"stat de caminho nunca visto = {statMs} ms; pread de outro fd já aberto = {lerMs} ms; total {total} ms";
            Out("[24.10] " + relato);
            w.WaitForExit(5000);
            r.WaitForExit(5000);
        });
        Assert.True(msWrite is >= 0 and < 1000, "write() preso no Db.Lock: " + relato);
    }

    /// <summary>
    /// §24.10 (b): uma página grande do delta é aplicada em transações curtas — entre elas o Db.Lock fica livre e o
    /// lookup/getattr do FUSE (Resolve + GetAttr, inclusive de caminho nunca visto) não espera a página inteira.
    /// Mede a maior espera de um getattr concorrente durante a aplicação de 4.000 itens numa página só.
    /// </summary>
    [Fact]
    public void R24_10_pagina_grande_do_delta_nao_segura_o_lookup()
    {
        // Calibrado na própria máquina: a MESMA página aplicada numa transação só (referência) × em lotes do padrão.
        // Limite absoluto em ms falhava no CI (Release + cobertura, executor ~2,5× mais lento que aqui).
        var (piorUnica, duracaoUnica) = MedirEsperaDoGetattrDuranteODelta(lote: 10_000);
        var (pior, duracao) = MedirEsperaDoGetattrDuranteODelta(lote: new AppConfig().DeltaBatchSize);
        // sem lotes as duas medições ficam iguais (±20 %); com lotes aqui ~0,15 e no executor ARM do CI ~0,4
        Assert.True(pior < piorUnica * 0.6,
            $"getattr esperou {pior} ms atrás do delta em lotes (página aplicada em {duracao} ms); numa transação só, " +
            $"{piorUnica} ms (em {duracaoUnica} ms) — os lotes não estão soltando o Db.Lock");
    }

    (long pior, long duracao) MedirEsperaDoGetattrDuranteODelta(int lote)
    {
        using var h = new Harness(c => c.DeltaBatchSize = lote);
        h.Remote.RemoteCreate("/", "P", null, folder: true);
        h.Remote.RemoteCreate("/", "fixo.txt", "x");
        h.Settle();
        const int N = 4000;
        for (var i = 0; i < N; i++) h.Remote.RemoteCreate("/P", $"f{i:D5}.txt", "x");
        var parar = false;
        long pior = 0, chamadas = 0;
        var esperas = new List<long>();
        var medidor = new Thread(() =>
        {
            var sw = new Stopwatch();
            var n = 0;
            while (!Volatile.Read(ref parar))
            {
                sw.Restart();
                h.Fs.GetAttr(h.P("/fixo.txt"), out _);
                h.Fs.GetAttr(h.P($"/P/nunca-{n++}.txt"), out _); // caminho novo: caminhada no banco
                var ms = sw.ElapsedMilliseconds;
                lock (esperas) esperas.Add(ms);
                if (ms > pior) pior = ms;
                chamadas++;
                Thread.Sleep(1);
            }
        })
        { IsBackground = true };
        medidor.Start();
        var total = Stopwatch.StartNew();
        h.Settle();
        var duracao = total.ElapsedMilliseconds;
        Volatile.Write(ref parar, true);
        medidor.Join();
        Assert.NotNull(h.Fs.Resolve(h.P($"/P/f{N - 1:D5}.txt")));
        Out($"[24.10b] lote {lote}: página de {N} itens aplicada em {duracao} ms; getattr concorrente: {chamadas} chamadas, " +
            $"pior espera {pior} ms (maiores: {string.Join(", ", esperas.OrderDescending().Take(5))}; " +
            $"mediana {esperas.Order().ElementAtOrDefault(esperas.Count / 2)} ms)");
        return (pior, duracao);
    }

    // ---------- §28 loop do FUSE não termina após setattr + desmontar (arm64) ----------

    /// <summary>
    /// Laço de montagem → utime na raiz da drive + chmod numa pasta (como R24_8 e Chmod_em_pasta) → desmontar,
    /// medindo o tempo até o loop terminar. Repetições por CD_B_REPETICOES (padrão 50). Falha se alguma desmontagem
    /// passar de 15 s (a mensagem traz _inflight, threads do SlowOps e tarefas do processo).
    /// </summary>
    [Fact(Explicit = true)]
    public async Task M28_setattr_e_desmontar_em_laco()
    {
        RealFuse.Require();
        var n = int.TryParse(Environment.GetEnvironmentVariable("CD_B_REPETICOES"), out var v) ? v : 50;
        var tempos = new List<long>();
        for (var i = 0; i < n; i++)
        {
            using var h = new Harness();
            h.Engine.Paused = true;
            Cadeia(h, "Pasta");
            var mnt = Path.Combine(h.Dir, "mnt");
            var loop = FuseHost.MountAsync(h.Fs, mnt, cacheTimeout: 1.0);
            var root = Path.Combine(mnt, Harness.Name);
            Harness.WaitUntil(() => Directory.Exists(root), "montagem visível");
            var r = Py("import os\nos.utime('.', (1000000000, 1000000000))\nos.chmod('Pasta', 0o700)\nos.utime('Pasta', None)\nprint('ok')", root);
            h.Engine.Stop();
            var sw = Stopwatch.StartNew();
            FuseHost.Unmount();
            var terminou = await loop.WithinAsync(TimeSpan.FromSeconds(15));
            tempos.Add(sw.ElapsedMilliseconds);
            if (!terminou)
            {
                var inflight = (int)typeof(FuseHost).GetField("_inflight", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                Assert.Fail($"rodada {i}: loop do FUSE não terminou após desmontar (py={r}; _inflight={inflight}; SlowOps.Threads={SlowOps.Threads}; " +
                            $"tarefas={Directory.GetDirectories("/proc/self/task").Length}; montado={FuseHost.MountPoint})");
            }
        }
        tempos.Sort();
        Out($"[28] {n}× setattr+desmontar ({RuntimeInformation.ProcessArchitecture}): mediana {tempos[n / 2]} ms, máx {tempos[^1]} ms");
    }

    /// <summary>
    /// §28: <c>fusermount -u</c> falha (EBUSY) com um processo parado com cwd na montagem. Decisão: registrar o erro
    /// (rc + stderr), tentar de novo por alguns segundos e forçar com <c>-uz</c>; na libfuse 3.14 o
    /// <c>fuse_session_exit</c> só marca a saída e o loop fica no <c>sem_wait</c> até chegar uma requisição — o
    /// Unmount acorda o loop. Com o processo ainda vivo: o loop termina e o ponto sai de /proc/self/mounts em 15 s.
    /// </summary>
    [Fact]
    public async Task R28_desmontar_com_montagem_ocupada_forca_e_termina_o_loop()
    {
        RealFuse.Require();
        using var h = new Harness();
        h.Engine.Paused = true;
        var mnt = Path.Combine(h.Dir, "mnt");
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var logAntes = FuseHost.Log;
        FuseHost.Log = logs.Enqueue;
        var loop = FuseHost.MountAsync(h.Fs, mnt, cacheTimeout: 1.0);
        Process? ocupa = null;
        try
        {
            var root = Path.Combine(mnt, Harness.Name);
            Harness.WaitUntil(() => Directory.Exists(root), "montagem visível");
            ocupa = Process.Start(new ProcessStartInfo("sleep", "60") { WorkingDirectory = root })!;
            Thread.Sleep(200);
            h.Engine.Stop();
            var sw = Stopwatch.StartNew();
            FuseHost.Unmount();
            var terminou = await loop.WithinAsync(TimeSpan.FromSeconds(15));
            var t = sw.ElapsedMilliseconds;
            var diag = FuseHost.Diagnostico();
            var montado = File.ReadAllText("/proc/self/mounts").Contains(" " + mnt + " ");
            Out($"[28 ocupada] loop terminou={terminou} em {t} ms; ainda em /proc/mounts={montado}; {diag}");
            Assert.True(terminou, "loop do FUSE não terminou após desmontar: " + diag);
            Assert.False(montado, "montagem ocupada continuou em /proc/self/mounts (sem -uz): " + diag);
            Assert.True(t < 15_000, $"desmontar levou {t} ms");
            Assert.False(ocupa.HasExited, "o processo que ocupava a montagem tinha de continuar vivo durante a desmontagem");
            Assert.Contains(logs, l => l.Contains("fusermount") && l.Contains("rc="));
        }
        finally
        {
            FuseHost.Log = logAntes;
            if (ocupa is { HasExited: false }) { ocupa.Kill(); ocupa.WaitForExit(); }
            ocupa?.Dispose();
            if (!loop.IsCompleted) FuseHost.Unmount();
            Assert.True(await loop.WithinAsync(TimeSpan.FromSeconds(15)), "loop do FUSE não terminou após desmontar: " + FuseHost.Diagnostico());
        }
    }
}
