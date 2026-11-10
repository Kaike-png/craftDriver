using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CraftDriver.App.Services;
using CraftDriver.App.ViewModels;
using CraftDriver.App.Views;
using CraftDriver.Tests;

namespace CraftDriver.UiTests;

/// <summary>Rodada 4, frente C: o que sobrou da §18 depois da interface redesenhada.</summary>
[Collection("host-isolado")]
public class Rodada4CUiTests
{
    static AppHost NewHost()
    {
        ShellUi.NewHome();
        var host = Task.Run(() => AppHost.CreateAsync()).GetAwaiter().GetResult();
        CraftDriver.App.App.Host = host;
        host.GraphFactory = _ => new FakeGraph();
        host.Notify = (_, _, _) => { };
        return host;
    }

    /// <summary>
    /// §18 "glifos em texto dependem de fonte": a barra de mensagem ainda fecha com o caractere ✕ (U+2715), que a
    /// Inter embutida não tem — sem fonte de símbolos no sistema vira quadrado. Os demais ícones já são PathIcon.
    /// </summary>
    [AvaloniaFact]
    public void Barra_de_mensagem_fecha_com_icone_vetorial_e_nao_com_glifo()
    {
        var host = NewHost();
        var vm = new MainViewModel(host);
        var w = new MainWindow { DataContext = vm, Width = 920, Height = 640 };
        w.Show();
        vm.SelectedTab = 0;
        vm.Message = "Mensagem qualquer";
        ShellUi.Pump(300);
        try
        {
            var fechar = w.GetVisualDescendants().OfType<Button>()
                .Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Fechar mensagem");
            Assert.True(fechar.IsEffectivelyVisible, "botão de fechar a mensagem visível");
            var textos = fechar.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Length > 0).ToList();
            Assert.True(textos.Count == 0 && fechar.Content is not string,
                $"o botão de fechar usa glifo de fonte em vez de PathIcon: \"{fechar.Content}\" {string.Join(" | ", textos)}");
            Assert.Contains(fechar.GetVisualDescendants().OfType<PathIcon>(), p => p.IsEffectivelyVisible);
        }
        finally
        {
            w.Close();
            vm.Dispose();
        }
    }
}
