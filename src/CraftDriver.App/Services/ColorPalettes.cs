using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace CraftDriver.App.Services;

/// <summary>Tokens de cor de um modo (claro ou escuro) de uma paleta do CraftDriver.</summary>
public sealed record ModeColorSet(
    string Bg,
    string Surface,
    string Line,
    string Text,
    string TextMuted,
    string Accent,
    string AccentText,
    string AccentTextHover,
    string AccentSoft,
    string AccentLine,
    string ButtonBg,
    string ButtonBgOver,
    string ButtonBgPressed,
    string ButtonBgDisabled,
    string ButtonFgDisabled,
    string ButtonBorder,
    string ButtonBorderOver,
    string ButtonBorderDisabled,
    string AccentBtnBg,
    string AccentBtnOver,
    string AccentBtnPressed,
    string AccentBtnDisabled,
    string AccentBtnFg,
    string AccentBtnFgDisabled,
    string ListHighlightLow,
    string ListHighlightMedium,
    string ListHighlightHigh);

/// <summary>Paleta completa do CraftDriver: barra lateral (escura nos dois temas), modo claro e modo escuro.</summary>
public sealed record AppColorPalette(
    string Key,
    string DisplayName,
    string PreviewHex,
    string BrandMarkHex,
    string NavBg,
    string NavLabel,
    string NavMuted,
    string NavHover,
    string NavSelected,
    string NavIndicator,
    string NavLine,
    string NavButtonLine,
    string NavAlertIcon,
    ModeColorSet Light,
    ModeColorSet Dark);

/// <summary>
/// Paletas de cores harmônicas do CraftDriver. Cada paleta mantém a mesma estrutura tonal da identidade original
/// (petróleo): barra lateral profunda, fundos levemente matizados e alto contraste nos modos claro e escuro.
/// </summary>
public static class ColorPalettes
{
    public const string DefaultKey = "teal";

