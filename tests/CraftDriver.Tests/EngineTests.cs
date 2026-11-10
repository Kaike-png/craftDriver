using CraftDriver.Core.Config;
using CraftDriver.Core.Graph;
using CraftDriver.Core.State;
using CraftDriver.Core.Sync;
using CraftDriver.Core.Vfs;

namespace CraftDriver.Tests;

/// <summary>Motor + VFS + Graph falso, em diretório temporário.</summary>
public sealed class Harness : IDisposable
{
    public readonly string Dir;
    public readonly FakeGraph Remote;
    public readonly AppConfig Config;
    public readonly StateDb Db;
    public string DbPath => Path.Combine(Dir, "state.db");
    public readonly SyncEngine Engine;
    public readonly VirtualFs Fs;
    public const string Name = "OneDrive - Teste";

    /// <param name="configure">Ajustes na configuração antes de o motor existir (ex.: cache em outro sistema de arquivos).</param>
    public Harness(Action<AppConfig>? configure = null) : this(configure, null, null, null) { }

    /// <summary>
    /// Reabrir o "app": mesma pasta (banco e cache) e mesma nuvem falsa de uma instância anterior já parada;
    /// <paramref name="beforeStart"/> roda com o motor criado e ainda não iniciado (como o AppHost.Start).
    /// </summary>
    public Harness(Action<AppConfig>? configure, string? dir, FakeGraph? remote, Action<SyncEngine>? beforeStart)
    {
        Dir = dir ?? Path.Combine(Path.GetTempPath(), "craftdriver-test-" + Guid.NewGuid().ToString("N")[..8]);
        Remote = remote ?? new FakeGraph();
        // menor que o padrão (3 s) para a suíte ficar rápida, mas maior que a pausa do teste de salvamento
        Directory.CreateDirectory(Dir);
        Config = new AppConfig
        {
            UploadSettleMs = 1000,
            CacheDir = Path.Combine(Dir, "cache"),
            PollIntervalSeconds = 5,
            Accounts = [new AccountConfig { Id = "acc", Username = "teste@exemplo.com" }],
            Drives = [new DriveConfig { DriveId = FakeGraph.DriveId, AccountId = "acc", DisplayName = Name }],
        };
        configure?.Invoke(Config);
        Db = new StateDb(DbPath);
        Engine = new SyncEngine(Config, Db, new ContentCache(Config.CacheDir), new ActivityLog(Path.Combine(Dir, "log.txt")), _ => Remote);
        Fs = new VirtualFs(Engine);
        beforeStart?.Invoke(Engine);
        Engine.Start();
        WaitUntil(() => Engine.GetDrive(FakeGraph.DriveId)?.LastSync != null, "carga inicial");
    }

    public DriveSync Drive => Engine.GetDrive(FakeGraph.DriveId)!;

    public static void WaitUntil(Func<bool> cond, string what, int timeoutMs = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Esperando: " + what);
            Thread.Sleep(50);
        }
    }

    /// <summary>Espera a fila esvaziar e aplica o delta remoto.</summary>
    public void Settle()
    {
        Engine.SyncNow();
        WaitUntil(() => Db.PendingOps() == 0 && !Drive.Busy, "fila vazia");
        Drive.RunDeltaAsync(CancellationToken.None).GetAwaiter().GetResult();
        WaitUntil(() => Db.PendingOps() == 0 && !Drive.Busy, "fila vazia (2)");
    }

    public string P(string rel) => "/" + Name + (rel.StartsWith('/') ? rel : "/" + rel);

    public void WriteFile(string rel, string text, bool create = true)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        ulong fh;
        int r = create && Fs.Resolve(P(rel)) == null
            ? Fs.Create(P(rel), OpenFlags.RdWr, out fh)
            : Fs.Open(P(rel), OpenFlags.RdWr | OpenFlags.Trunc, 0, out fh);
        Assert.Equal(0, r);
        Assert.Equal(bytes.Length, Fs.Write(fh, bytes, 0));
        Assert.Equal(0, Fs.Release(fh));
    }

    public string ReadFile(string rel)
    {
        Assert.Equal(0, Fs.Open(P(rel), 0, 0, out var fh));
        var buf = new byte[1 << 20];
        var n = Fs.Read(fh, buf, 0);
        Fs.Release(fh);
        Assert.True(n >= 0, $"read retornou {n}");
        return System.Text.Encoding.UTF8.GetString(buf, 0, n);
    }

    /// <summary>Para o motor e fecha o banco sem apagar a pasta (para reabrir com o outro construtor).</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        Engine.Stop();
        Thread.Sleep(300); // laços terminando a volta atual antes de fechar o banco
        Db.Dispose();
    }

    bool _closed;

    public void Dispose()
    {
        if (!_closed)
        {
            Engine.Stop();
            Db.Dispose();
        }
        try { Directory.Delete(Dir, true); } catch { }
    }
}

