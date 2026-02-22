using System.Collections.Specialized;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Phantom.ViewModels;

namespace Phantom.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;

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
            AppendLine(item);
        }

        ConsoleTextBox.ScrollToEnd();
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

        ConsoleTextBox.Document.Blocks.Add(paragraph);

        if (ConsoleTextBox.Document.Blocks.Count > 5000)
        {
            ConsoleTextBox.Document.Blocks.Remove(ConsoleTextBox.Document.Blocks.FirstBlock);
        }
    }

    private static Brush GetStreamBrush(string stream)
    {
        if (string.IsNullOrWhiteSpace(stream))
        {
            return Brushes.LightSteelBlue;
        }

        return stream.ToLowerInvariant() switch
        {
            "error" or "fatal" or "startuperror" or "dispatcherunhandled" or "unobservedtaskexception" => Brushes.OrangeRed,
            "warning" => Brushes.Gold,
            "info" => Brushes.LightSkyBlue,
            "query" => Brushes.DeepSkyBlue,
            "command" => Brushes.Plum,
            "output" => Brushes.LightSteelBlue,
            "trace" => Brushes.SlateGray,
            _ => Brushes.LightSteelBlue,
        };
    }

    private static Brush GetMessageBrush(Phantom.Models.PowerShellOutputEvent item)
    {
        var stream = item.Stream ?? string.Empty;
        var text = item.Text ?? string.Empty;
        var normalized = text.ToLowerInvariant();

        if (stream.Equals("Error", StringComparison.OrdinalIgnoreCase) ||
            stream.Equals("Fatal", StringComparison.OrdinalIgnoreCase) ||
            stream.Equals("StartupError", StringComparison.OrdinalIgnoreCase) ||
            stream.Equals("DispatcherUnhandled", StringComparison.OrdinalIgnoreCase) ||
            stream.Equals("UnobservedTaskException", StringComparison.OrdinalIgnoreCase))
        {
            return Brushes.OrangeRed;
        }

        if (normalized.Contains(" failed") ||
            normalized.Contains("exception") ||
            normalized.Contains("error"))
        {
            return Brushes.OrangeRed;
        }

        if (stream.Equals("Warning", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("cancelled") ||
            normalized.Contains("warning"))
        {
            return Brushes.Gold;
        }

        if (IsSuccessMessage(stream, normalized))
        {
            return Brushes.LightGreen;
        }

        if (stream.Equals("Query", StringComparison.OrdinalIgnoreCase))
        {
            return Brushes.LightSkyBlue;
        }

        if (stream.Equals("Trace", StringComparison.OrdinalIgnoreCase))
        {
            return Brushes.Gainsboro;
        }

        return Brushes.WhiteSmoke;
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
}
