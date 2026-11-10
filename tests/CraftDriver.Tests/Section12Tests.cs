using System.Text;
using CraftDriver.Core.Graph;
using CraftDriver.Core.State;
using CraftDriver.Core.Sync;
using CraftDriver.Core.Vfs;

namespace CraftDriver.Tests;

/// <summary>
/// Regressões da seção 12 do PENDENCIAS.md — perda/corrupção de dados.
/// A listagem é controlada no FakeGraph para não depender de o envio segurar _mutate.
/// </summary>
[Collection("fuse")] // usa o motor em segundo plano com tempos apertados: não intercalar com os testes FUSE
public class Section12Tests
{
    const string OwnerDrive = "dono-drive";
    static readonly TimeSpan Long = TimeSpan.FromSeconds(20);

    static TaskCompletionSource Tcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    static List<string> Ls(Harness h, string path)
    {
        var entries = new List<(string name, FileAttr attr)>();
        Assert.Equal(0, h.Fs.ReadDir(path, entries));
        return entries.Select(e => e.name).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    static long Lid(Harness h, string rel) => h.Fs.Resolve(h.P(rel))!.Value.Item!.Lid;

    /// <summary>Atalho em /Trabalho para /Projeto (a.txt, Sub/b.txt) da drive de outro usuário.</summary>
    static (FakeGraph owner, DriveSync sub) ShortcutSetup(Harness h, bool writable)
    {
        var o = h.Remote.AddOwnerDrive(OwnerDrive);
        o.RemoteCreate("/", "Projeto", null, folder: true);
        o.RemoteCreate("/Projeto", "a.txt", "conteúdo A");
        o.RemoteCreate("/Projeto", "Sub", null, folder: true);
        o.RemoteCreate("/Projeto/Sub", "b.txt", "conteúdo B");
        o.Shares[o.FindByPath("/Projeto")!.Id] = writable;
        h.Remote.RemoteCreate("/", "Trabalho", null, folder: true);
        h.Remote.AddShortcut("/Trabalho", "Projeto do Dono", o, "/Projeto");
        h.Settle();
        DriveSync? d = null;
        Harness.WaitUntil(() => (d = h.Engine.SharedDrives.FirstOrDefault(x => h.Db.Get(x.RootLid)?.Name == "Projeto do Dono")) != null, "sub-drive");
        d!.RunDeltaAsync(CancellationToken.None).GetAwaiter().GetResult();
        return (o, d);
    }

    static SyncEngine Restart(Harness h)
    {
        var engine = new SyncEngine(h.Config, h.Db, new ContentCache(h.Config.CacheDir), new ActivityLog(), _ => h.Remote);
        engine.Start();
        return engine;
    }

    static List<string?> RemoteTexts(FakeGraph g) => g.ListPaths().Select(g.ReadText).ToList();

    // ---------- 12.1 sweep da sub-drive compartilhada ----------

    /// <summary>
    /// A listagem (children) da pasta compartilhada é feita antes de pegar _mutate; um envio que termina nesse meio
    /// tempo ganha ItemId que não está em <c>seen</c> e o Sweep o trata como "sumiu da nuvem".
    /// </summary>
    [Theory]
    [InlineData(false)] // arquivo enviado: some localmente (cache apagado) até a próxima varredura (300 s)
    [InlineData(true)]  // editado de novo durante o envio: vira "excluído na nuvem" → cópia de conflito duplicada
    public async Task S12_1_varredura_do_compartilhado_nao_remove_o_que_subiu_durante_a_listagem(bool editDuringUpload)
    {
        using var h = new Harness();
        var (o, sub) = ShortcutSetup(h, writable: true);
        const string dir = "Trabalho/Projeto do Dono";
        var entered = Tcs();
        var release = Tcs();
        o.BeforeUpload = async (name, ct) =>
        {
            if (name != "novo.txt") return;
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        h.WriteFile(dir + "/novo.txt", "versão 1");
        Assert.True(await entered.Task.WithinAsync(Long), "o envio não começou");
        if (editDuringUpload) h.WriteFile(dir + "/novo.txt", "versão 2, editada durante o envio", create: false);

        var listed = Tcs();
        var deliver = Tcs();
        var pages = 0;
        o.AfterChildrenPage = async ct =>
        {
            if (Interlocked.Increment(ref pages) != 2) return;
            listed.TrySetResult(); // snapshot das duas pastas pronto; o upload ainda está bloqueado
            await deliver.Task.WaitAsync(ct);
        };
        var scan = Task.Run(() => sub.RunDeltaAsync(CancellationToken.None), TestSupport.Ct);
        try
        {
            Assert.True(await listed.Task.WithinAsync(Long), "listagem das duas pastas");
            release.TrySetResult();
            Harness.WaitUntil(() => h.Db.Get(Lid(h, dir + "/novo.txt"))!.ItemId != null, "upload concluído durante a listagem");
        }
        finally { release.TrySetResult(); deliver.TrySetResult(); }
        Assert.True(await scan.WithinAsync(Long), "varredura não terminou");
        o.BeforeUpload = null;
        o.AfterChildrenPage = null;

        var it = h.Fs.Resolve(h.P(dir + "/novo.txt"))?.Item;
        Assert.True(it != null, "novo.txt foi removido localmente pelo Sweep da sub-drive (subiu durante a listagem)");
        Assert.True(it!.ItemId != null && h.Db.Conflicts().Count == 0,
            $"o Sweep tratou novo.txt como excluído na nuvem: ItemId={it.ItemId ?? "null"}, conflitos=[{string.Join("; ", h.Db.Conflicts().Select(c => c.Reason))}]");
        Assert.True(it.Dirty || it.CachedCTag != null, "o conteúdo local de novo.txt foi apagado do cache pelo Sweep");
    }

    // ---------- 12.2 mesma corrida na drive principal (enumeração completa) ----------

    /// <summary>
    /// Enumeração completa (carga inicial, 410/resync, "Restaurar" exclusões zera o deltaLink): a página é buscada sem
    /// _mutate; se um envio (que segura _mutate) termina antes de a página ser aplicada, o item novo não está na página
    /// nem em <c>seen</c> → Sweep. Com uma página só (drive pequena) isso independe da Graph: a resposta foi montada
    /// antes do envio existir.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task S12_2_enumeracao_completa_nao_remove_arquivo_enviado_durante_ela(bool editDuringUpload)
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "existente.txt", "x");
        h.Settle();
        var entered = Tcs();
        var release = Tcs();
        h.Remote.BeforeUpload = async (name, ct) =>
        {
            if (name != "novo.txt") return;
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        h.WriteFile("novo.txt", "versão 1");
        Assert.True(await entered.Task.WithinAsync(Long), "o envio não começou");
        if (editDuringUpload) h.WriteFile("novo.txt", "versão 2, editada durante o envio", create: false);

        var listed = Tcs();
        var deliver = Tcs();
        h.Remote.AfterDeltaPage = async ct =>
        {
            listed.TrySetResult(); // página pronta antes de o arquivo existir na nuvem
            await deliver.Task.WaitAsync(ct);
        };
        h.Db.SetDeltaLink(FakeGraph.DriveId, null); // o que o reset do 410 faz
        var full = Task.Run(() => h.Drive.RunDeltaAsync(CancellationToken.None), TestSupport.Ct);
        try
        {
            Assert.True(await listed.Task.WithinAsync(Long), "página da enumeração completa");
            release.TrySetResult();
            Harness.WaitUntil(() => h.Db.Get(Lid(h, "novo.txt"))!.ItemId != null, "upload concluído durante a listagem");
        }
        finally { release.TrySetResult(); deliver.TrySetResult(); }
        Assert.True(await full.WithinAsync(Long), "enumeração não terminou");
        h.Remote.BeforeUpload = null;
        h.Remote.AfterDeltaPage = null;

        var it = h.Fs.Resolve(h.P("novo.txt"))?.Item;
        Assert.True(it != null, "novo.txt foi removido localmente pelo Sweep da enumeração completa");
        Assert.True(it!.ItemId != null && h.Db.Conflicts().Count == 0,
            $"o Sweep tratou novo.txt como excluído na nuvem: ItemId={it.ItemId ?? "null"}, conflitos=[{string.Join("; ", h.Db.Conflicts().Select(c => c.Reason))}]");
        Assert.True(it.Dirty || it.CachedCTag != null, "o conteúdo local de novo.txt foi apagado do cache pelo Sweep");
    }

    // ---------- 12.3 fd de escrita aberto ao encerrar ----------

    /// <summary>
    /// <c>Write</c> marca Dirty, mas o Upload só entra na fila no <c>Release</c>. Desmontar (fuse_session_exit) ou cair
    /// com o fd aberto → o release nunca chega; o <c>Start()</c> seguinte não procura Dirty sem operação.
    /// </summary>
    [Fact]
    public void S12_3_escrita_com_fd_aberto_ao_encerrar_sobe_no_proximo_inicio()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "notas.txt", "v1 nuvem");
        h.Settle();
        Assert.Equal(0, h.Fs.Open(h.P("notas.txt"), OpenFlags.RdWr, 0, out var fh));
        var data = Encoding.UTF8.GetBytes("v2 local, escrita com o arquivo ainda aberto");
        Assert.Equal(data.Length, h.Fs.Write(fh, data, 0));
        // sem Release: a desmontagem/queda acontece com o arquivo aberto
        h.Engine.Stop();

