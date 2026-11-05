using System.Net.Http;
using System.Net.Sockets;
using CraftDriver.Core.Auth;

namespace CraftDriver.Core.Sync;

/// <summary>Classifica exceções em falha de rede (tenta de novo, \"sem conexão\") ou de disco local.</summary>
public static class Errors
{
    /// <summary>errno de E/S local: disco cheio, cota, só leitura, erro de E/S, permissão, arquivo grande demais, fds.</summary>
    static readonly HashSet<int> LocalErrnos = [28 /*ENOSPC*/, 122 /*EDQUOT*/, 30 /*EROFS*/, 5 /*EIO*/, 13 /*EACCES*/, 27 /*EFBIG*/, 24 /*EMFILE*/, 23 /*ENFILE*/];

    /// <summary>
    /// Erro do disco/sistema de arquivos local (cache, temporários). No .NET para Unix, IOException.HResult
    /// é o errno; respostas HTTP interrompidas chegam como HttpIOException (também IOException) e não contam.
    /// </summary>
    public static bool IsLocalDiskError(Exception ex) => ex switch
    {
        UnauthorizedAccessException => true,
        HttpIOException => false,
        IOException io when io.InnerException is SocketException => false,
        IOException io => LocalErrnos.Contains(io.HResult),
        _ => false,
    };

    public static bool IsDiskFull(Exception ex) => ex is IOException { HResult: 28 or 122 } and not HttpIOException;

    /// <summary>Falha de rede/transporte: sem conexão, timeout, resposta interrompida.</summary>
    public static bool IsNetworkError(Exception ex, CancellationToken ct = default) => ex switch
    {
        HttpRequestException or TimeoutException => true,
        OperationCanceledException => !ct.IsCancellationRequested,
        IOException when IsLocalDiskError(ex) => false,
        IOException => true,
        _ => false,
    };

    /// <summary>errno para devolver ao processo que abriu/leu o arquivo quando o download falhou.</summary>
    public static int ErrnoForDownload(Exception ex) => ex switch
    {
        _ when IsDiskFull(ex) => Vfs.Errno.ENOSPC,
        AuthRequiredException => Vfs.Errno.EACCES,
        _ when IsLocalDiskError(ex) => Vfs.Errno.EIO,
        _ when IsNetworkError(ex) => Vfs.Errno.EHOSTUNREACH, // "No route to host": sem conexão
        _ => Vfs.Errno.EIO,
    };
}
