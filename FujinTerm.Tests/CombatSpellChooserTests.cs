using FujinTerm.Game.Combat;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

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
    public void Choose_PhysicalPriorityAboveSpells_SuppressesSpell()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("harm"),
            // Physical ahead of Spells — a swing is always possible, so it
            // owns the round and the attack spell never fires.
            PriorityPhysical = 1,
            PrioritySpells = 2,
            PriorityBackstab = 3,
            PriorityDebuffing = 4,
        };

        CombatSpellDecision d = sut.Choose(settings, Ctx(enemies: 1));

        Assert.Equal(CombatSpellAction.WeaponAttack, d.Action);
        Assert.Null(d.Spell);
    }

    [Fact]
    public void Choose_SpellsPriorityAboveBackstab_CastsBeforeBackstab()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("harm"),
            // Spells ahead of Backstab — the attack spell pre-empts the opener.
            PrioritySpells = 1,
            PriorityBackstab = 2,
            PriorityDebuffing = 3,
            PriorityPhysical = 4,
        };

        CombatSpellDecision d = sut.Choose(settings, Ctx(enemies: 1, backstabPending: true));

        Assert.Equal(CombatSpellAction.NormalAttackSpell, d.Action);
        Assert.Equal("harm", d.Spell);
    }

    [Fact]
    public void Choose_BackstabPending_ButNotPriorityOne_DoesNotBackstab()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            // Backstab ranked 2 (not 1) → ignored even while pending. Spells
            // has no slot configured, so the round falls to the weapon swing.
            PriorityPhysical = 3,
            PriorityBackstab = 2,
            PrioritySpells = 1,
            PriorityDebuffing = 4,
        };

        CombatSpellDecision d = sut.Choose(settings, Ctx(enemies: 1, backstabPending: true));

        Assert.Equal(CombatSpellAction.WeaponAttack, d.Action);
        Assert.Null(d.Spell);
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
}
