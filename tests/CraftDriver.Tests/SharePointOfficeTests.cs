using System.Net;
using System.Text;
using CraftDriver.Core.Graph;
using CraftDriver.Core.Vfs;

namespace CraftDriver.Tests;

/// <summary>
/// SharePoint × documentos do Office: substituir um .xlsx por sessão com <c>fileSystemInfo</c> falha na biblioteca real
/// (404 na 1ª parte + eTag avançado) e virava "conflito de edição" a cada salvamento sem ninguém mais editar.
/// </summary>
public sealed class SharePointOfficeTests
{
    [Fact]
    public void Editar_planilha_do_Office_no_SharePoint_nao_gera_conflito()
    {
        using var h = new Harness();
        h.Remote.RealUploadSessions = true;
        h.Remote.SharePointOfficeReplaceRejectsDate = true;
        var v1 = StreamingTests.Bytes(5 * 1024 * 1024, 3);
        Assert.Equal(0, h.Fs.Create(h.P("Detalhes da Venda.xlsx"), OpenFlags.RdWr, out var fh));
        Assert.Equal(v1.Length, h.Fs.Write(fh, v1, 0));
        Assert.Equal(0, h.Fs.Release(fh));
        Harness.WaitUntil(() => h.Remote.FindByPath("/Detalhes da Venda.xlsx") != null && h.Db.PendingOps() == 0 && !h.Drive.Busy, "1º envio", 30_000);

        // salvamento do editor (2×, como o salvamento automático)
        for (byte rodada = 4; rodada <= 5; rodada++)
        {
            var v = StreamingTests.Bytes(5 * 1024 * 1024, rodada);
            Assert.Equal(0, h.Fs.Open(h.P("Detalhes da Venda.xlsx"), OpenFlags.RdWr, 0, out fh));
            Assert.Equal(v.Length, h.Fs.Write(fh, v, 0));
            Assert.Equal(0, h.Fs.Release(fh));
            Harness.WaitUntil(() => h.Db.PendingOps() == 0 && !h.Drive.Busy, $"envio da edição {rodada}", 30_000);
            Assert.True(h.Remote.FindByPath("/Detalhes da Venda.xlsx")?.Content.AsSpan().SequenceEqual(v) == true || h.Db.Conflicts().Count > 0,
                $"edição {rodada} não chegou à nuvem: nuvem=[{string.Join(", ", h.Remote.ListPaths())}], problemas=[{string.Join("; ", h.Db.Failures().Select(f => f.ToString()))}]");
            Assert.True(h.Db.Conflicts().Count == 0 && h.Remote.ListPaths().SequenceEqual(["/Detalhes da Venda.xlsx"]),
                $"conflito falso: nuvem=[{string.Join(", ", h.Remote.ListPaths())}], conflitos=[{string.Join("; ", h.Db.Conflicts().Select(c => c.Copy + ": " + c.Reason))}]");
        }
    }

    /// <summary>
    /// Delta lido DURANTE o envio traz o estado de antes dele (o eTag que foi no If-Match). Ele é adiado e reaplicado
    /// depois do commit — e voltava o eTag/cTag/tamanho do item para a versão anterior: a próxima edição ia com o
    /// eTag velho → 412 → cópia de conflito (visto no SharePoint com dois salvamentos seguidos do GridCraft).
    /// </summary>
    [Fact]
    public async Task Delta_velho_lido_durante_o_envio_nao_volta_o_eTag()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        h.WriteFile("/planilha.xlsx", "v1");
        await Task.Run(h.Settle, ct);
        var id = h.Remote.FindByPath("/planilha.xlsx")!.Id;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.BeforeUpload = async (name, token) =>
        {
            if (name != "planilha.xlsx") return;
            started.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        try
        {
            h.WriteFile("/planilha.xlsx", "v2 do salvamento", create: false);
            h.Engine.SyncNow();
            Assert.True(await started.Task.WithinAsync(6000), "envio não começou");
            var before = (await h.Remote.GetItemAsync(h.Remote.Id, id, ct))!; // estado da nuvem antes do commit
            h.Remote.AfterDelta = (page, _) => Task.FromResult(page with { Items = [.. page.Items, before] });
            Assert.True(await h.Drive.RunDeltaAsync(ct).WithinAsync(3000), "delta preso no envio");
            h.Remote.AfterDelta = null;
        }
        finally { release.TrySetResult(); h.Remote.BeforeUpload = null; }
        // sem rodar outro delta: um delta novo traria o estado atual e esconderia a regressão até o próximo salvamento
        Harness.WaitUntil(() => h.Db.PendingOps() == 0 && !h.Drive.Busy, "envio confirmado", 10_000);
        Assert.Equal("v2 do salvamento", h.Remote.ReadText("/planilha.xlsx"));
        Assert.Equal(h.Remote.FindByPath("/planilha.xlsx")!.ETag, h.Db.GetByRemote(h.Drive.DriveId, id)!.ETag);

        h.WriteFile("/planilha.xlsx", "v3 do salvamento seguinte", create: false);
        h.Engine.SyncNow();
        Harness.WaitUntil(() => h.Remote.ReadText("/planilha.xlsx") == "v3 do salvamento seguinte" || h.Db.Conflicts().Count > 0, "2º salvamento", 10_000);
        Harness.WaitUntil(() => h.Db.PendingOps() == 0 && !h.Drive.Busy, "fila vazia", 10_000);
        Assert.True(h.Db.Conflicts().Count == 0, $"conflito falso: [{string.Join("; ", h.Db.Conflicts().Select(c => c.Copy + ": " + c.Reason))}]");
        Assert.Equal("v3 do salvamento seguinte", h.Remote.ReadText("/planilha.xlsx"));
    }

