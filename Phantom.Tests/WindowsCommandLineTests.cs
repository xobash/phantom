using Phantom.Services;

namespace Phantom.Tests;

public sealed class WindowsCommandLineTests
{
    [Fact]
    public void QuoteArgument_ReturnsUnchangedValue_WhenNoQuotingIsRequired()
    {
        var quoted = WindowsCommandLine.QuoteArgument(@"C:\Tools\phantom.exe");
        Assert.Equal(@"C:\Tools\phantom.exe", quoted);
    }

    [Fact]
    public void QuoteArgument_EscapesTrailingBackslash_WhenValueNeedsQuotes()
    {
        var quoted = WindowsCommandLine.QuoteArgument(@"C:\Program Files\Phantom\");
        Assert.Equal("\"C:\\Program Files\\Phantom\\\\\"", quoted);
    }

    [Fact]
    public void QuoteArgument_EscapesEmbeddedQuotes()
    {
        var quoted = WindowsCommandLine.QuoteArgument("C:\\Program Files\\Phantom \"Admin\"");
        Assert.Equal("\"C:\\Program Files\\Phantom \\\"Admin\\\"\"", quoted);
    }
}
