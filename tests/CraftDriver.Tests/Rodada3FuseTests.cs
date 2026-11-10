using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using CraftDriver.Core.State;
using CraftDriver.Core.Sync;
using CraftDriver.Core.Vfs;
using CraftDriver.Fuse;
using Microsoft.Data.Sqlite;

namespace CraftDriver.Tests;

/// <summary>
/// PENDENCIAS.md rodada 3, frente C (FUSE/VFS): §24.1/.2/.4/.5/.6/.7/.8, §22.5, §22.6, §23.11, §23.12.
/// Testes RED de propósito (afirmam o comportamento correto/alvo); os de desempenho comparam o número medido hoje
/// com o alvo e imprimem a medição na mensagem.
/// </summary>
[Collection("fuse")]
public class Rodada3FuseTests(ITestOutputHelper output)
{
    // ---------- infraestrutura ----------

    /// <summary>Conta statements na conexão principal, por tipo, com filtro opcional pela thread que executa.</summary>
    sealed class Sql : IDisposable
    {
        readonly SqliteConnection _c;
        readonly Func<bool> _filtro;
        int _select, _cte, _outros;
        public readonly ConcurrentQueue<string> Textos = new();
        public bool Guardar { get; set; }
        public int Select => Volatile.Read(ref _select);
        public int Cte => Volatile.Read(ref _cte);
        public int Outros => Volatile.Read(ref _outros);
        public int Total => Select + Cte + Outros;

