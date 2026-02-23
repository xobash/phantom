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

        SetAppBackgroundBrush(theme.AppBackground, darkMode);
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
        SetBrushColor("TerminalBackgroundBrush", theme.TerminalBackground);
        SetBrushColor("TerminalForegroundBrush", theme.TerminalForeground);
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

    private static void SetAppBackgroundBrush(string hex, bool darkMode)
    {
        var resources = Application.Current.Resources;
        var baseColor = (Color)ColorConverter.ConvertFromString(hex)!;
        var start = ScaleColor(baseColor, darkMode ? 0.9 : 1.03);
        var end = ScaleColor(baseColor, darkMode ? 1.15 : 0.97);

        if (resources["AppBgBrush"] is LinearGradientBrush gradient)
        {
            if (gradient.IsFrozen)
            {
                resources["AppBgBrush"] = CreateAppGradientBrush(start, end);
                return;
            }

            gradient.StartPoint = new Point(0, 0);
            gradient.EndPoint = new Point(1, 1);
            if (gradient.GradientStops.Count < 2)
            {
                gradient.GradientStops.Clear();
                gradient.GradientStops.Add(new GradientStop(start, 0));
                gradient.GradientStops.Add(new GradientStop(end, 1));
                return;
            }

            gradient.GradientStops[0].Color = start;
            gradient.GradientStops[0].Offset = 0;
            gradient.GradientStops[1].Color = end;
            gradient.GradientStops[1].Offset = 1;
            return;
        }

        resources["AppBgBrush"] = CreateAppGradientBrush(start, end);
    }

    private static Color ScaleColor(Color color, double scale)
    {
        static byte ClampChannel(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
        return Color.FromArgb(
            color.A,
            ClampChannel(color.R * scale),
            ClampChannel(color.G * scale),
            ClampChannel(color.B * scale));
    }

    private static LinearGradientBrush CreateAppGradientBrush(Color start, Color end)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop(start, 0));
        brush.GradientStops.Add(new GradientStop(end, 1));
        return brush;
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
        string GridRowSelectedBackground,
        string TerminalBackground,
        string TerminalForeground)
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
            GridRowSelectedBackground: "#3D3D3D",
            TerminalBackground: "#080808",
            TerminalForeground: "#DDE7FF");

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
            GridRowSelectedBackground: "#E3E3E3",
            TerminalBackground: "#F6F8FC",
            TerminalForeground: "#1C2430");
    }
}
