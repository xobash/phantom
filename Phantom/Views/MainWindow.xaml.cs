using System.Collections.Specialized;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Phantom.Services;
using Phantom.ViewModels;

namespace Phantom.Views;

public partial class MainWindow : Window
{
    private static readonly Regex ProgressPercentRegex = new(@"(?<!\d)(100|[1-9]?\d(?:\.\d+)?)\s*%", RegexOptions.Compiled);
    private static readonly Regex FailureTokenRegex = new(@"\b(false|failed|failure|error|exception|blocked|denied|timed\s*out|cancelled|missing|not\s+applied|not\s+installed|managed\s*/\s*restricted|no\s+package\s+found)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SuccessTokenRegex = new(@"\b(true|success|succeeded|completed\s+successfully|applied|installed|enabled|present|passed|done)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
            Foreground = GetStreamBrush(item)
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

    private static Brush GetStreamBrush(Phantom.Models.PowerShellOutputEvent item)
    {
        var darkTheme = IsDarkConsoleTheme();
        var stream = item.Stream ?? string.Empty;
        var classification = ClassifyLine(item);

        if (classification == ConsoleLineClassification.Command)
        {
            return darkTheme ? Brushes.White : Brushes.Black;
        }

        if (classification == ConsoleLineClassification.Success)
        {
            return darkTheme ? Brushes.LightGreen : Brushes.ForestGreen;
        }

        if (classification == ConsoleLineClassification.Failure)
        {
            return darkTheme ? Brushes.OrangeRed : Brushes.Firebrick;
        }

        if (classification == ConsoleLineClassification.Warning)
        {
            return darkTheme ? Brushes.Gold : Brushes.DarkGoldenrod;
        }

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
            "command" => darkTheme ? Brushes.White : Brushes.Black,
            "progress" => darkTheme ? Brushes.MediumAquamarine : Brushes.SeaGreen,
            "output" => darkTheme ? Brushes.LightSteelBlue : Brushes.SlateGray,
            "trace" => darkTheme ? Brushes.SlateGray : Brushes.Gray,
            _ => darkTheme ? Brushes.LightSteelBlue : Brushes.SlateGray,
        };
    }

    private static Brush GetMessageBrush(Phantom.Models.PowerShellOutputEvent item)
    {
        var darkTheme = IsDarkConsoleTheme();
        switch (ClassifyLine(item))
        {
            case ConsoleLineClassification.Command:
                return darkTheme ? Brushes.White : Brushes.Black;
            case ConsoleLineClassification.Success:
                return darkTheme ? Brushes.LightGreen : Brushes.ForestGreen;
            case ConsoleLineClassification.Failure:
                return darkTheme ? Brushes.OrangeRed : Brushes.Firebrick;
            case ConsoleLineClassification.Warning:
                return darkTheme ? Brushes.Gold : Brushes.DarkGoldenrod;
            default:
            {
                var stream = item.Stream ?? string.Empty;
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
        }
    }

    private static Brush GetProgressBrush()
    {
        return IsDarkConsoleTheme() ? Brushes.MediumAquamarine : Brushes.SeaGreen;
    }

    private static ConsoleLineClassification ClassifyLine(Phantom.Models.PowerShellOutputEvent item)
    {
        var stream = (item.Stream ?? string.Empty).Trim();
        var message = (item.Text ?? string.Empty).Trim();
        if (stream.Equals("Command", StringComparison.OrdinalIgnoreCase))
        {
            return ConsoleLineClassification.Command;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return stream.Equals("Warning", StringComparison.OrdinalIgnoreCase)
                ? ConsoleLineClassification.Warning
                : ConsoleLineClassification.Neutral;
        }

        if (stream.Equals("Error", StringComparison.OrdinalIgnoreCase) ||
            stream.Equals("Fatal", StringComparison.OrdinalIgnoreCase) ||
            stream.Equals("StartupError", StringComparison.OrdinalIgnoreCase) ||
            stream.Equals("DispatcherUnhandled", StringComparison.OrdinalIgnoreCase) ||
            stream.Equals("UnobservedTaskException", StringComparison.OrdinalIgnoreCase))
        {
            return ConsoleLineClassification.Failure;
        }

        if (FailureTokenRegex.IsMatch(message))
        {
            return ConsoleLineClassification.Failure;
        }

        var status = OperationStatusParser.Parse(message);
        if (status == OperationDetectState.NotApplied &&
            (message.Contains("Not Applied", StringComparison.OrdinalIgnoreCase) ||
             message.Equals("0", StringComparison.OrdinalIgnoreCase) ||
             message.Equals("False", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("Not Installed", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("Disabled", StringComparison.OrdinalIgnoreCase)))
        {
            return ConsoleLineClassification.Failure;
        }

        if (status == OperationDetectState.Applied ||
            SuccessTokenRegex.IsMatch(message))
        {
            return ConsoleLineClassification.Success;
        }

        if (stream.Equals("Warning", StringComparison.OrdinalIgnoreCase))
        {
            return ConsoleLineClassification.Warning;
        }

        return ConsoleLineClassification.Neutral;
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

    private enum ConsoleLineClassification
    {
        Neutral,
        Command,
        Success,
        Failure,
        Warning
    }
}
