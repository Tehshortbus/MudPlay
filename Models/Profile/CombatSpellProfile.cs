using System;
using MudPlay.Services;

namespace MudPlay.Models.Profile;

// One named "combat profile" — a quick-swap snapshot of a whole combat posture,
// not just spells. It captures the Combat tab's seven CombatSpellSlots + their
// mana-mode / drain knobs, the physical attack verbs, the room-skip / flee
// thresholds, the primary/alternate WEAPONS (+ off-hands), and the entire Health
// tab (rest / heal / flee thresholds, meditate/shadow, pre-/post-rest commands).
// Switching a profile swaps all of it at once, INCLUDING the action order (spells-
// first / physical-first / the custom round cycle) — a magic profile and a physical
// profile want opposite orders, so it's per-profile (report paradigm-20260922-141201).
// Targeting stays shared across profiles on the live CombatSettings. Any group can
// be taken out of profiles instead (CombatProfileSettings.SharedGroups).
//
// Stored per character in the top-level CharacterProfile.CombatProfiles blob
// (like Equipment / PartyBuffs), never a tier-merged Settings section.
public sealed class CombatSpellProfile
{
    // Stable identity — survives rename / reorder, so a placed toolbar button and
    // the active pointer keep referring to the same profile. The user-facing
    // "number" is the 1-based position in the list, not this.
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    // User label. Empty → shown as "Profile <n>" by position; @profile matches on
    // it by best-match when set.
    public string Name { get; set; } = string.Empty;

    // The captured spell slots (own copies, never shared with live settings).
    public CombatSpellSlot MultiAttackSpell { get; set; } = new();
    public bool MultiAttack2Enabled { get; set; }
    public CombatSpellSlot MultiAttack2Spell { get; set; } = new();
    public CombatSpellSlot AreaDebuffSpell { get; set; } = new();
    public CombatSpellSlot SingleTargetDebuffSpell { get; set; } = new();
    public CombatSpellSlot NormalAttackSpell { get; set; } = new();
    public CombatSpellSlot AlternateAttackSpell { get; set; } = new();
    public CombatSpellSlot DrainSpell { get; set; } = new();
    public CombatSpellSlot UndeadAttackSpell { get; set; } = new();

    // Slot-governing fields captured alongside the slots.
    public ThresholdMode SpellManaThresholdMode { get; set; } = ThresholdMode.Percentage;
    [SettingRange(0, 100)]
    public int DrainHpTrigger { get; set; } = 50;
    public bool DrainsOverrideAoe { get; set; }
    public bool AreaDebuffFirstRoundOnly { get; set; }

    // Physical attack verbs + room-skip / flee thresholds — a full combat posture,
    // not just spells.
    public string NormalAttackCommand { get; set; } = "a";
    public string AlternateAttackCommand { get; set; } = "a";
    [SettingRange(0, 20)]
    public int MinMonstersInRoom { get; set; }
    [SettingRange(1, 20)]
    public int MaxMonstersInRoom { get; set; } = 20;
    [SettingRange(1, 100)]
    public int RunDistance { get; set; } = 2;

    // Action order is per-profile: a magic profile leads with spells, a physical one with
    // the weapon. The three cycle fields only matter when ActionOrder is CustomRoundCycle.
    public CombatActionOrder ActionOrder { get; set; } = CombatActionOrder.SpellsFirst;
    [SettingRange(0, 999)]
    public int CycleRoundsPhysical { get; set; } = 1;
    [SettingRange(0, 999)]
    public int CycleRoundsSpell { get; set; } = 1;
    public bool CycleStartOnSpell { get; set; }

    // Backstab posture + kill-all-engaged are per-profile: a physical/backstab build wants
    // them on, a caster build wants them off (report paradigm-20260922-141201 follow-up).
    public bool DoBackstab { get; set; }
    public bool SkipBackstabIfMultiAttack { get; set; } = true;
    public bool RunIfBackstabFails { get; set; }
    public bool HitAndRunTactics { get; set; }
    [SettingRange(1, 20)]
    public int HitAndRunMaxRuns { get; set; } = 3;
    public bool ClearHostilesWhenSeenHidden { get; set; }
    public bool SeenHiddenClearWhileSolo { get; set; } = true;
    public bool SeenHiddenClearWhileInParty { get; set; } = true;
    public bool ClearHostilesWhenSneakFails { get; set; }
    public bool KillAllEngaged { get; set; }

    // When running away (the Room thresholds group, beside Run distance).
    public RunDirection RunDirection { get; set; } = RunDirection.Backward;
    public bool BreakBeforeFleeing { get; set; } = true;

