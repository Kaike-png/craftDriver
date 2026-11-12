using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CraftDriver.App.Services;

/// <summary>
/// Instância única por trava de arquivo (flock). Antes, a instância única era só "alguém responde ao ping?": durante a
/// espera do chaveiro (até 60 s, antes de abrir o socket) uma 2ª execução não achava ninguém, abria outro servidor
/// apagando o socket da 1ª e tentava montar o mesmo ponto. A trava é do kernel: some sozinha se o processo morrer (sem
/// arquivo velho para limpar) e não é herdada por processos filhos (O_CLOEXEC).
/// <para>
/// Só "trava presa por outro processo" (EWOULDBLOCK) quer dizer outra instância. Disco cheio, sistema só-leitura,
/// E/S ou permissão negada ao criar o arquivo viravam "O CraftDriver já está em execução" com saída 0 e nenhuma
/// instância (ou, no EACCES, exceção não tratada no Main): agora sobem como <see cref="SingleInstanceException"/>.
/// </para>
/// <para>
/// O flock é pedido também por P/Invoke: com <c>DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1</c> o <see cref="FileShare.None"/>
/// do .NET deixa de travar e a instância única sumia em silêncio.
/// </para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    const int EWOULDBLOCK = 11;
    const int LOCK_EX = 2, LOCK_NB = 4;

    readonly FileStream _lock;
    SingleInstance(FileStream fs) => _lock = fs;

    /// <summary>
    /// Pega a trava; null se outra instância já a tem (trava presa = EWOULDBLOCK). Qualquer outra falha ao criar/abrir
    /// o arquivo lança <see cref="SingleInstanceException"/> com o erro real.
    /// </summary>
    public static SingleInstance? TryAcquire(string lockPath)
    {
        FileStream fs;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            fs = new FileStream(lockPath, new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) == EWOULDBLOCK) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SingleInstanceException(lockPath, ex);
        }
        // mesmo descritor: com a trava do .NET ativa é só uma confirmação; desligada pela variável, é a trava
        if (flock(fs.SafeFileHandle.DangerousGetHandle().ToInt32(), LOCK_EX | LOCK_NB) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            fs.Dispose();
            if (errno == EWOULDBLOCK) return null;
            throw new SingleInstanceException(lockPath, new IOException($"flock falhou (errno {errno})", errno));
        }
        return new SingleInstance(fs);
    }

    /// <summary>
    /// Arquivo da trava do ponto de montagem (pelo caminho canônico): duas instâncias com CRAFTDRIVER_HOME diferentes
    /// não podem montar o mesmo ponto nem usar o mesmo cache. Fica fora do RuntimeDir da configuração — no runtime do
    /// usuário ($XDG_RUNTIME_DIR, senão /tmp/craftdriver-USUÁRIO), comum às duas.
    /// </summary>
    public static string MountLockPath(string canonicalMountPoint)
    {
        var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } rt
            ? rt
            : Path.Combine(Path.GetTempPath(), "craftdriver-" + Environment.UserName);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalMountPoint)))[..16];
        return Path.Combine(dir, "craftdriver-mount-" + hash + ".lock");
    }

    public void Dispose() => _lock.Dispose();

    [DllImport("libc", SetLastError = true)]
    static extern int flock(int fd, int operation);
}

/// <summary>Não foi possível criar/abrir o arquivo da trava (disco cheio, só-leitura, permissão, E/S).</summary>
public sealed class SingleInstanceException(string lockPath, Exception inner)
    : IOException($"não foi possível criar a trava de instância única {lockPath}: {inner.Message}", inner)
{
    public string LockPath { get; } = lockPath;
}
