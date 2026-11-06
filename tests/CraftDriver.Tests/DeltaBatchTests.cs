using CraftDriver.Core.Sync;

namespace CraftDriver.Tests;

/// <summary>Delta aplicado em transações curtas (seção 3) sem perder itens que chegam antes do pai.</summary>
public class DeltaBatchTests
{
    [Fact]
    public void Arvore_funda_com_filhos_antes_dos_pais_chega_inteira()
    {
        using var h = new Harness();
        // a/b/c/d/e/f criados e depois renomeados do mais fundo para o mais raso: no delta incremental
        // (ordem de alteração) cada filho chega antes do pai — uma passagem por nível
        var path = "";
        foreach (var n in new[] { "a", "b", "c", "d", "e", "f" })
        {
            h.Remote.RemoteCreate(path == "" ? "/" : path, n, null, folder: true);
            path += "/" + n;
        }
        h.Remote.RemoteCreate(path, "fundo.txt", "x");
        h.Remote.RemoteRename(path + "/fundo.txt", "fundo2.txt");
        var parts = new[] { "a", "b", "c", "d", "e", "f" };
        for (var i = parts.Length; i >= 1; i--)
        {
            var p = "/" + string.Join('/', parts[..i]);
            h.Remote.RemoteRename(p, parts[i - 1] + "2");
            parts[i - 1] += "2";
        }
        h.Settle();
        Assert.NotNull(h.Fs.Resolve(h.P("/a2/b2/c2/d2/e2/f2/fundo2.txt")));
    }

    [Fact]
    public void Lotes_pequenos_aplicam_a_pagina_inteira()
    {
        using var h = new Harness();
        var old = h.Drive.DeltaBatchSize;
        h.Drive.DeltaBatchSize = 3;
        try
        {
            h.Remote.RemoteCreate("/", "P", null, folder: true);
            for (var i = 0; i < 20; i++) h.Remote.RemoteCreate("/P", $"{i}.txt", "x");
            h.Settle();
            for (var i = 0; i < 20; i++) Assert.NotNull(h.Fs.Resolve(h.P($"/P/{i}.txt")));
        }
        finally { h.Drive.DeltaBatchSize = old; }
    }
}