    // Per-profile primary/alternate weapons + off-hands. The ACTIVE profile's weapons
    // live in the Workshop Default gear set (the live surface combat + the auto-equip
    // coordinator read); these stored fields are the per-profile memory for when the
    // profile is inactive. The manager snapshots them out of / restores them into the
    // Default set on switch (EquipmentWeaponSync). Backstab weapon stays global on the
    // Workshop Backstab set, so it isn't captured here.
    public string? NormalWeapon { get; set; }
    public string? NormalOffHand { get; set; }
    public string? AlternateWeapon { get; set; }
    public string? AlternateOffHand { get; set; }

    // The whole Health tab captured for this profile. Switching a profile swaps the
    // live Settings["Health"] to a clone of this.
    public HealthSettings Health { get; set; } = new();

    // The Spells-tab subset a profile carries — the between-round category priority
    // order + the self-heal / HP-regen picks. The rest of Settings["Spells"] (cures,
    // bless timing, ailment gates, the Buff Watchdog self-bless slots) stays
    // per-character and is preserved across a switch (WriteInto touches only these
    // fields).
    public CombatProfileSpells Spells { get; set; } = new();

    // The Party-tab subset a profile carries (party healing + party bless).
    public CombatProfileParty Party { get; set; } = new();

    // Snapshot the Combat-tab + Health-tab fields into a fresh profile. Weapons are
    // NOT captured here — they live in the gear set, so the caller (the manager)
    // fills them from the Default set via EquipmentWeaponSync.CaptureDefaultWeapons.
    public static CombatSpellProfile Capture(string name, CombatSettings src, HealthSettings health)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(health);
        return new CombatSpellProfile
        {
            Name = name ?? string.Empty,
            MultiAttackSpell = src.MultiAttackSpell.Clone(),
            MultiAttack2Enabled = src.MultiAttack2Enabled,
            MultiAttack2Spell = src.MultiAttack2Spell.Clone(),
            AreaDebuffSpell = src.AreaDebuffSpell.Clone(),
            SingleTargetDebuffSpell = src.SingleTargetDebuffSpell.Clone(),
            NormalAttackSpell = src.NormalAttackSpell.Clone(),
            AlternateAttackSpell = src.AlternateAttackSpell.Clone(),
            DrainSpell = src.DrainSpell.Clone(),
            UndeadAttackSpell = src.UndeadAttackSpell.Clone(),
            SpellManaThresholdMode = src.SpellManaThresholdMode,
            DrainHpTrigger = src.DrainHpTrigger,
            DrainsOverrideAoe = src.DrainsOverrideAoe,
            AreaDebuffFirstRoundOnly = src.AreaDebuffFirstRoundOnly,
            NormalAttackCommand = src.NormalAttackCommand,
            AlternateAttackCommand = src.AlternateAttackCommand,
            MinMonstersInRoom = src.MinMonstersInRoom,
            MaxMonstersInRoom = src.MaxMonstersInRoom,
            RunDistance = src.RunDistance,
            ActionOrder = src.ActionOrder,
            CycleRoundsPhysical = src.CycleRoundsPhysical,
            CycleRoundsSpell = src.CycleRoundsSpell,
            CycleStartOnSpell = src.CycleStartOnSpell,
            DoBackstab = src.DoBackstab,
            SkipBackstabIfMultiAttack = src.SkipBackstabIfMultiAttack,
            RunIfBackstabFails = src.RunIfBackstabFails,
            HitAndRunTactics = src.HitAndRunTactics,
            HitAndRunMaxRuns = src.HitAndRunMaxRuns,
            ClearHostilesWhenSeenHidden = src.ClearHostilesWhenSeenHidden,
            SeenHiddenClearWhileSolo = src.SeenHiddenClearWhileSolo,
            SeenHiddenClearWhileInParty = src.SeenHiddenClearWhileInParty,
            ClearHostilesWhenSneakFails = src.ClearHostilesWhenSneakFails,
            KillAllEngaged = src.KillAllEngaged,
            RunDirection = src.RunDirection,
            BreakBeforeFleeing = src.BreakBeforeFleeing,
            Health = health.Clone(),
        };
    }

    // Overlay this profile's Combat-tab fields (spells + verbs + room thresholds + action
    // order) onto a live CombatSettings, leaving targeting / backstab untouched. Health is
    // written separately by the manager (writeHealth) and weapons flow to the Default gear
    // set (EquipmentWeaponSync), not here.
    public void ApplyTo(CombatSettings dst)
    {
        ArgumentNullException.ThrowIfNull(dst);
        dst.MultiAttackSpell = MultiAttackSpell.Clone();
        dst.MultiAttack2Enabled = MultiAttack2Enabled;
        dst.MultiAttack2Spell = MultiAttack2Spell.Clone();
        dst.AreaDebuffSpell = AreaDebuffSpell.Clone();
        dst.SingleTargetDebuffSpell = SingleTargetDebuffSpell.Clone();
        dst.NormalAttackSpell = NormalAttackSpell.Clone();
        dst.AlternateAttackSpell = AlternateAttackSpell.Clone();
        dst.DrainSpell = DrainSpell.Clone();
        dst.UndeadAttackSpell = UndeadAttackSpell.Clone();
        dst.SpellManaThresholdMode = SpellManaThresholdMode;
        dst.DrainHpTrigger = DrainHpTrigger;
        dst.DrainsOverrideAoe = DrainsOverrideAoe;
        dst.AreaDebuffFirstRoundOnly = AreaDebuffFirstRoundOnly;
        dst.NormalAttackCommand = NormalAttackCommand;
        dst.AlternateAttackCommand = AlternateAttackCommand;
        dst.MinMonstersInRoom = MinMonstersInRoom;
        dst.MaxMonstersInRoom = MaxMonstersInRoom;
        dst.RunDistance = RunDistance;
        dst.ActionOrder = ActionOrder;
        dst.CycleRoundsPhysical = CycleRoundsPhysical;
        dst.CycleRoundsSpell = CycleRoundsSpell;
        dst.CycleStartOnSpell = CycleStartOnSpell;
        dst.DoBackstab = DoBackstab;
        dst.SkipBackstabIfMultiAttack = SkipBackstabIfMultiAttack;
        dst.RunIfBackstabFails = RunIfBackstabFails;
        dst.HitAndRunTactics = HitAndRunTactics;
        dst.HitAndRunMaxRuns = HitAndRunMaxRuns;
        dst.ClearHostilesWhenSeenHidden = ClearHostilesWhenSeenHidden;
        dst.SeenHiddenClearWhileSolo = SeenHiddenClearWhileSolo;
        dst.SeenHiddenClearWhileInParty = SeenHiddenClearWhileInParty;
        dst.ClearHostilesWhenSneakFails = ClearHostilesWhenSneakFails;
        dst.KillAllEngaged = KillAllEngaged;
        dst.RunDirection = RunDirection;
        dst.BreakBeforeFleeing = BreakBeforeFleeing;
    }

    // Overwrite just the Combat-tab fields (spells + verbs + room thresholds) from a
    // live CombatSettings, leaving Health + weapons + identity + name intact. The
    // Settings-window staging uses this to fold the Combat tab's boxes into a working
    // profile without clobbering what the Health tab or the weapon pickers folded.
    // Exact inverse of ApplyTo.
    public void CaptureCombatFrom(CombatSettings src)
    {
        ArgumentNullException.ThrowIfNull(src);
        MultiAttackSpell = src.MultiAttackSpell.Clone();
        MultiAttack2Enabled = src.MultiAttack2Enabled;
        MultiAttack2Spell = src.MultiAttack2Spell.Clone();
        AreaDebuffSpell = src.AreaDebuffSpell.Clone();
        SingleTargetDebuffSpell = src.SingleTargetDebuffSpell.Clone();
        NormalAttackSpell = src.NormalAttackSpell.Clone();
        AlternateAttackSpell = src.AlternateAttackSpell.Clone();
        DrainSpell = src.DrainSpell.Clone();
        UndeadAttackSpell = src.UndeadAttackSpell.Clone();
        SpellManaThresholdMode = src.SpellManaThresholdMode;
        DrainHpTrigger = src.DrainHpTrigger;
        DrainsOverrideAoe = src.DrainsOverrideAoe;
        AreaDebuffFirstRoundOnly = src.AreaDebuffFirstRoundOnly;
        NormalAttackCommand = src.NormalAttackCommand;
        AlternateAttackCommand = src.AlternateAttackCommand;
        MinMonstersInRoom = src.MinMonstersInRoom;
        MaxMonstersInRoom = src.MaxMonstersInRoom;
        RunDistance = src.RunDistance;
        ActionOrder = src.ActionOrder;
        CycleRoundsPhysical = src.CycleRoundsPhysical;
        CycleRoundsSpell = src.CycleRoundsSpell;
        CycleStartOnSpell = src.CycleStartOnSpell;
        DoBackstab = src.DoBackstab;
        SkipBackstabIfMultiAttack = src.SkipBackstabIfMultiAttack;
        RunIfBackstabFails = src.RunIfBackstabFails;
        HitAndRunTactics = src.HitAndRunTactics;
        HitAndRunMaxRuns = src.HitAndRunMaxRuns;
        ClearHostilesWhenSeenHidden = src.ClearHostilesWhenSeenHidden;
        SeenHiddenClearWhileSolo = src.SeenHiddenClearWhileSolo;
        SeenHiddenClearWhileInParty = src.SeenHiddenClearWhileInParty;
        ClearHostilesWhenSneakFails = src.ClearHostilesWhenSneakFails;
        KillAllEngaged = src.KillAllEngaged;
        RunDirection = src.RunDirection;
        BreakBeforeFleeing = src.BreakBeforeFleeing;
    }

    // A deep copy of the whole profile (new Id) — backs "add a copy" style flows
    // and defensive snapshots.
    public CombatSpellProfile Clone(bool newIdentity) => new()
    {
        Id = newIdentity ? Guid.NewGuid().ToString("N") : Id,
        Name = Name,
        MultiAttackSpell = MultiAttackSpell.Clone(),
        MultiAttack2Enabled = MultiAttack2Enabled,
        MultiAttack2Spell = MultiAttack2Spell.Clone(),
        AreaDebuffSpell = AreaDebuffSpell.Clone(),
        SingleTargetDebuffSpell = SingleTargetDebuffSpell.Clone(),
        NormalAttackSpell = NormalAttackSpell.Clone(),
        AlternateAttackSpell = AlternateAttackSpell.Clone(),
        DrainSpell = DrainSpell.Clone(),
        UndeadAttackSpell = UndeadAttackSpell.Clone(),
        SpellManaThresholdMode = SpellManaThresholdMode,
        DrainHpTrigger = DrainHpTrigger,
        DrainsOverrideAoe = DrainsOverrideAoe,
        AreaDebuffFirstRoundOnly = AreaDebuffFirstRoundOnly,
        NormalAttackCommand = NormalAttackCommand,
        AlternateAttackCommand = AlternateAttackCommand,
        MinMonstersInRoom = MinMonstersInRoom,
        MaxMonstersInRoom = MaxMonstersInRoom,
        RunDistance = RunDistance,
        ActionOrder = ActionOrder,
        CycleRoundsPhysical = CycleRoundsPhysical,
        CycleRoundsSpell = CycleRoundsSpell,
        CycleStartOnSpell = CycleStartOnSpell,
        DoBackstab = DoBackstab,
        SkipBackstabIfMultiAttack = SkipBackstabIfMultiAttack,
        RunIfBackstabFails = RunIfBackstabFails,
        HitAndRunTactics = HitAndRunTactics,
        HitAndRunMaxRuns = HitAndRunMaxRuns,
        ClearHostilesWhenSeenHidden = ClearHostilesWhenSeenHidden,
        SeenHiddenClearWhileSolo = SeenHiddenClearWhileSolo,
        SeenHiddenClearWhileInParty = SeenHiddenClearWhileInParty,
        ClearHostilesWhenSneakFails = ClearHostilesWhenSneakFails,
        KillAllEngaged = KillAllEngaged,
        RunDirection = RunDirection,
        BreakBeforeFleeing = BreakBeforeFleeing,
        NormalWeapon = NormalWeapon,
        NormalOffHand = NormalOffHand,
        AlternateWeapon = AlternateWeapon,
        AlternateOffHand = AlternateOffHand,
        Health = Health.Clone(),
        Spells = Spells.Clone(),
        Party = Party.Clone(),
    };
}

