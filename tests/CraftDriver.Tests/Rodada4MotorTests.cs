using System.Net;
using CraftDriver.Core.Graph;

namespace CraftDriver.Tests;

/// <summary>
/// §29: pasta excluída e recriada com o mesmo nome na mesma rodada do delta. A antiga sai do namespace (limbo) até o fim
/// da rodada — o que for movido para fora dela sobrevive — e, se a rodada não terminar, não pode ficar visível nem para
/// sempre no banco.
/// </summary>
public class Rodada4LimboTests
{
    static string Montar(Harness h)
    {
        h.Remote.RemoteCreate("/", "Docs", null, folder: true);
        h.Remote.RemoteCreate("/Docs", "Sub", null, folder: true);
        h.Remote.RemoteCreate("/Docs/Sub", "f.txt", "conteúdo");
        h.Remote.RemoteCreate("/Docs", "antigo.txt", "a");
        h.Settle();
        var antiga = h.Fs.Resolve(h.P("/Docs"))!.Value.Item!.ItemId!;
        h.Remote.RemoteCreate("/", "Temp", null, folder: true);
        h.Remote.RemoteMove("/Docs/Sub", "/Temp");
        h.Remote.RemoteDelete("/Docs");
        h.Remote.RemoteRename("/Temp", "Docs");
        return antiga;
    }

    static void AssertFinal(Harness h, string antiga)
    {
        Assert.Null(h.Fs.Resolve(h.P("/Docs/antigo.txt")));
        Assert.Equal("conteúdo", h.ReadFile("/Docs/Sub/f.txt"));
        Assert.Null(h.Db.GetByRemote(FakeGraph.DriveId, antiga)); // a pasta antiga não ficou no banco (limbo vazio)
        Assert.Single(h.Db.GetChildren(h.Drive.RootLid), i => i.Name == "Docs");
        Assert.Equal(0, h.Db.PendingOps());
    }

    /// <summary>1ª chamada: exclusão + pasta nova (ocupa o nome); a 2ª falha antes do fim da rodada.</summary>
    static void PrimeiraParteEFalha(Harness h, Exception falha)
    {
        var antes = h.Db.GetDrive(FakeGraph.DriveId)!.DeltaLink!;
        var chamada = 0;
        h.Remote.AfterDelta = (page, _) => ++chamada == 1
            ? Task.FromResult(new DeltaPage(page.Items.Where(r => r.Name != "Sub").OrderBy(r => r.IsDeleted ? 0 : 1).ToList(), antes, null))
            : chamada == 2 ? Task.FromException<DeltaPage>(falha)
            : Task.FromResult(page with { Items = page.Items.OrderBy(r => r.IsDeleted ? 0 : r.Name == "Sub" ? 2 : 1).ToList() });
    }

    [Fact]
    public async Task Rodada_interrompida_com_pasta_no_limbo_nao_a_mostra_e_a_repeticao_resolve()
    {
        using var h = new Harness();
        var antiga = Montar(h);
        PrimeiraParteEFalha(h, new IOException("rede caiu (simulado)"));
        await Assert.ThrowsAsync<IOException>(() => h.Drive.RunDeltaAsync(CancellationToken.None));

        // entre a queda e a repetição: só a pasta nova é visível, sem op local gerada
        Assert.Single(h.Db.GetChildren(h.Drive.RootLid), i => i.Name == "Docs");
        Assert.Null(h.Fs.Resolve(h.P("/Docs/antigo.txt")));
        Assert.Null(h.Fs.Resolve(h.P("/Docs/Sub")));
        Assert.NotNull(h.Db.GetByRemote(FakeGraph.DriveId, antiga)); // ainda no limbo, esperando a rodada repetida
        Assert.Equal(0, h.Db.PendingOps());

        h.Settle(); // deltaLink não avançou: a rodada se repete
        h.Remote.AfterDelta = null;
        AssertFinal(h, antiga);
    }

    [Fact]
    public async Task Token_expirado_depois_de_por_pasta_no_limbo_nao_a_deixa_para_sempre()
    {
        using var h = new Harness();
        var antiga = Montar(h);
        // a enumeração completa que segue o 410 limpa as exclusões adiadas; o que estava no limbo some no fim da rodada
        PrimeiraParteEFalha(h, new GraphException(HttpStatusCode.Gone, "resyncRequired", "token expirado"));
        await h.Drive.RunDeltaAsync(CancellationToken.None);
        h.Remote.AfterDelta = null;
        h.Settle();
        AssertFinal(h, antiga);
    }
}
