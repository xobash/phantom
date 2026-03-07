namespace Phantom.ViewModels;

public interface ISectionViewModel
{
    string Title { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
}