// The Spells-tab subset a combat profile carries: the between-round category
// priority order and the self-heal / HP-regen picks (plus the legacy self-bless
// timing, unread now that each buff slot carries its own).
// Everything else on the Spells tab (cures, ailment gates) and the Buff Watchdog
// self-bless slots stay per-character — WriteInto touches only these fields, so a
// switch never disturbs them.
public sealed class CombatProfileSpells
{
    // The same defaults as SpellsSettings, which says what each slot is.
    [SettingRange(1, 10)]
    public int PriorityEmergencyHeal { get; set; } = 1;
    [SettingRange(1, 10)]
    public int PriorityMajorPartyHeal { get; set; } = 2;
    [SettingRange(1, 10)]
    public int PriorityMinorPartyHeal { get; set; } = 3;
    [SettingRange(1, 10)]
    public int PriorityMajorSelfHeal { get; set; } = 4;
    [SettingRange(1, 10)]
    public int PriorityPriorityBuffs { get; set; } = 5;
    [SettingRange(1, 10)]
    public int PriorityMinorSelfHeal { get; set; } = 6;
    [SettingRange(1, 10)]
    public int PriorityDownedAllyHeal { get; set; } = 7;
    [SettingRange(1, 10)]
    public int PriorityCuring { get; set; } = 8;
    [SettingRange(1, 10)]
    public int PriorityBuffing { get; set; } = 9;
    [SettingRange(1, 10)]
    public int PriorityDebuffing { get; set; } = 10;

