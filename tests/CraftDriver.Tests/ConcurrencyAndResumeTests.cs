using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CraftDriver.Core.Graph;
using CraftDriver.Core.State;
using CraftDriver.Core.Sync;
using CraftDriver.Core.Vfs;
using CraftDriver.Fuse;

namespace CraftDriver.Tests;

/// <summary>Processos de verdade por cima da montagem (shell, python, cópias de binários).</summary>
static class Proc
{
    /// <summary>Python do sistema (não o de um venv que esteja no PATH).</summary>
    public const string Python = "/usr/bin/python3";

    public static string Sha(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static (int code, string output, string error) Run(string file, IEnumerable<string> args, string cwd, int timeoutMs = 120_000)
    {
        var psi = new ProcessStartInfo(file, args) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            throw new TimeoutException($"{file} não terminou em {timeoutMs} ms");
        }
        p.WaitForExit();
        return (p.ExitCode, output.Result.Trim(), error.Result.Trim());
    }

    public static (int code, string output, string error) Sh(string script, string cwd) => Run("bash", ["-c", script], cwd);

    public static (int code, string output, string error) Py(string script, string cwd, params string[] args) =>
        Run(Python, ["-c", script, .. args], cwd);

    /// <summary>Processo com stdin/stdout ligados ao teste (coordenação passo a passo).</summary>
    public static Process Start(string file, IEnumerable<string> args, string cwd) =>
        Process.Start(new ProcessStartInfo(file, args)
        {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        })!;

    public static async Task<string> LineAsync(Process p, string what, int timeoutMs = 20_000)
    {
        var line = p.StandardOutput.ReadLineAsync(TestSupport.Ct).AsTask();
        Assert.True(await line.WithinAsync(timeoutMs), $"sem resposta do processo: {what}");
        return await line ?? throw new InvalidOperationException($"processo terminou antes de: {what}");
    }

    static int SysOpenat => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 56 : 257;
    static int SysPread => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 67 : 17;

    /// <summary>
    /// Espera o processo (thread principal) ficar parado na syscall — no FUSE, esperando a resposta do VFS. Chamar
    /// depois de o processo avisar que vai entrar nela (senão pega um openat da inicialização do python).
    /// </summary>
    static void WaitInSyscall(Process p, int nr, string what)
    {
        var sw = Stopwatch.StartNew();
        var last = "";
        var seen = 0;
        while (seen < 3) // 3 amostras seguidas: parado lá, não passando
        {
            try { last = File.ReadAllText($"/proc/{p.Id}/syscall").Trim(); }
            catch (IOException) { last = "?"; }
            seen = last.Split(' ')[0] == nr.ToString() ? seen + 1 : 0;
            if (sw.ElapsedMilliseconds > 15_000) throw new TimeoutException($"{what}: processo não ficou na syscall {nr} (última: {last})");
            Thread.Sleep(100);
        }
    }

    public static void WaitInOpen(Process p, string what) => WaitInSyscall(p, SysOpenat, what);
    public static void WaitInPread(Process p, string what) => WaitInSyscall(p, SysPread, what);

    public static void Kill(Process? p)
    {
        if (p == null) return;
        try { p.Kill(true); } catch { }
        p.Dispose();
    }
}

/// <summary>
/// tmpfs pequeno de verdade, sem root: <c>unshare -rm</c> monta no namespace de um processo que fica dormindo, e o
/// teste (fora do namespace) usa o mesmo sistema de arquivos por <c>/proc/PID/root/...</c>.
/// </summary>
sealed class SmallTmpfs : IDisposable
{
    readonly Process _holder;
    readonly string _mountPoint;
    /// <summary>Caminho da raiz do tmpfs visto deste processo.</summary>
    public string Root { get; }

    SmallTmpfs(Process holder, string mountPoint)
    {
        _holder = holder;
        _mountPoint = mountPoint;
        Root = $"/proc/{holder.Id}/root{mountPoint}";
    }

