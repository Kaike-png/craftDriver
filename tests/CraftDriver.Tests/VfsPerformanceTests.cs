using System.Reflection;
using CraftDriver.Core.State;
using CraftDriver.Core.Vfs;
using Microsoft.Data.Sqlite;

namespace CraftDriver.Tests;

[Collection("fuse")]
public class VfsPerformanceTests
{
    sealed class Consultas : IDisposable
    {
        readonly SqliteConnection _c;
        public int Count;
        public readonly List<string> Sql = [];
        public Consultas(StateDb db)
        {
            _c = (SqliteConnection)typeof(StateDb).GetField("_c", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(db)!;
            SQLitePCL.raw.sqlite3_trace(_c.Handle, (_, sql) =>
            {
                if (sql.StartsWith("SELECT", StringComparison.Ordinal) || sql.StartsWith("WITH", StringComparison.Ordinal))
                { Count++; Sql.Add(sql); }
            }, null);
        }
        public void Dispose() => SQLitePCL.raw.sqlite3_trace(_c.Handle, (SQLitePCL.strdelegate_trace)null!, null);
    }

    [Fact]
    public void Resolve_repetido_em_pasta_com_10k_entradas_nao_zera_o_cache()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        h.Db.InTransaction(() =>
        {
            for (var i = 0; i < 10_000; i++) h.Db.Insert(new Item { DriveId = FakeGraph.DriveId, ParentLid = h.Drive.RootLid, Name = $"f{i}" });
        });
        for (var i = 0; i < 10_000; i++) Assert.NotNull(h.Fs.Resolve(h.P($"f{i}")));
        using var q = new Consultas(h.Db);
        for (var i = 0; i < 10_000; i++) Assert.NotNull(h.Fs.Resolve(h.P($"f{i}")));
        Assert.True(q.Count <= 10_005, $"{q.Count} SELECTs: cache limpo ao passar de 4096 refaz a descida de cada caminho");
    }

    [Fact]
    public void Invalida_cache_grande_sem_reter_buckets_que_seriam_zerados_a_cada_unlink()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        var itens = new List<Item>();
        for (var i = 0; i < 10_000; i++)
        {
            var it = new Item { DriveId = FakeGraph.DriveId, ParentLid = h.Drive.RootLid, Name = $"f{i}" };
            h.Db.Insert(it);
            itens.Add(it);
        }
        foreach (var it in itens) Assert.NotNull(h.Fs.Resolve(h.P(it.Name)));
        // apagar e inserir não invalidam mais o cache (§24.5/§24.9: o acerto revalida o lid); renomear troca a geração
        var ultimo = itens[^1];
        ultimo.Name = "renomeado";
        h.Db.Update(ultimo);
        Assert.NotNull(h.Fs.Resolve(h.P("f0")));
        foreach (var campo in new[] { "_resolved", "_posicoesCaminhos" })
        {
            var d = typeof(VirtualFs).GetField(campo, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(h.Fs)!;
            var capacidade = (int)d.GetType().GetMethod("EnsureCapacity")!.Invoke(d, [0])!;
            Assert.True(capacidade <= 16, $"{campo} reteve {capacidade} buckets: cada invalidação de rm -rf zeraria a tabela inteira");
        }
    }

