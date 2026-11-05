using CraftDriver.Core.Auth;

namespace CraftDriver.Tests;

public class KeyringIdentityTests
{
    [Fact]
    public void Instalacao_normal_mantem_os_atributos_de_sempre()
    {
        // tokens já salvos no chaveiro são achados por Product=onedrive-linux: mudar isso faria todos entrarem de novo
        Assert.Equal("onedrive-linux", AuthService.KeyringIdentity(null).Product);
    }

    [Fact]
    public void Craftdriver_home_usa_outro_item_do_chaveiro()
    {
        var a = AuthService.KeyringIdentity("/tmp/craftdriver-teste-a");
        var b = AuthService.KeyringIdentity("/tmp/craftdriver-teste-b");
        Assert.NotEqual("onedrive-linux", a.Product); // nunca o item da instalação normal
        Assert.NotEqual(a.Product, b.Product);
        Assert.Equal(a, AuthService.KeyringIdentity("/tmp/craftdriver-teste-a/")); // mesmo HOME, mesmo item
        Assert.Contains("/tmp/craftdriver-teste-a", a.Label); // dá para achar e apagar no KWallet/Seahorse
    }
}
