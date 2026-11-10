using System.Diagnostics;
using CraftDriver.Core.Graph;
using CraftDriver.Core.State;
using CraftDriver.Core.Sync;
using CraftDriver.Core.Vfs;
using CraftDriver.Fuse;
using ActivityKind = CraftDriver.Core.Sync.ActivityKind;

namespace CraftDriver.Tests;

/// <summary>Leitura por faixas (streaming) de arquivos grandes só-na-nuvem (seção 7 do PENDENCIAS.md).</summary>
public class StreamingTests
{
    const int MB = 1024 * 1024;
    const int Block = 4 * MB; // padrão de AppConfig.StreamingBlockKB

    internal static byte[] Bytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    static Item ItemAt(Harness h, string rel) => h.Fs.Resolve(h.P(rel))!.Value.Item!;

    static byte[] ReadAll(Harness h, ulong fh, long size, int chunk = 128 * 1024)
    {
        var all = new byte[size];
        long off = 0;
        var buf = new byte[chunk];
        while (true)
        {
            var n = h.Fs.Read(fh, buf, off);
            Assert.True(n >= 0, $"read em {off} retornou {n}");
            if (n == 0) break;
            buf.AsSpan(0, n).CopyTo(all.AsSpan((int)off));
            off += n;
        }
        Assert.Equal(size, off);
        return all;
    }

    [Fact]
    public void Abrir_arquivo_grande_nao_baixa_e_ler_do_meio_baixa_so_um_bloco()
    {
        using var h = new Harness();
        var data = Bytes(64 * MB, 1);
        h.Remote.RemoteCreateBytes("/", "video.mp4", data);
        h.Settle();

        Assert.Equal(0, h.Fs.Open(h.P("/video.mp4"), 0, 0, out var fh));
        try
        {
            Assert.Equal(0, h.Remote.BytesServed); // o open respondeu sem baixar nada
            Assert.True(h.Fs.IsStreaming(fh));
            Assert.Equal(64L * MB, h.Fs.GetAttr(null, fh, out var a) == 0 ? a.Size : -1);

            const long off = 30_000_000;
            Assert.True(h.Fs.ReadNeedsNetwork(fh, off, 4096));
            var buf = new byte[4096];
            Assert.Equal(4096, h.Fs.Read(fh, buf, off));
            Assert.Equal(data.AsSpan((int)off, 4096).ToArray(), buf);

            var range = Assert.Single(h.Remote.RangeRequests);
            Assert.Equal(off / Block * Block, range.offset);
            Assert.True(h.Remote.BytesServed <= Block, $"baixou {h.Remote.BytesServed} bytes para ler 4 KB");
            Assert.Equal(0, h.Remote.Downloads);

            // o mesmo bloco de novo: local, sem rede
            Assert.False(h.Fs.ReadNeedsNetwork(fh, off, 4096));
            Assert.True(h.Fs.ReadNeedsNetwork(fh, 0, 4096));
            Assert.Equal(4096, h.Fs.Read(fh, buf, off + 100));
            Assert.Equal(data.AsSpan((int)off + 100, 4096).ToArray(), buf);
            Assert.Single(h.Remote.RangeRequests);

            // leitura no fim do arquivo é cortada no tamanho
            Assert.Equal(10, h.Fs.Read(fh, buf, data.Length - 10));
            Assert.Equal(data.AsSpan(data.Length - 10).ToArray(), buf.AsSpan(0, 10).ToArray());
            Assert.Equal(0, h.Fs.Read(fh, buf, data.Length));

            Assert.Null(h.Db.Get(ItemAt(h, "/video.mp4").Lid)!.CachedCTag); // incompleto não é cache atual
        }
        finally { h.Fs.Release(fh); }
    }

