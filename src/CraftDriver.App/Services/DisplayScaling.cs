using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CraftDriver.App.Services;

/// <summary>Monitor do Hyprland: nome da saída (o mesmo que o XWayland expõe no RandR) e escala.</summary>
public sealed record HyprMonitor(string Name, double Scale);

/// <summary>Variáveis a definir antes do AppBuilder (vazio = nada) e uma explicação para o log.</summary>
public sealed record ScalingDecision(IReadOnlyDictionary<string, string> Set, string? Note);

/// <summary>
/// Escala da interface sob XWayland. O backend X11 do Avalonia 12.1 decide a escala nesta ordem
/// (src/Avalonia.X11/Screens/X11Screens.Scaling.cs): AVALONIA_SCREEN_SCALE_FACTORS / AVALONIA_GLOBAL_SCALE_FACTOR
/// / AVALONIA_USE_PHYSICAL_DPI; depois QT_SCREEN_SCALE_FACTORS / QT_SCALE_FACTOR / QT_AUTO_SCREEN_SCALE_FACTOR
/// (salvo AVALONIA_SCREEN_SCALE_IGNORE_QT=1); senão Xft.dpi/96. GDK_SCALE é ignorado.
/// No Hyprland:
/// - xwayland:force_zero_scaling = false (padrão): o XWayland desenha a 1x e o compositor amplia a janela num
///   monitor com escala &gt; 1 → borrado. O app não tem como corrigir (aumentar a própria escala daria escala
///   dupla); só a opção do Hyprland resolve — fica uma nota no log.
/// - force_zero_scaling = true: o XWayland recebe pixels reais e o app precisa escalar sozinho. O Avalonia não
///   conhece a escala do Hyprland (o wiki dele sugere GDK_SCALE, que o Avalonia ignora) → interface minúscula.
///   Aqui definimos AVALONIA_SCREEN_SCALE_FACTORS com a escala de cada monitor.
/// O que o usuário definiu explicitamente (variáveis acima) sempre prevalece. CRAFTDRIVER_AUTO_SCALE=0 desliga.
/// </summary>
public static class DisplayScaling
{
    static readonly string[] AvaloniaVars = ["AVALONIA_SCREEN_SCALE_FACTORS", "AVALONIA_GLOBAL_SCALE_FACTOR", "AVALONIA_USE_PHYSICAL_DPI"];
    static readonly string[] QtVars = ["QT_SCREEN_SCALE_FACTORS", "QT_SCALE_FACTOR", "QT_AUTO_SCREEN_SCALE_FACTOR", "QT_USE_PHYSICAL_DPI"];

    const string ForceZeroHint = "No Hyprland, a interface de apps X11 (como o CraftDriver) fica borrada em monitor com escala; " +
                                 "para nitidez, use \"xwayland { force_zero_scaling = true }\" na configuração do Hyprland " +
                                 "— o CraftDriver então aplica a escala de cada monitor sozinho.";

    /// <param name="env">Leitura de variável de ambiente.</param>
    /// <param name="monitors">Monitores do Hyprland; null = não é Hyprland (ou não respondeu).</param>
    /// <param name="forceZeroScaling">xwayland:force_zero_scaling; null = desconhecido.</param>
    public static ScalingDecision Decide(Func<string, string?> env, IReadOnlyList<HyprMonitor>? monitors, bool? forceZeroScaling)
    {
        var none = new Dictionary<string, string>();
        if (env("CRAFTDRIVER_AUTO_SCALE") == "0") return new(none, null);
        if (string.IsNullOrEmpty(env("WAYLAND_DISPLAY")) || string.IsNullOrEmpty(env("DISPLAY"))) return new(none, null);
        var userVars = AvaloniaVars.Concat(env("AVALONIA_SCREEN_SCALE_IGNORE_QT") == "1" ? [] : QtVars)
            .Where(v => !string.IsNullOrEmpty(env(v))).ToList();
        if (userVars.Count > 0)
            return new(none, $"Escala da interface definida pelo ambiente ({string.Join(", ", userVars)}); ajuste automático desligado.");
        if (monitors is not { Count: > 0 }) return new(none, null);
        if (monitors.All(m => Math.Abs(m.Scale - 1) < 0.001)) return new(none, null);
        if (forceZeroScaling != true) return new(none, ForceZeroHint);
        var factors = string.Join(";", monitors.Select(m => m.Name + "=" + m.Scale.ToString("0.###", CultureInfo.InvariantCulture)));
        return new(new Dictionary<string, string> { ["AVALONIA_SCREEN_SCALE_FACTORS"] = factors },
            $"Escala dos monitores do Hyprland aplicada (force_zero_scaling ativo): AVALONIA_SCREEN_SCALE_FACTORS={factors}");
    }

