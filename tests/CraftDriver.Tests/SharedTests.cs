using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using CraftDriver.Core.Graph;
using CraftDriver.Core.State;
using CraftDriver.Core.Sync;
using CraftDriver.Core.Vfs;
using CraftDriver.Fuse;

namespace CraftDriver.Tests;

/// <summary>Atalhos ("Adicionar a Meus arquivos") e "Compartilhados comigo": itens de outra drive.</summary>
[Collection("fuse")]
public class SharedTests
{
    const string OwnerDrive = "dono-drive";
    const uint ExplicitMode = VirtualFs.ExplicitMode;
    static string Container => "/" + Harness.Name + SyncEngine.SharedContainerSuffix;

    /// <summary>Drive de outro usuário com /Projeto (a.txt, Sub/b.txt), /Relatorio.txt e /Fotos/f.jpg.</summary>
    static FakeGraph Owner(Harness h)
    {
        var o = h.Remote.AddOwnerDrive(OwnerDrive);
        o.RemoteCreate("/", "Projeto", null, folder: true);
        o.RemoteCreate("/Projeto", "a.txt", "conteúdo A");
        o.RemoteCreate("/Projeto", "Sub", null, folder: true);
        o.RemoteCreate("/Projeto/Sub", "b.txt", "conteúdo B");
        o.RemoteCreate("/", "Relatorio.txt", "relatório do dono");
        o.RemoteCreate("/", "Fotos", null, folder: true);
        o.RemoteCreate("/Fotos", "f.jpg", "jpeg");
        o.RemoteCreate("/", "Privado.txt", "não compartilhado");
        return o;
    }

    static DriveSync Sub(Harness h, string rootName)
    {
        DriveSync? d = null;
        Harness.WaitUntil(() => (d = h.Engine.SharedDrives.FirstOrDefault(x => h.Db.Get(x.RootLid)?.Name == rootName)) != null, "sub-drive " + rootName);
        return d!;
    }

    static void Scan(DriveSync d) => d.RunDeltaAsync(CancellationToken.None).GetAwaiter().GetResult();

