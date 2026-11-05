using System.Diagnostics;
using System.Net;
using CraftDriver.Core.Graph;
using CraftDriver.Core.State;
using CraftDriver.Core.Sync;
using CraftDriver.Core.Vfs;
using CraftDriver.Fuse;

namespace CraftDriver.Tests;

/// <summary>Regressões da seção 1 do PENDENCIAS.md (perda/corrupção de dados) com a montagem real.</summary>
[Collection("fuse")]
public class FuseDataLossTests
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

    static void WithMount(Harness h, Action<string> body)
    {
        var mnt = Path.Combine(h.Dir, "mnt");
        var loop = FuseHost.MountAsync(h.Fs, mnt);
        try
        {
            var root = Path.Combine(mnt, Harness.Name);
            Harness.WaitUntil(() => Directory.Exists(root), "montagem visível");
            body(root);
        }
        finally
        {
            FuseHost.Unmount();
            Assert.True(loop.Wait(TimeSpan.FromSeconds(15)), "loop do FUSE não terminou após desmontar");
        }
    }

    [Fact]
    public void Fd_de_arquivo_apagado_continua_funcionando()
    {
        RealFuse.Require();
        using var h = new Harness();
        WithMount(h, root =>
        {
            const string py = """
                import os
                fd = os.open('tmp.bin', os.O_RDWR | os.O_CREAT, 0o644)
                os.write(fd, b'abc')
                os.unlink('tmp.bin')
                os.write(fd, b'def')
                os.ftruncate(fd, 5)
                st = os.fstat(fd)
                os.lseek(fd, 0, 0)
                print(os.read(fd, 100).decode(), st.st_size, os.path.exists('tmp.bin'))
                os.close(fd)
                """;
            var (code, output) = Sh($"python3 -c \"{py.Replace("\"", "\\\"")}\"", root);
            Assert.True(code == 0, output);
            Assert.Equal("abcde 5 False", output);
            // diretório aberto e listado normalmente (readdir sem caminho com nullpath_ok)
            Assert.Equal(0, Sh("mkdir -p d/e && touch d/f", root).code);
            Assert.Equal("e\nf", Sh("ls -1 d", root).output);
            Assert.Equal("d", Sh("ls -1", root).output);
        });
        h.Settle();
        Assert.DoesNotContain(h.Remote.ListPaths(), p => p.Contains("tmp.bin"));
    }

    [Fact]
    public void Copia_dentro_da_montagem_usa_copy_file_range()
    {
        RealFuse.Require();
        using var h = new Harness();
        var big = new string('x', 3_000_000) + "fim";
        h.Remote.RemoteCreate("/", "origem.bin", big);
        h.Settle();
        WithMount(h, root =>
        {
            var before = h.Fs.CopyRangeCalls;
            // uma chamada copia tudo (sem o handler, o kernel faria read/write de 128 KB pelo FUSE)
            var (code, output) = Sh("python3 -c \"import os\na=os.open('origem.bin', os.O_RDONLY)\nb=os.open('copia.bin', os.O_WRONLY|os.O_CREAT, 0o644)\nprint(os.copy_file_range(a, b, 10_000_000))\nos.close(a); os.close(b)\"", root);
            Assert.Equal(0, code);
            Assert.Equal(big.Length.ToString(), output);
            Assert.True(h.Fs.CopyRangeCalls > before, "copy_file_range não passou pelo craftDriver");
            Assert.Equal(Sh("sha256sum < origem.bin", root).output, Sh("sha256sum < copia.bin", root).output);

            // com deslocamentos e no meio de um arquivo existente
            // pede 4 bytes a 3 do fim: copia só os 3 que existem, sobrescrevendo a partir da posição 2
            Assert.Equal("3", Sh("printf 'ABCDEFGH' > alvo.txt && python3 -c \"import os\na=os.open('origem.bin', os.O_RDONLY)\nb=os.open('alvo.txt', os.O_WRONLY)\nprint(os.copy_file_range(a, b, 4, " + (big.Length - 3) + ", 2))\"", root).output);
            Assert.Equal("ABfimFGH", Sh("cat alvo.txt", root).output);
        });
        h.Settle();
        Assert.Equal(big, h.Remote.ReadText("/copia.bin"));
        Assert.Equal("ABfimFGH", h.Remote.ReadText("/alvo.txt"));
    }

    [Fact]
    public void Fixar_e_liberar_por_xattr()
    {
        RealFuse.Require();
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "Fotos", null, folder: true);
        h.Remote.RemoteCreate("/Fotos", "a.jpg", "jpeg");
        h.Settle();
        WithMount(h, root =>
        {
            Assert.Contains("user.onedrive.pinned", Sh("getfattr -d -m - Fotos", root).output);
            Assert.Equal("0", Sh("getfattr --only-values -n user.onedrive.pinned Fotos", root).output);

            Assert.Equal(0, Sh("setfattr -n user.onedrive.pinned -v 1 Fotos", root).code);
            Assert.Equal("1", Sh("getfattr --only-values -n user.onedrive.pinned Fotos", root).output);
            Harness.WaitUntil(() => h.Remote.Downloads == 1, "conteúdo da pasta fixada baixado");
            Assert.True(h.Db.IsEffectivelyPinned(h.Fs.Resolve($"/{Harness.Name}/Fotos/a.jpg")!.Value.Item!.Lid));

            // valor inválido e criação exclusiva (o atributo sempre existe) são recusados sem mudar nada
            static string Errno(string py) => $"python3 -c \"import os, errno\ntry:\n  {py}\nexcept OSError as e: print(errno.errorcode[e.errno])\"";
            Assert.Equal("EINVAL", Sh(Errno("os.setxattr('Fotos', 'user.onedrive.pinned', b'talvez')"), root).output);
            Assert.Equal("EEXIST", Sh(Errno("os.setxattr('Fotos', 'user.onedrive.pinned', b'1', os.XATTR_CREATE)"), root).output);
            Assert.Equal("1", Sh("getfattr --only-values -n user.onedrive.pinned Fotos", root).output);
            // outros atributos continuam não suportados
            Assert.NotEqual(0, Sh("setfattr -n user.qualquer -v 1 Fotos", root).code);
            Assert.NotEqual(0, Sh("setfattr -n user.onedrive.status -v Pinned Fotos", root).code);

            // remover o atributo = liberar (como "craftdriver unpin": volta a ser só online)
            Assert.Equal(0, Sh("setfattr -x user.onedrive.pinned Fotos", root).code);
            Assert.Equal("0", Sh("getfattr --only-values -n user.onedrive.pinned Fotos", root).output);
            Assert.False(h.Db.IsEffectivelyPinned(h.Fs.Resolve($"/{Harness.Name}/Fotos/a.jpg")!.Value.Item!.Lid));
            Assert.Equal("OnlineOnly", Sh("getfattr --only-values -n user.onedrive.status Fotos/a.jpg", root).output);

            Assert.Equal(0, Sh("setfattr -n user.onedrive.pinned -v true Fotos/a.jpg && setfattr -n user.onedrive.pinned -v 0 Fotos/a.jpg", root).code);
            Assert.Equal("0", Sh("getfattr --only-values -n user.onedrive.pinned Fotos/a.jpg", root).output);
        });
    }

    [Fact]
    public void Chmod_chown_e_listxattr_com_montagem_real()
    {
        RealFuse.RequireNonRoot();
        using var h = new Harness();
        WithMount(h, root =>
        {
            // umask fixo: o modo do arquivo novo segue o umask de quem cria (002 no su do Ubuntu dava 775)
            Assert.Equal(0, Sh("umask 022; printf '#!/bin/sh\\necho ok\\n' > s.sh && chmod +x s.sh", root).code);
            Assert.Equal("755", Sh("stat -c %a s.sh", root).output);
            Assert.Equal("ok", Sh("./s.sh", root).output);
            Assert.Equal(0, Sh("chmod 600 s.sh", root).code);
            Thread.Sleep(1100); // attr_timeout do kernel
            Assert.Equal("600", Sh("stat -c %a s.sh", root).output);
            Assert.Equal(0, Sh("mkdir -m 700 privada", root).code);
            Assert.Equal("700", Sh("stat -c %a privada", root).output);
            // chown para o próprio usuário funciona (cp -a); para outro, EPERM em vez de sucesso falso
            Assert.Equal(0, Sh("chown \"$(id -u):$(id -g)\" s.sh", root).code);
            Assert.NotEqual(0, Sh("chown 0:0 s.sh", root).code);
            // listxattr em caminho inexistente: ENOENT (antes devolvia lista vazia com sucesso)
            var (code, output) = Sh("python3 -c \"import os; os.listxattr('nao-existe')\"", root);
            Assert.NotEqual(0, code);
            Assert.Contains("FileNotFoundError", output);
        });
    }

    [Fact]
    public void Cp_preservando_data_mantem_mtime_local_e_na_nuvem()
    {
        RealFuse.Require();
        using var h = new Harness();
        var src = Path.Combine(h.Dir, "origem.txt");
        File.WriteAllText(src, "conteúdo antigo");
        File.SetLastWriteTimeUtc(src, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        WithMount(h, root =>
        {
            Assert.Equal(0, Sh($"cp -p '{src}' a.txt && cp -a '{src}' b.txt && install -p -m 644 '{src}' c.txt", root).code);
            foreach (var f in new[] { "a.txt", "b.txt", "c.txt" })
                Assert.Equal("1577934245", Sh($"stat -c %Y {f}", root).output);
            h.Settle();
            foreach (var f in new[] { "a.txt", "b.txt", "c.txt" })
            {
                Assert.Equal(new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero), h.Remote.FindByPath("/" + f)!.Modified);
                Assert.Equal("1577934245", Sh($"stat -c %Y {f}", root).output);
            }
        });
    }
}

