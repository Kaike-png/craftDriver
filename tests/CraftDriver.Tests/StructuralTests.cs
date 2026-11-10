using System.Diagnostics;
using CraftDriver.Core.Config;
using CraftDriver.Core.State;
using CraftDriver.Core.Sync;
using CraftDriver.Core.Vfs;
using CraftDriver.Fuse;

namespace CraftDriver.Tests;

/// <summary>Débito estrutural (seção 5 do PENDENCIAS.md).</summary>
[Collection("fuse")]
public class InterruptTests
{
    [Fact]
    public async Task Ctrl_c_no_processo_cancela_o_download_do_open()
    {
        RealFuse.Require();
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "lento.bin", "conteúdo");
        h.Settle();
        var entered = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        h.Remote.BeforeDownload = async ct =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
        };

        var mnt = Path.Combine(h.Dir, "mnt");
        var loop = FuseHost.MountAsync(h.Fs, mnt);
        try
        {
            var root = Path.Combine(mnt, Harness.Name);
            Harness.WaitUntil(() => Directory.Exists(root), "montagem visível");
            using var cat = Process.Start(new ProcessStartInfo("cat", [Path.Combine(root, "lento.bin")]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            Assert.True(await entered.Task.WithinAsync(TimeSpan.FromSeconds(10)), "o download não começou");
            Process.Start("kill", ["-INT", cat.Id.ToString()])!.WaitForExit();
            // o kernel manda FUSE_INTERRUPT; o handler precisa cancelar o download em vez de seguir esperando
            Assert.True(await cancelled.Task.WithinAsync(TimeSpan.FromSeconds(5)), "o download continuou depois do Ctrl-C");
            Assert.True(cat.WaitForExit(5000), "cat não terminou");
        }
        finally
        {
            h.Engine.Stop(); // solta um handler que tenha ficado preso no download (token global)
            FuseHost.Unmount();
            Assert.True(await loop.WithinAsync(TimeSpan.FromSeconds(15)), "loop do FUSE não terminou após desmontar: " + FuseHost.Diagnostico());
        }
    }
}

public class ReadConnectionTests
{
    [Fact]
    public void Leituras_da_interface_nao_esperam_o_lock_do_motor()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        h.WriteFile("/a.txt", "x"); // 1 operação pendente
        using var held = new ManualResetEventSlim();
        var holder = new Thread(() => { lock (h.Db.Lock) { held.Set(); Thread.Sleep(1500); } });
        holder.Start();
        held.Wait(TestContext.Current.CancellationToken);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(1, h.Db.PendingOps());
        Assert.Equal(0, h.Db.FailureCount());
        Assert.Empty(h.Db.Failures());
        Assert.Empty(h.Db.AllPinned());
        var (files, _, _, dirty) = h.Db.Counts(FakeGraph.DriveId);
        sw.Stop();
        holder.Join();
        Assert.Equal(1, files);
        Assert.Equal(1, dirty);
        Assert.True(sw.ElapsedMilliseconds < 500, $"leituras esperaram {sw.ElapsedMilliseconds} ms pelo lock");
    }

    [Fact]
    public void Leitura_ve_o_que_foi_gravado_logo_antes()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        for (var i = 0; i < 5; i++)
        {
            h.WriteFile($"/f{i}.txt", "x");
            Assert.Equal(i + 1, h.Db.PendingOps());
        }
        // dentro de uma transação aberta pela conexão principal, a de leitura não vê o que ainda não foi confirmado
        h.Db.InTransaction(() =>
        {
            h.Db.Enqueue(FakeGraph.DriveId, OpKind.Mkdir, 999);
            Assert.Equal(5, h.Db.PendingOps());
        });
        Assert.Equal(6, h.Db.PendingOps());
    }
}

