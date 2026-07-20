using Avalonia.Media;

namespace FujinTerm.Services;

// Enumerates the OS-installed font families and keeps only the fixed-pitch ones
// so the terminal font picker can offer system fonts alongside the two bundled
// faces. A proportional font would mangle the fixed CP437 cell grid, so anything
// that isn't monospace is dropped here rather than left for the user to pick and
// regret.
//
// Built once, then cached for the app's lifetime — probing every installed
// family is too costly to repeat each time Settings opens. Detection reads glyph
// advances straight off the font's metric table (no FormattedText layout pass),
// which is both far cheaper and safe to run off the UI thread, so Warm() can
// pre-build the list on a background thread at startup and the first Settings
// open pays nothing.
public static class MonospaceFontCatalog
{
    private static readonly object _gate = new();
    private static IReadOnlyList<string>? _families;

    // Family names of every installed monospace font, sorted case-insensitively
    // and de-duplicated across weights.
    public static IReadOnlyList<string> Families
    {
        get
        {
            if (_families is { } cached) return cached;
            lock (_gate)
                return _families ??= Build();
        }
    }

    // Pre-build the catalogue off the UI thread so opening Settings doesn't stall
    // on the first enumeration. Safe to call before any window exists — it only
    // touches the font subsystem. Failures are swallowed: the lazy getter simply
    // rebuilds on demand if the warm-up couldn't complete.
    public static void Warm()
    {
        try { _ = Families; }
        catch { /* best-effort pre-warm; the picker rebuilds lazily if this fails */ }
    }

    private static IReadOnlyList<string> Build()
    {
        SortedSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (FontFamily family in FontManager.Current.SystemFonts)
        {
            if (string.IsNullOrWhiteSpace(family.Name)) continue;
            if (IsMonospace(family))
                names.Add(family.Name);
        }
        return names.ToArray();
    }

    private static bool IsMonospace(FontFamily family)
    {
        try
        {
            if (!FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out GlyphTypeface? glyph)
                || glyph is null)
                return false;

            // Probe the actual Latin advances rather than trusting the font's
            // own IsFixedPitch flag — that flag is set on emoji/symbol faces
            // (all-uniform but no text glyphs) and on some proportional Nerd Font
            // variants, none of which render BBS text at fixed pitch. Requiring
            // 'i'/'M'/'W'/space to be present and equal keeps only faces that
            // actually behave as a monospace terminal font.
            return AdvancesUniform(glyph);
        }
        catch
        {
            // A family that can't be realised into a glyph typeface (a broken or
            // partial install) simply doesn't make the cut — skip it rather than
            // let one bad font abort the whole enumeration.
            return false;
        }
    }

    private static bool AdvancesUniform(GlyphTypeface glyph)
    {
        double em = glyph.Metrics.DesignEmHeight;
        if (em <= 0) return false;

        // Advances come back in font design units, independent of point size, so
        // the ratio between them is all that matters. A narrow 'i', a wide
        // 'M'/'W', and a space all advancing the same width is the fixed-pitch
        // signature. Any missing probe glyph (advance <= 0) fails it.
        double[] advances =
        {
            Advance(glyph, 'i'),
            Advance(glyph, 'M'),
            Advance(glyph, 'W'),
            Advance(glyph, ' '),
        };
        return WidthsUniform(advances, epsilon: em * 0.02);
    }

    private static double Advance(GlyphTypeface glyph, char ch) =>
        glyph.CharacterToGlyphMap.TryGetGlyph(ch, out ushort g) && g != 0
            && glyph.TryGetHorizontalGlyphAdvance(g, out ushort adv)
                ? adv
                : -1;

    // A fixed-pitch face advances its probe glyphs by the same width. Compare
    // within a tolerance (a small fraction of the em) to absorb the odd
    // design-unit rounding; any zero or negative width fails it outright, since a
    // face that can't measure a probe glyph isn't one to offer.
    internal static bool WidthsUniform(IReadOnlyList<double> widths, double epsilon = 0.5)
    {
        if (widths.Count == 0) return false;
        double first = widths[0];
        if (first <= 0) return false;
        foreach (double w in widths)
        {
            if (w <= 0) return false;
            if (Math.Abs(w - first) > epsilon) return false;
        }
        return true;
    }
}