    public string? MinorHealSpell { get; set; }
    public string? MajorHealSpell { get; set; }
    // Reorderable via PriorityEmergencyHeal (defaults to slot 1, so it leads).
    // See SpellsSettings.EmergencyHealSpell.
    public string? EmergencyHealSpell { get; set; }
    public string? HpRegenSpell { get; set; }

    // LEGACY bless timing (self): each buff slot carries its own now (BuffSlot). Still
    // stored and copied so an older profile round-trips; nothing reads it.
    public bool SelfBlessWhileResting { get; set; }
    public bool SelfBlessDuringCombat { get; set; }

    // Snapshot the profile-owned fields off a live SpellsSettings.
    public void CaptureFrom(SpellsSettings src)
    {
        ArgumentNullException.ThrowIfNull(src);
        PriorityEmergencyHeal = src.PriorityEmergencyHeal;
        PriorityMinorPartyHeal = src.PriorityMinorPartyHeal;
        PriorityMajorPartyHeal = src.PriorityMajorPartyHeal;
        PriorityDownedAllyHeal = src.PriorityDownedAllyHeal;
        PriorityMinorSelfHeal = src.PriorityMinorSelfHeal;
        PriorityMajorSelfHeal = src.PriorityMajorSelfHeal;
        PriorityCuring = src.PriorityCuring;
        PriorityBuffing = src.PriorityBuffing;
        PriorityPriorityBuffs = src.PriorityPriorityBuffs;
        PriorityDebuffing = src.PriorityDebuffing;
        MinorHealSpell = src.MinorHealSpell;
        MajorHealSpell = src.MajorHealSpell;
        EmergencyHealSpell = src.EmergencyHealSpell;
        HpRegenSpell = src.HpRegenSpell;
        SelfBlessWhileResting = src.SelfBlessWhileResting;
        SelfBlessDuringCombat = src.SelfBlessDuringCombat;
    }

