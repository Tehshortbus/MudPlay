namespace MudPlay.Models.GameData;

// One "Messages/Responses" entry — a parser-pattern bundle paired with
// the engine reaction that fires when any of its lines match.
//
// Surfaced + edited via the Game Data Browser → Messages tab. Spell-bound
// records carry up to four perspective-tagged lines (the same line shown
// from the caster, target, witness, and buff-applied points of view) so
// the future combat manager can ask a targeted question — "what's the
// caster line for spell N?" — without scanning every record. Non-spell
// records (item procs, monster ability lines, condition messages,
// life-counter triggers) typically populate only the slot that
// semantically fits.
//
// Storage lives alongside the active game-data set at
// Data/game data/{set}/messages.json with the realm-flavored seed at
// Data/Global/Messages.{stock|paradigm}.seed.json (user-writable; bootstrapped
// from the bundled Defaults/ copy on first launch, realm picked from the set's
// Info.json Legit). Each seed is decoded offline from that realm's MegaMUD
// messages.md by tools/decode_messages_md.py — a record's name attributes it to
// its spell/item by name. User edits write back to the per-set file.
//
// Identity rule: Id is SHA1(Name | CasterMessage | TargetMessage |
// WitnessMessage | AppliedMessage | AppliedEndsWith) truncated to 16
// lowercase hex chars. Any edit to Name or any line text produces a new
// Id; the store's upsert logic uses the original-Id reference to replace
// in place.
//
// CasterMessage: line YOU see when YOU cast the spell / use the item /
// proc the effect. TargetMessage: line YOU see when the spell hits YOU
// (damage spells, instant debuffs). WitnessMessage: line YOU see when
// someone else casts on someone else (third-party). AppliedMessage:
// buff / debuff begin text (what YOU see when the effect starts on you),
// paired with AppliedEndsWith. AppliedEndsWith: wear-off text, only
// meaningful alongside a non-empty AppliedMessage. RawFlagsHex preserves
// the full 16-bit flag word from the legacy MegaMUD format (reserved
// 0x0800). Links back-reference the game-data rows this record is anchored
// to — usually one Spells#N, possibly several when name-aliased variants
// share the same lines (e.g. priest + druid resist cold).
//
// A message is RECOGNITION only — it identifies a line and (via Flags) the
// condition it means. Player-defined RESPONSES to a seen line live in the
// Triggers table, not here; the legacy per-message Response + Action fields
// were retired with that split (nothing branched on Action, and the handful
// of message Responses were the same lines already handled as triggers).
//
// ConfuseFumbleLine: only meaningful on a Confused record. The wire line(s) a
// character confused by THIS source emits when it fumbles a just-sent command
// (the command is consumed and never executes) — a fumbled MOVE reverts on it.
// One wording per line; the generic every confuse source shares is
// "You fumble in confusion!". Recognition metadata, NOT part of the identity
// hash — editing it doesn't re-Id the record.
//
// CastResponse: an engine-driven RESPONSE (not recognition) — the one exception
// to messages being recognition-only. When a spell this record is linked to is
// detected cast, the client sends this text to the server, with '^M' expanded to
// a carriage return (the same encoding the Triggers table uses). Its use case is
// the silent "…temp" death-spells: they emit no line but stall the game engine,
// so a monster whose DeathSpell is a temp spell fires this on death — seeded to
// "^M^M" (two carriage returns) to nudge the engine past the stall. Not part of
// the identity hash.
public sealed record MessageRecord(
    string                       Id,
    string                       Name,
    MessageFlags                 Flags,
    ushort                       RawFlagsHex,
    string                       CasterMessage,
    string                       TargetMessage,
    string                       WitnessMessage,
    string                       AppliedMessage,
    string                       AppliedEndsWith,
    IReadOnlyList<GameDataLink>? Links = null,
    string                       ConfuseFumbleLine = "",
    string                       CastResponse = "")
{
    // Stable content hash used as Id. SHA1 of every identity field (Name +
    // each of the four perspective line slots + the applied wear-off pair
    // half), joined by |, truncated to 16 lowercase hex chars. Any edit to
    // any field flips the Id; callers use the original-Id reference to
    // find-and-replace the record in its store after a save.
    public static string ComputeId(
        string name,
        string casterMessage,
        string targetMessage,
        string witnessMessage,
        string appliedMessage,
        string appliedEndsWith)
    {
        byte[] buf = System.Text.Encoding.UTF8.GetBytes(
            $"{name}|{casterMessage}|{targetMessage}|{witnessMessage}|{appliedMessage}|{appliedEndsWith}");
        byte[] hash = System.Security.Cryptography.SHA1.HashData(buf);
        System.Text.StringBuilder sb = new(16);
        for (int i = 0; i < 8; i++)
            sb.Append(hash[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    // ----- Absent-line sentinels -----
    // A user marks a message slot the spell genuinely has no wording for by typing one of
    // these tokens into it, instead of leaving it blank. The distinction cuts two ways: a
    // recognition consumer (ConditionTracker, CasterMessageMatcher) treats a sentinel as
    // EMPTY, so it compiles no matcher — "{void}" never becomes a literal pattern that
    // tries to match a real server line — while the Game Data Browser's Incomplete
    // Messages worklist counts a sentinel as FILLED, so a fully-triaged record (every slot
    // either real text or a sentinel) drops off the list. A truly blank slot stays "not
    // yet investigated".
    private static readonly string[] AbsentSentinels = { "{null}", "{void}", "{empty}" };

    // True when line is exactly one of the absent-line sentinels (case-insensitive,
    // surrounding whitespace ignored).
    public static bool IsAbsentSentinel(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        string t = line.Trim();
        foreach (string s in AbsentSentinels)
            if (string.Equals(t, s, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // True when a slot carries no usable recognition text — blank OR an absent sentinel.
    // Recognition consumers skip these: an absent line compiles no pattern.
    public static bool IsBlankOrAbsent(string? line)
        => string.IsNullOrWhiteSpace(line) || IsAbsentSentinel(line);
}

// One back-reference from a MessageRecord to a record inside the active
// set's JSON tables. Display name is resolved live from the current set so
// it never goes stale on a game-data update. Table is the JSON file stem
// under Data/game data/{set}/ (case-insensitive on resolution); Number is
// the Number field on the target record.
public readonly record struct GameDataLink(
    string Table,
    int    Number);

// Typed view of the message flag bitfield. Values match the legacy
// MegaMUD messages.md 16-bit hex encoding so records round-trip through
// that format without translation.
//
// Several MegaMUD bits are deliberately absent from the model. The three
// find-mode bits — 0x0100 FindInConversations, 0x0400 FindInText, 0x4000
// UseWhenChasing — plus four effect bits no engine ever acted on: 0x0008
// LosingHp, 0x0080 HpRegenerating, 0x0200 ManaRegenerating, 0x1000
// EndsCombat. The decoder no longer names them and the seeds ship with them
// stripped; a stale name in an old data file fails the enum parse (caught in
// MessageStore.TryLoad, which then falls back to the seed). The raw 16-bit
// word — including the reserved-but-unknown 0x0800 — is still kept verbatim
// on MessageRecord.RawFlagsHex; it just never parses into a named flag.
[Flags]
public enum MessageFlags : ushort
{
    None                = 0,
    Blinded             = 0x0001,
    Confused            = 0x0002,
    Poisoned            = 0x0004,
    // 0x0008 LosingHp removed per user direction — no engine consumed it
    MovementPrevented   = 0x0010,
    AttackPrevented     = 0x0020,
    Diseased            = 0x0040,
    // 0x0080 HpRegenerating removed per user direction — no engine consumed it
    // 0x0100 FindInConversations dropped per user direction
    // 0x0200 ManaRegenerating removed per user direction — no engine consumed it
    // 0x0400 FindInText dropped per user direction
    // 0x0800 reserved — preserved via RawFlagsHex
    // 0x1000 EndsCombat removed per user direction — no engine consumed it
    LastActionFailed    = 0x2000,
    // 0x4000 UseWhenChasing dropped per user direction
    Disabled            = 0x8000,
}
