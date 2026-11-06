namespace CraftDriver.App.Services;

/// <summary>
/// Instância única por trava de arquivo (flock, via <see cref="FileShare.None"/> do .NET no Unix). Antes, a
/// instância única era só "alguém responde ao ping?": durante a espera do chaveiro (até 60 s, antes de abrir o
/// socket) uma 2ª execução não achava ninguém, abria outro servidor apagando o socket da 1ª e tentava montar o
/// mesmo ponto. A trava é do kernel: some sozinha se o processo morrer (sem arquivo velho para limpar) e não é
/// herdada por processos filhos (O_CLOEXEC).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    readonly FileStream _lock;
    SingleInstance(FileStream fs) => _lock = fs;

    /// <summary>Pega a trava; null se outra instância (deste usuário/CRAFTDRIVER_HOME) já a tem.</summary>
    public static SingleInstance? TryAcquire(string lockPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        try
        {
            var fs = new FileStream(lockPath, new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            return new SingleInstance(fs);
        }
        catch (IOException) { return null; }
    }

    public void Dispose() => _lock.Dispose();
}
