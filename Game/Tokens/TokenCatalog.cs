using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MudPlay.Game.Tokens;

// Recognises Paradigm "token of <place>" transport items and the lines their
// `look` and `use` produce. A token's identity, its `look` name, and its `use`
// message all fall out of the item name (the place is just the name minus
// "token of "), so no per-token table is needed and it stays set-independent.
// Pure + static.
public static partial class TokenCatalog
{
    private const string NamePrefix = "token of ";

    // The place a "token of <place>" item names — the remainder after "token of "
    // (e.g. "token of the Lost City" -> "the Lost City"). null when it isn't a token.
    public static string? PlaceOf(string? itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return null;
        int i = itemName.IndexOf(NamePrefix, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        string place = itemName[(i + NamePrefix.Length)..].Trim();
        return place.Length > 0 ? place : null;
    }

    // Match key for a place: lower-cased with a leading "the " dropped — the use
    // message may or may not carry the "the" for "the Lost City", so both forms
    // collapse to one key.
    public static string NormalizePlace(string place)
    {
        string p = (place ?? string.Empty).Trim().ToLowerInvariant();
        if (p.StartsWith("the ", StringComparison.Ordinal)) p = p[4..].Trim();
        return p;
    }

    // The clean `look` command name for a held token, rebuilt from its place so a
    // dumped article/casing ("a token of arlysia") doesn't reach the game verbatim.
    public static string LookName(string place) => NamePrefix + place;

    // "Uses remaining: N" off a look reply -> N; -1 when the line isn't one.
    public static int ParseUsesRemaining(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return -1;
        Match m = UsesRemainingRegex().Match(line);
        return m.Success && int.TryParse(m.Groups["n"].Value, out int n) ? n : -1;
    }

    // A token-name line in a look reply ("token of Arlysia") -> its place; else null.
    public static string? MatchLookNameLine(string line)
    {
        string t = (line ?? string.Empty).Trim();
        return t.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase) ? PlaceOf(t) : null;
    }

    // The token USE lines (self "You invoke the token…" and the witnessed "<name>
    // invokes a token…") are not matched here: they're the token spells' Caster/Witness
    // messages, recognised through the seeded MessageStore records so the wording lives
    // in one place (see TokenTracker's injected matchers). The look-reply parsers below
    // stay coded — they're a token-system mechanic (and reusable for future limited-use
    // items), not a per-spell message.

    // Held tokens in a carried-item list, as (LookName, Place) pairs deduped by the
    // normalized place (a player holds only one of each anyway).
    public static IReadOnlyList<(string LookName, string Place)> HeldTokens(IEnumerable<string> carriedItems)
    {
        var seen = new HashSet<string>();
        var result = new List<(string, string)>();
        if (carriedItems is null) return result;
        foreach (string name in carriedItems)
        {
            if (PlaceOf(name) is not { } place) continue;
            if (!seen.Add(NormalizePlace(place))) continue;
            result.Add((LookName(place), place));
        }
        return result;
    }

    [GeneratedRegex(@"uses?\s+remaining:\s*(?<n>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsesRemainingRegex();
}