public class ConfigFileTests
{
    static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "craftdriver-cfg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Config_e_gravada_so_para_o_dono()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "config.json");
            new AppConfig().Save(path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            // arquivo antigo com 0644 também é corrigido na próxima gravação
            File.SetUnixFileMode(path, (UnixFileMode)0b110_100_100);
            new AppConfig().Save(path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Config_grava_a_versao_do_esquema_e_le_arquivos_antigos()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, """{ "PollIntervalSeconds": 42 }"""); // arquivo de versão sem SchemaVersion
            var cfg = AppConfig.Load(path);
            Assert.Null(cfg.LoadWarning);
            Assert.Equal(42, cfg.PollIntervalSeconds);
            cfg.Save(path);
            Assert.Contains($"\"SchemaVersion\": {AppConfig.CurrentSchemaVersion}", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Config_de_versao_mais_nova_nao_e_sobrescrita()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "config.json");
            var newer = $$"""{ "SchemaVersion": {{AppConfig.CurrentSchemaVersion + 1}}, "PollIntervalSeconds": 7, "CampoNovo": "x" }""";
            File.WriteAllText(path, newer);
            var cfg = AppConfig.Load(path);
            Assert.Contains("mais nova", cfg.LoadWarning);
            cfg.PollIntervalSeconds = 99;
            cfg.Save(path); // um craftDriver antigo apagaria "CampoNovo": não grava
            Assert.Equal(newer, File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// <summary>Downloads em andamento não podem ocupar as threads do FUSE (seção 3/5 do PENDENCIAS.md).</summary>
[Collection("fuse")]
public class NonBlockingFuseTests
{
    [Fact]
    public async Task Stat_responde_com_mais_downloads_presos_que_threads_do_fuse()
    {
        RealFuse.Require();
        using var h = new Harness();
        const int files = 6, workers = 4;
        for (var i = 0; i < files; i++) h.Remote.RemoteCreate("/", $"f{i}.bin", $"conteúdo {i}");
        h.Settle();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        h.Remote.BeforeDownload = async ct =>
        {
            Interlocked.Increment(ref entered);
            await release.Task.WaitAsync(ct);
        };

        var mnt = Path.Combine(h.Dir, "mnt");
        var loop = FuseHost.MountAsync(h.Fs, mnt, workers);
        var cats = new List<Process>();
        try
        {
            var root = Path.Combine(mnt, Harness.Name);
            Harness.WaitUntil(() => Directory.Exists(root), "montagem visível");
            for (var i = 0; i < files; i++)
                cats.Add(Process.Start(new ProcessStartInfo("cat", [Path.Combine(root, $"f{i}.bin")]) { RedirectStandardOutput = true })!);
            Harness.WaitUntil(() => Volatile.Read(ref entered) >= 4, "downloads começaram"); // o motor baixa 4 por vez
            Thread.Sleep(300); // os outros 2 open() ficam esperando vaga no motor

            // stat de arquivo inexistente e listagem: precisam de uma thread do FUSE livre
            var probe = Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                var exists = File.Exists(Path.Combine(root, "nao-existe"));
                var listed = Directory.GetFiles(root).Length;
                return (sw.Elapsed, exists, listed);
            });
            Assert.True(await probe.WithinAsync(TimeSpan.FromSeconds(3)), "stat/ls ficaram presos atrás dos downloads");
            var (elapsed, exists, listed) = await probe;
            Assert.False(exists);
            Assert.Equal(files, listed);
            Assert.True(elapsed < TimeSpan.FromSeconds(1), $"stat levou {elapsed.TotalMilliseconds:F0} ms");

            release.TrySetResult();
            for (var i = 0; i < files; i++)
            {
                Assert.True(cats[i].WaitForExit(10_000), $"cat {i} não terminou");
                Assert.Equal($"conteúdo {i}", cats[i].StandardOutput.ReadToEnd());
            }
        }
        finally
        {
            release.TrySetResult();
            foreach (var c in cats) { try { c.Kill(); } catch { } c.Dispose(); }
            h.Engine.Stop();
            FuseHost.Unmount();
            Assert.True(await loop.WithinAsync(TimeSpan.FromSeconds(15)), "loop do FUSE não terminou após desmontar: " + FuseHost.Diagnostico());
        }
    }
}

/// <summary>Velocidade, tempo restante e cancelamento de transferências (seção 6).</summary>
public class TransferControlTests
{
    [Fact]
    public void Velocidade_e_tempo_restante_pela_janela_recente()
    {
        long now = 0;
        var t = new Transfer(() => now) { Name = "a.bin", Total = 10_000_000 };
        Assert.Equal(0, t.BytesPerSecond);
        Assert.Null(t.Remaining);

        t.Done = 2_000_000; // retomado de onde parou: o salto inicial não conta como velocidade
        now = 1000; t.Done = 3_000_000;
        now = 2000; t.Done = 4_000_000;
        Assert.Equal(1_000_000, t.BytesPerSecond, 1);
        Assert.Equal(TimeSpan.FromSeconds(6), t.Remaining);

        // só os últimos segundos contam: mudou de ritmo, a estimativa acompanha
        for (var i = 1; i <= 10; i++) { now = 2000 + i * 1000; t.Done = 4_000_000 + i * 100_000; }
        Assert.Equal(100_000, t.BytesPerSecond, 1);
        Assert.Equal(TimeSpan.FromSeconds(50), t.Remaining);

        // parado (nenhum progresso há mais que a janela): sem estimativa
        now += 10_000;
        Assert.Equal(0, t.BytesPerSecond);
        Assert.Null(t.Remaining);
    }

