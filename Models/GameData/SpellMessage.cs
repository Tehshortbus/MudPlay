namespace FujinTerm.Models.GameData;

/// <summary>
/// One spell-message record. Each <c>SpellMessages.json</c> row links a
/// spell (by <see cref="SpellId"/> matching the <c>Spells.Id</c> column)
/// to one of the match kinds the runtime parser fires on.
/// </summary>
/// <param name="SpellId">
/// Foreign key into the <c>Spells</c> table. The Phase 5 PR 5.7 Spells
/// tab's inline editor groups every <c>SpellMessage</c> with this
/// <c>SpellId</c> next to its parent spell.
/// </param>
/// <param name="Kind">
/// Which pipeline stage triggers this message. See
/// <see cref="SpellMessageKind"/>.
/// </param>
/// <param name="Pattern">
/// The substring / wildcard the runtime matches against the wire line.
/// Format is up to the importer's source; PR 5.8 stores it verbatim
/// and lets Phase 13 parser code decide how to interpret each kind.
/// </param>
/// <param name="EffectFlags">
/// Optional bitfield encoding side-effects the match implies (e.g.
/// "applies poison condition", "consumes one charge"). 0 when the
/// match is purely informational.
/// </param>
public sealed record SpellMessage(
    int SpellId,
    SpellMessageKind Kind,
    string Pattern,
    int EffectFlags = 0);

/// <summary>What stage of a spell's lifecycle a message fires on.</summary>
public enum SpellMessageKind
{
    /// <summary>Cast confirmation — "You begin casting…".</summary>
    Cast,
    /// <summary>Damage / heal landed on a target.</summary>
    Hit,
    /// <summary>Target shrugged off the effect.</summary>
    Resist,
    /// <summary>Duration ran out / buff faded.</summary>
    Expire,
    /// <summary>Target-side effect (the message shown on the receiving end of a single-target spell).</summary>
    TargetEffect,
}
