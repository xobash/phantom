using Phantom.Services;

namespace Phantom.Tests;

public sealed class PowerShellInputSanitizerTests
{
    [Fact]
    public void EnsureServiceName_ReturnsTrimmedName_ForValidInput()
    {
        var result = PowerShellInputSanitizer.EnsureServiceName("  wuauserv  ", "test");

        Assert.Equal("wuauserv", result);
    }

    [Fact]
    public void EnsureServiceName_Throws_ForEmptyInput()
    {
        var ex = Assert.Throws<ArgumentException>(() => PowerShellInputSanitizer.EnsureServiceName("   ", "test"));

        Assert.Contains("service name is required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureServiceName_Throws_ForInvalidCharacters()
    {
        var ex = Assert.Throws<ArgumentException>(() => PowerShellInputSanitizer.EnsureServiceName("wuauserv;Remove-Item", "test"));

        Assert.Contains("invalid service name", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureServiceName_Throws_ForOverlyLongName()
    {
        var tooLong = "a" + new string('b', 128);
        var ex = Assert.Throws<ArgumentException>(() => PowerShellInputSanitizer.EnsureServiceName(tooLong, "test"));

        Assert.Contains("invalid service name", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