public class QuickXorHashTests
{
    [Theory]
    [InlineData("", "AAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("Sg==", "SgAAAAAAAAAAAAAAAQAAAAAAAAA=")]
    [InlineData("jRRDVA==", "jaDAEKgAAAAAAAAABAAAAAAAAAA=")]
    [InlineData("J9iPGCbfZSTNyw==", "J8DGIzBggm+UgQTNUgYAAAAAAAA=")]
    [InlineData("2fuxhBJXtpWFe8dOfdGeHw==", "O9tHLAghgSvYohKFyMMxnNCHaHg=")]
    [InlineData("NbYXsp5/K6mR+NmHwExjvWeWDJFnXTKWVlzYHoesp2E=", "wjuAuWDiq04qDt1R8hHWDDcwVoQ=")]
    [InlineData("4SVmjtXIL8BB8SfkbR5Cpaljm2jpyUfAhIBf65XmKxHlz9dy5XixgiE/q1lv+esZW/E=", "wxZ0rxkMQEnRNAp8ZgEZLT4RdLM=")]
    public void Vetores_do_rclone(string input, string expected) =>
        Assert.Equal(expected, QuickXorHash.Compute(Convert.FromBase64String(input)));
}

public class NamePolicyTests
{
    [Theory]
    [InlineData("relatorio.docx", false)]
    [InlineData("a:b.txt", true)]
    [InlineData("CON", true)]
    [InlineData("termina.", true)]
    [InlineData(".~lock.planilha.ods#", true)]
    [InlineData("~$documento.docx", true)]
    [InlineData(".goutputstream-ABC123", true)]
    [InlineData("notas.md", false)]
    public void Local_only(string name, bool expected) =>
        Assert.Equal(expected, new NamePolicy(new AppConfig()).IsLocalOnly(name));
}

public class EngineTests
{
    [Fact]
    public void Carga_inicial_lista_arquivos_sem_baixar()
    {
        using var h0 = new Harness();
        h0.Remote.RemoteCreate("/", "Docs", null, folder: true);
        h0.Remote.RemoteCreate("/Docs", "a.txt", "conteúdo A");
        h0.Settle();

        var entries = new List<(string, FileAttr)>();
        Assert.Equal(0, h0.Fs.ReadDir(h0.P("/Docs"), entries));
        Assert.Single(entries);
        Assert.Equal("a.txt", entries[0].Item1);
        Assert.Equal(0, h0.Remote.Downloads); // sob demanda: nada baixado
        Assert.Equal(0, entries[0].Item2.Blocks);

        Assert.Equal("conteúdo A", h0.ReadFile("/Docs/a.txt"));
        Assert.Equal(1, h0.Remote.Downloads);
        Assert.Equal("conteúdo A", h0.ReadFile("/Docs/a.txt"));
        Assert.Equal(1, h0.Remote.Downloads); // segunda leitura vem do cache
    }

    [Fact]
    public void Criar_pasta_e_arquivo_envia_para_nuvem()
    {
        using var h = new Harness();
        Assert.Equal(0, h.Fs.Mkdir(h.P("/Projetos")));
        Assert.Equal(0, h.Fs.Mkdir(h.P("/Projetos/Sub")));
        h.WriteFile("/Projetos/Sub/nota.txt", "olá nuvem");
        h.Settle();
        Assert.Equal("olá nuvem", h.Remote.ReadText("/Projetos/Sub/nota.txt"));
    }

    [Fact]
    public void Alteracao_remota_invalida_cache()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "x.txt", "v1");
        h.Settle();
        Assert.Equal("v1", h.ReadFile("/x.txt"));
        h.Remote.RemoteUpdate("/x.txt", "versão 2");
        h.Settle();
        Assert.Equal("versão 2", h.ReadFile("/x.txt"));
    }

