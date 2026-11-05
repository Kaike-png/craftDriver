using System.Text.Json;
using CraftDriver.Core.Config;
using CraftDriver.Core.Ipc;
using Xunit;

namespace CraftDriver.Tests;

/// <summary>
/// O pacote é publicado com trimming: o JSON do config.json e do IPC usa geração de código em vez de reflexão.
/// Estes testes garantem que o formato gravado/lido continua idêntico ao da serialização por reflexão de antes
/// (config.json de versões anteriores continua sendo lido e o IPC entre versões continua conversando).
/// </summary>
public class JsonFormatTests
{
    static AppConfig Populated()
    {
        var c = new AppConfig
        {
            ClientId = "00000000-1111-2222-3333-444444444444", MountPoint = "/home/u/OneDrive", CacheDir = "/home/u/.cache/cd",
            Theme = "dark", PollIntervalSeconds = 45, DownloadConcurrency = 6, DownloadBytesPerSecond = 1_000_000,
            UploadBytesPerSecond = 2_000_000, CacheLimitMB = 2048, StreamingThresholdMB = 16, StreamingBlockKB = 1024,
            StartMinimized = true, MassDeleteThreshold = 10, Paused = true, BlockedProcesses = ["baloo_file", "tracker"],
            LocalOnlyPatterns = ["*.tmp", "~$*"], WindowWidth = 1000.5, WindowHeight = 700, WindowX = -20, WindowY = 30,
            WindowMaximized = true, TrayNoticeShown = true,
        };
        c.Accounts.Add(new AccountConfig { Id = "a1", Username = "eu@exemplo.com", TenantId = "t", Type = "business" });
        c.Drives.Add(new DriveConfig
        {
            DriveId = "b!x", AccountId = "a1", DisplayName = "Financeiro – Ações", Kind = "documentLibrary",
            SiteName = "Fin", WebUrl = "https://x.sharepoint.com/sites/fin/Docs", Enabled = false,
        });
        return c;
    }

    [Fact]
    public void Config_grava_o_mesmo_json_que_a_serializacao_por_reflexao()
    {
        var dir = Directory.CreateTempSubdirectory("cd-json-").FullName;
        var path = Path.Combine(dir, "config.json");
        var cfg = Populated();
        cfg.Save(path);
        var expected = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        Assert.Equal(expected, File.ReadAllText(path));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Config_le_json_gravado_por_reflexao_sem_perder_campos()
    {
        var dir = Directory.CreateTempSubdirectory("cd-json-").FullName;
        var path = Path.Combine(dir, "config.json");
        var original = Populated();
        var json = JsonSerializer.Serialize(original, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
        var loaded = AppConfig.Load(path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal(json, JsonSerializer.Serialize(loaded, new JsonSerializerOptions { WriteIndented = true }));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Ipc_usa_o_mesmo_formato_de_linha()
    {
        var req = new IpcRequest("pin", ["/home/u/OneDrive/a b.txt", "ç"]);
        Assert.Equal(JsonSerializer.Serialize(req), IpcJson.Serialize(req));
        Assert.Equal(req.Args, IpcJson.DeserializeRequest(JsonSerializer.Serialize(req))!.Args);
        var resp = new IpcResponse(true, "linha 1\nlinha 2");
        Assert.Equal(JsonSerializer.Serialize(resp), IpcJson.Serialize(resp));
        Assert.Equal(resp, IpcJson.DeserializeResponse(JsonSerializer.Serialize(resp)));
        Assert.DoesNotContain('\n', IpcJson.Serialize(resp)); // protocolo é uma linha por mensagem

        var add = new AddAccountRequest(Populated().Accounts[0], "b!x", "https://x");
        var back = IpcJson.DeserializeAddAccount(JsonSerializer.Serialize(add))!;
        Assert.Equal(JsonSerializer.Serialize(add), IpcJson.Serialize(add));
        Assert.Equal("eu@exemplo.com", back.Account.Username);
        Assert.Equal("b!x", back.DriveId);
    }
}
