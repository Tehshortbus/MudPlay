using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FujinTerm.Game.Map;

// One outgoing exit from a room: the target RoomKey plus a parsed RoomExitHint
// and the raw parenthetical text from the MDB cell (preserved so an unknown
// hint can still be surfaced for diagnostics or rendered on the map legend).
//
// The optional integer / list fields encode the requirement detail the walker
// needs to act:
//   - StatRequirement — picklock/bash skill needed for Door and KeyLocked
//     exits. 0 means no stat requirement.
//   - CanBash — true when the modifier reads "picklocks/strength" (both verbs
//     work); false when it reads "picklocks" alone (pick-only, bash
//     impossible).
//   - KeyItemId — item id required for KeyLocked, Item, Ticket exits.
//   - TollGold — gold cost on Toll exits.
//   - TextCommands — comma-separated alternatives on Text exits. Any one of
//     them moves the player.
//   - MinLevel / MaxLevel — character level window on a level-gated exit
//     ("(Level: 40 to 0)"). The exit is a plain cardinal step (Hint stays
//     None); the window only constrains who may traverse it. 0 in either slot
//     means "no bound on that side" (the MDB encodes both no-floor and no-cap
//     as 0, and also uses 999 as a no-cap sentinel — normalised to 0 here).
//   - ClassGate — the single class Number allowed through a class-gated exit
//     ("(Class: 13 OK, 0 NO)" → 13 = only Druids pass; confirmed from the
//     Crypt Shadowed Hall data where each hall exit gates to exactly one of
//     the 15 class Numbers, never a bitmask/sum). Like the level window the
//     exit stays a plain cardinal (Hint None); 0 means no class gate. The
//     trailing "N NO" count is always 0 in observed data and its non-zero
//     meaning is unconfirmed, so we don't act on it.
//   - PreCastSpell / PostCastSpell — the spell Numbers fired by a
//     "(Cast: pre-N, post-M)" exit. pre-N casts before the move is attempted,
//     post-M casts after stepping through; 0 in either slot means "no cast on
//     that side". The movement stays a plain cardinal (Hint None) — the cast
//     is a side effect, not a modifier the walker acts on at exit-parse time.
//     Confirmed on the Rhudaur Warped Asylum (map 9 rooms 1183-1206), where
//     every exit is "(Cast: pre-0, post-596)" and spell 596 random-teleports
//     the walker into rooms 1183-1206.
//   - CastTeleportRandom — set at graph-build time (not parse time, since it
//     needs the spell catalog): true when the post-cast spell is a teleport
//     with a random destination (TeleportRoom ability value 0 → a random room
//     in the spell's MinBase..MaxBase range). This is the "we can't predict
//     where this exit lands" signal: the walker's router refuses to plan
//     through it (a random landing can't be part of a deterministic route). It
//     does NOT change the map layout — the exit's nominal cardinal target is a
//     real adjacent room (the Warped Asylum is a fully-reciprocal grid whose
//     rooms connect almost entirely through these cast exits), so laying it out
//     normally is what lets the whole area render; the map just marks it with a
//     spell-wall glyph. A fixed (single-room) cast-teleport is deterministic,
//     so it needs no flag — it stays an ordinary cardinal that happens to cast.
//   - CastPocketEntrance — set at graph-build time: true when this cast exit is
//     the ONE-WAY mouth of a sink pocket — its target cannot reach its source by
//     any route (no walk-back). The Rhudaur Warped Asylum is the case: room 1182
//     casts west into 1183 and the 108 asylum rooms have zero exits back out, so
//     a layout rooted OUTSIDE the pocket would pour all 108 rooms into the host
//     area's grid and overlay it. BuildLayout refuses to expand THROUGH a pocket
//     entrance, so from outside the pocket is left undrawn (the mouth still shows
//     as a spell-wall stub); from inside, the one-way mouth is never traversed
//     anyway, so the whole area still renders. Distinct from CastTeleportRandom:
//     a pocket entrance is about topology (can you get back?), not predictability
//     (where do you land?) — the asylum's INTERNAL cast exits are reciprocal, so
//     they're not entrances and the area stays whole when viewed from within.
public readonly partial record struct RoomExit(
    RoomKey Target,
    RoomExitHint Hint,
    string? RawHint,
    int StatRequirement = 0,
    bool CanBash = true,
    int KeyItemId = 0,
    int TollGold = 0,
    IReadOnlyList<string>? TextCommands = null,
    MultiActionExitData? MultiAction = null,
    int TrapDamage = 0,
    int MinLevel = 0,
    int MaxLevel = 0,
    int ClassGate = 0,
    int PreCastSpell = 0,
    int PostCastSpell = 0,
    bool CastTeleportRandom = false,
    bool CastPocketEntrance = false)
{
    // True when this exit carries a character-level window (either a floor, a
    // cap, or both).
    public bool HasLevelGate => MinLevel > 0 || MaxLevel > 0;

    // True when this exit only admits a single character class.
    public bool HasClassGate => ClassGate > 0;

    // True when stepping this exit fires a spell (before or after the move).
    // Drives the map's per-exit spell-wall glyph: the user sees which direction
    // out of a room will trigger a cast on them.
    public bool CastsOnWalk => PreCastSpell > 0 || PostCastSpell > 0;

    // Render a level window as a friendly label: "Level 40+" (floor only),
    // "Level ≤3" (cap only), "Level 10–25" (both). Returns the empty string when
    // neither bound is set.
    public static string FormatLevelGate(int min, int max)
    {
        if (min > 0 && max > 0) return $"Level {min}–{max}";
        if (min > 0)            return $"Level {min}+";
        if (max > 0)            return $"Level ≤{max}";
        return string.Empty;
    }
    // Parse a single MDB exit cell. Returns false for the "0" sentinel
    // ("no exit"), for null/whitespace, and for malformed cells. The hint
    // vocabulary covers the full modifier set — see RoomExitHint for the matrix.
    // An unrecognised parenthetical round-trips through RawHint as a non-null
    // string while Hint stays None.
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
            out IReadOnlyList<string>? textCommands,
            out int trapDamage,
            out int minLevel,
            out int maxLevel,
            out int classGate,
            out int preCast,
            out int postCast);

        exit = new RoomExit(key, hint, rawHint,
            statReq, canBash, keyItemId, toll, textCommands,
            MultiAction: null, TrapDamage: trapDamage,
            MinLevel: minLevel, MaxLevel: maxLevel, ClassGate: classGate,
            PreCastSpell: preCast, PostCastSpell: postCast);
        return true;
    }

    private static void ClassifyHint(
        string? raw,
        out RoomExitHint hint,
        out int statReq,
        out bool canBash,
        out int keyItemId,
        out int toll,
        out IReadOnlyList<string>? textCommands,
        out int trapDamage,
        out int minLevel,
        out int maxLevel,
        out int classGate,
        out int preCast,
        out int postCast)
    {
        hint = RoomExitHint.None;
        statReq = 0;
        canBash = true;
        keyItemId = 0;
        toll = 0;
        textCommands = null;
        trapDamage = 0;
        minLevel = 0;
        maxLevel = 0;
        classGate = 0;
        preCast = 0;
        postCast = 0;

        if (string.IsNullOrEmpty(raw)) return;

        // Prefix-tag match — order matters: more specific shapes first.
        // ----------------------------------------------------------------
        if (raw.StartsWith("Spell Trap", StringComparison.OrdinalIgnoreCase)
         || raw.StartsWith("Trap",       StringComparison.OrdinalIgnoreCase))
        {
            hint = RoomExitHint.Trap;
            // "(Trap, 36 damage)" → 36. Many trap cells carry no
            // damage figure (older exports); TrapDamage stays 0 and
            // the tooltip / walker treat the absence as "unknown".
            Match dmgM = TrapDamageRegex().Match(raw);
            if (dmgM.Success) int.TryParse(dmgM.Groups[1].ValueSpan, out trapDamage);
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

        // "(Level: MIN to MAX)" — a level-gated cardinal step. The
        // movement stays a plain cardinal (hint = None); the window
        // captures who may traverse it. 0 in the min slot = no floor;
        // 0 or 999 in the max slot = no cap (both MDB no-cap sentinels).
        if (raw.StartsWith("Level", StringComparison.OrdinalIgnoreCase))
        {
            Match m = LevelRangeRegex().Match(raw);
            if (m.Success)
            {
                int.TryParse(m.Groups[1].ValueSpan, out minLevel);
                int.TryParse(m.Groups[2].ValueSpan, out int rawMax);
                maxLevel = rawMax is 0 or 999 ? 0 : rawMax;
            }
            return;  // hint stays None — movement is still a plain cardinal step
        }

        // "(Class: N OK, M NO)" — a class-gated cardinal step. Only class
        // Number N may traverse; the movement stays a plain cardinal (hint
        // None). The "M NO" count is always 0 in observed data and its
        // non-zero meaning is unconfirmed, so we parse only the allowed
        // class and leave the rest in RawHint.
        if (raw.StartsWith("Class", StringComparison.OrdinalIgnoreCase))
        {
            Match m = ClassGateRegex().Match(raw);
            if (m.Success) int.TryParse(m.Groups[1].ValueSpan, out classGate);
            return;  // hint stays None — movement is still a plain cardinal step
        }

        // "(Cast: pre-N, post-M)" — stepping this exit fires spell N before the
        // move and spell M after it (0 = no cast that side). The movement stays
        // a plain cardinal (hint None); the cast is captured on
        // PreCastSpell/PostCastSpell so the map can flag it and the graph-build
        // pass can promote a random-teleport post-cast to a portal. Whether the
        // post-cast spell teleports (and randomly) needs the spell catalog, so
        // that classification happens later in RoomGraphManager, not here.
        if (raw.StartsWith("Cast", StringComparison.OrdinalIgnoreCase))
        {
            Match preM = CastPreRegex().Match(raw);
            if (preM.Success) int.TryParse(preM.Groups[1].ValueSpan, out preCast);
            Match postM = CastPostRegex().Match(raw);
            if (postM.Success) int.TryParse(postM.Groups[1].ValueSpan, out postCast);
            return;  // hint stays None — movement is still a plain cardinal step
        }

        // Race / Alignment / Ability / Timed restrictions: walker treats as
        // None for now (path-time gates are a later concern); RawHint carries
        // the detail forward.
    }

    // Pull "N picklocks" / "N picklocks/strength" out of a modifier string. Used
    // by both Door and Key-with-alt branches. Returns (0, true) when no clause
    // is present.
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

    // Matches the first decimal number after a colon in a modifier
    // (key/item/ticket id, toll cost).
    [GeneratedRegex(@":\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex NumberAfterColon();

    // Matches the picklock skill number — "[N picklocks" or "or N picklocks".
    [GeneratedRegex(@"(?:\[|\bor\s+)(\d+)\s+picklocks", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PicklockStatRx();

    // Matches a trap-damage figure inside the trap modifier — "Trap, 36 damage".
    [GeneratedRegex(@"(\d+)\s+damage", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrapDamageRegex();

    // Matches the level window in a "Level: MIN to MAX" modifier.
    [GeneratedRegex(@"Level:\s*(\d+)\s+to\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LevelRangeRegex();

    // Matches the allowed class Number in a "Class: N OK, M NO" modifier.
    [GeneratedRegex(@"Class:\s*(\d+)\s*OK", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClassGateRegex();

    // Matches the pre-move spell Number in a "Cast: pre-N, post-M" modifier.
    [GeneratedRegex(@"pre-(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CastPreRegex();

    // Matches the post-move spell Number in a "Cast: pre-N, post-M" modifier.
    [GeneratedRegex(@"post-(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CastPostRegex();
}
