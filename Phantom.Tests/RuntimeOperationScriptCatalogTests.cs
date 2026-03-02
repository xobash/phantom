using Phantom.Services;

namespace Phantom.Tests;

public sealed class RuntimeOperationScriptCatalogTests
{
    [Fact]
    public void BuildOoShutUp10RunScript_UsesPinnedIntegrityAndSecureExecutionPath()
    {
        var script = RuntimeOperationScriptCatalog.BuildOoShutUp10RunScript();

        Assert.Contains("$env:ProgramData", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$env:TEMP", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Get-FileHash", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Get-AuthenticodeSignature", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SHA256", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Start-Process -FilePath 'OOSU10.exe'", script, StringComparison.OrdinalIgnoreCase);
    }
}
