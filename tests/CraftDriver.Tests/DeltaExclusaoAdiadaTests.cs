using CraftDriver.Core.Graph;

namespace CraftDriver.Tests;

/// <summary>
/// Pasta movida para fora de uma pasta que depois foi excluída (em outro dispositivo). O delta do OneDrive traz só o
/// item movido (sem os filhos) e não garante a ordem: se a exclusão do pai antigo for aplicada antes do movimento, a
/// subárvore some do banco e o item movido volta como pasta nova e vazia — os filhos nunca mais chegam pelo delta.
/// (Caso real: \"Advanced Installer/Projects\" sumiu da conta pessoal.)
/// </summary>
public class DeltaExclusaoAdiadaTests
{
    static void Montar(Harness h)
    {
        h.Remote.RemoteCreate("/", "Ferramentas", null, folder: true);
        h.Remote.RemoteCreate("/Ferramentas", "Advanced Installer", null, folder: true);
        h.Remote.RemoteCreate("/Ferramentas/Advanced Installer", "Projects", null, folder: true);
        h.Remote.RemoteCreate("/Ferramentas/Advanced Installer/Projects", "setup.aip", "projeto");
        h.Remote.RemoteCreate("/Ferramentas", "velho.txt", "fica para trás");
        h.Settle();
        Assert.Equal("projeto", h.ReadFile("/Ferramentas/Advanced Installer/Projects/setup.aip"));
    }

    static void MoverParaRaizEExcluirOrigem(Harness h)
    {
        h.Remote.RemoteMove("/Ferramentas/Advanced Installer", "/");
        h.Remote.RemoteDelete("/Ferramentas");
    }

    static void AssertSubarvoreMovida(Harness h)
    {
        Assert.Null(h.Fs.Resolve(h.P("/Ferramentas")));
        Assert.NotNull(h.Fs.Resolve(h.P("/Advanced Installer/Projects")));
        Assert.Equal("projeto", h.ReadFile("/Advanced Installer/Projects/setup.aip"));
    }

    [Fact]
    public void Exclusao_do_pai_antigo_antes_do_movimento_na_mesma_pagina_nao_perde_a_subarvore()
    {
        using var h = new Harness();
        Montar(h);
        MoverParaRaizEExcluirOrigem(h);
        // exclusões primeiro, como o OneDrive pode entregar
        h.Remote.AfterDelta = (page, _) => Task.FromResult(page with { Items = page.Items.OrderBy(r => r.IsDeleted ? 0 : 1).ToList() });
        h.Settle();
        h.Remote.AfterDelta = null;
        AssertSubarvoreMovida(h);
    }

    [Fact]
    public void Exclusao_do_pai_antigo_numa_pagina_e_movimento_na_seguinte_nao_perde_a_subarvore()
    {
        using var h = new Harness();
        Montar(h);
        var antes = h.Db.GetDrive(FakeGraph.DriveId)!.DeltaLink!;
        MoverParaRaizEExcluirOrigem(h);
        var chamada = 0;
        h.Remote.AfterDelta = (page, _) => Task.FromResult(++chamada == 1
            ? new DeltaPage(page.Items.Where(r => r.IsDeleted).ToList(), antes, null) // mesma consulta, 2ª página
            : page with { Items = page.Items.Where(r => !r.IsDeleted).ToList() });
        h.Settle();
        h.Remote.AfterDelta = null;
        Assert.True(chamada >= 2, "o delta não passou pelas duas páginas");
        AssertSubarvoreMovida(h);
    }

