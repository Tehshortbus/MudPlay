namespace FujinTerm.Models.GameData;

/// <summary>
/// One "Messages/Responses" entry — a parser-pattern bundle paired with
/// the engine reaction that fires when any of its lines match.
/// </summary>
/// <remarks>
/// <para>
/// Surfaced + edited via the Game Data Browser → Messages tab.
/// Spell-bound records carry up to five perspective-tagged lines (the
/// same line shown from the caster, target, witness, buff-applied, and
/// stat-line points of view) so the future combat manager can ask a
/// targeted question — "what's the caster line for spell N?" —
/// without scanning every record. Non-spell records (item procs,
/// monster ability lines, condition messages, life-counter triggers)
/// typically populate only the slot that semantically fits.
/// </para>
/// <para>
/// Storage lives alongside the active game-data set at
/// <c>Data/game data/{set}/messages.json</c> with the universal seed
/// at <c>Data/Global/Messages.seed.json</c> (user-writable; bootstrapped
/// from the bundled <c>Defaults/</c> copy on first launch). The seed is
/// generated from the wcc-export <c>spell-messages.json</c> via the
/// offline <c>gen_wcc_seed.py</c> script; user edits write back to the
/// per-set file (creating it on first save).
/// </para>
/// <para>
/// Identity rule: <see cref="Id"/> is <c>SHA1(Name | CasterMessage |
/// TargetMessage | WitnessMessage | AppliedMessage | AppliedEndsWith |
/// StatusLineMessage)</c> truncated to 16 lowercase hex chars. Any
/// edit to Name or any line text produces a new Id; the store's
/// upsert logic uses the original-Id reference to replace in place.
/// </para>
/// </remarks>
/// <param name="Id">Stable content hash of (Name + all five lines).</param>
/// <param name="Name">Display name shown in the Messages tab list — typically the spell name for spell-bound records.</param>
/// <param name="Action">High-level engine reaction when any line matches. See <see cref="MessageAction"/>.</param>
/// <param name="Flags">Typed view of the known flag bits. See <see cref="MessageFlags"/>.</param>
/// <param name="RawFlagsHex">Full 16-bit flag word as stored in the legacy MegaMUD format — preserves reserved 0x0800.</param>
/// <param name="Response">Verbatim response text (literal <c>^M</c> separators preserved).</param>
/// <param name="CasterMessage">Line YOU see when YOU cast the spell / use the item / proc the effect.</param>
/// <param name="TargetMessage">Line YOU see when the spell hits YOU (damage spells, instant debuffs).</param>
/// <param name="WitnessMessage">Line YOU see when someone else casts on someone else (third-party).</param>
/// <param name="AppliedMessage">Buff / debuff begin text — what YOU see when the effect starts on you. Paired with <see cref="AppliedEndsWith"/>.</param>
/// <param name="AppliedEndsWith">Wear-off text — what YOU see when the buff / debuff applied to you expires. Only meaningful alongside a non-empty <see cref="AppliedMessage"/>.</param>
/// <param name="StatusLineMessage">Entry in the player's <c>stat</c> active-effects list while the effect is on you.</param>
/// <param name="Links">Back-references to the game-data rows this record is anchored to — usually one Spells#N for spell-bound records, possibly several when name-aliased variants share the same lines (e.g. priest + druid resist cold).</param>
public sealed record MessageRecord(
    string                       Id,
    string                       Name,
    MessageAction                Action,
    MessageFlags                 Flags,
    ushort                       RawFlagsHex,
    string                       Response,
    string                       CasterMessage,
    string                       TargetMessage,
    string                       WitnessMessage,
    string                       AppliedMessage,
    string                       AppliedEndsWith,
    string                       StatusLineMessage,
    IReadOnlyList<GameDataLink>? Links = null)
{
    /// <summary>
    /// Stable content hash used as <see cref="Id"/>. SHA1 of every
    /// identity field (Name + each of the five perspective line slots
    /// + the applied wear-off pair half), joined by <c>|</c>, truncated
    /// to 16 lowercase hex chars. Any edit to any field flips the Id;
    /// callers use the original-Id reference to find-and-replace the
    /// record in its store after a save.
    /// </summary>
    public static string ComputeId(
        string name,
        string casterMessage,
        string targetMessage,
        string witnessMessage,
        string appliedMessage,
        string appliedEndsWith,
        string statusLineMessage)
    {
        byte[] buf = System.Text.Encoding.UTF8.GetBytes(
            $"{name}|{casterMessage}|{targetMessage}|{witnessMessage}|{appliedMessage}|{appliedEndsWith}|{statusLineMessage}");
        byte[] hash = System.Security.Cryptography.SHA1.HashData(buf);
        System.Text.StringBuilder sb = new(16);
        for (int i = 0; i < 8; i++)
            sb.Append(hash[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}

/// <summary>
/// One back-reference from a <see cref="MessageRecord"/> to a record
/// inside the active set's JSON tables. Display name is resolved live
/// from the current set so it never goes stale on a game-data update.
/// </summary>
/// <param name="Table">JSON file stem under <c>Data/game data/{set}/</c> — case-insensitive on resolution.</param>
/// <param name="Number">The <c>Number</c> field on the target record.</param>
public readonly record struct GameDataLink(
    string Table,
    int    Number);

/// <summary>
/// What the engine does when any of the record's lines fires. Values
/// match the legacy MegaMUD <c>messages.md</c> action code (single
/// decimal digit) so records round-trip through that format without
/// translation.
/// </summary>
public enum MessageAction
{
    /// <summary>Note the match for logging; take no engine action.</summary>
    Ignore      = 0,

    /// <summary>Re-poll the current room state before the next decision.</summary>
    RecheckRoom = 1,

    /// <summary>Pause the action loop until the condition expires.</summary>
    WaitForEnd  = 2,

    /// <summary>Rest until HP is full before continuing.</summary>
    RestHp      = 3,

    /// <summary>Rest / meditate until MA is full before continuing.</summary>
    RestMana    = 4,

    /// <summary>Skip auto-rest and switch to auto-run while the condition is active.</summary>
    Run         = 5,

    /// <summary>Drop the connection.</summary>
    Hangup      = 6,
}

/// <summary>
/// Typed view of the message flag bitfield. Values match the legacy
/// MegaMUD <c>messages.md</c> 16-bit hex encoding so records round-trip
/// through that format without translation.
/// </summary>
/// <remarks>
/// Three MegaMUD-specific find-mode bits are deliberately omitted:
/// <c>0x0100 FindInConversations</c>, <c>0x0400 FindInText</c>,
/// <c>0x4000 UseWhenChasing</c>. They were stripped from the model
/// per user direction. The importer masks them out on read so they
/// never enter the data; the only preserved-but-unknown bit is
/// <c>0x0800</c> (reserved / undocumented in the legacy format), kept
/// on <see cref="MessageRecord.RawFlagsHex"/>.
/// </remarks>
[Flags]
public enum MessageFlags : ushort
{
    None                = 0,
    Blinded             = 0x0001,
    Confused            = 0x0002,
    Poisoned            = 0x0004,
    LosingHp            = 0x0008,
    MovementPrevented   = 0x0010,
    AttackPrevented     = 0x0020,
    Diseased            = 0x0040,
    HpRegenerating      = 0x0080,
    // 0x0100 FindInConversations dropped per user direction
    ManaRegenerating    = 0x0200,
    // 0x0400 FindInText dropped per user direction
    // 0x0800 reserved — preserved via RawFlagsHex
    EndsCombat          = 0x1000,
    LastActionFailed    = 0x2000,
    // 0x4000 UseWhenChasing dropped per user direction
    Disabled            = 0x8000,
}