    public static readonly IReadOnlyList<AppColorPalette> All =
    [
        new AppColorPalette(
            Key: "teal",
            DisplayName: "Petróleo",
            PreviewHex: "#0E8F7E",
            BrandMarkHex: "#0E8F7E",
            NavBg: "#0F302C",
            NavLabel: "#C6D8D4",
            NavMuted: "#9DB8B3",
            NavHover: "#16403A",
            NavSelected: "#1E4D46",
            NavIndicator: "#5FD6C0",
            NavLine: "#24493F",
            NavButtonLine: "#3A625B",
            NavAlertIcon: "#FF8F84",
            Light: new ModeColorSet(
                Bg: "#F2F5F4", Surface: "#FFFFFF", Line: "#DCE3E1", Text: "#16211F", TextMuted: "#5A6865",
                Accent: "#0B7468", AccentText: "#0B7468", AccentTextHover: "#07554C", AccentSoft: "#E3F1EE", AccentLine: "#A9D3CA",
                ButtonBg: "#FFFFFF", ButtonBgOver: "#EDF3F1", ButtonBgPressed: "#E0E9E6", ButtonBgDisabled: "#F2F5F4",
                ButtonFgDisabled: "#98A5A1", ButtonBorder: "#C5D0CD", ButtonBorderOver: "#9FB0AB", ButtonBorderDisabled: "#E0E6E4",
                AccentBtnBg: "#0B7468", AccentBtnOver: "#095F55", AccentBtnPressed: "#084F47", AccentBtnDisabled: "#B8CEC9",
                AccentBtnFg: "#FFFFFF", AccentBtnFgDisabled: "#F4F7F6",
                ListHighlightLow: "#D6EBE6", ListHighlightMedium: "#C8E3DD", ListHighlightHigh: "#B8DBD3"),
            Dark: new ModeColorSet(
                Bg: "#121918", Surface: "#19221F", Line: "#2B3734", Text: "#E4ECEA", TextMuted: "#9AABA7",
                Accent: "#3CBFA8", AccentText: "#5FD6C0", AccentTextHover: "#8DE5D4", AccentSoft: "#173A35", AccentLine: "#2A6058",
                ButtonBg: "#1E2926", ButtonBgOver: "#263330", ButtonBgPressed: "#2E3D39", ButtonBgDisabled: "#182120",
                ButtonFgDisabled: "#66736F", ButtonBorder: "#374642", ButtonBorderOver: "#4F6560", ButtonBorderDisabled: "#26302E",
                AccentBtnBg: "#3CBFA8", AccentBtnOver: "#57CDB8", AccentBtnPressed: "#2FA592", AccentBtnDisabled: "#24403B",
                AccentBtnFg: "#052420", AccentBtnFgDisabled: "#6E8580",
                ListHighlightLow: "#1D4741", ListHighlightMedium: "#23524B", ListHighlightHigh: "#2A5E56")),

        new AppColorPalette(
            Key: "blue",
            DisplayName: "Azul",
            PreviewHex: "#256AE0",
            BrandMarkHex: "#256AE0",
            NavBg: "#10223E",
            NavLabel: "#C8D6EC",
            NavMuted: "#9DB2D4",
            NavHover: "#183157",
            NavSelected: "#21406E",
            NavIndicator: "#73B0FF",
            NavLine: "#243D63",
            NavButtonLine: "#3B5885",
            NavAlertIcon: "#FF8F84",
            Light: new ModeColorSet(
                Bg: "#F2F5FA", Surface: "#FFFFFF", Line: "#DCE2EC", Text: "#161E2B", TextMuted: "#586477",
                Accent: "#1B5BC7", AccentText: "#1B5BC7", AccentTextHover: "#144599", AccentSoft: "#E6EFFC", AccentLine: "#AFC7F2",
                ButtonBg: "#FFFFFF", ButtonBgOver: "#EDF2FA", ButtonBgPressed: "#E0E8F5", ButtonBgDisabled: "#F2F5FA",
                ButtonFgDisabled: "#94A0B2", ButtonBorder: "#C5CFE0", ButtonBorderOver: "#9BAAC4", ButtonBorderDisabled: "#E0E5EE",
                AccentBtnBg: "#1B5BC7", AccentBtnOver: "#164DA8", AccentBtnPressed: "#123F8A", AccentBtnDisabled: "#B8C7E0",
                AccentBtnFg: "#FFFFFF", AccentBtnFgDisabled: "#F4F7FC",
                ListHighlightLow: "#D9E6FA", ListHighlightMedium: "#C9DCF7", ListHighlightHigh: "#B8D0F4"),
            Dark: new ModeColorSet(
                Bg: "#111620", Surface: "#18202D", Line: "#2A3548", Text: "#E4EAF5", TextMuted: "#9AA7BD",
                Accent: "#66A6FF", AccentText: "#82B8FF", AccentTextHover: "#A8CEFF", AccentSoft: "#182E4F", AccentLine: "#2D5285",
                ButtonBg: "#1D2636", ButtonBgOver: "#253145", ButtonBgPressed: "#2E3C54", ButtonBgDisabled: "#171D29",
                ButtonFgDisabled: "#647085", ButtonBorder: "#36445C", ButtonBorderOver: "#4E6282", ButtonBorderDisabled: "#242E3F",
                AccentBtnBg: "#66A6FF", AccentBtnOver: "#82B8FF", AccentBtnPressed: "#4D92F2", AccentBtnDisabled: "#243652",
                AccentBtnFg: "#061830", AccentBtnFgDisabled: "#6E809E",
                ListHighlightLow: "#1C355C", ListHighlightMedium: "#23406E", ListHighlightHigh: "#2A4B80")),

        new AppColorPalette(
            Key: "indigo",
            DisplayName: "Índigo",
            PreviewHex: "#4F54D4",
            BrandMarkHex: "#555BE0",
            NavBg: "#171A3D",
            NavLabel: "#CDCFED",
            NavMuted: "#A5A8D4",
            NavHover: "#222657",
            NavSelected: "#2C316E",
            NavIndicator: "#9AA2FF",
            NavLine: "#2E3366",
            NavButtonLine: "#474D8A",
            NavAlertIcon: "#FF8F84",
            Light: new ModeColorSet(
                Bg: "#F4F4FA", Surface: "#FFFFFF", Line: "#DFDFEE", Text: "#191A2B", TextMuted: "#5D5F78",
                Accent: "#4347C2", AccentText: "#4347C2", AccentTextHover: "#323599", AccentSoft: "#EAEBFA", AccentLine: "#B9BBEE",
                ButtonBg: "#FFFFFF", ButtonBgOver: "#EFEFFA", ButtonBgPressed: "#E3E4F5", ButtonBgDisabled: "#F4F4FA",
                ButtonFgDisabled: "#989AB2", ButtonBorder: "#C9CAE0", ButtonBorderOver: "#A3A5C7", ButtonBorderDisabled: "#E2E2EE",
                AccentBtnBg: "#4347C2", AccentBtnOver: "#373AA3", AccentBtnPressed: "#2D3087", AccentBtnDisabled: "#BEC0E0",
                AccentBtnFg: "#FFFFFF", AccentBtnFgDisabled: "#F5F5FC",
                ListHighlightLow: "#DFDFF8", ListHighlightMedium: "#D1D2F5", ListHighlightHigh: "#C2C4F2"),
            Dark: new ModeColorSet(
                Bg: "#131421", Surface: "#1B1C2E", Line: "#2E304A", Text: "#E7E8F5", TextMuted: "#9FA1BD",
                Accent: "#8C96FF", AccentText: "#A3ABFF", AccentTextHover: "#C2C7FF", AccentSoft: "#22254F", AccentLine: "#3D4285",
                ButtonBg: "#212338", ButtonBgOver: "#2A2C47", ButtonBgPressed: "#333657", ButtonBgDisabled: "#181929",
                ButtonFgDisabled: "#686A85", ButtonBorder: "#3A3D5E", ButtonBorderOver: "#545885", ButtonBorderDisabled: "#272940",
                AccentBtnBg: "#8C96FF", AccentBtnOver: "#A3ABFF", AccentBtnPressed: "#747FF0", AccentBtnDisabled: "#2C2F54",
                AccentBtnFg: "#0B0D2E", AccentBtnFgDisabled: "#76799E",
                ListHighlightLow: "#262959", ListHighlightMedium: "#2E326B", ListHighlightHigh: "#363B7D")),

        new AppColorPalette(
            Key: "purple",
            DisplayName: "Roxo",
            PreviewHex: "#8245CC",
            BrandMarkHex: "#884BD4",
            NavBg: "#241438",
            NavLabel: "#D8CCE8",
            NavMuted: "#B4A1CC",
            NavHover: "#331D4F",
            NavSelected: "#432766",
            NavIndicator: "#C9A1FF",
            NavLine: "#3E265C",
            NavButtonLine: "#5B3D80",
            NavAlertIcon: "#FF8F84",
            Light: new ModeColorSet(
                Bg: "#F6F3FA", Surface: "#FFFFFF", Line: "#E3DDEC", Text: "#20172B", TextMuted: "#665B75",
                Accent: "#7235B8", AccentText: "#7235B8", AccentTextHover: "#572691", AccentSoft: "#F0E8FA", AccentLine: "#CCB4EB",
                ButtonBg: "#FFFFFF", ButtonBgOver: "#F2EEF8", ButtonBgPressed: "#E7E0F2", ButtonBgDisabled: "#F6F3FA",
                ButtonFgDisabled: "#9E94AD", ButtonBorder: "#CFC7DB", ButtonBorderOver: "#ADA1BF", ButtonBorderDisabled: "#E6E1ED",
                AccentBtnBg: "#7235B8", AccentBtnOver: "#5E2A9C", AccentBtnPressed: "#4D2180", AccentBtnDisabled: "#C9B8DE",
                AccentBtnFg: "#FFFFFF", AccentBtnFgDisabled: "#F7F4FC",
                ListHighlightLow: "#E8DCF7", ListHighlightMedium: "#DECFF4", ListHighlightHigh: "#D2BFF0"),
            Dark: new ModeColorSet(
                Bg: "#17121F", Surface: "#211A2B", Line: "#352C45", Text: "#ECE6F5", TextMuted: "#AA9EBA",
                Accent: "#BA8CFF", AccentText: "#C8A2FF", AccentTextHover: "#DFC4FF", AccentSoft: "#311F4A", AccentLine: "#563880",
                ButtonBg: "#282036", ButtonBgOver: "#332945", ButtonBgPressed: "#3E3254", ButtonBgDisabled: "#1D1726",
                ButtonFgDisabled: "#726682", ButtonBorder: "#453959", ButtonBorderOver: "#62527D", ButtonBorderDisabled: "#2E263D",
                AccentBtnBg: "#BA8CFF", AccentBtnOver: "#C8A2FF", AccentBtnPressed: "#A472F0", AccentBtnDisabled: "#382852",
                AccentBtnFg: "#1B0930", AccentBtnFgDisabled: "#85749E",
                ListHighlightLow: "#382454", ListHighlightMedium: "#432C66", ListHighlightHigh: "#4F3478")),

        new AppColorPalette(
            Key: "pink",
            DisplayName: "Rosa",
            PreviewHex: "#D13682",
            BrandMarkHex: "#D63B87",
            NavBg: "#361325",
            NavLabel: "#E8CCD9",
            NavMuted: "#CC9FB5",
            NavHover: "#4D1C35",
            NavSelected: "#632545",
            NavIndicator: "#FF99CB",
            NavLine: "#57243F",
            NavButtonLine: "#7A3B5C",
            NavAlertIcon: "#FFD07A",
            Light: new ModeColorSet(
                Bg: "#FAF3F6", Surface: "#FFFFFF", Line: "#ECDFE5", Text: "#2B1721", TextMuted: "#755A67",
                Accent: "#B3246A", AccentText: "#B3246A", AccentTextHover: "#8C1A52", AccentSoft: "#FCE8F2", AccentLine: "#EBB3CF",
                ButtonBg: "#FFFFFF", ButtonBgOver: "#F8EEF3", ButtonBgPressed: "#F2E0E9", ButtonBgDisabled: "#FAF3F6",
                ButtonFgDisabled: "#AD94A0", ButtonBorder: "#DBC7D0", ButtonBorderOver: "#BF9FAD", ButtonBorderDisabled: "#EDE1E6",
                AccentBtnBg: "#B3246A", AccentBtnOver: "#961D58", AccentBtnPressed: "#7D1648", AccentBtnDisabled: "#DEB6C9",
                AccentBtnFg: "#FFFFFF", AccentBtnFgDisabled: "#FCF4F8",
                ListHighlightLow: "#F8DCEB", ListHighlightMedium: "#F5CEE3", ListHighlightHigh: "#F0BED8"),
            Dark: new ModeColorSet(
                Bg: "#1F1219", Surface: "#2B1A23", Line: "#452C3A", Text: "#F5E6EE", TextMuted: "#BA9EAD",
                Accent: "#F57EB8", AccentText: "#FA99C8", AccentTextHover: "#FFBDD9", AccentSoft: "#4A1E35", AccentLine: "#80375D",
                ButtonBg: "#36202C", ButtonBgOver: "#452938", ButtonBgPressed: "#543244", ButtonBgDisabled: "#26171F",
                ButtonFgDisabled: "#826675", ButtonBorder: "#59394B", ButtonBorderOver: "#7D526A", ButtonBorderDisabled: "#3D2632",
                AccentBtnBg: "#F57EB8", AccentBtnOver: "#FA99C8", AccentBtnPressed: "#E065A1", AccentBtnDisabled: "#52283E",
                AccentBtnFg: "#2E081B", AccentBtnFgDisabled: "#9E748A",
                ListHighlightLow: "#54223C", ListHighlightMedium: "#662A4A", ListHighlightHigh: "#783257")),

        new AppColorPalette(
            Key: "red",
            DisplayName: "Vermelho",
            PreviewHex: "#D1343D",
            BrandMarkHex: "#D63A43",
            NavBg: "#361315",
            NavLabel: "#E8CDCE",
            NavMuted: "#CCA1A3",
            NavHover: "#4D1C1F",
            NavSelected: "#632529",
            NavIndicator: "#FF9CA2",
            NavLine: "#572427",
            NavButtonLine: "#7A3B3F",
            NavAlertIcon: "#FFD07A",
            Light: new ModeColorSet(
                Bg: "#FAF3F3", Surface: "#FFFFFF", Line: "#ECDFDF", Text: "#2B1718", TextMuted: "#755B5C",
                Accent: "#B8252E", AccentText: "#B8252E", AccentTextHover: "#911B22", AccentSoft: "#FCEAE9", AccentLine: "#EBB5B7",
                ButtonBg: "#FFFFFF", ButtonBgOver: "#F8EEEE", ButtonBgPressed: "#F2E0E1", ButtonBgDisabled: "#FAF3F3",
                ButtonFgDisabled: "#AD9495", ButtonBorder: "#DBC7C8", ButtonBorderOver: "#BF9FA1", ButtonBorderDisabled: "#EDE1E1",
                AccentBtnBg: "#B8252E", AccentBtnOver: "#9B1D25", AccentBtnPressed: "#80161D", AccentBtnDisabled: "#DEB8BA",
                AccentBtnFg: "#FFFFFF", AccentBtnFgDisabled: "#FCF4F4",
                ListHighlightLow: "#F8DDDE", ListHighlightMedium: "#F5CFD1", ListHighlightHigh: "#F0BFC1"),
            Dark: new ModeColorSet(
                Bg: "#1F1213", Surface: "#2B1A1B", Line: "#452C2E", Text: "#F5E6E7", TextMuted: "#BA9E9F",
                Accent: "#F77E85", AccentText: "#FF9CA2", AccentTextHover: "#FFC2C6", AccentSoft: "#4A1E21", AccentLine: "#80373C",
                ButtonBg: "#362022", ButtonBgOver: "#45292B", ButtonBgPressed: "#543235", ButtonBgDisabled: "#261718",
                ButtonFgDisabled: "#826668", ButtonBorder: "#59393B", ButtonBorderOver: "#7D5255", ButtonBorderDisabled: "#3D2627",
                AccentBtnBg: "#F77E85", AccentBtnOver: "#FF9CA2", AccentBtnPressed: "#E0656C", AccentBtnDisabled: "#52282B",
                AccentBtnFg: "#2E080B", AccentBtnFgDisabled: "#9E7476",
                ListHighlightLow: "#542225", ListHighlightMedium: "#662A2E", ListHighlightHigh: "#783236")),

        new AppColorPalette(
            Key: "orange",
            DisplayName: "Laranja",
            PreviewHex: "#D9601A",
            BrandMarkHex: "#DE641E",
            NavBg: "#361C0E",
            NavLabel: "#E8D5C8",
            NavMuted: "#CCB09D",
            NavHover: "#4D2915",
            NavSelected: "#63351C",
            NavIndicator: "#FFB475",
            NavLine: "#57321E",
            NavButtonLine: "#7A4C33",
            NavAlertIcon: "#FF8F84",
            Light: new ModeColorSet(
                Bg: "#FAF5F2", Surface: "#FFFFFF", Line: "#ECE3DC", Text: "#2B1D15", TextMuted: "#756155",
                Accent: "#B0440B", AccentText: "#B0440B", AccentTextHover: "#8A3306", AccentSoft: "#FCEEE4", AccentLine: "#EBC3A7",
                ButtonBg: "#FFFFFF", ButtonBgOver: "#F8F1EC", ButtonBgPressed: "#F2E6DD", ButtonBgDisabled: "#FAF5F2",
                ButtonFgDisabled: "#AD9B8E", ButtonBorder: "#DBCEC5", ButtonBorderOver: "#BFAA9B", ButtonBorderDisabled: "#EDE5DF",
                AccentBtnBg: "#B0440B", AccentBtnOver: "#943808", AccentBtnPressed: "#7A2D05", AccentBtnDisabled: "#DEC2B1",
                AccentBtnFg: "#FFFFFF", AccentBtnFgDisabled: "#FCF6F2",
                ListHighlightLow: "#F8E3D4", ListHighlightMedium: "#F5D7C1", ListHighlightHigh: "#F0C8AB"),
            Dark: new ModeColorSet(
                Bg: "#1F1510", Surface: "#2B1E17", Line: "#453328", Text: "#F5ECE6", TextMuted: "#BAA79B",
                Accent: "#F59D56", AccentText: "#FFB373", AccentTextHover: "#FFCE9E", AccentSoft: "#4A2B16", AccentLine: "#804E2A",
                ButtonBg: "#36251C", ButtonBgOver: "#453024", ButtonBgPressed: "#543B2D", ButtonBgDisabled: "#261B15",
                ButtonFgDisabled: "#827064", ButtonBorder: "#594133", ButtonBorderOver: "#7D5D4B", ButtonBorderDisabled: "#3D2C22",
                AccentBtnBg: "#F59D56", AccentBtnOver: "#FFB373", AccentBtnPressed: "#E0853E", AccentBtnDisabled: "#523520",
                AccentBtnFg: "#2B1304", AccentBtnFgDisabled: "#9E826D",
                ListHighlightLow: "#543119", ListHighlightMedium: "#663C20", ListHighlightHigh: "#784827")),

        new AppColorPalette(
            Key: "green",
            DisplayName: "Verde",
            PreviewHex: "#209452",
            BrandMarkHex: "#229C56",
            NavBg: "#102E1C",
            NavLabel: "#C8E0D1",
            NavMuted: "#9EC2AC",
            NavHover: "#174228",
            NavSelected: "#205736",
            NavIndicator: "#6EE09B",
            NavLine: "#234D34",
            NavButtonLine: "#396B4D",
            NavAlertIcon: "#FF8F84",
            Light: new ModeColorSet(
                Bg: "#F2F7F4", Surface: "#FFFFFF", Line: "#DCE6DF", Text: "#16241B", TextMuted: "#586B5E",
                Accent: "#18753F", AccentText: "#18753F", AccentTextHover: "#11592F", AccentSoft: "#E4F2E9", AccentLine: "#AED6BD",
                ButtonBg: "#FFFFFF", ButtonBgOver: "#EDF5F0", ButtonBgPressed: "#E0ECE4", ButtonBgDisabled: "#F2F7F4",
                ButtonFgDisabled: "#93A89A", ButtonBorder: "#C4D4C9", ButtonBorderOver: "#9CB5A4", ButtonBorderDisabled: "#DFE8E2",
                AccentBtnBg: "#18753F", AccentBtnOver: "#136133", AccentBtnPressed: "#0F4F29", AccentBtnDisabled: "#B7D1C1",
                AccentBtnFg: "#FFFFFF", AccentBtnFgDisabled: "#F4FAF6",
                ListHighlightLow: "#D7EDE0", ListHighlightMedium: "#C7E6D3", ListHighlightHigh: "#B6DEC6"),
            Dark: new ModeColorSet(
                Bg: "#111A14", Surface: "#18241C", Line: "#2A3B30", Text: "#E4F0E8", TextMuted: "#99B0A1",
                Accent: "#52CC82", AccentText: "#6EE09B", AccentTextHover: "#9BEBB9", AccentSoft: "#173D26", AccentLine: "#2B6642",
                ButtonBg: "#1D2B22", ButtonBgOver: "#25362B", ButtonBgPressed: "#2E4235", ButtonBgDisabled: "#17211B",
                ButtonFgDisabled: "#637569", ButtonBorder: "#364A3D", ButtonBorderOver: "#4E6B58", ButtonBorderDisabled: "#24332A",
                AccentBtnBg: "#52CC82", AccentBtnOver: "#6EE09B", AccentBtnPressed: "#3DB56D", AccentBtnDisabled: "#234230",
                AccentBtnFg: "#062412", AccentBtnFgDisabled: "#6D8A78",
                ListHighlightLow: "#1C472D", ListHighlightMedium: "#235737", ListHighlightHigh: "#2A6642")),

        new AppColorPalette(
            Key: "slate",
            DisplayName: "Grafite",
            PreviewHex: "#4F6275",
            BrandMarkHex: "#556A80",
            NavBg: "#181E26",
            NavLabel: "#CFD6DE",
            NavMuted: "#A3AEBA",
            NavHover: "#232C38",
            NavSelected: "#2E3A4A",
            NavIndicator: "#AFC2D6",
            NavLine: "#2F3A47",
            NavButtonLine: "#49586B",
            NavAlertIcon: "#FF8F84",
            Light: new ModeColorSet(
                Bg: "#F3F5F7", Surface: "#FFFFFF", Line: "#DDE2E8", Text: "#181E24", TextMuted: "#5C6670",
                Accent: "#3D4E60", AccentText: "#3D4E60", AccentTextHover: "#2B3845", AccentSoft: "#E8EDF2", AccentLine: "#BCC7D4",
                ButtonBg: "#FFFFFF", ButtonBgOver: "#EEF1F5", ButtonBgPressed: "#E2E7ED", ButtonBgDisabled: "#F3F5F7",
                ButtonFgDisabled: "#96A0AA", ButtonBorder: "#C7CFD8", ButtonBorderOver: "#A0ACB8", ButtonBorderDisabled: "#E1E5EA",
                AccentBtnBg: "#3D4E60", AccentBtnOver: "#31404F", AccentBtnPressed: "#273340", AccentBtnDisabled: "#BCC4CC",
                AccentBtnFg: "#FFFFFF", AccentBtnFgDisabled: "#F5F7F9",
                ListHighlightLow: "#DDE4EC", ListHighlightMedium: "#CFD9E4", ListHighlightHigh: "#C0CDDC"),
            Dark: new ModeColorSet(
                Bg: "#13161A", Surface: "#1B2026", Line: "#2E3640", Text: "#E7ECF2", TextMuted: "#9FAAB5",
                Accent: "#95AABF", AccentText: "#AFC2D6", AccentTextHover: "#CAD9E8", AccentSoft: "#232E3B", AccentLine: "#415469",
                ButtonBg: "#21272E", ButtonBgOver: "#2A313B", ButtonBgPressed: "#333C47", ButtonBgDisabled: "#181C21",
                ButtonFgDisabled: "#68727D", ButtonBorder: "#3A4450", ButtonBorderOver: "#546273", ButtonBorderDisabled: "#272E36",
                AccentBtnBg: "#95AABF", AccentBtnOver: "#AFC2D6", AccentBtnPressed: "#7D94AB", AccentBtnDisabled: "#2D3742",
                AccentBtnFg: "#0D141C", AccentBtnFgDisabled: "#768391",
                ListHighlightLow: "#263342", ListHighlightMedium: "#2E3E50", ListHighlightHigh: "#36495E")),
    ];

