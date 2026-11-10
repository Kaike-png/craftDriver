using System.Diagnostics;
using CraftDriver.Core.Sync;
using CraftDriver.Fuse;

namespace CraftDriver.Tests;

/// <summary>
/// PENDENCIAS.md §14 (bugs FUSE/VFS) reproduzidos na montagem real ou direto na <see cref="InodeTable"/>.
/// Testes RED de propósito: cada um afirma o comportamento correto e falha pelo motivo descrito no item.
/// errno conferido pelo nome (python <c>errno.errorcode</c>): as mensagens do shell saem em pt-BR.
/// </summary>
[Collection("fuse")]
public class AnaliseFuseVfsTests
{
    static (int code, string output) Sh(string script, string cwd)
    {
        var psi = new ProcessStartInfo("bash", ["-c", "umask 022; " + script])
        {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, (output + stderr.Result).Trim());
    }

    /// <summary>Python com helper <c>E(f)</c>: executa f e imprime OK/resultado ou o nome do errno.</summary>
    static string Py(string body, string cwd)
    {
        var code = "import os, sys, errno, time\n" +
                   "def E(f):\n" +
                   "    try:\n" +
                   "        r = f()\n" +
                   "        return 'OK' if r is None else str(r)\n" +
                   "    except OSError as e:\n" +
                   "        return errno.errorcode.get(e.errno, str(e.errno))\n" + body;
        var psi = new ProcessStartInfo("/usr/bin/python3", ["-c", code])
        {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (output + stderr.Result).Trim();
    }

    static async Task Mounted(Harness h, Action<string, string> body, double timeout = 1.0)
    {
        var mnt = Path.Combine(h.Dir, "mnt");
        var loop = FuseHost.MountAsync(h.Fs, mnt, cacheTimeout: timeout);
        try
        {
            var root = Path.Combine(mnt, Harness.Name);
            Harness.WaitUntil(() => Directory.Exists(root), "montagem visível");
            body(mnt, root);
        }
        finally
        {
            h.Engine.Stop();
            FuseHost.Unmount();
            Assert.True(await loop.WithinAsync(TimeSpan.FromSeconds(15)), "loop do FUSE não terminou após desmontar: " + FuseHost.Diagnostico());
        }
    }


    static string Tabela()
    {
        var t = typeof(FuseHost).GetField("_inodes", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        var byPath = (System.Collections.IDictionary)typeof(InodeTable).GetField("_byPath", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(t)!;
        var byIno = (System.Collections.IDictionary)typeof(InodeTable).GetField("_byIno", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(t)!;
        var trava = typeof(InodeTable).GetField("_lock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(t)!;
        lock (trava)
        {
            var sb = new System.Text.StringBuilder();
            foreach (System.Collections.DictionaryEntry e in byPath) sb.Append($"{e.Key}={e.Value}{(byIno.Contains(e.Value!) ? "" : "(PENDURADO)")}; ");
            return sb.ToString();
        }
    }

    // ---------------- 14.1 rename/move remoto com dentry em cache ----------------

    [Fact]
    public async Task R14_1_rename_remoto_de_arquivo_com_dentry_em_cache_abre_pelo_nome_novo()
    {
        RealFuse.Require();
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "a.txt", "conteudo");
        h.Settle();
        await Mounted(h, (_, root) =>
        {
            Assert.Equal(0, Sh("stat a.txt >/dev/null", root).code); // o kernel guarda dentry + inode (lid+1)
            h.Remote.RemoteRename("/a.txt", "b.txt");
            h.Settle();
            Thread.Sleep(1200); // entry/attr timeout (1 s)
            Assert.Equal("b.txt", Sh("ls -1", root).output);
            Assert.Equal("conteudo", Sh("cat b.txt", root).output);
            Assert.Equal("OK", Py("print(E(lambda: os.stat('b.txt') and None))", root));
        });
    }

    [Fact]
    public async Task R14_1_rename_remoto_de_pasta_listada_continua_navegavel()
    {
        RealFuse.Require();
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "Docs", null, folder: true);
        h.Remote.RemoteCreate("/Docs", "f.txt", "filho");
        h.Settle();
        await Mounted(h, (_, root) =>
        {
            Assert.Equal("f.txt", Sh("ls -1 Docs", root).output); // Dolphin com a pasta aberta
            var inoFilho = Sh("stat -c %i Docs/f.txt", root).output;
            h.Remote.RemoteRename("/Docs", "Docs2");
            h.Settle();
            Thread.Sleep(1200);
            Assert.Equal("Docs2", Sh("ls -1", root).output);
            Assert.Equal((0, "f.txt"), Sh("ls -1 Docs2", root));
            Assert.Equal((0, "filho"), Sh("cat Docs2/f.txt", root));
            Assert.Equal(inoFilho, Sh("stat -c %i Docs2/f.txt", root).output); // não pode virar inode virtual
        });
    }

    /// <summary>Controle: sem dentry em cache o mesmo rename funciona (isola a causa na InodeTable).</summary>
    [Fact]
    public async Task R14_1_controle_rename_remoto_sem_dentry_em_cache_funciona()
    {
        RealFuse.Require();
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "a.txt", "conteudo");
        h.Settle();
        await Mounted(h, (_, root) =>
        {
            h.Remote.RemoteRename("/a.txt", "b.txt");
            h.Settle();
            Assert.Equal("conteudo", Sh("cat b.txt", root).output);
        });
    }

    /// <summary>
    /// Mesma causa (caminho velho continua em _byPath): depois do rename remoto, um arquivo NOVO criado na nuvem
    /// com o nome antigo recebe o inode do renomeado — dois arquivos diferentes com o mesmo st_ino
    /// (diff/cp/rsync -H tratam como o mesmo arquivo / hardlink).
    /// </summary>
    [Fact]
    public async Task R14_1b_arquivo_novo_com_nome_antigo_nao_herda_inode_do_renomeado()
    {
        RealFuse.Require();
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "a.txt", "velho");
        h.Settle();
        await Mounted(h, (_, root) =>
        {
            Assert.Equal(0, Sh("stat a.txt >/dev/null", root).code);
            h.Remote.RemoteRename("/a.txt", "b.txt");
            h.Remote.RemoteCreate("/", "a.txt", "novo");
            h.Settle();
            Thread.Sleep(1200);
            var ia = Sh("stat -c %i a.txt", root).output;
            var ib = Sh("stat -c %i b.txt", root).output;
            Assert.NotEqual(ia, ib);
        });
    }

    [Fact]
    public void R14_1_InodeTable_lookup_do_nome_novo_adota_o_caminho_novo()
    {
        var lids = new Dictionary<string, long> { ["/d/a.txt"] = 41 };
        var t = new InodeTable(p => lids.TryGetValue(p, out var l) ? l : null);
        var ino = t.Lookup("/d/a.txt");
        Assert.Equal(42UL, ino);
        lids.Remove("/d/a.txt"); // delta: o lid 41 passou a /d/b.txt
        lids["/d/b.txt"] = 41;
        Assert.Equal(ino, t.Lookup("/d/b.txt"));
        Assert.Equal("/d/b.txt", t.PathOf(ino)); // hoje: "/d/a.txt" → open/getattr por inode dão ENOENT
    }

    // ---------------- 14.2 alias pendurado ----------------

    [Fact]
    public void R14_2_InodeTable_forget_nao_deixa_alias_pendurado()
    {
        var lids = new Dictionary<string, long> { ["/a.txt"] = 41 };
        var t = new InodeTable(p => lids.TryGetValue(p, out var l) ? l : null);
        var ino = t.Lookup("/a.txt");
        lids.Remove("/a.txt");
        lids["/b.txt"] = 41; // rename remoto
        Assert.Equal(ino, t.Lookup("/b.txt")); // alias "/b.txt" → ino (14.1)
        t.Forget(ino, 2); // o kernel esquece o inode (pressão de memória / drop_caches)
        // hoje: _byPath["/b.txt"] continua apontando para ino fora de _byIno → KeyNotFoundException → EIO
        var again = Record.Exception(() => t.Lookup("/b.txt"));
        Assert.Null(again);
        Assert.Equal("/b.txt", t.PathOf(ino));
    }

    [Fact]
    public void R14_2_InodeTable_detach_depois_do_forget_do_alias_nao_lanca()
    {
        var lids = new Dictionary<string, long> { ["/a.txt"] = 41 };
        var t = new InodeTable(p => lids.TryGetValue(p, out var l) ? l : null);
        var ino = t.Lookup("/a.txt");
        lids.Remove("/a.txt");
        lids["/b.txt"] = 41;
        t.Lookup("/b.txt");
        t.Forget(ino, 2);
        Assert.Null(Record.Exception(() => t.Detach("/b.txt"))); // unlink de b.txt → hoje KeyNotFound → EIO
    }

    /// <summary>
    /// 14.2 na montagem real, sem root: o forget vem de recuperação de memória do PRÓPRIO cgroup (memory.reclaim,
    /// gravável pelo dono no cgroup v2 delegado do usuário) — dentries/inodes são cobrados ao cgroup de quem fez o
    /// lookup. a→b remoto, stat b (alias), reclaim → FORGET com o alias "/b.txt" ainda na tabela → o lookup seguinte
    /// de b.txt lança KeyNotFoundException → EIO.
    /// </summary>
    [Fact]
    public async Task R14_2_alias_pendurado_depois_do_forget_da_EIO_na_montagem()
    {
        RealFuse.Require();
        var cg = "/sys/fs/cgroup" + File.ReadAllText("/proc/self/cgroup").Trim().Split("::")[1] + "/memory.reclaim";
        Assert.SkipUnless(File.Exists(cg) && Sh($"[ -w '{cg}' ] && echo s", "/").output == "s", "sem memory.reclaim gravável no cgroup do teste");
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "a.txt", "conteudo");
        h.Settle();
        await Mounted(h, (_, root) =>
        {
            // bash (não python: o importlib lista o cwd → readdirplus em vez de lookup)
            var log = new List<string>();
            string St(string n) { var r = Sh($"stat -c %i {n} 2>&1", root).output; log.Add($"stat {n}={r} | {Tabela()}"); return r; }
            Assert.Equal("4", St("a.txt"));
            h.Remote.RemoteRename("/a.txt", "b.txt");
            h.Settle();
            Thread.Sleep(1200);
            Assert.Equal("4", St("b.txt")); // alias "/b.txt" → 4 (14.1: Path continua "/a.txt")
            for (var i = 0; i < 5 && Tabela().Contains("b.txt=4"); i++)
            {
                Sh($"echo 1G > '{cg}' 2>/dev/null", "/"); // o kernel solta dentries/inodes → FORGET
                Thread.Sleep(300);
            }
            log.Add("depois do reclaim | " + Tabela());
            Assert.DoesNotContain("PENDURADO", Tabela());
            Assert.SkipUnless(!Tabela().Contains("b.txt=4"), "o kernel não mandou forget (reclaim não soltou a dentry): " + string.Join("\n", log));
            var stat = St("b.txt"); // lookup → KeyNotFoundException → EIO
            var cat = Sh("cat b.txt 2>&1", root).output;
            log.Add("cat=" + cat + " | ls -li=" + Sh("ls -li 2>&1", root).output);
            Assert.True(cat == "conteudo" && stat == "4", string.Join("\n", log));
        });
    }

