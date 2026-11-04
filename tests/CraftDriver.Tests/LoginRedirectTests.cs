using CraftDriver.Core.Auth;

namespace CraftDriver.Tests;

public class LoginRedirectTests
{
    [Theory]
    [InlineData("https://login.microsoftonline.com/common/oauth2/nativeclient?code=M.C5_BAY.2.U.abc&state=xyz", true)]
    [InlineData("  https://login.microsoftonline.com/common/oauth2/nativeclient?code=abc&state=1  ", true)]
    [InlineData("https://login.microsoftonline.com/common/oauth2/nativeclient?error=access_denied&error_description=The+user+denied", false)]
    [InlineData("https://account.microsoft.com/?lang=en-US", false)]
    [InlineData("https://login.microsoftonline.com/common/oauth2/nativeclient", false)]
    [InlineData("", false)]
    public void Valida_endereco_colado(string text, bool ok)
    {
        var err = AuthService.ValidatePastedRedirect(text, out var uri);
        Assert.Equal(ok, uri != null);
        Assert.Equal(ok, err == null);
    }

    [Fact]
    public void Erro_da_microsoft_e_mostrado()
    {
        var err = AuthService.ValidatePastedRedirect(
            "https://login.microsoftonline.com/common/oauth2/nativeclient?error=access_denied&error_description=The+user+denied", out _);
        Assert.Contains("The user denied", err);
    }
}
