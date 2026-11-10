using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace CraftDriver.App.Services;

/// <summary>
/// Cor do indicador de estado da barra lateral, pela mesma chave de <see cref="Icons"/> que o ViewModel expõe
/// (<c>StatusIcon</c>). A barra lateral é escura nos dois temas, então as cores são fixas.
/// </summary>
public static class StatusPalette
{
    static readonly IBrush Neutral = new ImmutableSolidColorBrush(Color.Parse("#3E5E59"));

    static readonly Dictionary<string, IBrush> Brushes = new()
    {
        ["ok"] = new ImmutableSolidColorBrush(Color.Parse("#12917F")),
        ["sync"] = new ImmutableSolidColorBrush(Color.Parse("#2F6FC4")),
        ["pause"] = new ImmutableSolidColorBrush(Color.Parse("#B7791F")),
        ["offline"] = new ImmutableSolidColorBrush(Color.Parse("#5E716D")),
        ["warning"] = new ImmutableSolidColorBrush(Color.Parse("#C8382B")),
        ["error"] = new ImmutableSolidColorBrush(Color.Parse("#C8382B")),
    };

    public static IBrush Get(string? key) => key != null && Brushes.TryGetValue(key, out var b) ? b : Neutral;

    /// <summary>Conversor chave de estado → pincel do indicador.</summary>
    public static readonly IValueConverter ToBrush = new FuncValueConverter<string?, IBrush>(Get);
}