    [Fact]
    public void Arquivo_pequeno_continua_baixando_inteiro_no_open()
    {
        using var h = new Harness();
        h.Remote.RemoteCreateBytes("/", "pequeno.bin", Bytes(MB, 2));
        h.Settle();
        Assert.Equal(0, h.Fs.Open(h.P("/pequeno.bin"), 0, 0, out var fh));
        Assert.False(h.Fs.IsStreaming(fh));
        Assert.Equal(1, h.Remote.Downloads);
        Assert.Empty(h.Remote.RangeRequests);
        h.Fs.Release(fh);
    }

    [Fact]
    public void Leitura_sequencial_completa_verifica_o_hash_e_promove_ao_cache()
    {
        using var h = new Harness();
        var data = Bytes(20 * MB, 3);
        h.Remote.RemoteCreateBytes("/", "seq.bin", data);
        h.Settle();
        var lid = ItemAt(h, "/seq.bin").Lid;

        Assert.Equal(0, h.Fs.Open(h.P("/seq.bin"), 0, 0, out var fh));
        Assert.True(h.Fs.IsStreaming(fh));
        Assert.Equal(data, ReadAll(h, fh, data.Length));

        var item = h.Db.Get(lid)!;
        Harness.WaitUntil(() => h.Db.Get(lid)!.CachedCTag == item.CTag, "promovido ao cache depois de completo");
        Assert.Equal(data, File.ReadAllBytes(h.Engine.Cache.PathFor(item.DriveId, lid)));
        Assert.Equal(data.Length, h.Remote.BytesServed); // cada bloco uma vez só (leitura e segundo plano não duplicam)
        Assert.Contains(h.Engine.Activity.Entries, e => e.Kind == ActivityKind.Download && e.Text.Contains("seq.bin"));
        Assert.Empty(Directory.EnumerateFiles(h.Engine.Cache.PartialDir)); // nada de esparso/bitmap sobrando

        // o handle aberto continua lendo; um open novo já é normal (cache completo)
        var buf = new byte[100];
        Assert.Equal(100, h.Fs.Read(fh, buf, 5 * MB));
        Assert.Equal(data.AsSpan(5 * MB, 100).ToArray(), buf);
        h.Fs.Release(fh);
        Assert.Equal(0, h.Fs.Open(h.P("/seq.bin"), 0, 0, out var fh2));
        Assert.False(h.Fs.IsStreaming(fh2));
        h.Fs.Release(fh2);
        Assert.Equal(data.Length, h.Remote.BytesServed);
    }

    [Fact]
    public void Hash_final_divergente_nao_vira_cache_e_descarta_os_blocos()
    {
        using var h = new Harness();
        var data = Bytes(12 * MB, 4);
        h.Remote.RemoteCreateBytes("/", "corrompido.bin", data);
        h.Settle();
        var lid = ItemAt(h, "/corrompido.bin").Lid;
        h.Remote.CorruptNextDownloads = 1; // um bloco chega errado, sem erro de rede

        Assert.Equal(0, h.Fs.Open(h.P("/corrompido.bin"), 0, 0, out var fh));
        // a mensagem "os blocos foram descartados" só pode aparecer quando eles já foram descartados
        bool? discardedWhenReported = null;
        h.Engine.Activity.Changed += () =>
        {
            // Changed vem de várias threads e outra entrada pode chegar junto: procurar em todas, não só na mais nova
            lock (h)
                if (discardedWhenReported == null && h.Engine.Activity.Entries.Any(e => e.Kind == ActivityKind.Error && e.Text.Contains("não confere")))
                    discardedWhenReported = h.Fs.ReadNeedsNetwork(fh, 0, 1);
        };
        try
        {
            ReadAll(h, fh, data.Length);
            // espera o HANDLER ter visto o aviso, não só a entrada na lista: Add enfileira e grava o log em arquivo
            // antes de disparar Changed, e conferir Entries aqui corria contra o handler (instável sob a suíte inteira)
            Harness.WaitUntil(() => { lock (h) return discardedWhenReported != null; }, "verificação final reprovada");
            lock (h) Assert.True(discardedWhenReported, "o aviso de descarte saiu antes de os blocos serem descartados");
            Assert.Null(h.Db.Get(lid)!.CachedCTag);
            Assert.False(h.Engine.Cache.Exists(FakeGraph.DriveId, lid));

            // os blocos foram descartados: ler de novo baixa outra vez, agora certo, e então promove
            Assert.True(h.Fs.ReadNeedsNetwork(fh, 0, 1));
            Assert.Equal(data, ReadAll(h, fh, data.Length));
            Harness.WaitUntil(() => h.Db.Get(lid)!.CachedCTag != null, "promovido depois da segunda leitura");
            Assert.Equal(data, File.ReadAllBytes(h.Engine.Cache.PathFor(FakeGraph.DriveId, lid)));
        }
        finally { h.Fs.Release(fh); }
    }

