using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FujinTerm.Game.Map;

/// <summary>
/// One outgoing exit from a room: the target <see cref="RoomKey"/>
/// plus a parsed <see cref="RoomExitHint"/> and the raw parenthetical
/// text from the MDB cell (preserved so an unknown hint can still be
/// surfaced for diagnostics or rendered on the map legend).
/// </summary>
/// <remarks>
/// <para>
/// The optional integer / list fields encode the requirement detail
/// the walker needs to act:
/// </para>
/// <list type="bullet">
///   <item><see cref="StatRequirement"/> — picklock/bash skill needed
///   for <see cref="RoomExitHint.Door"/> and <see cref="RoomExitHint.KeyLocked"/>
///   exits. <c>0</c> means no stat requirement.</item>
///   <item><see cref="CanBash"/> — <c>true</c> when the modifier
///   reads "picklocks/strength" (both verbs work); <c>false</c> when
///   it reads "picklocks" alone (pick-only, bash impossible).</item>
///   <item><see cref="KeyItemId"/> — item id required for
///   <see cref="RoomExitHint.KeyLocked"/>, <see cref="RoomExitHint.Item"/>,
///   <see cref="RoomExitHint.Ticket"/> exits.</item>
///   <item><see cref="TollGold"/> — gold cost on <see cref="RoomExitHint.Toll"/> exits.</item>
///   <item><see cref="TextCommands"/> — comma-separated alternatives
///   on <see cref="RoomExitHint.Text"/> exits. Any one of them moves
///   the player.</item>
/// </list>
/// </remarks>
public readonly partial record struct RoomExit(
    RoomKey Target,
    RoomExitHint Hint,
    string? RawHint,
    int StatRequirement = 0,
    bool CanBash = true,
    int KeyItemId = 0,
    int TollGold = 0,
    IReadOnlyList<string>? TextCommands = null,
    MultiActionExitData? MultiAction = null)
{
    /// <summary>
    /// Parse a single MDB exit cell. Returns <c>false</c> for the
    /// <c>"0"</c> sentinel ("no exit"), for null/whitespace, and for
    /// malformed cells.
    /// </summary>
    /// <remarks>
    /// Hint vocabulary now covers the full set MudProxy classifies —
    /// see <see cref="RoomExitHint"/> for the matrix. An
    /// unrecognised parenthetical round-trips through
    /// <see cref="RawHint"/> as a non-null string while
    /// <see cref="Hint"/> stays <see cref="RoomExitHint.None"/>.
    /// </remarks>
    public static bool TryParseWire(string? wire, out RoomExit exit)
    {
        exit = default;
        if (string.IsNullOrWhiteSpace(wire)) return false;

        string trimmed = wire.Trim();
        if (trimmed == "0") return false;

        string? rawHint = null;
        string keyPart = trimmed;

        int paren = trimmed.IndexOf('(');
        if (paren >= 0)
        {
            int close = trimmed.IndexOf(')', paren + 1);
            if (close > paren)
            {
                rawHint = trimmed.Substring(paren + 1, close - paren - 1).Trim();
                keyPart = trimmed[..paren].TrimEnd();
            }
        }

        if (!RoomKey.TryParseWire(keyPart, out RoomKey key)) return false;

        ClassifyHint(rawHint,
            out RoomExitHint hint,
            out int statReq,
            out bool canBash,
            out int keyItemId,
            out int toll,
            out IReadOnlyList<string>? textCommands);

        exit = new RoomExit(key, hint, rawHint,
            statReq, canBash, keyItemId, toll, textCommands);
        return true;
    }

    private static void ClassifyHint(
        string? raw,
        out RoomExitHint hint,
        out int statReq,
        out bool canBash,
        out int keyItemId,
        out int toll,
        out IReadOnlyList<string>? textCommands)
    {
        hint = RoomExitHint.None;
        statReq = 0;
        canBash = true;
        keyItemId = 0;
        toll = 0;
        textCommands = null;

        if (string.IsNullOrEmpty(raw)) return;

        // Prefix-tag match — order matters: more specific shapes first.
        // ----------------------------------------------------------------
        if (raw.StartsWith("Spell Trap", StringComparison.OrdinalIgnoreCase)
         || raw.StartsWith("Trap",       StringComparison.OrdinalIgnoreCase))
        {
            hint = RoomExitHint.Trap;
            return;
        }

        if (raw.StartsWith("Text:", StringComparison.OrdinalIgnoreCase))
        {
            hint = RoomExitHint.Text;
            string list = raw[5..].Trim();
            var cmds = new List<string>(4);
            foreach (string raw2 in list.Split(','))
            {
                string token = raw2.Trim();
                if (token.Length > 0) cmds.Add(token);
            }
            if (cmds.Count > 0) textCommands = cmds;
            return;
        }

        if (raw.StartsWith("Toll", StringComparison.OrdinalIgnoreCase))
        {
            hint = RoomExitHint.Toll;
            Match m = NumberAfterColon().Match(raw);
            if (m.Success) int.TryParse(m.Groups[1].ValueSpan, out toll);
            return;
        }

        if (raw.StartsWith("Ticket", StringComparison.OrdinalIgnoreCase))
        {
            hint = RoomExitHint.Ticket;
            Match m = NumberAfterColon().Match(raw);
            if (m.Success) int.TryParse(m.Groups[1].ValueSpan, out keyItemId);
            return;
        }

        if (raw.StartsWith("Item", StringComparison.OrdinalIgnoreCase))
        {
            // Distinguishing Item (inventory check) vs Teleport (room
            // CMD chain) requires the Room.Cmd field on the source room,
            // which isn't visible at exit-parse time. Classify as Item
            // here; RoomGraphManager promotes to Teleport in a second
            // pass after the source room's Cmd has been read.
            hint = RoomExitHint.Item;
            Match m = NumberAfterColon().Match(raw);
            if (m.Success) int.TryParse(m.Groups[1].ValueSpan, out keyItemId);
            return;
        }

        if (raw.StartsWith("Key", StringComparison.OrdinalIgnoreCase))
        {
            hint = RoomExitHint.KeyLocked;
            Match keyM = NumberAfterColon().Match(raw);
            if (keyM.Success) int.TryParse(keyM.Groups[1].ValueSpan, out keyItemId);
            // Optional stat-alt: "or N picklocks" / "or N picklocks/strength"
            (statReq, canBash) = ParsePicklocksClause(raw);
            return;
        }

        if (raw.StartsWith("Door", StringComparison.OrdinalIgnoreCase))
        {
            // "Door, Key: N" → key-locked even though the prefix says Door.
            if (raw.Contains("Key", StringComparison.OrdinalIgnoreCase))
            {
                hint = RoomExitHint.KeyLocked;
                Match keyM = NumberAfterColon().Match(raw);
                if (keyM.Success) int.TryParse(keyM.Groups[1].ValueSpan, out keyItemId);
            }
            else
            {
                hint = RoomExitHint.Door;
            }
            (statReq, canBash) = ParsePicklocksClause(raw);
            return;
        }

        if (raw.StartsWith("Hidden", StringComparison.OrdinalIgnoreCase))
        {
            // "Hidden/Passable" / "Hidden/Passage" — exit isn't shown
            // in "Obvious exits:" but a plain cardinal traverses it.
            // No special walker behaviour; classify as None so the
            // walker treats it as a normal step.
            string after = raw[6..].TrimStart('/', ' ', ',');
            if (after.StartsWith("Passable", StringComparison.OrdinalIgnoreCase)
             || after.StartsWith("Passage",  StringComparison.OrdinalIgnoreCase))
            {
                hint = RoomExitHint.None;
                return;
            }

            // "Hidden, Needs N Actions" → multi-action.
            if (raw.Contains("Needs", StringComparison.OrdinalIgnoreCase)
             && raw.Contains("Action", StringComparison.OrdinalIgnoreCase))
            {
                hint = RoomExitHint.MultiActionHidden;
                return;
            }

            // Plain "(Hidden)" → searchable via sea <dir>.
            hint = RoomExitHint.SearchableHidden;
            return;
        }

        // Level / Class / Race / Alignment / Ability / Cast / Timed
        // restrictions: walker treats as None for now (path-time gates
        // are a later concern); RawHint carries the detail forward.
    }

    /// <summary>
    /// Pull <c>N picklocks</c> / <c>N picklocks/strength</c> out of a
    /// modifier string. Used by both Door and Key-with-alt branches.
    /// Returns (0, true) when no clause is present.
    /// </summary>
    private static (int StatRequirement, bool CanBash) ParsePicklocksClause(string raw)
    {
        if (!raw.Contains("picklocks", StringComparison.OrdinalIgnoreCase))
            return (0, true);

        int statReq = 0;
        Match num = PicklockStatRx().Match(raw);
        if (num.Success) int.TryParse(num.Groups[1].ValueSpan, out statReq);
        // "picklocks/strength" → bashable, "picklocks" alone → pick only.
        bool canBash = raw.Contains("picklocks/strength", StringComparison.OrdinalIgnoreCase);
        return (statReq, canBash);
    }

    /// <summary>Matches the first decimal number after a colon in a modifier (key/item/ticket id, toll cost).</summary>
    [GeneratedRegex(@":\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex NumberAfterColon();

    /// <summary>Matches the picklock skill number — "[N picklocks" or "or N picklocks".</summary>
    [GeneratedRegex(@"(?:\[|\bor\s+)(\d+)\s+picklocks", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PicklockStatRx();
}
