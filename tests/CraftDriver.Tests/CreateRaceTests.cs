using System.Text;
using CraftDriver.Core.Vfs;

namespace CraftDriver.Tests;

/// <summary>
/// Seção 12.7 do PENDENCIAS.md: <c>Create</c> de item que o delta acabou de inserir (entre o lookup e o create do kernel).
/// </summary>
[Collection("fuse")] // usa o motor em segundo plano com tempos apertados: não intercalar com os testes FUSE
public class CreateRaceTests
{
    static readonly TimeSpan Long = TimeSpan.FromSeconds(20);

    static TaskCompletionSource Tcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---------- 12.7 Create chama Open segurando Db.Lock ----------

    /// <summary>
    /// O delta inseriu o item entre o lookup (ENOENT) e o create do kernel: o Create cai no Open com Trunc forçado —
    /// "echo x >> arquivo" (O_CREAT|O_APPEND, sem O_TRUNC) zera o conteúdo da nuvem.
    /// </summary>
    [Fact]
    public void S12_7a_create_sem_trunc_de_item_que_o_delta_acabou_de_inserir_preserva_o_conteudo()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "diario.txt", "linhas antigas da nuvem\n");
        h.Settle();
        Assert.Equal(0, h.Fs.Create(h.P("diario.txt"), OpenFlags.RdWr | OpenFlags.Creat | OpenFlags.Append, out var fh));
        var buf = new byte[256];
        var n = h.Fs.Read(fh, buf, 0);
        Assert.Equal(0, h.Fs.Release(fh));
        Assert.Equal("linhas antigas da nuvem\n", Encoding.UTF8.GetString(buf, 0, Math.Max(0, n)));
    }

    /// <summary>
    /// open(O_RDONLY|O_CREAT) do mesmo cenário: o Open baixa o arquivo DENTRO do lock do Create — getattr/readdir de
    /// toda a montagem esperam a rede. Pior: a continuação do download pega <c>lock (Db.Lock)</c> em outra thread
    /// (SyncEngine.EnsureContentAsync, ao mover o parcial) enquanto a thread do Create espera o download sem soltar o
    /// lock → deadlock permanente. Por isso o teste termina o download com erro de rede (não deixa concluir).
    /// </summary>
    [Fact]
    public async Task S12_7b_create_somente_leitura_nao_baixa_segurando_o_lock_do_banco()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "x.txt", "conteúdo da nuvem");
        h.Settle();
        var entered = Tcs();
        var release = Tcs();
        h.Remote.BeforeDownload = async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            throw new HttpRequestException("rede caiu (simulado: concluir o download aqui travaria o teste)");
        };
        var create = Task.Run(() =>
        {
            var r = h.Fs.Create(h.P("x.txt"), OpenFlags.Creat, out var fh); // O_RDONLY|O_CREAT
            return (r, fh);
        }, TestSupport.Ct);
        try
        {
            Assert.True(await entered.Task.WithinAsync(Long), "o create não baixou o arquivo");
            var free = await Task.Run(() =>
            {
                if (!Monitor.TryEnter(h.Db.Lock, 1000)) return false;
                Monitor.Exit(h.Db.Lock);
                return true;
            }, TestSupport.Ct);
            Assert.True(free, "Create baixa o arquivo com Db.Lock preso: a montagem inteira espera a rede");
        }
        finally
        {
            release.TrySetResult();
            if (await create.WithinAsync(Long))
            {
                h.Remote.BeforeDownload = null;
                var (r, fh) = await create;
                if (r == 0) h.Fs.Release(fh);
            }
        }
    }
}
