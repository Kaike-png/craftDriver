using System.Diagnostics;
using System.Runtime.InteropServices;
using CraftDriver.Fuse;

namespace CraftDriver.Tests;

/// <summary>
/// PENDENCIAS seção 11: desmontar com requisições em voo (open esperando download, read de bloco esperando a rede) não
/// pode usar memória nativa da libfuse já liberada: respostas assíncronas (fuse_reply_* a partir de SlowOps) numa
/// sessão destruída, ou <c>fuse_session_exit</c> numa sessão que o loop já destruiu. O cenário roda num processo
/// filho (o próprio executável de testes rodando só <see cref="UnmountChild"/>): um SIGSEGV/SIGABRT não derruba a
/// suíte e aparece como código de saída.
/// </summary>
[Collection("fuse")]
public class UnmountRaceTests
{
    [Fact]
    public async Task Desmontar_com_open_e_read_em_voo_nao_derruba_o_processo()
    {
        RealFuse.Require();
        const int rounds = 6;
        var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet"));
        var summary = Path.Combine(Path.GetTempPath(), $"craftdriver-unmount-{Guid.NewGuid():N}.txt");
        var psi = new ProcessStartInfo(dotnet,
            ["exec", Path.Combine(AppContext.BaseDirectory, "CraftDriver.Tests.dll"), "-method", UnmountChild.Method, "-explicit", "only"])
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.Environment[UnmountChild.Variable] = summary;
        psi.Environment[UnmountChild.Variable + "_RODADAS"] = rounds.ToString();
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync(TestSupport.Ct);
        var error = p.StandardError.ReadToEndAsync(TestSupport.Ct);
        var exited = await p.WaitForExitAsync(TestSupport.Ct).WithinAsync(TimeSpan.FromSeconds(150));
        if (!exited) Proc.Kill(p);
        var text = (await output.WithinAsync(5000) ? await output : "") + (await error.WithinAsync(5000) ? await error : "");
        Assert.True(exited, "processo filho travou:\n" + text);
        // morte por sinal (SIGSEGV = 139, SIGABRT = 134) ou asserção do cenário (1)
        Assert.True(p.ExitCode == 0, $"processo filho saiu com {p.ExitCode}:\n{text}");
        text = File.ReadAllText(summary);
        File.Delete(summary);
        Assert.StartsWith($"rodadas={rounds} ", text);
    }
}

/// <summary>
/// Cenário do processo filho: N montagens com opens/reads presos na rede, desmontando de três jeitos. Explícito: só
/// roda quando pedido pelo nome (<c>-method ... -explicit only</c>) pelo teste acima, com <see cref="Variable"/>
/// apontando o arquivo de resumo.
/// </summary>
public class UnmountChild
{
    public const string Variable = "CRAFTDRIVER_TEST_UNMOUNT_CHILD";
    public const string Method = "CraftDriver.Tests.UnmountChild.Cenario_do_processo_filho";
    const int KB = 1024;

    [Fact(Explicit = true)]
    public void Cenario_do_processo_filho()
    {
        var summary = Environment.GetEnvironmentVariable(Variable);
        Assert.False(string.IsNullOrEmpty(summary), $"rodar pelo {nameof(UnmountRaceTests)} ({Variable} = arquivo de resumo)");
        var rounds = int.Parse(Environment.GetEnvironmentVariable(Variable + "_RODADAS") ?? "9");
        File.WriteAllText(summary!, Run(rounds));
    }

    const string Script = """
        import os, sys
        path, off = sys.argv[1], int(sys.argv[2])
        print("abrindo", flush=True)
        try:
            fd = os.open(path, os.O_RDONLY)
            n = len(os.pread(fd, 4096, off))
            print("ok", n, flush=True)
        except OSError as e:
            print("erro", e.errno, "no read" if "fd" in dir() else "no open", flush=True)
        """;

    static bool Listed(string mnt) => File.ReadAllLines("/proc/self/mounts").Any(l => l.Split(' ')[1] == mnt);