    static readonly Dictionary<string, AppColorPalette> ByKey =
        All.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);

    public static AppColorPalette Get(string? key) =>
        key != null && ByKey.TryGetValue(key, out var palette) ? palette : All[0];

    public static int IndexOf(string? key)
    {
        var resolved = Get(key);
        for (var i = 0; i < All.Count; i++)
            if (All[i].Key == resolved.Key) return i;
        return 0;
    }

    public static ResourceDictionary BuildResourceDictionary(AppColorPalette p)
    {
        var rd = new ResourceDictionary
        {
            ["CdBrandMarkBrush"] = Brush(p.BrandMarkHex),
            ["CdNavBg"] = Brush(p.NavBg),
            ["CdNavText"] = Brush("#FFFFFF"),
            ["CdNavLabel"] = Brush(p.NavLabel),
            ["CdNavMuted"] = Brush(p.NavMuted),
            ["CdNavHover"] = Brush(p.NavHover),
            ["CdNavSelected"] = Brush(p.NavSelected),
            ["CdNavIndicator"] = Brush(p.NavIndicator),
            ["CdNavLine"] = Brush(p.NavLine),
            ["CdNavButtonLine"] = Brush(p.NavButtonLine),
            ["CdNavAlertBrush"] = Brush(p.NavAlertIcon),
        };
        rd.ThemeDictionaries[ThemeVariant.Light] = BuildModeDictionary(p.Light);
        rd.ThemeDictionaries[ThemeVariant.Dark] = BuildModeDictionary(p.Dark);
        return rd;
    }

    static ResourceDictionary BuildModeDictionary(ModeColorSet m) => new()
    {
        ["CdBg"] = Brush(m.Bg),
        ["CdSurface"] = Brush(m.Surface),
        ["CdLine"] = Brush(m.Line),
        ["CdText"] = Brush(m.Text),
        ["CdTextMuted"] = Brush(m.TextMuted),
        ["CdAccentText"] = Brush(m.AccentText),
        ["CdAccentTextHover"] = Brush(m.AccentTextHover),
        ["CdAccentSoft"] = Brush(m.AccentSoft),
        ["CdAccentLine"] = Brush(m.AccentLine),

        ["ButtonBackground"] = Brush(m.ButtonBg),
        ["ButtonBackgroundPointerOver"] = Brush(m.ButtonBgOver),
        ["ButtonBackgroundPressed"] = Brush(m.ButtonBgPressed),
        ["ButtonBackgroundDisabled"] = Brush(m.ButtonBgDisabled),
        ["ButtonForeground"] = Brush(m.Text),
        ["ButtonForegroundPointerOver"] = Brush(m.Text),
        ["ButtonForegroundPressed"] = Brush(m.Text),
        ["ButtonForegroundDisabled"] = Brush(m.ButtonFgDisabled),
        ["ButtonBorderBrush"] = Brush(m.ButtonBorder),
        ["ButtonBorderBrushPointerOver"] = Brush(m.ButtonBorderOver),
        ["ButtonBorderBrushPressed"] = Brush(m.ButtonBorderOver),
        ["ButtonBorderBrushDisabled"] = Brush(m.ButtonBorderDisabled),

        ["AccentButtonBackground"] = Brush(m.AccentBtnBg),
        ["AccentButtonBackgroundPointerOver"] = Brush(m.AccentBtnOver),
        ["AccentButtonBackgroundPressed"] = Brush(m.AccentBtnPressed),
        ["AccentButtonBackgroundDisabled"] = Brush(m.AccentBtnDisabled),
        ["AccentButtonForeground"] = Brush(m.AccentBtnFg),
        ["AccentButtonForegroundPointerOver"] = Brush(m.AccentBtnFg),
        ["AccentButtonForegroundPressed"] = Brush(m.AccentBtnFg),
        ["AccentButtonForegroundDisabled"] = Brush(m.AccentBtnFgDisabled),

        ["SystemControlHighlightListAccentLowBrush"] = Brush(m.ListHighlightLow),
        ["SystemControlHighlightListAccentMediumBrush"] = Brush(m.ListHighlightMedium),
        ["SystemControlHighlightListAccentHighBrush"] = Brush(m.ListHighlightHigh),
    };

    static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));
}
