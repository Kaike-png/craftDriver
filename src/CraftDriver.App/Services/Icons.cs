using Avalonia.Data.Converters;
using Avalonia.Media;

namespace CraftDriver.App.Services;

/// <summary>
/// Ícones vetoriais (desenhos de 24×24 no estilo Material) usados com <c>PathIcon</c>. Antes eram glifos em texto
/// (☁ ✔ ⟳ 📁 🗑…), que dependem de uma fonte de emoji — nos pacotes ela é só "recommends" e, sem ela, viravam
/// quadrados. O ViewModel expõe a chave; o XAML converte com <see cref="ToGeometry"/>.
/// </summary>
public static class Icons
{
    static readonly Dictionary<string, string> Paths = new()
    {
        ["cloud"] = "M19.35 10.04C18.67 6.59 15.64 4 12 4 9.11 4 6.6 5.64 5.35 8.04 2.34 8.36 0 10.91 0 14c0 3.31 2.69 6 6 6h13c2.76 0 5-2.24 5-5 0-2.64-2.05-4.78-4.65-4.96z",
        ["ok"] = "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-2 15l-5-5 1.41-1.41L10 14.17l7.59-7.59L19 8l-9 9z",
        ["sync"] = "M12 4V1L8 5l4 4V6c3.31 0 6 2.69 6 6 0 1.01-.25 1.97-.7 2.8l1.46 1.46C19.54 15.03 20 13.57 20 12c0-4.42-3.58-8-8-8zm0 14c-3.31 0-6-2.69-6-6 0-1.01.25-1.97.7-2.8L5.24 7.74C4.46 8.97 4 10.43 4 12c0 4.42 3.58 8 8 8v3l4-4-4-4v3z",
        ["pause"] = "M6 19h4V5H6v14zm8-14v14h4V5h-4z",
        ["warning"] = "M1 21h22L12 2 1 21zm12-3h-2v-2h2v2zm0-4h-2v-4h2v4z",
        ["error"] = "M12 2C6.47 2 2 6.47 2 12s4.47 10 10 10 10-4.47 10-10S17.53 2 12 2zm5 13.59L15.59 17 12 13.41 8.41 17 7 15.59 10.59 12 7 8.41 8.41 7 12 10.59 15.59 7 17 8.41 13.41 12 17 15.59z",
        ["offline"] = "M19.35 10.04C18.67 6.59 15.64 4 12 4c-1.48 0-2.85.43-4.01 1.17l1.46 1.46C10.21 6.23 11.08 6 12 6c3.04 0 5.5 2.46 5.5 5.5v.5H19c1.66 0 3 1.34 3 3 0 1.13-.64 2.11-1.56 2.62l1.45 1.45C23.16 18.16 24 16.68 24 15c0-2.64-2.05-4.78-4.65-4.96zM3 5.27l2.75 2.74C2.56 8.15 0 10.77 0 14c0 3.31 2.69 6 6 6h11.73l2 2L21 20.73 4.27 4 3 5.27zM7.73 10l8 8H6c-2.21 0-4-1.79-4-4s1.79-4 4-4h1.73z",
        ["folder"] = "M10 4H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z",
        ["file"] = "M14 2H6c-1.1 0-1.99.9-1.99 2L4 20c0 1.1.89 2 1.99 2H18c1.1 0 2-.9 2-2V8l-6-6zm2 16H8v-2h8v2zm0-4H8v-2h8v2zm-3-5V3.5L18.5 9H13z",
        ["delete"] = "M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z",
        ["upload"] = "M4 12l1.41 1.41L11 7.83V20h2V7.83l5.58 5.59L20 12l-8-8-8 8z",
        ["download"] = "M20 12l-1.41-1.41L13 16.17V4h-2v12.17l-5.58-5.59L4 12l8 8 8-8z",
        ["move"] = "M12 4l-1.41 1.41L16.17 11H4v2h12.17l-5.58 5.59L12 20l8-8z",
        ["info"] = "M12 8a4 4 0 1 0 0 8a4 4 0 1 0 0-8z",
    };

    static readonly Dictionary<string, Geometry> Cache = new();

    /// <summary>Desenho da chave (desconhecida = ponto de informação).</summary>
    public static Geometry Get(string? key)
    {
        key = key != null && Paths.ContainsKey(key) ? key : "info";
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var g)) Cache[key] = g = StreamGeometry.Parse(Paths[key]);
            return g;
        }
    }

    public static bool Exists(string key) => Paths.ContainsKey(key);

    public static Geometry Warning => Get("warning");

    /// <summary>Conversor chave → desenho para <c>PathIcon.Data</c>.</summary>
    public static readonly IValueConverter ToGeometry = new FuncValueConverter<string?, Geometry>(Get);
}
