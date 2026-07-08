namespace FujinTerm.Models.Profile;

// Per-character "Party" settings — drives the party services
// (Game.PartyPoller, Game.PartyManager, Game.Remote.PartyBroadcaster). Stored as
// the "Party" entry in CharacterProfile.Settings.
public sealed class PartySettings
{
    // Cadence for Game.PartyPoller's periodic par poll, in seconds. MegaMUD
    // default is 5; range 1..60.
    public int ParPollFrequencySec { get; set; } = 5;

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
    public int IfLeadingWaitTotalSec { get; set; } = 90;

    // Leader-side recovery reach — the farthest, in BFS room-hops from our
    // current position, we'll walk to re-collect a reconnecting party member
    // before declining. Gates BOTH reconnect-rejoin paths in
    // Game.Remote.PartyComebackManager: a follower's @comeback (with a room key)
    // and the leader-initiated @where probe of a returning member. Beyond this
    // distance the leader telepaths @forget instead of walking, so a member who
    // reconnects on the far side of the map isn't chased across it. Range
    // 1..500; default 30 rooms.
    public int ReturnDistanceRooms { get; set; } = 30;

    // When leading a party, drop incoming @wait broadcasts so the leader's
    // automation keeps running instead of pausing on a follower's request. Off
    // (default) honours @wait regardless of leadership. Consumed by
    // Game.Remote.PartyEssentialHandlers.
    public bool IgnoreWaitWhenLeading { get; set; }

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
    public int JoinNagInitialDelaySec { get; set; } = 5;

    // Cadence for the @join resend after the initial nag fires. Range 1..60,
    // default 10. Stops once the target joins, telepaths us back, or
    // JoinNagMaxTotalSec elapses.
    public int JoinNagFrequencySec { get; set; } = 10;

    // Hard cap on the total nag window measured from the initial invite. Past
    // this many seconds we give up and stop sending @join. Range 5..600,
    // default 55.
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
    public int MinorHealMemberThresholdPercent { get; set; } = 70;

    // Cast Major party heal when any party member's HP% falls below this value.
    // Default 40 — mirrors self-heal's MajorHealCombatTrigger default.
    public int MajorHealMemberThresholdPercent { get; set; } = 40;

    // Minimum number of party members below threshold for the engine to switch
    // from single-target to AOE / group heal. Default 2. Clamped to ≥ 2 at
    // engine time so a misconfig can't fire AOE on a single hurt member.
    public int AoeMinMembers { get; set; } = 2;

    // ----- Capacity --------------------------------------------------

    // Cap on engageable hostiles while in an active party — overrides the
    // Combat tab's CombatSettings.MaxMonstersInRoom whenever
    // Game.PartyState.IsInParty is true. Range 1..20; default 20 (same as the
    // Combat default, so it's a no-op until the user tightens it).
    // CombatSettings.MinMonstersInRoom still applies — only the upper bound is
    // party-scoped.
    public int MaxMonstersWhenPartying { get; set; } = 20;

    // ----- Vitals gate ----------------------------------------------

    // Pause the party action loop (assert
    // Game.Map.MovementCoordinator.PartyVitalsGate) while any other observed
    // party member's HP% is below this value, so the group holds for the hurt
    // member to rest / be healed before moving on. 0 (default) disables the
    // gate. Range 0..100. Members whose HP% hasn't been observed yet
    // (HpPercent == 0) don't trip it. Consumed by Game.PartyVitalsWatcher.
    public int WaitIfMemberBelowPercent { get; set; }

    // ----- Party bless gating ---------------------------------------
    // Two coarse gates the party-bless path honors before it casts a
    // beneficial spell on a party member. Both default ON: blessing the
    // party is the normal expectation, and a player who wants to hold
    // casts under specific conditions opts out explicitly.

    // When true (default), allow party-bless casts while the character is
    // resting. Consumed by the party-bless path in Game.Spells.CastingDirector.
    public bool BlessWhileResting { get; set; } = true;

    // When true (default), allow party-bless casts during combat. Consumed by
    // the party-bless path in Game.Spells.CastingDirector.
    public bool BlessDuringCombat { get; set; } = true;

    // ----- Party bless slots ----------------------------------------

    // Up to 10 beneficial-spell slots cast on OTHER party members. Each slot
    // pairs a spell short-code with the set of class numbers it applies to — a
    // member receives the buff only when their class number is listed. Cast as
    // <short> <given-name> (e.g. bles raijin). Row order is priority order; the
    // party-bless path in Game.Spells.CastingDirector walks self buffs first,
    // then these party slots top-to-bottom. Slots with no spell short are
    // skipped. Always 10 entries so the UI rows bind one-to-one; empty trailing
    // slots persist as blanks.
    public List<PartyBlessSlot> BlessSlots { get; set; } = NewBlessSlots();

    // Builds a fresh list of 10 empty bless slots.
    public static List<PartyBlessSlot> NewBlessSlots()
    {
        List<PartyBlessSlot> slots = new(PartyBlessSlotCount);
        for (int i = 0; i < PartyBlessSlotCount; i++)
            slots.Add(new PartyBlessSlot());
        return slots;
    }

    // Fixed number of party-bless slots shown in the UI.
    public const int PartyBlessSlotCount = 10;

    // Party-cure pickers ship in a follow-up commit — they need
    // per-member condition tracking, deferred until the spellbook
    // gamedata duration model lands.
}

// One party-bless slot: a beneficial spell plus the class numbers it targets. A
// party member gets the buff only when their class number is in ClassNumbers.
// Mutable DTO so the Settings → Party UI can two-way bind each row.
public sealed class PartyBlessSlot
{
    // 4-letter spell short-code (e.g. bles), or null/empty for an unused slot.
    public string? Spell { get; set; }

    // Class numbers (from Classes.json) this buff applies to. Empty means the
    // slot targets no one.
    public List<int> ClassNumbers { get; set; } = new();
}

// Local character's combat rank within a party.
public enum PartyRank
{
    Front,
    Mid,
    Back,
}
