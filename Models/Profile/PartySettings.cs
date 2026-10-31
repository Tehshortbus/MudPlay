using MudPlay.Services;

namespace MudPlay.Models.Profile;

// Per-character "Party" settings — drives the party services
// (Game.PartyPoller, Game.PartyManager, Game.Remote.PartyBroadcaster). Stored as
// the "Party" entry in CharacterProfile.Settings.
public sealed class PartySettings
{
    // What makes Game.PartyPoller send par — any mix of the three, or none. With
    // none, the client never sends par itself and members' HP between the user's
    // own par commands is whatever Game.PartyHpEstimator reads off the round ledger.

    // On a timer, every ParPollFrequencySec. On by default (MegaMUD parity).
    public bool ParPollOnTimer { get; set; } = true;

    // Cadence for the timed par, in seconds. MegaMUD default is 5; range 1..60.
    [SettingRange(1, 60)]
    public int ParPollFrequencySec { get; set; } = 5;

    // When a combat round we fought in closes.
    public bool ParPollAfterCombatRound { get; set; }

    // Widens ParPollAfterCombatRound to rounds we only witnessed: a fight in the
    // room we took no part in, as with Auto Combat off. No effect without it.
    public bool ParPollIncludeWitnessedRounds { get; set; }

    // When a round's totals carry damage no line named a dealer or a victim for —
    // the "unknown" row — so the ledger's read of members' HP can't be trusted.
    public bool ParPollOnUnknownDamage { get; set; }

    // When a party member disconnects and reconnects within the
    // Game.PartyManager.DisconnectGraceWindow, the leader auto-sends
    // invite <name>. Off lets the user re-invite manually.
    public bool AutoInviteReconnecting { get; set; } = true;

    // "If leading, wait only" — leader-side cap on how long we keep watching for
    // a dropped party member to return. Drives
    // Game.PartyManager.DisconnectGraceWindow: when a member's "X just hung
    // up!!!" / "X just disconnected!!!." line fires (or par observes them
    // missing without a per-player signal), we record the moment and re-invite
    // them if they re-enter the realm within this window. Stored as total
    // seconds. Range 0..3600 (1 hour). Default 90.
    [SettingRange(0, 3600)]
    public int IfLeadingWaitTotalSec { get; set; } = 90;

    // Leader-side recovery reach — the farthest, in BFS room-hops from our
    // current position, we'll walk to re-collect a reconnecting party member
    // before declining. Gates BOTH reconnect-rejoin paths in
    // Game.Remote.PartyComebackManager: a follower's @comeback (with a room key)
    // and the leader-initiated @where probe of a returning member. Beyond this
    // distance the leader telepaths @forget instead of walking, so a member who
    // reconnects on the far side of the map isn't chased across it. Range
    // 1..500; default 30 rooms.
    [SettingRange(1, 500)]
    public int ReturnDistanceRooms { get; set; } = 30;

    // "If leading, accept @comeback for up to" — minutes after a member drops (or
    // is left behind) that we still honour their @comeback, and keep an engine a
    // failed search stopped for them to resume. As a follower, the same value is how
    // long a drop can last before we stop sending @comeback on re-entry — past it
    // the party has moved on. Range 0..60; 0 turns @comeback rejoin off. Default 2.
    [SettingRange(0, 60)]
    public int AcceptComebackMinutes { get; set; } = 2;

    // When leading a party, drop incoming @wait broadcasts so the leader's
    // automation keeps running instead of pausing on a follower's request. Off
    // (default) honours @wait regardless of leadership. Consumed by
    // Game.Remote.PartyEssentialHandlers.
    public bool IgnoreWaitWhenLeading { get; set; }

    // ----- @panic (MegaMUD parity) -----------------------------------
    // @panic is the party-wide bail-out signal: a leader whose HP falls to its
    // "hang if below" floor says a bare "@panic" on the say channel and then
    // escapes (hang up, or break + sys-goto-wimpy per HealthSettings). Every
    // partymate that doesn't ignore it escapes the same way, so the whole group
    // bails together. Modelled on the say-channel ailment signals — the token
    // rides say, not a telepath @-command. Consumed by Game.Health.HealthManager
    // (send) and Game.Conditions.PanicResponder (receive).

    // Leader-side send gate: when leading a party, broadcast "@panic" on say the
    // moment our own emergency-hangup floor is crossed (HealthSettings.HangIfBelowHp
    // with a hostile present). OFF by default — broadcasting an order that drops
    // the whole party is opt-in. Read by Game.Health.HealthManager.
    public bool UsePanicWhileLeading { get; set; }

    // Receive-side gate: when true, a partymate's "@panic" is ignored (we don't
    // bail). OFF by default (MegaMUD parity) — an un-ignored @panic makes us
    // escape exactly as our own low-HP emergency would. Read by
    // Game.Conditions.PanicResponder.
    public bool IgnorePanics { get; set; }

    // When the party leader fails to bash a door we can see ("You see <leader>
    // attempt to bash the door to the <dir>."), pitch in by forcing the same
    // door — bash <dir> or pick <dir> depending on
    // OtherSettings.PicklocksOverBash. Only fires when the actor is our current
    // Game.PartyState.LeaderName. Off by default. Consumed by
    // LeaderDoorAssistManager.
    public bool HelpLeaderOpenDoors { get; set; }

    // On loop / Auto-Lair start, broadcast @Reset to every party member so their
    // exp / kill counters zero together. Gated by
    // Game.Remote.PartyBroadcaster.AutoExpResetEnabled.
    public bool ResetStatisticsOnLoopStart { get; set; } = true;