    public static SmallTmpfs Mount(int megabytes)
    {
        var dir = Path.Combine(Path.GetTempPath(), "craftdriver-tmpfs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Process p;
        try
        {
            p = Process.Start(new ProcessStartInfo("unshare",
                ["-rm", "sh", "-c", $"mount -t tmpfs -o size={megabytes}m tmpfs \"$1\" && echo pronto && exec sleep 600", "sh", dir])
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip("sem o comando unshare: teste de disco cheio ignorado");
            throw;
        }
        var ready = p.StandardOutput.ReadLineAsync();
        if (!ready.Wait(10_000) || ready.Result != "pronto")
        {
            Proc.Kill(p);
            Assert.Skip("unshare -rm + mount tmpfs indisponível (user namespaces desligados?): teste de disco cheio ignorado");
        }
        var t = new SmallTmpfs(p, dir);
        Assert.Equal("tmpfs", Proc.Run("stat", ["-f", "-c", "%T", t.Root], "/").output);
        return t;
    }

    /// <summary>Bytes ocupados no tmpfs.</summary>
    public long Used => long.Parse(Proc.Py("import os,sys; s=os.statvfs(sys.argv[1]); print((s.f_blocks-s.f_bfree)*s.f_frsize)", "/", Root).output);

    public void Dispose()
    {
        Proc.Kill(_holder); // o namespace (e o tmpfs) some com o último processo
        try { Directory.Delete(_mountPoint); } catch { }
    }
}

/// <summary>
/// PENDENCIAS seção 10: online-only grande pelo shell, processo bloqueado, opens concorrentes do mesmo online-only e
/// disco do cache cheio (ENOSPC) — tudo com a montagem FUSE de verdade.
/// </summary>
[Collection("fuse")]
public class ConcurrencyAndResumeFuseTests
{
    const int MB = 1024 * 1024;

    static Item ItemAt(Harness h, string rel) => h.Fs.Resolve(h.P(rel))!.Value.Item!;

    static async Task MountedAsync(Harness h, Func<string, Task> body)
    {
        var mnt = Path.Combine(h.Dir, "mnt");
        var loop = FuseHost.MountAsync(h.Fs, mnt);
        try
        {
            var root = Path.Combine(mnt, Harness.Name);
            Harness.WaitUntil(() => Directory.Exists(root), "montagem visível");
            await body(root);
        }
        finally
        {
            h.Engine.Stop();
            FuseHost.Unmount();
            Assert.True(await loop.WithinAsync(TimeSpan.FromSeconds(15)), "loop do FUSE não terminou após desmontar: " + FuseHost.Diagnostico());
        }
    }

    // ---------- 1) online-only grande pelas ferramentas do shell ----------

    [Fact]
    public async Task Online_only_grande_pelo_shell_le_por_faixas_e_cp_completa_o_cache()
    {
        RealFuse.Require();
        using var h = new Harness();
        var video = StreamingTests.Bytes(64 * MB, 21);
        var other = StreamingTests.Bytes(64 * MB, 22);
        h.Remote.RemoteCreateBytes("/", "video.bin", video);
        h.Remote.RemoteCreateBytes("/", "outro.bin", other);
        h.Settle();
        Assert.True(video.Length > h.Config.StreamingThresholdMB * MB, "o arquivo precisa passar do limiar de leitura por faixas");
        var block = h.Config.StreamingBlockKB * 1024;
        var outside = Path.Combine(h.Dir, "fora.bin");

        await MountedAsync(h, async root =>
        {
            // sha256sum de um online-only: confere com a nuvem, cada bloco baixado uma vez, nada inteiro
            var (code, output, error) = Proc.Sh("sha256sum outro.bin", root);
            Assert.True(code == 0, error);
            Assert.Equal(Proc.Sha(other) + "  outro.bin", output);
            Assert.Equal(other.Length, h.Remote.BytesServed);
            var offsets = h.Remote.RangeRequests.Select(r => r.offset).ToList();
            Assert.Equal(other.Length / block, offsets.Count);
            Assert.Equal(offsets.Count, offsets.Distinct().Count());
            Assert.Equal(0, h.Remote.Downloads);
            h.Remote.RangeRequests.Clear();
            var served = h.Remote.BytesServed;

            // dd do meio: só o bloco que contém a faixa
            const long skip = 10_000; // 10.000 × 4096 = 40.960.000 → bloco 9 (de 4 MiB)
            (code, output, error) = Proc.Sh($"dd if=video.bin bs=4096 skip={skip} count=1 status=none | sha256sum", root);
            Assert.True(code == 0, error);
            Assert.Equal(Proc.Sha(video.AsSpan((int)(skip * 4096), 4096)) + "  -", output);
            Assert.Equal([(9L * block, (long)block)], h.Remote.RangeRequests);
            Assert.Equal(block, h.Remote.BytesServed - served);

            // cp para fora da montagem: cópia idêntica, cada bloco uma vez (o 9 não vem de novo), e vira cache
            (code, _, error) = Proc.Sh($"cp video.bin '{outside}'", root);
            Assert.True(code == 0, error);
            Assert.True(video.AsSpan().SequenceEqual(File.ReadAllBytes(outside)), "cópia feita pelo cp difere da nuvem");
            offsets = h.Remote.RangeRequests.Select(r => r.offset).ToList();
            Assert.Equal(video.Length / block, offsets.Count);
            Assert.Equal(offsets.Count, offsets.Distinct().Count());
            Assert.Equal(video.Length, h.Remote.BytesServed - served);
            var lid = ItemAt(h, "/video.bin").Lid;
            Harness.WaitUntil(() => h.Db.Get(lid)!.CachedCTag != null, "promovido ao cache");
            Assert.True(video.AsSpan().SequenceEqual(File.ReadAllBytes(h.Engine.Cache.PathFor(FakeGraph.DriveId, lid))));

            // já é cache: lido de novo sem rede e marcado como disponível localmente
            h.Remote.OfflineMode = true;
            (code, output, error) = Proc.Sh("sha256sum video.bin; getfattr --only-values -n user.onedrive.status video.bin", root);
            Assert.True(code == 0, error);
            Assert.Equal(Proc.Sha(video) + "  video.bin\nAvailable", output.Replace("\r", ""));
            Assert.Equal(video.Length, h.Remote.BytesServed - served);
            await Task.CompletedTask;
        });
    }

    // ---------- 2) processo bloqueado (indexador/miniaturas) ----------

    const string BlockedProbe = """
        import os, sys, errno, threading, ctypes
        root = sys.argv[1]
        def tryopen(p):
            try:
                fd = os.open(p, os.O_RDONLY)
                os.read(fd, 16)
                os.close(fd)
                return "ok"
            except OSError as e:
                return errno.errorcode[e.errno]
        print("comm", open("/proc/self/comm").read().strip())
        for n in sys.argv[2:]:
            p = os.path.join(root, n)
            print(n, tryopen(p), os.stat(p).st_size)
        print("ls", ",".join(sorted(os.listdir(root))))
        res = []
        def worker():
            # threads de indexadores costumam ter outro nome (QThread...): o comm da thread não é o do processo
            ctypes.CDLL(None).prctl(15, b"QThread", 0, 0, 0)
            res.append(open("/proc/thread-self/comm").read().strip() + " " + tryopen(os.path.join(root, sys.argv[2])))
        t = threading.Thread(target=worker)
        t.start()
        t.join()
        print("thread", res[0])
        """;

    [Fact]
    public async Task Processo_bloqueado_recebe_EACCES_sem_baixar_e_continua_listando()
    {
        RealFuse.Require();
        using var h = new Harness();
        const string photo = "conteúdo da foto";
        h.Remote.RemoteCreate("/", "foto.jpg", photo);
        h.Remote.RemoteCreateBytes("/", "video.mkv", StreamingTests.Bytes(16 * MB, 5)); // acima do limiar: leitura por faixas
        h.Remote.RemoteCreate("/", "miniaturas", null, folder: true);
        h.Remote.RemoteCreate("/miniaturas", "thumbnail-capa.jpg", "capa");
        h.Settle();
        Assert.Contains("baloo", h.Config.BlockedProcesses); // lista padrão
        Assert.Contains("tracker-miner", h.Config.BlockedProcesses);

        // executáveis com o nome dos indexadores: o kernel põe o nome do caminho executado em /proc/PID/comm
        var bin = Path.Combine(h.Dir, "bin");
        Directory.CreateDirectory(bin);
        var baloo = Path.Combine(bin, "baloo_file");
        File.CreateSymbolicLink(baloo, Proc.Python);
        var tracker = Path.Combine(bin, "tracker-miner-fs-3");
        File.Copy("/usr/bin/cat", tracker);
        File.SetUnixFileMode(tracker, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.UserWrite);

        await MountedAsync(h, async root =>
        {
            var size = System.Text.Encoding.UTF8.GetByteCount(photo);
            var (code, output, error) = Proc.Run(baloo, ["-c", BlockedProbe, root, "foto.jpg", "video.mkv"], root);
            Assert.True(code == 0, error);
            Assert.Equal(
                $"comm baloo_file\nfoto.jpg EACCES {size}\nvideo.mkv EACCES {16 * MB}\nls foto.jpg,miniaturas,video.mkv\nthread QThread EACCES",
                output);
            (code, _, error) = Proc.Run(tracker, ["foto.jpg"], root);
            Assert.NotEqual(0, code);
            Assert.Equal(0, h.Remote.Downloads);
            Assert.Empty(h.Remote.RangeRequests);
            Assert.Equal(0, h.Remote.BytesServed);
            Assert.Equal("OnlineOnly", Proc.Sh("getfattr --only-values -n user.onedrive.status foto.jpg", root).output);

            // qualquer outro programa baixa normalmente — inclusive arquivo cujo NOME parece um da lista
            Assert.Equal(photo, Proc.Sh("cat foto.jpg", root).output);
            Assert.Equal(1, h.Remote.Downloads);
            (code, output, error) = Proc.Sh("cat miniaturas/thumbnail-capa.jpg", root);
            Assert.True(code == 0, $"cat de um arquivo chamado thumbnail-*.jpg foi bloqueado: {error}");
            Assert.Equal("capa", output);

            // o bloqueio é contra baixar: o que já está no cache o indexador lê
            (code, output, error) = Proc.Run(baloo, ["-c", BlockedProbe, root, "foto.jpg"], root);
            Assert.True(code == 0, error);
            Assert.Contains($"foto.jpg ok {size}", output);
            Assert.Equal(2, h.Remote.Downloads);
            await Task.CompletedTask;
        });
    }

    // ---------- 3) dois open concorrentes do mesmo online-only ----------

    const string Reader = """
        import os, sys, hashlib
        path, off, n = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
        print("abrindo", flush=True)
        fd = os.open(path, os.O_RDONLY)
        print("lendo", flush=True)
        print("lido", hashlib.sha256(os.pread(fd, n, off)).hexdigest(), flush=True)
        sys.stdin.readline()
        print("relido", hashlib.sha256(os.pread(fd, n, off)).hexdigest(), flush=True)
        """;

    const string Writer = """
        import os, sys
        path, off, n = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
        print("abrindo", flush=True)
        fd = os.open(path, os.O_RDWR)
        print("aberto", flush=True)
        sys.stdin.readline()
        data = (b"ESCRITOR-" * (n // 9 + 1))[:n]
        assert os.pwrite(fd, data, off) == n
        os.fsync(fd)
        os.close(fd)
        print("gravado", flush=True)
        """;

    static byte[] Written(byte[] original, int off, int n)
    {
        var expected = original.ToArray();
        var data = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("ESCRITOR-", n / 9 + 1)))[..n];
        data.CopyTo(expected, off);
        return expected;
    }

