using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using CraftDriver.Core.Config;

namespace CraftDriver.App.Services;

/// <summary>
/// Tema claro/escuro e paleta de cores do aplicativo. "system" deixa o Avalonia seguir o portal
/// (org.freedesktop.appearance color-scheme); sem portal (ex.: Hyprland sem xdg-desktop-portal-gtk/kde) o Avalonia
/// cai no claro, e a escolha manual resolve.
/// </summary>
public static class ThemeService
{
    public static readonly string[] Values = ["system", "light", "dark"];

    static ResourceDictionary? _activePaletteDict;

    /// <summary>Paleta de cores atualmente aplicada.</summary>
    public static AppColorPalette CurrentPalette { get; private set; } = ColorPalettes.All[0];

    /// <summary>Disparado quando a paleta de cores muda (usado para atualizar bandeja e ícone das janelas).</summary>
    public static event Action<AppColorPalette>? PaletteChanged;

    public static ThemeVariant VariantFor(string? theme) => theme switch
    {
        "light" => ThemeVariant.Light,
        "dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    public static int IndexOf(string? theme) => Math.Max(0, Array.IndexOf(Values, theme ?? "system"));

    public static void Apply(AppConfig cfg)
    {
        var palette = ColorPalettes.Get(cfg.AccentColor);
        if (Application.Current is not { } app)
        {
            CurrentPalette = palette;
            StatusPalette.ApplyPalette(palette);
            return;
        }
        app.RequestedThemeVariant = VariantFor(cfg.Theme);
        ApplyPalette(app, palette);
    }

    public static void ApplyPalette(Application app, AppColorPalette palette)
    {
        CurrentPalette = palette;
        StatusPalette.ApplyPalette(palette);

        if (app.Styles.OfType<FluentTheme>().FirstOrDefault() is { } fluent)
        {
            if (fluent.Palettes.TryGetValue(ThemeVariant.Light, out var light))
            {
                light.Accent = Color.Parse(palette.Light.Accent);
                light.RegionColor = Color.Parse(palette.Light.Bg);
            }
            if (fluent.Palettes.TryGetValue(ThemeVariant.Dark, out var dark))
            {
                dark.Accent = Color.Parse(palette.Dark.Accent);
                dark.RegionColor = Color.Parse(palette.Dark.Bg);
            }
        }

        var next = ColorPalettes.BuildResourceDictionary(palette);
        var merged = app.Resources.MergedDictionaries;
        if (_activePaletteDict != null)
        {
            var idx = merged.IndexOf(_activePaletteDict);
            if (idx >= 0) merged[idx] = next;
            else merged.Add(next);
        }
        else
        {
            merged.Add(next);
        }
        _activePaletteDict = next;

        PaletteChanged?.Invoke(palette);
    }

    /// <summary>
    /// Carrega o ícone de bandeja/janela tingindo os pixels petróleo (#0E8F7E) com a cor da paleta ativa,
    /// preservando antialiasing, nuvem branca e emblemas semânticos (ex.: círculo vermelho de erro).
    /// </summary>
    public static WindowIcon LoadTintedIcon(string name, AppColorPalette? palette = null)
    {
        var p = palette ?? CurrentPalette;
        var uri = new Uri($"avares://CraftDriver.App/Assets/{name}.png");
        if (p.Key == ColorPalettes.DefaultKey || name is "cloud-paused" or "cloud-offline")
            return new WindowIcon(AssetLoader.Open(uri));

        using var srcStream = AssetLoader.Open(uri);
        using var src = new Bitmap(srcStream);
        var size = src.PixelSize;
        var format = src.Format ?? PixelFormat.Bgra8888;
        var alpha = src.AlphaFormat ?? AlphaFormat.Premul;
        var wb = new WriteableBitmap(size, src.Dpi, format, alpha);

        using (var fb = wb.Lock())
        {
            var byteCount = fb.RowBytes * size.Height;
            src.CopyPixels(new PixelRect(size), fb.Address, byteCount, fb.RowBytes);
            var pixels = new byte[byteCount];
            Marshal.Copy(fb.Address, pixels, 0, byteCount);
            RecolorTealPixels(pixels, Color.Parse(p.BrandMarkHex), format == PixelFormat.Rgba8888, alpha == AlphaFormat.Premul);
            Marshal.Copy(pixels, 0, fb.Address, byteCount);
        }
        return new WindowIcon(wb);
    }

    internal static void RecolorTealPixels(byte[] pixels, Color target, bool isRgba, bool isPremul)
    {
        var rIdx = isRgba ? 0 : 2;
        var bIdx = isRgba ? 2 : 0;

        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            var a = pixels[i + 3];
            if (a == 0) continue;

            double r = pixels[i + rIdx];
            double g = pixels[i + 1];
            double b = pixels[i + bIdx];

            if (isPremul && a < 255)
            {
                var invA = 255.0 / a;
                r *= invA;
                g *= invA;
                b *= invA;
            }

            // Só recolorimos a faixa petróleo do ícone base (#0E8F7E: G e B claramente acima de R),
            // preservando branco, cinza e o vermelho do emblema de erro (#C42B1C).
            if (g <= r + 4.0 || b <= r + 4.0) continue;

            var t = Math.Clamp((g - r) / 129.0, 0.0, 1.0);
            var nr = 255.0 * (1.0 - t) + target.R * t;
            var ng = 255.0 * (1.0 - t) + target.G * t;
            var nb = 255.0 * (1.0 - t) + target.B * t;

            if (isPremul && a < 255)
            {
                var scale = a / 255.0;
                nr *= scale;
                ng *= scale;
                nb *= scale;
            }

            pixels[i + rIdx] = (byte)Math.Clamp((int)Math.Round(nr), 0, 255);
            pixels[i + 1] = (byte)Math.Clamp((int)Math.Round(ng), 0, 255);
            pixels[i + bIdx] = (byte)Math.Clamp((int)Math.Round(nb), 0, 255);
        }
    }
}