    // ---------------- 14.3 getattr de inode desligado sem fh ----------------

    [Fact]
    public async Task R14_3_stat_de_arquivo_apagado_ainda_aberto_por_caminho_sem_fh()
    {
        RealFuse.Require();
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "x.txt", "abc");
        h.Settle();
        await Mounted(h, (_, root) =>
        {
            // /proc/self/fd/N → getattr pelo inode sem FUSE_GETATTR_FH; no ext4 dá certo (nlink 0)
            var r = Py("""
                fd = os.open('x.txt', os.O_RDONLY)
                os.unlink('x.txt')
                time.sleep(1.2)
                print(E(lambda: os.stat(f'/proc/self/fd/{fd}').st_size))
                print(E(lambda: os.fstat(fd).st_size))
                """, root);
            Assert.Equal("3\n3", r);
        });
    }

    // ---------------- 14.5 O_RDONLY|O_TRUNC ----------------

    [Fact]
    public async Task R14_5_open_rdonly_com_trunc_trunca_como_no_ext4()
    {
        RealFuse.Require();
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "t.txt", "conteudo");
        h.Settle();
        await Mounted(h, (_, root) =>
        {
            var ext4 = Path.Combine(h.Dir, "t-local.txt");
            File.WriteAllText(ext4, "conteudo");
            var prog = "print(E(lambda: os.close(os.open(sys.argv_f, os.O_RDONLY | os.O_TRUNC))), os.path.getsize(sys.argv_f))";
            Assert.Equal("OK 0", Py("sys.argv_f = " + Quote(ext4) + "\n" + prog, root)); // referência: disco local
            Assert.Equal("OK 0", Py("sys.argv_f = 't.txt'\n" + prog, root));
        });
    }

    static string Quote(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    // ---------------- 14.6 rmdir com filhos só excluídos ----------------

    [Fact]
    public async Task R14_6_rmdir_de_pasta_que_parece_vazia_explica_a_recusa()
    {
        RealFuse.Require();
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "P", null, folder: true);
        h.Remote.RemoteCreate("/P", "Sub", null, folder: true);
        h.Settle();
        var sub = h.Fs.Resolve(h.P("/P/Sub"))!.Value.Item!.Lid;
        Assert.Null(h.Engine.SetExcluded(sub, true));
        await Mounted(h, (_, root) =>
        {
            Assert.Equal((0, ""), Sh("ls -A P", root)); // parece vazia
            var r = Py("print(E(lambda: os.rmdir('P')))", root);
            Assert.Equal("ENOTEMPTY", r); // seguro (apagar P levaria Sub na nuvem) — comportamento correto
            // o defeito descrito: a recusa não é explicada em lugar nenhum
            Assert.Contains(h.Engine.Activity.Entries, e => e.Text.Contains("P") && e.Text.Contains("exclu", StringComparison.OrdinalIgnoreCase));
        });
    }

    // ---------------- 14.7 mtime durante escrita / precisão ----------------

    [Fact]
    public async Task R14_7_mtime_avanca_a_cada_write_com_o_arquivo_aberto()
    {
        RealFuse.Require();
        using var h = new Harness();
        h.Settle();
        await Mounted(h, (_, root) =>
        {
            var prog = """
                fd = os.open(sys.argv_f, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o644)
                os.write(fd, b'1'); t1 = os.fstat(fd).st_mtime_ns
                time.sleep(1.3)
                os.write(fd, b'2'); t2 = os.fstat(fd).st_mtime_ns
                print('avancou' if t2 - t1 >= 1_000_000_000 else f'parado {t2 - t1}')
                os.close(fd)
                """;
            Assert.Equal("avancou", Py("sys.argv_f = " + Quote(Path.Combine(h.Dir, "m-local.txt")) + "\n" + prog, root));
            Assert.Equal("avancou", Py("sys.argv_f = 'm.txt'\n" + prog, root));
        });
    }

    [Fact]
    public async Task R14_7_cp_p_preserva_mtime_com_nanossegundos_e_destino_nao_fica_mais_velho()
    {
        RealFuse.Require();
        using var h = new Harness();
        h.Settle();
        await Mounted(h, (_, root) =>
        {
            var src = Path.Combine(h.Dir, "fonte.c");
            Assert.Equal(0, Sh($"echo x > '{src}' && touch -d '2026-01-02 03:04:05.123456789' '{src}' && cp -p '{src}' dest.c", root).code);
            // make: alvo "mais velho" que a fonte → recompila sempre; o ext4 guarda os ns
            Assert.Equal("ok", Sh($"[ dest.c -ot '{src}' ] && echo mais-velho || echo ok", root).output);
        });
    }

    // ---------------- 14.8 link/symlink/mknod ----------------

    [Fact]
    public async Task R14_8_link_symlink_mkfifo_dao_EPERM_e_nao_ENOSYS()
    {
        RealFuse.Require();
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "alvo.txt", "x");
        h.Settle();
        await Mounted(h, (_, root) =>
        {
            var r = Py("""
                print(E(lambda: os.link('alvo.txt', 'hl')))
                print(E(lambda: os.symlink('alvo.txt', 'sl')))
                print(E(lambda: os.mkfifo('ff')))
                """, root);
            Assert.Equal("EPERM\nEPERM\nEPERM", r);
        });
    }

    // ---------------- 14.9 raiz virtual ----------------

    [Fact]
    public async Task R14_9_ls_l_da_raiz_virtual_nao_mostra_1970_e_permissao_nao_mente()
    {
        RealFuse.Require();
        using var h = new Harness();
        h.Settle();
        await Mounted(h, (mnt, _) =>
        {
            var ls = Sh("LC_ALL=C ls -l --time-style=+%Y", mnt).output;
            var ano = Sh("TZ=UTC LC_ALL=C ls -l --time-style=+%Y | awk 'NR>1 {print $6}'", mnt).output;
            var m1 = Sh("stat -c %Y .", mnt).output;
            Thread.Sleep(2100);
            var m2 = Sh("stat -c %Y .", mnt).output;
            var modo = Sh("stat -c %a .", mnt).output;
            var w = Sh("[ -w . ] && echo gravavel || echo so-leitura", mnt).output;
            var c = Py("print(E(lambda: os.close(os.open('x', os.O_CREAT|os.O_WRONLY, 0o644))))", mnt);
            var fatos = $"ls -l:\n{ls}\nmtime raiz {m1} → {m2}; modo {modo}; test -w={w}; create={c}";
            Assert.True(ano.Split('\n').All(a => int.Parse(a) >= 2000), "entradas das drives com data de 1970 (mtime 0): " + fatos);
            Assert.True(m1 == m2, "mtime da raiz é 'agora' a cada stat: " + fatos);
            Assert.True(w == "so-leitura" || c == "OK", "test -w mente: " + fatos);
        });
    }

    [Fact]
    public async Task R18_handler_local_nao_reinvalida_o_cache_que_o_proprio_kernel_acabou_de_alterar()
    {
        RealFuse.Require();
        using var h = new Harness();
        h.Engine.Paused = true;
        var eventos = 0;
        h.Fs.ItemChanged += (_, _) => Interlocked.Increment(ref eventos);
        await Mounted(h, (_, root) =>
        {
            Assert.Equal((0, "abc"), Sh("printf abc > dados.tmp; mv dados.tmp novo.tmp; cat novo.tmp; rm novo.tmp", root));
            Assert.Equal(0, Volatile.Read(ref eventos)); // create/write/rename/unlink locais já invalidam as dentries no kernel
        });
    }

    [Fact]
    public async Task R18_delta_invalida_dentry_positiva_negativa_e_pasta_com_timeout_longo()
    {
        RealFuse.Require();
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "Docs", null, folder: true);
        h.Remote.RemoteCreate("/Docs", "a.txt", "conteudo");
        h.Settle();
        await Mounted(h, (_, root) =>
        {
            Assert.Equal(0, Sh("stat Docs Docs/a.txt >/dev/null; ! test -e Nova", root).code);
            var ino = Sh("stat -c %i Docs/a.txt", root).output;
            h.Remote.RemoteRename("/Docs", "Nova");
            h.Settle();
            // Dolphin já consultou o nome antigo e o novo. Com timeout 60 s, nenhuma expiração pode mascarar a falta de notify.
            Harness.WaitUntil(() => Sh("test ! -e Docs && test -f Nova/a.txt", root).code == 0, "delta invalidou as dentries em cache", 3000);
            Assert.Equal((0, "conteudo"), Sh("cat Nova/a.txt", root));
            Assert.Equal(ino, Sh("stat -c %i Nova/a.txt", root).output);
        }, timeout: 60);
    }

    [Fact]
    public async Task R18_delta_invalida_atributos_conteudo_e_exclusao()
    {
        RealFuse.Require();
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "a.txt", "abc");
        h.Settle();
        await Mounted(h, (_, root) =>
        {
            Assert.Equal((0, "abc"), Sh("cat a.txt", root));
            Assert.Equal("3", Sh("stat -c %s a.txt", root).output);
            h.Remote.RemoteUpdate("/a.txt", "conteudo novo");
            h.Settle();
            Harness.WaitUntil(() => Sh("stat -c %s a.txt", root).output == "13", "delta invalidou tamanho do inode", 3000);
            Assert.Equal((0, "conteudo novo"), Sh("cat a.txt", root));
            h.Remote.RemoteDelete("/a.txt");
            h.Settle();
            Harness.WaitUntil(() => Sh("test ! -e a.txt", root).code == 0, "delta invalidou exclusão", 3000);
        }, timeout: 60);
    }

    // ---------------- 14.10 UTF-8 inválido ----------------

    [Fact]
    public async Task R14_10_nome_com_utf8_invalido_e_recusado_e_nao_colide()
    {
        RealFuse.Require();
        using var h = new Harness();
        h.Settle();
        await Mounted(h, (_, root) =>
        {
            var r = Py("""
                def mk(n, data):
                    fd = os.open(n, os.O_WRONLY | os.O_CREAT, 0o644); os.write(fd, data); os.close(fd)
                print(E(lambda: mk(b'a\xff.txt', b'1')))
                print(E(lambda: mk(b'a\xfe.txt', b'2')))
                print(sorted(os.listdir(b'.')))
                print(E(lambda: open(b'a\xff.txt', 'rb').read()))
                """, root);
            var lines = r.Split('\n');
            // o certo: recusar (EILSEQ/EINVAL — o OneDrive não guarda bytes inválidos); hoje cria "a\uFFFD.txt"
            // e o 2º open(O_CREAT) abre o MESMO arquivo e sobrescreve o conteúdo do 1º
            Assert.True(lines[0] is "EILSEQ" or "EINVAL", r);
        });
    }

    // ---------------- 14.11 lookup de nome > 255 bytes ----------------

    [Fact]
    public async Task R14_11_lookup_de_nome_longo_da_ENAMETOOLONG()
    {
        RealFuse.Require();
        using var h = new Harness();
        h.Settle();
        await Mounted(h, (_, root) =>
        {
            Assert.Equal("ENAMETOOLONG", Py("print(E(lambda: os.stat('a' * 300)))", h.Dir)); // referência: ext4/tmpfs
            Assert.Equal("ENAMETOOLONG", Py("print(E(lambda: os.stat('a' * 300)))", root));
        });
    }

    // ---------------- 14.12 datas antes de 1970 ----------------

    [Fact]
    public async Task R14_12_touch_antes_de_1970_com_fracao_de_segundo()
    {
        RealFuse.Require();
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "velho.txt", "x");
        h.Settle();
        await Mounted(h, (_, root) =>
        {
            Assert.Equal(0, Sh("touch -d '1960-01-01 00:00:00 UTC' velho.txt", root).code);
            Assert.Equal("-315619200", Sh("stat -c %Y velho.txt", root).output); // inteiro: certo
            Assert.Equal(0, Sh("touch -d '1960-01-01 00:00:00.5 UTC' velho.txt", root).code);
            Thread.Sleep(1200);
            // st_mtim = {-315619200, 500000000}; total = -315619199500000000 ns.
            // GNU stat %.9Y imprime o valor escalar (-315619199.5), não sec.nsec do timespec.
            Assert.Equal("-315619199500000000", Py("print(os.stat('velho.txt').st_mtime_ns)", root));
        });
    }
}