/// <summary>Regressões da seção 1 do PENDENCIAS.md sem montagem (VirtualFs + motor + Graph falso).</summary>
public class DataLossTests
{
    static Item Get(Harness h, string rel) => h.Fs.Resolve(h.P(rel))!.Value.Item!;

    [Fact]
    public void Renomear_somente_online_para_nome_local_baixa_antes_de_excluir_da_nuvem()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "relatorio.txt", "conteúdo da nuvem");
        h.Remote.RemoteCreate("/", "Pasta", null, folder: true);
        h.Remote.RemoteCreate("/Pasta", "a.txt", "A");
        h.Remote.RemoteCreate("/Pasta", "b.txt", "B");
        h.Settle();
        Assert.Equal(0, h.Remote.Downloads);

        // "*.tmp" é só local: deixa de sincronizar e sai da nuvem — o conteúdo tem de ficar aqui
        Assert.Equal(0, h.Fs.Rename(h.P("/relatorio.txt"), h.P("/relatorio.tmp"), 0));
        Assert.Equal(0, h.Fs.Rename(h.P("/Pasta"), h.P("/Pasta.tmp"), 0));
        Assert.Equal(3, h.Remote.Downloads);
        Assert.Equal("conteúdo da nuvem", h.ReadFile("/relatorio.tmp"));
        Assert.Equal("A", h.ReadFile("/Pasta.tmp/a.txt"));
        Assert.Equal("B", h.ReadFile("/Pasta.tmp/b.txt"));
        Assert.Equal(ItemStatus.LocalOnly, h.Fs.StatusOf(Get(h, "/relatorio.tmp")));
        h.Settle();
        Assert.Empty(h.Remote.ListPaths());
    }

    [Fact]
    public void Renomear_para_nome_local_offline_falha_sem_alterar_nada()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "relatorio.txt", "conteúdo da nuvem");
        h.Settle();
        h.Remote.OfflineMode = true;
        Assert.Equal(-Errno.EHOSTUNREACH, h.Fs.Rename(h.P("/relatorio.txt"), h.P("/relatorio.tmp"), 0));
        Assert.NotNull(h.Fs.Resolve(h.P("/relatorio.txt")));
        Assert.Null(h.Fs.Resolve(h.P("/relatorio.tmp")));
        Assert.Equal(0, h.Db.PendingOps());
        h.Remote.OfflineMode = false;
        Assert.Equal("conteúdo da nuvem", h.ReadFile("/relatorio.txt"));
    }

    [Fact]
    public void Download_com_hash_divergente_e_refeito()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "dados.bin", "conteúdo íntegro");
        h.Settle();
        h.Remote.CorruptNextDownloads = 1;
        Assert.Equal("conteúdo íntegro", h.ReadFile("/dados.bin"));
        Assert.Equal(2, h.Remote.Downloads);
    }

    [Fact]
    public void Download_sempre_corrompido_nao_vira_cache_atual()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "dados.bin", "conteúdo íntegro");
        h.Settle();
        h.Remote.CorruptNextDownloads = 100;
        Assert.Equal(-Errno.EIO, h.Fs.Open(h.P("/dados.bin"), 0, 0, out _));
        Assert.Equal(SyncEngine.DownloadAttempts, h.Remote.Downloads);
        var it = Get(h, "/dados.bin");
        Assert.Null(it.CachedCTag);
        Assert.False(h.Engine.Cache.Exists(it.DriveId, it.Lid));
        Assert.Contains(h.Engine.Activity.Entries, e => e.Kind == CraftDriver.Core.Sync.ActivityKind.Error && e.Text.Contains("não confere"));
    }

    [Fact]
    public void Erro_permanente_fica_em_problemas_e_pode_ser_refeito()
    {
        using var h = new Harness();
        var fail = true;
        h.Remote.UploadError = name => fail && name == "ruim.txt"
            ? new GraphException(HttpStatusCode.BadRequest, "invalidRequest", "recusado pela nuvem") : null;
        h.WriteFile("/ruim.txt", "x");
        h.WriteFile("/bom.txt", "y");
        h.Settle();

        Assert.Contains("/bom.txt", h.Remote.ListPaths()); // a fila não travou
        var f = Assert.Single(h.Db.Failures());
        Assert.Equal(OpKind.Upload, f.Kind);
        Assert.Equal("ruim.txt", f.Name);
        Assert.Contains("recusado", f.Error);
        Assert.True(Get(h, "/ruim.txt").Dirty); // alteração local preservada
        Assert.Equal(EngineStatus.Error, h.Engine.Status);

        fail = false;
        Assert.True(h.Drive.RetryFailure(f.Id));
        h.Settle();
        Assert.Equal("x", h.Remote.ReadText("/ruim.txt"));
        Assert.Empty(h.Db.Failures());
        Assert.False(Get(h, "/ruim.txt").Dirty);
    }

    [Fact]
    public void Conteudo_local_sumido_nao_vira_arquivo_vazio()
    {
        using var h = new Harness();
        h.Engine.Paused = true;
        h.WriteFile("/novo.txt", "só existe aqui");
        var it = Get(h, "/novo.txt");
        File.Delete(h.Engine.Cache.PathFor(it.DriveId, it.Lid));

        Assert.Equal(-Errno.EIO, h.Fs.Open(h.P("/novo.txt"), 0, 0, out _));
        Assert.False(h.Engine.Cache.Exists(it.DriveId, it.Lid)); // não criou vazio

        h.Engine.Paused = false;
        h.Settle();
        Assert.Empty(h.Remote.ListPaths()); // não enviou arquivo vazio
        Assert.Contains(h.Db.Failures(), f => f.Name == "novo.txt" && f.Error.Contains("sumiu"));
    }

    [Fact]
    public void Limite_do_cache_nao_despeja_arquivo_sendo_aberto()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "alvo.bin", new string('g', 2 * 1024 * 1024));
        h.Settle();
        h.Config.CacheLimitMB = 1; // o próprio arquivo já passa do limite: seria o primeiro despejado

        // o limite do cache roda exatamente entre o fim do download e a abertura do arquivo
        var ran = false;
        h.Engine.Activity.Changed += () =>
        {
            if (!ran && h.Engine.Activity.Entries.FirstOrDefault()?.Text == "Baixado: alvo.bin") { ran = true; h.Engine.EnforceCacheLimit(); }
        };
        Assert.Equal(0, h.Fs.Open(h.P("/alvo.bin"), 0, 0, out var fh));
        var buf = new byte[16];
        Assert.Equal(16, h.Fs.Read(fh, buf, 2 * 1024 * 1024 - 16));
        Assert.All(buf, b => Assert.Equal((byte)'g', b));
        h.Fs.Release(fh);
        Assert.True(ran);

        // fechado, volta a ser despejável
        h.Engine.EnforceCacheLimit();
        Assert.Null(Get(h, "/alvo.bin").CachedCTag);
    }
}
