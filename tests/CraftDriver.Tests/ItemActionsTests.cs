using System.Net;
using System.Text;
using CraftDriver.Core.Graph;
using CraftDriver.Core.Sync;

namespace CraftDriver.Tests;

/// <summary>Link de compartilhamento, versões anteriores e Lixeira (ItemActions) contra a Graph falsa.</summary>
public class ItemActionsTests
{
    static ItemActions Actions(Harness h) => new(h.Engine, h.Fs);
    static long Lid(Harness h, string rel) => h.Fs.Resolve(h.P(rel))!.Value.Item!.Lid;

    // ---------- link ----------

    [Fact]
    public async Task Link_pessoal_e_para_qualquer_pessoa()
    {
        using var h = new Harness();
        var n = h.Remote.RemoteCreate("/", "foto.jpg", "x");
        h.Settle();
        var r = await Actions(h).CreateShareLinkAsync(Lid(h, "foto.jpg"), edit: false, anyone: false, CancellationToken.None);
        Assert.Equal($"https://1drv.ms/fake/{n.Id}?view&anonymous", r.Url);
        Assert.Equal([(n.Id, "view", "anonymous")], h.Remote.LinkRequests);
    }

    [Fact]
    public async Task Link_corporativo_com_anyone_pede_qualquer_pessoa_com_o_link()
    {
        using var h = new Harness();
        h.Config.Drives[0].Kind = "business";
        var n = h.Remote.RemoteCreate("/", "plano.xlsx", "x");
        h.Settle();
        var r = await Actions(h).CreateShareLinkAsync(Lid(h, "plano.xlsx"), edit: true, anyone: true, CancellationToken.None);
        Assert.Equal("anonymous", r.Scope);
        Assert.Equal([(n.Id, "edit", "anonymous")], h.Remote.LinkRequests);
    }

    [Fact]
    public async Task Link_corporativo_com_anyone_recusado_pela_politica_da_erro_claro_sem_trocar_de_escopo()
    {
        using var h = new Harness();
        h.Config.Drives[0].Kind = "business";
        h.Remote.RefuseAnonymousLinks = true;
        var n = h.Remote.RemoteCreate("/", "plano.xlsx", "x");
        h.Settle();
        var e = await Assert.ThrowsAsync<ItemActionException>(() =>
            Actions(h).CreateShareLinkAsync(Lid(h, "plano.xlsx"), edit: true, anyone: true, CancellationToken.None));
        Assert.Contains("qualquer pessoa", e.Message);
        Assert.Contains("política da sua organização", e.Message);
        Assert.Equal([(n.Id, "edit", "anonymous")], h.Remote.LinkRequests); // nada de cair para organization
    }

    [Fact]
    public async Task Link_pessoal_com_anyone_continua_qualquer_pessoa()
    {
        using var h = new Harness(); // pessoal: organization não existe; --anyone não muda nada
        var n = h.Remote.RemoteCreate("/", "foto.jpg", "x");
        h.Settle();
        var r = await Actions(h).CreateShareLinkAsync(Lid(h, "foto.jpg"), edit: false, anyone: true, CancellationToken.None);
        Assert.Equal("anonymous", r.Scope);
        Assert.Equal([(n.Id, "view", "anonymous")], h.Remote.LinkRequests);
    }

    [Fact]
    public async Task Link_pessoal_recusado_nao_tenta_organizacao()
    {
        using var h = new Harness(); // Kind = personal: "organization" não existe no OneDrive pessoal
        h.Remote.RefuseAnonymousLinks = true;
        h.Remote.RemoteCreate("/", "a.txt", "x");
        h.Settle();
        var e = await Assert.ThrowsAsync<ItemActionException>(() => Actions(h).CreateShareLinkAsync(Lid(h, "a.txt"), false, anyone: false, CancellationToken.None));
        Assert.Contains("sem permissão", e.Message);
        Assert.Single(h.Remote.LinkRequests);
    }

