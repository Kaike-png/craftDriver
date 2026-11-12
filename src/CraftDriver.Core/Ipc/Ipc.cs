using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CraftDriver.Core.Ipc;

public sealed record IpcRequest(string Cmd, string[] Args);
/// <param name="Links">
/// share: links criados que a instância NÃO copiou (ela não tem sessão gráfica, ex.: serviço do systemd sem
/// WAYLAND_DISPLAY); o CLI, que roda na sessão do usuário, copia. Null nos demais comandos.
/// </param>
public sealed record IpcResponse(bool Ok, string Text, string[]? Links = null);

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
    Func<IpcRequest, Task<IpcResponse>> _handler;

    /// <summary>Tamanho máximo de um pedido (caracteres da linha JSON).</summary>
    public static int MaxLineChars { get; set; } = 1 << 20;

    /// <summary>Prazo para o cliente mandar a linha do pedido (cliente parado não segura uma tarefa para sempre).</summary>
    public static TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Quem responde aos pedidos. Trocável: o Program abre o servidor antes do AppHost existir (respostas de
    /// "ainda iniciando") e o AppHost assume o mesmo socket ao iniciar.
    /// </summary>
    public Func<IpcRequest, Task<IpcResponse>> Handler
    {
        get => Volatile.Read(ref _handler);
        set => Volatile.Write(ref _handler, value);
    }
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
        // fora do contexto de quem cria (a thread de UI, no AppHost.Start): as continuações do laço não podem
        // depender dela — com a UI ocupada, nenhum comando seria atendido
        _ = Task.Run(AcceptLoop);
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
                    string? line;
                    using (var timeout = new CancellationTokenSource(ReadTimeout))
                    {
                        try { line = await ReadLineLimitedAsync(reader, MaxLineChars, timeout.Token); }
                        catch (OperationCanceledException) { return; } // cliente parado: solta a conexão
                        catch (InvalidDataException)
                        {
                            await writer.WriteLineAsync(IpcJson.Serialize(new IpcResponse(false, $"Pedido grande demais para o CraftDriver (máximo {MaxLineChars} caracteres).")));
                            return;
                        }
                    }
                    if (line == null) return;
                    IpcResponse resp;
                    IpcRequest? req = null;
                    try { req = IpcJson.DeserializeRequest(line); }
                    catch (JsonException) { /* abaixo */ }
                    // responder sempre: sem resposta o cliente conclui que o app "não está em execução"
                    if (req is not { Cmd: not null, Args: not null })
                        resp = new IpcResponse(false, "Pedido inválido para o CraftDriver (esperado JSON {\"Cmd\":…,\"Args\":[…]}).");
                    else
                    {
                        try { resp = await Handler(req); }
                        catch (Exception ex) { resp = new IpcResponse(false, ex.Message); }
                    }
                    await writer.WriteLineAsync(IpcJson.Serialize(resp));
                }
                catch { /* cliente desconectou */ }
            });
        }
    }

    /// <summary>Lê uma linha de até <paramref name="max"/> caracteres; mais que isso lança InvalidDataException.</summary>
    static async Task<string?> ReadLineLimitedAsync(StreamReader reader, int max, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new char[4096];
        while (true)
        {
            // um char por vez seria lento; Peek não é assíncrono: lê blocos e corta na quebra de linha
            // (o protocolo é um pedido por conexão, então o que vier depois do \n é descartado)
            var n = await reader.ReadAsync(buf.AsMemory(), ct);
            if (n == 0) return sb.Length == 0 ? null : sb.ToString();
            var nl = Array.IndexOf(buf, '\n', 0, n);
            sb.Append(buf, 0, nl < 0 ? n : nl);
            if (sb.Length > max) throw new InvalidDataException();
            if (nl >= 0) return sb.ToString().TrimEnd('\r');
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