    // Persisted local-character rank — Front / Mid / Back. CombatManager reads
    // this for party-aware target ordering.
    public PartyRank Rank { get; set; } = PartyRank.Mid;

    // Seconds to wait after sending invite X before sending the first follow-up
    // /X @join nag. Range 1..60, default 5. Drives Game.AutoPartyManager's nag
    // escalation.
    [SettingRange(1, 60)]
    public int JoinNagInitialDelaySec { get; set; } = 5;

    // Cadence for the @join resend after the initial nag fires. Range 1..60,
    // default 10. Stops once the target joins, telepaths us back, or
    // JoinNagMaxTotalSec elapses.
    [SettingRange(1, 60)]
    public int JoinNagFrequencySec { get; set; } = 10;

    // Hard cap on the total nag window measured from the initial invite. Past
    // this many seconds we give up and stop sending @join. Range 5..600,
    // default 55.
    [SettingRange(5, 600)]
    public int JoinNagMaxTotalSec { get; set; } = 55;

    // Master enable for the @join follow-up nag. When true (default), after
    // auto- or manually inviting a player the leader sends /given @join once
    // JoinNagInitialDelaySec elapses, then re-nags per JoinNagFrequencySec up to
    // JoinNagMaxTotalSec. Off suppresses every @join send — the invite still
    // goes out, but no follow-up. Gates Game.AutoPartyManager.JoinNagEnabled.
    public bool SendJoinToInvited { get; set; } = true;

    // Master enable for the on-join @health round-trip + retry nag. When true
    // (default — historical always-on behaviour), a freshly joined member is
    // telepathed /given @health to capture their HP/MP baseline, retried per the
    // shared nag cadence. Off suppresses every @health send. Gates
    // Game.PartyPoller.HealthNagEnabled.
    public bool SendHealthToMembers { get; set; } = true;

    // Master enable for the once-a-day party stats probe. When true (default),
    // the first time we party with a given player on a local day the client
    // telepaths them /given @level and /given @version, recording their exact
    // level and client version onto their player record. Off suppresses both
    // sends. Gates Game.Remote.PartyProbeManager.Enabled. (Distinct from the
    // @health round-trip above, which fires on every join for live vitals.)
    public bool ProbeStatsOnPartyJoin { get; set; } = true;

    // ----- Party-cast spell pickers (CastingDirector) ---------------
    // Each Minor / Major slot owns BOTH a single-target spell and an AOE / group
    // spell. CastingDirector picks single vs AOE at cast time based on how many
    // party members are below the threshold.

    // Single-target heal cast when one party member drops below
    // MinorHealMemberThresholdPercent.
    public string? MinorPartyHealSpell { get; set; }

    // Group AOE heal cast when AoeMinMembers+ members are below
    // MinorHealMemberThresholdPercent.
    public string? MinorPartyHealAoeSpell { get; set; }

    // Symmetric major / critical single-target heal.
    public string? MajorPartyHealSpell { get; set; }

    // Symmetric major / critical group AOE heal.
    public string? MajorPartyHealAoeSpell { get; set; }

    // Cast Minor party heal when any party member's HP% falls below this value.
    // Default 70.
    [SettingRange(0, 100)]
    public int MinorHealMemberThresholdPercent { get; set; } = 70;

    // Cast Major party heal when any party member's HP% falls below this value.
    // Default 40 — mirrors self-heal's MajorHealCombatTrigger default.
    [SettingRange(0, 100)]
    public int MajorHealMemberThresholdPercent { get; set; } = 40;

    // Minimum number of party members below threshold for the engine to switch
    // from single-target to AOE / group heal. Default 2. Clamped to ≥ 2 at
    // engine time so a misconfig can't fire AOE on a single hurt member.
    [SettingRange(2, 6)]
    public int AoeMinMembers { get; set; } = 2;

    // The channel a following party member asks for a heal on (`@heal`) when its HP
    // reaches the Health tab's Run if below, in place of running. Default Say.
    public HealRequestChannel RequestHealChannel { get; set; } = HealRequestChannel.Say;

    // ----- Capacity --------------------------------------------------

    // Cap on engageable hostiles while in an active party — overrides the
    // Combat tab's CombatSettings.MaxMonstersInRoom whenever
    // Game.PartyState.IsInParty is true. Range 1..20; default 20 (same as the
    // Combat default, so it's a no-op until the user tightens it).
    // CombatSettings.MinMonstersInRoom still applies — only the upper bound is
    // party-scoped.
    [SettingRange(1, 20)]
    public int MaxMonstersWhenPartying { get; set; } = 20;

    // ----- Vitals gate ----------------------------------------------

    // Pause the party action loop (assert
    // Game.Map.MovementCoordinator.PartyVitalsGate) while any other observed
    // party member's HP% is below this value, so the group holds for the hurt
    // member to rest / be healed before moving on. 0 (default) disables the
    // gate. Range 0..100. Members whose HP% hasn't been observed yet
    // (HpPercent == 0) don't trip it. Consumed by Game.PartyVitalsWatcher.
    [SettingRange(0, 100)]
    public int WaitIfMemberBelowPercent { get; set; }

    // LEGACY: the two switches every party-buff cast shared. Each buff slot's own
    // BlessWhileResting / BlessDuringCombat cover its party casts now; profile
    // schema 6 folded these into the slots that existed then. Kept so an older
    // profile still loads and migrates; the party buffs themselves are the plan on
    // CharacterProfile.PartyBuffs (Models/Profile/BuffSettings.cs).
    public bool BlessWhileResting { get; set; }
    public bool BlessDuringCombat { get; set; }
}

// Local character's combat rank within a party.
public enum PartyRank
{
    Front,
    Mid,
    Back,
}
