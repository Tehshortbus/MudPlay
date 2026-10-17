using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MudPlay.Game.Emotes;

public enum EmoteSegmentKind { Text, Emoji, Image }

// One ordered slice of a scanned message: plain Text, an Emoji (Text holds the emoji
// string), or an Image (Payload holds the avares:// URI, Shortcode the name for a
// tooltip / alt). Pure data so the view maps each to a Run / inline image.
public readonly record struct EmoteSegment(
    string Text, EmoteSegmentKind Kind, string? Payload = null, string? Shortcode = null);

// Splits a message into text + emote segments against an EmoteCatalog. Word shortcodes
// (":lol:") resolve case-insensitively; classic emoticons (":)") match case-sensitively
// but only when whitespace-bounded, so "8:00" or a smiley inside a word doesn't trip.
// A ":name:" that isn't in the catalog stays literal text. The compiled regex is built
// once per catalog. Pure; the view never parses the string itself.
public sealed class EmoteScanner
{
    private readonly EmoteCatalog _catalog;
    private readonly Regex _regex;

    public EmoteScanner(EmoteCatalog catalog)
    {
        _catalog = catalog ?? throw new System.ArgumentNullException(nameof(catalog));
        _regex = BuildRegex(catalog);
    }

    // The catalog this scanner matches against.
    public EmoteCatalog Catalog => _catalog;

    // Wraps the static built-in catalog — the conversation renderer's default.
    public static EmoteScanner BuiltIn { get; } = new(EmoteCatalog.BuiltIn);

    private static Regex BuildRegex(EmoteCatalog catalog)
    {
        // Emoticon alternation, longest literal first so ":-)" wins over ":)".
        string emoticons = string.Join("|", catalog.Emoticons.Keys
            .OrderByDescending(k => k.Length)
            .Select(Regex.Escape));

        // A ":name:" shortcode, OR a whitespace-bounded emoticon. Shortcode is tried
        // first so ":Derp:" is a (possibly-unknown) shortcode, not the ":D" emoticon.
        string pattern = @"(?<sc>:[A-Za-z0-9_+\-]+:)";
        if (emoticons.Length > 0)
            pattern += $@"|(?<=^|\s)(?<em>{emoticons})(?=$|\s)";

        return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    // Ordered segments for text. Never returns empty-text segments. When nothing
    // matches, yields a single Text segment with the whole string.
    public IReadOnlyList<EmoteSegment> Scan(string? text)
    {
        List<EmoteSegment> segments = new();
        if (string.IsNullOrEmpty(text)) return segments;

        int pos = 0;
        foreach (Match m in _regex.Matches(text))
        {
            if (m.Index > pos)
                segments.Add(new EmoteSegment(text.Substring(pos, m.Index - pos), EmoteSegmentKind.Text));

            if (m.Groups["sc"].Success)
            {
                string name = m.Value.Trim(':');
                if (_catalog.TryGetShortcode(name, out Emote e))
                    segments.Add(ToSegment(e, m.Value));
                else
                    segments.Add(new EmoteSegment(m.Value, EmoteSegmentKind.Text));  // unknown → literal
            }
            else if (_catalog.TryGetEmoticon(m.Value, out Emote e))
            {
                segments.Add(new EmoteSegment(e.Payload, EmoteSegmentKind.Emoji));
            }
            else
            {
                segments.Add(new EmoteSegment(m.Value, EmoteSegmentKind.Text));
            }
            pos = m.Index + m.Length;
        }

        if (pos < text.Length)
            segments.Add(new EmoteSegment(text.Substring(pos), EmoteSegmentKind.Text));
        return segments;
    }

    private static EmoteSegment ToSegment(Emote e, string original) => e.Kind == EmoteKind.Image
        ? new EmoteSegment(original, EmoteSegmentKind.Image, e.Payload, e.Shortcode)
        : new EmoteSegment(e.Payload, EmoteSegmentKind.Emoji);
}
