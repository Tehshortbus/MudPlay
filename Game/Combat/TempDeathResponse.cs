using System.Text;
using System.Text.RegularExpressions;

namespace MudPlay.Game.Combat;

// Pure helpers behind the "temp death-spell" recovery: the silent "…temp" spells
// (a monster's DeathSpell) emit no wire line but stall the game engine, so when a
// monster whose DeathSpell is a temp spell dies, the client sends that spell's
// MessageRecord.CastResponse (seeded "^M^M") to nudge the engine past the stall.
// AppServices does the death → DeathSpell → record wiring; this holds the two
// decisions worth pinning in a test.
public static partial class TempDeathResponse
{
    // True when a spell name carries the whole word "temp" — the naming convention
    // for these death-cast spells ("lich temp", "necromancer temp"). Whole-word so
    // "acid tempest" (a real "tempest" spell) doesn't match.
    public static bool IsTempSpell(string? spellName)
        => !string.IsNullOrWhiteSpace(spellName) && TempWord().IsMatch(spellName);

    // Expand a CastResponse to the raw wire bytes to send: each "^M" becomes a
    // carriage return (the Triggers-table encoding), Latin-1 like every other
    // keystroke. Null when there's nothing to send.
    public static byte[]? ExpandToWireBytes(string? castResponse)
    {
        if (string.IsNullOrEmpty(castResponse)) return null;
        string expanded = castResponse.Replace("^M", "\r");
        if (expanded.Length == 0) return null;
        return Encoding.Latin1.GetBytes(expanded);
    }

    [GeneratedRegex(@"\btemp\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TempWord();
}
