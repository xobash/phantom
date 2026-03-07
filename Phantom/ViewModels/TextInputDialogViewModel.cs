namespace Phantom.ViewModels;

public sealed class TextInputDialogViewModel : ObservableObject
{
    private string _promptText = string.Empty;
    private string _inputText = string.Empty;

    public string PromptText
    {
        get => _promptText;
        set => SetProperty(ref _promptText, value);
    }

    public string InputText
    {
        get => _inputText;
        set => SetProperty(ref _inputText, value);
    }
}
