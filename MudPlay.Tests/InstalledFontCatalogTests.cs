using MudPlay.Services;
using SkiaSharp;
using Xunit;

namespace MudPlay.Tests;

// The monospace filter's decision core: does a set of measured probe-glyph
// advances read as fixed-pitch? Enumerating installed families depends on the
// machine, so the width comparison is pinned on its own, and the Skia probe is
// pinned against the fonts the app bundles.
public sealed class InstalledFontCatalogTests
{
    private static SKTypeface BundledFont(string file)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MudPlay.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        SKTypeface? face = SKTypeface.FromFile(Path.Combine(dir!.FullName, "Assets", "Fonts", file));
        Assert.NotNull(face);
        return face!;
    }

    [Theory]
    [InlineData("JetBrainsMono-Regular.ttf")]
    [InlineData("Mx437_IBM_VGA_8x16.ttf")]
    public void FixedPitchFont_ReadsAsFixedPitch(string file)
    {
        using SKTypeface face = BundledFont(file);
        Assert.True(InstalledFontCatalog.IsFixedPitch(face));
    }

    [Fact]
    public void ProportionalFont_DoesNotReadAsFixedPitch()
    {
        using SKTypeface face = BundledFont("IBMPlexSans-Regular.ttf");
        Assert.False(InstalledFontCatalog.IsFixedPitch(face));
    }

    // Advances in font design units with hinting off, against the client's
    // tolerance of 2% of the em (a 1000-unit em here).
    private const double Em = 1000;
    private static bool Uniform(params double[] widths) =>
        InstalledFontCatalog.WidthsUniform(widths, epsilon: Em * 0.02);

    [Fact]
    public void IdenticalWidths_AreUniform()
    {
        Assert.True(Uniform(600, 600, 600, 600));
    }

    [Fact]
    public void DesignUnitRounding_InsideTheTolerance_IsUniform()
    {
        Assert.True(Uniform(600, 600, 612, 600));
    }

    [Fact]
    public void ASpreadPastTheTolerance_IsNotUniform()
    {
        Assert.False(Uniform(600, 612, 621));
    }

    [Fact]
    public void ProportionalSpread_IsNotUniform()
    {
        // A narrow 'i' next to a wide 'W' — the proportional-font signature.
        Assert.False(Uniform(278, 600, 944, 300));
    }

    [Fact]
    public void AnyZeroWidth_FailsOutright()
    {
        Assert.False(Uniform(600, 600, 0, 600));
    }

    [Fact]
    public void LeadingZeroWidth_FailsOutright()
    {
        Assert.False(Uniform(0, 600, 600, 600));
    }

    [Fact]
    public void AMissingGlyph_FailsOutright()
    {
        // A face without a probe glyph measures it as -1.
        Assert.False(Uniform(600, -1, 600, 600));
    }

    [Fact]
    public void EmptySet_IsNotUniform()
    {
        Assert.False(Uniform());
    }

    // ----- The saved catalogue -----------------------------------------
    //
    // A client reads the lists from disk instead of opening every installed font,
    // so the stamp is what keeps a stale catalogue from being believed.

    [Fact]
    public void Stamp_IsTheSameWhateverOrderTheFamiliesAreListedIn()
    {
        Assert.Equal(
            InstalledFontCatalog.Stamp(new[] { "Hack", "Noto Sans", "DejaVu Sans Mono" }),
            InstalledFontCatalog.Stamp(new[] { "DejaVu Sans Mono", "Hack", "Noto Sans" }));
    }

    [Theory]
    [InlineData("Hack", "Noto Sans", "Fira Code")]      // one installed
    [InlineData("Hack")]                                // one removed
    [InlineData("Hack", "Noto sans")]                   // a different family by its letters' case
    public void Stamp_ChangesWithTheInstalledFamilies(params string[] now)
    {
        Assert.NotEqual(InstalledFontCatalog.Stamp(new[] { "Hack", "Noto Sans" }), InstalledFontCatalog.Stamp(now));
    }

    [Fact]
    public void SavedCatalogue_IsReadBackForItsOwnStamp_AndForNoOther()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mudplay-font-catalogue-{Guid.NewGuid():N}.json");
        try
        {
            Assert.Null(InstalledFontCatalog.TryRead(path, "A"));   // nothing saved yet

            InstalledFontCatalog.Save(path, "A", (new[] { "Hack" }, new[] { "Hack", "Noto Sans" }));

            var read = InstalledFontCatalog.TryRead(path, "A");
            Assert.NotNull(read);
            Assert.Equal(new[] { "Hack" }, read!.Value.Monospace);
            Assert.Equal(new[] { "Hack", "Noto Sans" }, read.Value.Text);
            Assert.Null(InstalledFontCatalog.TryRead(path, "B"));   // the installed fonts changed
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("{\"Stamp\":\"A\"}")]                               // the lists are missing
    public void SavedCatalogue_ThatCannotBeRead_IsTakenAsNotThere(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"mudplay-font-catalogue-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, content);
            Assert.Null(InstalledFontCatalog.TryRead(path, "A"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