    static List<string> Ls(Harness h, string path)
    {
        var entries = new List<(string name, FileAttr attr)>();
        Assert.Equal(0, h.Fs.ReadDir(path, entries));
        return entries.Select(e => e.name).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    /// <summary>Atalho para pasta compartilhada só para leitura, colocado numa subpasta da drive do usuário.</summary>
    static (FakeGraph owner, DriveSync sub) ShortcutSetup(Harness h, bool writable)
    {
        var o = Owner(h);
        o.Shares[o.FindByPath("/Projeto")!.Id] = writable;
        h.Remote.RemoteCreate("/", "Trabalho", null, folder: true);
        h.Remote.AddShortcut("/Trabalho", "Projeto do Dono", o, "/Projeto");
        h.Settle();
        var sub = Sub(h, "Projeto do Dono");
        Scan(sub);
        return (o, sub);
    }

    [Fact]
    public void Atalho_em_subpasta_lista_e_le_conteudo_da_drive_do_dono()
    {
        using var h = new Harness();
        var (o, _) = ShortcutSetup(h, writable: false);

        Assert.Equal(["Projeto do Dono"], Ls(h, h.P("Trabalho")));
        Assert.Equal(["Sub", "a.txt"], Ls(h, h.P("Trabalho/Projeto do Dono")));
        Assert.Equal(["b.txt"], Ls(h, h.P("Trabalho/Projeto do Dono/Sub")));
        Assert.Equal(0, o.Downloads); // listar não baixa

        Assert.Equal("conteúdo A", h.ReadFile("Trabalho/Projeto do Dono/a.txt"));
        Assert.Equal("conteúdo B", h.ReadFile("Trabalho/Projeto do Dono/Sub/b.txt"));
        Assert.Equal(2, o.Downloads); // conteúdo veio da drive do dono
        Assert.Equal(0, h.Remote.Downloads);

        // mudanças do dono aparecem na próxima varredura (sem delta: listagem de children)
        o.RemoteCreate("/Projeto", "c.txt", "novo do dono");
        o.RemoteUpdate("/Projeto/a.txt", "A versão 2");
        o.RemoteDelete("/Projeto/Sub/b.txt");
        Scan(Sub(h, "Projeto do Dono"));
        Assert.Equal(["Sub", "a.txt", "c.txt"], Ls(h, h.P("Trabalho/Projeto do Dono")));
        Assert.Empty(Ls(h, h.P("Trabalho/Projeto do Dono/Sub")));
        Assert.Equal("A versão 2", h.ReadFile("Trabalho/Projeto do Dono/a.txt"));
    }

    [Fact]
    public void Listagem_de_pasta_compartilhada_segue_paginas()
    {
        using var h = new Harness();
        var o = Owner(h);
        o.ChildrenPageSize = 2;
        for (var i = 0; i < 5; i++) o.RemoteCreate("/Projeto", $"extra{i}.txt", "x");
        o.Shares[o.FindByPath("/Projeto")!.Id] = false;
        h.Remote.AddShortcut("/", "Projeto", o, "/Projeto");
        h.Settle();
        Scan(Sub(h, "Projeto"));
        Assert.Equal(7, Ls(h, h.P("Projeto")).Count); // a.txt, Sub, extra0..4
    }

    [Fact]
    public void Somente_leitura_aparece_0444_0555_e_recusa_escrita_sem_enfileirar()
    {
        using var h = new Harness();
        var (o, _) = ShortcutSetup(h, writable: false);
        var dir = h.P("Trabalho/Projeto do Dono");

        Assert.Equal(0, h.Fs.GetAttr(dir + "/a.txt", out var fa));
        Assert.Equal(ExplicitMode | 0x124u, fa.Mode); // 0444
        Assert.Equal(0, h.Fs.GetAttr(dir, out var da));
        Assert.Equal(ExplicitMode | 0x16Du, da.Mode); // 0555
        Assert.Equal(0, h.Fs.GetAttr(h.P("Trabalho"), out var own));
        Assert.Equal(0u, own.Mode); // o resto da drive continua normal

        Assert.Equal(-Errno.EACCES, h.Fs.Create(dir + "/novo.txt", OpenFlags.RdWr, out _));
        Assert.Equal(-Errno.EACCES, h.Fs.Open(dir + "/a.txt", OpenFlags.RdWr, 0, out _));
        Assert.Equal(-Errno.EACCES, h.Fs.Open(dir + "/a.txt", OpenFlags.WrOnly | OpenFlags.Trunc, 0, out _));
        Assert.Equal(-Errno.EACCES, h.Fs.Mkdir(dir + "/Nova"));
        Assert.Equal(-Errno.EACCES, h.Fs.Unlink(dir + "/a.txt"));
        Assert.Equal(-Errno.EACCES, h.Fs.Rename(dir + "/a.txt", dir + "/z.txt", 0));
        Assert.Equal(-Errno.EACCES, h.Fs.Truncate(dir + "/a.txt", 0, null));
        Assert.Equal(-Errno.EACCES, h.Fs.Utimens(dir + "/a.txt", 0));
        Assert.Equal(-Errno.EPERM, h.Fs.Chmod(dir + "/a.txt", 0x1FF));
        // a raiz do atalho não se apaga nem se move pelo Linux (apagaria a pasta do dono)
        Assert.Equal(-Errno.EPERM, h.Fs.Rmdir(dir));
        Assert.Equal(-Errno.EPERM, h.Fs.Rename(dir, h.P("Outro"), 0));

        Assert.Equal(0, h.Db.PendingOps());
        h.Settle();
        Assert.Equal(0, o.DeniedWrites);
        Assert.Equal(0, o.Uploads + o.Deletes + o.Moves);
        Assert.Equal("conteúdo A", o.ReadText("/Projeto/a.txt"));
    }

    [Fact]
    public void Compartilhado_com_escrita_envia_para_a_drive_do_dono()
    {
        using var h = new Harness();
        var (o, sub) = ShortcutSetup(h, writable: true);
        Assert.True(sub.Shared!.Writable);
        var dir = "Trabalho/Projeto do Dono";
        Assert.Equal(0, h.Fs.GetAttr(h.P(dir + "/a.txt"), out var fa));
        Assert.Equal(0u, fa.Mode);

        h.WriteFile(dir + "/novo.txt", "escrito no Linux");
        h.WriteFile(dir + "/a.txt", "A editado", create: false);
        Assert.Equal(0, h.Fs.Mkdir(h.P(dir + "/Pasta nova")));
        Harness.WaitUntil(() => h.Db.PendingOps() == 0 && o.ReadText("/Projeto/novo.txt") != null
                                                       && o.FindByPath("/Projeto/Pasta nova") != null
                                                       && o.ReadText("/Projeto/a.txt") == "A editado", "envio para o dono");

        Assert.Equal("escrito no Linux", o.ReadText("/Projeto/novo.txt"));
        Assert.DoesNotContain(h.Remote.ListPaths(), p => p.Contains("novo.txt") || p.Contains("Pasta nova"));
        Assert.Equal(0, o.DeniedWrites);

        // mover para fora do compartilhamento é entre drives: o mv copia e apaga
        Assert.Equal(-Errno.EXDEV, h.Fs.Rename(h.P(dir + "/novo.txt"), h.P("Trabalho/novo.txt"), 0));
    }

    [Fact]
    public void Exclusao_em_massa_em_compartilhamento_gravavel_espera_confirmacao()
    {
        using var h = new Harness();
        h.Config.MassDeleteThreshold = 3;
        var (o, sub) = ShortcutSetup(h, writable: true);
        for (var i = 0; i < 4; i++) o.RemoteCreate("/Projeto", $"d{i}.txt", "x");
        Scan(sub);
        for (var i = 0; i < 4; i++) Assert.Equal(0, h.Fs.Unlink(h.P($"Trabalho/Projeto do Dono/d{i}.txt")));

        Harness.WaitUntil(() => sub.DeleteHold, "retenção na sub-drive");
        Assert.Equal(EngineStatus.DeleteConfirmation, h.Engine.Status); // aparece para o usuário confirmar
        Thread.Sleep(DriveSync.DeleteQuietMs + 500);
        Assert.Equal(0, o.Deletes);

        sub.ConfirmDeletes();
        Harness.WaitUntil(() => h.Db.PendingOps() == 0 && o.FindByPath("/Projeto/d0.txt") == null, "exclusões no dono");
        Assert.Equal("conteúdo A", o.ReadText("/Projeto/a.txt"));
    }

    [Fact]
    public async Task Compartilhados_comigo_fica_ao_lado_da_drive_e_le_arquivos_e_pastas()
    {
        using var h = new Harness();
        var o = Owner(h);
        h.Remote.ShareWithMe(o, "/Relatorio.txt", writable: false);
        h.Remote.ShareWithMe(o, "/Fotos", writable: false);
        await h.Drive.RefreshSharedWithMeAsync(CancellationToken.None, force: true);

        Assert.Equal([Harness.Name, Harness.Name + SyncEngine.SharedContainerSuffix], Ls(h, "/"));
        Assert.Equal(["Fotos", "Relatorio.txt"], Ls(h, Container));
        Scan(Sub(h, "Fotos"));
        Assert.Equal(["f.jpg"], Ls(h, Container + "/Fotos"));

        Assert.Equal(0, h.Fs.Open(Container + "/Relatorio.txt", 0, 0, out var fh));
        var buf = new byte[256];
        var n = h.Fs.Read(fh, buf, 0);
        h.Fs.Release(fh);
        Assert.Equal("relatório do dono", Encoding.UTF8.GetString(buf, 0, n));
        Assert.Equal(1, o.Downloads);

        // a pasta virtual não aceita nada novo
        Assert.Equal(-Errno.EACCES, h.Fs.Create(Container + "/x.txt", OpenFlags.RdWr, out _));
        Assert.Equal(-Errno.EACCES, h.Fs.Mkdir(Container + "/X"));
        Assert.Equal(-Errno.EPERM, h.Fs.Unlink(Container + "/Relatorio.txt"));
        Assert.Equal(0, h.Db.PendingOps());
        // o que não foi compartilhado não aparece
        Assert.Null(h.Fs.Resolve(Container + "/Privado.txt"));
    }

    [Fact]
    public async Task Remover_compartilhamento_ou_atalho_faz_o_item_sumir()
    {
        using var h = new Harness();
        var (o, sub) = ShortcutSetup(h, writable: false);
        h.Remote.ShareWithMe(o, "/Fotos", writable: false);
        h.Remote.ShareWithMe(o, "/Relatorio.txt", writable: false);
        await h.Drive.RefreshSharedWithMeAsync(CancellationToken.None, force: true);
        Scan(Sub(h, "Fotos"));
        Assert.Equal("jpeg", h.Fs.Resolve(Container + "/Fotos/f.jpg") is { } ? ReadAll(h, Container + "/Fotos/f.jpg") : null);

        // o dono para de compartilhar: some de "Compartilhados comigo", e o cache dele vai junto
        h.Remote.Unshare(o, "/Fotos");
        await h.Drive.RefreshSharedWithMeAsync(CancellationToken.None, force: true);
        Assert.Equal(["Relatorio.txt"], Ls(h, Container));
        Assert.Null(h.Fs.Resolve(Container + "/Fotos/f.jpg"));
        Harness.WaitUntil(() => h.Engine.SharedDrives.All(d => h.Db.Get(d.RootLid)?.Name != "Fotos"), "sub-drive parada");

        // acesso revogado com o atalho ainda lá: a pasta fica vazia
        o.Shares.Clear();
        Scan(sub);
        Assert.True(sub.Inaccessible);
        Assert.Empty(Ls(h, h.P("Trabalho/Projeto do Dono")));

        // atalho removido na nuvem: some da árvore
        h.Remote.RemoteDelete("/Trabalho/Projeto do Dono");
        h.Settle();
        Assert.Null(h.Fs.Resolve(h.P("Trabalho/Projeto do Dono")));
        Assert.Empty(Ls(h, h.P("Trabalho")));
        Assert.DoesNotContain(h.Db.SharedRoots(), s => s.Source == SharedSource.Shortcut);
    }

    static string ReadAll(Harness h, string path)
    {
        Assert.Equal(0, h.Fs.Open(path, 0, 0, out var fh));
        var buf = new byte[1 << 16];
        var n = h.Fs.Read(fh, buf, 0);
        h.Fs.Release(fh);
        return Encoding.UTF8.GetString(buf, 0, n);
    }

    [Fact]
    public void Atalho_movido_ou_renomeado_na_nuvem_acompanha_e_pasta_pai_apagada_leva_junto()
    {
        using var h = new Harness();
        ShortcutSetup(h, writable: false);
        h.Remote.RemoteRename("/Trabalho/Projeto do Dono", "Renomeado");
        h.Settle();
        Assert.Equal(["Renomeado"], Ls(h, h.P("Trabalho")));
        Assert.Equal(["Sub", "a.txt"], Ls(h, h.P("Trabalho/Renomeado")));

        var key = h.Db.SharedRoots().Single().Key;
        h.Remote.RemoteDeleteFolderOnly("/Trabalho"); // o delta traz só a pasta, não o atalho dentro dela
        h.Settle();
        Assert.Null(h.Fs.Resolve(h.P("Trabalho")));
        Assert.Empty(h.Db.SharedRoots());
        Assert.Equal((0L, 0L, 0L, 0L), h.Db.Counts(key)); // nada da sub-drive ficou órfão no banco
        Harness.WaitUntil(() => h.Engine.SharedDrives.Count == 0, "sub-drive parada");
    }

    [Fact]
    public async Task Falha_ao_consultar_compartilhados_mantem_a_lista_e_nao_bloqueia_listagem()
    {
        using var h = new Harness();
        var o = Owner(h);
        h.Remote.ShareWithMe(o, "/Relatorio.txt", writable: false);
        await h.Drive.RefreshSharedWithMeAsync(CancellationToken.None, force: true);
        Assert.Equal(["Relatorio.txt"], Ls(h, Container));

        h.Remote.SharedWithMeFails = true;
        await h.Drive.RefreshSharedWithMeAsync(CancellationToken.None, force: true);
        Assert.Equal(["Relatorio.txt"], Ls(h, Container));

        // sem rede: a listagem vem do banco, na hora
        h.Remote.OfflineMode = true;
        var sw = Stopwatch.StartNew();
        Assert.Equal(["Relatorio.txt"], Ls(h, Container));
        Assert.True(sw.ElapsedMilliseconds < 500);
        Assert.True(h.Fs.Open(Container + "/Relatorio.txt", 0, 0, out _) < 0); // abrir sem cache e sem rede: erro, não trava
    }

    static (int code, string output) Sh(string script, string cwd)
    {
        var psi = new ProcessStartInfo("bash", ["-c", script])
        {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output.Trim());
    }

    /// <summary>errno de abrir para escrita, pelo nome (independe do idioma das mensagens).</summary>
    const string OpenWriteErrno = """
        python3 -c $'import sys,os,errno\ntry:\n os.open(sys.argv[1], os.O_WRONLY|os.O_CREAT)\n print("OK")\nexcept OSError as e:\n print(errno.errorcode[e.errno])' 
        """;

    [Fact]
    public async Task Montagem_real_ls_cat_e_escrita_recusada()
    {
        RealFuse.Require();
        using var h = new Harness();
        var (o, _) = ShortcutSetup(h, writable: false);
        h.Remote.ShareWithMe(o, "/Relatorio.txt", writable: false);
        await h.Drive.RefreshSharedWithMeAsync(CancellationToken.None, force: true);

        var mnt = Path.Combine(h.Dir, "mnt");
        var loop = FuseHost.MountAsync(h.Fs, mnt);
        try
        {
            Harness.WaitUntil(() => Directory.Exists(Path.Combine(mnt, Harness.Name)), "montagem visível");
            Assert.Equal($"{Harness.Name}\n{Harness.Name}{SyncEngine.SharedContainerSuffix}", Sh("LC_ALL=C ls -1", mnt).output);
            var share = Harness.Name + SyncEngine.SharedContainerSuffix;
            Assert.Equal("Relatorio.txt", Sh($"LC_ALL=C ls -1 '{share}'", mnt).output);
            Assert.Equal("relatório do dono", Sh($"cat '{share}/Relatorio.txt'", mnt).output);

            var root = Path.Combine(mnt, Harness.Name);
            Assert.Equal("Sub\na.txt", Sh("LC_ALL=C ls -1 'Trabalho/Projeto do Dono'", root).output);
            Assert.Equal("conteúdo A", Sh("cat 'Trabalho/Projeto do Dono/a.txt'", root).output);
            Assert.Equal("conteúdo B", Sh("cat 'Trabalho/Projeto do Dono/Sub/b.txt'", root).output);
            Assert.Equal("444 555", Sh("stat -c %a 'Trabalho/Projeto do Dono/a.txt' 'Trabalho/Projeto do Dono' | tr '\\n' ' '", root).output);

            Assert.Equal("EACCES", Sh(OpenWriteErrno + "'Trabalho/Projeto do Dono/a.txt'", root).output);
            Assert.Equal("EACCES", Sh(OpenWriteErrno + "'Trabalho/Projeto do Dono/novo.txt'", root).output);
            Assert.Equal("EACCES", Sh(OpenWriteErrno + $"'{share}/novo.txt'", mnt).output);
            Assert.NotEqual(0, Sh("rm -rf 'Trabalho/Projeto do Dono'", root).code);
            Assert.Equal("conteúdo A", Sh("cat 'Trabalho/Projeto do Dono/a.txt'", root).output);
            Assert.Equal(0, h.Db.PendingOps());
        }
        finally
        {
            h.Engine.Stop();
            FuseHost.Unmount();
            Assert.True(await loop.WithinAsync(TimeSpan.FromSeconds(15)), "loop do FUSE não terminou após desmontar");
        }
        Assert.Equal(0, o.DeniedWrites + o.Uploads + o.Deletes);
    }
}

/// <summary>Formato real da Graph para atalhos, sharedWithMe, children e permissões.</summary>
public class SharedGraphProtocolTests
{
    sealed class Server(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<string> Urls = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Urls.Add(req.RequestUri!.ToString());
            return Task.FromResult(respond(req));
        }
    }

