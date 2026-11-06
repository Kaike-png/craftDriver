using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using CraftDriver.Core.Config;
using CraftDriver.Core.Graph;
using CraftDriver.Core.State;
using CraftDriver.Core.Sync;
using CraftDriver.Core.Vfs;
using Microsoft.Data.Sqlite;
using static CraftDriver.Tests.TestSupport;

namespace CraftDriver.Tests;

/// <summary>
/// Reproduções (RED) da rodada 3 do PENDENCIAS.md, frente A (fila/exclusões do motor): §22.2, §22.3, §23.1, §23.2,
/// §23.3, §23.6, §23.14, §23.15. Nenhum destes testes corrige produção: cada um falha hoje pelo motivo do item.
/// </summary>
public class Rodada3MotorFilaTests(ITestOutputHelper output)
{
    /// <summary>Graph falsa interceptável (o FakeGraph não tem gancho para DeleteAsync/MoveAsync).</summary>
    public class GraphProxy : DispatchProxy
    {
        public IGraphApi Target = null!;
        /// <summary>Retorna não-null para substituir a chamada (ex.: Task com exceção, Task presa num portão).</summary>
        public Func<MethodInfo, object?[]?, object?>? Intercept;

        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            if (Intercept?.Invoke(m!, args) is { } r) return r;
            try { return m!.Invoke(Target, args); }
            catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
        }