        public Sql(StateDb db, Func<bool>? filtro = null)
        {
            _filtro = filtro ?? (() => true);
            _c = (SqliteConnection)typeof(StateDb).GetField("_c", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(db)!;
            SQLitePCL.raw.sqlite3_trace(_c.Handle, (_, sql) =>
            {
                if (!_filtro()) return;
                var t = sql.TrimStart();
                if (Guardar) Textos.Enqueue(t.Length > 60 ? t[..60] : t);
                if (t.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref _cte);
                else if (t.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref _select);
                else Interlocked.Increment(ref _outros);
            }, null);
        }

        public void Reset()
        {
            Volatile.Write(ref _select, 0);
            Volatile.Write(ref _cte, 0);
            Volatile.Write(ref _outros, 0);
            Textos.Clear();
        }

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

    static string Py(string body, string cwd)
    {
        var code = "import os, sys, errno, time\n" +
                   "def E(f):\n" +
                   "    try:\n" +
                   "        r = f()\n" +
                   "        return 'OK' if r is None else str(r)\n" +
                   "    except OSError as e:\n" +
                   "        return errno.errorcode.get(e.errno, str(e.errno))\n" + body;
        var psi = new ProcessStartInfo("/usr/bin/python3", ["-c", code]) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (o + err.Result).Trim();
    }

    static async Task Montado(Harness h, Action<string, string> body, double timeout = 1.0)
    {
        RealFuse.Require();
        var mnt = Path.Combine(h.Dir, "mnt");
        var loop = FuseHost.MountAsync(h.Fs, mnt, cacheTimeout: timeout);
        try
        {
            var root = Path.Combine(mnt, Harness.Name);
            Harness.WaitUntil(() => Directory.Exists(root), "montagem visível");
            body(mnt, root);
        }
        finally
        {
            h.Engine.Stop();
            FuseHost.Unmount();
            Assert.True(await loop.WithinAsync(TimeSpan.FromSeconds(15)), "loop do FUSE não terminou após desmontar: " + FuseHost.Diagnostico());
        }
    }

    static InodeTable Inodes() =>
        (InodeTable)typeof(FuseHost).GetField("_inodes", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    /// <summary>Pasta com n arquivos direto no banco (com id remoto, sem conteúdo no cache).</summary>
    static long Pasta(Harness h, long parent, string name, int n)
    {
        long dir = 0;
        h.Db.InTransaction(() =>
        {
            dir = h.Db.Insert(new Item { DriveId = FakeGraph.DriveId, ParentLid = parent, Name = name, IsDir = true, ItemId = "dir-" + name + Guid.NewGuid().ToString("N")[..6], MTime = 1_700_000_000_000 });
            for (var i = 0; i < n; i++)
                h.Db.Insert(new Item
                {
                    DriveId = FakeGraph.DriveId, ParentLid = dir, Name = $"f{i:D5}.txt", Size = 10, MTime = 1_700_000_000_000,
                    ItemId = $"{name}-{i}-{Guid.NewGuid():N}", CTag = "c1",
                });
        });
        return dir;
    }

    // ---------- §24.1 SlowOps: wakeup perdido no limite da fila ----------

    /// <summary>
    /// Fila isolada (a classe privada <c>SlowOps.Fila</c>, por reflexão) com limite 1: o chamador espera o trabalho
    /// anterior sinalizar DE DENTRO do trabalho e enfileira o próximo na hora — exatamente a janela entre o
    /// <c>TryDequeue</c> que falhou e o <c>_idle++</c> do worker. Se o Run vê idle==0 e o limite já está cheio,
    /// retorna sem acordar ninguém; o worker dorme 30 s e sai sem reler a fila. O teste tenta por até 20 s.
    /// </summary>
    [Fact]
    public void R24_1_SlowOps_trabalho_enfileirado_no_limite_nunca_fica_sem_worker()
    {
        var tipo = typeof(SlowOps).GetNestedType("Fila", BindingFlags.NonPublic)!;
        var fila = Activator.CreateInstance(tipo, 1, "teste-r24-1")!;
        var run = (Action<Action>)Delegate.CreateDelegate(typeof(Action<Action>), fila, tipo.GetMethod("Run")!);
        var fQueue = tipo.GetField("_queue", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var fThreads = tipo.GetField("_threads", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var fIdle = tipo.GetField("_idle", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var rnd = new Random(24);
        var sw = Stopwatch.StartNew();
        long iteracoes = 0;
        string? perdido = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(20) && perdido == null)
        {
            iteracoes++;
            var feito = new StrongBox<int>();
            run(() => Volatile.Write(ref feito.Value, 1));
            var prazo = Stopwatch.StartNew();
            while (Volatile.Read(ref feito.Value) == 0)
            {
                if (prazo.ElapsedMilliseconds > 2000)
                {
                    var q = (ConcurrentQueue<Action>)fQueue.GetValue(fila)!;
                    perdido = $"iteração {iteracoes}: trabalho parado na fila há 2 s (fila={q.Count}, threads={fThreads.GetValue(fila)}, idle={fIdle.GetValue(fila)})";
                    break;
                }
                Thread.SpinWait(1);
            }
            Thread.SpinWait(rnd.Next(0, 40)); // varre o deslocamento entre o Enqueue e o _idle++ do worker
        }
        if (perdido != null) run(() => { }); // destrava o worker para não deixar a thread presa
        output.WriteLine($"iterações: {iteracoes}; {perdido ?? "nenhum wakeup perdido"}");
        Assert.True(perdido == null, $"wakeup perdido no limite da fila: {perdido}");
    }

    // ---------- §24.2 tempestade de notificações na enumeração ----------

    /// <summary>
    /// Enumeração remota de 3.000 arquivos em /Novo/A/B (pasta que o kernel nunca viu). Só o Insert de /Novo tem pai
    /// conhecido pelo kernel; os demais não têm dentry nem inode em cache — nenhum SELECT seria necessário na thread
    /// fuse-notify para eles. Alvo: ≤ 20 SELECTs nessa thread.
    /// </summary>
    [Fact]
    public async Task R24_2_enumeracao_de_inodes_desconhecidos_nao_consulta_o_banco_na_thread_fuse_notify()
    {
        using var h = new Harness();
        const int n = 3000;
        h.Remote.RemoteCreate("/", "Novo", null, folder: true);
        h.Remote.RemoteCreate("/Novo", "A", null, folder: true);
        h.Remote.RemoteCreate("/Novo/A", "B", null, folder: true);
        for (var i = 0; i < n; i++) h.Remote.RemoteCreate("/Novo/A/B", $"f{i:D5}.txt", "x");
        int selects = 0, eventos = 0;
        await Montado(h, (_, root) =>
        {
            Assert.Equal(0, Sh("ls >/dev/null", root).code); // o kernel conhece só a raiz da drive
            using var sql = new Sql(h.Db, () => Thread.CurrentThread.Name == "fuse-notify");
            h.Fs.ItemChanged += (_, _) => Interlocked.Increment(ref eventos);
            h.Drive.RunDeltaAsync(CancellationToken.None).GetAwaiter().GetResult();
            // espera a fila de notificações esvaziar (contagem estável por 1 s)
            var anterior = -1;
            while (sql.Select != anterior) { anterior = sql.Select; Thread.Sleep(1000); }
            selects = sql.Select + sql.Cte;
        });
        output.WriteLine($"[24.2] {n} itens enumerados: {eventos} eventos, {selects} SELECTs na thread fuse-notify");
        Assert.True(selects <= 20, $"{selects} SELECTs na thread fuse-notify para {n} itens ({eventos} eventos; ≈{selects / (double)Math.Max(1, eventos):F1} por evento); alvo ≤ 20");
    }

    // ---------- §24.4 rm -rf ----------

    /// <summary>
    /// rm -rf de 1.000 arquivos (sem conteúdo no cache, motor pausado). Alvo: sem CTE recursiva para arquivo (só a do
    /// rmdir da pasta) e no máximo 8 statements por arquivo (lookup + unlink), sem contar BEGIN/COMMIT: cada unlink
    /// roda numa transação (um commit em vez de 4 implícitos). Medido com 1.000 arquivos: com a transação 870–994 ms
    /// (9,8 statements/arquivo contando BEGIN/COMMIT), sem ela 1.255–3.193 ms (7,8) — a transação é mais rápida.
    /// </summary>
    [Fact]
    public async Task R24_4_rm_rf_nao_faz_CTE_por_arquivo_e_poucos_statements_por_unlink()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        const int n = 1000;
        Pasta(h, h.Drive.RootLid, "alvo", n);
        int cte = 0, sel = 0, outros = 0, transacao = 0;
        long ms = 0;
        await Montado(h, (_, root) =>
        {
            Assert.Equal(0, Sh("ls -l alvo >/dev/null", root).code);
            Thread.Sleep(1100); // entradas do ls expiram: o rm faz lookup como no uso real depois de algum tempo
            using var sql = new Sql(h.Db) { Guardar = true };
            var sw = Stopwatch.StartNew();
            var r = Sh("rm -rf alvo", root);
            ms = sw.ElapsedMilliseconds;
            Assert.Equal(0, r.code);
            (cte, sel, outros) = (sql.Cte, sql.Select, sql.Outros);
            transacao = sql.Textos.Count(t => t.StartsWith("BEGIN", StringComparison.Ordinal) || t.StartsWith("COMMIT", StringComparison.Ordinal));
            foreach (var g in sql.Textos.GroupBy(x => System.Text.RegularExpressions.Regex.Replace(x, @"\d+", "#")).OrderByDescending(g => g.Count()).Take(15))
                output.WriteLine($"  {g.Count(),6}  {g.Key.ReplaceLineEndings(" ")}");
        });
        var porArquivo = (cte + sel + outros - transacao) / (double)n;
        var escritas = (outros - transacao) / (double)n;
        output.WriteLine($"[24.4] rm -rf {n} arquivos: {ms} ms; CTE={cte} SELECT={sel} outros={outros} (BEGIN/COMMIT={transacao}) → {porArquivo:F1} statements/arquivo");
        // Quantos lookups o kernel faz por arquivo varia com a versão (aqui, 7.x: ~1 SELECT de item por arquivo; runners do
        // CI, 6.8: ~3,8): o orçamento de SELECT é folgado; o que a correção garante é a CTE e as escritas por unlink.
        Assert.True(cte <= 1 && escritas <= 5 && sel / (double)n <= 6,
            $"rm -rf {n} arquivos ({ms} ms): {cte} CTE recursivas (alvo ≤ 1), {sel} SELECT (alvo ≤ 6/arquivo), {outros} outros " +
            $"(BEGIN/COMMIT={transacao}; escritas {escritas:F1}/arquivo, alvo ≤ 5) = {porArquivo:F1} statements/arquivo");
    }

    /// <summary>
    /// Lookup de nomes ainda não vistos numa pasta já resolvida (rm -rf/find frios, primeiro ls -l): o pai está no
    /// cache de caminhos, então cada filho custa só o GetChild — não a caminhada inteira desde a raiz da drive.
    /// </summary>
    [Fact]
    public void R24_4_lookup_de_filho_novo_em_pasta_ja_resolvida_custa_um_SELECT()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        var pai = h.Drive.RootLid;
        foreach (var nome in new[] { "p1", "p2", "p3" })
            pai = h.Db.Insert(new Item { DriveId = FakeGraph.DriveId, ParentLid = pai, Name = nome, IsDir = true, ItemId = "id-" + nome });
        Pasta(h, pai, "alvo", 100);
        Assert.NotNull(h.Fs.Resolve(h.P("p1/p2/p3/alvo")));
        var tid = Environment.CurrentManagedThreadId;
        using var sql = new Sql(h.Db, () => Environment.CurrentManagedThreadId == tid);
        for (var i = 0; i < 100; i++) Assert.NotNull(h.Fs.Resolve(h.P($"p1/p2/p3/alvo/f{i:D5}.txt")));
        Assert.Null(h.Fs.Resolve(h.P("p1/p2/p3/alvo/nao-existe.txt")));
        var porFilho = (sql.Select + sql.Cte) / 101.0;
        output.WriteLine($"[24.4] 101 lookups de filhos novos: {porFilho:F1} SELECT/lookup");
        Assert.True(porFilho <= 1, $"{porFilho:F1} SELECTs por lookup de filho novo numa pasta já resolvida (alvo 1)");
    }

    // ---------- §24.5 / §22.5 ItemsVersion a cada write ----------

    /// <summary>
    /// 200 write() num arquivo aberto: cada um faz MarkDirty → Db.Update → ItemsVersion++, zerando o LRU de caminhos.
    /// Um Resolve de outro arquivo na mesma pasta (profundidade 5) entre writes deveria ser hit (1 SELECT).
    /// Sem FUSE: chama o VirtualFs direto, motor pausado, SELECTs só da thread do teste.
    /// </summary>
    [Fact]
    public void R24_5_write_nao_invalida_o_cache_de_caminhos()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        var pai = h.Drive.RootLid;
        foreach (var nome in new[] { "p1", "p2", "p3", "p4" })
            pai = h.Db.Insert(new Item { DriveId = FakeGraph.DriveId, ParentLid = pai, Name = nome, IsDir = true, ItemId = "id-" + nome });
        h.Db.Insert(new Item { DriveId = FakeGraph.DriveId, ParentLid = pai, Name = "vizinho.txt", Size = 1, ItemId = "id-viz", CTag = "c" });
        Assert.Equal(0, h.Fs.Create(h.P("p1/p2/p3/p4/grande.bin"), OpenFlags.RdWr, out var fh));
        var vizinho = h.P("p1/p2/p3/p4/vizinho.txt");
        Assert.NotNull(h.Fs.Resolve(vizinho));
        var tid = Environment.CurrentManagedThreadId;
        using var sql = new Sql(h.Db, () => Environment.CurrentManagedThreadId == tid);
        const int writes = 200;
        var bloco = new byte[4096];
        var versao0 = h.Db.ItemsVersion;
        long selectsResolve = 0;
        for (var i = 0; i < writes; i++)
        {
            Assert.Equal(bloco.Length, h.Fs.Write(fh, bloco, (long)i * bloco.Length));
            sql.Reset();
            Assert.NotNull(h.Fs.Resolve(vizinho));
            selectsResolve += sql.Select + sql.Cte;
        }
        var subidas = h.Db.ItemsVersion - versao0;
        Assert.Equal(0, h.Fs.Release(fh));
        var porResolve = selectsResolve / (double)writes;
        output.WriteLine($"[24.5] {writes} writes: ItemsVersion +{subidas}; Resolve do vizinho = {porResolve:F1} SELECT/chamada");
        Assert.True(subidas == 0 && porResolve <= 1,
            $"{writes} write(): ItemsVersion subiu {subidas} vezes (alvo 0) e cada Resolve de caminho já visto custou {porResolve:F1} SELECTs (alvo 1, hit)");
    }

    // ---------- §22.6 confirmação do upload reinvalida o inode ----------

    /// <summary>
    /// Arquivo gravado pela montagem (o kernel já tem o inode e as páginas). A confirmação do envio (Update fora de
    /// MutacaoLocal, muda cTag/tamanho) dispara ItemChanged com cTag diferente → KernelNotifications chama
    /// notify_inval_inode (condição de <c>Notificar</c>: <c>_inodes.Has(ino) &amp;&amp; antes.CTag != atual.CTag</c>).
    /// </summary>
    [Fact]
    public async Task R22_6_confirmacao_do_upload_nao_reinvalida_o_inode_do_arquivo_gravado_localmente()
    {
        using var h = new Harness();
        var invalidaria = new ConcurrentQueue<string>();
        long lid = 0;
        await Montado(h, (_, root) =>
        {
            h.Fs.ItemChanged += (antes, depois) =>
            {
                if (antes != null && depois != null && antes.Lid == Volatile.Read(ref lid) && antes.CTag != depois.CTag
                    && Inodes().Has((ulong)antes.Lid + 1))
                    invalidaria.Enqueue($"cTag {antes.CTag ?? "null"} → {depois.CTag}, size {antes.Size} → {depois.Size}, dirty {antes.Dirty} → {depois.Dirty}");
            };
            Assert.Equal(0, Sh("printf 'conteudo local' > novo.txt && stat novo.txt >/dev/null", root).code);
            Volatile.Write(ref lid, h.Fs.Resolve(h.P("novo.txt"))!.Value.Item!.Lid);
            Harness.WaitUntil(() => h.Db.Get(lid) is { ItemId: not null, Dirty: false } && h.Db.PendingOps() == 0, "envio confirmado");
            Thread.Sleep(300);
        });
        Assert.True(invalidaria.IsEmpty, "a confirmação do envio reinvalidou o inode do arquivo recém-gravado: " + string.Join(" | ", invalidaria));
    }

    // ---------- §24.6 SetExcluded sem ItemChanged ----------

    [Fact]
    public void R24_6_SetExcluded_emite_ItemChanged_para_a_pasta()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        var dir = Pasta(h, h.Drive.RootLid, "Escolhida", 2);
        var eventos = new ConcurrentQueue<long>();
        h.Fs.ItemChanged += (a, d) => eventos.Enqueue((a ?? d)!.Lid);
        Assert.Null(h.Engine.SetExcluded(dir, true));
        Assert.Contains(dir, eventos); // RED: o kernel só descobre pelo timeout (1 s)
    }

