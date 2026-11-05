using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CraftDriver.Core.Ipc;

public sealed record IpcRequest(string Cmd, string[] Args);
public sealed record IpcResponse(bool Ok, string Text);

/// <summary>
/// Conta recém-autenticada pelo <c>craftdriver login</c>, entregue à instância em execução
/// (comando "add-account", JSON no primeiro argumento). Os tokens já estão no cache compartilhado;
/// só a configuração precisa ser gravada pela instância, que é dona do config.json enquanto roda.
/// </summary>
public sealed record AddAccountRequest(CraftDriver.Core.Config.AccountConfig Account, string DriveId, string? DriveWebUrl);

/// <summary>JSON das mensagens do IPC (uma linha por mensagem).</summary>
public static class IpcJson
{
    static readonly Config.IpcJsonContext Ctx = Config.IpcJsonContext.Default;
    public static string Serialize(IpcRequest r) => JsonSerializer.Serialize(r, Ctx.IpcRequest);
    public static string Serialize(IpcResponse r) => JsonSerializer.Serialize(r, Ctx.IpcResponse);
    public static string Serialize(AddAccountRequest r) => JsonSerializer.Serialize(r, Ctx.AddAccountRequest);
    public static IpcRequest? DeserializeRequest(string s) => JsonSerializer.Deserialize(s, Ctx.IpcRequest);
    public static IpcResponse? DeserializeResponse(string s) => JsonSerializer.Deserialize(s, Ctx.IpcResponse);
    public static AddAccountRequest? DeserializeAddAccount(string s) => JsonSerializer.Deserialize(s, Ctx.AddAccountRequest);
}

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
                    IpcResponse resp;
                    IpcRequest? req = null;
                    try { req = IpcJson.DeserializeRequest(line); }
                    catch (JsonException) { /* abaixo */ }
                    // responder sempre: sem resposta o cliente conclui que o app "não está em execução"
                    if (req is not { Cmd: not null, Args: not null })
                        resp = new IpcResponse(false, "Pedido inválido para o craftDriver (esperado JSON {\"Cmd\":…,\"Args\":[…]}).");
                    else
                    {
                        try { resp = await _handler(req); }
                        catch (Exception ex) { resp = new IpcResponse(false, ex.Message); }
                    }
                    await writer.WriteLineAsync(IpcJson.Serialize(resp));
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
            await writer.WriteLineAsync(IpcJson.Serialize(new IpcRequest(cmd, args)));
            var line = await reader.ReadLineAsync(cts.Token);
            return line == null ? null : IpcJson.DeserializeResponse(line);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
        {
            return null;
        }
    }
}