    // Overlay them onto a live SpellsSettings, leaving every per-character field
    // (cures, bless timing, ailment gates, the self-bless slots) untouched.
    public void WriteInto(SpellsSettings dst)
    {
        ArgumentNullException.ThrowIfNull(dst);
        dst.PriorityEmergencyHeal = PriorityEmergencyHeal;
        dst.PriorityMinorPartyHeal = PriorityMinorPartyHeal;
        dst.PriorityMajorPartyHeal = PriorityMajorPartyHeal;
        dst.PriorityDownedAllyHeal = PriorityDownedAllyHeal;
        dst.PriorityMinorSelfHeal = PriorityMinorSelfHeal;
        dst.PriorityMajorSelfHeal = PriorityMajorSelfHeal;
        dst.PriorityCuring = PriorityCuring;
        dst.PriorityBuffing = PriorityBuffing;
        dst.PriorityPriorityBuffs = PriorityPriorityBuffs;
        dst.PriorityDebuffing = PriorityDebuffing;
        dst.MinorHealSpell = MinorHealSpell;
        dst.MajorHealSpell = MajorHealSpell;
        dst.EmergencyHealSpell = EmergencyHealSpell;
        dst.HpRegenSpell = HpRegenSpell;
        dst.SelfBlessWhileResting = SelfBlessWhileResting;
        dst.SelfBlessDuringCombat = SelfBlessDuringCombat;
    }

    public CombatProfileSpells Clone() => new()
    {
        PriorityEmergencyHeal = PriorityEmergencyHeal,
        PriorityMinorPartyHeal = PriorityMinorPartyHeal,
        PriorityMajorPartyHeal = PriorityMajorPartyHeal,
        PriorityDownedAllyHeal = PriorityDownedAllyHeal,
        PriorityMinorSelfHeal = PriorityMinorSelfHeal,
        PriorityMajorSelfHeal = PriorityMajorSelfHeal,
        PriorityCuring = PriorityCuring,
        PriorityBuffing = PriorityBuffing,
        PriorityPriorityBuffs = PriorityPriorityBuffs,
        PriorityDebuffing = PriorityDebuffing,
        MinorHealSpell = MinorHealSpell,
        MajorHealSpell = MajorHealSpell,
        EmergencyHealSpell = EmergencyHealSpell,
        HpRegenSpell = HpRegenSpell,
        SelfBlessWhileResting = SelfBlessWhileResting,
        SelfBlessDuringCombat = SelfBlessDuringCombat,
    };
}
