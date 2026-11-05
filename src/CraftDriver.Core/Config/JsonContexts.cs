using System.Text.Json.Serialization;
using CraftDriver.Core.Ipc;

namespace CraftDriver.Core.Config;

/// <summary>
/// JSON gerado em tempo de compilação (o pacote é publicado com trimming, e a serialização por reflexão poderia perder
/// membros). Formato idêntico ao anterior — ver JsonFormatTests.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;

/// <summary>IPC: compacto, porque o protocolo é uma mensagem por linha.</summary>
[JsonSerializable(typeof(IpcRequest))]
[JsonSerializable(typeof(IpcResponse))]
[JsonSerializable(typeof(AddAccountRequest))]
internal sealed partial class IpcJsonContext : JsonSerializerContext;