    [Fact]
    public void Falha_de_rede_num_bloco_devolve_erro_e_nao_zeros()
    {
        using var h = new Harness();
        var data = Bytes(16 * MB, 5);
        h.Remote.RemoteCreateBytes("/", "rede.bin", data);
        h.Settle();
        Assert.Equal(0, h.Fs.Open(h.P("/rede.bin"), 0, 0, out var fh));
        try
        {
            h.Remote.OfflineMode = true;
            var buf = new byte[4096];
            Assert.Equal(-Errno.EHOSTUNREACH, h.Fs.Read(fh, buf, 9 * MB));
            Assert.True(h.Fs.ReadNeedsNetwork(fh, 9 * MB, 4096)); // o bloco não foi marcado

            h.Remote.OfflineMode = false;
            Assert.Equal(4096, h.Fs.Read(fh, buf, 9 * MB));
            Assert.Equal(data.AsSpan(9 * MB, 4096).ToArray(), buf);
        }
        finally { h.Fs.Release(fh); }
    }

    [Fact]
    public void Abrir_para_escrita_exige_o_conteudo_inteiro_e_aproveita_os_blocos()
    {
        using var h = new Harness();
        var data = Bytes(16 * MB, 6);
        h.Remote.RemoteCreateBytes("/", "editar.bin", data);
        h.Settle();
        var lid = ItemAt(h, "/editar.bin").Lid;

        Assert.Equal(0, h.Fs.Open(h.P("/editar.bin"), 0, 0, out var reader));
        var buf = new byte[4096];
        Assert.Equal(4096, h.Fs.Read(reader, buf, 6 * MB));

        Assert.Equal(0, h.Fs.Open(h.P("/editar.bin"), OpenFlags.RdWr, 0, out var writer));
        Assert.False(h.Fs.IsStreaming(writer));
        var cur = h.Db.Get(lid)!;
        Assert.Equal(cur.CTag, cur.CachedCTag); // o open de escrita só voltou com o arquivo inteiro e verificado
        Assert.Equal(data.Length, h.Remote.BytesServed); // o bloco já lido não foi baixado de novo

        Assert.Equal(4, h.Fs.Write(writer, "NOVO"u8, 0));
        Assert.Equal(4, h.Fs.Read(reader, buf.AsSpan(0, 4), 0)); // mesmo arquivo: o leitor vê a escrita
        Assert.Equal("NOVO"u8.ToArray(), buf.AsSpan(0, 4).ToArray());
        h.Fs.Release(writer);
        h.Fs.Release(reader);
        h.Settle();
        var remote = h.Remote.FindByPath("/editar.bin")!.Content;
        Assert.Equal("NOVO"u8.ToArray(), remote.AsSpan(0, 4).ToArray());
        Assert.Equal(data.AsSpan(4).ToArray(), remote.AsSpan(4).ToArray());
    }

