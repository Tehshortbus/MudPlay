namespace FujinTerm.Game;

/// <summary>
/// Reasons an engine can ask the party leader to pause via
/// <see cref="PartyRestSync.RequestWait"/>. The on-the-wire <c>@wait</c>
/// signal carries no reason ("an @wait doesn't need to say why") — the
/// reason exists only inside <see cref="PartyRestSync"/> so that several
/// independent gates (auto-rest recovery plus each curable ailment) can
/// hold the wait at once and <c>@ok</c> only fires when the LAST reason
/// clears. Without this, an ailment recovering would prematurely release
/// a wait the HealthManager still needs (or vice-versa).
/// </summary>
public enum WaitReason
{
    /// <summary>HealthManager auto-rest / recovery gate.</summary>
    Health,

    /// <summary>Local character is poisoned.</summary>
    Poison,

    /// <summary>Local character is blinded.</summary>
    Blindness,

    /// <summary>Local character is confused.</summary>
    Confusion,

    /// <summary>Local character is diseased.</summary>
    Disease,
}