    [Fact]
    public void Edicao_local_sobrescreve_mesmo_item()
    {
        using var h = new Harness();
        var n = h.Remote.RemoteCreate("/", "doc.txt", "original");
        h.Settle();
        h.WriteFile("/doc.txt", "editado", create: false);
        h.Settle();
        Assert.Equal("editado", h.Remote.ReadText("/doc.txt"));
        Assert.Equal(n.Id, h.Remote.FindByPath("/doc.txt")!.Id);
    }

    [Fact]
    public void Salvamento_atomico_preserva_id_remoto()
    {
        using var h = new Harness();
        var n = h.Remote.RemoteCreate("/", "texto.md", "v1");
        h.Settle();
        // padrão gedit/kate: grava temporário e renomeia por cima
        h.WriteFile("/.goutputstream-XYZ", "v2 do editor");
        Assert.Equal(0, h.Fs.Rename(h.P("/.goutputstream-XYZ"), h.P("/texto.md"), 0));
        h.Settle();
        Assert.Equal("v2 do editor", h.Remote.ReadText("/texto.md"));
        Assert.Equal(n.Id, h.Remote.FindByPath("/texto.md")!.Id);
        Assert.DoesNotContain(h.Remote.ListPaths(), p => p.Contains("goutputstream"));
    }

    [Fact]
    public void Salvamento_com_temporario_de_nome_comum_nao_cria_item_extra_na_nuvem()
    {
        using var h = new Harness();
        var n = h.Remote.RemoteCreate("/", "planilha.xlsx", "v1");
        h.Settle();
        var uploads = h.Remote.Uploads;
        // editores que gravam num temporário sem padrão conhecido e renomeiam por cima logo após fechar
        h.WriteFile("/planilha.xlsx.sb-1a2b3c", "v2 do editor");
        Thread.Sleep(300);
        Assert.Equal(0, h.Fs.Rename(h.P("/planilha.xlsx.sb-1a2b3c"), h.P("/planilha.xlsx"), 0));
        h.Settle();
        Assert.Equal("v2 do editor", h.Remote.ReadText("/planilha.xlsx"));
        Assert.Equal(n.Id, h.Remote.FindByPath("/planilha.xlsx")!.Id);
        Assert.Equal(0, h.Remote.Deletes);          // nada vai para a Lixeira
        Assert.Equal(1, h.Remote.Uploads - uploads); // só a planilha é enviada
        Assert.DoesNotContain(h.Remote.ListPaths(), p => p.Contains(".sb-"));
    }

    [Fact]
    public void Temporarios_nao_sobem()
    {
        using var h = new Harness();
        h.WriteFile("/.~lock.planilha.ods#", "lock");
        h.WriteFile("/~$relatorio.docx", "lock office");
        h.Settle();
        Assert.Empty(h.Remote.ListPaths());
        Assert.Equal(0, h.Fs.GetAttr(h.P("/.~lock.planilha.ods#"), out _));
    }

