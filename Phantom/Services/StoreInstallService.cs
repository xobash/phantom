namespace Phantom.Services;

public enum StorePackageManager
{
    Winget,
    Scoop,
    Chocolatey,
    Pip,
    Npm,
    DotNetTool,
    PowerShellGallery
}

public sealed class StoreManagerAvailability
{
    public required PackageManagerResolution Winget { get; init; }
    public required PackageManagerResolution Scoop { get; init; }
    public required PackageManagerResolution Chocolatey { get; init; }
    public required PackageManagerResolution Pip { get; init; }
    public required PackageManagerResolution Npm { get; init; }
    public required PackageManagerResolution DotNetTool { get; init; }
    public required PackageManagerResolution PowerShellGallery { get; init; }

    public IEnumerable<(StorePackageManager Manager, PackageManagerResolution Resolution)> All()
    {
        yield return (StorePackageManager.Winget, Winget);
        yield return (StorePackageManager.Scoop, Scoop);
        yield return (StorePackageManager.Chocolatey, Chocolatey);
        yield return (StorePackageManager.Pip, Pip);
        yield return (StorePackageManager.Npm, Npm);
        yield return (StorePackageManager.DotNetTool, DotNetTool);
        yield return (StorePackageManager.PowerShellGallery, PowerShellGallery);
    }
}

public sealed class StoreInstallService
{
    private readonly PackageManagerResolver _resolver;

    public StoreInstallService(PackageManagerResolver resolver)
    {
        _resolver = resolver;
    }

    public async Task<StoreManagerAvailability> GetManagerAvailabilityAsync(CancellationToken cancellationToken)
    {
        var winget = await _resolver.ResolveWingetAsync(cancellationToken).ConfigureAwait(false);
        var scoop = await _resolver.ResolveScoopAsync(cancellationToken).ConfigureAwait(false);
        var choco = await _resolver.ResolveChocolateyAsync(cancellationToken).ConfigureAwait(false);
        var pip = await _resolver.ResolvePipAsync(cancellationToken).ConfigureAwait(false);
        var npm = await _resolver.ResolveNpmAsync(cancellationToken).ConfigureAwait(false);
        var dotnet = await _resolver.ResolveDotNetToolAsync(cancellationToken).ConfigureAwait(false);
        var psGallery = await _resolver.ResolvePowerShellGalleryAsync(cancellationToken).ConfigureAwait(false);
        return new StoreManagerAvailability
        {
            Winget = winget,
            Scoop = scoop,
            Chocolatey = choco,
            Pip = pip,
            Npm = npm,
            DotNetTool = dotnet,
            PowerShellGallery = psGallery
        };
    }
}