    static string Run(int rounds)
    {
        using var h = new Harness(c =>
        {
            c.StreamingThresholdMB = 1;
            c.StreamingBlockKB = 256;
        });
        // um arquivo grande por read: com libfuse < 3.17.3 (cache de página, sem direct_io) o readahead do primeiro
        // read cobre o bloco vizinho e o segundo leitor espera a página travada no kernel, sem chegar ao FUSE
        var big = new byte[(rounds + 2) * 256 * KB];
        new Random(5).NextBytes(big);
        h.Remote.RemoteCreateBytes("/", "grande1.bin", big);
        h.Remote.RemoteCreateBytes("/", "grande2.bin", big);
        h.Settle();
        var mnt = Path.Combine(h.Dir, "mnt");
        var results = new Dictionary<string, int>();

        for (var r = 0; r < rounds; r++)
        {
            h.Remote.RemoteCreate("/", $"a{r}.txt", "pequeno a " + r);
            h.Remote.RemoteCreate("/", $"b{r}.txt", "pequeno b " + r);
            h.Settle();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = 0;
            h.Remote.BeforeDownload = async ct =>
            {
                Interlocked.Increment(ref entered);
                await gate.Task.WaitAsync(ct);
            };
            var loop = FuseHost.MountAsync(h.Fs, mnt);
            var root = Path.Combine(mnt, Harness.Name);
            Harness.WaitUntil(() => Directory.Exists(root), "montagem visível");
            var block = 256L * KB;
            (string file, long off)[] work =
            [
                ($"a{r}.txt", 0), ($"b{r}.txt", 0), // open de só-na-nuvem: espera o download em SlowOps
                ("grande1.bin", (r + 1) * block), ("grande2.bin", (r + 1) * block), // read de bloco em SlowOps
            ];
            var children = work.Select(w => Proc.Start(Proc.Python, ["-c", Script, Path.Combine(root, w.file), w.off.ToString()], h.Dir)).ToList();
            Thread? hammer = null;
            var stopHammer = false;
            try
            {
                foreach (var c in children) Assert.True(c.StandardOutput.ReadLine() == "abrindo", "filho não começou");
                Harness.WaitUntil(() => Volatile.Read(ref entered) == work.Length, "todos esperando a rede");

                switch (r % 3)
                {
                    case 0:
                        // Unmount com tudo preso: a sessão só pode ser destruída depois das respostas
                        FuseHost.Unmount();
                        Thread.Sleep(300);
                        Assert.True(!loop.IsCompleted && FuseHost.IsMounted, "sessão destruída com respostas em voo");
                        gate.TrySetResult();
                        break;
                    case 1:
                        // desmontagem externa (lazy) + Unmount repetido enquanto o loop termina e destrói a sessão
                        hammer = new Thread(() =>
                        {
                            while (!Volatile.Read(ref stopHammer)) FuseHost.Unmount();
                        })
                        { IsBackground = true };
                        hammer.Start();
                        Proc.Run("fusermount3", ["-uz", mnt], "/");
                        Thread.Sleep(r * 7 % 40);
                        gate.TrySetResult();
                        break;
                    default:
                        // respostas chegando junto com a desmontagem
                        gate.TrySetResult();
                        FuseHost.Unmount();
                        break;
                }
                Assert.True(loop.Wait(TimeSpan.FromSeconds(20)), "loop do FUSE não terminou");
                Volatile.Write(ref stopHammer, true);
                hammer?.Join();
                Assert.True(!FuseHost.IsMounted, "sessão continua marcada como montada depois do fim do loop");
                FuseHost.Unmount(); // depois do fim do loop: nada a fazer, nunca a sessão liberada
                Harness.WaitUntil(() => !Listed(mnt), "ponto de montagem liberado", 10_000);
                foreach (var (c, w) in children.Zip(work))
                {
                    Assert.True(c.WaitForExit(15_000), "filho preso depois de desmontar");
                    var line = c.StandardOutput.ReadLine() ?? "(nada)";
                    var key = $"modo{r % 3}/{(w.file.StartsWith("grande", StringComparison.Ordinal) ? "bloco" : "open")}/{(line.StartsWith("ok", StringComparison.Ordinal) ? "ok" : line)}";
                    results[key] = results.GetValueOrDefault(key) + 1;
                }
            }
            finally
            {
                Volatile.Write(ref stopHammer, true);
                gate.TrySetResult();
                FuseHost.Unmount();
                loop.Wait(TimeSpan.FromSeconds(20));
                foreach (var c in children) Proc.Kill(c);
            }
        }
        h.Engine.Stop();
        return $"rodadas={rounds} resultados: {string.Join(", ", results.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"))}";
    }
}