    // ---------- §24.7 readdirplus paginado × item recriado ----------

    /// <summary>
    /// Página 2 do readdirplus usa o lid do snapshot de offset 0 (item antigo). Se no meio o nome foi apagado e
    /// recriado (lid novo) e o kernel já fez lookup do novo, <c>ReaddirEntry(path, lidVelho)</c> faz Unbind do inode
    /// novo: getattr(inoNovo) passa a dar ENOENT. Teste direto na InodeTable com a mesma chamada do ReplyDir.
    /// </summary>
    [Fact]
    public void R24_7_readdirplus_com_lid_velho_do_snapshot_nao_desliga_o_inode_novo()
    {
        var t = new InodeTable(_ => null);
        t.Lookup("/d", 10);
        var novo = t.Lookup("/d/x", 21);       // lookup do item recriado
        t.ReaddirEntry("/d/x", 20, _ => true); // página seguinte do snapshot (lid 20, já apagado)
        Assert.Equal("/d/x", t.PathOf(novo));
    }

    // ---------- §24.8 lookup não-UTF-8 e touch na raiz ----------

    [Fact]
    public async Task R24_8_lookup_de_nome_nao_UTF8_inexistente_da_ENOENT()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        string r = "";
        await Montado(h, (_, root) => r = Py("print(E(lambda: os.stat(b'caf\\xe9.txt')))", root));
        Assert.Equal("ENOENT", r);
    }

