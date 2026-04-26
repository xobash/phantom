using System.Threading;
using System.Windows.Controls;
using Phantom.ViewModels;

namespace Phantom.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            await viewModel.EnsureLogsLoadedAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }
}