    [Fact]
    public async Task Link_de_item_ainda_nao_enviado_da_erro_claro()
    {
        using var h = new Harness();
        h.Remote.BeforeUpload = (_, ct) => Task.Delay(Timeout.Infinite, ct); // envio nunca termina
        h.WriteFile("novo.txt", "rascunho");
        var e = await Assert.ThrowsAsync<ItemActionException>(() => Actions(h).CreateShareLinkAsync(Lid(h, "novo.txt"), false, anyone: false, CancellationToken.None));
        Assert.Contains("ainda não foi enviado", e.Message);
        Assert.Empty(h.Remote.LinkRequests);
    }

    // ---------- versões ----------

    [Fact]
    public async Task Lista_versoes_da_mais_nova_para_a_mais_antiga()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "notas.md", "v1");
        h.Remote.RemoteUpdate("/notas.md", "versão 2");
        h.Remote.RemoteUpdate("/notas.md", "terceira!");
        h.Settle();
        var list = await Actions(h).ListVersionsAsync(Lid(h, "notas.md"), CancellationToken.None);
        Assert.Equal(["3.0", "2.0", "1.0"], list.Select(v => v.Id));
        Assert.Equal([9, 9, 2], list.Select(v => v.Size));
    }

    [Fact]
    public async Task Versoes_de_pasta_recusadas()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "Pasta", null, folder: true);
        h.Settle();
        var e = await Assert.ThrowsAsync<ItemActionException>(() => Actions(h).ListVersionsAsync(Lid(h, "Pasta"), CancellationToken.None));
        Assert.Contains("pasta", e.Message);
    }

    [Fact]
    public async Task Restaurar_versao_invalida_o_cache_e_a_leitura_seguinte_ve_o_conteudo_restaurado()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "doc.txt", "original");
        h.Remote.RemoteUpdate("/doc.txt", "editado depois");
        h.Settle();
        Assert.Equal("editado depois", h.ReadFile("doc.txt")); // fica no cache
        var lid = Lid(h, "doc.txt");
        var before = h.Db.Get(lid)!;
        Assert.NotNull(before.CachedCTag);

        await Actions(h).RestoreVersionAsync(lid, "1.0", CancellationToken.None);

        Assert.Equal(1, h.Remote.VersionRestores);
        Assert.Equal("original", h.Remote.ReadText("/doc.txt"));
        var after = h.Db.Get(lid)!;
        Assert.NotEqual(before.CTag, after.CTag); // metadados já atualizados, sem esperar o delta
        Assert.Null(after.CachedCTag);
        Assert.False(h.Engine.Cache.Exists(after.DriveId, lid));
        Assert.Equal("original", h.ReadFile("doc.txt"));
        h.Settle(); // o delta depois não muda nada nem cria conflito
        Assert.Equal("original", h.ReadFile("doc.txt"));
        Assert.Empty(h.Db.Conflicts());
    }

    [Fact]
    public async Task Restaurar_versao_de_arquivo_fixado_baixa_a_versao_restaurada()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "fixo.txt", "antigo");
        h.Remote.RemoteUpdate("/fixo.txt", "novo conteúdo");
        h.Settle();
        var lid = Lid(h, "fixo.txt");
        h.Engine.SetPinned(lid, true);
        Harness.WaitUntil(() => h.Db.Get(lid)!.CachedCTag != null, "fixado baixado");

        await Actions(h).RestoreVersionAsync(lid, "1.0", CancellationToken.None);

        Harness.WaitUntil(() => h.Db.Get(lid) is { CachedCTag: { } c } it && c == it.CTag, "versão restaurada baixada");
        Assert.Equal("antigo", File.ReadAllText(h.Engine.Cache.PathFor(h.Db.Get(lid)!.DriveId, lid)));
    }

    [Fact]
    public async Task Restaurar_versao_com_alteracoes_locais_nao_enviadas_e_recusado()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "rel.txt", "v1");
        h.Remote.RemoteUpdate("/rel.txt", "v2");
        h.Settle();
        var gate = new TaskCompletionSource();
        h.Remote.BeforeUpload = (_, ct) => gate.Task.WaitAsync(ct); // segura o envio
        h.WriteFile("rel.txt", "minha edição local", create: false);
        var lid = Lid(h, "rel.txt");
        Assert.True(h.Db.Get(lid)!.Dirty);

        var e = await Assert.ThrowsAsync<ItemActionException>(() => Actions(h).RestoreVersionAsync(lid, "1.0", CancellationToken.None));
        Assert.Contains("ainda não foram enviadas", e.Message);
        Assert.Equal(0, h.Remote.VersionRestores);
        Assert.Equal("minha edição local", h.ReadFile("rel.txt"));
        gate.SetResult();
    }

    [Fact]
    public async Task Baixar_versao_cria_copia_ao_lado_sem_tocar_no_original()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "Pasta", null, folder: true);
        h.Remote.RemoteCreate("/Pasta", "contrato.docx", "primeira");
        h.Remote.RemoteUpdate("/Pasta/contrato.docx", "segunda");
        h.Settle();
        var lid = Lid(h, "Pasta/contrato.docx");
        var actions = Actions(h);
        var v1 = (await actions.ListVersionsAsync(lid, CancellationToken.None)).Single(v => v.Id == "1.0");

        var name = await actions.DownloadVersionCopyAsync(lid, v1, CancellationToken.None);
        Assert.Equal(ItemActions.VersionCopyName("contrato.docx", v1.Modified), name);
        Assert.Matches(@"^contrato \(versão \d{4}-\d{2}-\d{2} \d{4}\)\.docx$", name);
        Assert.Equal("primeira", h.ReadFile("Pasta/" + name));
        Assert.Equal("segunda", h.ReadFile("Pasta/contrato.docx"));

        // de novo: não sobrescreve a primeira cópia
        var again = await actions.DownloadVersionCopyAsync(lid, v1, CancellationToken.None);
        Assert.NotEqual(name, again);
        Assert.EndsWith(" (2).docx", again);

        h.Settle();
        Assert.Equal("primeira", h.Remote.ReadText("/Pasta/" + name)); // enviada como arquivo novo
        Assert.Equal("segunda", h.Remote.ReadText("/Pasta/contrato.docx"));
        Assert.Equal(0, h.Remote.VersionRestores);
    }

    [Theory]
    [InlineData("relatorio.docx", "relatorio (versão 2026-11-01 1030).docx")]
    [InlineData("LEIAME", "LEIAME (versão 2026-11-01 1030)")]
    [InlineData(".bashrc", ".bashrc (versão 2026-11-01 1030)")]
    [InlineData("pacote.tar.gz", "pacote.tar (versão 2026-11-01 1030).gz")]
    public void Nome_da_copia_da_versao(string name, string expected)
    {
        var local = new DateTimeOffset(2026, 11, 1, 10, 30, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 1, 10, 30, 0)));
        Assert.Equal(expected, ItemActions.VersionCopyName(name, local));
    }

    // ---------- Lixeira ----------

    [Fact]
    public async Task Excluido_aqui_ou_na_nuvem_aparece_na_lixeira_e_restaura_no_onedrive_pessoal()
    {
        using var h = new Harness();
        var local = h.Remote.RemoteCreate("/", "apagado-aqui.txt", "a");
        var remote = h.Remote.RemoteCreate("/", "apagado-na-web.txt", "b");
        h.Settle();
        Assert.Equal(0, h.Fs.Unlink(h.P("apagado-aqui.txt")));
        h.Settle();
        h.Remote.RemoteDelete("/apagado-na-web.txt");
        h.Settle();

        var trash = h.Db.Trash();
        Assert.Equal(["apagado-na-web.txt", "apagado-aqui.txt"], trash.Select(t => t.Name));
        Assert.Equal([remote.Id, local.Id], trash.Select(t => t.ItemId));

        var restored = await Actions(h).RestoreFromTrashAsync(trash.Single(t => t.ItemId == local.Id).Id, CancellationToken.None);
        Assert.Equal("apagado-aqui.txt", restored);
        Assert.Equal(1, h.Remote.TrashRestores);
        h.Settle();
        Assert.Equal("a", h.ReadFile("apagado-aqui.txt"));
        Assert.Equal(["apagado-na-web.txt"], h.Db.Trash().Select(t => t.Name));
    }

    [Fact]
    public async Task Lixeira_corporativa_nao_chama_api_beta_e_indica_o_navegador()
    {
        using var h = new Harness();
        h.Config.Drives[0].Kind = "business";
        h.Config.Drives[0].WebUrl = "https://empresa-my.sharepoint.com/personal/u_empresa_com/Documents";
        h.Remote.RemoteCreate("/", "x.txt", "a");
        h.Settle();
        h.Remote.RemoteDelete("/x.txt");
        h.Settle();
        var e = await Assert.ThrowsAsync<ItemActionException>(() => Actions(h).RestoreFromTrashAsync(h.Db.Trash().Single().Id, CancellationToken.None));
        Assert.Contains("https://empresa-my.sharepoint.com/personal/u_empresa_com/_layouts/15/RecycleBin.aspx", e.Message);
        Assert.Equal(0, h.Remote.TrashRestores);
    }

    [Fact]
    public async Task Item_que_ja_saiu_da_lixeira_sai_da_lista()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "y.txt", "a");
        h.Settle();
        h.Remote.RemoteDelete("/y.txt");
        h.Settle();
        var rec = h.Db.Trash().Single();
        await Actions(h).RestoreFromTrashAsync(rec.Id, CancellationToken.None);
        h.Db.AddTrash(rec.DriveId, rec.ItemId, rec.Name, false); // registro velho de novo, mas já restaurado
        var e = await Assert.ThrowsAsync<ItemActionException>(() => Actions(h).RestoreFromTrashAsync(h.Db.Trash().Single().Id, CancellationToken.None));
        Assert.Contains("não está mais na Lixeira", e.Message);
        Assert.Empty(h.Db.Trash());
    }

    [Theory]
    [InlineData("business", "https://empresa-my.sharepoint.com/personal/u_empresa_com/Documents", "https://empresa-my.sharepoint.com/personal/u_empresa_com/_layouts/15/RecycleBin.aspx")]
    [InlineData("documentLibrary", "https://empresa.sharepoint.com/sites/Financeiro/Documentos%20Compartilhados", "https://empresa.sharepoint.com/sites/Financeiro/_layouts/15/RecycleBin.aspx")]
    [InlineData("documentLibrary", "https://empresa.sharepoint.com/teams/Equipe/Shared%20Documents", "https://empresa.sharepoint.com/teams/Equipe/_layouts/15/RecycleBin.aspx")]
    [InlineData("documentLibrary", "https://empresa.sharepoint.com/Shared%20Documents", "https://empresa.sharepoint.com/_layouts/15/RecycleBin.aspx")]
    [InlineData("personal", "https://onedrive.live.com/?cid=abc", "https://onedrive.live.com/?v=recyclebin")]
    [InlineData("business", null, "https://onedrive.live.com/?v=recyclebin")]
    public void Url_da_lixeira_no_navegador(string kind, string? webUrl, string expected) =>
        Assert.Equal(expected, ItemActions.RecycleBinUrl(new CraftDriver.Core.Config.DriveConfig { Kind = kind, WebUrl = webUrl }));

    [Theory]
    [InlineData("personal", "https://my.microsoftpersonalcontent.com/personal/4f28e9287facea47/Documents", "https://onedrive.live.com/")]
    [InlineData("personal", null, "https://onedrive.live.com/")]
    [InlineData("personal", "https://onedrive.live.com/?cid=4f28e9287facea47", "https://onedrive.live.com/?cid=4f28e9287facea47")]
    [InlineData("business", "https://empresa-my.sharepoint.com/personal/u_empresa_com/Documents", "https://empresa-my.sharepoint.com/personal/u_empresa_com/Documents")]
    [InlineData("documentLibrary", "https://empresa.sharepoint.com/sites/Setor/TI", "https://empresa.sharepoint.com/sites/Setor/TI")]
    [InlineData("business", null, null)]
    public void Url_da_biblioteca_no_navegador(string kind, string? webUrl, string? expected) =>
        Assert.Equal(expected, ItemActions.BrowserUrl(new CraftDriver.Core.Config.DriveConfig { Kind = kind, WebUrl = webUrl }));
}