    [Fact]
    public async Task Cancelar_download_do_open_devolve_ECANCELED_e_guarda_o_parcial()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "grande.bin", new string('x', 100_000));
        h.Settle();
        var entered = new TaskCompletionSource();
        h.Remote.BeforeDownload = async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        var open = Task.Run(() => h.Fs.Open(h.P("/grande.bin"), 0, 0, out _));
        Assert.True(await entered.Task.WithinAsync(5000), "download não começou");
        var transfer = Assert.Single(h.Engine.Activity.Transfers);
        Assert.False(transfer.Upload);

        Assert.True(h.Engine.Activity.CancelTransfer(transfer.Id));
        Assert.True(await open.WithinAsync(5000), "open não terminou depois de cancelar");
        Assert.Equal(-Errno.ECANCELED, await open);
        Assert.Empty(h.Engine.Activity.Transfers);
        Assert.False(h.Engine.Token.IsCancellationRequested); // só esta transferência
        Assert.Contains(h.Engine.Activity.Entries, e => e.Text.Contains("cancelado") && e.Text.Contains("grande.bin"));

        h.Remote.BeforeDownload = null; // abrir de novo funciona
        Assert.Equal(0, h.Fs.Open(h.P("/grande.bin"), 0, 0, out var fh));
        h.Fs.Release(fh);
    }

    [Fact]
    public async Task Cancelar_envio_vai_para_problemas_sem_travar_a_fila()
    {
        using var h = new Harness();
        var entered = new TaskCompletionSource();
        h.Remote.BeforeUpload = async (name, ct) =>
        {
            if (name != "lento.txt") return;
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        h.WriteFile("/lento.txt", "conteúdo");
        Assert.True(await entered.Task.WithinAsync(15_000), "envio não começou");
        h.WriteFile("/depois.txt", "outro");
        var transfer = Assert.Single(h.Engine.Activity.Transfers, t => t.Upload);

        Assert.True(h.Engine.Activity.CancelTransfer(transfer.Id));
        Harness.WaitUntil(() => h.Remote.ListPaths().Contains("/depois.txt"), "fila seguiu depois do cancelamento");
        var failure = Assert.Single(h.Db.Failures());
        Assert.Contains("cancelado", failure.Error);
        Assert.DoesNotContain("/lento.txt", h.Remote.ListPaths());

        // "Tentar de novo" na aba Problemas envia normalmente
        h.Remote.BeforeUpload = null;
        Assert.True(h.Drive.RetryFailure(failure.Id));
        h.Settle();
        Assert.Equal("conteúdo", h.Remote.ReadText("/lento.txt"));
    }
}

