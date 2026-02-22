using System.Windows;
using System.Windows.Controls;
using Phantom.Models;
using Phantom.ViewModels;

namespace Phantom.Views;

public partial class TweaksView : UserControl
{
    public TweaksView()
    {
        InitializeComponent();
    }

    private void OnTweakToggleClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TweaksViewModel viewModel)
        {
            return;
        }

        if (sender is not CheckBox checkBox || checkBox.DataContext is not TweakDefinition tweak)
        {
            return;
        }

        viewModel.ApplyToggleFromUi(tweak, checkBox.IsChecked == true);
    }
}
