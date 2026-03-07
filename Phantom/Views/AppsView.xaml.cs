using System.Windows;
using System.Windows.Controls;

namespace Phantom.Views;

public partial class AppsView : UserControl
{
    public AppsView()
    {
        InitializeComponent();
    }

    private void OpenRowMenu(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.ContextMenu is ContextMenu menu)
        {
            menu.PlacementTarget = element;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }
}
