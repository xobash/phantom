using System.Windows;

namespace Phantom.Views;

public partial class RebootPromptDialog : Window
{
    public RebootPromptDialog()
    {
        InitializeComponent();
    }

    public bool RebootNow { get; private set; }

    private void RebootNow_Click(object sender, RoutedEventArgs e)
    {
        RebootNow = true;
        DialogResult = true;
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        RebootNow = false;
        DialogResult = true;
        Close();
    }
}