/// <summary>Limites de transferência mudam sem reiniciar (seção 7).</summary>
public class TransferLimitTests
{
    [Fact]
    public void Downloads_simultaneos_e_banda_mudam_na_hora()
    {
        using var h = new Harness();
        h.Config.DownloadConcurrency = 1;
        h.Drive.ApplyTransferLimits(); // a drive do Harness já foi criada com 4
        for (var i = 0; i < 3; i++) h.Remote.RemoteCreate("/", $"f{i}.bin", $"conteúdo {i}");
        h.Settle();
        var inside = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.BeforeDownload = async ct =>
        {
            Interlocked.Increment(ref inside);
            await release.Task.WaitAsync(ct);
        };
        var opens = Enumerable.Range(0, 3).Select(i => Task.Run(() =>
        {
            var r = h.Fs.Open(h.P($"/f{i}.bin"), 0, 0, out var fh);
            if (r == 0) h.Fs.Release(fh);
            return r;
        })).ToList();
        Harness.WaitUntil(() => Volatile.Read(ref inside) == 1, "primeiro download");
        Thread.Sleep(300);
        Assert.Equal(1, Volatile.Read(ref inside));

        h.Config.DownloadConcurrency = 3;
        h.Config.DownloadBytesPerSecond = 5_000_000;
        h.Config.UploadBytesPerSecond = 250_000;
        h.Engine.ApplyTransferLimits();
        Harness.WaitUntil(() => Volatile.Read(ref inside) == 3, "os outros dois começam sem reiniciar");
        Assert.Equal(5_000_000, h.Drive.DownloadBytesPerSecond);
        Assert.Equal(250_000, h.Remote.UploadLimits[FakeGraph.DriveId]);

        release.TrySetResult();
        Assert.All(opens, o => Assert.Equal(0, o.Result));
    }

    [Fact]
    public async Task Limitador_cancelado_na_fila_nao_ocupa_vaga_e_diminuir_vale_ao_liberar()
    {
        var l = new AdjustableLimiter(2);
        await l.WaitAsync(TestContext.Current.CancellationToken);
        await l.WaitAsync(TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        var cancelled = l.WaitAsync(cts.Token);
        var next = l.WaitAsync(TestContext.Current.CancellationToken);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        l.Release(); // a vaga vai para quem continua esperando, não para o cancelado
        await next.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        l.SetLimit(1); // 2 em uso, limite 1: só entra outro depois de liberar as duas
        var late = l.WaitAsync(TestContext.Current.CancellationToken);
        l.Release();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(late.IsCompleted);
        l.Release();
        await late.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }
}

/// <summary>Sincronização seletiva: pasta excluída some da montagem e nada dela é baixado (seção 7).</summary>
public class SelectiveSyncTests
{
    static Harness Setup()
    {
        var h = new Harness();
        h.Remote.RemoteCreate("/", "Fotos", null, folder: true);
        h.Remote.RemoteCreate("/Fotos", "Viagem", null, folder: true);
        h.Remote.RemoteCreate("/Fotos/Viagem", "praia.jpg", "jpeg");
        h.Remote.RemoteCreate("/Fotos", "capa.jpg", "capa");
        h.Remote.RemoteCreate("/", "Docs", null, folder: true);
        h.Remote.RemoteCreate("/Docs", "a.txt", "a");
        h.Settle();
        return h;
    }

    static long Lid(Harness h, string rel, bool includeExcluded = false) =>
        h.Fs.Resolve(h.P(rel), includeExcluded)!.Value.Item!.Lid;