    [Theory]
    [InlineData(2 * MB, 0, 2 * MB)] // pequeno: baixado inteiro no open
    [InlineData(64 * MB, 40_000_000, 4096)] // grande: leitura por faixas
    public async Task Dois_leitores_simultaneos_baixam_uma_vez(int size, int off, int n)
    {
        RealFuse.RequireDirectIoStreaming(); // errno real / open independente: garantias do modo direct_io
        using var h = new Harness();
        var data = StreamingTests.Bytes(size, 31);
        h.Remote.RemoteCreateBytes("/", "dado.bin", data);
        h.Settle();
        var streaming = size > h.Config.StreamingThresholdMB * MB;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        h.Remote.BeforeDownload = async ct =>
        {
            Interlocked.Increment(ref entered);
            await release.Task.WaitAsync(ct);
        };
        Process? a = null, b = null;
        try
        {
            await MountedAsync(h, async root =>
            {
                var path = Path.Combine(root, "dado.bin");
                a = Proc.Start(Proc.Python, ["-c", Reader, path, off.ToString(), n.ToString()], root);
                Assert.Equal("abrindo", await Proc.LineAsync(a, "leitor A"));
                if (streaming) Assert.Equal("lendo", await Proc.LineAsync(a, "open de A (por faixas, sem rede)"));
                Harness.WaitUntil(() => Volatile.Read(ref entered) == 1, "A pediu o conteúdo");

                b = Proc.Start(Proc.Python, ["-c", Reader, path, off.ToString(), n.ToString()], root);
                Assert.Equal("abrindo", await Proc.LineAsync(b, "leitor B"));
                if (streaming)
                {
                    Assert.Equal("lendo", await Proc.LineAsync(b, "open de B"));
                    Proc.WaitInPread(b, "B esperando o mesmo bloco");
                }
                else Proc.WaitInOpen(b, "B esperando o mesmo download");
                Thread.Sleep(300);
                Assert.Equal(1, Volatile.Read(ref entered)); // B espera o pedido de A em vez de abrir outro

                release.TrySetResult();
                var expected = Proc.Sha(data.AsSpan(off, n));
                foreach (var (p, name) in new[] { (a, "A"), (b, "B") })
                {
                    if (!streaming) Assert.Equal("lendo", await Proc.LineAsync(p, "open de " + name));
                    Assert.Equal("lido " + expected, await Proc.LineAsync(p, "leitura de " + name));
                    p.StandardInput.WriteLine();
                    Assert.Equal("relido " + expected, await Proc.LineAsync(p, "releitura de " + name));
                    Assert.True(p.WaitForExit(10_000), $"leitor {name} não terminou");
                    Assert.Equal(0, p.ExitCode);
                }
                if (streaming)
                {
                    Assert.Equal([(9L * h.Config.StreamingBlockKB * 1024, (long)h.Config.StreamingBlockKB * 1024)], h.Remote.RangeRequests);
                    Assert.Equal(0, h.Remote.Downloads);
                }
                else
                {
                    Assert.Equal(1, h.Remote.Downloads);
                    Assert.Empty(h.Remote.RangeRequests);
                }
            });
        }
        finally
        {
            release.TrySetResult();
            Proc.Kill(a);
            Proc.Kill(b);
        }
    }