    [Fact]
    public async Task Conflito_de_edicao_mantem_as_duas_versoes()
    {
        using var h = new Harness();
        var events = new List<ConflictRecord>();
        h.Engine.ConflictDetected += c => { lock (events) events.Add(c); };
        h.Remote.RemoteCreate("/", "plano.txt", "base");
        h.Settle();
        h.ReadFile("/plano.txt");
        h.Engine.Paused = true; // segura o envio
        h.WriteFile("/plano.txt", "versão local", create: false);
        h.Remote.RemoteUpdate("/plano.txt", "versão remota");
        await h.Drive.RunDeltaAsync(CancellationToken.None);
        h.Engine.Paused = false;
        h.Settle();

        var paths = h.Remote.ListPaths();
        Assert.Equal("versão remota", h.Remote.ReadText("/plano.txt"));
        var conflict = paths.Single(p => p.Contains("conflito"));
        Assert.Equal("versão local", h.Remote.ReadText(conflict));
        Assert.Equal("versão remota", h.ReadFile("/plano.txt"));

        // fica registrado (aba Conflitos) apontando para a cópia, e avisa uma vez
        var rec = Assert.Single(h.Db.Conflicts());
        Assert.Equal(FakeGraph.DriveId, rec.DriveId);
        Assert.Equal("plano.txt", rec.Original);
        Assert.Equal(conflict.TrimStart('/'), rec.Copy);
        Assert.Equal(h.Fs.Resolve(h.P(conflict))!.Value.Item!.Lid, rec.Lid);
        Assert.Contains("edição", rec.Reason);
        Assert.Equal(rec.Id, Assert.Single(events).Id);

        h.Db.DismissConflict(rec.Id);
        Assert.Empty(h.Db.Conflicts());
        Assert.Contains(h.Remote.ListPaths(), p => p.Contains("conflito")); // dispensar não apaga a cópia
    }

