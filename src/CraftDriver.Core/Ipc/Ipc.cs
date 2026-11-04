using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CraftDriver.Core.Ipc;

public sealed record IpcRequest(string Cmd, string[] Args);
public sealed record IpcResponse(bool Ok, string Text);

/// <summary>Servidor de comandos via socket Unix (uma linha JSON por requisição).</summary>
public sealed class IpcServer : IDisposable
{
    readonly string _path;
    readonly Func<IpcRequest, Task<IpcResponse>> _handler;
    readonly Socket _sock;
    readonly CancellationTokenSource _cts = new();

    public IpcServer(string path, Func<IpcRequest, Task<IpcResponse>> handler)
    {
        _path = path;
        _handler = handler;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.Delete(path);
        _sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _sock.Bind(new UnixDomainSocketEndPoint(path));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _sock.Listen(8);
        _ = AcceptLoop();
    }

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket client;
            try { client = await _sock.AcceptAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(async () =>
            {
                using var c = client;
                await using var stream = new NetworkStream(c, true);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                try
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null) return;
                    var req = JsonSerializer.Deserialize<IpcRequest>(line) ?? new IpcRequest("", []);
                    IpcResponse resp;
                    try { resp = await _handler(req); }
                    catch (Exception ex) { resp = new IpcResponse(false, ex.Message); }
                    await writer.WriteLineAsync(JsonSerializer.Serialize(resp));
                }
                catch { /* cliente desconectou */ }
            });
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _sock.Dispose();
        try { File.Delete(_path); } catch { }
    }
}

public static class IpcClient
{
    public static async Task<IpcResponse?> SendAsync(string path, string cmd, string[] args, TimeSpan? timeout = null)
    {
        if (!File.Exists(path)) return null;
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await sock.ConnectAsync(new UnixDomainSocketEndPoint(path), cts.Token);
            await using var stream = new NetworkStream(sock, true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new IpcRequest(cmd, args)));
            var line = await reader.ReadLineAsync(cts.Token);
            return line == null ? null : JsonSerializer.Deserialize<IpcResponse>(line);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
        {
            return null;
        }
    }
}
