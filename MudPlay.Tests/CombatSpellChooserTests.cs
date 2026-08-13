using MudPlay.Game.Combat;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// Pins <see cref="CombatSpellChooser"/> — the per-round combat-spell decision
/// unit. <see cref="CombatSpellChooser.Choose"/> resolves the round's <b>combat
/// action</b>: backstab gate → attack (multi-attack while qualified → normal →
/// alternate → weapon). Debuffing is an <b>in-between action</b> resolved
/// separately by <see cref="CombatSpellChooser.ChooseDebuff"/> (area
/// once-per-room XOR single once-per-target), so the two are pinned
/// independently. Mana gating
/// (<see cref="ThresholdMode.Percentage"/> vs <see cref="ThresholdMode.Absolute"/>)
/// and per-room cast caps are exercised per branch.
/// </summary>
public sealed class CombatSpellChooserTests
{
    private static CombatSpellSlot Slot(
        string? name, int minEnemies = 0, int? maxCasts = null, int minMana = 0) => new()
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
    public void Choose_BackstabPending_FiresBackstab_NoSpellPreempts()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            AreaDebuffSpell = Slot("blind"),
            MultiAttackSpell = Slot("star"),
            NormalAttackSpell = Slot("harm"),
        };

        CombatSpellDecision d = sut.Choose(settings, Ctx(enemies: 5, backstabPending: true));

        Assert.Equal(CombatSpellAction.Backstab, d.Action);
        Assert.Null(d.Spell);
    }

    [Fact]
    public void Choose_PhysicalFirst_SuppressesSpell()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("harm"),
            // Physical first — always swing, never cast an attack spell.
            ActionOrder = CombatActionOrder.PhysicalFirst,
        };

        CombatSpellDecision d = sut.Choose(settings, Ctx(enemies: 1));

        Assert.Equal(CombatSpellAction.WeaponAttack, d.Action);
        Assert.Null(d.Spell);
    }

    [Fact]
    public void Choose_BackstabPending_FiresEvenWhenPhysicalFirst()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("harm"),
            // The backstab opener sits outside the ActionOrder choice — it fires
            // first when pending regardless of Physical-first.
            ActionOrder = CombatActionOrder.PhysicalFirst,
        };

        CombatSpellDecision d = sut.Choose(settings, Ctx(enemies: 1, backstabPending: true));

        Assert.Equal(CombatSpellAction.Backstab, d.Action);
        Assert.Null(d.Spell);
    }

    // ----- 1b. Physical-first weapon-ineffective fallback ----------------
    // PhysicalFirst normally swings; it reaches for the attack-spell cascade only
    // when the engine reports the weapon path exhausted (WeaponIneffective) — the
    // normal weapon can't damage the target and there's no working alternate.

    private static CombatSpellContext PhysCtx(
        bool weaponIneffective, bool backstabPending = false) =>
        new(EnemyCount: 1, TargetRawName: "a rat", Mana: 100, MaxMana: 100,
            BackstabPending: backstabPending, WeaponIneffective: weaponIneffective);

    [Fact]
    public void Choose_PhysicalFirst_WeaponIneffective_FallsToSpell()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("harm"),
            ActionOrder = CombatActionOrder.PhysicalFirst,
        };

        // Weapon path exhausted → the cascade fires even under Physical-first.
        CombatSpellDecision d = sut.Choose(settings, PhysCtx(weaponIneffective: true));

        Assert.Equal(CombatSpellAction.NormalAttackSpell, d.Action);
        Assert.Equal("harm", d.Spell);
    }

    [Fact]
    public void Choose_PhysicalFirst_WeaponEffective_Swings()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("harm"),
            ActionOrder = CombatActionOrder.PhysicalFirst,
        };

        // Weapon still hits → swing, spell stays suppressed.
        CombatSpellDecision d = sut.Choose(settings, PhysCtx(weaponIneffective: false));

        Assert.Equal(CombatSpellAction.WeaponAttack, d.Action);
        Assert.Null(d.Spell);
    }

    [Fact]
    public void Choose_PhysicalFirst_WeaponIneffective_NoSpell_Swings()
    {
        CombatSpellChooser sut = new();
        // No attack spell configured — nothing to fall back to, so even with the
        // weapon path exhausted the round stays a (useless) swing.
        CombatSettings settings = new() { ActionOrder = CombatActionOrder.PhysicalFirst };

        CombatSpellDecision d = sut.Choose(settings, PhysCtx(weaponIneffective: true));

        Assert.Equal(CombatSpellAction.WeaponAttack, d.Action);
        Assert.Null(d.Spell);
    }

    [Fact]
    public void Choose_PhysicalFirst_WeaponIneffective_BackstabStillFirst()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("harm"),
            ActionOrder = CombatActionOrder.PhysicalFirst,
        };

        // The opener outranks the weapon-ineffective fallback too.
        CombatSpellDecision d = sut.Choose(
            settings, PhysCtx(weaponIneffective: true, backstabPending: true));

        Assert.Equal(CombatSpellAction.Backstab, d.Action);
    }

    [Fact]
    public void Choose_SpellsFirst_IgnoresWeaponIneffective()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("harm"),
            ActionOrder = CombatActionOrder.SpellsFirst,
        };

        // SpellsFirst casts regardless of the weapon flag — the flag only gates
        // the Physical-first path.
        Assert.Equal(CombatSpellAction.NormalAttackSpell,
            sut.Choose(settings, PhysCtx(weaponIneffective: false)).Action);
        Assert.Equal(CombatSpellAction.NormalAttackSpell,
            sut.Choose(settings, PhysCtx(weaponIneffective: true)).Action);
    }

    // ----- 2. In-between debuff: area once-per-room, excludes single -----
    // Debuffs are in-between actions resolved by ChooseDebuff, NOT combat
    // actions — Choose never returns a debuff. Each case pins ChooseDebuff
    // for the debuff decision and Choose for the round's combat action.

    [Fact]
    public void ChooseDebuff_Area_FiresOncePerRoom_AndExcludesSingle()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            AreaDebuffSpell = Slot("blindall", minEnemies: 2),
            SingleTargetDebuffSpell = Slot("weaken"),
            NormalAttackSpell = Slot("harm"),
        };

        // First round: area debuff is due.
        CombatSpellDecision? first = sut.ChooseDebuff(settings, Ctx(enemies: 3));
        Assert.Equal(CombatSpellAction.AreaDebuff, first?.Action);
        Assert.Equal("blindall", first?.Spell);
        sut.MarkCast(first!.Value, "a rat");

        // Next round: area already cast → no debuff (area excludes single).
        Assert.Null(sut.ChooseDebuff(settings, Ctx(enemies: 3)));

        // The combat action (Choose) never returns a debuff — it attacks.
        CombatSpellDecision attack = sut.Choose(settings, Ctx(enemies: 3));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, attack.Action);
        Assert.Equal("harm", attack.Spell);
    }

    [Fact]
    public void ChooseDebuff_Area_BelowMinEnemies_Skipped()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            AreaDebuffSpell = Slot("blindall", minEnemies: 4),
            NormalAttackSpell = Slot("harm"),
        };

        // Only 2 enemies, area needs 4 → no debuff (area never falls to single).
        Assert.Null(sut.ChooseDebuff(settings, Ctx(enemies: 2)));
        // Combat action falls straight to the attack spell.
        Assert.Equal(CombatSpellAction.NormalAttackSpell,
            sut.Choose(settings, Ctx(enemies: 2)).Action);
    }

    // ----- 2b. Single-target debuff: once per target --------------------

    [Fact]
    public void ChooseDebuff_Single_FiresOncePerTarget()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SingleTargetDebuffSpell = Slot("weaken"),
            NormalAttackSpell = Slot("harm"),
        };

        // Target A: debuff due first.
        CombatSpellDecision? a1 = sut.ChooseDebuff(settings, Ctx(target: "a rat"));
        Assert.Equal(CombatSpellAction.SingleDebuff, a1?.Action);
        sut.MarkCast(a1!.Value, "a rat");

        // Same target again: already debuffed → no debuff.
        Assert.Null(sut.ChooseDebuff(settings, Ctx(target: "a rat")));

        // New target: debuff due again.
        CombatSpellDecision? b1 = sut.ChooseDebuff(settings, Ctx(target: "a kobold"));
        Assert.Equal(CombatSpellAction.SingleDebuff, b1?.Action);
        sut.MarkCast(b1!.Value, "a kobold");

        Assert.Null(sut.ChooseDebuff(settings, Ctx(target: "a kobold")));
    }

    [Fact]
    public void ChooseDebuff_Single_TargetMatchIsCaseInsensitive()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SingleTargetDebuffSpell = Slot("weaken"),
            NormalAttackSpell = Slot("harm"),
        };

        CombatSpellDecision? first = sut.ChooseDebuff(settings, Ctx(target: "A Rat"));
        Assert.Equal(CombatSpellAction.SingleDebuff, first?.Action);
        sut.MarkCast(first!.Value, "A Rat");

        Assert.Null(sut.ChooseDebuff(settings, Ctx(target: "a rat")));
    }

    [Fact]
    public void ChooseDebuff_Single_HonoursMaxCastsPerRoom()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SingleTargetDebuffSpell = Slot("weaken", maxCasts: 1),
            NormalAttackSpell = Slot("harm"),
        };

        CombatSpellDecision? a = sut.ChooseDebuff(settings, Ctx(target: "a rat"));
        Assert.Equal(CombatSpellAction.SingleDebuff, a?.Action);
        sut.MarkCast(a!.Value, "a rat");

        // New target, but the room-wide single-debuff cap (1) is reached →
        // no more debuffs.
        Assert.Null(sut.ChooseDebuff(settings, Ctx(target: "a kobold")));
    }

    [Fact]
    public void ChooseDebuff_Single_MaxCastsZero_NeverCasts()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            // 0 is an explicit off switch, not "unlimited".
            SingleTargetDebuffSpell = Slot("weaken", maxCasts: 0),
            NormalAttackSpell = Slot("harm"),
        };

        Assert.Null(sut.ChooseDebuff(settings, Ctx(target: "a rat")));
    }

    [Fact]
    public void Choose_MultiAttack_MaxCastsZero_NeverCasts()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            // Configured spell, but a 0 cap means it must never fire.
            MultiAttackSpell = Slot("star", minEnemies: 3, maxCasts: 0),
            NormalAttackSpell = Slot("harm"),
        };

        CombatSpellDecision r = sut.Choose(settings, Ctx(enemies: 4));
        Assert.NotEqual(CombatSpellAction.MultiAttack, r.Action);
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
    public void ManaOk_Percentage_MeetsRoundedThreshold_NotRawFraction()
    {
        // Report paradigm-20260805-224742: 82% of a 66 max MA is 54.12; the Settings
        // conversion label rounds that to "54", so 54 mana must CAST (matching what
        // the user set as their reserve), not swap to physical because 54/66 = 81.8%
        // falls a fraction under 82%.
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SpellManaThresholdMode = ThresholdMode.Percentage,
            NormalAttackSpell = Slot("disr", minMana: 82),
        };

        // 54 == Round(66 * 0.82) → casts.
        Assert.Equal(CombatSpellAction.NormalAttackSpell,
            sut.Choose(settings, Ctx(mana: 54, maxMana: 66)).Action);

        // 53 is below the rounded 54-mana reserve → weapon.
        Assert.Equal(CombatSpellAction.WeaponAttack,
            sut.Choose(settings, Ctx(mana: 53, maxMana: 66)).Action);
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

    // ----- Per-target weapon latch (mana reserve / MaxCasts) ------------
    // Once a single-target attack spell has been casting at a target and its
    // cascade lapses (mana reserve unmet OR MaxCasts rounds spent), the chooser
    // commits to the weapon for that target — a mana-regen tick must NOT flip it
    // back to the spell mid-fight (CONFIRMED per-target latch).

    private static CombatSpellContext IneffectiveCtx(int mana, int maxMana = 100) =>
        new(EnemyCount: 1, TargetRawName: "a rat", Mana: mana, MaxMana: maxMana,
            BackstabPending: false, WeaponIneffective: true);

    [Fact]
    public void Latch_ManaReserveTrips_StaysOnWeaponWhenManaRegens()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SpellManaThresholdMode = ThresholdMode.Percentage,
            NormalAttackSpell = Slot("harm", minMana: 50), // reserve 50% of max
        };

        // Round 1: 60% ≥ 50% → cast, and a real spell round happened.
        CombatSpellDecision r1 = sut.Choose(settings, Ctx(mana: 60, maxMana: 100));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, r1.Action);
        sut.MarkCast(r1, "a rat");

        // Round 2: 40% < 50% → reserve unmet → drop to the weapon and latch.
        Assert.Equal(CombatSpellAction.WeaponAttack,
            sut.Choose(settings, Ctx(mana: 40, maxMana: 100)).Action);

        // Round 3: mana regenerated to 90% — WITHOUT the latch this would re-cast;
        // WITH it we stay on the weapon for this target.
        Assert.Equal(CombatSpellAction.WeaponAttack,
            sut.Choose(settings, Ctx(mana: 90, maxMana: 100)).Action);
    }

    [Fact]
    public void Latch_ClearsOnNewTarget_ReCastsFresh()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SpellManaThresholdMode = ThresholdMode.Percentage,
            NormalAttackSpell = Slot("harm", minMana: 50),
        };

        CombatSpellDecision r1 = sut.Choose(settings, Ctx(mana: 60, maxMana: 100));
        sut.MarkCast(r1, "a rat");
        Assert.Equal(CombatSpellAction.WeaponAttack,
            sut.Choose(settings, Ctx(mana: 40, maxMana: 100)).Action);   // latched

        // New target → the latch clears; a healthy-mana round casts again.
        sut.ResetForNewTarget();
        Assert.Equal(CombatSpellAction.NormalAttackSpell,
            sut.Choose(settings, Ctx(mana: 90, maxMana: 100)).Action);
    }

    [Fact]
    public void Latch_NotArmed_WhenSpellNeverCastOnTarget()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SpellManaThresholdMode = ThresholdMode.Percentage,
            NormalAttackSpell = Slot("harm", minMana: 50),
        };

        // Fresh target, mana too low to ever start — weapon, but NOT latched.
        Assert.Equal(CombatSpellAction.WeaponAttack,
            sut.Choose(settings, Ctx(mana: 40, maxMana: 100)).Action);

        // Mana regenerates → the spell starts (the latch only arms after a real
        // spell round, so a never-cast target isn't stuck on the weapon).
        Assert.Equal(CombatSpellAction.NormalAttackSpell,
            sut.Choose(settings, Ctx(mana: 90, maxMana: 100)).Action);
    }

    [Fact]
    public void Latch_NotArmed_WhenWeaponCannotHitTarget()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SpellManaThresholdMode = ThresholdMode.Percentage,
            NormalAttackSpell = Slot("harm", minMana: 50),
        };

        // Weapon is proven ineffective — the spell is the only kill means.
        CombatSpellDecision r1 = sut.Choose(settings, IneffectiveCtx(mana: 60));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, r1.Action);
        sut.MarkCast(r1, "a rat");

        // Reserve unmet this round — we don't latch (a useless swing helps nobody);
        // we wait for mana instead.
        sut.Choose(settings, IneffectiveCtx(mana: 40));

        // Mana back up → the spell resumes rather than staying on the weapon.
        Assert.Equal(CombatSpellAction.NormalAttackSpell,
            sut.Choose(settings, IneffectiveCtx(mana: 90)).Action);
    }

    [Fact]
    public void Latch_DoesNotSuppressMultiAttack()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SpellManaThresholdMode = ThresholdMode.Percentage,
            MultiAttackSpell = Slot("star", minEnemies: 2),
            NormalAttackSpell = Slot("harm", minMana: 50),
        };

        // Single mob → normal fires, then its reserve trips → latch to weapon.
        CombatSpellDecision r1 = sut.Choose(settings, Ctx(enemies: 1, mana: 60, maxMana: 100));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, r1.Action);
        sut.MarkCast(r1, "a rat");
        Assert.Equal(CombatSpellAction.WeaponAttack,
            sut.Choose(settings, Ctx(enemies: 1, mana: 40, maxMana: 100)).Action);

        // Room fills to 5 → the room-scoped AoE nuke is NOT suppressed by the
        // single-target latch.
        Assert.Equal(CombatSpellAction.MultiAttack,
            sut.Choose(settings, Ctx(enemies: 5, mana: 90, maxMana: 100)).Action);
    }

    [Fact]
    public void Latch_MaxCastsRoundsSpent_DropsToWeapon()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("harm", maxCasts: 2), // 2 rounds then switch
        };

        CombatSpellDecision r1 = sut.Choose(settings, Ctx());
        Assert.Equal(CombatSpellAction.NormalAttackSpell, r1.Action);
        sut.MarkCast(r1, "a rat");

        CombatSpellDecision r2 = sut.Choose(settings, Ctx());
        Assert.Equal(CombatSpellAction.NormalAttackSpell, r2.Action);
        sut.MarkCast(r2, "a rat");

        // Two rounds spent → drop to the weapon and stay there for the target.
        Assert.Equal(CombatSpellAction.WeaponAttack, sut.Choose(settings, Ctx()).Action);
        Assert.Equal(CombatSpellAction.WeaponAttack, sut.Choose(settings, Ctx()).Action);
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

        // Exhaust the area debuff (in-between) + multi + normal (combat action).
        CombatSpellDecision? area = sut.ChooseDebuff(settings, Ctx(enemies: 3));
        Assert.Equal(CombatSpellAction.AreaDebuff, area?.Action);
        sut.MarkCast(area!.Value, "a rat");
        CombatSpellDecision multi = sut.Choose(settings, Ctx(enemies: 3));
        Assert.Equal(CombatSpellAction.MultiAttack, multi.Action);
        sut.MarkCast(multi, "a rat");
        CombatSpellDecision normal = sut.Choose(settings, Ctx(enemies: 3));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, normal.Action);
        sut.MarkCast(normal, "a rat");

        // Debuff spent (area excludes single) + attack spells spent → weapon.
        Assert.Null(sut.ChooseDebuff(settings, Ctx(enemies: 3)));
        Assert.Equal(CombatSpellAction.WeaponAttack, sut.Choose(settings, Ctx(enemies: 3)).Action);

        // New room: bookkeeping wiped → area debuff available again.
        sut.ResetForNewRoom();
        CombatSpellDecision? afterReset = sut.ChooseDebuff(settings, Ctx(enemies: 3));
        Assert.Equal(CombatSpellAction.AreaDebuff, afterReset?.Action);
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

        CombatSpellDecision? r1 = sut.ChooseDebuff(settings, Ctx(target: "a rat"));
        Assert.Equal(CombatSpellAction.SingleDebuff, r1?.Action);
        sut.MarkCast(r1!.Value, "a rat");
        Assert.Null(sut.ChooseDebuff(settings, Ctx(target: "a rat")));

        // Same instance name in a fresh room must be debuffable again.
        sut.ResetForNewRoom();
        Assert.Equal(CombatSpellAction.SingleDebuff,
            sut.ChooseDebuff(settings, Ctx(target: "a rat"))?.Action);
    }

    // ----- Deterministic level-immunity gating (LevelBlockedActions) -----

    private static CombatSpellContext LevelBlockedCtx(
        params CombatSpellAction[] blocked) =>
        new(EnemyCount: 3, TargetRawName: "a rat", Mana: 100, MaxMana: 100,
            BackstabPending: false,
            LevelBlockedActions: new HashSet<CombatSpellAction>(blocked));

    [Fact]
    public void ChooseDebuff_SingleLevelBlocked_NoDebuff_RoundAttacks()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SingleTargetDebuffSpell = Slot("weaken"),
            NormalAttackSpell = Slot("harm"),
        };

        // SingleDebuff's ReqLevel < the target's SpellImmu → engine marks it
        // level-blocked → no debuff is offered and the combat action attacks.
        Assert.Null(sut.ChooseDebuff(
            settings, LevelBlockedCtx(CombatSpellAction.SingleDebuff)));
        CombatSpellDecision d = sut.Choose(
            settings, LevelBlockedCtx(CombatSpellAction.SingleDebuff));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, d.Action);
        Assert.Equal("harm", d.Spell);
    }

    [Fact]
    public void Choose_NormalAttackSpellLevelBlocked_FallsToAlternate()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("harm"),
            AlternateAttackSpell = Slot("flame"),
        };

        CombatSpellDecision d = sut.Choose(
            settings, LevelBlockedCtx(CombatSpellAction.NormalAttackSpell));
        Assert.Equal(CombatSpellAction.AlternateAttackSpell, d.Action);
        Assert.Equal("flame", d.Spell);
    }

    [Fact]
    public void Choose_BothAttackSpellsLevelBlocked_FallsToWeapon()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("harm"),
            AlternateAttackSpell = Slot("flame"),
        };

        CombatSpellDecision d = sut.Choose(
            settings,
            LevelBlockedCtx(
                CombatSpellAction.NormalAttackSpell,
                CombatSpellAction.AlternateAttackSpell));
        Assert.Equal(CombatSpellAction.WeaponAttack, d.Action);
        Assert.Null(d.Spell);
    }

    [Fact]
    public void ChooseDebuff_NotLevelBlocked_FiresNormally()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SingleTargetDebuffSpell = Slot("weaken"),
            NormalAttackSpell = Slot("harm"),
        };

        // An empty (but non-null) block set leaves the debuff eligible.
        CombatSpellDecision? d = sut.ChooseDebuff(settings, LevelBlockedCtx());
        Assert.Equal(CombatSpellAction.SingleDebuff, d?.Action);
        Assert.Equal("weaken", d?.Spell);
    }

    [Fact]
    public void ChooseDebuff_Area_NeverLevelBlocked()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            AreaDebuffSpell = Slot("blindall", minEnemies: 2),
            NormalAttackSpell = Slot("harm"),
        };

        // Even if the set names AreaDebuff (it never does in practice), the
        // area branch ignores level-block — room spells hit the whole room.
        CombatSpellDecision? d = sut.ChooseDebuff(
            settings, LevelBlockedCtx(CombatSpellAction.AreaDebuff));
        Assert.Equal(CombatSpellAction.AreaDebuff, d?.Action);
        Assert.Equal("blindall", d?.Spell);
    }

    [Fact]
    public void Choose_MultiAttack_NeverLevelBlocked()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            MultiAttackSpell = Slot("star", minEnemies: 2),
            NormalAttackSpell = Slot("harm"),
        };

        CombatSpellDecision d = sut.Choose(
            settings, LevelBlockedCtx(CombatSpellAction.MultiAttack));
        Assert.Equal(CombatSpellAction.MultiAttack, d.Action);
        Assert.Equal("star", d.Spell);
    }

    // ----- Deterministic elemental-resist gating (ResistBlockedActions) --
    // A target that resists an attack spell's damage element ≥ 100% neutralizes
    // it (0 damage / heal), so the engine marks the slot resist-blocked and the
    // chooser skips it down the cascade — exactly like level-block, but only the
    // two single-target attack slots are ever named (elemental only; M.R. and
    // poison spells never appear here).

    private static CombatSpellContext ResistBlockedCtx(
        params CombatSpellAction[] blocked) =>
        new(EnemyCount: 3, TargetRawName: "a skeleton", Mana: 100, MaxMana: 100,
            BackstabPending: false,
            ResistBlockedActions: new HashSet<CombatSpellAction>(blocked));

    [Fact]
    public void Choose_NormalAttackSpellResistBlocked_FallsToAlternate()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("cold"),
            AlternateAttackSpell = Slot("flame"),
        };

        CombatSpellDecision d = sut.Choose(
            settings, ResistBlockedCtx(CombatSpellAction.NormalAttackSpell));
        Assert.Equal(CombatSpellAction.AlternateAttackSpell, d.Action);
        Assert.Equal("flame", d.Spell);
    }

    [Fact]
    public void Choose_BothAttackSpellsResistBlocked_FallsToWeapon()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("cold"),
            AlternateAttackSpell = Slot("frost"),
        };

        CombatSpellDecision d = sut.Choose(
            settings,
            ResistBlockedCtx(
                CombatSpellAction.NormalAttackSpell,
                CombatSpellAction.AlternateAttackSpell));
        Assert.Equal(CombatSpellAction.WeaponAttack, d.Action);
        Assert.Null(d.Spell);
    }

    [Fact]
    public void Choose_NotResistBlocked_FiresNormally()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("cold"),
        };

        // An empty (but non-null) block set leaves the attack spell eligible.
        CombatSpellDecision d = sut.Choose(settings, ResistBlockedCtx());
        Assert.Equal(CombatSpellAction.NormalAttackSpell, d.Action);
        Assert.Equal("cold", d.Spell);
    }

    [Fact]
    public void Choose_MultiAttack_NeverResistBlocked()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            MultiAttackSpell = Slot("star", minEnemies: 2),
            NormalAttackSpell = Slot("cold"),
        };

        // Even if the set names MultiAttack (it never does in practice), the
        // multi branch ignores resist — room spells hit the whole room, so one
        // resistant occupant doesn't disqualify them.
        CombatSpellDecision d = sut.Choose(
            settings, ResistBlockedCtx(CombatSpellAction.MultiAttack));
        Assert.Equal(CombatSpellAction.MultiAttack, d.Action);
        Assert.Equal("star", d.Spell);
    }

    [Fact]
    public void Choose_NormalResistBlocked_AlternateLevelBlocked_FallsToWeapon()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("cold"),
            AlternateAttackSpell = Slot("harm"),
        };

        // The two deterministic gates compose: Normal is resist-blocked, Alternate
        // is level-blocked (SpellImmu), so both single-target spells are skipped
        // and the round falls through to the weapon swing.
        CombatSpellContext ctx = new(
            EnemyCount: 1, TargetRawName: "a skeleton", Mana: 100, MaxMana: 100,
            BackstabPending: false,
            LevelBlockedActions: new HashSet<CombatSpellAction>
                { CombatSpellAction.AlternateAttackSpell },
            ResistBlockedActions: new HashSet<CombatSpellAction>
                { CombatSpellAction.NormalAttackSpell });

        CombatSpellDecision d = sut.Choose(settings, ctx);
        Assert.Equal(CombatSpellAction.WeaponAttack, d.Action);
        Assert.Null(d.Spell);
    }

    // ----- Per-monster spell overrides ----------------------------------
    // A per-monster override substitutes its cast-code at the matching rung
    // (attack → NormalAttackSpell, pre-attack → SingleTargetDebuffSpell),
    // bypassing the effectiveness gates (immunity / level / resist) but keeping
    // the physical constraints (mana floor, once-per-target, cast cap).

    private static CombatSpellContext OverrideCtx(
        string? attackOverride = null, int? attackCap = null,
        string? preOverride = null, int? preCap = null,
        string target = "a rat", int mana = 100, int maxMana = 100,
        IReadOnlySet<CombatSpellAction>? immune = null,
        IReadOnlySet<CombatSpellAction>? levelBlocked = null,
        IReadOnlySet<CombatSpellAction>? resistBlocked = null) =>
        new(EnemyCount: 1, TargetRawName: target, Mana: mana, MaxMana: maxMana,
            BackstabPending: false,
            ImmuneAttackSpells: immune,
            LevelBlockedActions: levelBlocked,
            ResistBlockedActions: resistBlocked,
            OverrideAttackSpell: attackOverride,
            OverrideAttackMaxCasts: attackCap,
            OverridePreAttackSpell: preOverride,
            OverridePreAttackMaxCasts: preCap);

    [Fact]
    public void Choose_AttackOverride_SubstitutesForNormalSlot()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new() { NormalAttackSpell = Slot("harm") };

        CombatSpellDecision d = sut.Choose(
            settings, OverrideCtx(attackOverride: "fireball", attackCap: 3));

        Assert.Equal(CombatSpellAction.NormalAttackSpell, d.Action);
        Assert.Equal("fireball", d.Spell);   // the override, not the configured "harm"
    }

    [Fact]
    public void Choose_AttackOverride_BypassesImmuneLevelResistGates()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("harm"),
            AlternateAttackSpell = Slot("flame"),
        };

        // The normal rung is flagged immune AND level-blocked AND resist-blocked
        // — all three would push a configured slot down the cascade. The override
        // ignores them and fires anyway (the user vouched it works).
        CombatSpellContext ctx = OverrideCtx(
            attackOverride: "fireball", attackCap: 5,
            immune: new HashSet<CombatSpellAction> { CombatSpellAction.NormalAttackSpell },
            levelBlocked: new HashSet<CombatSpellAction> { CombatSpellAction.NormalAttackSpell },
            resistBlocked: new HashSet<CombatSpellAction> { CombatSpellAction.NormalAttackSpell });

        CombatSpellDecision d = sut.Choose(settings, ctx);

        Assert.Equal(CombatSpellAction.NormalAttackSpell, d.Action);
        Assert.Equal("fireball", d.Spell);
    }

    [Fact]
    public void Choose_AttackOverride_HonoursCap_ThenFallsToAlternate()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("harm"),
            AlternateAttackSpell = Slot("flame"),
        };
        CombatSpellContext ctx = OverrideCtx(attackOverride: "fireball", attackCap: 1);

        CombatSpellDecision r1 = sut.Choose(settings, ctx);
        Assert.Equal("fireball", r1.Spell);
        sut.MarkCast(r1, "a rat");

        // Override cap (1) reached → the configured normal slot is skipped
        // (mutually exclusive with the override) → alternate.
        CombatSpellDecision r2 = sut.Choose(settings, ctx);
        Assert.Equal(CombatSpellAction.AlternateAttackSpell, r2.Action);
        Assert.Equal("flame", r2.Spell);
    }

    [Fact]
    public void Choose_AttackOverride_HonoursNormalSlotManaFloor()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SpellManaThresholdMode = ThresholdMode.Absolute,
            NormalAttackSpell = Slot("harm", minMana: 30),
        };

        // Below the rung's mana floor → override can't fire → weapon.
        Assert.Equal(CombatSpellAction.WeaponAttack,
            sut.Choose(settings, OverrideCtx(
                attackOverride: "fireball", attackCap: 5, mana: 20, maxMana: 200)).Action);

        // At/above the floor → override fires.
        Assert.Equal("fireball",
            sut.Choose(settings, OverrideCtx(
                attackOverride: "fireball", attackCap: 5, mana: 40, maxMana: 200)).Spell);
    }

    [Fact]
    public void ChooseDebuff_PreAttackOverride_SubstitutesForSingleSlot()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new() { SingleTargetDebuffSpell = Slot("weaken") };

        CombatSpellDecision? d = sut.ChooseDebuff(
            settings, OverrideCtx(preOverride: "curse", preCap: 3));

        Assert.Equal(CombatSpellAction.SingleDebuff, d?.Action);
        Assert.Equal("curse", d?.Spell);
    }

    [Fact]
    public void ChooseDebuff_PreAttackOverride_BypassesLevelBlock()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new() { SingleTargetDebuffSpell = Slot("weaken") };

        CombatSpellContext ctx = OverrideCtx(
            preOverride: "curse", preCap: 3,
            levelBlocked: new HashSet<CombatSpellAction> { CombatSpellAction.SingleDebuff });

        CombatSpellDecision? d = sut.ChooseDebuff(settings, ctx);

        Assert.Equal(CombatSpellAction.SingleDebuff, d?.Action);
        Assert.Equal("curse", d?.Spell);
    }

    [Fact]
    public void ChooseDebuff_PreAttackOverride_FiresOncePerTarget()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new() { SingleTargetDebuffSpell = Slot("weaken") };

        CombatSpellDecision? a1 = sut.ChooseDebuff(
            settings, OverrideCtx(preOverride: "curse", preCap: 5, target: "a rat"));
        Assert.Equal("curse", a1?.Spell);
        sut.MarkCast(a1!.Value, "a rat");

        // Same target → already debuffed → nothing.
        Assert.Null(sut.ChooseDebuff(
            settings, OverrideCtx(preOverride: "curse", preCap: 5, target: "a rat")));

        // New target → override fires again.
        CombatSpellDecision? b1 = sut.ChooseDebuff(
            settings, OverrideCtx(preOverride: "curse", preCap: 5, target: "a kobold"));
        Assert.Equal("curse", b1?.Spell);
    }

    [Fact]
    public void ChooseDebuff_PreAttackOverride_HonoursCap()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new() { SingleTargetDebuffSpell = Slot("weaken") };

        CombatSpellDecision? a = sut.ChooseDebuff(
            settings, OverrideCtx(preOverride: "curse", preCap: 1, target: "a rat"));
        Assert.Equal("curse", a?.Spell);
        sut.MarkCast(a!.Value, "a rat");

        // Room-wide cap (1) reached → a new target gets nothing.
        Assert.Null(sut.ChooseDebuff(
            settings, OverrideCtx(preOverride: "curse", preCap: 1, target: "a kobold")));
    }

    // ----- Full per-room ordering walk-through ---------------------------

    [Fact]
    public void FullRoomSequence_DebuffOnce_ThenMultiNormalAltWeapon()
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

        // In-between debuff (once per room) — resolved by ChooseDebuff.
        CombatSpellDecision? debuff = sut.ChooseDebuff(settings, Ctx(enemies: 4));
        Assert.Equal(CombatSpellAction.AreaDebuff, debuff?.Action);
        sut.MarkCast(debuff!.Value, "a rat");
        Assert.Null(sut.ChooseDebuff(settings, Ctx(enemies: 4)));

        // Combat-action round 1 — multi-attack (qualified, under cap).
        CombatSpellDecision r1 = sut.Choose(settings, Ctx(enemies: 4));
        Assert.Equal(CombatSpellAction.MultiAttack, r1.Action);
        sut.MarkCast(r1, "a rat");

        // Round 2 — multi cap reached → normal.
        CombatSpellDecision r2 = sut.Choose(settings, Ctx(enemies: 4));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, r2.Action);
        sut.MarkCast(r2, "a rat");

        // Round 3 — normal cap reached → alternate.
        CombatSpellDecision r3 = sut.Choose(settings, Ctx(enemies: 4));
        Assert.Equal(CombatSpellAction.AlternateAttackSpell, r3.Action);
        sut.MarkCast(r3, "a rat");

        // Round 4 — everything spent → weapon.
        CombatSpellDecision r4 = sut.Choose(settings, Ctx(enemies: 4));
        Assert.Equal(CombatSpellAction.WeaponAttack, r4.Action);
    }

    // ----- Alternating action orders (per-round spell↔physical) ---------
    // The two Alternate* orders resolve their spell-vs-physical preference per
    // round and pass it in via ctx.AlternationPreferSpell (true = this round is a
    // spell phase, false = physical). A spell phase behaves like SpellsFirst
    // (falls to the swing when no spell can fire); a physical phase behaves like
    // PhysicalFirst (falls to the cascade only when the weapon is ineffective).
    // The engine-owned round counter and the every-round command re-issue are
    // exercised at the manager level (CombatManagerSpellsTests).

    private static CombatSpellContext AltCtx(
        bool preferSpell, bool weaponIneffective = false,
        int mana = 100, int maxMana = 100, bool backstabPending = false) =>
        new(EnemyCount: 1, TargetRawName: "a rat", Mana: mana, MaxMana: maxMana,
            BackstabPending: backstabPending, WeaponIneffective: weaponIneffective,
            AlternationPreferSpell: preferSpell);

    [Fact]
    public void Alternation_SpellPhase_CastsEvenWhenFixedOrderIsPhysicalFirst()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("harm"),
            // The per-round preference must override the configured fixed order.
            ActionOrder = CombatActionOrder.PhysicalFirst,
        };

        CombatSpellDecision d = sut.Choose(settings, AltCtx(preferSpell: true));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, d.Action);
        Assert.Equal("harm", d.Spell);
    }

    [Fact]
    public void Alternation_PhysicalPhase_SwingsEvenWhenFixedOrderIsSpellsFirst()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("harm"),
            ActionOrder = CombatActionOrder.SpellsFirst,
        };

        CombatSpellDecision d = sut.Choose(settings, AltCtx(preferSpell: false));
        Assert.Equal(CombatSpellAction.WeaponAttack, d.Action);
        Assert.Null(d.Spell);
    }

    [Fact]
    public void Alternation_SpellPhase_NoCastableSpell_FallsToWeapon()
    {
        CombatSpellChooser sut = new();
        // Spell phase but nothing is configured to fire → fall back to the swing.
        CombatSpellDecision d = sut.Choose(new CombatSettings(), AltCtx(preferSpell: true));
        Assert.Equal(CombatSpellAction.WeaponAttack, d.Action);
    }

    [Fact]
    public void Alternation_PhysicalPhase_WeaponIneffective_FallsToSpell()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new() { NormalAttackSpell = Slot("harm") };

        // Physical phase but the weapon can't hit → the cascade fires anyway.
        CombatSpellDecision d = sut.Choose(
            settings, AltCtx(preferSpell: false, weaponIneffective: true));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, d.Action);
        Assert.Equal("harm", d.Spell);
    }

    [Fact]
    public void Alternation_BackstabOpensRegardlessOfPhase()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new() { NormalAttackSpell = Slot("harm") };

        // The opener outranks the per-round choice in both phases.
        Assert.Equal(CombatSpellAction.Backstab,
            sut.Choose(settings, AltCtx(preferSpell: false, backstabPending: true)).Action);
        Assert.Equal(CombatSpellAction.Backstab,
            sut.Choose(settings, AltCtx(preferSpell: true, backstabPending: true)).Action);
    }

    // ----- CombatSettings.AlternateAttackSpells cycling ------------------
    // Off (default): Alternate is a one-way fallback — once Normal lapses and
    // Alternate takes over, the per-target latch commits to the weapon/alternate
    // for the rest of the fight even if Normal becomes castable again (pinned
    // above under "Per-target weapon latch"). On: the engine keeps re-checking
    // BOTH slots every round, so a lapsed slot can come back once it's eligible
    // again — but it stays on whichever is currently firing until THAT one
    // lapses, rather than yanking back to Normal the instant Normal recovers.

    [Fact]
    public void Cycling_SwitchesToAlternate_ThenBackToNormal_OnceAlternateItselfLapses()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SpellManaThresholdMode = ThresholdMode.Absolute,
            AlternateAttackSpells = true,
            // Normal is only mana-gated (no cap) — recoverable once mana regens.
            NormalAttackSpell = Slot("harm", minMana: 50),
            // Alternate has a hard per-target cap — once spent it never comes
            // back for this target, unlike a mana reserve.
            AlternateAttackSpell = Slot("flame", minMana: 10, maxCasts: 1),
        };

        // Plenty of mana — Normal is checked first and is eligible.
        CombatSpellDecision r1 = sut.Choose(settings, Ctx(mana: 100, maxMana: 100));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, r1.Action);
        sut.MarkCast(r1, "a rat");

        // Mana drops below Normal's reserve but still above Alternate's — cycles
        // to Alternate instead of latching to the weapon.
        CombatSpellDecision r2 = sut.Choose(settings, Ctx(mana: 30, maxMana: 100));
        Assert.Equal(CombatSpellAction.AlternateAttackSpell, r2.Action);
        Assert.Equal("flame", r2.Spell);
        sut.MarkCast(r2, "a rat");

        // Alternate's own cap (1) is now spent AND mana has regenerated above
        // Normal's reserve — WITHOUT cycling this would stay latched to the
        // weapon forever; WITH it, Normal resumes.
        CombatSpellDecision r3 = sut.Choose(settings, Ctx(mana: 90, maxMana: 100));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, r3.Action);
        Assert.Equal("harm", r3.Spell);
    }

    [Fact]
    public void Cycling_StaysOnActiveSlot_UntilItLapses_EvenIfTheOtherIsAlsoEligible()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SpellManaThresholdMode = ThresholdMode.Absolute,
            AlternateAttackSpells = true,
            NormalAttackSpell = Slot("harm", minMana: 50),
            AlternateAttackSpell = Slot("flame", minMana: 10),
        };

        CombatSpellDecision r1 = sut.Choose(settings, Ctx(mana: 100, maxMana: 100));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, r1.Action);
        sut.MarkCast(r1, "a rat");

        // Drop below Normal's reserve — flips to Alternate.
        CombatSpellDecision r2 = sut.Choose(settings, Ctx(mana: 30, maxMana: 100));
        Assert.Equal(CombatSpellAction.AlternateAttackSpell, r2.Action);
        sut.MarkCast(r2, "a rat");

        // Mana is back up — Normal is eligible again too, but Alternate (the
        // currently active slot) is STILL eligible, so the engine stays put
        // rather than switching every round it can.
        CombatSpellDecision r3 = sut.Choose(settings, Ctx(mana: 100, maxMana: 100));
        Assert.Equal(CombatSpellAction.AlternateAttackSpell, r3.Action);
    }

    [Fact]
    public void Cycling_BothSlotsIneligible_FallsToWeapon()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SpellManaThresholdMode = ThresholdMode.Absolute,
            AlternateAttackSpells = true,
            NormalAttackSpell = Slot("harm", minMana: 50),
            AlternateAttackSpell = Slot("flame", minMana: 50),
        };

        Assert.Equal(CombatSpellAction.WeaponAttack,
            sut.Choose(settings, Ctx(mana: 10, maxMana: 100)).Action);
    }

    [Fact]
    public void Cycling_MaxCastsDriven_SwitchesToAlternate_ThenBackToNormal_OnNewTarget()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            AlternateAttackSpells = true,
            NormalAttackSpell = Slot("harm", maxCasts: 1),
            AlternateAttackSpell = Slot("flame", maxCasts: 1),
        };

        CombatSpellDecision r1 = sut.Choose(settings, Ctx());
        Assert.Equal(CombatSpellAction.NormalAttackSpell, r1.Action);
        sut.MarkCast(r1, "a rat");

        // Normal's cap is a hard per-target cap — spending it moves to Alternate.
        CombatSpellDecision r2 = sut.Choose(settings, Ctx());
        Assert.Equal(CombatSpellAction.AlternateAttackSpell, r2.Action);
        sut.MarkCast(r2, "a rat");

        // Both caps spent for THIS target → weapon (MaxCasts, unlike mana, never
        // un-spends mid-fight).
        Assert.Equal(CombatSpellAction.WeaponAttack, sut.Choose(settings, Ctx()).Action);

        // A fresh target resets the per-target caps — Normal opens first again.
        sut.ResetForNewTarget();
        Assert.Equal(CombatSpellAction.NormalAttackSpell, sut.Choose(settings, Ctx()).Action);
    }

    [Fact]
    public void Cycling_OnlyOneSlotConfigured_BehavesAsPlainCascade()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SpellManaThresholdMode = ThresholdMode.Absolute,
            AlternateAttackSpells = true,
            NormalAttackSpell = Slot("harm", minMana: 50),
            // No Alternate configured — cycling needs both slots, so this is a
            // no-op and the plain cascade (with its permanent latch) applies.
        };

        CombatSpellDecision r1 = sut.Choose(settings, Ctx(mana: 60, maxMana: 100));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, r1.Action);
        sut.MarkCast(r1, "a rat");

        Assert.Equal(CombatSpellAction.WeaponAttack,
            sut.Choose(settings, Ctx(mana: 40, maxMana: 100)).Action);

        // Latched to the weapon (cycling never engaged) — mana regen doesn't help.
        Assert.Equal(CombatSpellAction.WeaponAttack,
            sut.Choose(settings, Ctx(mana: 90, maxMana: 100)).Action);
    }

    [Fact]
    public void Cycling_AttackOverride_TakesPriorityAndIgnoresCycling()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            AlternateAttackSpells = true,
            NormalAttackSpell = Slot("harm"),
            AlternateAttackSpell = Slot("flame"),
        };

        // A per-monster override still occupies the Normal rung outright even
        // with cycling on — it's a more specific, deliberate per-monster choice.
        CombatSpellDecision d = sut.Choose(
            settings, OverrideCtx(attackOverride: "fireball", attackCap: 3));

        Assert.Equal(CombatSpellAction.NormalAttackSpell, d.Action);
        Assert.Equal("fireball", d.Spell);
    }

    [Fact]
    public void Cycling_ResetsToNormalFirst_OnNewTarget()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SpellManaThresholdMode = ThresholdMode.Absolute,
            AlternateAttackSpells = true,
            NormalAttackSpell = Slot("harm", minMana: 50),
            AlternateAttackSpell = Slot("flame", minMana: 10),
        };

        // Flip onto Alternate for this target.
        sut.Choose(settings, Ctx(mana: 100, maxMana: 100));
        CombatSpellDecision alt = sut.Choose(settings, Ctx(mana: 30, maxMana: 100));
        Assert.Equal(CombatSpellAction.AlternateAttackSpell, alt.Action);
        sut.MarkCast(alt, "a rat");

        // A fresh target must reopen on Normal, not stay pinned to Alternate,
        // even though both slots would be eligible again.
        sut.ResetForNewTarget();
        CombatSpellDecision fresh = sut.Choose(settings, Ctx(mana: 100, maxMana: 100));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, fresh.Action);
    }

    [Fact]
    public void Cycling_IsIdempotent_RepeatedChooseWithoutMarkCast_StableDecision()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SpellManaThresholdMode = ThresholdMode.Absolute,
            AlternateAttackSpells = true,
            NormalAttackSpell = Slot("harm", minMana: 50),
            AlternateAttackSpell = Slot("flame", minMana: 10),
        };

        // Normal is ineligible this round — the first Choose() call flips the
        // preference pointer to Alternate. Calling Choose again with the SAME
        // context (no MarkCast in between) must keep returning Alternate, not
        // toggle back to Normal or oscillate.
        CombatSpellContext ctx = Ctx(mana: 30, maxMana: 100);
        Assert.Equal(CombatSpellAction.AlternateAttackSpell, sut.Choose(settings, ctx).Action);
        Assert.Equal(CombatSpellAction.AlternateAttackSpell, sut.Choose(settings, ctx).Action);
        Assert.Equal(CombatSpellAction.AlternateAttackSpell, sut.Choose(settings, ctx).Action);
    }
}
