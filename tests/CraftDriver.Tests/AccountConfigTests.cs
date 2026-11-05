using System.Text.Json;
using CraftDriver.Core.Config;
using CraftDriver.Core.Ipc;

namespace CraftDriver.Tests;

public class AccountConfigTests
{
    static AccountConfig Work(string id = "a1", string user = "ana@contoso.com") =>
        new() { Id = id, Username = user, TenantId = "t", Type = "business" };

    [Fact]
    public void Upsert_cria_conta_e_drive_principal()
    {
        var cfg = new AppConfig();
        var dc = cfg.UpsertAccount(Work(), "d1", "https://x");
        Assert.NotNull(dc);
        Assert.Equal("OneDrive - contoso", dc.DisplayName);
        Assert.Equal("business", dc.Kind);
        Assert.Equal("a1", dc.AccountId);
        Assert.Single(cfg.Accounts);
        Assert.Single(cfg.Drives);
    }

    [Fact]
    public void Upsert_da_mesma_conta_substitui_sem_duplicar_drive()
    {
        var cfg = new AppConfig();
        cfg.UpsertAccount(Work(), "d1", null);
        cfg.Drives[0].DisplayName = "Renomeada";
        var again = cfg.UpsertAccount(Work(user: "ana.nova@contoso.com"), "d1", null);
        Assert.Null(again);
        Assert.Equal("ana.nova@contoso.com", Assert.Single(cfg.Accounts).Username);
        Assert.Equal("Renomeada", Assert.Single(cfg.Drives).DisplayName);
    }

    [Fact]
    public void Upsert_gera_nome_unico_para_segunda_conta_do_mesmo_dominio()
    {
        var cfg = new AppConfig();
        cfg.UpsertAccount(Work("a1"), "d1", null);
        var dc = cfg.UpsertAccount(Work("a2", "bia@contoso.com"), "d2", null);
        Assert.Equal("OneDrive - contoso (2)", dc!.DisplayName);
    }

    [Fact]
    public void Pedido_add_account_sobrevive_ao_json_do_ipc()
    {
        var req = new AddAccountRequest(new AccountConfig { Id = "x", Username = "u@outlook.com", Type = "personal" }, "d", "https://w");
        var back = JsonSerializer.Deserialize<AddAccountRequest>(JsonSerializer.Serialize(req))!;
        Assert.Equal("x", back.Account.Id);
        Assert.True(back.Account.IsPersonal);
        Assert.Equal("d", back.DriveId);
        Assert.Equal("https://w", back.DriveWebUrl);
    }
}