    /// <summary>Variáveis que esta execução definiu (não vieram do ambiente do usuário).</summary>
    public static IReadOnlyCollection<string> AppliedVariables => Applied.Keys.ToList();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Applied = new();

    /// <summary>Define no processo as variáveis da decisão e lembra quais foram (ver <see cref="AppliedVariables"/>).</summary>
    internal static void SetVariables(ScalingDecision d)
    {
        foreach (var (k, v) in d.Set)
        {
            Environment.SetEnvironmentVariable(k, v);
            Applied[k] = true;
        }
    }

    /// <summary>Consulta o Hyprland (se for ele), decide e define as variáveis no processo. Nunca lança.</summary>
    public static ScalingDecision Apply()
    {
        try
        {
            IReadOnlyList<HyprMonitor>? monitors = null;
            bool? forceZero = null;
            if (HyprSocket() is { } sock)
            {
                monitors = ParseMonitors(HyprRequest(sock, "j/monitors"));
                forceZero = ParseBoolOption(HyprRequest(sock, "j/getoption xwayland:force_zero_scaling"));
            }
            var d = Decide(Environment.GetEnvironmentVariable, monitors, forceZero);
            SetVariables(d);
            return d;
        }
        catch (Exception ex)
        {
            return new(new Dictionary<string, string>(), "Não foi possível consultar a escala do Hyprland: " + ex.Message);
        }
    }

    /// <summary>Soquete de comandos do Hyprland ($XDG_RUNTIME_DIR/hypr/&lt;assinatura&gt;/.socket.sock), se existir.</summary>
    static string? HyprSocket()
    {
        var sig = Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE");
        if (string.IsNullOrEmpty(sig)) return null;
        foreach (var root in new[] { Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), "/tmp" })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var p = Path.Combine(root, "hypr", sig, ".socket.sock");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Um pedido ao soquete do Hyprland (mesmo protocolo do hyprctl: envia o comando, lê até fechar).</summary>
    internal static string? HyprRequest(string socketPath, string command, int timeoutMs = 500)
    {
        using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        s.SendTimeout = s.ReceiveTimeout = timeoutMs;
        var connect = s.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        if (!connect.Wait(timeoutMs)) return null;
        s.Send(Encoding.UTF8.GetBytes(command));
        var buf = new byte[16384];
        using var ms = new MemoryStream();
        int n;
        while ((n = s.Receive(buf)) > 0) ms.Write(buf, 0, n);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    internal static IReadOnlyList<HyprMonitor>? ParseMonitors(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        return doc.RootElement.EnumerateArray()
            .Where(m => m.TryGetProperty("name", out _) && m.TryGetProperty("scale", out _))
            .Select(m => new HyprMonitor(m.GetProperty("name").GetString() ?? "", m.GetProperty("scale").GetDouble()))
            .ToList();
    }

    /// <summary>Resposta de getoption: versões novas dão {"bool": true}, antigas {"int": 1}.</summary>
    internal static bool? ParseBoolOption(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) return null;
        if (r.TryGetProperty("bool", out var b) && b.ValueKind is JsonValueKind.True or JsonValueKind.False) return b.GetBoolean();
        if (r.TryGetProperty("int", out var i) && i.ValueKind == JsonValueKind.Number) return i.GetInt64() != 0;
        return null;
    }
}
