using Phantom.Services;

namespace Phantom.Tests;

public sealed class HomeCardIconCatalogTests
{
    [Fact]
    public void ValidateRequiredMappings_DoesNotThrow()
    {
        HomeCardIconCatalog.ValidateRequiredMappings();
    }

    [Fact]
    public void EveryRequiredHomeCardAndKpiTitle_HasExplicitGlyphMapping()
    {
        foreach (var title in HomeCardIconCatalog.RequiredTopCardTitles)
        {
            var glyph = HomeCardIconCatalog.GetTopCardGlyph(title);
            Assert.False(string.IsNullOrWhiteSpace(glyph));
        }

        foreach (var title in HomeCardIconCatalog.RequiredKpiTitles)
        {
            var glyph = HomeCardIconCatalog.GetKpiGlyph(title);
            Assert.False(string.IsNullOrWhiteSpace(glyph));
        }
    }
}