    [Theory]
    [InlineData(2 * MB, 100_000, 300_000)]
    [InlineData(64 * MB, 40_000_000, 4096)]
    public async Task Leitor_e_escritor_simultaneos_nao_corrompem_e_o_envio_tem_o_que_o_escritor_gravou(int size, int off, int n)
    {
        RealFuse.Require();
        using var h = new Harness();
        var data = StreamingTests.Bytes(size, 41);
        h.Remote.RemoteCreateBytes("/", "dado.bin", data);
        h.Settle();
        var streaming = size > h.Config.StreamingThresholdMB * MB;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        h.Remote.BeforeDownload = async ct =>
        {
            Interlocked.Increment(ref entered);
            await release.Task.WaitAsync(ct);
        };
        var expected = Written(data, off, n);
        Process? reader = null, writer = null;
        try
        {
            await MountedAsync(h, async root =>
            {
                var path = Path.Combine(root, "dado.bin");
                reader = Proc.Start(Proc.Python, ["-c", Reader, path, off.ToString(), n.ToString()], root);
                Assert.Equal("abrindo", await Proc.LineAsync(reader, "leitor"));
                if (streaming) Assert.Equal("lendo", await Proc.LineAsync(reader, "open do leitor"));
                Harness.WaitUntil(() => Volatile.Read(ref entered) >= 1, "o leitor pediu o conteúdo");

                writer = Proc.Start(Proc.Python, ["-c", Writer, path, off.ToString(), n.ToString()], root);
                Assert.Equal("abrindo", await Proc.LineAsync(writer, "escritor"));
                Proc.WaitInOpen(writer, "escritor esperando o conteúdo");
                if (!streaming)
                {
                    Thread.Sleep(300);
                    Assert.Equal(1, Volatile.Read(ref entered)); // o mesmo download serve os dois
                }

                release.TrySetResult();
                if (!streaming) Assert.Equal("lendo", await Proc.LineAsync(reader, "open do leitor"));
                Assert.Equal("lido " + Proc.Sha(data.AsSpan(off, n)), await Proc.LineAsync(reader, "leitura antes da escrita"));
                Assert.Equal("aberto", await Proc.LineAsync(writer, "open O_RDWR", 60_000));
                writer.StandardInput.WriteLine();
                Assert.Equal("gravado", await Proc.LineAsync(writer, "escrita"));
                Assert.True(writer.WaitForExit(10_000));
                Assert.Equal(0, writer.ExitCode);

                // o fd do leitor (aberto antes) enxerga o que o escritor gravou: é o mesmo arquivo
                reader.StandardInput.WriteLine();
                Assert.Equal("relido " + Proc.Sha(expected.AsSpan(off, n)), await Proc.LineAsync(reader, "releitura"));
                Assert.True(reader.WaitForExit(10_000));

                // o arquivo inteiro pela montagem e o que foi enviado são o conteúdo do escritor
                var (code, output, error) = Proc.Sh("sha256sum dado.bin", root);
                Assert.True(code == 0, error);
                Assert.Equal(Proc.Sha(expected) + "  dado.bin", output);
                h.Settle();
                Assert.True(expected.AsSpan().SequenceEqual(h.Remote.FindByPath("/dado.bin")!.Content), "o envio não tem o conteúdo do escritor");
                Assert.Equal(0, h.Db.FailureCount());
                if (streaming)
                {
                    // o open de escrita completou a sessão de faixas (cada bloco uma vez), sem baixar inteiro
                    var offsets = h.Remote.RangeRequests.Select(r => r.offset).ToList();
                    Assert.Equal(offsets.Count, offsets.Distinct().Count());
                    Assert.Equal(size, h.Remote.BytesServed);
                    Assert.Equal(0, h.Remote.Downloads);
                }
                else Assert.Equal(1, h.Remote.Downloads);
            });
        }
        finally
        {
            release.TrySetResult();
            Proc.Kill(reader);
            Proc.Kill(writer);
        }
    }

