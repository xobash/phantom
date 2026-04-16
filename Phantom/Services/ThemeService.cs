using System.Diagnostics;
using Microsoft.Win32;
using Phantom.Models;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Phantom.Services;

public sealed class ThemeService
{
    public bool IsDarkMode { get; private set; } = true;
    public string CurrentMode { get; private set; } = AppThemeModes.Auto;

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    public void ApplyThemeMode(string? mode)
    {
        var normalizedMode = AppThemeModes.Normalize(mode);
        var darkMode = normalizedMode switch
        {
            AppThemeModes.Dark => true,
            AppThemeModes.Light => false,
            _ => IsSystemDarkModePreferred()
        };

        CurrentMode = normalizedMode;
        ApplyThemeCore(darkMode);
    }

    public void ApplyTheme(bool darkMode)
    {
        CurrentMode = darkMode ? AppThemeModes.Dark : AppThemeModes.Light;
        ApplyThemeCore(darkMode);
    }

    public bool IsSystemDarkModePreferred()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, WindowsSupportPolicy.PreferredRegistryView);
            using var personalizeKey = currentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false);
            var appModeValue = personalizeKey?.GetValue("AppsUseLightTheme");
            if (appModeValue is int appModeInt)
            {
                return appModeInt == 0;
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"ThemeService.IsSystemDarkModePreferred registry read failed: {ex.Message}");
        }

        return true;
    }

    private void ApplyThemeCore(bool darkMode)
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
        SetBrushColor("ScrollTrackBrush", theme.ScrollTrack);
        SetBrushColor("ScrollThumbBrush", theme.ScrollThumb);
        SetBrushColor("ScrollThumbHoverBrush", theme.ScrollThumbHover);
        SetBrushColor("ScrollThumbPressedBrush", theme.ScrollThumbPressed);
        SetBrushColor("TerminalBackgroundBrush", theme.TerminalBackground);
        SetBrushColor("TerminalForegroundBrush", theme.TerminalForeground);

        ApplyWindowChromeTheme(darkMode);
    }

    private static void ApplyWindowChromeTheme(bool darkMode)
    {
        if (!OperatingSystem.IsWindows() || Application.Current is null)
        {
            return;
        }

        foreach (Window window in Application.Current.Windows)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                var dark = darkMode ? 1 : 0;
                _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
                _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref dark, sizeof(int));
            }
            catch
            {
            }
        }
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
        var start = ScaleColor(baseColor, darkMode ? 0.82 : 1.03);
        var middle = ScaleColor(baseColor, darkMode ? 0.95 : 1.00);
        var end = ScaleColor(baseColor, darkMode ? 1.12 : 0.94);

        if (resources["AppBgBrush"] is LinearGradientBrush gradient)
        {
            if (gradient.IsFrozen)
            {
                resources["AppBgBrush"] = CreateAppGradientBrush(start, middle, end);
                return;
            }

            gradient.StartPoint = new Point(0, 0);
            gradient.EndPoint = new Point(1, 1);
            if (gradient.GradientStops.Count < 3)
            {
                gradient.GradientStops.Clear();
                gradient.GradientStops.Add(new GradientStop(start, 0));
                gradient.GradientStops.Add(new GradientStop(middle, 0.58));
                gradient.GradientStops.Add(new GradientStop(end, 1));
                return;
            }

            gradient.GradientStops[0].Color = start;
            gradient.GradientStops[0].Offset = 0;
            gradient.GradientStops[1].Color = middle;
            gradient.GradientStops[1].Offset = 0.58;
            gradient.GradientStops[2].Color = end;
            gradient.GradientStops[2].Offset = 1;
            while (gradient.GradientStops.Count > 3)
            {
                gradient.GradientStops.RemoveAt(gradient.GradientStops.Count - 1);
            }
            return;
        }

        resources["AppBgBrush"] = CreateAppGradientBrush(start, middle, end);
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

    private static LinearGradientBrush CreateAppGradientBrush(Color start, Color middle, Color end)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop(start, 0));
        brush.GradientStops.Add(new GradientStop(middle, 0.58));
        brush.GradientStops.Add(new GradientStop(end, 1));
        return brush;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

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
        string ScrollTrack,
        string ScrollThumb,
        string ScrollThumbHover,
        string ScrollThumbPressed,
        string TerminalBackground,
        string TerminalForeground)
    {
        public static ThemePalette Dark { get; } = new(
            AppBackground: "#111716",
            WindowBackground: "#171C1B",
            ShellBackground: "#131918",
            ShellAltBackground: "#181F1E",
            PanelBackground: "#1D2423",
            PanelAltBackground: "#212A28",
            CardBackground: "#202826",
            Border: "#34413D",
            TextPrimary: "#F3F4F1",
            TextSecondary: "#B9C2BC",
            Accent: "#B98543",
            AccentBorder: "#D49C57",
            NavItemBackground: "#1B2221",
            NavItemHover: "#212927",
            NavItemSelected: "#28312F",
            NavItemBorder: "#33403C",
            InputBackground: "#141A19",
            GridHeaderBackground: "#212A28",
            GridRowBackground: "#1B2221",
            GridRowAltBackground: "#202826",
            GridRowHoverBackground: "#262F2D",
            GridRowSelectedBackground: "#313936",
            ScrollTrack: "#101413",
            ScrollThumb: "#4C5954",
            ScrollThumbHover: "#65736D",
            ScrollThumbPressed: "#7C8A84",
            TerminalBackground: "#0B0F0F",
            TerminalForeground: "#D9E2DC");

        public static ThemePalette Light { get; } = new(
            AppBackground: "#ECE8E1",
            WindowBackground: "#F6F3EE",
            ShellBackground: "#EBE6DE",
            ShellAltBackground: "#F2EEE8",
            PanelBackground: "#FAF7F2",
            PanelAltBackground: "#FFFFFF",
            CardBackground: "#FAF7F2",
            Border: "#CDC3B7",
            TextPrimary: "#1B1F1D",
            TextSecondary: "#535B57",
            Accent: "#A36A2C",
            AccentBorder: "#B97E3E",
            NavItemBackground: "#F5F1EA",
            NavItemHover: "#EDE7DE",
            NavItemSelected: "#E6DED2",
            NavItemBorder: "#D1C7BB",
            InputBackground: "#FFFFFF",
            GridHeaderBackground: "#EFE8DE",
            GridRowBackground: "#FFFFFF",
            GridRowAltBackground: "#FBF8F3",
            GridRowHoverBackground: "#F1EADF",
            GridRowSelectedBackground: "#E8DFD1",
            ScrollTrack: "#EEE7DD",
            ScrollThumb: "#B1A69A",
            ScrollThumbHover: "#978B7F",
            ScrollThumbPressed: "#7A6F65",
            TerminalBackground: "#F4F1EA",
            TerminalForeground: "#24302C");
    }
}
