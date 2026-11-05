using Tmds.DBus.Protocol;

namespace CraftDriver.App.Services;

/// <summary>
/// Detecta se há quem mostre o ícone de bandeja: o TrayIcon do Avalonia no Linux é um StatusNotifierItem,
/// que só aparece se o serviço org.kde.StatusNotifierWatcher estiver no barramento de sessão E algum host
/// (painel/barra: Plasma, waybar, quickshell, extensão AppIndicator do GNOME…) estiver registrado nele
/// (propriedade IsStatusNotifierHostRegistered). Sem isso, esconder a janela deixaria o app invisível.
/// Especificação: https://www.freedesktop.org/wiki/Specifications/StatusNotifierItem/StatusNotifierWatcher/
/// </summary>
public static class TrayHostProbe
{
    public const string WatcherName = "org.kde.StatusNotifierWatcher";
    public const string WatcherPath = "/StatusNotifierWatcher";

    /// <summary>
    /// true só quando o watcher responde que há host registrado. Sem barramento, sem watcher, erro ou
    /// demora além de <paramref name="timeout"/>: false. Nunca lança.
    /// </summary>
    public static async Task<bool> IsHostAvailableAsync(string? busAddress, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(busAddress)) return false;
        try
        {
            using var conn = new DBusConnection(busAddress);
            return await QueryAsync(conn).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch { return false; }
    }

    static async Task<bool> QueryAsync(DBusConnection conn)
    {
        await conn.ConnectAsync().ConfigureAwait(false);
        MessageBuffer msg;
        using (var w = conn.GetMessageWriter())
        {
            w.WriteMethodCallHeader(WatcherName, WatcherPath, "org.freedesktop.DBus.Properties", "Get", "ss", MessageFlags.None);
            w.WriteString(WatcherName);
            w.WriteString("IsStatusNotifierHostRegistered");
            msg = w.CreateMessage();
        }
        return await conn.CallMethodAsync(msg, static (m, _) => m.GetBodyReader().ReadVariantValue().GetBool(), null).ConfigureAwait(false);
    }

    /// <summary>
    /// Espera um host aparecer por até <paramref name="wait"/> (no início da sessão o painel pode subir depois
    /// do craftDriver). Consulta a cada <paramref name="interval"/>.
    /// </summary>
    public static async Task<bool> WaitForHostAsync(string? busAddress, TimeSpan wait, TimeSpan? interval = null)
    {
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            if (await IsHostAvailableAsync(busAddress, TimeSpan.FromSeconds(2)).ConfigureAwait(false)) return true;
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(interval ?? TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        }
    }
}