    [Fact]
    public async Task Escrita_durante_o_envio_do_mesmo_arquivo_tambem_sobe()
    {
        RealFuse.Require();
        using var h = new Harness();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploads = 0;
        h.Remote.BeforeUpload = async (name, ct) =>
        {
            if (name != "notas.txt" || Interlocked.Increment(ref uploads) > 1) return;
            held.TrySetResult();
            await release.Task.WaitAsync(ct); // o 1º envio fica no meio enquanto o arquivo é editado de novo
        };
        try
        {
            await MountedAsync(h, async root =>
            {
                var (code, _, error) = Proc.Sh("echo versão 1 > notas.txt", root);
                Assert.True(code == 0, error);
                Assert.True(await held.Task.WithinAsync(20_000), "o envio da versão 1 não começou");
                (code, _, error) = Proc.Sh("echo versão 2 > notas.txt", root);
                Assert.True(code == 0, error);
                release.TrySetResult();
                h.Settle();
                Assert.Equal("versão 2\n", h.Remote.ReadText("/notas.txt"));
                Assert.False(h.Db.Get(ItemAt(h, "/notas.txt").Lid)!.Dirty, "ficou com alterações locais sem envio na fila");
            });
        }
        finally { release.TrySetResult(); }
    }

    // ---------- 5) disco do cache cheio (tmpfs pequeno de verdade) ----------

