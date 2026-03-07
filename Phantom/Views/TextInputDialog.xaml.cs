using System.Windows;
using Phantom.ViewModels;

namespace Phantom.Views;

public partial class TextInputDialog : Window
{
    private readonly TextInputDialogViewModel _vm;

    public TextInputDialog(string title, string prompt)
    {
        InitializeComponent();
        Title = title;
        _vm = new TextInputDialogViewModel { PromptText = prompt };
        DataContext = _vm;
    }

    public string InputText => _vm.InputText;

    private void Proceed_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
