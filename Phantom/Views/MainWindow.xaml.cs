using System.Collections.Specialized;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Phantom.ViewModels;

namespace Phantom.Views;

public partial class MainWindow : Window
{
    private static readonly Regex ProgressPercentRegex = new(@"(?<!\d)(100|[1-9]?\d(?:\.\d+)?)\s*%", RegexOptions.Compiled);

    private MainViewModel? _viewModel;
    private string _activeConsoleFilter = "All logs";

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => RebuildConsole();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ConsoleMessages.CollectionChanged -= OnConsoleCollectionChanged;
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ConsoleMessages.CollectionChanged += OnConsoleCollectionChanged;
            RebuildConsole();
        }
    }

    private void OnConsoleCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null)
        {
            return;
        }

        Dispatcher.Invoke(() =>
        {
            foreach (var item in e.NewItems.OfType<Phantom.Models.PowerShellOutputEvent>())
            {
                if (!ShouldDisplay(item))
                {
                    continue;
                }

                AppendLine(item);
            }

            ConsoleTextBox.ScrollToEnd();
        });
    }

    private void RebuildConsole()
    {
        if (_viewModel is null)
        {
            return;
        }

        ConsoleTextBox.Document.Blocks.Clear();
        foreach (var item in _viewModel.ConsoleMessages)
        {
            if (!ShouldDisplay(item))
            {
                continue;
            }

            AppendLine(item);
        }

        ConsoleTextBox.ScrollToEnd();
    }

    private bool ShouldDisplay(Phantom.Models.PowerShellOutputEvent item)
    {
        var stream = (item.Stream ?? string.Empty).ToLowerInvariant();
        var text = (item.Text ?? string.Empty).ToLowerInvariant();
        var isErrorLike =
            stream is "error" or "fatal" or "startuperror" or "dispatcherunhandled" or "unobservedtaskexception" ||
            text.Contains("exception") ||
            text.Contains(" error") ||
            text.StartsWith("error", StringComparison.Ordinal);
        var isWarningLike = stream == "warning" || text.Contains("warning");

        return _activeConsoleFilter switch
        {
            "No Trace" => stream != "trace",
            "Warnings+" => isWarningLike || isErrorLike,
            "Errors only" => isErrorLike,
            _ => true
        };
    }

    private void OnConsoleFilterChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ConsoleFilterCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item)
        {
            _activeConsoleFilter = item.Content?.ToString() ?? "All logs";
        }
        else
        {
            _activeConsoleFilter = "All logs";
        }

        RebuildConsole();
    }

    private void AppendLine(Phantom.Models.PowerShellOutputEvent item)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0),
            LineHeight = 14
        };

        paragraph.Inlines.Add(new Run($"[{item.Timestamp:HH:mm:ss}] [{item.Stream}] ")
        {
            Foreground = GetStreamBrush(item.Stream)
        });

        paragraph.Inlines.Add(new Run(item.Text)
        {
            Foreground = GetMessageBrush(item)
        });

        if (TryExtractProgressPercent(item.Text, out var progressPercent))
        {
            paragraph.Inlines.Add(new Run($"  {BuildProgressBar(progressPercent)}")
            {
                Foreground = GetProgressBrush()
            });
        }

        ConsoleTextBox.Document.Blocks.Add(paragraph);

        if (ConsoleTextBox.Document.Blocks.Count > 5000)
        {
            ConsoleTextBox.Document.Blocks.Remove(ConsoleTextBox.Document.Blocks.FirstBlock);
        }
    }

    private static Brush GetStreamBrush(string stream)
    {
        var darkTheme = IsDarkConsoleTheme();
        if (string.IsNullOrWhiteSpace(stream))
        {
            return darkTheme ? Brushes.LightSteelBlue : Brushes.SlateGray;
        }

        return stream.ToLowerInvariant() switch
        {
            "error" or "fatal" or "startuperror" or "dispatcherunhandled" or "unobservedtaskexception" => darkTheme ? Brushes.OrangeRed : Brushes.Firebrick,
            "warning" => darkTheme ? Brushes.Gold : Brushes.DarkGoldenrod,
            "info" => darkTheme ? Brushes.LightSkyBlue : Brushes.SteelBlue,
            "query" => darkTheme ? Brushes.DeepSkyBlue : Brushes.Teal,
            "command" => darkTheme ? Brushes.Plum : Brushes.MediumPurple,
            "progress" => darkTheme ? Brushes.MediumAquamarine : Brushes.SeaGreen,
            "output" => darkTheme ? Brushes.LightSteelBlue : Brushes.SlateGray,
            "trace" => darkTheme ? Brushes.SlateGray : Brushes.Gray,
            _ => darkTheme ? Brushes.LightSteelBlue : Brushes.SlateGray,
        };
    }

    private static Brush GetMessageBrush(Phantom.Models.PowerShellOutputEvent item)
    {
        var darkTheme = IsDarkConsoleTheme();
        var stream = item.Stream ?? string.Empty;
        var text = item.Text ?? string.Empty;
        var normalized = text.ToLowerInvariant();

        if (stream.Equals("Error", StringComparison.OrdinalIgnoreCase) ||
            stream.Equals("Fatal", StringComparison.OrdinalIgnoreCase) ||
            stream.Equals("StartupError", StringComparison.OrdinalIgnoreCase) ||
            stream.Equals("DispatcherUnhandled", StringComparison.OrdinalIgnoreCase) ||
            stream.Equals("UnobservedTaskException", StringComparison.OrdinalIgnoreCase))
        {
            return darkTheme ? Brushes.OrangeRed : Brushes.Firebrick;
        }

        if (normalized.Contains(" failed") ||
            normalized.Contains("exception") ||
            normalized.Contains("error"))
        {
            return darkTheme ? Brushes.OrangeRed : Brushes.Firebrick;
        }

        if (stream.Equals("Warning", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("cancelled") ||
            normalized.Contains("warning"))
        {
            return darkTheme ? Brushes.Gold : Brushes.DarkGoldenrod;
        }

        if (IsSuccessMessage(stream, normalized))
        {
            return darkTheme ? Brushes.LightGreen : Brushes.ForestGreen;
        }

        if (stream.Equals("Query", StringComparison.OrdinalIgnoreCase))
        {
            return darkTheme ? Brushes.LightSkyBlue : Brushes.SteelBlue;
        }

        if (stream.Equals("Progress", StringComparison.OrdinalIgnoreCase))
        {
            return darkTheme ? Brushes.MediumAquamarine : Brushes.SeaGreen;
        }

        if (stream.Equals("Trace", StringComparison.OrdinalIgnoreCase))
        {
            return darkTheme ? Brushes.Gainsboro : Brushes.DimGray;
        }

        return darkTheme ? Brushes.WhiteSmoke : Brushes.Black;
    }

    private static Brush GetProgressBrush()
    {
        return IsDarkConsoleTheme() ? Brushes.MediumAquamarine : Brushes.SeaGreen;
    }

    private static bool IsSuccessMessage(string stream, string normalizedMessage)
    {
        if (string.IsNullOrWhiteSpace(normalizedMessage))
        {
            return false;
        }

        if (normalizedMessage.Contains("startup completed") ||
            normalizedMessage.Contains("initialization completed") ||
            normalizedMessage.Contains("completed. success=true") ||
            normalizedMessage.Contains("passed.") ||
            normalizedMessage.Contains("launched") ||
            normalizedMessage.Contains("wired and ready") ||
            normalizedMessage.Contains("present"))
        {
            return true;
        }

        return stream.Equals("Info", StringComparison.OrdinalIgnoreCase) &&
               (normalizedMessage.Contains("completed") ||
                normalizedMessage.Contains("applied") ||
                normalizedMessage.Contains("installed") ||
                normalizedMessage.Contains("exported") ||
                normalizedMessage.Contains("imported"));
    }

    private static bool TryExtractProgressPercent(string text, out int percent)
    {
        percent = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = ProgressPercentRegex.Match(text);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        percent = Math.Clamp((int)Math.Round(parsed, MidpointRounding.AwayFromZero), 0, 100);
        return true;
    }

    private static string BuildProgressBar(int percent)
    {
        const int slots = 10;
        var filled = (int)Math.Round((percent / 100d) * slots, MidpointRounding.AwayFromZero);
        filled = Math.Clamp(filled, 0, slots);
        return $"[{new string('#', filled)}{new string('-', slots - filled)}] {percent}%";
    }

    private static bool IsDarkConsoleTheme()
    {
        if (Application.Current?.Resources["TerminalBackgroundBrush"] is not SolidColorBrush brush)
        {
            return true;
        }

        var color = brush.Color;
        var luminance = ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255d;
        return luminance < 0.55;
    }
}