    const string FillNew = """
        import os, sys, errno
        path = sys.argv[1]
        fd = os.open(path, os.O_CREAT | os.O_WRONLY | os.O_EXCL, 0o644)
        chunk = bytes(range(256)) * 4096   # 1 MiB
        total, err = 0, "nenhum"
        try:
            while total < 64 * 1024 * 1024:
                total += os.write(fd, chunk)
        except OSError as e:
            err = errno.errorcode[e.errno]
        try:
            os.close(fd)
            closed = "ok"
        except OSError as e:
            closed = errno.errorcode[e.errno]
        size = os.stat(path).st_size
        with open(path, "rb") as f:
            head = f.read(total)
        good = head == (chunk * (total // len(chunk) + 1))[:total]
        print(err, closed, total > 0, size >= total, good)
        os.unlink(path)
        print("apagado")
        """;

    [Fact]
    public async Task Disco_cheio_ao_escrever_arquivo_novo_da_ENOSPC_e_nao_estraga_nada()
    {
        RealFuse.Require();
        using var tmpfs = SmallTmpfs.Mount(8);
        using var h = new Harness(c => c.CacheDir = Path.Combine(tmpfs.Root, "cache"));
        const string kept = "conteúdo que já estava no cache";
        h.Remote.RemoteCreate("/", "existente.txt", kept);
        h.Settle();

        await MountedAsync(h, async root =>
        {
            Assert.Equal(kept, Proc.Sh("cat existente.txt", root).output);
            var before = tmpfs.Used;

            var (code, output, error) = Proc.Py(FillNew, root, Path.Combine(root, "novo.bin"));
            Assert.True(code == 0, error);
            Assert.Equal("ENOSPC ok True True True\napagado", output); // ENOSPC, não EIO; o que foi gravado está certo
            Harness.WaitUntil(() => tmpfs.Used <= before + 256 * 1024, $"espaço devolvido ao apagar (ocupado: {tmpfs.Used})");

            // nada mais foi afetado: o arquivo do cache, novas gravações e o envio
            Assert.Equal(kept, Proc.Sh("cat existente.txt", root).output);
            Assert.Equal(1, h.Remote.Downloads);
            (code, _, error) = Proc.Sh("echo depois > depois.txt", root);
            Assert.True(code == 0, error);
            h.Settle();
            Assert.Equal("depois\n", h.Remote.ReadText("/depois.txt"));
            Assert.Null(h.Remote.FindByPath("/novo.bin"));
            Assert.Equal(0, h.Db.FailureCount());
            await Task.CompletedTask;
        });
    }

    const string OpenRead = """
        import os, sys, errno
        path, off = sys.argv[1], int(sys.argv[2])
        try:
            fd = os.open(path, os.O_RDONLY)
        except OSError as e:
            print("open", errno.errorcode[e.errno]); sys.exit(0)
        print("open ok", flush=True)
        try:
            print("read", len(os.pread(fd, 4096, off)))
        except OSError as e:
            print("read", errno.errorcode[e.errno])
        os.close(fd)
        """;