    [Fact]
    public void Pasta_excluida_e_recriada_com_o_mesmo_nome_na_mesma_rodada_nunca_fica_duplicada()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "Docs", null, folder: true);
        h.Remote.RemoteCreate("/Docs", "antigo.txt", "a");
        h.Settle();
        var antes = h.Db.GetDrive(FakeGraph.DriveId)!.DeltaLink!;
        h.Remote.RemoteDelete("/Docs");
        h.Remote.RemoteCreate("/", "Docs", null, folder: true);
        h.Remote.RemoteCreate("/Docs", "novo.txt", "n");
        var root = h.Fs.Resolve(h.P("/"))!.Value.Item!;
        int? docsEntreAsPaginas = null;
        var chamada = 0;
        h.Remote.AfterDelta = (page, _) =>
        {
            if (++chamada == 1) return Task.FromResult(new DeltaPage(page.Items.Where(r => r.Name != "novo.txt").ToList(), antes, null));
            // entre as páginas: a pasta nova já entrou e a antiga ainda estava com a exclusão pendente
            if (chamada == 2) docsEntreAsPaginas = h.Db.GetChildren(root.Lid).Count(i => i.Name == "Docs");
            return Task.FromResult(page with { Items = page.Items.Where(r => r.Name == "novo.txt").ToList() });
        };
        h.Settle();
        h.Remote.AfterDelta = null;
        Assert.Equal(1, docsEntreAsPaginas);
        var entries = new List<(string, Core.Vfs.FileAttr)>();
        Assert.Equal(0, h.Fs.ReadDir(h.P("/"), entries));
        Assert.Single(entries, e => e.Item1.StartsWith("Docs", StringComparison.Ordinal));
        entries.Clear();
        Assert.Equal(0, h.Fs.ReadDir(h.P("/Docs"), entries));
        Assert.Equal(["novo.txt"], entries.Select(e => e.Item1).ToList());
        Assert.Empty(h.Db.Conflicts());
    }

    [Fact]
    public void Pasta_renomeada_para_o_nome_de_uma_excluida_na_mesma_rodada_nunca_fica_duplicada()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "Docs", null, folder: true);
        h.Remote.RemoteCreate("/Docs", "antigo.txt", "a");
        h.Remote.RemoteCreate("/", "Outra", null, folder: true);
        h.Remote.RemoteCreate("/Outra", "b.txt", "b");
        h.Remote.RemoteCreate("/", "z.txt", "z");
        h.Settle();
        var antes = h.Db.GetDrive(FakeGraph.DriveId)!.DeltaLink!;
        h.Remote.RemoteDelete("/Docs");
        h.Remote.RemoteRename("/Outra", "Docs");
        h.Remote.RemoteUpdate("/z.txt", "z2");
        var root = h.Fs.Resolve(h.P("/"))!.Value.Item!;
        int? docsEntreAsPaginas = null;
        var chamada = 0;
        h.Remote.AfterDelta = (page, _) =>
        {
            if (++chamada == 1) return Task.FromResult(new DeltaPage(page.Items.Where(r => r.Name != "z.txt").ToList(), antes, null));
            if (chamada == 2) docsEntreAsPaginas = h.Db.GetChildren(root.Lid).Count(i => i.Name == "Docs");
            return Task.FromResult(page with { Items = page.Items.Where(r => r.Name == "z.txt").ToList() });
        };
        h.Settle();
        h.Remote.AfterDelta = null;
        Assert.Equal(1, docsEntreAsPaginas);
        Assert.Equal("b", h.ReadFile("/Docs/b.txt"));
        Assert.Null(h.Fs.Resolve(h.P("/Docs/antigo.txt")));
        Assert.Empty(h.Db.Conflicts());
    }

    [Fact]
    public void Pasta_excluida_e_restaurada_na_mesma_rodada_continua_aqui()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "Docs", null, folder: true);
        h.Remote.RemoteCreate("/Docs", "a.txt", "a");
        h.Settle();
        var antes = h.Db.GetDrive(FakeGraph.DriveId)!.DeltaLink!;
        var docs = h.Remote.FindByPath("/Docs")!;
        h.Remote.RemoteRename("/Docs", "Docs"); // volta no delta (como depois de restaurada da Lixeira)
        var chamada = 0;
        h.Remote.AfterDelta = (page, _) => Task.FromResult(++chamada == 1
            ? new DeltaPage([new RemoteItem { Id = docs.Id, Name = "Docs", ParentId = "root", IsFolder = true, IsDeleted = true }], antes, null)
            : page);
        h.Settle();
        h.Remote.AfterDelta = null;
        Assert.True(chamada >= 2, "o delta não passou pelas duas páginas");
        Assert.Equal("a", h.ReadFile("/Docs/a.txt"));
    }

    [Fact]
    public void Pasta_excluida_sem_nada_movido_some_no_fim_da_rodada()
    {
        using var h = new Harness();
        Montar(h);
        var ids = new[] { "/Ferramentas", "/Ferramentas/Advanced Installer", "/Ferramentas/Advanced Installer/Projects",
            "/Ferramentas/Advanced Installer/Projects/setup.aip", "/Ferramentas/velho.txt" }.Select(p => h.Remote.FindByPath(p)!.Id).ToList();
        h.Remote.RemoteDeleteFolderOnly("/Ferramentas"); // só a pasta no delta: os filhos saem junto com ela
        h.Settle();
        Assert.Null(h.Fs.Resolve(h.P("/Ferramentas")));
        Assert.All(ids, id => Assert.Null(h.Db.GetByRemote(FakeGraph.DriveId, id)));
    }
}
