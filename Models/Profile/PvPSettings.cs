namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character "PvP" settings — drives <see cref="Game.PvP.PvPManager"/>'s
/// reaction to inbound attacks from other players. Stored as the
/// <c>"PvP"</c> entry in <see cref="CharacterProfile.Settings"/>.
/// </summary>
/// <remarks>
/// <para>
/// v1 ships <see cref="DefaultAction"/> only — applies the same
/// reaction to every player attacker. Per-player whitelists / allow-
/// lists (sourcing from the Phase 5 Players tab's FriendOrFoe flag)
/// land as a follow-up that layers on top of the same engine.
/// </para>
/// <para>
/// <see cref="Action.Attack"/> and <see cref="Action.Chase"/> are
/// reserved and unwired in v1 — they need walker + persistent target
/// support that ships after the foundational reactive path is
/// smoke-tested.
/// </para>
/// </remarks>
public sealed class PvPSettings
{
    /// <summary>
    /// Reaction taken when a hostile player line is observed.
    /// Default <see cref="Action.Ignore"/> — the engine logs and
    /// fires <see cref="Game.PvP.PvPManager.PvPDetected"/> but takes
    /// no action. Explicit opt-in required for Flee / Hangup.
    /// </summary>
    public Action DefaultAction { get; set; } = Action.Ignore;

    /// <summary>
    /// Optional fixed direction for <see cref="Action.Flee"/>.
    /// Blank → server picks a random direction (canonical
    /// <c>flee</c> behaviour). Set to e.g. "north" to force a
    /// specific exit. v1 always uses <c>flee</c>; future walker
    /// integration may switch to <c>run &lt;direction&gt;</c>.
    /// </summary>
    public string FleeDirection { get; set; } = string.Empty;

    /// <summary>
    /// Wire command sent for <see cref="Action.Hangup"/>. Server-
    /// specific — varies by BBS. Default <c>/q</c> covers most
    /// MajorMUD installs. Hardcoded reroll / suicide blocks in
    /// the engine never use this path.
    /// </summary>
    public string HangupCommand { get; set; } = "/q";

    /// <summary>What to do when a hostile player line is detected.</summary>
    public enum Action
    {
        /// <summary>Log only; take no action. Default for fresh chars.</summary>
        Ignore,

        /// <summary>Send <c>flee</c> immediately. Single-shot per PvP
        /// encounter — re-arms when InCombat clears.</summary>
        Flee,

        /// <summary>Send the configured <see cref="HangupCommand"/>
        /// to disconnect. Requires explicit opt-in; the engine logs a
        /// warning before sending.</summary>
        Hangup,

        /// <summary>(Reserved — v1 unwired.) Set the attacker as
        /// CombatManager's target and swing.</summary>
        Attack,

        /// <summary>(Reserved — v1 unwired.) If the attacker flees, follow
        /// them via the walker.</summary>
        Chase,
    }
}