        using var engine = Restart(h);
        Harness.WaitUntil(() => engine.GetDrive(FakeGraph.DriveId)?.LastSync != null, "carga após reinício");
        engine.SyncNow();
        Thread.Sleep(h.Config.UploadSettleMs + 1500);
        Harness.WaitUntil(() => h.Db.PendingOps() == 0 && engine.GetDrive(FakeGraph.DriveId) is { Busy: false }, "fila vazia");

        var item = h.Db.Get(Lid(h, "notas.txt"))!;
        Assert.True(h.Remote.ReadText("/notas.txt") == "v2 local, escrita com o arquivo ainda aberto",
            $"a alteração nunca subiu: nuvem='{h.Remote.ReadText("/notas.txt")}', local Dirty={item.Dirty}, operações pendentes={h.Db.PendingOps()}");
    }

    // ---------- 12.4 ações em itens de atalho/compartilhado usam a chave interna da sub-drive ----------

    [Fact]
    public async Task S12_4a_copiar_link_de_item_em_atalho()
    {
        using var h = new Harness();
        var (o, _) = ShortcutSetup(h, writable: false);
        var a = o.FindByPath("/Projeto/a.txt")!;
        var r = await new ItemActions(h.Engine, h.Fs).CreateShareLinkAsync(Lid(h, "Trabalho/Projeto do Dono/a.txt"), edit: false, anyone: false, CancellationToken.None);
        Assert.Equal($"https://1drv.ms/fake/{a.Id}?view&anonymous", r.Url);
    }

    [Fact]
    public async Task S12_4b_versoes_de_item_em_atalho()
    {
        using var h = new Harness();
        var (o, _) = ShortcutSetup(h, writable: true);
        o.RemoteUpdate("/Projeto/a.txt", "A versão 2");
        var list = await new ItemActions(h.Engine, h.Fs).ListVersionsAsync(Lid(h, "Trabalho/Projeto do Dono/a.txt"), CancellationToken.None);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task S12_4c_baixar_copia_de_versao_de_item_em_atalho()
    {
        using var h = new Harness();
        var (o, _) = ShortcutSetup(h, writable: true);
        var name = await new ItemActions(h.Engine, h.Fs).DownloadVersionCopyAsync(Lid(h, "Trabalho/Projeto do Dono/a.txt"),
            new DriveItemVersion("1.0", DateTimeOffset.UtcNow.AddDays(-1), 10, "Teste"), CancellationToken.None);
        Assert.Contains(name, Ls(h, h.P("Trabalho/Projeto do Dono")));
    }

    // ---------- 12.5 mv local para nome que já existe na nuvem ----------

    /// <summary>
    /// O VFS só confere o banco local; o PATCH usa conflictBehavior=replace e a nuvem apaga o arquivo que outro
    /// dispositivo criou depois do último delta. O FakeGraph imita o replace (HIPÓTESE sobre a Graph/SharePoint).
    /// </summary>
    [Fact]
    public void S12_5_mv_para_nome_existente_na_nuvem_nao_apaga_o_arquivo_de_outro_dispositivo()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "rascunho.txt", "meu rascunho");
        h.Settle();
        h.Remote.RemoteCreate("/", "final.txt", "versão de outro dispositivo"); // depois do último delta
        Assert.Equal(0, h.Fs.Rename(h.P("rascunho.txt"), h.P("final.txt"), 0));
        h.Settle();
        var texts = RemoteTexts(h.Remote);
        Assert.True(texts.Contains("versão de outro dispositivo"),
            $"o arquivo do outro dispositivo foi substituído (foi para a Lixeira): nuvem=[{string.Join(", ", h.Remote.ListPaths())}]");
    }

    // ---------- 12.6 conflito 412 com o arquivo aberto ----------

    /// <summary>
    /// HandleEditConflictAsync faz File.Move do arquivo do cache para a cópia; um handle aberto continua apontando para
    /// o mesmo inode — as escritas seguintes caem na cópia (já enviada) e o original fica Dirty sem arquivo.
    /// </summary>
    [Fact]
    public void S12_6_conflito_de_edicao_com_arquivo_aberto_nao_perde_escritas_seguintes()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "planilha.txt", "v1");
        h.Settle();
        h.WriteFile("planilha.txt", "edição local A", create: false); // enfileira o envio
        h.Remote.RemoteUpdate("/planilha.txt", "edição de outro dispositivo"); // o envio vai dar 412
        Assert.Equal(0, h.Fs.Open(h.P("planilha.txt"), OpenFlags.RdWr, 0, out var fh)); // editor mantém aberto
        Harness.WaitUntil(() => h.Db.Conflicts().Count == 1 && h.Db.PendingOps() == 0 && !h.Drive.Busy, "conflito resolvido", 20_000);

        var later = Encoding.UTF8.GetBytes("edição local B, depois do conflito");
        Assert.Equal(later.Length, h.Fs.Write(fh, later, 0));
        Assert.Equal(0, h.Fs.Release(fh));
        h.Settle();

        var texts = RemoteTexts(h.Remote);
        Assert.Equal("edição de outro dispositivo", h.Remote.ReadText("/planilha.txt"));
        var orig = h.Db.Get(Lid(h, "planilha.txt"))!;
        Assert.True(texts.Contains("edição local B, depois do conflito"),
            $"a escrita depois do 412 não chegou à nuvem: nuvem=[{string.Join(" | ", texts)}]; original Dirty={orig.Dirty}, pendentes={h.Db.PendingOps()}");
    }

    // ---------- 12.8 retomada de sessão já concluída ----------

    /// <summary>
    /// O servidor aceitou a última parte e gravou o arquivo, mas a resposta se perdeu. O reenvio/retomada encontra a
    /// sessão inexistente (404, como o servidor de sessões do FakeGraph responde — HIPÓTESE sobre a Graph real) →
    /// "expirou" → nova sessão com conflictBehavior=fail → 409 → cópia de conflito e arquivo duplicado na nuvem.
    /// </summary>
    [Fact]
    public void S12_8_resposta_perdida_da_ultima_parte_nao_duplica_o_arquivo()
    {
        using var h = new Harness();
        h.Remote.RealUploadSessions = true;
        h.Remote.LoseResponseAfterUploadComplete = true;
        var data = StreamingTests.Bytes(5 * 1024 * 1024, 7);
        Assert.Equal(0, h.Fs.Create(h.P("grande.bin"), OpenFlags.RdWr, out var fh));
        Assert.Equal(data.Length, h.Fs.Write(fh, data, 0));
        Assert.Equal(0, h.Fs.Release(fh));
        Harness.WaitUntil(() => h.Remote.FindByPath("/grande.bin") != null, "primeiro envio gravado", 20_000);
        // espera as novas tentativas (reenvio da parte → 404 → "expirou" → nova sessão) terminarem
        Harness.WaitUntil(() => h.Db.PendingOps() == 0 && !h.Drive.Busy, "fila vazia", 60_000);
        h.Settle();
        Assert.Equal(1, h.Remote.SessionCreatesAttempted); // confere hash ANTES de tentar abrir uma sessão nova
        Assert.True(h.Remote.ListPaths().SequenceEqual(["/grande.bin"]) && h.Db.Conflicts().Count == 0,
            $"envio duplicado: nuvem=[{string.Join(", ", h.Remote.ListPaths())}], conflitos=[{string.Join("; ", h.Db.Conflicts().Select(c => c.Copy + ": " + c.Reason))}]");
    }
}
