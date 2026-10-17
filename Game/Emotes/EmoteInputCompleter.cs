using System;
using System.Collections.Generic;
using System.Linq;

namespace MudPlay.Game.Emotes;

// Pure logic behind the Discord-style ":" emote picker in the conversation input box:
// find the active ":partial" token at the caret, suggest matching shortcodes, and
// splice the chosen one back in. No Avalonia types.
public static class EmoteInputCompleter
{
    // The active token being typed: the text spans [ColonIndex, caret), starting at a
    // ':' with only shortcode characters up to the caret. Partial is the text after the
    // colon (may be empty right after ":").
    public readonly record struct ActiveToken(int ColonIndex, string Partial);

    // Find the ":partial" token the caret sits at the end of, or null. The colon must be
    // at the start of the line or follow whitespace (so a "8:00" time or a "::" doesn't
    // trigger), and everything from it to the caret must be shortcode characters.
    public static ActiveToken? FindActiveToken(string? text, int caret)
    {
        if (string.IsNullOrEmpty(text) || caret < 1 || caret > text.Length) return null;

        int colon = -1;
        for (int i = caret - 1; i >= 0; i--)
        {
            char ch = text[i];
            if (ch == ':') { colon = i; break; }
            if (!IsShortcodeChar(ch)) return null;   // whitespace / other → not a token
        }
        if (colon < 0) return null;
        // Reject only when a shortcode char precedes the colon (a mid-token colon like
        // "8:00" or "word:x"). Whitespace, or a leading say-slow "." precursor, is fine.
        if (colon > 0 && IsShortcodeChar(text[colon - 1])) return null;

        return new ActiveToken(colon, text.Substring(colon + 1, caret - colon - 1));
    }

    // Shortcode names matching partial: a prefix match ranks first, then a looser
    // substring match, each alphabetical. Empty partial lists everything (capped).
    public static IReadOnlyList<string> Suggest(string partial, IEnumerable<string> shortcodes, int max = 8)
    {
        partial = partial.Trim().ToLowerInvariant();
        IEnumerable<string> names = shortcodes.Distinct();
        if (partial.Length == 0)
            return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(max).ToList();

        List<string> prefix = new(), contains = new();
        foreach (string n in names)
        {
            string ln = n.ToLowerInvariant();
            if (ln.StartsWith(partial, StringComparison.Ordinal)) prefix.Add(n);
            else if (ln.Contains(partial, StringComparison.Ordinal)) contains.Add(n);
        }
        prefix.Sort(StringComparer.OrdinalIgnoreCase);
        contains.Sort(StringComparer.OrdinalIgnoreCase);
        return prefix.Concat(contains).Take(max).ToList();
    }

    // Splice the chosen shortcode into text, replacing the active token with ":name: ".
    // Returns the new text and the caret position after the trailing space.
    public static (string Text, int Caret) Apply(string text, ActiveToken token, int caret, string shortcode)
    {
        string before = text.Substring(0, token.ColonIndex);
        string after = text.Substring(caret);
        string insert = $":{shortcode}: ";
        return (before + insert + after, before.Length + insert.Length);
    }

    private static bool IsShortcodeChar(char c)
        => char.IsLetterOrDigit(c) || c is '_' or '+' or '-';
}
