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
            Foreground = Brushes.LightSteelBlue
        });

        paragraph.Inlines.Add(new Run(item.Text)
        {
            Foreground = item.Stream.Equals("Error", StringComparison.OrdinalIgnoreCase)
                ? Brushes.OrangeRed
                : item.Stream.Equals("Warning", StringComparison.OrdinalIgnoreCase)
                    ? Brushes.Gold
                    : Brushes.WhiteSmoke
        });

        ConsoleTextBox.Document.Blocks.Add(paragraph);

        if (ConsoleTextBox.Document.Blocks.Count > 5000)
        {
            ConsoleTextBox.Document.Blocks.Remove(ConsoleTextBox.Document.Blocks.FirstBlock);
        }
    }
}