    [Fact]
    public void Xattr_xdg_e_desconhecido_nao_resolve_o_caminho()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        using var q = new Consultas(h.Db);
        for (var i = 0; i < 100; i++) Assert.Null(h.Fs.GetXattr(h.P("nao-existe"), "user.xdg.origin.url"));
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void Status_de_1000_irmaos_nao_faz_CTE_de_ancestrais_por_arquivo()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        var pasta = new Item { DriveId = FakeGraph.DriveId, ParentLid = h.Drive.RootLid, Name = "P", IsDir = true, Pinned = true };
        h.Db.Insert(pasta);
        var itens = Enumerable.Range(0, 1000).Select(i => new Item { DriveId = FakeGraph.DriveId, ParentLid = pasta.Lid, Name = $"f{i}", ItemId = $"r{i}" }).ToList();
        foreach (var i in itens) h.Db.Insert(i);
        using var q = new Consultas(h.Db);
        foreach (var i in itens) Assert.Equal(ItemStatus.AlwaysAvailable, h.Fs.StatusOf(i));
        Assert.True(q.Count <= 2, $"{q.Count} consultas para fixação efetiva: o conjunto deve ficar em memória");
        pasta.Pinned = false;
        h.Db.Update(pasta);
        Assert.Equal(ItemStatus.OnlineOnly, h.Fs.StatusOf(itens[0]));
    }

    [Fact]
    public void Fixado_dentro_de_ancestral_excluido_nao_fica_efetivamente_fixado()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        var pai = new Item { DriveId = FakeGraph.DriveId, ParentLid = h.Drive.RootLid, Name = "P", IsDir = true };
        h.Db.Insert(pai);
        var filho = new Item { DriveId = FakeGraph.DriveId, ParentLid = pai.Lid, Name = "f", Pinned = true };
        h.Db.Insert(filho);
        h.Db.SetExcluded(pai.DriveId, pai.Lid, true);
        Assert.False(h.Db.IsEffectivelyPinned(filho.Lid));
        Assert.False(h.Db.IsEffectivelyPinnedVfs(filho.Lid));
        h.Db.SetExcluded(pai.DriveId, pai.Lid, false);
        Assert.True(h.Db.IsEffectivelyPinnedVfs(filho.Lid));
    }

    [Fact]
    public void Colisao_unicode_usa_indice_de_nome_normalizado()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        var item = new Item { DriveId = FakeGraph.DriveId, ParentLid = h.Drive.RootLid, Name = "Ação" };
        h.Db.Insert(item);
        using var q = new Consultas(h.Db);
        Assert.Equal(item.Lid, h.Db.GetChildNoCase(h.Drive.RootLid, "AC\u0327A\u0303O")!.Lid);
        Assert.DoesNotContain(q.Sql, s => s.Contains("GLOB", StringComparison.Ordinal));
        Assert.Contains(q.Sql, s => s.Contains("name_key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lookup_negativo_e_cacheado_e_invalida_quando_o_nome_aparece_na_nuvem()
    {
        RealFuse.Require();
        using var h = new Harness();
        var mnt = Path.Combine(h.Dir, "mnt");
        var loop = CraftDriver.Fuse.FuseHost.MountAsync(h.Fs, mnt, cacheTimeout: 60);
        try
        {
            var root = Path.Combine(mnt, Harness.Name);
            Harness.WaitUntil(() => Directory.Exists(root), "montagem visível");
            using var q = new Consultas(h.Db);
            var psi = new System.Diagnostics.ProcessStartInfo("bash", ["-c", "for i in $(seq 1000); do test -e novo; done; true"])
            { WorkingDirectory = root };
            using var p = System.Diagnostics.Process.Start(psi)!;
            await p.WaitForExitAsync(TestSupport.Ct);
            Assert.True(q.Count <= 15, $"{q.Count} SELECTs para 1000 lookups do mesmo nome ausente");
            h.Remote.RemoteCreate("/", "novo", "abc");
            h.Settle();
            Harness.WaitUntil(() => File.Exists(Path.Combine(root, "novo")), "novo item invalida lookup negativo", 3000);
            Assert.Equal("abc", File.ReadAllText(Path.Combine(root, "novo")));
        }
        finally
        {
            h.Engine.Stop();
            CraftDriver.Fuse.FuseHost.Unmount();
            Assert.True(await loop.WithinAsync(TimeSpan.FromSeconds(15)));
        }
    }

    [Fact]
    public void Fracao_de_nanossegundos_sobrevive_ao_reabrir_o_banco_e_data_remota_nova_descarta_o_complemento()
    {
        var dir = Path.Combine(Path.GetTempPath(), "craftdriver-nanos-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "state.db");
            using (var db = new StateDb(path)) db.DefinirFracaoMtime(7, 123, 456789);
            using (var db = new StateDb(path))
            {
                Assert.Equal(456789, db.FracaoMtime(7, 123));
                Assert.Equal(0, db.FracaoMtime(7, 124));
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}
