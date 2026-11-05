using Avalonia;
using Avalonia.Controls;
using CraftDriver.Core.Config;

namespace CraftDriver.App.Services;

/// <summary>
/// Lembra tamanho, posição e maximizado da janela principal no config.json.
/// Posição: só é restaurada se a faixa do título (topo da janela) cair numa área útil de alguma tela atual
/// — evita abrir fora da tela depois de desconectar um monitor. No Wayland (inclusive XWayland, que é como o
/// craftDriver roda) quem posiciona é o compositor: o pedido de posição pode ser ignorado (ex.: Hyprland com
/// janela lado a lado), e então só o tamanho/maximizado valem.
/// </summary>
public static class WindowPlacement
{
    /// <summary>Pedaço mínimo do topo da janela (px) que precisa estar visível para aceitar a posição salva.</summary>
    const int MinVisibleWidth = 120, TitleStrip = 32;

    /// <summary>A posição salva ainda cabe em alguma tela? (pixels de tela)</summary>
    public static bool IsPositionVisible(PixelPoint pos, PixelSize size, IEnumerable<PixelRect> workingAreas)
    {
        var strip = new PixelRect(pos.X, pos.Y, Math.Max(1, size.Width), TitleStrip);
        return workingAreas.Any(a =>
        {
            var i = a.Intersect(strip);
            // pelo menos metade da faixa do título visível (dá para agarrar e arrastar), e o título não acima da tela
            return i.Width >= Math.Min(MinVisibleWidth, size.Width) && i.Height >= TitleStrip / 2 && pos.Y >= a.Y - 4;
        });
    }

    /// <summary>Tamanho salvo limitado ao mínimo da janela e à maior tela atual (DIPs).</summary>
    public static Size ClampSize(Size saved, Size min, Size? maxScreen)
    {
        var w = Math.Max(saved.Width, min.Width);
        var h = Math.Max(saved.Height, min.Height);
        if (maxScreen is { } m)
        {
            w = Math.Min(w, Math.Max(min.Width, m.Width));
            h = Math.Min(h, Math.Max(min.Height, m.Height));
        }
        return new Size(w, h);
    }

    /// <summary>Aplica o que foi salvo (antes do Show).</summary>
    public static void Restore(Window w, AppConfig cfg)
    {
        var screens = w.Screens?.All ?? [];
        if (cfg.WindowWidth is > 0 && cfg.WindowHeight is > 0)
        {
            Size? max = screens.Count > 0
                ? screens.Select(s => new Size(s.WorkingArea.Width / s.Scaling, s.WorkingArea.Height / s.Scaling))
                         .OrderByDescending(s => s.Width * s.Height).First()
                : null;
            var size = ClampSize(new Size(cfg.WindowWidth.Value, cfg.WindowHeight.Value), new Size(w.MinWidth, w.MinHeight), max);
            w.Width = size.Width;
            w.Height = size.Height;
        }
        if (cfg.WindowX is { } x && cfg.WindowY is { } y)
        {
            var scaling = screens.Count > 0 ? screens.Max(s => s.Scaling) : 1;
            var px = new PixelSize((int)(w.Width * scaling), (int)(w.Height * scaling));
            if (IsPositionVisible(new PixelPoint(x, y), px, screens.Select(s => s.WorkingArea)))
            {
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.Position = new PixelPoint(x, y);
            }
        }
        if (cfg.WindowMaximized) w.WindowState = WindowState.Maximized;
    }

    /// <summary>Guarda no config (sem gravar o arquivo). <paramref name="normal"/> = último tamanho/posição no estado normal.</summary>
    public static void Store(AppConfig cfg, Size normalSize, PixelPoint? normalPosition, bool maximized)
    {
        if (normalSize.Width > 0 && normalSize.Height > 0)
        {
            cfg.WindowWidth = Math.Round(normalSize.Width);
            cfg.WindowHeight = Math.Round(normalSize.Height);
        }
        if (normalPosition is { } p)
        {
            cfg.WindowX = p.X;
            cfg.WindowY = p.Y;
        }
        cfg.WindowMaximized = maximized;
    }
}