    [Fact]
    public async Task Download_que_nao_cabe_no_disco_da_ENOSPC_e_nao_deixa_parcial()
    {
        RealFuse.RequireDirectIoStreaming(); // errno real / open independente: garantias do modo direct_io
        using var tmpfs = SmallTmpfs.Mount(3);
        using var h = new Harness(c =>
        {
            c.CacheDir = Path.Combine(tmpfs.Root, "cache");
            c.StreamingThresholdMB = 0; // primeiro o download inteiro no open
        });
        h.Remote.RemoteCreateBytes("/", "grande.iso", StreamingTests.Bytes(6 * MB, 51));
        h.Remote.RemoteCreate("/", "pequeno.txt", "cabe");
        h.Settle();

        await MountedAsync(h, async root =>
        {
            var before = tmpfs.Used;
            var (code, output, error) = Proc.Py(OpenRead, root, Path.Combine(root, "grande.iso"), "0");
            Assert.True(code == 0, error);
            Assert.Equal("open ENOSPC", output);
            Assert.True(tmpfs.Used <= before + 256 * 1024, $"o download que não coube continua ocupando {tmpfs.Used - before} bytes");
            Assert.Empty(Directory.EnumerateFiles(h.Engine.Cache.PartialDir));
            Assert.Equal("OnlineOnly", Proc.Sh("getfattr --only-values -n user.onedrive.status grande.iso", root).output);

            // o resto continua funcionando
            Assert.Equal("cabe", Proc.Sh("cat pequeno.txt", root).output);

            // leitura por faixas: o bloco (4 MiB) não cabe → o read recebe ENOSPC (não EIO nem zeros)
            h.Config.StreamingThresholdMB = 1;
            (code, output, error) = Proc.Py(OpenRead, root, Path.Combine(root, "grande.iso"), (3 * MB).ToString());
            Assert.True(code == 0, error);
            Assert.Equal("open ok\nread ENOSPC", output);
            Assert.True(tmpfs.Used <= before + 256 * 1024, $"o bloco que não coube continua ocupando {tmpfs.Used - before} bytes");
            Assert.Equal("cabe", Proc.Sh("cat pequeno.txt", root).output);
            await Task.CompletedTask;
        });
    }
}

/// <summary>
/// Retomada de upload em sessão (PENDENCIAS seção 10): o envio grande passa pelo <c>GraphClient</c> real contra o
/// servidor de sessões do FakeGraph (<see cref="FakeGraph.RealUploadSessions"/>); o motor é parado no meio do envio e
/// recriado sobre o mesmo banco e cache, como num reinício do app. Sem FUSE: o que importa aqui é o motor + protocolo.
/// </summary>
[Collection(UploadResumeTests.ChunkSizeCollection)]
public class UploadResumeTests
{
    /// <summary>Envios grandes em sessão (pesados): um de cada vez. O ChunkSize hoje é por instância do GraphClient.</summary>
    public const string ChunkSizeCollection = "GraphClient.ChunkSize";

    const int MB = 1024 * 1024;
    /// <summary>3 partes com o ChunkSize padrão do GraphClient (10 MiB).</summary>
    const int Size = 25 * MB;
    const long Chunk = 320 * 1024 * 32;

    static void Write(VirtualFs fs, string path, byte[] data, bool create)
    {
        ulong fh;
        Assert.Equal(0, create ? fs.Create(path, OpenFlags.RdWr, out fh) : fs.Open(path, OpenFlags.RdWr, 0, out fh));
        for (var off = 0; off < data.Length; off += MB)
            Assert.Equal(Math.Min(MB, data.Length - off), fs.Write(fh, data.AsSpan(off, Math.Min(MB, data.Length - off)), off));
        Assert.Equal(0, fs.Release(fh));
    }

