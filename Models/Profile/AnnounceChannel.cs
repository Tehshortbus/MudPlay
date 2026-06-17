namespace FujinTerm.Models.Profile;

/// <summary>
/// Chat channel the Auto-Trainer "Announce level-ups" feature broadcasts
/// "I can now train to level: N" on. Persisted in
/// <see cref="AutoTrainerSettings.AnnounceChannel"/>; mapped to a MajorMUD
/// wire form (<c>gang</c> / <c>gos</c> word-verbs, <c>"</c> / <c>.</c>
/// punctuation prefixes) at send time
/// by <see cref="FujinTerm.Game.LevelUpAnnouncer"/>. Telepath isn't offered —
/// an availability broadcast has no single recipient.
/// </summary>
public enum AnnounceChannel
{
    /// <summary>Party gangpath — <c>gang</c>.</summary>
    Gangpath = 0,

    /// <summary>Realm-wide gossip — <c>gos</c>.</summary>
    Gossip = 1,

    /// <summary>Area yell — leading <c>"</c>.</summary>
    Yell = 2,

    /// <summary>Local room say — leading <c>.</c>.</summary>
    Say = 3,
}
