using System.Net;
using System.Net.Sockets;
using Microsoft.Identity.Client;

namespace CraftDriver.Core.Auth;

/// <summary>Erro de login explicado para o usuário.</summary>
/// <param name="Kind">Categoria (para testes e decisões da interface).</param>
/// <param name="Message">Texto em pt-BR: o que houve e o que fazer.</param>
/// <param name="Detail">Detalhe técnico (código MSAL/AADSTS e mensagem original), para o painel recolhível.</param>
/// <param name="CanRetry">Faz sentido oferecer "Tentar de novo".</param>
public sealed record LoginError(LoginErrorKind Kind, string Message, string Detail, bool CanRetry = true);

public enum LoginErrorKind
{
    Canceled,
    Declined,
    AdminConsentRequired,
    WrongAccountOrTenant,
    CodeExpired,
    Network,
    LocalPortBusy,
    BrowserNotOpened,
    ServiceUnavailable,
    Other,
}

/// <summary>
/// Traduz as exceções do MSAL (e de rede) que aparecem no login em mensagens em português. Os códigos
/// AADSTS vêm na mensagem da <see cref="MsalServiceException"/> (o ErrorCode é o código OAuth genérico, ex.:
/// invalid_grant/access_denied), então a busca é feita nos dois.
/// Referência dos códigos: https://learn.microsoft.com/entra/identity-platform/reference-error-codes
/// </summary>
public static class LoginErrors
{
    public static LoginError Describe(Exception ex)
    {
        var detail = Detail(ex);
        var msal = Find<MsalException>(ex);
        var code = msal?.ErrorCode ?? "";
        var text = string.Join(" ", Chain(ex).Select(e => e.Message));

        bool Aad(params string[] codes) => codes.Any(c => text.Contains(c, StringComparison.OrdinalIgnoreCase));

        if (ex is OperationCanceledException && msal == null && !Chain(ex).Any(e => e is TimeoutException) || code == MsalError.AuthenticationCanceledError)
            return new(LoginErrorKind.Canceled, "O login foi cancelado antes de terminar.", detail);

        // 90094/90095: a organização exige aprovação do administrador; 65004: o usuário recusou;
        // 65001: falta consentimento (em conta corporativa quase sempre só o administrador pode dar)
        if (Aad("AADSTS90094", "AADSTS90095"))
            return AdminConsent(detail);
        if (Aad("AADSTS65004"))
            return new(LoginErrorKind.Declined,
                "O pedido de acesso do craftDriver foi recusado na página da Microsoft. Para conectar a conta, tente de novo e clique em \"Aceitar\".",
                detail);
        if (Aad("AADSTS65001"))
            return AdminConsent(detail);
        if (Aad("AADSTS50020", "AADSTS50034", "AADSTS500200", "AADSTS50177", "AADSTS700016", "AADSTS90072", "AADSTS50128", "AADSTS50059"))
            return new(LoginErrorKind.WrongAccountOrTenant,
                "Essa conta não pode entrar por esse caminho. Confira se escolheu o tipo certo — conta pessoal (Outlook/Hotmail/Live) " +
                "ou de trabalho/escola (Microsoft 365) — e se o e-mail é da organização certa. Ao tentar de novo, escolha a conta na página da Microsoft.",
                detail);
        if (code is MsalError.CodeExpired or "expired_token" || Aad("AADSTS70020", "AADSTS70019"))
            return new(LoginErrorKind.CodeExpired,
                "O código de login expirou (vale por cerca de 15 minutos). Clique em \"Tentar de novo\" para gerar um código novo.",
                detail);
        if (code == MsalError.AccessDenied || Aad("access_denied"))
            return new(LoginErrorKind.Declined,
                "O acesso foi negado na página da Microsoft (o pedido foi recusado ou a conta não tem permissão). Tente de novo e aceite o pedido de acesso.",
                detail);
        if (code == MsalError.HttpListenerError || IsAddressInUse(ex))
            return new(LoginErrorKind.LocalPortBusy,
                "O craftDriver não conseguiu receber a resposta do navegador numa porta local (http://localhost). " +
                "Outro programa pode estar usando a porta ou um firewall bloqueia. Tente de novo; se continuar, use o método manual (colar o endereço).",
                detail);
        if (code == MsalError.LinuxXdgOpen)
            return new(LoginErrorKind.BrowserNotOpened,
                "Não foi possível abrir o navegador (xdg-open falhou). Use o método manual: copie o endereço da página de login e abra-o no navegador.",
                detail);
        if (IsNetwork(ex) || code == MsalError.RequestTimeout)
            return new(LoginErrorKind.Network,
                "Sem conexão com a Microsoft. Verifique a internet (ou o proxy) e tente de novo.",
                detail);
        if (code == MsalError.ServiceNotAvailable || msal is MsalServiceException { StatusCode: >= 500 })
            return new(LoginErrorKind.ServiceUnavailable,
                "O serviço de login da Microsoft não respondeu agora. Aguarde um pouco e tente de novo.",
                detail);
        return new(LoginErrorKind.Other,
            "A Microsoft não concluiu o login. Tente de novo; se o erro se repetir, veja os detalhes técnicos abaixo.",
            detail);
    }

    static LoginError AdminConsent(string detail) => new(LoginErrorKind.AdminConsentRequired,
        "A sua organização exige que um administrador aprove o craftDriver antes do primeiro uso. " +
        "Peça ao administrador do Microsoft 365 para conceder o consentimento ao aplicativo e tente de novo depois.",
        detail);

    /// <summary>Uma linha para o terminal (<c>craftdriver login</c>).</summary>
    public static string ForConsole(Exception ex)
    {
        var e = Describe(ex);
        return e.Message + Environment.NewLine + "Detalhe técnico: " + e.Detail;
    }

    static string Detail(Exception ex)
    {
        var parts = new List<string>();
        foreach (var e in Chain(ex))
        {
            var head = e is MsalException m ? $"{e.GetType().Name} ({m.ErrorCode})" : e.GetType().Name;
            if (e is MsalServiceException s && s.StatusCode != 0) head += $" HTTP {s.StatusCode}";
            parts.Add($"{head}: {e.Message}");
        }
        return string.Join(Environment.NewLine + "→ ", parts);
    }

    static IEnumerable<Exception> Chain(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException) yield return e;
    }

    static T? Find<T>(Exception ex) where T : Exception => Chain(ex).OfType<T>().FirstOrDefault();

    static bool IsNetwork(Exception ex) =>
        Chain(ex).Any(e => e is HttpRequestException or WebException
            || e is SocketException se && se.SocketErrorCode != SocketError.AddressAlreadyInUse
            || e is TimeoutException);

    static bool IsAddressInUse(Exception ex) =>
        Chain(ex).Any(e => e is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
            || e is HttpListenerException);
}
