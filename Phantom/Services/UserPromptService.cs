using Phantom.Views;
using System.Windows;

namespace Phantom.Services;

public interface IUserPromptService
{
    Task<bool> ConfirmDangerousAsync(string promptText);
    Task<bool> PromptRebootAsync();
}

public sealed class UserPromptService : IUserPromptService
{
    public async Task<bool> ConfirmDangerousAsync(string promptText)
    {
        if (Application.Current is null)
        {
            return false;
        }

        return await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new TextInputDialog("Dangerous Operation", promptText);
            AttachOwner(dialog);

            var ok = dialog.ShowDialog() == true;
            return ok && string.Equals(dialog.InputText?.Trim(), "Y", StringComparison.OrdinalIgnoreCase);
        });
    }

    public async Task<bool> PromptRebootAsync()
    {
        if (Application.Current is null)
        {
            return false;
        }

        return await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new RebootPromptDialog();
            AttachOwner(dialog);
            return dialog.ShowDialog() == true && dialog.RebootNow;
        });
    }

    private static void AttachOwner(Window dialog)
    {
        if (Application.Current is null)
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        var owner = Application.Current.Windows
            .OfType<Window>()
            .FirstOrDefault(w => w.IsVisible && w.IsActive)
            ?? Application.Current.MainWindow;

        if (owner is not null && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }
}