    [Fact]
    public void Arquivo_mudou_na_nuvem_open_seguinte_usa_a_versao_nova()
    {
        using var h = new Harness();
        var v1 = Bytes(16 * MB, 7);
        var v2 = Bytes(16 * MB, 8);
        h.Remote.RemoteCreateBytes("/", "muda.bin", v1);
        h.Settle();

        Assert.Equal(0, h.Fs.Open(h.P("/muda.bin"), 0, 0, out var old));
        var buf = new byte[4096];
        Assert.Equal(4096, h.Fs.Read(old, buf, 5 * MB));
        Assert.Equal(v1.AsSpan(5 * MB, 4096).ToArray(), buf);

        h.Remote.RemoteUpdateBytes("/muda.bin", v2);
        h.Settle();

        Assert.Equal(0, h.Fs.Open(h.P("/muda.bin"), 0, 0, out var fresh));
        Assert.Equal(4096, h.Fs.Read(fresh, buf, 5 * MB));
        Assert.Equal(v2.AsSpan(5 * MB, 4096).ToArray(), buf); // bloco velho descartado, não reaproveitado
        Assert.Single(Directory.EnumerateFiles(h.Engine.Cache.PartialDir, "*.stream"));

        // o fd antigo segue com o que já tinha; o resto da versão velha não existe mais na nuvem
        Assert.Equal(4096, h.Fs.Read(old, buf, 5 * MB));
        Assert.Equal(v1.AsSpan(5 * MB, 4096).ToArray(), buf);
        Assert.Equal(-Errno.EIO, h.Fs.Read(old, buf, 13 * MB));
        h.Fs.Release(old);
        h.Fs.Release(fresh);
    }

    [Fact]
    public void Blocos_baixados_sobrevivem_a_fechar_e_a_reiniciar()
    {
        using var h = new Harness();
        var data = Bytes(16 * MB, 9);
        h.Remote.RemoteCreateBytes("/", "retoma.bin", data);
        h.Settle();
        var buf = new byte[4096];
        Assert.Equal(0, h.Fs.Open(h.P("/retoma.bin"), 0, 0, out var fh));
        Assert.Equal(4096, h.Fs.Read(fh, buf, 9 * MB));
        h.Fs.Release(fh);

        // "reinício": motor e VFS novos sobre o mesmo banco e cache
        h.Engine.Stop();
        using var engine = new SyncEngine(h.Config, h.Db, new ContentCache(h.Config.CacheDir), new ActivityLog(), _ => h.Remote);
        var fs = new VirtualFs(engine);
        engine.Start();
        Harness.WaitUntil(() => engine.GetDrive(FakeGraph.DriveId)?.Ready == true, "drive pronta");
        Assert.Equal(0, fs.Open(h.P("/retoma.bin"), 0, 0, out fh));
        Assert.True(fs.IsStreaming(fh));
        Assert.False(fs.ReadNeedsNetwork(fh, 9 * MB, 4096)); // bitmap persistido
        Assert.Equal(4096, fs.Read(fh, buf, 9 * MB + 1));
        Assert.Equal(data.AsSpan(9 * MB + 1, 4096).ToArray(), buf);
        Assert.Single(h.Remote.RangeRequests);
        fs.Release(fh);
        engine.Stop();
    }

    [Fact]
    public void Limite_do_cache_conta_so_os_blocos_baixados_e_nao_despeja_arquivo_aberto()
    {
        using var h = new Harness();
        var data = Bytes(32 * MB, 13);
        h.Remote.RemoteCreateBytes("/", "lru.bin", data);
        h.Settle();
        var lid = ItemAt(h, "/lru.bin").Lid;
        Assert.Equal(0, h.Fs.Open(h.P("/lru.bin"), 0, 0, out var fh));
        try
        {
            Assert.Equal(4096, h.Fs.Read(fh, new byte[4096], 20 * MB));
            // o arquivo esparso tem 32 MB aparentes, mas só um bloco ocupa o cache
            var total = h.Engine.Cache.TotalBytes();
            Assert.True(total <= Block + 64 * 1024, $"cache conta {total} bytes");

            ReadAll(h, fh, data.Length);
            Harness.WaitUntil(() => h.Db.Get(lid)!.CachedCTag != null, "promovido");
            h.Config.CacheLimitMB = 1;
            h.Engine.EnforceCacheLimit();
            Assert.NotNull(h.Db.Get(lid)!.CachedCTag); // aberto: não é despejado
            Assert.True(h.Engine.Cache.Exists(FakeGraph.DriveId, lid));
        }
        finally { h.Fs.Release(fh); }
        h.Engine.EnforceCacheLimit();
        Assert.Null(h.Db.Get(lid)!.CachedCTag); // fechado: o LRU volta a valer
    }