    [Fact]
    public void Renomear_mover_e_excluir()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "A", null, folder: true);
        h.Remote.RemoteCreate("/", "B", null, folder: true);
        var f = h.Remote.RemoteCreate("/A", "f.txt", "dados");
        h.Settle();

        Assert.Equal(0, h.Fs.Rename(h.P("/A/f.txt"), h.P("/B/g.txt"), 0));
        h.Settle();
        Assert.Equal(f.Id, h.Remote.FindByPath("/B/g.txt")!.Id);
        Assert.Equal(0, h.Remote.Downloads); // mover não baixa

        Assert.Equal(-Errno.ENOTEMPTY, h.Fs.Rmdir(h.P("/B")));
        Assert.Equal(0, h.Fs.Unlink(h.P("/B/g.txt")));
        Assert.Equal(0, h.Fs.Rmdir(h.P("/B")));
        h.Settle();
        Assert.Equal(["/A"], h.Remote.ListPaths());
    }

    [Fact]
    public void Apagar_pasta_recursivamente_gera_uma_exclusao_na_nuvem()
    {
        using var h = new Harness();
        h.Config.MassDeleteThreshold = 0; // aviso desativado: testa só o agrupamento
        h.Remote.RemoteCreate("/", "Anexos", null, folder: true);
        h.Remote.RemoteCreate("/Anexos", "Sub", null, folder: true);
        for (var i = 0; i < 50; i++) h.Remote.RemoteCreate(i % 2 == 0 ? "/Anexos" : "/Anexos/Sub", $"f{i}.pdf", "x");
        h.Remote.RemoteCreate("/", "manter.txt", "fica");
        h.Settle();

        // como o Dolphin/rm -r: arquivos primeiro, depois as pastas (de dentro para fora)
        for (var i = 1; i < 50; i += 2) Assert.Equal(0, h.Fs.Unlink(h.P($"/Anexos/Sub/f{i}.pdf")));
        Assert.Equal(0, h.Fs.Rmdir(h.P("/Anexos/Sub")));
        for (var i = 0; i < 50; i += 2) Assert.Equal(0, h.Fs.Unlink(h.P($"/Anexos/f{i}.pdf")));
        Assert.Equal(0, h.Fs.Rmdir(h.P("/Anexos")));
        h.Settle();

        Assert.Equal(1, h.Remote.Deletes);
        Assert.Equal(["/manter.txt"], h.Remote.ListPaths());
    }

    static void ApagarAnexos(Harness h, int arquivos)
    {
        h.Remote.RemoteCreate("/", "Anexos", null, folder: true);
        for (var i = 0; i < arquivos; i++) h.Remote.RemoteCreate("/Anexos", $"f{i}.pdf", "x");
        h.Remote.RemoteCreate("/", "manter.txt", "fica");
        h.Settle();
        for (var i = 0; i < arquivos; i++) Assert.Equal(0, h.Fs.Unlink(h.P($"/Anexos/f{i}.pdf")));
        Assert.Equal(0, h.Fs.Rmdir(h.P("/Anexos")));
    }

    [Fact]
    public void Exclusao_em_massa_fica_retida_ate_confirmar()
    {
        using var h = new Harness();
        h.Config.MassDeleteThreshold = 10;
        DriveSync? detected = null;
        h.Engine.MassDeleteDetected += d => detected = d;
        ApagarAnexos(h, 30);

        Thread.Sleep(h.Drive.DeleteQuietMs + 1500);
        h.Engine.SyncNow();
        Thread.Sleep(500);
        Assert.Same(h.Drive, detected);
        Assert.True(h.Drive.DeleteHold);
        Assert.Equal(31, h.Drive.HeldDeleteCount);
        Assert.Equal(0, h.Remote.Deletes); // nada saiu para a nuvem
        Assert.Equal(EngineStatus.DeleteConfirmation, h.Engine.Status);
        Assert.Equal((true, 31L), h.Db.GetDeleteHold(FakeGraph.DriveId)); // persiste para reinícios

        h.Drive.ConfirmDeletes();
        h.Settle();
        Assert.False(h.Drive.DeleteHold);
        Assert.Equal(1, h.Remote.Deletes); // a pasta inteira numa chamada só
        Assert.Equal(["/manter.txt"], h.Remote.ListPaths());
    }

    [Fact]
    public void Exclusao_em_massa_lista_o_que_foi_apagado()
    {
        using var h = new Harness();
        h.Config.MassDeleteThreshold = 10;
        h.Remote.RemoteCreate("/", "Docs", null, folder: true);
        h.Remote.RemoteCreate("/Docs", "Sub", null, folder: true);
        for (var i = 0; i < 3; i++) h.Remote.RemoteCreate("/Docs", $"solto{i}.txt", "x");
        ApagarAnexos(h, 8);
        // pasta inteira (com subpasta), arquivos soltos numa pasta que fica, e a subpasta vazia dela
        for (var i = 0; i < 3; i++) Assert.Equal(0, h.Fs.Unlink(h.P($"/Docs/solto{i}.txt")));
        Assert.Equal(0, h.Fs.Rmdir(h.P("/Docs/Sub")));
        Harness.WaitUntil(() => h.Drive.DeleteHold, "retenção");

        var held = h.Drive.HeldDeletes();
        // a pasta apagada aparece uma vez, com o total dentro; o conteúdo dela não é listado item a item
        Assert.Equal(
            [("/Anexos", true, 9), ("/Docs/solto0.txt", false, 1), ("/Docs/solto1.txt", false, 1),
             ("/Docs/solto2.txt", false, 1), ("/Docs/Sub", true, 1)],
            held.Select(x => (x.Path, x.IsFolder, x.Count)).ToList());
        Assert.Equal(13, held.Sum(x => x.Count));

        // limite: os primeiros N mais o total continua certo para o "e mais X"
        var first = h.Drive.HeldDeletes(limit: 2);
        Assert.Equal(2, first.Count);
        Assert.Equal("/Anexos", first[0].Path);

        h.Drive.ConfirmDeletes();
        h.Settle();
        Assert.Empty(h.Drive.HeldDeletes());
    }

    [Fact]
    public async Task Exclusao_em_massa_pode_ser_restaurada()
    {
        using var h = new Harness();
        h.Config.MassDeleteThreshold = 10;
        ApagarAnexos(h, 30);
        Harness.WaitUntil(() => h.Drive.DeleteHold, "retenção");
        Assert.Equal(-Errno.ENOENT, h.Fs.GetAttr(h.P("/Anexos"), out _));

        await h.Drive.RestoreDeletesAsync(TestContext.Current.CancellationToken);
        Harness.WaitUntil(() => h.Fs.Resolve(h.P("/Anexos/f29.pdf")) != null, "itens restaurados");
        h.Settle();

        var entries = new List<(string, FileAttr)>();
        Assert.Equal(0, h.Fs.ReadDir(h.P("/Anexos"), entries));
        Assert.Equal(30, entries.Count);
        Assert.Equal(0, h.Remote.Deletes);
        Assert.Equal(32, h.Remote.ListPaths().Count);
        Assert.False(h.Drive.DeleteHold);
    }

    [Fact]
    public void Poucas_exclusoes_nao_pedem_confirmacao()
    {
        using var h = new Harness();
        ApagarAnexos(h, 5);
        h.Settle();
        Assert.False(h.Drive.DeleteHold);
        Assert.Equal(1, h.Remote.Deletes);
    }

    [Fact]
    public void Exclusao_remota_remove_local()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "Pasta", null, folder: true);
        h.Remote.RemoteCreate("/Pasta", "1.txt", "1");
        h.Settle();
        h.Remote.RemoteDelete("/Pasta");
        h.Settle();
        Assert.Equal(-Errno.ENOENT, h.Fs.GetAttr(h.P("/Pasta"), out _));
    }

    [Fact]
    public void Offline_enfileira_e_envia_ao_voltar()
    {
        using var h = new Harness();
        h.Remote.OfflineMode = true;
        h.WriteFile("/offline.txt", "escrito sem internet");
        Harness.WaitUntil(() => h.Drive.Offline, "detectar offline");
        Assert.Equal("escrito sem internet", h.ReadFile("/offline.txt"));
        h.Remote.OfflineMode = false;
        // backoff: força nova tentativa
        if (h.Db.PeekOp(FakeGraph.DriveId) is { } op) h.Db.FailOp(op.Id, "teste", TimeSpan.Zero); // a op está em backoff (próxima tentativa no futuro): agenda para já
        h.Settle();
        Assert.Equal("escrito sem internet", h.Remote.ReadText("/offline.txt"));
    }

    [Fact]
    public void Fixar_baixa_e_liberar_espaco_remove_cache()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "Fotos", null, folder: true);
        h.Remote.RemoteCreate("/Fotos", "1.jpg", "img1");
        h.Remote.RemoteCreate("/Fotos", "2.jpg", "img2");
        h.Settle();
        var fotos = h.Fs.Resolve(h.P("/Fotos"))!.Value.Item!;
        h.Engine.SetPinned(fotos.Lid, true);
        Harness.WaitUntil(() => h.Remote.Downloads == 2, "baixar fixados");
        var img = h.Fs.Resolve(h.P("/Fotos/1.jpg"))!.Value.Item!;
        Assert.Equal(ItemStatus.AlwaysAvailable, h.Fs.StatusOf(img));

        h.Engine.SetPinned(fotos.Lid, false);
        img = h.Fs.Resolve(h.P("/Fotos/1.jpg"))!.Value.Item!;
        Assert.Equal(ItemStatus.OnlineOnly, h.Fs.StatusOf(img));
    }

    [Fact]
    public async Task Arquivo_criado_nos_dois_lados_vira_conflito()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        h.WriteFile("/igual.txt", "daqui");
        h.Remote.RemoteCreate("/", "igual.txt", "de lá");
        await h.Drive.RunDeltaAsync(CancellationToken.None);
        h.Engine.Paused = false;
        h.Settle();
        var paths = h.Remote.ListPaths();
        Assert.Equal(2, paths.Count);
        Assert.Equal("de lá", h.Remote.ReadText("/igual.txt"));
        Assert.Equal("daqui", h.Remote.ReadText(paths.Single(p => p.Contains("conflito"))));
        var rec = Assert.Single(h.Db.Conflicts());
        Assert.Equal("igual.txt", rec.Original);
        Assert.Contains("conflito", rec.Copy);
    }
}