    /// <summary>
    /// Caracterização (decisão de produto): utime/touch na raiz da drive responde 0 sem mudar a data. A raiz não tem
    /// data própria na nuvem para gravar, e recusar com EPERM quebraria <c>rsync -a</c>/<c>cp -a</c> de uma árvore
    /// inteira para a montagem (ambos ajustam a data do diretório de destino no fim e tratam a falha como erro).
    /// </summary>
    [Fact]
    public async Task R24_8_touch_na_raiz_da_drive_responde_sucesso_sem_mudar_a_data()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        string r = "";
        await Montado(h, (_, root) => r = Py(
            "antes = int(os.stat('.').st_mtime)\n" +
            "r = E(lambda: os.utime('.', (1000000000, 1000000000)))\n" +
            "print(r, int(os.stat('.').st_mtime) == antes)", root));
        Assert.Equal("OK True", r);
    }

    // ---------- §23.11 parciais da versão velha ----------

    /// <summary>
    /// .partial/.stream de uma versão (cTag) que não é mais a atual do item: DeleteOrphanPartials só olha drive+lid,
    /// então o arquivo da versão velha sobrevive até o próximo open do lid.
    /// </summary>
    [Fact]
    public void R23_11_DeleteOrphanPartials_apaga_parcial_de_versao_velha()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        var lid = h.Db.Insert(new Item { DriveId = FakeGraph.DriveId, ParentLid = h.Drive.RootLid, Name = "video.mp4", Size = 100, ItemId = "vid", CTag = "nova" });
        var velho = h.Engine.Cache.PartialPathFor(FakeGraph.DriveId, lid, "velha");
        var atual = h.Engine.Cache.PartialPathFor(FakeGraph.DriveId, lid, "nova");
        File.WriteAllBytes(velho, new byte[4096]);
        File.WriteAllBytes(atual, new byte[4096]);
        h.Engine.Cache.DeleteOrphanPartials(h.Db.Get); // a API recebe o item: a versão (cTag) entra na conta
        Assert.True(File.Exists(atual), "apagou o parcial da versão atual");
        Assert.False(File.Exists(velho), "parcial da versão velha (cTag diferente da atual) continua no disco");
    }

    /// <summary>
    /// Sessão de leitura por faixas de uma versão que a nuvem já trocou: ao fechar o último handle, o .stream/.blocks
    /// dela não serve para mais nada (o próximo open usa outra versão) e não pode ficar ocupando o cache até o
    /// próximo open do lid.
    /// </summary>
    [Fact]
    public void R23_11_fechar_a_sessao_de_versao_velha_apaga_os_blocos()
    {
        using var h = new Harness();
        const int mb = 1024 * 1024;
        h.Remote.RemoteCreateBytes("/", "muda.bin", StreamingTests.Bytes(16 * mb, 7));
        h.Settle();
        Assert.Equal(0, h.Fs.Open(h.P("/muda.bin"), 0, 0, out var fh));
        Assert.True(h.Fs.IsStreaming(fh));
        var buf = new byte[4096];
        Assert.Equal(4096, h.Fs.Read(fh, buf, 5 * mb));
        Assert.NotEmpty(Directory.EnumerateFiles(h.Engine.Cache.PartialDir, "*.stream"));
        h.Remote.RemoteUpdateBytes("/muda.bin", StreamingTests.Bytes(16 * mb, 8));
        h.Settle();
        Assert.Equal(0, h.Fs.Release(fh));
        Assert.Empty(Directory.EnumerateFiles(h.Engine.Cache.PartialDir));
    }

    /// <summary>
    /// Blocos descartados por hash divergente: sem direct_io (libfuse &lt; 3.17.3) o kernel pode ter guardado páginas
    /// deles. O descarte precisa virar ItemChanged com versão diferente (→ notify_inval_inode na montagem).
    /// </summary>
    [Fact]
    public void R23_11_descarte_de_blocos_corrompidos_avisa_o_kernel()
    {
        using var h = new Harness();
        const int mb = 1024 * 1024;
        var data = StreamingTests.Bytes(12 * mb, 4);
        h.Remote.RemoteCreateBytes("/", "corrompido.bin", data);
        h.Settle();
        var lid = h.Fs.Resolve(h.P("/corrompido.bin"))!.Value.Item!.Lid;
        var avisos = new ConcurrentQueue<string>();
        h.Fs.ItemChanged += (antes, depois) =>
        {
            if (antes?.Lid == lid && depois != null && antes.CTag != depois.CTag) avisos.Enqueue($"{antes.CTag} → {depois.CTag}");
        };
        h.Remote.CorruptNextDownloads = 1;
        Assert.Equal(0, h.Fs.Open(h.P("/corrompido.bin"), 0, 0, out var fh));
        try
        {
            var buf = new byte[128 * 1024];
            for (long off = 0; off < data.Length; off += buf.Length) Assert.True(h.Fs.Read(fh, buf, off) >= 0);
            Harness.WaitUntil(() => h.Engine.Activity.Entries.Any(e => e.Kind == CraftDriver.Core.Sync.ActivityKind.Error && e.Text.Contains("não confere")),
                "verificação final reprovada");
            Harness.WaitUntil(() => !avisos.IsEmpty, "aviso de conteúdo descartado para o kernel");
        }
        finally { h.Fs.Release(fh); }
    }

    // ---------- §23.12 ContentCache.DeleteDrive × sub-drives ----------

    /// <summary>ForgetDrive da hospedeira apaga os parciais da sub-drive (atalho)? (verde = hipótese refutada)</summary>
    [Fact]
    public async Task R23_12_ForgetDrive_apaga_parciais_das_sub_drives()
    {
        using var h = new Harness();
        var o = h.Remote.AddOwnerDrive("dono-r3");
        o.RemoteCreate("/", "Projeto", null, folder: true);
        o.RemoteCreate("/Projeto", "a.txt", "conteúdo A");
        o.Shares[o.FindByPath("/Projeto")!.Id] = true;
        h.Remote.AddShortcut("/", "Projeto do Dono", o, "/Projeto");
        h.Settle();
        DriveSync? sub = null;
        Harness.WaitUntil(() => (sub = h.Engine.SharedDrives.FirstOrDefault(x => h.Db.Get(x.RootLid)?.Name == "Projeto do Dono")) != null, "sub-drive");
        await sub!.RunDeltaAsync(CancellationToken.None);
        var a = h.Db.GetChildren(sub.RootLid).Single(x => x.Name == "a.txt");
        Assert.Contains('|', a.DriveId);
        var parcial = h.Engine.Cache.PartialPathFor(a.DriveId, a.Lid, a.CTag);
        File.WriteAllBytes(parcial, new byte[10]);
        var conteudo = h.Engine.Cache.PathFor(a.DriveId, a.Lid);
        File.WriteAllBytes(conteudo, new byte[10]);
        h.Engine.ForgetDrive(FakeGraph.DriveId);
        Assert.False(File.Exists(parcial), "parcial da sub-drive sobreviveu ao ForgetDrive da hospedeira");
        Assert.False(File.Exists(conteudo), "conteúdo da sub-drive sobreviveu");
    }
}