    [Theory]
    [InlineData("\"{981F838D-C684-4EA9-B0A4-FB775E48E346},7\"", null, "\"{981F838D-C684-4EA9-B0A4-FB775E48E346},9\"", true)]  // intermediário do commit
    [InlineData("\"{981F838D-C684-4EA9-B0A4-FB775E48E346},10\"", null, "\"{981F838D-C684-4EA9-B0A4-FB775E48E346},9\"", false)] // mudou depois
    [InlineData("\"{11111111-C684-4EA9-B0A4-FB775E48E346},1\"", null, "\"{981F838D-C684-4EA9-B0A4-FB775E48E346},9\"", false)] // outro item
    [InlineData("aXYZ!12.1", "aXYZ!12.1", "aXYZ!12.2", true)]   // qualquer formato: o eTag do If-Match é o estado anterior
    [InlineData("aXYZ!12.3", "aXYZ!12.1", "aXYZ!12.2", false)]  // formato desconhecido: na dúvida reaplica (pode ser mais novo)
    public void Estado_anterior_ao_commit(string etag, string? sent, string committed, bool older) =>
        Assert.Equal(older, CraftDriver.Core.Sync.DriveSync.IsOlderState(etag, sent, committed));

    [Theory]
    [InlineData("Planilha.xlsx", "01ITEM", false)] // substituir documento do Office: sem a data (o SharePoint usa a do documento)
    [InlineData("Relatório.DOCX", "01ITEM", false)]
    [InlineData("Planilha.xlsx", null, true)]      // arquivo novo: a data vai na sessão (funciona no SharePoint)
    [InlineData("notas.txt", "01ITEM", true)]      // outros tipos: a data continua indo ao substituir
    [InlineData("xlsx", "01ITEM", true)]
    public async Task Sessao_leva_a_data_exceto_ao_substituir_documento_do_Office(string name, string? itemId, bool comData)
    {
        var bodies = new List<string>();
        var http = new HttpClient(new Respond(async (req, ct) =>
        {
            if (req.Method == HttpMethod.Post)
            {
                bodies.Add(await req.Content!.ReadAsStringAsync(ct));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"uploadUrl":"https://up.example/s"}""", Encoding.UTF8, "application/json") };
            }
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"id":"01ITEM","name":"x","size":3,"file":{}}""", Encoding.UTF8, "application/json") };
        }));
        var c = new GraphClient((_, _) => Task.FromResult("tk"), http);
        var f = Path.Combine(Path.GetTempPath(), "cd-office-" + Guid.NewGuid());
        File.WriteAllText(f, "abc");
        try
        {
            await c.UploadAsync("b!drv", "01PAI", name, itemId, itemId == null ? null : "\"e\"", f, DateTimeOffset.UtcNow, null, TestContext.Current.CancellationToken);
            Assert.Single(bodies);
            Assert.Equal(comData, bodies[0].Contains("fileSystemInfo", StringComparison.Ordinal));
            Assert.Contains(itemId != null ? "\"replace\"" : "\"fail\"", bodies[0]);
        }
        finally { File.Delete(f); }
    }

    sealed class Respond(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => f(r, ct);
    }
}
