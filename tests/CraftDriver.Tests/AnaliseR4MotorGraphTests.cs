using System.Globalization;
using CraftDriver.Core.Graph;
using CraftDriver.Core.State;
using CraftDriver.Core.Sync;
using CraftDriver.Core.Vfs;

namespace CraftDriver.Tests;

/// <summary>
/// Análise rodada 4, frente A (motor/Graph): testes RED das pendências ainda abertas. Falham hoje pelo motivo descrito
/// no PENDENCIAS.md; nenhum corrige produção.
/// </summary>
public class AnaliseR4MotorGraphTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------- topo: craftdriver share em conta de trabalho ----------

    [Fact]
    public async Task Link_em_conta_de_trabalho_usa_escopo_da_organizacao_por_padrao()
    {
        using var h = new Harness();
        h.Config.Drives[0].Kind = "business"; // tenant que PERMITE anônimo: hoje o link sai "qualquer pessoa"
        var n = h.Remote.RemoteCreate("/", "contrato.pdf", "x");
        h.Settle();
        var lid = h.Fs.Resolve(h.P("contrato.pdf"))!.Value.Item!.Lid;
        var r = await new ItemActions(h.Engine, h.Fs).CreateShareLinkAsync(lid, edit: false, anyone: false, Ct);
        Assert.Equal([(n.Id, "view", "organization")], h.Remote.LinkRequests);
        Assert.Equal("organization", r.Scope);
    }

    // ---------- §29: pasta excluída e recriada com subpasta movida da antiga para a nova ----------

    [Fact]
    public void Subpasta_movida_da_pasta_excluida_para_a_recriada_com_o_mesmo_nome_nao_volta_vazia()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "Docs", null, folder: true);
        h.Remote.RemoteCreate("/Docs", "Sub", null, folder: true);
        h.Remote.RemoteCreate("/Docs/Sub", "f.txt", "conteúdo");
        h.Remote.RemoteCreate("/Docs", "antigo.txt", "a");
        h.Settle();
        Assert.Equal("conteúdo", h.ReadFile("/Docs/Sub/f.txt"));

        // outro dispositivo: cria pasta nova, leva Sub para ela, exclui a antiga e dá à nova o nome "Docs"
        h.Remote.RemoteCreate("/", "Temp", null, folder: true);
        h.Remote.RemoteMove("/Docs/Sub", "/Temp");
        h.Remote.RemoteDelete("/Docs");
        h.Remote.RemoteRename("/Temp", "Docs");
        // ordem que o OneDrive pode entregar: exclusão da antiga, a nova (ocupa o nome) e só depois Sub movida
        h.Remote.AfterDelta = (page, _) => Task.FromResult(page with
        {
            Items = page.Items.OrderBy(r => r.IsDeleted ? 0 : r.Name == "Sub" ? 2 : 1).ToList(),
        });
        h.Settle();
        h.Remote.AfterDelta = null;

        Assert.Null(h.Fs.Resolve(h.P("/Docs/antigo.txt")));
        Assert.NotNull(h.Fs.Resolve(h.P("/Docs/Sub")));
        Assert.True(h.Fs.Resolve(h.P("/Docs/Sub/f.txt")) != null, "a subpasta movida voltou vazia (a liberação do nome apagou a subárvore)");
        Assert.Equal("conteúdo", h.ReadFile("/Docs/Sub/f.txt"));
    }
}

/// <summary>Topo do PENDENCIAS: a retomada de envio em sessão não registra de onde retomou.</summary>
[Collection(UploadResumeTests.ChunkSizeCollection)]
public class AnaliseR4RetomadaLogTests
{
    const int MB = 1024 * 1024;
    const long Chunk = 320 * 1024 * 32;

