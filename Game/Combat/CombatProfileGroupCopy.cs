using System;
using System.Collections.Generic;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Combat;

// Copies one settings group between combat profiles. A group the user shares
// across profiles (CombatProfileSettings.SharedGroups) is kept identical in every
// profile, so applying any profile leaves it as it was: that's all "shared" needs,
// and the switch / Settings-window paths stay "apply the whole profile".
public static class CombatProfileGroupCopy
{
    // Copy every shared group from `from` into each of `to` (skipping `from` itself).
    public static void SyncShared(
        IEnumerable<CombatProfileGroup>? shared, CombatSpellProfile from, IEnumerable<CombatSpellProfile> to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (shared is null) return;
        foreach (CombatSpellProfile target in to)
        {
            if (ReferenceEquals(target, from)) continue;
            foreach (CombatProfileGroup g in shared) Copy(g, from, target);
        }
    }

    public static void Copy(CombatProfileGroup group, CombatSpellProfile from, CombatSpellProfile to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        switch (group)
        {
            case CombatProfileGroup.ActionOrder:
                to.ActionOrder = from.ActionOrder;
                to.CycleRoundsPhysical = from.CycleRoundsPhysical;
                to.CycleRoundsSpell = from.CycleRoundsSpell;
                to.CycleStartOnSpell = from.CycleStartOnSpell;
                break;
            case CombatProfileGroup.WeaponsAndCommands:
                to.NormalWeapon = from.NormalWeapon;
                to.NormalOffHand = from.NormalOffHand;
                to.AlternateWeapon = from.AlternateWeapon;
                to.AlternateOffHand = from.AlternateOffHand;
                to.NormalAttackCommand = from.NormalAttackCommand;
                to.AlternateAttackCommand = from.AlternateAttackCommand;
                break;
            case CombatProfileGroup.Backstab:
                to.DoBackstab = from.DoBackstab;
                to.SkipBackstabIfMultiAttack = from.SkipBackstabIfMultiAttack;
                to.RunIfBackstabFails = from.RunIfBackstabFails;
                to.HitAndRunTactics = from.HitAndRunTactics;
                to.HitAndRunMaxRuns = from.HitAndRunMaxRuns;
                to.ClearHostilesWhenSeenHidden = from.ClearHostilesWhenSeenHidden;
                to.SeenHiddenClearWhileSolo = from.SeenHiddenClearWhileSolo;
                to.SeenHiddenClearWhileInParty = from.SeenHiddenClearWhileInParty;
                to.ClearHostilesWhenSneakFails = from.ClearHostilesWhenSneakFails;
                break;
            case CombatProfileGroup.RoomThresholds:
                to.MinMonstersInRoom = from.MinMonstersInRoom;
                to.MaxMonstersInRoom = from.MaxMonstersInRoom;
                to.RunDistance = from.RunDistance;
                to.KillAllEngaged = from.KillAllEngaged;
                to.RunDirection = from.RunDirection;
                to.BreakBeforeFleeing = from.BreakBeforeFleeing;
                break;
            case CombatProfileGroup.SpellCombat:
                to.MultiAttackSpell = from.MultiAttackSpell.Clone();
                to.MultiAttack2Enabled = from.MultiAttack2Enabled;
                to.MultiAttack2Spell = from.MultiAttack2Spell.Clone();
                to.AreaDebuffSpell = from.AreaDebuffSpell.Clone();
                to.SingleTargetDebuffSpell = from.SingleTargetDebuffSpell.Clone();
                to.NormalAttackSpell = from.NormalAttackSpell.Clone();
                to.AlternateAttackSpell = from.AlternateAttackSpell.Clone();
                to.DrainSpell = from.DrainSpell.Clone();
                to.SpellManaThresholdMode = from.SpellManaThresholdMode;
                to.DrainHpTrigger = from.DrainHpTrigger;
                to.DrainsOverrideAoe = from.DrainsOverrideAoe;
                to.AreaDebuffFirstRoundOnly = from.AreaDebuffFirstRoundOnly;
                break;
            case CombatProfileGroup.HealthHp:
                to.Health.HpThresholdMode = from.Health.HpThresholdMode;
                to.Health.RestMaxHp = from.Health.RestMaxHp;
                to.Health.RestIfBelowHp = from.Health.RestIfBelowHp;
                to.Health.HealRestTrigger = from.Health.HealRestTrigger;
                to.Health.MinorHealCombatTrigger = from.Health.MinorHealCombatTrigger;
                to.Health.MajorHealCombatTrigger = from.Health.MajorHealCombatTrigger;
                to.Health.EmergencyHealTrigger = from.Health.EmergencyHealTrigger;
                to.Health.RunIfBelowHp = from.Health.RunIfBelowHp;
                to.Health.HangIfBelowHp = from.Health.HangIfBelowHp;
                break;
            case CombatProfileGroup.HealthMana:
                to.Health.MaThresholdMode = from.Health.MaThresholdMode;
                to.Health.RestMaxMa = from.Health.RestMaxMa;
                to.Health.RestIfBelowMa = from.Health.RestIfBelowMa;
                to.Health.HealIfAboveMaResting = from.Health.HealIfAboveMaResting;
                to.Health.HealIfAboveMaCombat = from.Health.HealIfAboveMaCombat;
                to.Health.RunIfBelowMa = from.Health.RunIfBelowMa;
                to.Health.BlessIfAboveMa = from.Health.BlessIfAboveMa;
                break;
            case CombatProfileGroup.RestingOptions:
                to.Health.UseMeditateAbility = from.Health.UseMeditateAbility;
                to.Health.MeditateBeforeResting = from.Health.MeditateBeforeResting;
                to.Health.UtilizeShadowRest = from.Health.UtilizeShadowRest;
                break;
            case CombatProfileGroup.EmergencyEscape:
                to.Health.SysGotoWimpyInsteadOfHanging = from.Health.SysGotoWimpyInsteadOfHanging;
                to.Health.SysGotoWimpyLocation = from.Health.SysGotoWimpyLocation;
                break;
            case CombatProfileGroup.RestingCommands:
                to.Health.PreRestCommand = from.Health.PreRestCommand;
                to.Health.PostRestCommand = from.Health.PostRestCommand;
                break;
            case CombatProfileGroup.SpellPriority:
                to.Spells.PriorityEmergencyHeal = from.Spells.PriorityEmergencyHeal;
                to.Spells.PriorityMinorPartyHeal = from.Spells.PriorityMinorPartyHeal;
                to.Spells.PriorityMajorPartyHeal = from.Spells.PriorityMajorPartyHeal;
                to.Spells.PriorityDownedAllyHeal = from.Spells.PriorityDownedAllyHeal;
                to.Spells.PriorityMinorSelfHeal = from.Spells.PriorityMinorSelfHeal;
                to.Spells.PriorityMajorSelfHeal = from.Spells.PriorityMajorSelfHeal;
                to.Spells.PriorityCuring = from.Spells.PriorityCuring;
                to.Spells.PriorityBuffing = from.Spells.PriorityBuffing;
                to.Spells.PriorityPriorityBuffs = from.Spells.PriorityPriorityBuffs;
                to.Spells.PriorityDebuffing = from.Spells.PriorityDebuffing;
                break;
            case CombatProfileGroup.HealingRegen:
                to.Spells.MinorHealSpell = from.Spells.MinorHealSpell;
                to.Spells.MajorHealSpell = from.Spells.MajorHealSpell;
                to.Spells.EmergencyHealSpell = from.Spells.EmergencyHealSpell;
                to.Spells.HpRegenSpell = from.Spells.HpRegenSpell;
                break;
            case CombatProfileGroup.BlessTiming:
                to.Spells.SelfBlessWhileResting = from.Spells.SelfBlessWhileResting;
                to.Spells.SelfBlessDuringCombat = from.Spells.SelfBlessDuringCombat;
                break;
            case CombatProfileGroup.PartyHealing:
                to.Party.MinorPartyHealSpell = from.Party.MinorPartyHealSpell;
                to.Party.MinorPartyHealAoeSpell = from.Party.MinorPartyHealAoeSpell;
                to.Party.MajorPartyHealSpell = from.Party.MajorPartyHealSpell;
                to.Party.MajorPartyHealAoeSpell = from.Party.MajorPartyHealAoeSpell;
                to.Party.MinorHealMemberThresholdPercent = from.Party.MinorHealMemberThresholdPercent;
                to.Party.MajorHealMemberThresholdPercent = from.Party.MajorHealMemberThresholdPercent;
                to.Party.AoeMinMembers = from.Party.AoeMinMembers;
                break;
            case CombatProfileGroup.PartyBless:
                to.Party.BlessWhileResting = from.Party.BlessWhileResting;
                to.Party.BlessDuringCombat = from.Party.BlessDuringCombat;
                break;
        }
    }
}
