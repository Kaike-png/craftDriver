using System.Net;
using System.Net.Sockets;
using CraftDriver.Core.Auth;
using Microsoft.Identity.Client;

namespace CraftDriver.Tests;

/// <summary>Tradução dos erros do MSAL no login (mensagens reais do Entra ID nos AADSTS).</summary>
public class LoginErrorsTests
{
    public static TheoryData<Exception, LoginErrorKind> Cases => new()
    {
        { new MsalClientException(MsalError.AuthenticationCanceledError, "User canceled authentication."), LoginErrorKind.Canceled },
        { new OperationCanceledException(), LoginErrorKind.Canceled },
        { new MsalServiceException("invalid_grant", "AADSTS65001: The user or administrator has not consented to use the application with ID 'x'."), LoginErrorKind.AdminConsentRequired },
        { new MsalServiceException("invalid_grant", "AADSTS90094: Admin consent is required for the permissions requested by this application."), LoginErrorKind.AdminConsentRequired },
        { new MsalServiceException("access_denied", "AADSTS65004: User declined to consent to access the app."), LoginErrorKind.Declined },
        { new MsalServiceException(MsalError.AccessDenied, "access_denied"), LoginErrorKind.Declined },
        { new MsalServiceException("invalid_request", "AADSTS50020: User account 'maria@outra.com' from identity provider 'live.com' does not exist in tenant 'Contoso'."), LoginErrorKind.WrongAccountOrTenant },
        { new MsalServiceException("invalid_request", "AADSTS500200: User account 'x@outlook.com' is a personal Microsoft account. Personal Microsoft accounts are not supported for this application."), LoginErrorKind.WrongAccountOrTenant },
        { new MsalServiceException(MsalError.CodeExpired, "Verification code expired before contacting the server"), LoginErrorKind.CodeExpired },
        { new MsalServiceException("expired_token", "AADSTS70020: The provided value for the input parameter 'device_code' is not valid. This device code has expired."), LoginErrorKind.CodeExpired },
        { new MsalServiceException(MsalError.ServiceNotAvailable, "Service is unavailable to process the request", new HttpRequestException("Nome ou serviço desconhecido (login.microsoftonline.com:443)", new SocketException((int)SocketError.HostNotFound))), LoginErrorKind.Network },
        { new HttpRequestException("Connection refused"), LoginErrorKind.Network },
        { new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout", new TimeoutException()), LoginErrorKind.Network },
        { new MsalServiceException(MsalError.RequestTimeout, "Request to the endpoint timed out."), LoginErrorKind.Network },
        { new MsalClientException(MsalError.HttpListenerError, "An HttpListenerException occurred while listening on http://localhost:43123/", new HttpListenerException(98, "Address already in use")), LoginErrorKind.LocalPortBusy },
        { new SocketException((int)SocketError.AddressAlreadyInUse), LoginErrorKind.LocalPortBusy },
        { new MsalClientException(MsalError.LinuxXdgOpen, "Unable to open a web page using xdg-open, gnome-open, kfmclient or wslview tools."), LoginErrorKind.BrowserNotOpened },
        { new MsalServiceException("invalid_grant", "AADSTS9002313: Invalid request. Request is malformed or invalid."), LoginErrorKind.Other },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Classifica_o_erro(Exception ex, LoginErrorKind kind)
    {
        var e = LoginErrors.Describe(ex);
        Assert.Equal(kind, e.Kind);
        Assert.False(string.IsNullOrWhiteSpace(e.Message));
        Assert.DoesNotContain("AADSTS", e.Message); // o código fica só no detalhe técnico
        Assert.Contains(ex.Message, e.Detail);
        Assert.True(e.CanRetry);
    }

    [Fact]
    public void Detalhe_traz_codigo_msal_e_cadeia_de_excecoes()
    {
        var ex = new MsalServiceException(MsalError.ServiceNotAvailable, "Service is unavailable", new HttpRequestException("falhou"));
        var e = LoginErrors.Describe(ex);
        Assert.Contains("MsalServiceException (service_not_available)", e.Detail);
        Assert.Contains("HttpRequestException: falhou", e.Detail);
    }

    [Fact]
    public void Consentimento_admin_explica_o_que_fazer_em_portugues()
    {
        var e = LoginErrors.Describe(new MsalServiceException("invalid_grant", "AADSTS65001: The user or administrator has not consented"));
        Assert.Contains("administrador", e.Message);
        Assert.Contains("AADSTS65001", e.Detail);
        Assert.Contains("Detalhe técnico", LoginErrors.ForConsole(new MsalServiceException("invalid_grant", "AADSTS65001: x")));
    }
}