    static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public void Atalho_no_delta_traz_drive_e_item_do_dono()
    {
        // item de "Adicionar a Meus arquivos" como vem no delta da drive do usuário
        using var doc = JsonDocument.Parse("""
            {"id":"ABC!123","name":"Projeto do Dono","parentReference":{"driveId":"abc","id":"ABC!101"},
             "remoteItem":{"id":"DEF!456","folder":{"childCount":3},"parentReference":{"driveId":"def","driveType":"personal"}},
             "fileSystemInfo":{"lastModifiedDateTime":"2026-01-02T03:04:05Z"}}
            """);
        var r = RemoteItem.FromJson(doc.RootElement);
        Assert.True(r.IsRemoteShortcut);
        Assert.Equal("def", r.RemoteDriveId);
        Assert.Equal("DEF!456", r.RemoteId);
        Assert.True(r.RemoteIsFolder);
        Assert.Equal("ABC!101", r.ParentId);
    }

    [Fact]
    public async Task SharedWithMe_le_remoteItem_e_segue_paginas()
    {
        var server = new Server(req => req.RequestUri!.AbsolutePath.EndsWith("/me/drive/sharedWithMe") && req.RequestUri.Query == ""
            ? Json(HttpStatusCode.OK, """
                {"value":[{"id":"1312abc","name":"March Proposal.docx","remoteItem":{"id":"1991210caf!192","name":"March Proposal.docx",
                  "file":{},"size":19121,"parentReference":{"driveId":"1991210caf","id":"1991210caf!104"}}}],
                 "@odata.nextLink":"https://graph.microsoft.com/v1.0/me/drive/sharedWithMe?$skiptoken=2"}
                """)
            : Json(HttpStatusCode.OK, """
                {"value":[{"id":"1312def","remoteItem":{"id":"987def!654","name":"Pasta","folder":{},
                  "parentReference":{"driveId":"987def","id":"987def!321"}}}]}
                """));
        var client = new GraphClient((_, _) => Task.FromResult("t"), new HttpClient(server));
        var list = await client.GetSharedWithMeAsync(CancellationToken.None);
        Assert.Equal(2, list.Count);
        Assert.Equal(("1991210caf", "1991210caf!192", false, 19121L, "March Proposal.docx"),
            (list[0].RemoteDriveId, list[0].RemoteId, list[0].RemoteIsFolder, list[0].Size, list[0].Name));
        Assert.Equal(("987def", "987def!654", true, "Pasta"), (list[1].RemoteDriveId, list[1].RemoteId, list[1].RemoteIsFolder, list[1].Name));
        Assert.Equal("https://graph.microsoft.com/v1.0/me/drive/sharedWithMe", server.Urls[0]);
    }

