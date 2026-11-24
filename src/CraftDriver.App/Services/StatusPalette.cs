using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace CraftDriver.App.Services;

/// <summary>
/// Cor do indicador de estado da barra lateral, pela mesma chave de <see cref="Icons"/> que o ViewModel expõe
/// (<c>StatusIcon</c>). O estado "ok" (Atualizado) acompanha a paleta de cores escolhida; os demais mantêm
/// cores semânticas fixas sobre a barra lateral escura.
/// </summary>
public static class StatusPalette
{
    static readonly SolidColorBrush Neutral = new(Color.Parse("#3A625B"));
    static readonly SolidColorBrush Ok = new(Color.Parse("#0E8F7E"));

    static readonly Dictionary<string, IBrush> Brushes = new()
    {
        ["ok"] = Ok,
        ["sync"] = new ImmutableSolidColorBrush(Color.Parse("#2F6FC4")),
        ["pause"] = new ImmutableSolidColorBrush(Color.Parse("#B7791F")),
        ["offline"] = new ImmutableSolidColorBrush(Color.Parse("#5E716D")),
        ["warning"] = new ImmutableSolidColorBrush(Color.Parse("#C8382B")),
        ["error"] = new ImmutableSolidColorBrush(Color.Parse("#C8382B")),
    };

    public static void ApplyPalette(AppColorPalette palette)
    {
        Ok.Color = Color.Parse(palette.BrandMarkHex);
        Neutral.Color = Color.Parse(palette.NavButtonLine);
    }

    public static IBrush Get(string? key) => key != null && Brushes.TryGetValue(key, out var b) ? b : Neutral;

    /// <summary>Conversor chave de estado → pincel do indicador.</summary>
    public static readonly IValueConverter ToBrush = new FuncValueConverter<string?, IBrush>(Get);
}
