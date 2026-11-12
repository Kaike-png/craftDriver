using System.Diagnostics;
using CraftDriver.Fuse;

namespace CraftDriver.Tests;

/// <summary>
/// Operações de arquivo que nenhum teste exercitava (PENDENCIAS seção 10): fsync, flush (close de fd duplicado),
/// statfs, xattrs que não são do CraftDriver, O_APPEND, pread parcial, mmap e O_EXCL. Tudo pelo FUSE real, com
/// python/coreutils por cima; errno conferido pelo nome (as mensagens saem em pt-BR).
/// </summary>
[Collection("fuse")]
public class FileOpsTests
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

    /// <summary>Roda um script python na pasta da drive; a saída padrão é o resultado.</summary>
    static string Py(string root, string code)
    {
        var file = Path.Combine(Path.GetTempPath(), "cd-fileops-" + Guid.NewGuid().ToString("N")[..8] + ".py");
        File.WriteAllText(file, "import os, errno, mmap, sys\n" + code);
        try
        {
            var (rc, output) = Sh($"python3 '{file}'", root);
            Assert.True(rc == 0, "python falhou: " + output);
            return output;
        }
        finally { File.Delete(file); }
    }

    sealed class Mounted : IDisposable
    {
        public readonly Harness H = new();
        public readonly string Root;
        readonly Task _loop;

        public Mounted(Action<Harness>? setup = null)
        {
            setup?.Invoke(H);
            H.Settle();
            var mnt = Path.Combine(H.Dir, "mnt");
            _loop = FuseHost.MountAsync(H.Fs, mnt);
            Root = Path.Combine(mnt, Harness.Name);
            Harness.WaitUntil(() => Directory.Exists(Root), "montagem visível");
        }

        public void Dispose()
        {
            FuseHost.Unmount();
            Assert.True(_loop.Wait(TimeSpan.FromSeconds(15)), "loop do FUSE não terminou após desmontar: " + FuseHost.Diagnostico());
            H.Dispose();
        }
    }

    [Fact]
    public void Fsync_com_o_arquivo_aberto_nao_envia_conteudo_pela_metade()
    {
        RealFuse.Require();
        using var m = new Mounted();
        // escreve, fsync, espera mais que o atraso de envio, escreve mais e só então fecha
        Py(m.Root, """
            import time
            fd = os.open("diario.txt", os.O_CREAT | os.O_WRONLY, 0o644)
            os.write(fd, b"parte 1\n")
            os.fsync(fd)
            # outro leitor já vê o que foi sincronizado
            assert open("diario.txt", "rb").read() == b"parte 1\n"
            time.sleep(2.5)
            os.write(fd, b"parte 2\n")
            os.close(fd)
            """);
        m.H.Settle();
        Assert.Equal("parte 1\nparte 2\n", m.H.Remote.ReadText("/diario.txt"));
        Assert.Equal(1, m.H.Remote.Uploads); // nada de envio intermediário com "parte 1" só
    }

    [Fact]
    public void Close_de_fd_duplicado_nao_encerra_a_escrita_do_outro()
    {
        RealFuse.Require();
        using var m = new Mounted();
        // dup(): o close do primeiro fd manda FLUSH; o segundo continua escrevendo no mesmo arquivo aberto
        Py(m.Root, """
            fd = os.open("dup.txt", os.O_CREAT | os.O_WRONLY, 0o644)
            fd2 = os.dup(fd)
            os.write(fd, b"a")
            os.close(fd)
            os.write(fd2, b"b")
            os.close(fd2)
            """);
        m.H.Settle();
        Assert.Equal("ab", m.H.Remote.ReadText("/dup.txt"));
    }

    [Fact]
    public void Df_mostra_a_cota_da_drive()
    {
        RealFuse.Require();
        using var m = new Mounted();
        Harness.WaitUntil(() => m.H.Drive.QuotaTotal > 0, "cota lida da nuvem");
        // FakeGraph: 5 GiB de cota, 1 GiB usado
        var (rc, output) = Sh("stat -f -c '%S %b %a %l' .", m.Root);
        Assert.Equal(0, rc);
        Assert.Equal($"4096 {(5L << 30) / 4096} {(4L << 30) / 4096} 255", output);
        // df funciona (exit 0) e não mostra "-" (sem informação)
        var df = Sh("df -B1 --output=size,avail . | tail -1", m.Root);
        Assert.Equal(0, df.code);
        Assert.Equal($"{5L << 30} {4L << 30}", string.Join(' ', df.output.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
    }

    [Fact]
    public void Xattr_alheio_e_recusado_sem_mexer_no_arquivo_e_cp_a_funciona()
    {
        RealFuse.Require();
        using var m = new Mounted(h => h.Remote.RemoteCreate("/", "foto.jpg", "jpeg"));
        var uploadsBefore = m.H.Remote.Uploads;
        var result = Py(m.Root, """
            def err(f):
                try: f(); return "ok"
                except OSError as e: return errno.errorcode[e.errno]
            print(err(lambda: os.setxattr("foto.jpg", "user.xdg.tags", b"ferias")))
            print(err(lambda: os.getxattr("foto.jpg", "user.xdg.tags")))
            print(err(lambda: os.removexattr("foto.jpg", "user.xdg.tags")))
            print(sorted(os.listxattr("foto.jpg")))
            """).Split('\n');
        Assert.Equal("ENOTSUP", result[0]); // o OneDrive não guarda xattrs: recusar é honesto
        Assert.Equal("ENODATA", result[1]);
        Assert.Equal("ENOTSUP", result[2]);
        Assert.Contains("user.onedrive.status", result[3]);
        Assert.Equal(0, m.H.Remote.Downloads); // nem setxattr nem listxattr baixam o arquivo
        Assert.Equal(uploadsBefore, m.H.Remote.Uploads);

        // cp -a de um arquivo com xattr (navegadores gravam user.xdg.origin.url) não falha por isso
        var src = Path.Combine(m.H.Dir, "baixado.pdf");
        File.WriteAllText(src, "pdf");
        Assert.Equal(0, Sh($"setfattr -n user.xdg.origin.url -v https://exemplo.com '{src}'", m.H.Dir).code);
        var cp = Sh($"cp -a '{src}' .", m.Root);
        Assert.True(cp.code == 0, "cp -a falhou: " + cp.output);
        // o RELEASE do kernel é assíncrono ao close() do cp (o envio só entra na fila nele): Settle logo depois podia ver
        // a fila vazia e terminar antes (falhou 1× sob a suíte inteira)
        Harness.WaitUntil(() => m.H.Remote.ReadText("/baixado.pdf") == "pdf", "cp -a enviado");
    }

    [Fact]
    public void Append_em_arquivo_so_online_baixa_e_acrescenta()
    {
        RealFuse.Require();
        using var m = new Mounted(h => h.Remote.RemoteCreate("/", "log.txt", "linha 1\n"));
        Assert.Equal(0, Sh("echo 'linha 2' >> log.txt", m.Root).code);
        // O_APPEND também com pwrite num offset qualquer: o Linux ignora o offset e escreve no fim
        Py(m.Root, """
            fd = os.open("log.txt", os.O_WRONLY | os.O_APPEND)
            os.pwrite(fd, b"linha 3\n", 0)
            os.close(fd)
            """);
        m.H.Settle();
        Assert.Equal("linha 1\nlinha 2\nlinha 3\n", m.H.Remote.ReadText("/log.txt"));
        Assert.Equal(1, m.H.Remote.Downloads);
    }

    [Fact]
    public void Pread_parcial_no_fim_e_alem_do_fim()
    {
        RealFuse.Require();
        using var m = new Mounted(h => h.Remote.RemoteCreate("/", "abc.txt", "0123456789"));
        var r = Py(m.Root, """
            fd = os.open("abc.txt", os.O_RDONLY)
            print(os.pread(fd, 4, 3).decode(), os.pread(fd, 100, 7).decode(), repr(os.pread(fd, 10, 10)), repr(os.pread(fd, 10, 500)))
            os.close(fd)
            """);
        Assert.Equal("3456 789 b'' b''", r);
    }

    [Fact]
    public void Mmap_le_arquivo_so_online_e_grava_com_map_shared()
    {
        RealFuse.Require();
        using var m = new Mounted(h =>
        {
            h.Remote.RemoteCreate("/", "leitura.txt", "conteúdo da nuvem");
            h.Remote.RemoteCreate("/", "escrita.bin", "aaaaaaaaaa");
        });
        var r = Py(m.Root, """
            with open("leitura.txt", "rb") as f:
                mm = mmap.mmap(f.fileno(), 0, prot=mmap.PROT_READ)
                print(mm[:].decode()); mm.close()
            with open("escrita.bin", "r+b") as f:
                mm = mmap.mmap(f.fileno(), 0)  # MAP_SHARED, leitura e escrita
                mm[2:5] = b"XYZ"
                mm.flush(); mm.close()
            print(open("escrita.bin", "rb").read().decode())
            """).Split('\n');
        Assert.Equal("conteúdo da nuvem", r[0]);
        Assert.Equal("aaXYZaaaaa", r[1]);
        m.H.Settle();
        Assert.Equal("aaXYZaaaaa", m.H.Remote.ReadText("/escrita.bin"));
    }

    [Theory]
    [InlineData(false)] // libfuse/kernel atuais: direct_io + DIRECT_IO_ALLOW_MMAP
    [InlineData(true)]  // libfuse < 3.17.3 (Ubuntu 24.04, Debian 12): cache de página
    public void Mmap_de_arquivo_grande_em_leitura_por_faixas(bool pageCacheOnly)
    {
        RealFuse.Require();
        FuseHost.ForcePageCacheStreaming = pageCacheOnly;
        try { MmapGrande(pageCacheOnly); }
        finally { FuseHost.ForcePageCacheStreaming = false; }
    }

    static void MmapGrande(bool pageCacheOnly)
    {
        // acima de StreamingThresholdMB: o open responde com direct_io enquanto o arquivo está incompleto
        var big = new byte[12 << 20];
        new Random(7).NextBytes(big);
        using var m = new Mounted(h => h.Remote.RemoteCreateBytes("/", "video.bin", big));
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(big.AsSpan(5 << 20, 4096))).ToLowerInvariant();
        var r = Py(m.Root, """
            import hashlib
            with open("video.bin", "rb") as f:
                try:
                    mm = mmap.mmap(f.fileno(), 0, prot=mmap.PROT_READ)
                    print(hashlib.sha256(mm[5<<20:(5<<20)+4096]).hexdigest()); mm.close()
                except OSError as e:
                    print("mmap:" + errno.errorcode[e.errno])
            """);
        Assert.Equal(expected, r);
        // o mmap funciona nos dois modos (acima); o modo usado depende da libfuse: direct_io se ela negocia o mmap
        Assert.Equal(!pageCacheOnly && FuseHost.LibfuseCanAllowDirectIoMmap, FuseHost.StreamingDirectIo);
    }

    [Fact]
    public void O_excl_em_arquivo_existente_so_online_nao_baixa()
    {
        RealFuse.Require();
        using var m = new Mounted(h => h.Remote.RemoteCreate("/", "existe.txt", "da nuvem"));
        var r = Py(m.Root, """
            def err(f):
                try: f(); return "ok"
                except OSError as e: return errno.errorcode[e.errno]
            print(err(lambda: os.open("existe.txt", os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o644)))
            fd = os.open("novo.txt", os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o644); os.write(fd, b"novo"); os.close(fd)
            print(err(lambda: os.open("novo.txt", os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o644)))
            """).Split('\n');
        Assert.Equal(["EEXIST", "EEXIST"], r);
        Assert.Equal(0, m.H.Remote.Downloads);
        m.H.Settle();
        Assert.Equal("da nuvem", m.H.Remote.ReadText("/existe.txt"));
        Assert.Equal("novo", m.H.Remote.ReadText("/novo.txt"));
    }
}