    [Fact]
    public async Task Ctrl_c_durante_o_read_de_um_bloco_cancela_o_download()
    {
        using var h = new Harness();
        h.Remote.RemoteCreateBytes("/", "lento.bin", Bytes(16 * MB, 10));
        h.Settle();
        var entered = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        h.Remote.BeforeDownload = async ct =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
        };
        var interrupt = false;
        h.Fs.InterruptRequested = () => Volatile.Read(ref interrupt);

        Assert.Equal(0, h.Fs.Open(h.P("/lento.bin"), 0, 0, out var fh));
        var read = Task.Run(() => h.Fs.Read(fh, new byte[4096], 8 * MB));
        Assert.True(await entered.Task.WithinAsync(5000), "o bloco não começou a baixar");
        Volatile.Write(ref interrupt, true);
        Assert.True(await read.WithinAsync(5000), "read não voltou depois do Ctrl-C");
        Assert.Equal(-Errno.EINTR, await read);
        Assert.True(await cancelled.Task.WithinAsync(5000), "o download do bloco continuou depois do Ctrl-C");
        h.Fs.Release(fh);
    }

    [Fact]
    public void Leitura_sequencial_baixa_em_segundo_plano_com_progresso_e_cancelamento_pela_interface()
    {
        using var h = new Harness();
        var data = Bytes(40 * MB, 11);
        h.Remote.RemoteCreateBytes("/", "filme.mkv", data);
        h.Settle();
        var calls = 0;
        h.Remote.BeforeDownload = async ct =>
        {
            if (Interlocked.Increment(ref calls) > 3) await Task.Delay(Timeout.Infinite, ct); // 4º bloco fica preso
        };

        Assert.Equal(0, h.Fs.Open(h.P("/filme.mkv"), 0, 0, out var fh));
        try
        {
            var buf = new byte[128 * 1024];
            for (long off = 0; off < 2 * MB; off += buf.Length) Assert.Equal(buf.Length, h.Fs.Read(fh, buf, off));

            // leitura sequencial dispara o download do resto em segundo plano, visível como transferência
            Harness.WaitUntil(() => Volatile.Read(ref calls) >= 4, "download em segundo plano começou");
            var t = Assert.Single(h.Engine.Activity.Transfers);
            Assert.False(t.Upload);
            Assert.Equal(data.Length, t.Total);
            Assert.True(t.Done >= 3L * Block, $"progresso {t.Done}");

            Assert.True(h.Engine.Activity.CancelTransfer(t.Id));
            Harness.WaitUntil(() => h.Engine.Activity.Transfers.Count == 0, "transferência encerrada");
            Assert.Contains(h.Engine.Activity.Entries, e => e.Text.Contains("cancelado") && e.Text.Contains("filme.mkv"));
            Thread.Sleep(300);
            Assert.Equal(4, Volatile.Read(ref calls)); // parou de baixar

            // o handle continua utilizável
            h.Remote.BeforeDownload = null;
            Assert.Equal(buf.Length, h.Fs.Read(fh, buf, 30 * MB));
            Assert.Equal(data.AsSpan(30 * MB, buf.Length).ToArray(), buf);
        }
        finally { h.Fs.Release(fh); }
    }
}

