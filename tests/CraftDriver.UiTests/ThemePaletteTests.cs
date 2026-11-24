using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using CraftDriver.App.Services;
using CraftDriver.App.ViewModels;
using CraftDriver.App.Views;
using CraftDriver.Core.Config;
using CraftDriver.Tests;

namespace CraftDriver.UiTests;

[Collection("host-isolado")]
public class ThemePaletteTests
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

    static double RelativeLuminance(string hex)
    {
        var c = Color.Parse(hex);
        static double Linear(byte channel)
        {
            var s = channel / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    static double ContrastRatio(string hex1, string hex2)
    {
        var l1 = RelativeLuminance(hex1);
        var l2 = RelativeLuminance(hex2);
        var lighter = Math.Max(l1, l2);
        var darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    [Fact]
    public void Todas_as_paletas_atendem_contraste_wcag_aa_no_claro_escuro_e_barra_lateral()
    {
        Assert.True(ColorPalettes.All.Count >= 9);
        Assert.Equal(ColorPalettes.All.Count, ColorPalettes.All.Select(p => p.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var p in ColorPalettes.All)
        {
            // Barra lateral (escura nos dois temas): texto branco deve ter contraste >= 7:1 (AAA)
            Assert.True(ContrastRatio("#FFFFFF", p.NavBg) >= 7.0, $"{p.Key}: NavText sobre NavBg");
            Assert.True(ContrastRatio("#FFFFFF", p.NavSelected) >= 7.0, $"{p.Key}: NavText sobre NavSelected");
            Assert.True(ContrastRatio(p.NavLabel, p.NavBg) >= 4.5, $"{p.Key}: NavLabel sobre NavBg");
            Assert.True(ContrastRatio(p.NavIndicator, p.NavBg) >= 4.5, $"{p.Key}: NavIndicator sobre NavBg");

            // Modo Claro e Modo Escuro
            foreach (var (modeName, m) in new[] { ("Light", p.Light), ("Dark", p.Dark) })
            {
                Assert.True(ContrastRatio(m.Text, m.Bg) >= 4.5, $"{p.Key}/{modeName}: Text sobre Bg");
                Assert.True(ContrastRatio(m.Text, m.Surface) >= 4.5, $"{p.Key}/{modeName}: Text sobre Surface");
                Assert.True(ContrastRatio(m.TextMuted, m.Surface) >= 4.5, $"{p.Key}/{modeName}: TextMuted sobre Surface");
                Assert.True(ContrastRatio(m.AccentBtnFg, m.AccentBtnBg) >= 4.5, $"{p.Key}/{modeName}: AccentBtnFg sobre AccentBtnBg");
                Assert.True(ContrastRatio(m.AccentText, m.Surface) >= 4.5, $"{p.Key}/{modeName}: AccentText sobre Surface");
                Assert.True(ContrastRatio(m.AccentText, m.AccentSoft) >= 4.5, $"{p.Key}/{modeName}: AccentText sobre AccentSoft");
            }
        }
    }

    [Fact]
    public void Paleta_desconhecida_ou_nula_cai_no_padrao_teal()
    {
        Assert.Equal("teal", ColorPalettes.Get(null).Key);
        Assert.Equal("teal", ColorPalettes.Get("").Key);
        Assert.Equal("teal", ColorPalettes.Get("cor-que-nao-existe").Key);
        Assert.Equal("blue", ColorPalettes.Get("BLUE").Key);
        Assert.Equal(0, ColorPalettes.IndexOf("desconhecida"));
    }

    [AvaloniaFact]
    public void Paleta_de_cores_aplica_na_hora_salva_e_atualiza_recursos_dinamicos()
    {
        var host = NewHost();
        var vm = new MainViewModel(host);
        var app = Application.Current!;
        try
        {
            Assert.Equal("teal", vm.AccentColor);
            Assert.Equal(ColorPalettes.All.Count, vm.ColorOptions.Count);
            Assert.Single(vm.ColorOptions, o => o.IsSelected && o.Key == "teal");

            foreach (var key in new[] { "blue", "purple", "red", "orange", "pink" })
            {
                var expected = ColorPalettes.Get(key);
                vm.SelectAccentColor(key);

                Assert.Equal(key, vm.AccentColor);
                Assert.Equal(key, AppConfig.Load().AccentColor);
                Assert.Single(vm.ColorOptions, o => o.IsSelected && o.Key == key);

                // Recursos da barra lateral e BrandMark atualizados
                Assert.True(app.TryFindResource("CdNavBg", ThemeVariant.Light, out var navBgObj));
                Assert.Equal(Color.Parse(expected.NavBg), ((SolidColorBrush)navBgObj!).Color);

                Assert.True(app.TryFindResource("CdBrandMarkBrush", ThemeVariant.Light, out var brandObj));
                Assert.Equal(Color.Parse(expected.BrandMarkHex), ((SolidColorBrush)brandObj!).Color);

                // Recursos dependentes de tema (Claro e Escuro) atualizados
                Assert.True(app.TryFindResource("CdAccentText", ThemeVariant.Light, out var lightAccent));
                Assert.Equal(Color.Parse(expected.Light.AccentText), ((SolidColorBrush)lightAccent!).Color);

                Assert.True(app.TryFindResource("CdAccentText", ThemeVariant.Dark, out var darkAccent));
                Assert.Equal(Color.Parse(expected.Dark.AccentText), ((SolidColorBrush)darkAccent!).Color);

                // FluentTheme.Palettes sincronizado
                var fluent = app.Styles.OfType<FluentTheme>().First();
                Assert.Equal(Color.Parse(expected.Light.Accent), fluent.Palettes[ThemeVariant.Light].Accent);
                Assert.Equal(Color.Parse(expected.Dark.Accent), fluent.Palettes[ThemeVariant.Dark].Accent);

                // Indicador de estado "Atualizado" (ok) acompanha a paleta; "error" permanece semântico
                var okBrush = Assert.IsType<SolidColorBrush>(StatusPalette.Get("ok"));
                Assert.Equal(Color.Parse(expected.BrandMarkHex), okBrush.Color);
                var errBrush = Assert.IsAssignableFrom<ISolidColorBrush>(StatusPalette.Get("error"));
                Assert.Equal(Color.Parse("#C8382B"), errBrush.Color);
            }
        }
        finally
        {
            vm.SelectAccentColor(ColorPalettes.DefaultKey);
            vm.ThemeIndex = 0;
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void Icone_da_bandeja_recolore_apenas_petroleo_e_preserva_branco_e_erro()
    {
        var red = ColorPalettes.Get("red");
        var target = Color.Parse(red.BrandMarkHex);

        // BGRA8888: 1) petróleo puro (#0E8F7E), 2) branco (#FFFFFF), 3) vermelho de erro (#C42B1C), 4) transparente
        byte[] pixels =
        [
            0x7E, 0x8F, 0x0E, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF,
            0x1C, 0x2B, 0xC4, 0xFF,
            0x00, 0x00, 0x00, 0x00,
        ];

        ThemeService.RecolorTealPixels(pixels, target, isRgba: false, isPremul: true);

        // Pixel 1: petróleo -> cor da paleta vermelha
        Assert.Equal(target.B, pixels[0]);
        Assert.Equal(target.G, pixels[1]);
        Assert.Equal(target.R, pixels[2]);
        Assert.Equal(0xFF, pixels[3]);

        // Pixel 2: branco intacto
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, pixels[4..8]);

        // Pixel 3: vermelho de erro intacto
        Assert.Equal(new byte[] { 0x1C, 0x2B, 0xC4, 0xFF }, pixels[8..12]);

        // Pixel 4: transparente intacto
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, pixels[12..16]);

        // Carrega os ícones reais da bandeja sem erro para todas as variações
        foreach (var iconName in new[] { "cloud", "cloud-sync", "cloud-error", "cloud-paused", "cloud-offline" })
            Assert.NotNull(ThemeService.LoadTintedIcon(iconName, red));
    }

    [AvaloniaFact]
    public void Configuracoes_renderiza_seletor_de_cores_no_claro_e_no_escuro()
    {
        var host = NewHost();
        var vm = new MainViewModel(host);
        vm.SelectedTab = MainViewModel.SettingsTab;
        var w = new MainWindow { DataContext = vm, Width = 1040, Height = 720 };
        try
        {
            w.Show();
            w.UpdateLayout();

            var swatches = w.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("color-swatch"))
                .ToList();
            Assert.Equal(ColorPalettes.All.Count, swatches.Count);

            // Alterna entre modo escuro e claro com paletas diferentes sem falhas de layout
            vm.ThemeIndex = 2; // Escuro
            vm.SelectAccentColor("purple");
            w.UpdateLayout();

            vm.ThemeIndex = 1; // Claro
            vm.SelectAccentColor("orange");
            w.UpdateLayout();
        }
        finally
        {
            w.Close();
            vm.SelectAccentColor(ColorPalettes.DefaultKey);
            vm.ThemeIndex = 0;
            vm.Dispose();
        }
    }
}
