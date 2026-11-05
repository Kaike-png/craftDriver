using Avalonia;
using Avalonia.Styling;
using CraftDriver.Core.Config;

namespace CraftDriver.App.Services;

/// <summary>
/// Tema claro/escuro. "system" deixa o Avalonia seguir o portal (org.freedesktop.appearance color-scheme);
/// sem portal (ex.: Hyprland sem xdg-desktop-portal-gtk/kde) o Avalonia cai no claro, e a escolha manual resolve.
/// </summary>
public static class ThemeService
{
    public static readonly string[] Values = ["system", "light", "dark"];

    public static ThemeVariant VariantFor(string? theme) => theme switch
    {
        "light" => ThemeVariant.Light,
        "dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    public static int IndexOf(string? theme) => Math.Max(0, Array.IndexOf(Values, theme ?? "system"));

    public static void Apply(AppConfig cfg)
    {
        if (Application.Current is { } app) app.RequestedThemeVariant = VariantFor(cfg.Theme);
    }
}
