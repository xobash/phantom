using System.Windows.Media;

namespace Phantom.Services;

public sealed class ThemeService
{
    public bool IsDarkMode { get; private set; } = true;

    public void ApplyTheme(bool darkMode)
    {
        IsDarkMode = darkMode;
        var theme = darkMode ? ThemePalette.Dark : ThemePalette.Light;

        SetBrushColor("BgBrush", theme.WindowBackground);
        SetBrushColor("CardBrush", theme.CardBackground);
        SetBrushColor("BorderBrush", theme.Border);

        SetBrushColor("AppBgBrush", theme.AppBackground);
        SetBrushColor("ShellBrush", theme.ShellBackground);
        SetBrushColor("ShellAltBrush", theme.ShellAltBackground);

        SetBrushColor("DarkCardBrush", theme.PanelBackground);
        SetBrushColor("DarkCard2Brush", theme.PanelAltBackground);
        SetBrushColor("DarkBorderBrush", theme.Border);

        SetBrushColor("DarkTextBrush", theme.TextPrimary);
        SetBrushColor("MutedTextBrush", theme.TextSecondary);
        SetBrushColor("AccentBrush", theme.Accent);
        SetBrushColor("AccentBorderBrush", theme.AccentBorder);

        SetBrushColor("NavItemBackgroundBrush", theme.NavItemBackground);
        SetBrushColor("NavItemHoverBrush", theme.NavItemHover);
        SetBrushColor("NavItemSelectedBrush", theme.NavItemSelected);
        SetBrushColor("NavItemBorderBrush", theme.NavItemBorder);

        SetBrushColor("InputBackgroundBrush", theme.InputBackground);
        SetBrushColor("HeaderBackgroundBrush", theme.GridHeaderBackground);
        SetBrushColor("RowBackgroundBrush", theme.GridRowBackground);
        SetBrushColor("RowAltBackgroundBrush", theme.GridRowAltBackground);
        SetBrushColor("RowHoverBackgroundBrush", theme.GridRowHoverBackground);
        SetBrushColor("RowSelectedBackgroundBrush", theme.GridRowSelectedBackground);
    }

    private static void SetBrushColor(string key, string hex)
    {
        var resources = Application.Current.Resources;
        var color = (Color)ColorConverter.ConvertFromString(hex)!;

        if (resources[key] is SolidColorBrush brush)
        {
            if (brush.IsFrozen)
            {
                resources[key] = new SolidColorBrush(color);
            }
            else
            {
                brush.Color = color;
            }
        }
        else
        {
            resources[key] = new SolidColorBrush(color);
        }
    }

    private sealed record ThemePalette(
        string AppBackground,
        string WindowBackground,
        string ShellBackground,
        string ShellAltBackground,
        string PanelBackground,
        string PanelAltBackground,
        string CardBackground,
        string Border,
        string TextPrimary,
        string TextSecondary,
        string Accent,
        string AccentBorder,
        string NavItemBackground,
        string NavItemHover,
        string NavItemSelected,
        string NavItemBorder,
        string InputBackground,
        string GridHeaderBackground,
        string GridRowBackground,
        string GridRowAltBackground,
        string GridRowHoverBackground,
        string GridRowSelectedBackground)
    {
        public static ThemePalette Dark { get; } = new(
            AppBackground: "#141414",
            WindowBackground: "#1E1E1E",
            ShellBackground: "#1B1B1B",
            ShellAltBackground: "#202020",
            PanelBackground: "#262626",
            PanelAltBackground: "#2B2B2B",
            CardBackground: "#2B2B2B",
            Border: "#3D3D3D",
            TextPrimary: "#F2F2F2",
            TextSecondary: "#CECECE",
            Accent: "#6C6C6C",
            AccentBorder: "#8A8A8A",
            NavItemBackground: "#262626",
            NavItemHover: "#2E2E2E",
            NavItemSelected: "#373737",
            NavItemBorder: "#444444",
            InputBackground: "#1F1F1F",
            GridHeaderBackground: "#303030",
            GridRowBackground: "#252525",
            GridRowAltBackground: "#2B2B2B",
            GridRowHoverBackground: "#333333",
            GridRowSelectedBackground: "#3D3D3D");

        public static ThemePalette Light { get; } = new(
            AppBackground: "#E8E8E8",
            WindowBackground: "#F0F0F0",
            ShellBackground: "#E2E2E2",
            ShellAltBackground: "#ECECEC",
            PanelBackground: "#F8F8F8",
            PanelAltBackground: "#FFFFFF",
            CardBackground: "#F8F8F8",
            Border: "#CFCFCF",
            TextPrimary: "#111111",
            TextSecondary: "#454545",
            Accent: "#5E5E5E",
            AccentBorder: "#757575",
            NavItemBackground: "#F7F7F7",
            NavItemHover: "#EEEEEE",
            NavItemSelected: "#E4E4E4",
            NavItemBorder: "#CFCFCF",
            InputBackground: "#FFFFFF",
            GridHeaderBackground: "#F0F0F0",
            GridRowBackground: "#FFFFFF",
            GridRowAltBackground: "#F9F9F9",
            GridRowHoverBackground: "#F0F0F0",
            GridRowSelectedBackground: "#E3E3E3");
    }
}
