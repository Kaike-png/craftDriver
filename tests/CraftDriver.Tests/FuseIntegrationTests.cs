using System.Diagnostics;
using CraftDriver.Fuse;

namespace CraftDriver.Tests;

/// <summary>Monta o FUSE de verdade e usa ferramentas do sistema por cima.</summary>
[Collection("fuse")]
public class FuseIntegrationTests
{
    static (int code, string output) Sh(string script, string cwd)
    {
        var psi = new ProcessStartInfo("bash", ["-c", script])
        {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output.Trim());
    }

    [Fact]
    public async Task Montagem_real_com_ferramentas_do_shell()
    {
        RealFuse.Require();
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "Documentos", null, folder: true);
        h.Remote.RemoteCreate("/Documentos", "leia-me.txt", "linha 1\nlinha 2\n");
        h.Settle();

        var mnt = Path.Combine(h.Dir, "mnt");
        var loop = FuseHost.MountAsync(h.Fs, mnt);
        try
        {
            var root = Path.Combine(mnt, Harness.Name);
            Harness.WaitUntil(() => Directory.Exists(root), "montagem visível");

            // listagem sem baixar
            var (c1, ls) = Sh("ls -1 Documentos", root);
            Assert.Equal(0, c1);
            Assert.Equal("leia-me.txt", ls);
            Assert.Equal(0, h.Remote.Downloads);
            Assert.Equal("0", Sh("du -k Documentos/leia-me.txt | cut -f1", root).output); // "tamanho em disco" 0 = só nuvem
            Assert.Equal("OnlineOnly", Sh("getfattr --only-values -n user.onedrive.status Documentos/leia-me.txt 2>/dev/null || attr -q -g onedrive.status Documentos/leia-me.txt", root).output);

            // leitura sob demanda
            Assert.Equal("linha 1\nlinha 2", Sh("cat Documentos/leia-me.txt", root).output);
            Assert.Equal(1, h.Remote.Downloads);

            // edição in-place estilo sed -i (arquivo temporário + rename por cima)
            Assert.Equal(0, Sh("sed -i 's/linha 2/linha DOIS/' Documentos/leia-me.txt", root).code);

            // novas pastas e arquivos, cópia, movimento
            Assert.Equal(0, Sh("mkdir -p Projetos/2026 && echo 'olá' > Projetos/2026/a.txt && cp Projetos/2026/a.txt Projetos/b.txt && mv Projetos/b.txt Projetos/c.txt", root).code);
            Assert.Equal(0, Sh("head -c 6000000 /dev/urandom > Projetos/grande.bin", root).code);
            var localHash = Sh("sha256sum Projetos/grande.bin | cut -d' ' -f1", root).output;

            // temporário do LibreOffice não sobe
            Assert.Equal(0, Sh("echo lock > 'Projetos/.~lock.x.ods#'", root).code);

            // exclusão recursiva
            Assert.Equal(0, Sh("mkdir -p Lixo/sub && echo x > Lixo/sub/y && rm -rf Lixo", root).code);

            // a raiz virtual é somente das drives
            Assert.NotEqual(0, Sh("touch nao-pode.txt", mnt).code);

            h.Settle();

            var paths = h.Remote.ListPaths();
            Assert.Contains("/Projetos/2026/a.txt", paths);
            Assert.Contains("/Projetos/c.txt", paths);
            Assert.Contains("/Projetos/grande.bin", paths);
            Assert.DoesNotContain(paths, p => p.Contains("lock"));
            Assert.DoesNotContain(paths, p => p.StartsWith("/Lixo"));
            Assert.DoesNotContain(paths, p => p.Contains("/sed"));
            Assert.Equal("linha 1\nlinha DOIS\n", h.Remote.ReadText("/Documentos/leia-me.txt"));
            Assert.Equal("olá\n", h.Remote.ReadText("/Projetos/c.txt"));
            var remoteBig = h.Remote.FindByPath("/Projetos/grande.bin")!.Content;
            Assert.Equal(localHash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(remoteBig)).ToLowerInvariant());

