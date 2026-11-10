using System.Net;
using System.Text;
using CraftDriver.Core.Config;
using CraftDriver.Core.Graph;
using CraftDriver.Core.State;
using CraftDriver.Core.Sync;
using CraftDriver.Core.Vfs;
using Microsoft.Data.Sqlite;
using static CraftDriver.Tests.TestSupport;

namespace CraftDriver.Tests;

/// <summary>
/// Regressões da §13 do PENDENCIAS.md — comportamento do motor e do cliente Graph.
/// </summary>
public class Rodada2MotorTests(ITestOutputHelper output)
{
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    static GraphClient Client(HttpMessageHandler h) => new((_, _) => Task.FromResult("tk"), new HttpClient(h));

    // ---------- 13.6 ----------

    [Fact]
    public async Task S13_6_mudanca_so_de_metadados_na_nuvem_nao_gera_copia_de_conflito()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "doc.txt", "v1");
        h.Settle();
        Assert.Equal("v1", h.ReadFile("/doc.txt"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.BeforeUpload = async (_, ct) => { started.TrySetResult(); await gate.Task.WaitAsync(ct); };
        h.WriteFile("/doc.txt", "v2 local", create: false);
        h.Engine.SyncNow();
        Assert.True(await started.Task.WithinAsync(15000), "envio não começou");
        h.Remote.FindByPath("/doc.txt")!.MetaVersion++; // ex.: propriedade/descrição alterada: eTag muda, cTag não
        h.Remote.BeforeUpload = null;
        gate.TrySetResult();
        await Task.Run(h.Settle, Ct);
        Assert.True(h.Db.Conflicts().Count == 0 && h.Remote.ReadText("/doc.txt") == "v2 local",
            $"só metadados mudaram na nuvem, mas: conflitos={h.Db.Conflicts().Count}, nuvem='{h.Remote.ReadText("/doc.txt")}', " +
            $"arquivos={string.Join(", ", h.Remote.ListPaths())}");
    }

    // ---------- 13.7 ----------

    [Fact]
    public async Task S13_7_mesmo_arquivo_criado_nos_dois_lados_com_mesmo_conteudo_adota_o_remoto()
    {
        using var h = new Harness();
        var once = 0;
        h.Remote.BeforeUpload = (name, _) =>
        {
            if (name == "igual.txt" && Interlocked.Exchange(ref once, 1) == 0)
                h.Remote.RemoteCreate("/", "igual.txt", "mesmo conteúdo"); // outro dispositivo enviou antes
            return Task.CompletedTask;
        };
        h.WriteFile("/igual.txt", "mesmo conteúdo");
        await Task.Run(h.Settle, Ct);
        Assert.True(h.Db.Conflicts().Count == 0,
            $"conteúdo idêntico (mesmo hash) virou conflito: {string.Join(", ", h.Remote.ListPaths())}");
    }

    // ---------- 13.8 ----------

    [Fact]
    public void S13_8_RetryFailure_preserva_is_dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "r2-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = new StateDb(Path.Combine(dir, "s.db"));
            db.Enqueue("d", OpKind.Delete, 0, "rid", "pai", "Pasta", isDir: true);
            var op = db.PeekOp("d")!;
            Assert.True(op.IsDir);
            db.FailPermanently(op, "recusado");
            db.RetryFailure(db.Failures("d").Single().Id);
            Assert.True(db.PeekOp("d")!.IsDir, "exclusão de PASTA voltou de Problemas como exclusão de arquivo (is_dir=0)");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---------- 13.9 ----------

    [Fact]
    public void S13_9_arquivo_sumido_do_cache_nao_e_falta_de_conexao()
    {
        var path = Path.Combine(Path.GetTempPath(), "nao-existe-" + Guid.NewGuid().ToString("N"));
        var fnf = Record.Exception(() => File.OpenRead(path).Dispose())!;
        Assert.IsType<FileNotFoundException>(fnf);
        output.WriteLine($"FileNotFoundException.HResult={fnf.HResult}");
        var dnf = Record.Exception(() => File.OpenRead(Path.Combine(path, "x")).Dispose())!;
        Assert.False(Errors.IsNetworkError(fnf, Ct) || Errors.IsNetworkError(dnf, Ct) || Errors.ErrnoForDownload(fnf) == Errno.EHOSTUNREACH,
            $"ENOENT local classificado como rede: IsNetworkError(FNF)={Errors.IsNetworkError(fnf, Ct)}, " +
            $"IsNetworkError({dnf.GetType().Name})={Errors.IsNetworkError(dnf, Ct)}, ErrnoForDownload(FNF)={Errors.ErrnoForDownload(fnf)} (EHOSTUNREACH={Errno.EHOSTUNREACH})");
    }

    [Fact]
    public void S13_9_EACCES_do_cache_ja_e_erro_local()
    {
        // contraprova da parte "EACCES" do item: o .NET lança UnauthorizedAccessException, que já é erro local
        RealFuse.RequireNonRoot();
        var path = Path.Combine(Path.GetTempPath(), "sem-acesso-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "x");
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            var ex = Record.Exception(() => File.OpenRead(path).Dispose())!;
            Assert.IsType<UnauthorizedAccessException>(ex);
            Assert.False(Errors.IsNetworkError(ex, Ct));
            Assert.Equal(Errno.EIO, Errors.ErrnoForDownload(ex));
        }
        finally { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); File.Delete(path); }
    }

    // ---------- 13.10 ----------

    [Fact]
    public async Task S13_10_restoreVersion_nao_e_reenviado_apos_queda()
    {
        var posts = 0;
        var api = Client(new Handler((r, _) =>
        {
            if (r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/restoreVersion"))
            {
                // o servidor processou e a resposta se perdeu
                if (Interlocked.Increment(ref posts) == 1) throw new HttpRequestException("conexão caiu (simulado)");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));
        try { await api.RestoreVersionAsync("d", "item", "3.0", Ct); } catch (HttpRequestException) { }
        Assert.True(posts == 1, $"POST restoreVersion (não idempotente) enviado {posts}× — cada um cria uma versão nova");
    }

    [Fact]
    public async Task S13_10_PUT_de_arquivo_vazio_com_fail_reenviado_da_409()
    {
        var puts = 0;
        var api = Client(new Handler((r, _) =>
        {
            if (r.Method == HttpMethod.Put)
            {
                if (Interlocked.Increment(ref puts) == 1) throw new HttpRequestException("conexão caiu depois de gravar (simulado)");
                return Task.FromResult(Json("{\"error\":{\"code\":\"nameAlreadyExists\",\"message\":\"já existe\"}}", HttpStatusCode.Conflict));
            }
            return Task.FromResult(Json("{\"id\":\"novo\",\"name\":\"vazio.txt\",\"size\":0}"));
        }));
        var file = Path.Combine(Path.GetTempPath(), "vazio-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(file, []);
        try
        {
            var ex = await Record.ExceptionAsync(() => api.UploadAsync("d", "root", "vazio.txt", null, null, file, DateTimeOffset.UtcNow, null, Ct));
            Assert.True(ex == null, $"o 1º PUT criou o arquivo; o reenvio automático recebeu {ex?.Message} ({puts} PUTs) — o motor transforma isso em cópia de conflito");
        }
        finally { File.Delete(file); }
    }

    // ---------- 13.11 ----------

    [Fact]
    public async Task S13_11_permissoes_paginadas_sao_lidas_ate_o_fim()
    {
        var api = Client(new Handler((r, _) => Task.FromResult(r.RequestUri!.Query.Contains("pagina2")
            ? Json("{\"value\":[{\"roles\":[\"write\"]}]}")
            : Json("{\"value\":[{\"roles\":[\"read\"]}],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/drives/d/items/i/permissions?pagina2=1\"}"))));
        Assert.True(await api.CanWriteAsync("d", "i", Ct), "a permissão de escrita na 2ª página foi ignorada (sem paginação)");
    }

    [Theory]
    [InlineData("sp.owner")]
    [InlineData("sp.member")]
    public async Task S13_11_papeis_SharePoint_de_escrita(string role)
    {
        // HIPÓTESE: nomes dos papéis como a Graph os devolveria em bibliotecas do SharePoint (não visto em conta real)
        var api = Client(new Handler((_, _) => Task.FromResult(Json($"{{\"value\":[{{\"roles\":[\"{role}\"]}}]}}"))));
        Assert.True(await api.CanWriteAsync("d", "i", Ct), $"papel '{role}' tratado como somente leitura");
    }

    // ---------- 13.12 ----------

    [Fact]
    public async Task S13_12_anyone_recusado_mostra_o_erro_do_pedido_sem_tentar_organizacao()
    {
        using var h = new Harness(c => c.Drives[0].Kind = "business");
        h.Remote.RemoteCreate("/", "doc.txt", "x");
        h.Settle();
        h.Remote.LinkError = scope => scope == "anonymous"
            ? new GraphException(HttpStatusCode.BadRequest, "invalidRequest", "Data de expiração inválida (primeiro pedido)")
            : new GraphException(HttpStatusCode.Forbidden, "accessDenied", "Escopo organization indisponível (segundo pedido)");
        var item = h.Fs.Resolve(h.P("doc.txt"))!.Value.Item!;
        var ex = await Assert.ThrowsAsync<ItemActionException>(() => new ItemActions(h.Engine, h.Fs).CreateShareLinkAsync(item.Lid, false, anyone: true, Ct));
        // decisão do usuário (rodada 4): com --anyone não se troca de escopo em silêncio — nem uma 2ª tentativa
        Assert.Contains("Data de expiração inválida", ex.Message);
        Assert.DoesNotContain("Escopo organization indisponível", ex.Message);
        Assert.Equal(new[] { "anonymous" }, h.Remote.LinkRequests.Select(x => x.Scope));
    }

    // ---------- 13.13 ----------

    [Fact]
    public async Task S13_13_exclusao_offline_nao_apaga_versao_editada_em_outro_dispositivo()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "rel.txt", "v1");
        h.Settle();
        h.Remote.OfflineMode = true;
        Assert.Equal(0, h.Fs.Unlink(h.P("/rel.txt")));     // excluído aqui, sem conexão
        h.Remote.RemoteUpdate("/rel.txt", "v2 de outro PC"); // enquanto isso, editado em outro dispositivo
        h.Remote.OfflineMode = false;
        h.Engine.SyncNow();
        await Task.Run(() => Harness.WaitUntil(() => h.Db.PendingOps() == 0 && !h.Drive.Busy, "fila vazia", 20000), Ct);
        Assert.True(h.Remote.ReadText("/rel.txt") == "v2 de outro PC",
            "DELETE sem If-Match apagou a versão editada em outro dispositivo (foi para a Lixeira)");
    }
}

/// <summary>Regressões da §17 do PENDENCIAS.md — esquema e estado.</summary>
public class Rodada2EsquemaTests
{
    static string NewDir() => Path.Combine(Path.GetTempPath(), "r2e-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void S17_migracao_que_falha_por_outro_motivo_nao_pode_ser_engolida()
    {
        RealFuse.RequireNonRoot(); // root ignora o modo só-leitura do arquivo
        var dir = NewDir();
        var path = Path.Combine(dir, "s.db");
        try
        {
            new StateDb(path).Dispose();
            // banco de uma versão antiga (sem ops.is_dir e items.mode) num disco/arquivo só-leitura
            using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "ALTER TABLE ops DROP COLUMN is_dir; ALTER TABLE items DROP COLUMN mode; PRAGMA wal_checkpoint(TRUNCATE);";
                cmd.ExecuteNonQuery();
            }
            File.SetUnixFileMode(path, UnixFileMode.UserRead);
            StateDb? db = null;
            var open = Record.Exception(() => db = new StateDb(path));
            try
            {
                var later = open == null ? Record.Exception(() => db!.PeekOp("d")) : null;
                Assert.True(open != null,
                    $"a migração falhou (banco só-leitura) e o erro foi engolido como \"já existe\"; o primeiro uso quebra depois: {later?.Message}");
            }
            finally { db?.Dispose(); }
        }
        finally
        {
            if (File.Exists(path)) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void S17_remover_drive_apaga_conflitos_lixeira_e_excluidas()
    {
        var dir = NewDir();
        try
        {
            using var db = new StateDb(Path.Combine(dir, "s.db"));
            var lid = db.Insert(new Item { DriveId = "d", ItemId = "x", Name = "pasta", IsDir = true });
            db.AddConflict("d", lid, "a.txt", "a (conflito).txt", "teste");
            db.AddTrash("d", "rid", "apagado.txt", false);
            db.SetExcluded("d", lid, true);
            db.DeleteDriveData("d");
            Assert.True(db.Conflicts().Count == 0 && db.Trash().Count == 0 && !db.IsExcluded(lid),
                $"drive removida deixou: conflitos={db.Conflicts().Count}, lixeira={db.Trash().Count}, excluída em memória={db.IsExcluded(lid)}");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void S17_indices_das_consultas_frequentes()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "s.db");
        try
        {
            new StateDb(path).Dispose();
            using var c = new SqliteConnection($"Data Source={path};Pooling=False");
            c.Open();
            string Plan(string sql)
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
                using var r = cmd.ExecuteReader();
                var sb = new StringBuilder();
                while (r.Read()) sb.Append(r.GetString(3)).Append("; ");
                return sb.ToString();
            }
            // No LRU, SCAN USING INDEX é justamente o plano desejado (percorre a ordem do índice parcial); SCAN da
            // tabela + TEMP B-TREE é o problema. Exige o índice previsto em cada consulta, não apenas a palavra SEARCH.
            var queries = new[]
            {
                ("SELECT 1 FROM ops WHERE lid=1 AND kind='upload'", "ix_ops_lid"),
                ("SELECT 1 FROM items WHERE drive_id='d'", "ix_items_drive"),
                ("SELECT 1 FROM items WHERE pinned=1", "ix_items_pinned"),
                ("SELECT 1 FROM items WHERE cached_ctag IS NOT NULL ORDER BY last_access", "ix_items_lru"),
                ("SELECT 1 FROM failed_ops WHERE drive_id='d'", "ix_failed_drive"),
                ("SELECT 1 FROM conflicts WHERE drive_id='d'", "ix_conflicts_drive"),
                ("SELECT 1 FROM excluded WHERE drive_id='d'", "ix_excluded_drive"),
            };
            foreach (var (query, index) in queries)
            {
                var plan = Plan(query);
                Assert.True(plan.Contains(index, StringComparison.Ordinal), $"índice {index} ausente no plano: {query} → {plan}");
                Assert.DoesNotContain("USE TEMP B-TREE", plan);
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void S17_FailPermanently_de_exclusao_lid_0_nao_duplica()
    {
        var dir = NewDir();
        try
        {
            using var db = new StateDb(Path.Combine(dir, "s.db"));
            for (var i = 0; i < 2; i++)
            {
                db.Enqueue("d", OpKind.Delete, 0, "rid", null, "x.txt");
                db.FailPermanently(db.PeekOp("d")!, "recusado");
            }
            Assert.True(db.Failures("d").Count == 1, $"mesma exclusão (lid 0, rid) registrada {db.Failures("d").Count}× em Problemas");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
