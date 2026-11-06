using System.Text;
using CraftDriver.Core.Vfs;
using CraftDriver.Fuse;

namespace CraftDriver.Tests;

[Collection("fuse")]
public class VfsRaceTests
{
    [Theory]
    [InlineData("create")]
    [InlineData("mkdir")]
    [InlineData("rename")]
    public void Pai_apagado_entre_resolve_e_mutacao_nao_cria_item_orfao(string operacao)
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        Assert.Equal(0, h.Fs.Mkdir(h.P("P")));
        Assert.Equal(0, h.Fs.Create(h.P("origem"), OpenFlags.WrOnly, out var original));
        h.Fs.Release(original);
        var pai = h.Fs.Resolve(h.P("P"))!.Value.Item!.Lid;
        h.Fs.MutationStepForTests = etapa =>
        {
            if (etapa != operacao) return;
            h.Db.Delete(pai); // delta ou rmdir concorrente depois de Resolve, antes de entrar no monitor
        };
        var resultado = operacao switch
        {
            "create" => h.Fs.Create(h.P("P/novo"), OpenFlags.WrOnly, out _),
            "mkdir" => h.Fs.Mkdir(h.P("P/nova")),
            _ => h.Fs.Rename(h.P("origem"), h.P("P/destino"), 0),
        };
        Assert.Equal(-Errno.ENOENT, resultado);
        Assert.Empty(h.Db.GetChildren(pai));
        if (operacao == "rename") Assert.NotNull(h.Fs.Resolve(h.P("origem")));
    }

    [Fact]
    public async Task Fila_local_nao_espera_256_trabalhos_de_rede_bloqueados()
    {
        using var liberar = new ManualResetEventSlim();
        using var entraram = new CountdownEvent(SlowOps.MaxThreads);
        using var terminaram = new CountdownEvent(SlowOps.MaxThreads);
        for (var i = 0; i < SlowOps.MaxThreads; i++)
            SlowOps.Run(() =>
            {
                entraram.Signal();
                liberar.Wait();
                terminaram.Signal();
            }, network: true);
        try
        {
            Assert.True(entraram.Wait(TimeSpan.FromSeconds(20), TestSupport.Ct), "os 256 trabalhos precisam estar dentro da espera de rede");
            var local = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SlowOps.Run(() => local.SetResult(), network: false);
            Assert.True(await local.Task.WithinAsync(TimeSpan.FromSeconds(2)), "fsync/rename/open local ficou atrás de 256 operações de rede");
        }
        finally
        {
            liberar.Set();
            Assert.True(terminaram.Wait(TimeSpan.FromSeconds(20), TestSupport.Ct));
        }
    }

    [Fact]
    public async Task FUSE_real_fsync_rename_e_open_local_respondem_com_257_reads_presos()
    {
        // Com libfuse < 3.17.3 (cache de página, sem direct_io) os reads chegam como readahead em segundo plano, que o
        // kernel limita a max_background (12): 256 reads presos na rede não acontecem — e a inversão também não.
        RealFuse.RequireDirectIoStreaming();
        using var h = new Harness(c => { c.StreamingThresholdMB = 1; c.StreamingBlockKB = 1024; });
        var conteudo = Encoding.UTF8.GetBytes(new string('x', 2 * 1024 * 1024));
        for (var i = 0; i < SlowOps.MaxThreads + 1; i++) h.Remote.RemoteCreate("/", $"rede{i}", null).Content = conteudo;
        h.Settle();
        h.Engine.Paused = true;
        Assert.Equal(0, h.Fs.Create(h.P("cache"), OpenFlags.RdWr, out var fh));
        Assert.Equal(3, h.Fs.Write(fh, "abc"u8, 0));
        h.Fs.Release(fh);
        var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.BeforeDownload = async ct =>
        {
            await liberar.Task.WaitAsync(ct);
            throw new HttpRequestException("fim da espera de rede simulada");
        };
        var mnt = Path.Combine(h.Dir, "mnt");
        var loop = FuseHost.MountAsync(h.Fs, mnt, maxThreads: 4);
        System.Diagnostics.Process? leitor = null;
        try
        {
            var root = Path.Combine(mnt, Harness.Name);
            Harness.WaitUntil(() => Directory.Exists(root), "montagem visível");
            var codigo = """
                import os, concurrent.futures
                fds = [os.open('rede'+str(i), os.O_RDONLY) for i in range(257)]
                def ler(fd):
                    try: os.pread(fd, 1, 0)
                    except OSError: pass
                    finally: os.close(fd)
                with concurrent.futures.ThreadPoolExecutor(max_workers=257) as p:
                    list(p.map(ler, fds))
                """;
            leitor = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/python3", ["-c", codigo])
            { WorkingDirectory = root, RedirectStandardError = true, RedirectStandardOutput = true })!;
            Harness.WaitUntil(() => h.Fs.ReadCount >= SlowOps.MaxThreads, "256 reads entraram no VFS e estão esperando rede", 30000);
            var local = Task.Run(() =>
            {
                using (var arquivo = new FileStream(Path.Combine(root, "cache"), FileMode.Open, FileAccess.ReadWrite)) arquivo.Flush(true);
                File.Move(Path.Combine(root, "cache"), Path.Combine(root, "renomeado"));
                return File.ReadAllText(Path.Combine(root, "renomeado"));
            }, TestSupport.Ct);
            try
            {
                Assert.True(await local.WithinAsync(TimeSpan.FromSeconds(3)), "operações locais presas atrás da fila de rede");
                Assert.Equal("abc", await local);
            }
            finally
            {
                liberar.TrySetResult();
                await local.WithinAsync(TimeSpan.FromSeconds(20));
            }
        }
        finally
        {
            liberar.TrySetResult();
            if (leitor != null)
            {
                if (!leitor.WaitForExit(30000)) leitor.Kill(true);
                leitor.Dispose();
            }
            h.Engine.Stop();
            FuseHost.Unmount();
            Assert.True(await loop.WithinAsync(TimeSpan.FromSeconds(15)));
        }
    }

    [Fact]
    public void Read_de_sessao_com_blocos_descartados_nao_pode_baixar_na_thread_do_loop()
    {
        // O predicado de despacho deve encaminhar TODA leitura streaming, inclusive cache hit: a bitmap pode
        // mudar entre HasRange e TryRead (verificação de hash descarta os blocos). Arquivos totalmente locais seguem inline.
        using var h = new Harness(c => { c.StreamingThresholdMB = 1; c.StreamingBlockKB = 1024; });
        h.Remote.RemoteCreate("/", "grande", new string('x', 2 * 1024 * 1024));
        h.Settle();
        Assert.Equal(0, h.Fs.Open(h.P("grande"), 0, 0, out var fh));
        try
        {
            Assert.True(h.Fs.IsStreaming(fh));
            var buf = new byte[1];
            Assert.Equal(1, h.Fs.Read(fh, buf, 0));
            Assert.False(h.Fs.ReadNeedsNetwork(fh, 0, 1)); // hit na bitmap neste instante
            Assert.True(h.Fs.ReadOutsideLoop(fh)); // mesmo assim fica fora do loop
        }
        finally { h.Fs.Release(fh); }
        Assert.Equal(0, h.Fs.Create(h.P("local"), OpenFlags.WrOnly, out var local));
        try { Assert.False(h.Fs.ReadOutsideLoop(local)); }
        finally { h.Fs.Release(local); }
    }
}