            // mudança remota aparece no ponto de montagem
            h.Remote.RemoteUpdate("/Projetos/c.txt", "mudou na nuvem");
            h.Settle();
            Thread.Sleep(1100); // attr_timeout do kernel
            Assert.Equal("mudou na nuvem", Sh("cat Projetos/c.txt", root).output);
        }
        finally
        {
            FuseHost.Unmount();
            Assert.True(await loop.WithinAsync(TimeSpan.FromSeconds(15)), "loop do FUSE não terminou após desmontar: " + FuseHost.Diagnostico());
        }
    }

    /// <summary>Semântica que, na API de baixo nível, depende da tabela de inodes do FuseHost.</summary>
    [Fact]
    public void Inode_e_o_mesmo_entre_montagens_e_depois_de_renomear()
    {
        RealFuse.Require();
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "Docs", null, folder: true);
        h.Remote.RemoteCreate("/Docs", "a.txt", "a");
        h.Remote.RemoteCreate("/Docs", "b.txt", "b");
        h.Settle();
        var mnt = Path.Combine(h.Dir, "mnt");
        string Mounted(Func<string, string> body)
        {
            var loop = FuseHost.MountAsync(h.Fs, mnt);
            try
            {
                var root = Path.Combine(mnt, Harness.Name);
                Harness.WaitUntil(() => Directory.Exists(root), "montagem visível");
                return body(root);
            }
            finally
            {
                FuseHost.Unmount();
                Assert.True(loop.Wait(TimeSpan.FromSeconds(15)), "loop do FUSE não terminou após desmontar: " + FuseHost.Diagnostico());
            }
        }

        var first = Mounted(root => Sh("stat -c %i Docs/a.txt", root).output);
        // outra ordem de acesso na segunda montagem: um contador daria outro número
        var second = Mounted(root => Sh("stat -c %i Docs/b.txt Docs >/dev/null; stat -c %i Docs/a.txt", root).output);
        Assert.Equal(first, second);
        var renamed = Mounted(root => Sh("mv Docs/a.txt Docs/c.txt && stat -c %i Docs/c.txt", root).output);
        Assert.Equal(first, renamed);
        var dir = Mounted(root => Sh("stat -c %i Docs", root).output);
        Assert.Equal(dir, Mounted(root => Sh("stat -c %i Docs/b.txt >/dev/null; stat -c %i Docs", root).output));
        Assert.NotEqual(first, dir);
    }

    /// <summary>Semântica que, na API de baixo nível, depende da tabela de inodes do FuseHost.</summary>
    [Fact]
    public async Task Inodes_diretorio_grande_e_renomeacoes()
    {
        RealFuse.Require();
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "Grande", null, folder: true);
        var longName = new string('n', 200);
        for (var i = 0; i < 700; i++) h.Remote.RemoteCreate("/Grande", $"{longName}-{i:D4}.txt", "x"); // vários readdir
        h.Settle();

        var mnt = Path.Combine(h.Dir, "mnt");
        var loop = FuseHost.MountAsync(h.Fs, mnt);
        try
        {
            var root = Path.Combine(mnt, Harness.Name);
            Harness.WaitUntil(() => Directory.Exists(root), "montagem visível");

            // listagem paginada: todas, sem repetição, e de novo (cache do kernel/readdirplus)
            Assert.Equal("700 700", Sh("ls -1 Grande | wc -l | tr -d ' '; ls -1 Grande | sort -u | wc -l", root).output.Replace("\n", " "));
            Assert.Equal("700", Sh("ls -la Grande | grep -c txt", root).output);
            Assert.Equal("700", Sh("find Grande -type f | wc -l | tr -d ' '", root).output);

            // inode estável para o mesmo caminho e distinto entre arquivos
            var (_, inos) = Sh($"stat -c %i Grande/{longName}-0001.txt Grande/{longName}-0001.txt Grande/{longName}-0002.txt", root);
            var parts = inos.Split('\n');
            Assert.Equal(parts[0], parts[1]);
            Assert.NotEqual(parts[0], parts[2]);

            // pasta com filhos já vistos pelo kernel muda de nome: os filhos seguem pelo caminho novo
            Assert.Equal(0, Sh("mkdir -p A/B && echo dentro > A/B/c.txt && cat A/B/c.txt >/dev/null && mv A Z", root).code);
            Assert.Equal("dentro", Sh("cat Z/B/c.txt", root).output);
            Assert.NotEqual(0, Sh("test -e A/B/c.txt", root).code);
            Assert.Equal(0, Sh("mkdir A && echo nova > A/d.txt", root).code); // nome antigo reutilizável
            Assert.Equal("nova", Sh("cat A/d.txt", root).output);

            // mv por cima de arquivo existente: o nome passa a mostrar o conteúdo novo
            Assert.Equal(0, Sh("echo velho > alvo.txt && cat alvo.txt >/dev/null && echo novo > origem.txt && mv -f origem.txt alvo.txt", root).code);
            Assert.Equal("novo", Sh("cat alvo.txt", root).output);
            Assert.NotEqual(0, Sh("test -e origem.txt", root).code);

            // arquivo truncado e reescrito pelo mesmo nome várias vezes (cache de página entre opens)
            Assert.Equal("3", Sh("for i in 1 2 3; do echo $i > v.txt; done; cat v.txt", root).output);
            Assert.Equal(0, Sh("truncate -s 0 v.txt && test ! -s v.txt", root).code);

            h.Settle();
            var paths = h.Remote.ListPaths();
            Assert.Contains("/Z/B/c.txt", paths);
            Assert.Contains("/A/d.txt", paths);
            Assert.DoesNotContain("/origem.txt", paths);
            Assert.Equal("novo\n", h.Remote.ReadText("/alvo.txt"));
        }
        finally
        {
            FuseHost.Unmount();
            Assert.True(await loop.WithinAsync(TimeSpan.FromSeconds(15)), "loop do FUSE não terminou após desmontar: " + FuseHost.Diagnostico());
        }
    }
}