/// <summary>Protocolo HTTP de createLink, versões e restore contra um servidor falso que grava os pedidos.</summary>
public class GraphActionsProtocolTests
{
    sealed class Recorder(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<(string Method, string Url, string? Body, string? ContentType)> Requests = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var body = req.Content == null ? null : await req.Content.ReadAsStringAsync(ct);
            Requests.Add((req.Method.Method, req.RequestUri!.ToString(), body, req.Content?.Headers.ContentType?.MediaType));
            return respond(req, body);
        }
    }

    static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    static (GraphClient, Recorder) Client(Func<HttpRequestMessage, string?, HttpResponseMessage> respond)
    {
        var rec = new Recorder(respond);
        return (new GraphClient((_, _) => Task.FromResult("tk"), new HttpClient(rec)), rec);
    }

    /// <summary>
    /// Verificado no OneDrive real: PUT /content seguido de PATCH de fileSystemInfo cria DUAS versões por envio
    /// (3 edições de um .txt = 6 versões). Pela sessão de upload a data vai no createUploadSession: uma versão por
    /// envio e as mesmas 2 idas e voltas.
    /// </summary>
    [Fact]
    public async Task Arquivo_pequeno_sobe_por_sessao_com_a_data_e_sem_patch()
    {
        var (c, rec) = Client((req, _) => req.Method == HttpMethod.Post
            ? Json(HttpStatusCode.OK, """{"uploadUrl":"https://up.example/sessao1"}""")
            : Json(HttpStatusCode.Created, """{"id":"01NOVO","name":"a.txt","size":9,"file":{}}"""));
        var f = Path.Combine(Path.GetTempPath(), "cd-pequeno-" + Guid.NewGuid() + ".txt");
        File.WriteAllText(f, "versao 3\n");
        try
        {
            var when = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
            var item = await c.UploadAsync("drv", "01PAI", "a.txt", "01ITEM", "\"etag\"", f, when, null, CancellationToken.None);
            Assert.Equal("01NOVO", item.Id);
            Assert.DoesNotContain(rec.Requests, r => r.Method == "PATCH");
            Assert.Equal(["POST", "PUT"], rec.Requests.Select(r => r.Method));
            Assert.EndsWith("/items/01ITEM/createUploadSession", rec.Requests[0].Url);
            Assert.Contains("\"lastModifiedDateTime\":\"2020-01-02T03:04:05.0000000Z\"", rec.Requests[0].Body);
            Assert.Equal("https://up.example/sessao1", rec.Requests[1].Url);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public async Task Arquivo_vazio_continua_no_put_simples()
    {
        // a sessão não aceita 0 bytes (Content-Range vazio): PUT /content e a data por PATCH
        var (c, rec) = Client((_, _) => Json(HttpStatusCode.OK, """{"id":"01V","name":"v.txt","size":0,"file":{}}"""));
        var f = Path.Combine(Path.GetTempPath(), "cd-vazio-" + Guid.NewGuid());
        File.WriteAllBytes(f, []);
        try
        {
            await c.UploadAsync("drv", "01PAI", "v.txt", null, null, f, DateTimeOffset.UtcNow, null, CancellationToken.None);
            Assert.Equal(["PUT", "PATCH"], rec.Requests.Select(r => r.Method));
            Assert.Contains("/content", rec.Requests[0].Url);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public async Task CreateLink_posta_tipo_e_escopo_e_le_o_webUrl()
    {
        var (c, rec) = Client((_, _) => Json(HttpStatusCode.Created,
            """{"id":"p1","roles":["read"],"link":{"type":"view","scope":"organization","webUrl":"https://empresa.sharepoint.com/:t:/g/abc"}}"""));
        var link = await c.CreateLinkAsync("b!drive", "01ITEM", "view", "anonymous", CancellationToken.None);
        Assert.Equal("https://empresa.sharepoint.com/:t:/g/abc", link.WebUrl);
        Assert.Equal("organization", link.Scope); // o que o servidor criou, não o pedido
        var r = Assert.Single(rec.Requests);
        Assert.Equal("POST", r.Method);
        Assert.Equal("https://graph.microsoft.com/v1.0/drives/b%21drive/items/01ITEM/createLink", r.Url);
        Assert.Equal("application/json", r.ContentType);
        Assert.Equal("""{"type":"view","scope":"anonymous"}""", r.Body);
    }

    [Fact]
    public async Task CreateLink_recusado_pela_politica_vira_GraphException_403()
    {
        var (c, _) = Client((_, _) => Json(HttpStatusCode.Forbidden, """{"error":{"code":"accessDenied","message":"anonymous links disabled"}}"""));
        var e = await Assert.ThrowsAsync<GraphException>(() => c.CreateLinkAsync("d", "i", "edit", "anonymous", CancellationToken.None));
        Assert.Equal(HttpStatusCode.Forbidden, e.Status);
        Assert.True(ItemActions.IsPolicyRefusal(e));
    }

    [Fact]
    public async Task Versoes_lista_paginada_com_autor_e_tamanho()
    {
        var (c, rec) = Client((req, _) => req.RequestUri!.Query.Contains("skip")
            ? Json(HttpStatusCode.OK, """{"value":[{"id":"1.0","lastModifiedDateTime":"2026-10-01T08:00:00Z","size":16}]}""")
            : Json(HttpStatusCode.OK, """
                {"value":[{"id":"3.0","lastModifiedBy":{"user":{"displayName":"Ana"}},"lastModifiedDateTime":"2026-11-01T13:30:00Z","size":123},
                          {"id":"2.0","lastModifiedBy":{"user":{"displayName":"Bia"}},"lastModifiedDateTime":"2026-10-20T10:00:00Z","size":62}],
                 "@odata.nextLink":"https://graph.microsoft.com/v1.0/drives/d/items/i/versions?skip=2"}
                """));
        var list = await c.ListVersionsAsync("d", "i", CancellationToken.None);
        Assert.Equal(["3.0", "2.0", "1.0"], list.Select(v => v.Id));
        Assert.Equal(["Ana", "Bia", null], list.Select(v => v.ModifiedBy));
        Assert.Equal(123, list[0].Size);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 13, 30, 0, TimeSpan.Zero), list[0].Modified);
        Assert.Equal("https://graph.microsoft.com/v1.0/drives/d/items/i/versions", rec.Requests[0].Url);
        Assert.Equal(2, rec.Requests.Count);
    }

    [Fact]
    public async Task RestoreVersion_e_post_sem_corpo_no_endereco_da_versao()
    {
        var (c, rec) = Client((_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
        await c.RestoreVersionAsync("d", "i", "2.0", CancellationToken.None);
        var r = Assert.Single(rec.Requests);
        Assert.Equal("POST", r.Method);
        Assert.Equal("https://graph.microsoft.com/v1.0/drives/d/items/i/versions/2.0/restoreVersion", r.Url);
        Assert.Equal("", r.Body);
        Assert.Equal("application/json", r.ContentType);
    }

    [Fact]
    public async Task Conteudo_da_versao_e_baixado_do_endereco_da_versao()
    {
        var (c, rec) = Client((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("conteúdo antigo"u8.ToArray()) });
        using var ms = new MemoryStream();
        await c.DownloadVersionAsync("d", "i", "1.0", ms, CancellationToken.None);
        Assert.Equal("conteúdo antigo", Encoding.UTF8.GetString(ms.ToArray()));
        Assert.Equal("https://graph.microsoft.com/v1.0/drives/d/items/i/versions/1.0/content", Assert.Single(rec.Requests).Url);
    }

    [Fact]
    public async Task Restaurar_da_lixeira_usa_o_endpoint_v1_do_onedrive_pessoal()
    {
        var (c, rec) = Client((_, _) => Json(HttpStatusCode.OK, """{"id":"i","name":"volta.txt","file":{},"parentReference":{"id":"root"}}"""));
        var item = await c.RestoreDeletedAsync("d", "i", CancellationToken.None);
        Assert.Equal("volta.txt", item.Name);
        var r = Assert.Single(rec.Requests);
        Assert.Equal("POST", r.Method);
        Assert.Equal("https://graph.microsoft.com/v1.0/me/drive/items/i/restore", r.Url);
        Assert.Equal("{}", r.Body);
    }
}
