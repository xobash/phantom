using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Phantom.ViewModels;

namespace Phantom.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        if (IsDescendantOfButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (DataContext is not HomeViewModel vm)
        {
            return;
        }

        var title = element.Tag as string;
        if (vm.RefreshCardCommand.CanExecute(title))
        {
            vm.RefreshCardCommand.Execute(title);
            e.Handled = true;
        }
    }

    private static bool IsDescendantOfButton(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ButtonBase)
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }
}
