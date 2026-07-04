using System.Collections.Generic;
using System.Globalization;

namespace FujinTerm.Game;

// Builds the ordered keystroke payloads that drive MajorMUD's `train stats`
// full-screen form to apply a CP plan. The form is navigated entirely with
// Enter (\r): pressing Enter on a field moves to the next one, and typing a
// number into a stat box replaces its value (confirmed against a live capture),
// so each payload here is either an empty string (bare Enter — skip / advance /
// save) or the target digits (type-then-Enter — set the stat + advance).
//
// Field order top-to-bottom, focus starting on Family Name: Family Name,
// Strength, Intellect, Willpower, Agility, Health, Charm, Hair Length, Hair
// Colour, Eye Colour, Exit. We only touch the six stat boxes; every other field
// is advanced past untouched, and the final Enter lands on Exit (which defaults
// to SAVE) to commit and leave. Sending absolute target values is
// self-correcting — the field ends on the target regardless of its starting
// value — and a stat is only typed when its target exceeds the current value
// (you can't untrain), so unchanged stats cost no keystrokes and no CP.
public static class AutoTrainSequenceBuilder
{
    // Number of stat boxes (STR, INT, WIL, AGL, HEA, CHM).
    public const int StatCount = 6;

    // Produce the Enter-driven payload list for current → target base stats
    // (both length-6, order STR/INT/WIL/AGL/HEA/CHM). The result is 11 payloads:
    // skip Family Name, the six stat boxes (target digits where it's a raise,
    // else empty), skip the three appearance boxes, and a final Enter to save +
    // exit.
    public static IReadOnlyList<string> Build(IReadOnlyList<int> current, IReadOnlyList<int> target)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(target);
        if (current.Count != StatCount || target.Count != StatCount)
            throw new ArgumentException($"Expected {StatCount} stats.", nameof(target));

        var seq = new List<string>(11)
        {
            string.Empty,   // Family Name — advance, never edited
        };
        for (int i = 0; i < StatCount; i++)
            seq.Add(target[i] > current[i]
                ? target[i].ToString(CultureInfo.InvariantCulture)   // type target, Enter commits + advances
                : string.Empty);                                     // already at/above target — skip
        seq.Add(string.Empty);   // Hair Length
        seq.Add(string.Empty);   // Hair Colour
        seq.Add(string.Empty);   // Eye Colour
        seq.Add(string.Empty);   // Exit (defaults to SAVE) — Enter commits + exits
        return seq;
    }

    // True when target raises at least one stat above current — i.e. there's
    // something worth training.
    public static bool HasRaise(IReadOnlyList<int> current, IReadOnlyList<int> target)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(target);
        for (int i = 0; i < StatCount && i < current.Count && i < target.Count; i++)
            if (target[i] > current[i]) return true;
        return false;
    }
}
