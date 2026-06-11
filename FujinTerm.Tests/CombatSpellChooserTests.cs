using FujinTerm.Game.Combat;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins <see cref="CombatSpellChooser"/> — the per-round combat-spell decision
/// unit — against the realm's one-action-per-round ordering: backstab gate →
/// pre-attack debuff (area once-per-room XOR single once-per-target) → attack
/// (multi-attack while qualified → normal → alternate → weapon). Mana gating
/// (<see cref="ThresholdMode.Percentage"/> vs <see cref="ThresholdMode.Absolute"/>)
/// and per-room cast caps are exercised per branch.
/// </summary>
public sealed class CombatSpellChooserTests
{
    private static CombatSpellSlot Slot(
        string? name, int minEnemies = 0, int maxCasts = 0, int minMana = 0) => new()
    {
        SpellName = name,
        MinEnemies = minEnemies,
        MaxCastsPerRoom = maxCasts,
        MinManaPerCast = minMana,
    };

    private static CombatSpellContext Ctx(
        int enemies = 1, string target = "a rat", int mana = 100, int maxMana = 100,
        bool backstabPending = false) =>
        new(enemies, target, mana, maxMana, backstabPending);

    // ----- 1. Backstab gate ---------------------------------------------

    [Fact]
    public void Choose_BackstabPending_AlwaysWeapon_NoSpellPreempts()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            AreaDebuffSpell = Slot("blind"),
            MultiAttackSpell = Slot("star"),
            NormalAttackSpell = Slot("harm"),
        };

        CombatSpellDecision d = sut.Choose(settings, Ctx(enemies: 5, backstabPending: true));

        Assert.Equal(CombatSpellAction.WeaponAttack, d.Action);
        Assert.Null(d.Spell);
    }

    // ----- 2. Pre-attack debuff: area once-per-room, excludes single -----

    [Fact]
    public void Choose_AreaDebuff_FiresOncePerRoom_AndExcludesSingle()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            AreaDebuffSpell = Slot("blindall", minEnemies: 2),
            SingleTargetDebuffSpell = Slot("weaken"),
            NormalAttackSpell = Slot("harm"),
        };

        // First round: area debuff wins.
        CombatSpellDecision first = sut.Choose(settings, Ctx(enemies: 3));
        Assert.Equal(CombatSpellAction.AreaDebuff, first.Action);
        Assert.Equal("blindall", first.Spell);
        sut.MarkCast(first, "a rat");

        // Next round: area already cast → never falls to the single-target
        // debuff (area excludes single) → goes straight to the attack phase.
        CombatSpellDecision second = sut.Choose(settings, Ctx(enemies: 3));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, second.Action);
        Assert.Equal("harm", second.Spell);
    }

    [Fact]
    public void Choose_AreaDebuff_BelowMinEnemies_Skipped()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            AreaDebuffSpell = Slot("blindall", minEnemies: 4),
            NormalAttackSpell = Slot("harm"),
        };

        // Only 2 enemies, area needs 4 → skip straight to attack. Area never
        // falls through to single (none configured here anyway).
        CombatSpellDecision d = sut.Choose(settings, Ctx(enemies: 2));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, d.Action);
    }

    // ----- 2b. Single-target debuff: once per target --------------------

    [Fact]
    public void Choose_SingleDebuff_FiresOncePerTarget()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SingleTargetDebuffSpell = Slot("weaken"),
            NormalAttackSpell = Slot("harm"),
        };

        // Target A: debuff first.
        CombatSpellDecision a1 = sut.Choose(settings, Ctx(target: "a rat"));
        Assert.Equal(CombatSpellAction.SingleDebuff, a1.Action);
        sut.MarkCast(a1, "a rat");

        // Same target again: already debuffed → attack.
        CombatSpellDecision a2 = sut.Choose(settings, Ctx(target: "a rat"));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, a2.Action);

        // New target: debuff again.
        CombatSpellDecision b1 = sut.Choose(settings, Ctx(target: "a kobold"));
        Assert.Equal(CombatSpellAction.SingleDebuff, b1.Action);
        sut.MarkCast(b1, "a kobold");

        CombatSpellDecision b2 = sut.Choose(settings, Ctx(target: "a kobold"));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, b2.Action);
    }

    [Fact]
    public void Choose_SingleDebuff_TargetMatchIsCaseInsensitive()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SingleTargetDebuffSpell = Slot("weaken"),
            NormalAttackSpell = Slot("harm"),
        };

        CombatSpellDecision first = sut.Choose(settings, Ctx(target: "A Rat"));
        sut.MarkCast(first, "A Rat");

        CombatSpellDecision second = sut.Choose(settings, Ctx(target: "a rat"));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, second.Action);
    }

    [Fact]
    public void Choose_SingleDebuff_HonoursMaxCastsPerRoom()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SingleTargetDebuffSpell = Slot("weaken", maxCasts: 1),
            NormalAttackSpell = Slot("harm"),
        };

        CombatSpellDecision a = sut.Choose(settings, Ctx(target: "a rat"));
        Assert.Equal(CombatSpellAction.SingleDebuff, a.Action);
        sut.MarkCast(a, "a rat");

        // New target, but the room-wide single-debuff cap (1) is reached →
        // no more debuffs, straight to attack.
        CombatSpellDecision b = sut.Choose(settings, Ctx(target: "a kobold"));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, b.Action);
    }

    // ----- 3. Attack phase: multi-attack while qualified ----------------

    [Fact]
    public void Choose_MultiAttack_RepeatsUntilRoomThinsBelowMinEnemies()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            MultiAttackSpell = Slot("star", minEnemies: 3),
            NormalAttackSpell = Slot("harm"),
        };

        // 4 enemies: multi-attack fires.
        CombatSpellDecision r1 = sut.Choose(settings, Ctx(enemies: 4));
        Assert.Equal(CombatSpellAction.MultiAttack, r1.Action);
        sut.MarkCast(r1, "a rat");

        CombatSpellDecision r2 = sut.Choose(settings, Ctx(enemies: 3));
        Assert.Equal(CombatSpellAction.MultiAttack, r2.Action);
        sut.MarkCast(r2, "a rat");

        // Room thinned to 2 (< MinEnemies 3) → fall to single-target spell.
        CombatSpellDecision r3 = sut.Choose(settings, Ctx(enemies: 2));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, r3.Action);
    }

    [Fact]
    public void Choose_MultiAttack_StopsWhenCastCapReached_FallsToNormal()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            MultiAttackSpell = Slot("star", minEnemies: 2, maxCasts: 2),
            NormalAttackSpell = Slot("harm"),
        };

        CombatSpellDecision r1 = sut.Choose(settings, Ctx(enemies: 5));
        Assert.Equal(CombatSpellAction.MultiAttack, r1.Action);
        sut.MarkCast(r1, "a rat");

        CombatSpellDecision r2 = sut.Choose(settings, Ctx(enemies: 5));
        Assert.Equal(CombatSpellAction.MultiAttack, r2.Action);
        sut.MarkCast(r2, "a rat");

        // Cap (2) reached → fall to normal even though room is still full.
        CombatSpellDecision r3 = sut.Choose(settings, Ctx(enemies: 5));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, r3.Action);
    }

    [Fact]
    public void Choose_MultiAttack_StopsWhenManaInsufficient_FallsToNormal()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SpellManaThresholdMode = ThresholdMode.Absolute,
            MultiAttackSpell = Slot("star", minEnemies: 2, minMana: 30),
            NormalAttackSpell = Slot("harm", minMana: 10),
        };

        // Plenty of mana: multi fires.
        CombatSpellDecision r1 = sut.Choose(settings, Ctx(enemies: 5, mana: 50, maxMana: 200));
        Assert.Equal(CombatSpellAction.MultiAttack, r1.Action);

        // Mana now below multi's 30 but above normal's 10 → normal.
        CombatSpellDecision r2 = sut.Choose(settings, Ctx(enemies: 5, mana: 20, maxMana: 200));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, r2.Action);
    }

    // ----- 3b. Attack phase: normal → alternate → weapon ----------------

    [Fact]
    public void Choose_FallsThrough_Normal_Then_Alternate_Then_Weapon()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("harm", maxCasts: 1),
            AlternateAttackSpell = Slot("flame", maxCasts: 1),
        };

        CombatSpellDecision r1 = sut.Choose(settings, Ctx());
        Assert.Equal(CombatSpellAction.NormalAttackSpell, r1.Action);
        sut.MarkCast(r1, "a rat");

        // Normal cap reached → alternate.
        CombatSpellDecision r2 = sut.Choose(settings, Ctx());
        Assert.Equal(CombatSpellAction.AlternateAttackSpell, r2.Action);
        sut.MarkCast(r2, "a rat");

        // Both caps reached → weapon.
        CombatSpellDecision r3 = sut.Choose(settings, Ctx());
        Assert.Equal(CombatSpellAction.WeaponAttack, r3.Action);
        Assert.Null(r3.Spell);
    }

    [Fact]
    public void Choose_NoSpellsConfigured_AlwaysWeapon()
    {
        CombatSpellChooser sut = new();
        CombatSpellDecision d = sut.Choose(new CombatSettings(), Ctx());
        Assert.Equal(CombatSpellAction.WeaponAttack, d.Action);
    }

    [Fact]
    public void Choose_WhitespaceSpellName_TreatedAsUnconfigured()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("   "),
            AlternateAttackSpell = Slot("flame"),
        };

        CombatSpellDecision d = sut.Choose(settings, Ctx());
        Assert.Equal(CombatSpellAction.AlternateAttackSpell, d.Action);
    }

    // ----- Mana gating modes --------------------------------------------

    [Fact]
    public void ManaOk_Percentage_GatesOnShareOfMaxMana()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SpellManaThresholdMode = ThresholdMode.Percentage,
            NormalAttackSpell = Slot("harm", minMana: 50), // need >= 50% of max
        };

        // 40/100 = 40% < 50% → cannot cast → weapon.
        Assert.Equal(CombatSpellAction.WeaponAttack,
            sut.Choose(settings, Ctx(mana: 40, maxMana: 100)).Action);

        // 60/100 = 60% >= 50% → casts.
        Assert.Equal(CombatSpellAction.NormalAttackSpell,
            sut.Choose(settings, Ctx(mana: 60, maxMana: 100)).Action);
    }

    [Fact]
    public void ManaOk_Percentage_ZeroMaxMana_NeverCasts()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SpellManaThresholdMode = ThresholdMode.Percentage,
            NormalAttackSpell = Slot("harm", minMana: 1),
        };

        Assert.Equal(CombatSpellAction.WeaponAttack,
            sut.Choose(settings, Ctx(mana: 0, maxMana: 0)).Action);
    }

    [Fact]
    public void ManaOk_ZeroThreshold_AlwaysPasses()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SpellManaThresholdMode = ThresholdMode.Absolute,
            NormalAttackSpell = Slot("harm", minMana: 0),
        };

        Assert.Equal(CombatSpellAction.NormalAttackSpell,
            sut.Choose(settings, Ctx(mana: 0, maxMana: 0)).Action);
    }

    // ----- MarkCast / ResetForNewRoom bookkeeping -----------------------

    [Fact]
    public void MarkCast_WeaponAttack_NoOp()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("harm", maxCasts: 1),
        };

        // A weapon decision must not consume any spell counter.
        sut.MarkCast(CombatSpellDecision.Weapon, "a rat");

        CombatSpellDecision d = sut.Choose(settings, Ctx());
        Assert.Equal(CombatSpellAction.NormalAttackSpell, d.Action);
    }

    [Fact]
    public void Choose_IsPure_DoesNotMutateCounters()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("harm", maxCasts: 1),
        };

        // Calling Choose repeatedly without MarkCast must keep returning the
        // same decision — the chooser commits only via MarkCast.
        Assert.Equal(CombatSpellAction.NormalAttackSpell, sut.Choose(settings, Ctx()).Action);
        Assert.Equal(CombatSpellAction.NormalAttackSpell, sut.Choose(settings, Ctx()).Action);
        Assert.Equal(CombatSpellAction.NormalAttackSpell, sut.Choose(settings, Ctx()).Action);
    }

    [Fact]
    public void ResetForNewRoom_ClearsAllBookkeeping()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            AreaDebuffSpell = Slot("blindall", minEnemies: 2),
            SingleTargetDebuffSpell = Slot("weaken"),
            MultiAttackSpell = Slot("star", minEnemies: 2, maxCasts: 1),
            NormalAttackSpell = Slot("harm", maxCasts: 1),
        };

        // Exhaust area + multi + normal in the first room.
        CombatSpellDecision area = sut.Choose(settings, Ctx(enemies: 3));
        Assert.Equal(CombatSpellAction.AreaDebuff, area.Action);
        sut.MarkCast(area, "a rat");
        CombatSpellDecision multi = sut.Choose(settings, Ctx(enemies: 3));
        Assert.Equal(CombatSpellAction.MultiAttack, multi.Action);
        sut.MarkCast(multi, "a rat");
        CombatSpellDecision normal = sut.Choose(settings, Ctx(enemies: 3));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, normal.Action);
        sut.MarkCast(normal, "a rat");

        // Now weapon (everything spent).
        Assert.Equal(CombatSpellAction.WeaponAttack, sut.Choose(settings, Ctx(enemies: 3)).Action);

        // New room: bookkeeping wiped → area debuff available again.
        sut.ResetForNewRoom();
        CombatSpellDecision afterReset = sut.Choose(settings, Ctx(enemies: 3));
        Assert.Equal(CombatSpellAction.AreaDebuff, afterReset.Action);
    }

    [Fact]
    public void ResetForNewRoom_ReArmsSingleDebuffPerTarget()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SingleTargetDebuffSpell = Slot("weaken"),
            NormalAttackSpell = Slot("harm"),
        };

        CombatSpellDecision r1 = sut.Choose(settings, Ctx(target: "a rat"));
        sut.MarkCast(r1, "a rat");
        Assert.Equal(CombatSpellAction.NormalAttackSpell,
            sut.Choose(settings, Ctx(target: "a rat")).Action);

        // Same instance name in a fresh room must be debuffable again.
        sut.ResetForNewRoom();
        Assert.Equal(CombatSpellAction.SingleDebuff,
            sut.Choose(settings, Ctx(target: "a rat")).Action);
    }

    // ----- Full per-room ordering walk-through ---------------------------

    [Fact]
    public void Choose_FullRoomSequence_AreaThenMultiThenNormalThenWeapon()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SpellManaThresholdMode = ThresholdMode.Absolute,
            AreaDebuffSpell = Slot("blindall", minEnemies: 2),
            MultiAttackSpell = Slot("star", minEnemies: 2, maxCasts: 1),
            NormalAttackSpell = Slot("harm", maxCasts: 1),
            AlternateAttackSpell = Slot("flame", maxCasts: 1),
        };

        // Round 1 — area debuff (once per room).
        CombatSpellDecision r1 = sut.Choose(settings, Ctx(enemies: 4));
        Assert.Equal(CombatSpellAction.AreaDebuff, r1.Action);
        sut.MarkCast(r1, "a rat");

        // Round 2 — multi-attack (qualified, under cap).
        CombatSpellDecision r2 = sut.Choose(settings, Ctx(enemies: 4));
        Assert.Equal(CombatSpellAction.MultiAttack, r2.Action);
        sut.MarkCast(r2, "a rat");

        // Round 3 — multi cap reached → normal.
        CombatSpellDecision r3 = sut.Choose(settings, Ctx(enemies: 4));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, r3.Action);
        sut.MarkCast(r3, "a rat");

        // Round 4 — normal cap reached → alternate.
        CombatSpellDecision r4 = sut.Choose(settings, Ctx(enemies: 4));
        Assert.Equal(CombatSpellAction.AlternateAttackSpell, r4.Action);
        sut.MarkCast(r4, "a rat");

        // Round 5 — everything spent → weapon.
        CombatSpellDecision r5 = sut.Choose(settings, Ctx(enemies: 4));
        Assert.Equal(CombatSpellAction.WeaponAttack, r5.Action);
    }
}