        public object? Forward(MethodInfo m, object?[]? args) => m.Invoke(Target, args);
    }

    /// <summary>Igual ao <see cref="Harness"/>, mas o motor fala com um <see cref="GraphProxy"/> sobre o FakeGraph.</summary>
    sealed class ProxyHarness : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "craftdriver-test-" + Guid.NewGuid().ToString("N")[..8]);
        public readonly FakeGraph Remote = new();
        public readonly GraphProxy Proxy;
        public readonly StateDb Db;
        public readonly SyncEngine Engine;
        public readonly VirtualFs Fs;
        public string DbPath => Path.Combine(Dir, "state.db");

        public ProxyHarness(Action<AppConfig>? configure = null)
        {
            Directory.CreateDirectory(Dir);
            var config = new AppConfig
            {
                UploadSettleMs = 1000, CacheDir = Path.Combine(Dir, "cache"), PollIntervalSeconds = 3600,
                Accounts = [new AccountConfig { Id = "acc", Username = "teste@exemplo.com" }],
                Drives = [new DriveConfig { DriveId = FakeGraph.DriveId, AccountId = "acc", DisplayName = Harness.Name }],
            };
            configure?.Invoke(config);
            var proxy = DispatchProxy.Create<IGraphApi, GraphProxy>();
            Proxy = (GraphProxy)(object)proxy;
            Proxy.Target = Remote;
            Db = new StateDb(DbPath);
            Engine = new SyncEngine(config, Db, new ContentCache(config.CacheDir), new ActivityLog(Path.Combine(Dir, "log.txt")), _ => proxy);
            Fs = new VirtualFs(Engine);
            Engine.Start();
            Harness.WaitUntil(() => Engine.GetDrive(FakeGraph.DriveId)?.LastSync != null, "carga inicial");
        }

        public DriveSync Drive => Engine.GetDrive(FakeGraph.DriveId)!;
        public string P(string rel) => "/" + Harness.Name + (rel.StartsWith('/') ? rel : "/" + rel);

        public void Settle()
        {
            Engine.SyncNow();
            Harness.WaitUntil(() => Db.PendingOps() == 0 && !Drive.Busy, "fila vazia");
            Drive.RunDeltaAsync(CancellationToken.None).GetAwaiter().GetResult();
            Harness.WaitUntil(() => Db.PendingOps() == 0 && !Drive.Busy, "fila vazia (2)");
        }

        public void WriteFile(string rel, string text)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            Assert.Equal(0, Fs.Resolve(P(rel)) == null
                ? Fs.Create(P(rel), OpenFlags.RdWr, out var fh)
                : Fs.Open(P(rel), OpenFlags.RdWr | OpenFlags.Trunc, 0, out fh));
            Assert.Equal(bytes.Length, Fs.Write(fh, bytes, 0));
            Assert.Equal(0, Fs.Release(fh));
        }

        public void Dispose()
        {
            Engine.Stop();
            Db.Dispose();
            try { Directory.Delete(Dir, true); } catch { }
        }
    }

    static void Sql(string dbPath, string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static string OpsDump(string dbPath)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,kind,lid,remote_id,parent_remote,name,attempts,state,last_error FROM ops ORDER BY id";
        using var r = cmd.ExecuteReader();
        var s = new List<string>();
        while (r.Read())
            s.Add($"#{r.GetInt64(0)} {r.GetString(1)} lid={r.GetInt64(2)} rid={(r.IsDBNull(3) ? "-" : r.GetString(3))} " +
                  $"pai={(r.IsDBNull(4) ? "NULL" : r.GetString(4))} nome={(r.IsDBNull(5) ? "-" : r.GetString(5))} " +
                  $"tent={r.GetInt64(6)} {r.GetString(7)} {(r.IsDBNull(8) ? "" : r.GetString(8))}");
        return s.Count == 0 ? "(fila vazia)" : string.Join(" | ", s);
    }

    // ---------- §23.1 (CommitUploadAsync: Enqueue(Delete) sem pai) ----------

    [Fact]
    public async Task S23_1_rm_durante_envio_e_recriar_igual_nao_apaga_o_arquivo_novo()
    {
        using var h = new Harness();
        var first = 0;
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.AfterUpload = async (r, ct) =>
        {
            if (r.Name != "x.txt" || Interlocked.Increment(ref first) != 1) return;
            written.TrySetResult();
            await gate.Task.WaitAsync(ct); // envio 1 já gravou na nuvem; a resposta ainda não chegou
        };
        try
        {
            h.WriteFile("/x.txt", "mesmo conteúdo");
            h.Engine.SyncNow();
            Assert.True(await written.Task.WithinAsync(15000), "o envio 1 não chegou à nuvem");
            Assert.Equal(0, h.Fs.Unlink(h.P("/x.txt")));
            h.WriteFile("/x.txt", "mesmo conteúdo"); // recriado (envio 2 pendente, item sem id remoto)
            gate.TrySetResult();                      // commit do envio 1: Enqueue(Delete, 0, R, parent=null)
            await Task.Run(h.Settle, Ct);
            await Task.Run(h.Settle, Ct);
            var local = h.Fs.Resolve(h.P("/x.txt")) != null;
            var remote = h.Remote.ReadText("/x.txt");
            output.WriteLine($"local={local} nuvem={remote ?? "(ausente)"} lixeira={h.Db.Trash().Count} caminhos={string.Join(",", h.Remote.ListPaths())}");
            try { output.WriteLine(File.ReadAllText(Path.Combine(h.Dir, "log.txt"))); } catch (IOException) { }
            Assert.True(local && remote == "mesmo conteúdo",
                $"X recriado depois do rm deveria sobreviver: local existe={local}, nuvem='{remote ?? "(ausente)"}' — o envio 2 " +
                "adotou o id R (409 + mesmo conteúdo) sem esperar a exclusão sem parent_remote, que depois apagou R");
        }
        finally { gate.TrySetResult(); }
    }

    /// <summary>
    /// Mesma cena, mas o delta chega entre o commit do envio 1 e a reivindicação do envio 2: ApplyRemote vê R (sem linha
    /// local, com Delete pendente) e o adota no X novo por nome+conteúdo (TryAdoptSameContent) — caminho que a espera por
    /// nome na fila não cobre.
    /// </summary>
    [Fact]
    public async Task S23_1b_delta_entre_commit_e_envio_nao_adota_id_com_exclusao_pendente()
    {
        using var h = new Harness();
        var first = 0;
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.AfterUpload = async (r, ct) =>
        {
            if (r.Name != "x.txt" || Interlocked.Increment(ref first) != 1) return;
            written.TrySetResult();
            await gate.Task.WaitAsync(ct);
        };
        try
        {
            h.WriteFile("/x.txt", "mesmo conteúdo");
            h.Engine.SyncNow();
            Assert.True(await written.Task.WithinAsync(15000), "o envio 1 não chegou à nuvem");
            Assert.Equal(0, h.Fs.Unlink(h.P("/x.txt")));
            h.WriteFile("/x.txt", "mesmo conteúdo");
            gate.TrySetResult();
            Harness.WaitUntil(() => OpsDump(h.DbPath).Contains(" delete "), "commit do envio 1 (Delete enfileirado)");
            await Task.Delay(200, Ct);
            await h.Drive.RunDeltaAsync(Ct); // antes do envio 2 (UploadSettleMs = 1 s)
            var adopted = h.Fs.Resolve(h.P("/x.txt"))?.Item?.ItemId;
            var ops = OpsDump(h.DbPath);
            await Task.Run(h.Settle, Ct);
            await Task.Run(h.Settle, Ct);
            var local = h.Fs.Resolve(h.P("/x.txt")) != null;
            var remote = h.Remote.ReadText("/x.txt");
            output.WriteLine($"após o delta: X novo com id={adopted ?? "(nenhum)"}; fila: {ops}; final local={local} nuvem={remote ?? "(ausente)"}");
            Assert.True(local && remote == "mesmo conteúdo",
                $"X novo deveria sobreviver: local={local}, nuvem='{remote ?? "(ausente)"}'; o delta deu a ele o id {adopted ?? "(nenhum)"} " +
                $"que tinha exclusão pendente ({ops})");
        }
        finally { gate.TrySetResult(); }
    }

    // ---------- §22.3: VirtualFs.cs:1018 (mv de arquivo sujo sobre destino → Delete(src) sem pai) ----------

    [Fact]
    public async Task S22_3_mv_sujo_sobre_destino_e_recriar_origem_nao_apaga_a_origem_nova()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "rascunho.txt", "versão da nuvem");
        h.Remote.RemoteCreate("/", "doc.txt", "doc antigo");
        await Task.Run(h.Settle, Ct);
        h.WriteFile("/rascunho.txt", "edição", create: false);      // src fica Dirty → !CanMoveRemote
        Assert.Equal(0, h.Fs.Rename(h.P("/rascunho.txt"), h.P("/doc.txt"), 0)); // salvamento por cima: Delete(src, pai=null)
        var ops = OpsDump(h.DbPath);
        h.WriteFile("/rascunho.txt", "versão da nuvem");            // recriado com o conteúdo que a nuvem ainda tem
        await Task.Run(h.Settle, Ct);
        await Task.Run(h.Settle, Ct);
        var local = h.Fs.Resolve(h.P("/rascunho.txt")) != null;
        var remote = h.Remote.ReadText("/rascunho.txt");
        output.WriteLine($"fila após o mv: {ops}");
        Assert.True(local && remote == "versão da nuvem",
            $"rascunho.txt recriado deveria existir: local={local}, nuvem='{remote ?? "(ausente)"}'; fila após o mv: {ops}");
    }

    // ---------- §22.3: VirtualFs.cs:1041 (renomear para nome só local → Delete sem pai e com o nome NOVO) ----------

    [Fact]
    public async Task S22_3_renomear_para_temporario_e_recriar_nao_apaga_o_novo()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "nota.txt", "texto");
        await Task.Run(h.Settle, Ct);
        Assert.Equal(0, h.Fs.Rename(h.P("/nota.txt"), h.P("/nota.txt.tmp"), 0)); // vira só local: Delete(R) na nuvem
        var ops = OpsDump(h.DbPath);
        h.WriteFile("/nota.txt", "texto");
        await Task.Run(h.Settle, Ct);
        await Task.Run(h.Settle, Ct);
        var local = h.Fs.Resolve(h.P("/nota.txt")) != null;
        var remote = h.Remote.ReadText("/nota.txt");
        output.WriteLine($"fila após o rename: {ops}");
        Assert.True(local && remote == "texto",
            $"nota.txt recriado deveria existir: local={local}, nuvem='{remote ?? "(ausente)"}'; fila após o rename: {ops}");
    }

    // ---------- §23.2 ----------

    [Fact]
    public async Task S23_2_apagar_e_criar_com_maiusculas_fora_do_ASCII_nao_gera_copia_de_conflito()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "Ação.txt", "antigo");
        await Task.Run(h.Settle, Ct);
        Assert.Equal(0, h.Fs.Unlink(h.P("/Ação.txt")));
        h.WriteFile("/AÇÃO.txt", "novo");
        await Task.Run(h.Settle, Ct);
        await Task.Run(h.Settle, Ct);
        var paths = h.Remote.ListPaths();
        output.WriteLine(string.Join(", ", paths));
        Assert.True(!paths.Any(p => p.Contains("conflito")) && h.Db.Conflicts().Count == 0,
            $"rm 'Ação.txt' + criar 'AÇÃO.txt' (mesmo nome para a nuvem) gerou conflito: nuvem={string.Join(", ", paths)}; " +
            $"conflitos={h.Db.Conflicts().Count} — a espera por nome usa COLLATE NOCASE (só A–Z)");
    }

    // ---------- §23.3 ----------

    [Fact]
    public async Task S23_3_412_na_exclusao_nao_duplica_o_nome_ocupado_por_item_com_id()
    {
        using var h = new ProxyHarness();
        var idA = h.Remote.RemoteCreate("/", "a.txt", "versão 1").Id;
        h.Remote.RemoteCreate("/", "b.txt", "bê");
        await Task.Run(h.Settle, Ct);
        var root = h.Fs.Resolve(h.P("/a.txt"))!.Value.Item!.ParentLid;
        var moveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var moveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Proxy.Intercept = (m, args) => m.Name != nameof(IGraphApi.MoveAsync) ? null : ((Func<Task<RemoteItem>>)(async () =>
        {
            moveEntered.TrySetResult();
            await moveGate.Task;
            return await (Task<RemoteItem>)h.Proxy.Forward(m, args)!;
        }))();
        try
        {
            h.Drive.DeleteQuietMs = 200;
            Assert.Equal(0, h.Fs.Rename(h.P("/b.txt"), h.P("/a.txt"), 0)); // Delete(a, eTag v1) + Move(b → a.txt)
            h.Remote.RemoteUpdate("/a.txt", "versão 2 de outro dispositivo"); // a exclusão vai dar 412
            h.Engine.SyncNow();
            Assert.True(await moveEntered.Task.WithinAsync(15000), "o Move não começou; fila: " + OpsDump(h.DbPath));
            var sameName = h.Db.GetChildren(root).Where(i => i.Name == "a.txt").ToList();
            output.WriteLine($"filhos 'a.txt' da raiz: {sameName.Count}; problemas: {string.Join(" / ", h.Db.Failures().Select(f => f.Kind + ":" + f.Error))}");
            var cloudBack = h.Db.GetByRemote(FakeGraph.DriveId, idA);
            Assert.True(sameName.Count == 1 && cloudBack != null,
                $"versão da nuvem de volta localmente: '{cloudBack?.Name}'; depois do 412 na exclusão, a raiz tem {sameName.Count} linhas 'a.txt' ({string.Join(", ", sameName.Select(i => $"lid {i.Lid} id {i.ItemId}"))}) " +
                "— ApplyRemote reinseriu a versão da nuvem sem tratar o nome ocupado pelo item com id (b renomeado); readdir mostra duplicata");
            // decisão do usuário: a versão da nuvem fica com nome de conflito; o item local (b) fica com o nome original
            Assert.Contains("conflito", cloudBack!.Name);
            moveGate.TrySetResult();
            await Task.Run(h.Settle, Ct);
            await Task.Run(h.Settle, Ct);
            var paths = h.Remote.ListPaths();
            output.WriteLine($"nuvem: {string.Join(", ", paths)}; problemas: {string.Join(" / ", h.Db.Failures().Select(f => f.Kind + ":" + f.Error))}");
            // o Move de b espera o rename da versão da nuvem (que libera 'a.txt'); a exclusão recusada não segura o nome
            Assert.Equal("bê", h.Remote.ReadText("/a.txt"));
            Assert.Equal("versão 2 de outro dispositivo", h.Remote.ReadText("/" + h.Db.GetByRemote(FakeGraph.DriveId, idA)!.Name));
            Assert.Single(h.Db.GetChildren(root), i => i.Name == "a.txt");
            Assert.Single(h.Db.Conflicts());
        }
        finally { moveGate.TrySetResult(); }
    }

    [Fact]
    public async Task S23_3b_restaurar_exclusoes_com_Move_pendente_sobre_o_nome_nao_duplica()
    {
        using var h = new ProxyHarness(c => c.DeleteQuietMs = 600_000);
        var idA = h.Remote.RemoteCreate("/", "a.txt", "aaa").Id;
        h.Remote.RemoteCreate("/", "b.txt", "bê");
        await Task.Run(h.Settle, Ct);
        var root = h.Fs.Resolve(h.P("/a.txt"))!.Value.Item!.ParentLid;
        var moveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Proxy.Intercept = (m, args) => m.Name != nameof(IGraphApi.MoveAsync) ? null : ((Func<Task<RemoteItem>>)(async () =>
        {
            await moveGate.Task;
            return await (Task<RemoteItem>)h.Proxy.Forward(m, args)!;
        }))();
        try
        {
            Assert.Equal(0, h.Fs.Rename(h.P("/b.txt"), h.P("/a.txt"), 0)); // Delete(a) retido + Move(b → a.txt) esperando
            await h.Drive.RestoreDeletesAsync(Ct);                          // reinsere a.txt (id da nuvem)
            var sameName = h.Db.GetChildren(root).Where(i => i.Name == "a.txt").ToList();
            var cloudBack = h.Db.GetByRemote(FakeGraph.DriveId, idA);
            Assert.True(sameName.Count == 1 && cloudBack != null,
                $"item restaurado: '{cloudBack?.Name}'; depois de cancelar as exclusões, a raiz tem {sameName.Count} linhas 'a.txt' " +
                $"({string.Join(", ", sameName.Select(i => $"lid {i.Lid} id {i.ItemId}"))}) — readdir mostra duplicata");
            Assert.Contains("conflito", cloudBack!.Name); // a versão da nuvem cede o nome; b (mv local) fica com ele
            moveGate.TrySetResult();
            await Task.Run(h.Settle, Ct);
            output.WriteLine($"nuvem: {string.Join(", ", h.Remote.ListPaths())}");
            Assert.Equal("bê", h.Remote.ReadText("/a.txt"));
            Assert.Equal("aaa", h.Remote.ReadText("/" + h.Db.GetByRemote(FakeGraph.DriveId, idA)!.Name));
            Assert.Single(h.Db.GetChildren(root), i => i.Name == "a.txt");
        }
        finally { moveGate.TrySetResult(); }
    }

    // ---------- §22.2 ----------

    [Fact]
    public async Task S22_2_envio_com_500_perene_vai_para_Problemas_apos_teto_de_tentativas()
    {
        using var h = new Harness();
        h.Remote.UploadError = name => name == "perene.txt"
            ? new GraphException(HttpStatusCode.InternalServerError, "generalException", "erro interno (simulado)") : null;
        h.WriteFile("/perene.txt", "x");
        h.Engine.SyncNow();
        Harness.WaitUntil(() => OpsDump(h.DbPath).Contains("tent=1"), "primeira falha do envio");
        Sql(h.DbPath, "UPDATE ops SET attempts=50, next_try=0 WHERE kind='upload'");
        h.Drive.WakeOps();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 10000 && h.Db.Failures().Count == 0 && !OpsDump(h.DbPath).Contains("tent=51"))
            await Task.Delay(50, Ct);
        await Task.Delay(300, Ct);
        var ops = OpsDump(h.DbPath);
        output.WriteLine(ops);
        Assert.True(h.Db.Failures().Any(f => f.Kind == OpKind.Upload),
            $"após 51 tentativas com 500 o envio continua invisível na fila (sem Problemas): {ops}");
    }

    // ---------- §23.6 (+ §22.2) ----------

    /// <summary>
    /// Decisão do usuário (§23.6): a exclusão com 500 perene vai para Problemas após o teto; o envio do MESMO nome na
    /// mesma pasta continua esperando (sem cópia de conflito) até o Problema ser resolvido, sem travar as outras operações.
    /// Ao "tentar de novo" com a nuvem respondendo, a exclusão roda e o envio anda.
    /// </summary>
    [Fact]
    public async Task S23_6_exclusao_com_500_perene_vai_para_Problemas_e_o_envio_do_mesmo_nome_espera_ser_resolvida()
    {
        using var h = new ProxyHarness();
        h.Remote.RemoteCreate("/", "a.txt", "velho");
        await Task.Run(h.Settle, Ct);
        var failing = true;
        h.Proxy.Intercept = (m, _) => m.Name == nameof(IGraphApi.DeleteAsync) && Volatile.Read(ref failing)
            ? Task.FromException(new GraphException(HttpStatusCode.InternalServerError, "generalException", "erro interno (simulado)"))
            : null;
        h.Drive.DeleteQuietMs = 200;
        Assert.Equal(0, h.Fs.Unlink(h.P("/a.txt")));
        h.WriteFile("/a.txt", "novo"); // envio de item novo espera a exclusão do mesmo nome
        h.Engine.SyncNow();
        Harness.WaitUntil(() => OpsDump(h.DbPath).Contains("tent=1"), "primeira falha da exclusão");
        Sql(h.DbPath, "UPDATE ops SET attempts=50, next_try=0 WHERE kind='delete'");
        h.Drive.WakeOps();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 10000 && h.Db.Failures().Count == 0 && !OpsDump(h.DbPath).Contains("tent=51"))
            await Task.Delay(50, Ct);
        var failure = h.Db.Failures().FirstOrDefault(f => f.Kind == OpKind.Delete);
        Assert.True(failure != null, $"exclusão com 500 após 51 tentativas não aparece em Problemas: {OpsDump(h.DbPath)}");
        Assert.Contains("esgotadas", failure.Error);

        // outra operação anda enquanto o envio de a.txt espera
        h.WriteFile("/b.txt", "bê");
        h.Engine.SyncNow();
        Harness.WaitUntil(() => h.Remote.ReadText("/b.txt") == "bê", "b.txt enviado enquanto a.txt espera");
        await Task.Delay(1500, Ct); // > UploadSettleMs: o envio de a.txt já poderia ter ido
        var ops = OpsDump(h.DbPath);
        output.WriteLine(ops);
        Assert.True(ops.Contains(" upload ") && h.Remote.ReadText("/a.txt") == "velho" && h.Db.Conflicts().Count == 0
                    && !h.Remote.ListPaths().Any(p => p.Contains("conflito")),
            $"o envio de a.txt deveria continuar esperando a exclusão em Problemas, sem cópia de conflito: fila={ops}; " +
            $"nuvem={string.Join(", ", h.Remote.ListPaths())}; conflitos={h.Db.Conflicts().Count}");
        // o painel da fila mostra que a exclusão em Problemas segura o envio do mesmo nome (integração F1×F4)
        var w = h.Db.QueueWaits().FirstOrDefault(q => q.Kind == OpKind.Delete && q.InProblems);
        Assert.True(w is { Blocked: 1, Name: "a.txt" },
            $"painel não mostra a exclusão em Problemas segurando o envio: {string.Join("; ", h.Db.QueueWaits())}");

        // usuário resolve: "tentar de novo" com a nuvem respondendo → a exclusão roda e o envio anda
        Volatile.Write(ref failing, false);
        Assert.True(h.Drive.RetryFailure(failure.Id));
        await Task.Run(h.Settle, Ct);
        Assert.Equal("novo", h.Remote.ReadText("/a.txt"));
        Assert.NotNull(h.Fs.Resolve(h.P("/a.txt")));
        Assert.Empty(h.Db.Conflicts());
    }

    [Fact]
    public async Task S23_6_descartar_a_exclusao_em_Problemas_libera_o_envio_do_mesmo_nome()
    {
        using var h = new ProxyHarness();
        h.Remote.RemoteCreate("/", "a.txt", "velho");
        await Task.Run(h.Settle, Ct);
        h.Proxy.Intercept = (m, _) => m.Name == nameof(IGraphApi.DeleteAsync)
            ? Task.FromException(new GraphException(HttpStatusCode.InternalServerError, "generalException", "erro interno (simulado)"))
            : null;
        h.Drive.DeleteQuietMs = 200;
        Assert.Equal(0, h.Fs.Unlink(h.P("/a.txt")));
        h.WriteFile("/a.txt", "novo");
        h.Engine.SyncNow();
        Harness.WaitUntil(() => OpsDump(h.DbPath).Contains("tent=1"), "primeira falha da exclusão");
        Sql(h.DbPath, "UPDATE ops SET attempts=50, next_try=0 WHERE kind='delete'");
        h.Drive.WakeOps();
        Harness.WaitUntil(() => h.Db.Failures().Any(f => f.Kind == OpKind.Delete), "exclusão em Problemas");
        await Task.Delay(1500, Ct);
        Assert.Contains(" upload ", OpsDump(h.DbPath)); // ainda esperando
        h.Db.DismissFailure(h.Db.Failures().Single(f => f.Kind == OpKind.Delete).Id);
        // descartada: o nome deixa de estar retido — o envio sai da fila (a nuvem ainda tem o antigo: vira conflito)
        Harness.WaitUntil(() => !OpsDump(h.DbPath).Contains(" upload "), "envio liberado depois de descartar a exclusão");
        Assert.NotNull(h.Remote.ListPaths().FirstOrDefault(p => p.EndsWith(".txt") && p != "/a.txt"));
    }

    // ---------- §23.14 ----------

    [Fact]
    public void S23_14_refutado_backoff_acumula_no_maximo_uma_op_extra()
    {
        var dir = Path.Combine(Path.GetTempPath(), "craftdriver-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            using var db = new StateDb(Path.Combine(dir, "state.db"));
            db.Enqueue("d", OpKind.Upload, 7);
            var op = db.PeekOp("d")!;
            db.FailOp(op.Id, "500", TimeSpan.FromMinutes(5)); // em backoff (attempts=1)
            for (var i = 0; i < 5; i++) db.Enqueue("d", OpKind.Upload, 7); // 5 releases durante o backoff
            var count = db.PendingOps("d");
            Assert.True(count <= 2, $"5 releases durante o backoff deixaram {count} ops de envio do mesmo lid (esperado ≤ 2)");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---------- §23.15 ----------

    [Fact]
    public async Task S23_15_restaurar_exclusao_ja_feita_por_outro_dispositivo_registra_na_lixeira()
    {
        using var h = new Harness(c => c.DeleteQuietMs = 600_000);
        h.Remote.RemoteCreate("/", "a.txt", "conteúdo");
        await Task.Run(h.Settle, Ct);
        Assert.Equal(0, h.Fs.Unlink(h.P("/a.txt")));
        h.Remote.RemoteDelete("/a.txt"); // outro dispositivo apagou antes (GetItem → 404/null)
        await h.Drive.RestoreDeletesAsync(Ct);
        var trash = h.Db.Trash();
        output.WriteLine($"lixeira: {string.Join(", ", trash.Select(t => t.Name))}; pendentes: {h.Db.PendingOps()}");
        Assert.True(trash.Any(t => t.Name == "a.txt"),
            $"a.txt já estava excluído na nuvem: RestoreDeletesAsync ignorou o 404 sem registrar na Lixeira (lixeira={trash.Count}) e logou como restaurado");
    }

    // ---------- provas complementares das correções (frente F1) ----------

    /// <summary>§22.3 (SyncEngine Mkdir): pasta apagada enquanto o mkdir estava na rede e recriada com o mesmo nome.</summary>
    [Fact]
    public async Task S22_3_mkdir_em_voo_rmdir_e_recriar_nao_apaga_a_pasta_nova()
    {
        using var h = new ProxyHarness(c => c.DeleteQuietMs = 200);
        var first = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Proxy.Intercept = (m, args) => m.Name != nameof(IGraphApi.CreateFolderAsync) || Interlocked.Increment(ref first) != 1 ? null
            : ((Func<Task<RemoteItem>>)(async () =>
            {
                var r = await (Task<RemoteItem>)h.Proxy.Forward(m, args)!; // criada na nuvem; a resposta atrasa
                entered.TrySetResult();
                await gate.Task;
                return r;
            }))();
        try
        {
            Assert.Equal(0, h.Fs.Mkdir(h.P("/pasta")));
            h.Engine.SyncNow();
            Assert.True(await entered.Task.WithinAsync(15000), "o mkdir não chegou à nuvem");
            Assert.Equal(0, h.Fs.Rmdir(h.P("/pasta")));
            Assert.Equal(0, h.Fs.Mkdir(h.P("/pasta")));
            h.WriteFile("/pasta/dentro.txt", "conteúdo");
            gate.TrySetResult(); // commit do mkdir 1: Delete(0, R) — o mkdir 2 tem de esperar
            await Task.Run(h.Settle, Ct);
            await Task.Run(h.Settle, Ct);
            output.WriteLine($"nuvem: {string.Join(", ", h.Remote.ListPaths())}; problemas: {h.Db.Failures().Count}");
            Assert.Equal("conteúdo", h.Remote.ReadText("/pasta/dentro.txt"));
            Assert.NotNull(h.Fs.Resolve(h.P("/pasta/dentro.txt")));
        }
        finally { gate.TrySetResult(); }
    }

    /// <summary>§23.3 com o nome ocupado por um item NOVO (sem id): a versão da nuvem volta com nome de conflito e o novo sobe com o nome.</summary>
    [Fact]
    public async Task S23_3c_412_na_exclusao_com_arquivo_novo_no_nome_a_nuvem_cede_o_nome()
    {
        using var h = new ProxyHarness(c => c.DeleteQuietMs = 200);
        var idA = h.Remote.RemoteCreate("/", "a.txt", "versão 1").Id;
        await Task.Run(h.Settle, Ct);
        Assert.Equal(0, h.Fs.Unlink(h.P("/a.txt")));
        h.WriteFile("/a.txt", "novo");
        h.Remote.RemoteUpdate("/a.txt", "versão 2 de outro dispositivo"); // a exclusão vai dar 412
        await Task.Run(h.Settle, Ct);
        await Task.Run(h.Settle, Ct);
        var back = h.Db.GetByRemote(FakeGraph.DriveId, idA);
        output.WriteLine($"nuvem: {string.Join(", ", h.Remote.ListPaths())}; local da versão da nuvem: {back?.Name}");
        Assert.Equal("novo", h.Remote.ReadText("/a.txt"));
        Assert.NotNull(back);
        Assert.Contains("conflito", back.Name);
        Assert.Equal("versão 2 de outro dispositivo", h.Remote.ReadText("/" + back.Name));
        Assert.Equal("novo", ReadLocal(h, "/a.txt"));
    }

    static string ReadLocal(ProxyHarness h, string rel)
    {
        Assert.Equal(0, h.Fs.Open(h.P(rel), 0, 0, out var fh));
        try
        {
            var buf = new byte[4096];
            var n = h.Fs.Read(fh, buf, 0);
            return System.Text.Encoding.UTF8.GetString(buf, 0, n);
        }
        finally { h.Fs.Release(fh); }
    }

    /// <summary>§22.2: com o servidor atendendo os outros pedidos, o teto vale para todos os "transitórios" do item.</summary>
    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(401)]
    [InlineData(503)]
    public async Task S22_2_teto_vale_para_408_429_401_503_quando_o_servidor_atende_os_outros(int status)
    {
        using var h = new Harness();
        h.Remote.UploadError = name => name == "perene.txt"
            ? new GraphException((HttpStatusCode)status, "simulado", "falha transitória (simulada)") : null;
        h.WriteFile("/perene.txt", "x");
        h.Engine.SyncNow();
        Harness.WaitUntil(() => OpsDump(h.DbPath).Contains("tent=1"), "primeira falha do envio");
        Sql(h.DbPath, "UPDATE ops SET attempts=50, next_try=0 WHERE kind='upload'");
        h.Drive.WakeOps();
        Harness.WaitUntil(() => h.Db.Failures().Any(f => f.Kind == OpKind.Upload), "envio em Problemas após o teto");
        Assert.Contains("esgotadas", h.Db.Failures().Single().Error);
        Assert.Equal(0, h.Db.PendingOps());
    }

    /// <summary>§22.2: throttling/queda da conta inteira (nada responde há horas) não manda a fila para Problemas no teto.</summary>
    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(429)]
    [InlineData(401)]
    public async Task S22_2_sem_resposta_do_servidor_ha_horas_o_teto_nao_vale(int status)
    {
        using var h = new Harness();
        h.Remote.UploadError = name => name == "perene.txt"
            ? new GraphException((HttpStatusCode)status, "simulado", "falha transitória (simulada)") : null;
        typeof(DriveSync).GetProperty(nameof(DriveSync.LastSync))!.SetValue(h.Drive, DateTime.Now.AddHours(-5));
        h.WriteFile("/perene.txt", "x");
        h.Engine.SyncNow();
        Harness.WaitUntil(() => OpsDump(h.DbPath).Contains("tent=1"), "primeira falha do envio");
        typeof(DriveSync).GetProperty(nameof(DriveSync.LastSync))!.SetValue(h.Drive, DateTime.Now.AddHours(-5));
        Sql(h.DbPath, "UPDATE ops SET attempts=50, next_try=0 WHERE kind='upload'");
        h.Drive.WakeOps();
        Harness.WaitUntil(() => OpsDump(h.DbPath).Contains("tent=51"), "51ª tentativa");
        Assert.Empty(h.Db.Failures());
    }

    /// <summary>§23.5: o envio do filho apagado é cancelado quando a pasta é excluída (não fica preso nem sobe depois).</summary>
    [Fact]
    public async Task S23_5_rm_rf_cancela_o_envio_do_filho_apagado()
    {
        using var h = new Harness(c => c.DeleteQuietMs = 100);
        h.Remote.RemoteCreate("/", "pasta", null, folder: true);
        h.Settle();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.BeforeUpload = async (name, ct) =>
        {
            if (name != "grande.bin") return;
            entered.TrySetResult();
            await never.Task.WaitAsync(ct); // só sai por cancelamento
        };
        h.WriteFile("pasta/grande.bin", "GBs");
        Assert.True(await entered.Task.WithinAsync(10000), "envio não começou");
        Assert.Equal(0, h.Fs.Unlink(h.P("pasta/grande.bin")));
        Assert.Equal(0, h.Fs.Rmdir(h.P("pasta")));
        h.Engine.NotifyLocalChange(FakeGraph.DriveId);
        Harness.WaitUntil(() => h.Remote.FindByPath("/pasta") is not { Deleted: false }, "pasta excluída", 6000);
        Harness.WaitUntil(() => h.Db.PendingOps() == 0, "envio do filho apagado cancelado e fora da fila", 6000);
        Assert.False(h.Remote.FindByPath("/pasta/grande.bin") is { Deleted: false }, "o envio cancelado não pode deixar o filho na nuvem");
        Assert.Empty(h.Db.Failures());
    }
}