    [Fact]
    public async Task Retomada_de_envio_registra_em_Debug_o_offset_retomado()
    {
        using var h = new Harness();
        var data = StreamingTests.Bytes(25 * MB, 71);
        h.Remote.RealUploadSessions = true;
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.BeforeUploadChunk = async (off, ct) =>
        {
            if (off < Chunk) return;
            held.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        Assert.Equal(0, h.Fs.Create(h.P("/grande.bin"), OpenFlags.RdWr, out var fh));
        for (var off = 0; off < data.Length; off += MB)
            Assert.Equal(MB, h.Fs.Write(fh, data.AsSpan(off, MB), off));
        Assert.Equal(0, h.Fs.Release(fh));
        Assert.True(await held.Task.WithinAsync(20_000), "o envio não chegou à 2ª parte");
        h.Engine.Stop();
        Harness.WaitUntil(() => !h.Drive.Busy, "envio interrompido");
        h.Remote.BeforeUploadChunk = null;
        Assert.Equal(Chunk, h.Remote.SessionBytesReceived);

        var logFile = Path.Combine(h.Dir, "retomada.log");
        using var engine = new SyncEngine(h.Config, h.Db, new ContentCache(h.Config.CacheDir), new ActivityLog(logFile), _ => h.Remote);
        engine.Start();
        Harness.WaitUntil(() => h.Db.PendingOps() == 0 && engine.GetDrive(FakeGraph.DriveId) is { Busy: false }, "fila vazia após reiniciar", 30_000);
        engine.Stop();
        Assert.Equal(1, h.Remote.SessionsCreated); // retomou a MESMA sessão (o comportamento já está certo)

        var debug = File.Exists(logFile) ? File.ReadAllLines(logFile).Where(l => l.Contains("[DEBUG]")).ToList() : [];
        var offset = Chunk.ToString(CultureInfo.InvariantCulture);
        Assert.True(debug.Any(l => l.Contains("grande.bin") && l.Contains(offset)),
            $"nenhuma linha DEBUG com o offset retomado ({offset}) de grande.bin; DEBUG: [{string.Join(" | ", debug)}]");
    }
}

/// <summary>§30(a): arquivo apagado dentro de uma pasta fixada com o app encerrado. Decisão do usuário: perguntar na
/// abertura se apaga também na nuvem; até a resposta, nuvem intacta e o arquivo não volta nas cópias.</summary>
public class AnaliseR4OfflineTests
{
    [Fact]
    public void Arquivo_apagado_offline_fica_pendente_ate_o_usuario_confirmar_ou_manter()
    {
        var mount = Path.Combine(Path.GetTempPath(), "craftdriver-offline-" + Guid.NewGuid().ToString("N")[..8], "OneDrive");
        string Real(string rel) => Path.Join(mount, Harness.Name, rel.TrimStart('/'));
        var h = new Harness();
        try
        {
            h.Remote.RemoteCreate("/", "Fixada", null, folder: true);
            h.Remote.RemoteCreate("/Fixada", "a.txt", "versão A");
            h.Remote.RemoteCreate("/Fixada", "b.txt", "versão B");
            h.Remote.RemoteCreate("/Fixada", "c.txt", "versão C");
            h.Settle();
            h.Engine.SetPinned(h.Fs.Resolve(h.P("/Fixada"))!.Value.Item!.Lid, true);
            Harness.WaitUntil(() => h.Db.PinnedNeedingDownload(FakeGraph.DriveId).Count == 0, "fixados baixados");

            // 1º encerramento: cópias na pasta real; o usuário apaga b.txt e c.txt com o app fechado
            OfflineCopies.Materialize(h.Engine, h.Fs, mount);
            h.Close();
            File.Delete(Real("/Fixada/b.txt"));
            File.Delete(Real("/Fixada/c.txt"));

            void Reabrir()
            {
                var remote = h.Remote;
                h = new Harness(null, h.Dir, remote, e => OfflineCopies.Import(e, _ => false));
                h.Settle();
            }

            Reabrir();
            Assert.Equal("versão B", h.Remote.ReadText("/Fixada/b.txt")); // nada apagado na nuvem sem confirmação
            Assert.Equal("versão C", h.Remote.ReadText("/Fixada/c.txt"));
            Assert.Equal("versão B", h.ReadFile("/Fixada/b.txt")); // continua na montagem
            var pend = OfflineDeletions.Pending(h.Engine);
            Assert.Equal(["/" + Harness.Name + "/Fixada/b.txt", "/" + Harness.Name + "/Fixada/c.txt"], pend.Select(p => p.Path));

            // encerra sem responder: as pendentes não voltam nas cópias
            OfflineCopies.Materialize(h.Engine, h.Fs, mount);
            Assert.True(File.Exists(Real("/Fixada/a.txt")), "a pasta deixou de ser fixada");
            Assert.False(File.Exists(Real("/Fixada/b.txt")), "b.txt, apagado com o app fechado, voltou nas cópias offline");
            Assert.False(File.Exists(Real("/Fixada/c.txt")), "c.txt, apagado com o app fechado, voltou nas cópias offline");
            h.Close();

            // abre de novo: a pergunta continua; o usuário apaga b.txt e mantém c.txt
            Reabrir();
            pend = OfflineDeletions.Pending(h.Engine);
            Assert.Equal(2, pend.Count);
            var b = pend.Single(p => p.Path.EndsWith("/b.txt", StringComparison.Ordinal)).Lid;
            var c = pend.Single(p => p.Path.EndsWith("/c.txt", StringComparison.Ordinal)).Lid;
            Assert.Equal(1, OfflineDeletions.ConfirmDelete(h.Engine, [b]));
            OfflineDeletions.Keep(h.Engine, [c]);
            h.Settle();
            Assert.Null(h.Remote.ReadText("/Fixada/b.txt")); // foi para a Lixeira
            Assert.Null(h.Fs.Resolve(h.P("/Fixada/b.txt")));
            Assert.Equal("versão C", h.Remote.ReadText("/Fixada/c.txt"));
            Assert.Empty(OfflineDeletions.Pending(h.Engine));

            OfflineCopies.Materialize(h.Engine, h.Fs, mount);
            Assert.Equal("versão C", File.ReadAllText(Real("/Fixada/c.txt"))); // mantido: volta nas cópias
            Assert.False(File.Exists(Real("/Fixada/b.txt")));
        }
        finally
        {
            h.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(mount)!, true); } catch { }
        }
    }
}
