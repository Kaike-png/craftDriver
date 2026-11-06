using System.Reflection;
using CraftDriver.Fuse;

namespace CraftDriver.Tests;

/// <summary>
/// <see cref="InodeTable"/> sem montagem: caminho ↔ inode quando o delta renomeia/move por trás do kernel
/// (§14.1/14.2), corrida do readdirplus (§14.13) e índice de filhos (§16).
/// </summary>
public class InodeTableTests
{
    /// <summary>Banco falso: caminho → lid (o que o VirtualFs.Resolve responderia agora).</summary>
    sealed class Banco
    {
        public readonly Dictionary<string, long> Lids = new(StringComparer.Ordinal);
        public long? Of(string p) => Lids.TryGetValue(p, out var l) ? l : null;
        public void Mover(string de, string para)
        {
            foreach (var (k, v) in Lids.Where(kv => kv.Key == de || kv.Key.StartsWith(de + "/", StringComparison.Ordinal)).ToList())
            {
                Lids.Remove(k);
                Lids[para + k[de.Length..]] = v;
            }
        }
    }

    /// <summary>Invariante: _byPath[p] == i ⇔ _byIno[i].Path == p (nenhum caminho aponta para inode que não existe).</summary>
    static void Consistente(InodeTable t)
    {
        var f = BindingFlags.Instance | BindingFlags.NonPublic;
        var byPath = (System.Collections.IDictionary)typeof(InodeTable).GetField("_byPath", f)!.GetValue(t)!;
        var byIno = (System.Collections.IDictionary)typeof(InodeTable).GetField("_byIno", f)!.GetValue(t)!;
        foreach (System.Collections.DictionaryEntry e in byPath)
        {
            Assert.True(byIno.Contains(e.Value!), $"caminho {e.Key} aponta para inode {e.Value} que não está na tabela");
            Assert.Equal((string)e.Key, t.PathOf((ulong)e.Value!));
        }
    }

    [Fact]
    public void Pasta_renomeada_na_nuvem_leva_os_filhos_ao_ser_achada_pelo_nome_novo()
    {
        var db = new Banco { Lids = { ["/D"] = 9, ["/D/f.txt"] = 10 } };
        var t = new InodeTable(db.Of);
        var dir = t.Lookup("/D");
        var filho = t.Lookup("/D/f.txt");
        db.Mover("/D", "/E");
        Assert.Equal(dir, t.Lookup("/E"));
        Assert.Equal("/E", t.PathOf(dir));
        Assert.Equal("/E/f.txt", t.PathOf(filho)); // opendir/open por inode do filho não pode resolver o caminho velho
        Assert.Equal(filho, t.Lookup("/E/f.txt"));
        Consistente(t);
    }

    [Fact]
    public void Nome_antigo_ocupado_por_outro_item_recebe_outro_inode()
    {
        var db = new Banco { Lids = { ["/a.txt"] = 41 } };
        var t = new InodeTable(db.Of);
        var velho = t.Lookup("/a.txt");
        db.Mover("/a.txt", "/b.txt");
        db.Lids["/a.txt"] = 50; // outro arquivo criado na nuvem com o nome antigo
        var novo = t.Lookup("/a.txt");
        Assert.Equal(51UL, novo);
        Assert.Null(t.PathOf(velho)); // até o próximo lookup do nome novo
        Assert.Equal(velho, t.Lookup("/b.txt"));
        Assert.Equal("/b.txt", t.PathOf(velho));
        Consistente(t);
    }

    [Fact]
    public void Move_local_depois_de_rename_remoto_e_forget_nao_lanca_nem_deixa_caminho_pendurado()
    {
        var db = new Banco { Lids = { ["/d/a"] = 41 } };
        var t = new InodeTable(db.Of);
        var ino = t.Lookup("/d/a");
        db.Mover("/d/a", "/d/b");
        Assert.Equal(ino, t.Lookup("/d/b"));
        t.Move("/d/b", "/d/c"); // mv local
        Assert.Equal("/d/c", t.PathOf(ino));
        Consistente(t);
        t.Forget(ino, 2);
        Assert.Null(t.PathOf(ino));
        Assert.Null(Record.Exception(() => t.Detach("/d/c")));
        Assert.Null(Record.Exception(() => t.Move("/d/c", "/d/e")));
        Consistente(t);
    }

    [Fact]
    public void Detach_e_Move_nao_confundem_prefixo_de_nome()
    {
        var t = new InodeTable();
        var a = t.Lookup("/a");
        var af = t.Lookup("/a/f");
        var ab = t.Lookup("/ab");
        var abf = t.Lookup("/ab/f");
        t.Move("/a", "/z");
        Assert.Equal("/z/f", t.PathOf(af));
        Assert.Equal("/ab/f", t.PathOf(abf));
        t.Detach("/ab");
        Assert.Null(t.PathOf(ab));
        Assert.Null(t.PathOf(abf));
        Assert.Equal("/z", t.PathOf(a));
        Consistente(t);
    }

