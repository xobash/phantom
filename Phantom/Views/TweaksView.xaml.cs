using System.Windows;
using System.Windows.Controls;
using System.Threading;
using Phantom.Models;
using Phantom.ViewModels;

namespace Phantom.Views;

public partial class TweaksView : UserControl
{
    public TweaksView()
    {
        InitializeComponent();
    }

    private async void OnTweakToggleClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TweaksViewModel viewModel)
        {
            return;
        }

        if (sender is not CheckBox checkBox || checkBox.DataContext is not TweakDefinition tweak)
        {
            return;
        }

        try
        {
            using var uiCancellation = new CancellationTokenSource();
            await viewModel.ApplyToggleFromUiAsync(tweak, checkBox.IsChecked == true, uiCancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Phantom Command Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