    /// <summary>Envia <paramref name="data"/> e para o motor quando a parte em <paramref name="stopAt"/> chega ao servidor.</summary>
    static async Task<Op> StartAndInterruptAsync(Harness h, byte[] data, long stopAt)
    {
        h.Remote.RealUploadSessions = true;
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.BeforeUploadChunk = async (off, ct) =>
        {
            if (off < stopAt) return;
            held.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct); // só o cancelamento (motor parando) solta
        };
        Write(h.Fs, h.P("/grande.bin"), data, create: true);
        Assert.True(await held.Task.WithinAsync(20_000), "o envio não chegou à parte esperada");
        h.Engine.Stop(); // "fechou o app" no meio do envio
        Harness.WaitUntil(() => !h.Drive.Busy, "envio interrompido");
        h.Remote.BeforeUploadChunk = null;
        var op = h.Db.PeekOp(FakeGraph.DriveId)!;
        Assert.Equal(OpKind.Upload, op.Kind);
        Assert.NotNull(op.UploadUrl);
        Assert.True(File.Exists(op.UploadSnapshot), "a cópia do envio sumiu ao parar o motor");
        Assert.Equal(stopAt, h.Remote.SessionBytesReceived);
        Assert.Equal(0, h.Remote.Uploads);
        return op;
    }

    static SyncEngine Restart(Harness h, out VirtualFs fs)
    {
        var engine = new SyncEngine(h.Config, h.Db, new ContentCache(h.Config.CacheDir), new ActivityLog(), _ => h.Remote);
        fs = new VirtualFs(engine);
        engine.Start();
        return engine;
    }

    static void WaitQueueEmpty(Harness h, SyncEngine engine) =>
        Harness.WaitUntil(() => h.Db.PendingOps() == 0 && engine.GetDrive(FakeGraph.DriveId) is { Busy: false }, "fila vazia após reiniciar", 30_000);

    [Fact]
    public async Task Envio_interrompido_continua_da_sessao_sem_reenviar_partes()
    {
        using var h = new Harness();
        var data = StreamingTests.Bytes(Size, 61);
        var op = await StartAndInterruptAsync(h, data, stopAt: Chunk);

        using var engine = Restart(h, out _);
        WaitQueueEmpty(h, engine);
        engine.Stop();

        Assert.Equal(0, h.Db.FailureCount());
        Assert.True(data.AsSpan().SequenceEqual(h.Remote.FindByPath("/grande.bin")!.Content), "conteúdo final na nuvem difere");
        Assert.Equal(1, h.Remote.SessionsCreated); // a mesma sessão
        Assert.True(h.Remote.SessionStatusQueries >= 1, "retomou sem perguntar ao servidor onde parou");
        Assert.Equal(data.Length, h.Remote.SessionBytesReceived); // nenhum byte aceito foi reenviado
        Assert.Equal([0L, Chunk, Chunk, 2 * Chunk], h.Remote.ChunkOffsets); // a 2ª parte interrompida, depois dela em diante
        Assert.Equal(1, h.Remote.Uploads);
        Assert.Equal(0, h.Db.FailureCount());
        Assert.False(File.Exists(op.UploadSnapshot), "a cópia do envio ficou para trás");
    }

    [Fact]
    public async Task Sessao_expirada_ao_retomar_recomeca_do_zero_sem_erro_permanente()
    {
        using var h = new Harness();
        var data = StreamingTests.Bytes(Size, 62);
        var op = await StartAndInterruptAsync(h, data, stopAt: 2 * Chunk);
        h.Remote.ExpireUploadSessions(); // a URL guardada agora dá 404

        using var engine = Restart(h, out _);
        WaitQueueEmpty(h, engine);
        engine.Stop();

        Assert.Equal(0, h.Db.FailureCount());
        Assert.True(data.AsSpan().SequenceEqual(h.Remote.FindByPath("/grande.bin")!.Content), "conteúdo final na nuvem difere");
        Assert.Equal(2, h.Remote.SessionsCreated);
        Assert.Equal(2 * Chunk + data.Length, h.Remote.SessionBytesReceived); // o que tinha ido se perdeu; o arquivo todo de novo
        Assert.Equal([0L, Chunk, 2 * Chunk, 0L, Chunk, 2 * Chunk], h.Remote.ChunkOffsets); // sessão nova já na 1ª tentativa
        Assert.Equal(1, h.Remote.Uploads);
        Assert.Equal(0, h.Db.FailureCount());
        Assert.False(File.Exists(op.UploadSnapshot));
    }

    [Fact]
    public async Task Arquivo_alterado_antes_de_retomar_envia_o_conteudo_novo_em_sessao_nova()
    {
        using var h = new Harness();
        var data = StreamingTests.Bytes(Size, 63);
        var op = await StartAndInterruptAsync(h, data, stopAt: Chunk);

        // reinicia pausado, o usuário edita o arquivo, depois retoma a sincronização
        h.Config.Paused = true;
        using var engine = Restart(h, out var fs);
        Harness.WaitUntil(() => engine.GetDrive(FakeGraph.DriveId)?.Ready == true, "drive pronta");
        var changed = StreamingTests.Bytes(Size + 123, 64);
        Write(fs, h.P("/grande.bin"), changed, create: false);
        Assert.Equal(1, h.Remote.SessionsCreated);
        var statusBefore = h.Remote.SessionStatusQueries;
        var offsetsBefore = h.Remote.ChunkOffsets.Count;
        engine.Paused = false;
        WaitQueueEmpty(h, engine);
        engine.Stop();

        Assert.Equal(0, h.Db.FailureCount());
        Assert.True(changed.AsSpan().SequenceEqual(h.Remote.FindByPath("/grande.bin")!.Content), "a nuvem não tem o conteúdo novo");
        Assert.Equal(statusBefore, h.Remote.SessionStatusQueries); // a sessão velha nem foi consultada
        Assert.Equal(2, h.Remote.SessionsCreated);
        Assert.Equal(0L, h.Remote.ChunkOffsets[offsetsBefore]); // recomeçou do zero, com a cópia nova
        Assert.Equal(1, h.Remote.Uploads); // a versão velha nunca foi concluída
        Assert.Equal(0, h.Db.FailureCount());
        Assert.False(File.Exists(op.UploadSnapshot), "a cópia da versão velha ficou para trás");
    }
}
