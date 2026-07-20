using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

// The monospace filter's decision core: does a set of measured probe-glyph
// advances read as fixed-pitch? Enumeration itself needs a live Avalonia font
// stack, so only the pure width comparison is pinned here — the part with an
// epsilon tolerance and a zero-width guard worth getting right.
public sealed class MonospaceFontCatalogTests
{
    [Fact]
    public void IdenticalWidths_AreUniform()
    {
        Assert.True(MonospaceFontCatalog.WidthsUniform(new[] { 9.0, 9.0, 9.0, 9.0 }));
    }

    [Fact]
    public void WithinHalfPixel_IsUniform()
    {
        // Rasteriser rounding jitter under the tolerance still counts as fixed pitch.
        Assert.True(MonospaceFontCatalog.WidthsUniform(new[] { 9.0, 9.3, 8.7, 9.1 }));
    }

    [Fact]
    public void ProportionalSpread_IsNotUniform()
    {
        // A narrow 'i' next to a wide 'W' — the proportional-font signature.
        Assert.False(MonospaceFontCatalog.WidthsUniform(new[] { 4.0, 9.0, 12.0, 4.5 }));
    }

    [Fact]
    public void AnyZeroWidth_FailsOutright()
    {
        Assert.False(MonospaceFontCatalog.WidthsUniform(new[] { 9.0, 9.0, 0.0, 9.0 }));
    }

    [Fact]
    public void LeadingZeroWidth_FailsOutright()
    {
        Assert.False(MonospaceFontCatalog.WidthsUniform(new[] { 0.0, 9.0, 9.0, 9.0 }));
    }

    [Fact]
    public void EmptySet_IsNotUniform()
    {
        Assert.False(MonospaceFontCatalog.WidthsUniform(System.Array.Empty<double>()));
    }
}
