using System.Diagnostics;

namespace CraftDriver.Core.Auth;

/// <summary>
/// Garante que existe um provedor do Secret Service (org.freedesktop.secrets) na sessão.
/// Fora do Plasma/GNOME (Hyprland, Sway, i3…) ninguém inicia o serviço: no KWallet 6 ele é o
/// <c>ksecretd</c>, que só é ativável pelo nome org.kde.secretservicecompat, e não pelo nome padrão.
/// </summary>
public static class SecretServiceLauncher
{
    public const string SecretsName = "org.freedesktop.secrets";

    /// <summary>Nomes D-Bus que, ao serem ativados, passam a fornecer org.freedesktop.secrets.</summary>
    static readonly string[] Providers =
    [
        SecretsName,                     // gnome-keyring, KeePassXC, ksecretd com arquivo de serviço próprio
        "org.kde.secretservicecompat",   // KWallet 6 (ksecretd)
        "org.kde.kwalletd6",             // KWallet 6 com a integração Secret Service no próprio kwalletd
        "org.kde.kwalletd5",
    ];

    /// <summary>Tenta deixar o Secret Service disponível. Retorna true se ficou disponível.</summary>
    public static async Task<bool> EnsureRunningAsync(TimeSpan timeout, Action<string>? log = null)
    {
        if (await HasOwnerAsync(SecretsName).ConfigureAwait(false)) return true;
        var activatable = await ListActivatableAsync().ConfigureAwait(false);
        foreach (var name in Providers)
        {
            if (!activatable.Contains(name)) continue;
            log?.Invoke($"Iniciando o serviço de chaveiro ({name})");
            await DBusAsync("org.freedesktop.DBus.StartServiceByName", $"string:{name}", "uint32:0").ConfigureAwait(false);
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (await HasOwnerAsync(SecretsName).ConfigureAwait(false)) return true;
                await Task.Delay(250).ConfigureAwait(false);
            }
        }
        return await HasOwnerAsync(SecretsName).ConfigureAwait(false);
    }

    static async Task<bool> HasOwnerAsync(string name)
        => (await DBusAsync("org.freedesktop.DBus.NameHasOwner", $"string:{name}").ConfigureAwait(false))?.Contains("boolean true") == true;

    static async Task<HashSet<string>> ListActivatableAsync()
    {
        var output = await DBusAsync("org.freedesktop.DBus.ListActivatableNames").ConfigureAwait(false) ?? "";
        var set = new HashSet<string>();
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("string \"")) set.Add(t[8..^1]);
        }
        return set;
    }

    /// <summary>Chamada ao barramento da sessão via dbus-send (presente em toda distro com D-Bus).</summary>
    static async Task<string?> DBusAsync(string method, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("dbus-send")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in new[] { "--session", "--print-reply", "--dest=org.freedesktop.DBus", "/org/freedesktop/DBus", method })
                psi.ArgumentList.Add(a);
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return null;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var output = await p.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return p.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null; // sem dbus-send ou sem barramento: segue para o teste do chaveiro
        }
    }
}
