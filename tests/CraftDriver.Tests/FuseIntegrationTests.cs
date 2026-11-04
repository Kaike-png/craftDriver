using System.Diagnostics;
using CraftDriver.Fuse;

namespace CraftDriver.Tests;

/// <summary>Monta o FUSE de verdade e usa ferramentas do sistema por cima.</summary>
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
    public void Montagem_real_com_ferramentas_do_shell()
    {
        if (!File.Exists("/dev/fuse")) return;
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
            Assert.True(loop.Wait(TimeSpan.FromSeconds(15)), "loop do FUSE não terminou após desmontar");
        }
    }
}