    [Fact]
    public async Task Children_usa_a_drive_do_dono_e_o_nextLink()
    {
        var server = new Server(req => Json(HttpStatusCode.OK, """
            {"value":[{"id":"def!7","name":"a.txt","file":{},"size":3,"cTag":"c1","eTag":"e1","parentReference":{"driveId":"def","id":"def!456"}}],
             "@odata.nextLink":"https://graph.microsoft.com/v1.0/drives/def/items/def!456/children?$skiptoken=x"}
            """));
        var client = new GraphClient((_, _) => Task.FromResult("t"), new HttpClient(server));
        var page = await client.GetChildrenAsync("def", "def!456", null, CancellationToken.None);
        Assert.StartsWith("https://graph.microsoft.com/v1.0/drives/def/items/def%21456/children?$select=", server.Urls[0]);
        Assert.Contains("remoteItem", server.Urls[0]);
        Assert.Equal("a.txt", page.Items.Single().Name);
        Assert.Equal("c1", page.Items.Single().CTag);
        await client.GetChildrenAsync("def", "def!456", page.NextLink, CancellationToken.None);
        Assert.Equal(page.NextLink, server.Urls[1]);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"value":[{"id":"p1","roles":["read"],"grantedToV2":{"user":{"displayName":"Eu"}}}]}""", false)]
    [InlineData(HttpStatusCode.OK, """{"value":[{"id":"p1","roles":["read"]},{"id":"p2","roles":["write"],"link":{"type":"edit"}}]}""", true)]
    [InlineData(HttpStatusCode.OK, """{"value":[{"id":"p1","roles":["owner"]}]}""", true)]
    [InlineData(HttpStatusCode.OK, """{"value":[]}""", false)]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"code":"accessDenied","message":"x"}}""", false)]
    public async Task Permissao_de_escrita_vem_dos_papeis(HttpStatusCode code, string body, bool expected)
    {
        var server = new Server(_ => Json(code, body));
        var client = new GraphClient((_, _) => Task.FromResult("t"), new HttpClient(server));
        Assert.Equal(expected, await client.CanWriteAsync("def", "def!456", CancellationToken.None));
        Assert.Equal("https://graph.microsoft.com/v1.0/drives/def/items/def%21456/permissions", server.Urls[0]);
    }
}