    [Fact]
    public void Move_com_exchange_troca_as_subarvores()
    {
        var t = new InodeTable();
        var x = t.Lookup("/x");
        var xf = t.Lookup("/x/f");
        var y = t.Lookup("/y");
        var yg = t.Lookup("/y/g");
        t.Move("/x", "/y", exchange: true);
        Assert.Equal("/y", t.PathOf(x));
        Assert.Equal("/y/f", t.PathOf(xf));
        Assert.Equal("/x", t.PathOf(y));
        Assert.Equal("/x/g", t.PathOf(yg));
        Consistente(t);
    }

    /// <summary>
    /// §14.13: dois readdirplus do mesmo diretório ao mesmo tempo. Um entrega a entrada (o kernel passa a ter uma
    /// referência); o outro não tem espaço no buffer e desiste. Com "Peek, depois AddRef/DropIfUnused" o segundo
    /// apagava o nó entre o Peek e o AddRef do primeiro → referência que a tabela não conhece → EBADF/ENOENT.
    /// </summary>
    [Fact]
    public async Task Readdirplus_concorrente_nao_perde_a_referencia_entregue_ao_kernel()
    {
        var t = new InodeTable();
        const int N = 200_000;
        long entregues = 0;
        var go = new Barrier(2);
        var a = Task.Run(() =>
        {
            go.SignalAndWait();
            for (var i = 0; i < N; i++)
                if (t.ReaddirEntry("/d/x", null, _ => true) != 0) entregues++;
        }, TestSupport.Ct);
        var b = Task.Run(() =>
        {
            go.SignalAndWait();
            for (var i = 0; i < N; i++) t.ReaddirEntry("/d/x", null, _ => false); // não coube
        }, TestSupport.Ct);
        await Task.WhenAll(a, b);
        Assert.Equal(N, entregues);
        var ino = t.Lookup("/d/x"); // +1
        Assert.Equal("/d/x", t.PathOf(ino));
        t.Forget(ino, N); // o kernel devolve tudo o que recebeu
        Assert.Equal("/d/x", t.PathOf(ino)); // ainda falta o Lookup acima
        t.Forget(ino, 1);
        Assert.Null(t.PathOf(ino));
        Consistente(t);
    }

    [Fact]
    public void Readdirplus_que_nao_coube_nao_deixa_no_na_tabela()
    {
        var t = new InodeTable();
        var antes = t.Count;
        Assert.Equal(0UL, t.ReaddirEntry("/d/y", null, _ => false));
        Assert.Equal(antes, t.Count);
        var ino = t.ReaddirEntry("/d/y", 77, _ => true);
        Assert.Equal(78UL, ino); // lid informado pelo ReadDir: sem consultar o banco
        Assert.Equal("/d/y", t.PathOf(ino));
    }

    [Fact]
    public void Subarvore_inclui_descendentes_quando_o_kernel_esqueceu_o_pai()
    {
        var t = new InodeTable();
        var pai = t.Lookup("/a");
        var filho = t.Lookup("/a/f");
        t.Forget(pai, 1);
        t.Move("/a", "/b");
        Assert.Equal("/b/f", t.PathOf(filho));
        t.Detach("/b");
        Assert.Null(t.PathOf(filho));
        Consistente(t);
    }

    [Fact]
    public void Subarvore_inclui_pais_intermediarios_sem_inode()
    {
        var t = new InodeTable();
        var filho = t.Lookup("/a/sub/f");
        t.Move("/a", "/b");
        Assert.Equal("/b/sub/f", t.PathOf(filho));
        Consistente(t);
    }

    [Fact]
    public void Lid_informado_nao_consulta_o_banco()
    {
        var consultas = 0;
        var t = new InodeTable(_ => { consultas++; return 5; });
        Assert.Equal(8UL, t.Lookup("/p", 7));
        Assert.Equal(0, consultas);
    }

    [Fact]
    public void Readdirplus_sem_caminho_usa_nodeid_zero()
    {
        var f = typeof(FuseHost).GetMethod("InodeNaoLigado", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(0UL, (ulong)f.Invoke(null, null)!);
    }

    [Fact]
    public void Relocate_poe_o_inode_no_caminho_atual()
    {
        var t = new InodeTable(p => p switch { "/D" => 9, "/D/f" => 10, _ => null });
        var d = t.Lookup("/D");
        var f = t.Lookup("/D/f");
        t.Relocate(d, "/X/D");
        Assert.Equal("/X/D/f", t.PathOf(f));
        t.Detach("/X/D");
        t.Relocate(d, "/Y"); // desligado (path null): volta a ter caminho
        Assert.Equal("/Y", t.PathOf(d));
        Consistente(t);
    }
}
