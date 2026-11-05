using CraftDriver.Core.State;
using System.Collections.Concurrent;
using Xunit;

namespace CraftDriver.Tests;

/// <summary>
/// PENDENCIAS seção 11: corridas entre o limite do cache (LRU) e o <c>open</c>. A reserva do Open
/// (<c>VirtualFs.Reserve</c>) e a conferência de <c>IsOpen</c> dentro do <c>Db.Lock</c> no despejo são as proteções.
/// </summary>
public class LruOpenRaceTests
{
    static Item Get(Harness h, string rel) => h.Fs.Resolve(h.P(rel))!.Value.Item!;

    /// <summary>
    /// Intercalação forçada: o LRU confere <c>IsOpen</c> (falso) ANTES da reserva do Open e só apaga o arquivo do cache
    /// DEPOIS de o Open decidir se o cache vale. Com a decisão tomada sobre a cópia do item lida antes da reserva, o
    /// Open via "cache em dia", o arquivo sumia e o resultado era EIO num arquivo limpo que está na nuvem.
    /// </summary>
    [Fact]
    public async Task Despejo_que_conferiu_antes_da_reserva_nao_faz_o_open_dar_EIO()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "alvo.bin", new string('g', 2 * 1024 * 1024));
        h.Settle();
        Assert.Equal(0, h.Fs.Open(h.P("/alvo.bin"), 0, 0, out var first)); // baixa: fica em dia no cache
        h.Fs.Release(first);
        var lid = Get(h, "/alvo.bin").Lid;
        Assert.NotNull(h.Db.Get(lid)!.CachedCTag);
        var downloads = h.Remote.Downloads;
        h.Config.CacheLimitMB = 1; // o próprio arquivo já passa do limite: é o despejado

        using var openAtReserve = new ManualResetEventSlim();
        using var lruChecked = new ManualResetEventSlim();
        using var openChecked = new ManualResetEventSlim();
        var lruSawClosed = false;
        h.Fs.OpenStepForTests = (step, l) =>
        {
            if (l != lid) return;
            if (step == "reservando")
            {
                openAtReserve.Set();
                Assert.True(lruChecked.Wait(10_000), "o LRU não conferiu IsOpen");
            }
            else if (step == "conferido") openChecked.Set();
        };
        var isOpen = h.Engine.IsOpen;
        h.Engine.IsOpen = l =>
        {
            var r = isOpen(l);
            if (l == lid && !lruChecked.IsSet)
            {
                lruSawClosed = !r;
                lruChecked.Set();
                // segura o despejo (dentro do Db.Lock) até o Open decidir; com a correção o Open espera este lock
                // para reler o item, então aqui só se espera o prazo
                openChecked.Wait(1500);
            }
            return r;
        };

        var ct = TestContext.Current.CancellationToken;
        var open = Task.Run(() =>
        {
            var r = h.Fs.Open(h.P("/alvo.bin"), 0, 0, out var fh);
            return (r, fh);
        }, ct);
        Assert.True(openAtReserve.Wait(10_000, ct), "o Open não chegou à reserva");
        var lru = Task.Run(h.Engine.EnforceCacheLimit, ct);
        Assert.True(await lru.WithinAsync(TimeSpan.FromSeconds(15)), "o LRU não terminou");
        Assert.True(await open.WithinAsync(TimeSpan.FromSeconds(15)), "o Open não terminou");
        h.Fs.OpenStepForTests = null;
        h.Engine.IsOpen = isOpen;

        Assert.True(lruSawClosed, "a intercalação não aconteceu: o LRU já via o arquivo aberto");
        var (rc, handle) = await open;
        Assert.Equal(0, rc);
        try
        {
            var cur = h.Db.Get(lid)!;
            Assert.True(cur.CachedCTag != null && h.Engine.Cache.Exists(cur.DriveId, lid), "despejado com o handle aberto");
            var buf = new byte[16];
            Assert.Equal(16, h.Fs.Read(handle, buf, 2 * 1024 * 1024 - 16));
            Assert.All(buf, b => Assert.Equal((byte)'g', b));
            Assert.Equal(downloads + 1, h.Remote.Downloads); // foi despejado e o Open baixou de novo
        }
        finally { h.Fs.Release(handle); }
        Assert.DoesNotContain(h.Engine.Activity.Entries, e => e.Text.Contains("não está no cache"));
    }

    /// <summary>
    /// <c>truncate -s</c> por caminho (sem fd aberto) num arquivo em cache: o LRU rodando entre a conferência do cache e
    /// o SetLength apagava o arquivo, e o truncate falhava (ou cortava um arquivo que já não estava lá).
    /// </summary>
    [Fact]
    public void Truncate_por_caminho_nao_perde_o_arquivo_para_o_lru()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "t.bin", new string('t', 2 * 1024 * 1024));
        h.Settle();
        Assert.Equal(0, h.Fs.Open(h.P("/t.bin"), 0, 0, out var first)); // baixa: fica em dia no cache
        h.Fs.Release(first);
        var lid = Get(h, "/t.bin").Lid;
        h.Config.CacheLimitMB = 1; // o próprio arquivo passa do limite
        var lruRan = false;
        h.Fs.OpenStepForTests = (step, l) =>
        {
            if (step != "truncate-conferido" || l != lid) return;
            h.Engine.EnforceCacheLimit(); // despejo exatamente entre a conferência e o SetLength
            lruRan = true;
        };
        var rc = h.Fs.Truncate(h.P("/t.bin"), 1000, null);
        h.Fs.OpenStepForTests = null;
        Assert.True(lruRan, "o LRU não rodou no meio do truncate");
        Assert.Equal(0, rc);
        var cur = h.Db.Get(lid)!;
        Assert.True(h.Engine.Cache.Exists(cur.DriveId, lid), "o arquivo sumiu do cache no meio do truncate");
        Assert.Equal(1000, new FileInfo(h.Engine.Cache.PathFor(cur.DriveId, lid)).Length);
        h.Settle();
        Assert.Equal(1000, h.Remote.ReadText("/t.bin")!.Length); // a nuvem recebe os 1000 bytes
    }

    /// <summary>
    /// Estresse: LRU girando sem parar contra opens/reads/releases em paralelo, cache menor que os arquivos. Enquanto um
    /// handle está aberto o item nunca pode estar despejado, nenhum open pode falhar e todo read devolve o conteúdo
    /// certo. Pega a remoção da reserva (o LRU despeja arquivo aberto) e a conferência de IsOpen fora do lock.
    /// </summary>
    [Fact]
    public void Estresse_lru_contra_open_nunca_despeja_arquivo_aberto_nem_da_erro()
    {
        using var h = new Harness();
        const int files = 4, size = 400 * 1024;
        for (var i = 0; i < files; i++) h.Remote.RemoteCreate("/", $"f{i}.bin", new string((char)('a' + i), size));
        h.Settle();
        var lids = Enumerable.Range(0, files).Select(i => Get(h, $"/f{i}.bin").Lid).ToArray();
        h.Config.CacheLimitMB = 1; // 1,6 MB de arquivos: cada passada despeja quem não estiver aberto

        var stop = DateTime.UtcNow.AddSeconds(3);
        long opens = 0, passes = 0;
        var errors = new ConcurrentQueue<string>();
        var lru = new Thread(() =>
        {
            while (DateTime.UtcNow < stop)
            {
                h.Engine.EnforceCacheLimit();
                Interlocked.Increment(ref passes);
            }
        });
        var workers = Enumerable.Range(0, 3).Select(w => new Thread(() =>
        {
            var buf = new byte[16];
            var i = w;
            while (DateTime.UtcNow < stop && errors.IsEmpty)
            {
                i = (i + 1) % files;
                var r = h.Fs.Open(h.P($"/f{i}.bin"), 0, 0, out var fh);
                Interlocked.Increment(ref opens);
                if (r != 0) { errors.Enqueue($"open f{i}: {r}"); continue; }
                try
                {
                    for (var k = 0; k < 3; k++)
                    {
                        var cur = h.Db.Get(lids[i]);
                        if (cur?.CachedCTag == null || !h.Engine.Cache.Exists(cur.DriveId, cur.Lid))
                            errors.Enqueue($"f{i} despejado com o handle aberto");
                        var n = h.Fs.Read(fh, buf, size - 16);
                        if (n != 16 || buf.Any(b => b != (byte)('a' + i))) errors.Enqueue($"read f{i}: {n}");
                    }
                }
                finally { h.Fs.Release(fh); }
            }
        })).ToList();
        lru.Start();
        workers.ForEach(t => t.Start());
        workers.ForEach(t => t.Join());
        lru.Join();

        var summary = $"opens={opens} passadas={passes} downloads={h.Remote.Downloads} erros: {string.Join(" | ", errors.Take(5))}";
        Assert.True(errors.IsEmpty, summary);
        Assert.True(opens > 200 && h.Remote.Downloads > 50, "o estresse não despejou/baixou o bastante: " + summary);
    }
}
