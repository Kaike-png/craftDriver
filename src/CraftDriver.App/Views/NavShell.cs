using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace CraftDriver.App.Views;

/// <summary>
/// Espaços extras do tema de navegação lateral (<c>CdNavTabControl</c>): a janela principal continua sendo um
/// <see cref="TabControl"/> (atalhos, testes e leitores de tela veem abas), mas o modelo desenha as abas numa
/// barra lateral com a marca e o estado no topo, e reserva faixas acima e abaixo do conteúdo para avisos que
/// valem em todas as abas.
/// </summary>
public static class NavShell
{
    /// <summary>Topo da barra lateral, acima das abas (marca, estado, ações rápidas).</summary>
    public static readonly AttachedProperty<object?> SidebarHeaderProperty =
        AvaloniaProperty.RegisterAttached<TabControl, object?>("SidebarHeader", typeof(NavShell));

    /// <summary>Faixa acima do conteúdo da aba (ex.: exclusões em massa aguardando confirmação).</summary>
    public static readonly AttachedProperty<object?> ContentHeaderProperty =
        AvaloniaProperty.RegisterAttached<TabControl, object?>("ContentHeader", typeof(NavShell));

    /// <summary>Faixa abaixo do conteúdo da aba (barra de mensagens).</summary>
    public static readonly AttachedProperty<object?> ContentFooterProperty =
        AvaloniaProperty.RegisterAttached<TabControl, object?>("ContentFooter", typeof(NavShell));

    public static object? GetSidebarHeader(TabControl c) => c.GetValue(SidebarHeaderProperty);
    public static void SetSidebarHeader(TabControl c, object? value) => c.SetValue(SidebarHeaderProperty, value);
    public static object? GetContentHeader(TabControl c) => c.GetValue(ContentHeaderProperty);
    public static void SetContentHeader(TabControl c, object? value) => c.SetValue(ContentHeaderProperty, value);
    public static object? GetContentFooter(TabControl c) => c.GetValue(ContentFooterProperty);
    public static void SetContentFooter(TabControl c, object? value) => c.SetValue(ContentFooterProperty, value);
}

/// <summary>
/// Painel das abas da barra lateral: um <see cref="DockPanel"/> (para Configurações ficar no pé) que também
/// responde às setas, Home e End. Sem <see cref="INavigableContainer"/>, o ItemsControl ignora as setas e as
/// abas só eram alcançáveis com o mouse.
/// </summary>
public sealed class NavStripPanel : DockPanel, INavigableContainer
{
    IInputElement? INavigableContainer.GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        var items = Children.Where(c => c.IsVisible && c.IsEffectivelyEnabled).ToList();
        if (items.Count == 0) return null;
        var i = from is Control c ? items.IndexOf(c) : -1;
        var next = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => items.Count - 1,
            NavigationDirection.Down or NavigationDirection.Right or NavigationDirection.Next => i + 1,
            NavigationDirection.Up or NavigationDirection.Left or NavigationDirection.Previous => i < 0 ? items.Count - 1 : i - 1,
            _ => int.MinValue,
        };
        if (next == int.MinValue) return null;
        if (next < 0 || next >= items.Count)
        {
            if (!wrap) return null;
            next = (next + items.Count) % items.Count;
        }
        return items[next];
    }
}
