using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace MudPlay.Services;

// Enumerates the OS-installed font families for the font pickers, in two lists
// built by one scan:
//   Monospace — the fixed-pitch ones, for the terminal (and anything drawn on its
//     cell grid). A proportional font would mangle the fixed CP437 grid, so
//     anything that isn't monospace is dropped rather than left for the user to
//     pick and regret.
//   Text — every installed family, for the Conversation window. Its rows are
//     plain wrapped text with no grid and no ANSI art to keep aligned, so nothing
//     is ruled out; a letter the chosen face lacks falls back to another font.
//
// Telling the two apart means opening every installed family, and Skia keeps
// each face it has opened for the life of the process, whatever is disposed or
// purged afterwards: on a machine with 687 families the scan left about 80 MB of
// native memory and 677 mapped font files in every client, for two pickers most
// sessions never open (measured 2026-10-12). So the client does not scan. Listing
// the family names opens nothing; a stamp of them keys a catalogue saved on disk,
// and when the installed fonts no longer match it, a copy of the program started
// with ProbeArgument scans, saves the catalogue and exits, taking that memory
// with it. Only when that can't be done is the scan run in this process, and
// outside the client (the XAML previewer) it always is: nothing there
// should start a process or write to the data folder.
//
// Built once, then cached for the app's lifetime. Detection reads glyph advances
// straight from Skia (no glyph typeface, no FormattedText layout pass), which is
// safe to run off the UI thread, so Warm() builds the lists on a background
// thread at startup and the first Settings open pays nothing.
public static class InstalledFontCatalog
{
    // Starts the program as the helper that scans and saves the catalogue.
    public const string ProbeArgument = "--probe-fonts";

    // Raised when what makes a family usable or fixed-pitch changes, so a
    // catalogue saved by the older rule is not believed.
    private const int ProbeVersion = 1;

    // A scan of several hundred families takes a second or two; a helper that
    // has not finished in this long is stuck on a font and is stopped.
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(90);

    private static readonly object _gate = new();
    private static (IReadOnlyList<string> Monospace, IReadOnlyList<string> Text)? _lists;
    private static string? _cataloguePath;

    // Where the lists came from and how long that took, for the program log and
    // the bug report.
    public static string Source { get; private set; } = "not built yet";

    // Family names of every installed monospace font, sorted case-insensitively
    // and de-duplicated across weights.
    public static IReadOnlyList<string> Monospace => Lists.Monospace;

    // Family names of every installed font, monospace included, sorted and
    // de-duplicated the same way.
    public static IReadOnlyList<string> Text => Lists.Text;

    private static (IReadOnlyList<string> Monospace, IReadOnlyList<string> Text) Lists
    {
        get
        {
            if (_lists is { } cached) return cached;
            lock (_gate)
                return _lists ??= Build();
        }
    }

    // The client's entry point says where the catalogue is saved. Until it does,
    // none is read or written and no helper is started.
    public static void UseSavedCatalogue(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _cataloguePath = path;
    }

    // Pre-build the catalogue off the UI thread so opening Settings doesn't stall
    // on the first enumeration. Safe to call before any window exists — it only
    // touches the font subsystem. Failures are swallowed: the lazy getter simply
    // rebuilds on demand if the warm-up couldn't complete.
    public static void Warm()
    {
        try { _ = Lists; }
        catch { /* best-effort pre-warm; the picker rebuilds lazily if this fails */ }
    }

    // The helper's whole run: scan, save, and say by the exit code whether a
    // catalogue for the installed fonts is now on disk.
    public static int RunProbe()
    {
        try
        {
            if (_cataloguePath is not { } path) return 2;
            string[] families = InstalledFamilies();
            Save(path, Stamp(families), Probe(families));
            return 0;
        }
        catch
        {
            // Nothing to report to: the client that started this reads the exit
            // code, and scans for itself.
            return 1;
        }
    }

    // The probe letters, in the order IsFixedPitch reads them.
    private const string ProbeText = "aiMW ";

    private static (IReadOnlyList<string> Monospace, IReadOnlyList<string> Text) Build()
    {
        Stopwatch clock = Stopwatch.StartNew();
        string[] families = InstalledFamilies();
        if (_cataloguePath is not { } path)
        {
            var scanned = Probe(families);
            Source = $"scanned in this process in {clock.ElapsedMilliseconds} ms";
            return scanned;
        }

        string stamp = Stamp(families);
        if (TryRead(path, stamp) is { } saved)
        {
            Source = $"read from the saved catalogue in {clock.ElapsedMilliseconds} ms";
            return saved;
        }

        string? helperFailed = RunHelper();
        if (helperFailed is null && TryRead(path, stamp) is { } rebuilt)
        {
            Source = $"scanned by a helper process in {clock.ElapsedMilliseconds} ms "
                + "(no catalogue was saved for the fonts now installed)";
            return rebuilt;
        }

        var here = Probe(families);
        string kept = "saved for next time";
        try { Save(path, stamp, here); }
        catch (Exception ex) { kept = $"not saved: {ex.Message}"; }
        Source = $"scanned in this process in {clock.ElapsedMilliseconds} ms, and its memory kept: the helper process "
            + $"{helperFailed ?? "left no catalogue for these fonts"}; {kept}";
        return here;
    }

    // Names only: listing them opens no font file.
    private static string[] InstalledFamilies() =>
        SKFontManager.Default.GetFontFamilies()
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

