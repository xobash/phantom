using System.Diagnostics;
using System.ComponentModel;
using Microsoft.Win32;
using Phantom.Models;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Phantom.Services;

public sealed class ThemeService : IDisposable
{
    private static readonly Color FallbackAccentColor = Color.FromRgb(0, 120, 212);

    public bool IsDarkMode { get; private set; } = true;
    public string CurrentMode { get; private set; } = AppThemeModes.Auto;
    public string CurrentAccentMode { get; private set; } = AppAccentModes.Windows;
    public string CurrentCustomAccentColor { get; private set; } = string.Empty;
    public Color CurrentAccentColor { get; private set; } = FallbackAccentColor;
    public string CurrentAccentHex => ToHex(CurrentAccentColor);

    public event EventHandler? ThemeChanged;

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private bool _disposed;

    public ThemeService()
    {
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
    }

    public void ApplyThemeMode(string? mode, string? accentMode = null, string? customAccentColor = null)
    {
        var normalizedMode = AppThemeModes.Normalize(mode);
        var darkMode = normalizedMode switch
        {
            AppThemeModes.Dark => true,
            AppThemeModes.Light => false,
            _ => IsSystemDarkModePreferred()
        };

        CurrentMode = normalizedMode;
        ApplyAccent(accentMode, customAccentColor);
        ApplyThemeCore(darkMode);
    }

    public void ApplyTheme(bool darkMode, string? accentMode = null, string? customAccentColor = null)
    {
        CurrentMode = darkMode ? AppThemeModes.Dark : AppThemeModes.Light;
        ApplyAccent(accentMode, customAccentColor);
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

    public static bool TryParseAccentColor(string? value, out Color color, out string normalizedHex)
    {
        color = FallbackAccentColor;
        normalizedHex = string.Empty;

        var text = (value ?? string.Empty).Trim();
        if (text.StartsWith('#'))
        {
            text = text[1..];
        }

        if (text.Length is not (6 or 8) || text.Any(c => !Uri.IsHexDigit(c)))
        {
            return false;
        }

        try
        {
            var offset = text.Length == 8 ? 2 : 0;
            var alpha = text.Length == 8
                ? Convert.ToByte(text[..2], 16)
                : byte.MaxValue;
            var red = Convert.ToByte(text.Substring(offset, 2), 16);
            var green = Convert.ToByte(text.Substring(offset + 2, 2), 16);
            var blue = Convert.ToByte(text.Substring(offset + 4, 2), 16);
            color = Color.FromArgb(alpha, red, green, blue);
            normalizedHex = ToHex(Color.FromRgb(red, green, blue));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyAccent(string? accentMode, string? customAccentColor)
    {
        CurrentAccentMode = AppAccentModes.Normalize(accentMode ?? CurrentAccentMode);
        CurrentCustomAccentColor = customAccentColor?.Trim() ?? CurrentCustomAccentColor;

        if (string.Equals(CurrentAccentMode, AppAccentModes.Custom, StringComparison.Ordinal) &&
            TryParseAccentColor(CurrentCustomAccentColor, out var customColor, out var normalizedHex))
        {
            CurrentCustomAccentColor = normalizedHex;
            CurrentAccentColor = customColor;
            return;
        }

        CurrentAccentColor = ResolveWindowsAccentColor();
    }

    private void ApplyThemeCore(bool darkMode)
    {
        IsDarkMode = darkMode;
        var theme = darkMode ? ThemePalette.Dark : ThemePalette.Light;
        var accentBorder = AdjustForBorder(CurrentAccentColor, darkMode);

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
        SetBrushColor("AccentBrush", CurrentAccentColor);
        SetBrushColor("AccentBorderBrush", accentBorder);

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
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(CurrentAccentMode, AppAccentModes.Windows, StringComparison.Ordinal))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(e.PropertyName) &&
            !string.Equals(e.PropertyName, nameof(SystemParameters.WindowGlassColor), StringComparison.Ordinal) &&
            !string.Equals(e.PropertyName, nameof(SystemParameters.WindowGlassBrush), StringComparison.Ordinal))
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        _ = dispatcher.BeginInvoke(() => ApplyThemeMode(CurrentMode, CurrentAccentMode, CurrentCustomAccentColor));
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
        SetBrushColor(key, (Color)ColorConverter.ConvertFromString(hex)!);
    }

    private static void SetBrushColor(string key, Color color)
    {
        var resources = Application.Current.Resources;

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

    private static Color ResolveWindowsAccentColor()
    {
        if (!OperatingSystem.IsWindows())
        {
            return FallbackAccentColor;
        }

        try
        {
            var color = SystemParameters.WindowGlassColor;
            return Color.FromRgb(color.R, color.G, color.B);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"ThemeService.ResolveWindowsAccentColor failed: {ex.Message}");
            return FallbackAccentColor;
        }
    }

    private static Color AdjustForBorder(Color color, bool darkMode)
    {
        return darkMode
            ? Mix(color, Colors.White, 0.24)
            : Mix(color, Colors.Black, 0.18);
    }

    private static Color Mix(Color source, Color target, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        static byte Blend(byte a, byte b, double amount) => (byte)Math.Clamp((int)Math.Round(a + ((b - a) * amount)), 0, 255);
        return Color.FromArgb(
            source.A,
            Blend(source.R, target.R, amount),
            Blend(source.G, target.G, amount),
            Blend(source.B, target.B, amount));
    }

    private static string ToHex(Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        GC.SuppressFinalize(this);
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