    [Fact]
    public void Pasta_excluida_some_da_montagem_e_volta_ao_reincluir()
    {
        using var h = Setup();
        Assert.Equal("capa", h.ReadFile("/Fotos/capa.jpg")); // já baixado: o conteúdo sai do cache ao excluir
        var fotos = Lid(h, "/Fotos");
        var capa = Lid(h, "/Fotos/capa.jpg");
        Assert.True(h.Engine.Cache.Exists(FakeGraph.DriveId, capa));

        Assert.Null(h.Engine.SetExcluded(fotos, true));
        Assert.Null(h.Fs.Resolve(h.P("/Fotos")));
        Assert.Null(h.Fs.Resolve(h.P("/Fotos/Viagem/praia.jpg")));
        Assert.Equal(-Errno.ENOENT, h.Fs.GetAttr(h.P("/Fotos/capa.jpg"), out _));
        var entries = new List<(string name, FileAttr attr)>();
        Assert.Equal(0, h.Fs.ReadDir(h.P("/"), entries));
        Assert.Equal(["Docs"], entries.Select(e => e.name));
        Assert.False(h.Engine.Cache.Exists(FakeGraph.DriveId, capa));
        Assert.Equal([fotos], h.Db.ExcludedFolders(FakeGraph.DriveId).Select(i => i.Lid));
        Assert.Equal(0, h.Db.PendingOps()); // excluir não apaga nada na nuvem
        Assert.Equal(4, h.Remote.ListPaths().Count(p => p.StartsWith("/Fotos"))); // tudo continua na nuvem

        // mudança na nuvem dentro da pasta excluída: só metadados, nada baixado
        var downloads = h.Remote.Downloads;
        h.Remote.RemoteCreate("/Fotos/Viagem", "nova.jpg", "nova");
        h.Settle();
        Assert.Equal(downloads, h.Remote.Downloads);
        Assert.Null(h.Fs.Resolve(h.P("/Fotos/Viagem/nova.jpg")));

        Assert.Null(h.Engine.SetExcluded(fotos, false));
        Assert.Equal("nova", h.ReadFile("/Fotos/Viagem/nova.jpg"));
        Assert.Empty(h.Db.ExcludedFolders(FakeGraph.DriveId));
    }

    [Fact]
    public void Fixar_a_drive_inteira_nao_baixa_pasta_excluida()
    {
        using var h = Setup();
        var fotos = Lid(h, "/Fotos");
        h.Engine.SetPinned(Lid(h, "/Fotos/Viagem/praia.jpg"), true);
        Assert.Null(h.Engine.SetExcluded(fotos, true));
        Assert.False(h.Db.Get(Lid(h, "/Fotos/Viagem/praia.jpg", includeExcluded: true))!.Pinned); // fixação sai junto

        h.Engine.SetPinned(h.Drive.RootLid, true);
        Harness.WaitUntil(() => h.Engine.Cache.Exists(FakeGraph.DriveId, Lid(h, "/Docs/a.txt")), "Docs baixado");
        h.Settle();
        Assert.Empty(h.Db.PinnedNeedingDownload(FakeGraph.DriveId));
        Assert.False(h.Engine.Cache.Exists(FakeGraph.DriveId, Lid(h, "/Fotos/capa.jpg", includeExcluded: true)));
        Assert.False(h.Db.IsEffectivelyPinned(Lid(h, "/Fotos/capa.jpg", includeExcluded: true)));
    }

    [Fact]
    public void Nao_exclui_pasta_com_alteracoes_ainda_nao_enviadas_nem_a_raiz()
    {
        using var h = Setup();
        h.Engine.Paused = true;
        h.WriteFile("/Fotos/Viagem/rascunho.txt", "não enviado");
        var error = h.Engine.SetExcluded(Lid(h, "/Fotos"), true);
        Assert.Contains("não enviad", error);
        Assert.NotNull(h.Fs.Resolve(h.P("/Fotos/Viagem/rascunho.txt")));
        Assert.NotNull(h.Engine.SetExcluded(h.Drive.RootLid, true));
        Assert.NotNull(h.Engine.SetExcluded(Lid(h, "/Docs/a.txt"), true)); // só pastas
    }

    [Fact]
    public void Exclusao_persiste_ao_reabrir_o_banco()
    {
        using var h = Setup();
        var fotos = Lid(h, "/Fotos");
        Assert.Null(h.Engine.SetExcluded(fotos, true));
        using var again = new StateDb(h.DbPath);
        Assert.True(again.IsExcluded(fotos));
        Assert.Equal([fotos], again.ExcludedFolders(FakeGraph.DriveId).Select(i => i.Lid));
    }
}
