using System.Runtime.ExceptionServices;

namespace CraftDriver.Tests;

/// <summary>Utilidades comuns dos testes (xunit.v3).</summary>
static class TestSupport
{
    /// <summary>Cancelamento do xunit (fim do prazo do teste, Ctrl-C no runner).</summary>
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Raiz do repositório (onde está o CraftDriver.slnx), a partir da pasta dos binários de teste.</summary>
    public static string RepoRoot
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "CraftDriver.slnx"))) return d.FullName;
            throw new DirectoryNotFoundException("CraftDriver.slnx não encontrado acima de " + AppContext.BaseDirectory);
        }
    }

    /// <summary>
    /// Espera <paramref name="task"/> sem bloquear a thread por até <paramref name="timeout"/>:
    /// true = terminou (exceção do próprio task é relançada), false = passou do prazo.
    /// Substitui <c>Assert.True(task.Wait(prazo), "...")</c> mantendo a mensagem da asserção.
    /// O resto do teste nunca continua na thread que completou o task: vários testes fazem
    /// <c>entered.TrySetResult()</c> dentro do download/envio falso (TaskCompletionSource sem
    /// RunContinuationsAsynchronously), e a continuação do await roda ali mesmo, de forma síncrona — o teste
    /// passaria a segurar a thread do motor/FUSE (o Ctrl-C/cancelamento nunca chegaria ao <c>Task.Delay</c>
    /// que deveria ser cancelado). <c>Task.Yield</c> devolve essa thread, como o <c>Wait()</c> antigo fazia.
    /// </summary>
    public static async Task<bool> WithinAsync(this Task task, TimeSpan timeout)
    {
        var done = false;
        ExceptionDispatchInfo? error = null;
        try
        {
            await task.WaitAsync(timeout, Ct);
            done = true;
        }
        catch (TimeoutException) when (!task.IsCompleted) { }
        catch (Exception e) { error = ExceptionDispatchInfo.Capture(e); }
        await Task.Yield(); // também na falha: o finally do teste (Engine.Stop, Unmount) não roda na thread do motor
        error?.Throw();
        return done;
    }

    public static Task<bool> WithinAsync(this Task task, int milliseconds) => task.WithinAsync(TimeSpan.FromMilliseconds(milliseconds));
}

/// <summary>Pré-requisito dos testes que montam o FUSE de verdade.</summary>
static class RealFuse
{
    /// <summary>Simula máquina sem FUSE (prova de que o teste vira "Ignorado" em vez de passar calado).</summary>
    public const string DisableVariable = "CRAFTDRIVER_TEST_NO_FUSE";

    public static bool Available =>
        File.Exists("/dev/fuse") && Environment.GetEnvironmentVariable(DisableVariable) != "1";

    /// <summary>Sem /dev/fuse o teste aparece como ignorado (antes: <c>return</c> e contava como aprovado).</summary>
    public static void Require() =>
        Assert.SkipUnless(Available, $"sem /dev/fuse (ou {DisableVariable}=1): teste de montagem FUSE real ignorado");

    [System.Runtime.InteropServices.DllImport("libc")] static extern uint geteuid();

    /// <summary>Testes de permissão (EACCES/EPERM): o root ignora permissões (CAP_DAC_OVERRIDE), ex. em contêiner.</summary>
    public static void RequireNonRoot()
    {
        Require();
        Assert.SkipWhen(geteuid() == 0, "rodando como root: o kernel não aplica as permissões testadas aqui");
    }

    /// <summary>
    /// Garantias do modo direct_io da leitura por faixas (errno real, open que não espera a página de outro leitor).
    /// Com libfuse &lt; 3.17.3 (Ubuntu 24.04, Debian 12) o app usa o cache de página, que não as dá (docs/streaming.md).
    /// </summary>
    public static void RequireDirectIoStreaming()
    {
        Require();
        Assert.SkipUnless(CraftDriver.Fuse.FuseHost.LibfuseCanAllowDirectIoMmap,
            "libfuse < 3.17.3: leitura por faixas pelo cache de página (sem errno real nem open independente; ver docs/streaming.md)");
    }
}
