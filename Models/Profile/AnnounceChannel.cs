namespace FujinTerm.Models.Profile;

/// <summary>
/// Chat channel the Auto-Trainer "Announce level-ups" feature broadcasts
/// "I can now train to level: N" on. Persisted in
/// <see cref="AutoTrainerSettings.AnnounceChannel"/>; mapped to a MajorMUD
/// wire verb (<c>gang</c> / <c>gos</c> / <c>yell</c> / <c>say</c>) at send time
/// by <see cref="FujinTerm.Game.LevelUpAnnouncer"/>. Telepath isn't offered —
/// an availability broadcast has no single recipient.
/// </summary>
public enum AnnounceChannel
{
    /// <summary>Party gangpath — <c>gang</c>.</summary>
    Gangpath = 0,

    /// <summary>Realm-wide gossip — <c>gos</c>.</summary>
    Gossip = 1,

    /// <summary>Area yell — <c>yell</c>.</summary>
    Yell = 2,

    /// <summary>Local room say — <c>say</c>.</summary>
    Say = 3,
}