    private static (IReadOnlyList<string> Monospace, IReadOnlyList<string> Text) Probe(IEnumerable<string> families)
    {
        SortedSet<string> mono = new(StringComparer.OrdinalIgnoreCase);
        SortedSet<string> text = new(StringComparer.OrdinalIgnoreCase);
        foreach (string family in families)
        {
            (bool usable, bool fixedPitch) = ProbeFamily(family);
            if (usable) text.Add(family);
            if (fixedPitch) mono.Add(family);
        }
        return (mono.ToArray(), text.ToArray());
    }

    // What the saved catalogue is good for: these family names, scanned by this
    // rule. The order the system lists them in is not part of it.
    internal static string Stamp(IEnumerable<string> families)
    {
        StringBuilder all = new($"v{ProbeVersion}\n");
        foreach (string family in families.Order(StringComparer.Ordinal))
            all.Append(family).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(all.ToString())));
    }

    // The saved lists, when the file is there, whole, and for this stamp.
    internal static (IReadOnlyList<string> Monospace, IReadOnlyList<string> Text)? TryRead(string path, string stamp)
    {
        try
        {
            return JsonStore.Load<Saved>(path) is { Monospace: { } mono, Text: { } text } saved
                   && saved.Stamp == stamp
                ? (mono, text)
                : null;
        }
        catch
        {
            // A catalogue that can't be read is one that isn't there: it is rebuilt.
            return null;
        }
    }

    internal static void Save(string path, string stamp,
        (IReadOnlyList<string> Monospace, IReadOnlyList<string> Text) lists) =>
        JsonStore.Save(path, new Saved
        {
            Stamp = stamp,
            Monospace = lists.Monospace.ToArray(),
            Text = lists.Text.ToArray(),
        });

    // Null when the helper ran to the end; else what went wrong, for Source.
    private static string? RunHelper()
    {
        try
        {
            if (Environment.ProcessPath is not { Length: > 0 } exe)
                return "could not be started (the program's own path is not known)";
            ProcessStartInfo start = new(exe) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(ProbeArgument);
            using Process? helper = Process.Start(start);
            if (helper is null) return "could not be started";
            if (!helper.WaitForExit(HelperTimeout))
            {
                try { helper.Kill(entireProcessTree: true); }
                catch { /* it ended by itself between the wait and the kill */ }
                return $"was still running after {HelperTimeout.TotalSeconds:0} s and was stopped";
            }
            return helper.ExitCode == 0 ? null : $"ended with exit code {helper.ExitCode}";
        }
        catch (Exception ex)
        {
            return $"could not be started ({ex.Message})";
        }
    }

    private sealed class Saved
    {
        public string? Stamp { get; set; }
        public string[]? Monospace { get; set; }
        public string[]? Text { get; set; }
    }

    // Deliberately not through FontManager.TryGetGlyphTypeface: for a family whose
    // name carries a weight ("Roboto Black") that call synthesises a bold face by
    // copying the whole font file out of a native stream the garbage collector can
    // free mid-copy, which kills the process outright in optimised builds. This
    // scan runs for every installed family at launch, while startup is allocating
    // hardest, so it asks Skia for the face and its advances directly.
    private static (bool Usable, bool FixedPitch) ProbeFamily(string family)
    {
        try
        {
            using SKTypeface? face = SKFontManager.Default.MatchFamily(family);
            return face is null ? default : (true, IsFixedPitch(face));
        }
        catch
        {
            // A family that can't be realised (a broken or partial install) simply
            // doesn't make the cut — skip it rather than let one bad font abort the
            // whole enumeration.
            return default;
        }
    }

    // Whether a face sets BBS text at fixed pitch, which is what the terminal's
    // cell grid needs.
    internal static bool IsFixedPitch(SKTypeface face)
    {
        int em = face.UnitsPerEm;
        if (em <= 0) return false;

        // Sized to the em with hinting off, so the advances come back in font
        // design units and only the ratio between them matters.
        using SKFont font = new(face, em)
        {
            LinearMetrics = true,
            Hinting = SKFontHinting.None,
            Subpixel = true,
        };
        Span<float> advances = stackalloc float[ProbeText.Length];
        font.GetGlyphWidths(ProbeText, advances, Span<SKRect>.Empty);
        // A missing letter still measures (as the face's "not defined" box), so ask
        // for the glyph itself.
        for (int i = 0; i < ProbeText.Length; i++)
            if (face.GetGlyph(ProbeText[i]) == 0) advances[i] = -1;

        // Probe the actual Latin advances rather than trusting the font's own
        // IsFixedPitch flag — that flag is set on emoji/symbol faces (all-uniform
        // but no text glyphs) and on some proportional Nerd Font variants, none of
        // which render BBS text at fixed pitch. A narrow 'i', a wide 'M' / 'W' and a
        // space all advancing the same width is the fixed-pitch signature; a face
        // missing any of them fails, since WidthsUniform rejects the -1. An 'a' has
        // to be there too, though not at the same width.
        if (advances[0] <= 0) return false;
        double[] pitch = { advances[1], advances[2], advances[3], advances[4] };
        return WidthsUniform(pitch, epsilon: em * 0.02);
    }

    // A fixed-pitch face advances its probe glyphs by the same width. Compare
    // within a tolerance (a small fraction of the em) to absorb the odd
    // design-unit rounding; any zero or negative width fails it outright, since a
    // face that can't measure a probe glyph isn't one to offer.
    internal static bool WidthsUniform(IReadOnlyList<double> widths, double epsilon)
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