/// <summary>Leitura por faixas com o FUSE montado de verdade.</summary>
[Collection("fuse")]
public class StreamingFuseTests
{
    [Fact]
    public async Task Montagem_real_le_do_meio_sem_esperar_o_arquivo_inteiro()
    {
        RealFuse.Require();
        using var h = new Harness();
        var data = StreamingTests.Bytes(64 * 1024 * 1024, 12);
        h.Remote.RemoteCreateBytes("/", "grande.bin", data);
        h.Remote.RemoteCreate("/", "outro.txt", "x");
        h.Settle();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        h.Remote.BeforeDownload = async ct =>
        {
            Interlocked.Increment(ref entered);
            await release.Task.WaitAsync(ct);
        };

        var mnt = Path.Combine(h.Dir, "mnt");
        var loop = FuseHost.MountAsync(h.Fs, mnt, maxThreads: 1);
        Process? py = null;
        try
        {
            var root = Path.Combine(mnt, Harness.Name);
            Harness.WaitUntil(() => Directory.Exists(root), "montagem visível");
            const long off = 40_000_000;
            var script = $$"""
                import os, sys, hashlib
                fd = os.open({{"\"" + Path.Combine(root, "grande.bin") + "\""}}, os.O_RDONLY)
                print("aberto", flush=True)
                a = os.pread(fd, 4096, {{off}})
                b = os.pread(fd, 4096, {{off}})
                print(hashlib.sha256(a).hexdigest(), a == b, len(a), flush=True)
                """;
            var readsBefore = h.Fs.ReadCount;
            py = Process.Start(new ProcessStartInfo("python3", ["-c", script]) { RedirectStandardOutput = true, RedirectStandardError = true })!;

            // o open voltou com a rede presa: não esperou download nenhum
            var first = py.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask();
            Assert.True(await first.WithinAsync(5000), "open não voltou enquanto a rede estava presa");
            Assert.Equal("aberto", await first);
            Harness.WaitUntil(() => Volatile.Read(ref entered) == 1, "o read pediu o bloco");

            // com uma thread só no loop, o read esperando a rede não pode ocupá-la
            var sw = Stopwatch.StartNew();
            var stat = Task.Run(() => File.Exists(Path.Combine(root, "outro.txt")));
            Assert.True(await stat.WithinAsync(3000), "stat ficou preso atrás do read que espera a rede");
            Assert.True(await stat);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"stat levou {sw.ElapsedMilliseconds} ms");

            release.TrySetResult();
            Assert.True(py.WaitForExit(10_000), "python não terminou");
            var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data.AsSpan((int)off, 4096))).ToLowerInvariant();
            Assert.Equal($"{expected} True 4096", py.StandardOutput.ReadToEnd().Trim());
            Assert.True(h.Remote.BytesServed <= 4 * 1024 * 1024, $"baixou {h.Remote.BytesServed} bytes");
            Assert.Equal(0, h.Remote.Downloads);
            // direct_io (libfuse ≥ 3.17.3): o segundo pread do mesmo trecho também chega ao VFS; pelo cache de página
            // (libfuse antiga) ele pode vir do kernel
            Assert.Equal(FuseHost.LibfuseCanAllowDirectIoMmap, FuseHost.StreamingDirectIo);
            var reads = h.Fs.ReadCount - readsBefore;
            Assert.True(FuseHost.StreamingDirectIo ? reads == 2 : reads is >= 1 and <= 2, $"reads no VFS: {reads}");

            // arquivo inteiro pelo shell confere, e depois disso vira cache atual
            var sha = Process.Start(new ProcessStartInfo("sha256sum", [Path.Combine(root, "grande.bin")]) { RedirectStandardOutput = true })!;
            Assert.True(sha.WaitForExit(30_000));
            Assert.StartsWith(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant(), sha.StandardOutput.ReadToEnd());
            var lid = h.Fs.Resolve(h.P("/grande.bin"))!.Value.Item!.Lid;
            Harness.WaitUntil(() => h.Db.Get(lid)!.CachedCTag != null, "promovido ao cache");
            Assert.Equal(data.Length, h.Remote.BytesServed);
        }
        finally
        {
            release.TrySetResult();
            try { py?.Kill(); } catch { }
            py?.Dispose();
            h.Engine.Stop();
            FuseHost.Unmount();
            Assert.True(await loop.WithinAsync(TimeSpan.FromSeconds(15)), "loop do FUSE não terminou após desmontar: " + FuseHost.Diagnostico());
        }
    }
}
