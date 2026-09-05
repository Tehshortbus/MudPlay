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
        bool backstabPending = false, bool allowNukes = true,
        System.Collections.Generic.IReadOnlyList<string>? roomMobKeys = null) =>
        new(enemies, target, mana, maxMana, backstabPending, AllowNukes: allowNukes,
            RoomMobKeys: roomMobKeys);

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

    // The AoE per-room cap survives a ROSTER clear (an AoE multi-kill empties the
    // listed pack and fires a synthetic room-clear though the character never moved),
    // so the area debuff doesn't re-fire at the same physical room's survivors /
    // re-reveals (report paradigm-20260902-160110 — "ISTO fired twice in one fight").
    // A genuine physical-room change (ResetForNewRoom) still re-arms it.
    [Fact]
    public void ChooseDebuff_Area_CapSurvivesRosterClear_ResetsOnNewRoom()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            AreaDebuffSpell = Slot("blindall", minEnemies: 2),
            NormalAttackSpell = Slot("harm"),
        };

        CombatSpellDecision? first = sut.ChooseDebuff(settings, Ctx(enemies: 3));
        Assert.Equal(CombatSpellAction.AreaDebuff, first?.Action);
        sut.MarkCast(first!.Value, "a rat");
        Assert.Null(sut.ChooseDebuff(settings, Ctx(enemies: 3)));   // cap reached

        // Roster clear (AoE wave-kill in the SAME room) must NOT re-arm the cap.
        sut.ResetForRosterClear();
        Assert.Null(sut.ChooseDebuff(settings, Ctx(enemies: 3)));

        // A real room change re-arms it.
        sut.ResetForNewRoom();
        Assert.Equal(CombatSpellAction.AreaDebuff,
            sut.ChooseDebuff(settings, Ctx(enemies: 3))?.Action);
    }

    // A same-room RESPAWN (after the room is confirmed cleared — the player rested)
    // must be re-debuffed, while a same-fight survivor (a mid-fight wave-clear roster
    // reset) must NOT be (report paradigm-20260903-070438 vs the -160110 "isto fired
    // twice" fix).
    [Fact]
    public void ChooseDebuff_Area_ResetOnRoomClear_ReDebuffsRespawn_NotSurvivor()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            AreaDebuffSpell = Slot("blindall", minEnemies: 2),
            NormalAttackSpell = Slot("harm"),
        };
        var keys = new[] { "slimeworm" };

        CombatSpellDecision? first = sut.ChooseDebuff(settings, Ctx(enemies: 3, roomMobKeys: keys));
        Assert.Equal(CombatSpellAction.AreaDebuff, first?.Action);
        sut.MarkCast(first!.Value, "slimeworm", keys);

        // A mid-fight wave-clear roster reset (hidden same-species survivors) keeps the
        // tags — the survivor is NOT re-debuffed.
        sut.ResetForRosterClear();
        Assert.Null(sut.ChooseDebuff(settings, Ctx(enemies: 3, roomMobKeys: keys)));

        // The room is confirmed genuinely cleared (the player rested) — a same-room
        // respawn after that IS re-debuffed.
        sut.ResetAreaDebuffTags();
        Assert.Equal(CombatSpellAction.AreaDebuff,
            sut.ChooseDebuff(settings, Ctx(enemies: 3, roomMobKeys: keys))?.Action);
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

    // ----- 2c. Debuff redesign: AoE covering vs single fall-through -----

    [Fact]
    public void ChooseDebuff_AreaConfigured_ButAutoNukeOff_FiresSingleTarget()
    {
        // report paradigm-20260817-205819: with the AoE debuff configured but Auto-Nuke
        // OFF the AoE isn't "covering" the room, so the single-target rung takes over
        // instead of being short-circuited by the configured-but-inactive AoE.
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            AreaDebuffSpell = Slot("stnk", minEnemies: 1),
            SingleTargetDebuffSpell = Slot("vuln"),
            NormalAttackSpell = Slot("lbol"),
        };

        CombatSpellDecision? d = sut.ChooseDebuff(settings, Ctx(enemies: 2, allowNukes: false));
        Assert.Equal(CombatSpellAction.SingleDebuff, d?.Action);
        Assert.Equal("vuln", d?.Spell);
    }

    [Fact]
    public void ChooseDebuff_Area_CastsOnce_NoReFire_ForTheSameRoster()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            AreaDebuffSpell = Slot("stnk", minEnemies: 1, maxCasts: 3),
            SingleTargetDebuffSpell = Slot("vuln"),
        };
        string[] roster = { "ant one", "ant two", "ant three" };

        CombatSpellDecision? first = sut.ChooseDebuff(settings, Ctx(enemies: 3, roomMobKeys: roster));
        Assert.Equal(CombatSpellAction.AreaDebuff, first?.Action);
        sut.MarkCast(first!.Value, "ant one", roster);

        // Same mobs next round: all tagged → no re-fire (cap still has room), and the
        // single rung must NOT fire while the AoE is covering.
        Assert.Null(sut.ChooseDebuff(settings, Ctx(enemies: 3, roomMobKeys: roster)));
    }

    [Fact]
    public void ChooseDebuff_Area_ReFires_ForANewArrival_UpToCap()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            AreaDebuffSpell = Slot("stnk", minEnemies: 1, maxCasts: 2),
        };
        string[] roster1 = { "ant one", "ant two" };
        CombatSpellDecision? c1 = sut.ChooseDebuff(settings, Ctx(enemies: 2, roomMobKeys: roster1));
        Assert.Equal(CombatSpellAction.AreaDebuff, c1?.Action);
        sut.MarkCast(c1!.Value, "ant one", roster1);      // casts = 1

        // A new mob arrives → re-fire (still under the cap of 2).
        string[] roster2 = { "ant one", "ant two", "ant three" };
        CombatSpellDecision? c2 = sut.ChooseDebuff(settings, Ctx(enemies: 3, roomMobKeys: roster2));
        Assert.Equal(CombatSpellAction.AreaDebuff, c2?.Action);
        sut.MarkCast(c2!.Value, "ant one", roster2);      // casts = 2 = cap

        // Yet another new mob, but the AoE is at its per-room cap → skip (not single).
        string[] roster3 = { "ant one", "ant two", "ant three", "ant four" };
        Assert.Null(sut.ChooseDebuff(settings, Ctx(enemies: 4, roomMobKeys: roster3)));
    }

    // report paradigm-20260827-082106: hunting the same species room-to-room, the
    // AoE debuff fired only in the first room. RawNames repeat across a same-species
    // loop, so the room we're leaving's tags mark the next room's identical crabs as
    // "already debuffed" and the once-per-room AoE skips. The pre-move reset
    // (CombatManager.NotePreMove → ResetForNewRoom) clears the tags before the next
    // room, so each room fires the AoE again.
    [Fact]
    public void ChooseDebuff_Area_SameSpeciesNextRoom_SkippedUntilRoomReset()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            AreaDebuffSpell = Slot("stnk", minEnemies: 2, maxCasts: 1),
        };
        // Every room in the loop shows the identical RawNames.
        string[] crabs = { "ironshell crab", "ironshell crab", "scorpion crab" };

        // Room 1: fires, tags the crabs (per-room cap now spent).
        CombatSpellDecision? room1 = sut.ChooseDebuff(settings, Ctx(enemies: 3, roomMobKeys: crabs));
        Assert.Equal(CombatSpellAction.AreaDebuff, room1?.Action);
        sut.MarkCast(room1!.Value, "ironshell crab", crabs);

        // Same room, next round: all tagged → no re-fire (correct once-per-room).
        Assert.Null(sut.ChooseDebuff(settings, Ctx(enemies: 3, roomMobKeys: crabs)));

        // Walk into a FRESH room of the same species WITHOUT a reset: the leaving
        // room's tags bleed in, so the identical crabs read as already-debuffed and
        // the AoE is wrongly skipped — the reported bug.
        Assert.Null(sut.ChooseDebuff(settings, Ctx(enemies: 3, roomMobKeys: crabs)));

        // The pre-move hook resets the per-room economy → the new room fires again.
        sut.ResetForNewRoom();
        CombatSpellDecision? room2 = sut.ChooseDebuff(settings, Ctx(enemies: 3, roomMobKeys: crabs));
        Assert.Equal(CombatSpellAction.AreaDebuff, room2?.Action);
    }

    // reports paradigm-20260901-123720 / -140747: a pre-attack AoE debuff is sent
    // optimistically, so MarkCast tags the room BEFORE the server accepts it. When the
    // cast is rejected ("You have already cast a spell this round!" — it collided with a
    // buff or the user's manual cast), UnmarkCast rolls the tag + per-room count back so
    // the debuff re-fires next round instead of the room staying falsely debuffed.
    [Fact]
    public void UnmarkCast_Area_RejectedSend_RefiresNextRound()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            AreaDebuffSpell = Slot("isto", minEnemies: 2, maxCasts: 1),
        };
        string[] roster = { "slimeworm", "slimeworm" };

        CombatSpellDecision? first = sut.ChooseDebuff(settings, Ctx(enemies: 2, roomMobKeys: roster));
        Assert.Equal(CombatSpellAction.AreaDebuff, first?.Action);
        sut.MarkCast(first!.Value, "slimeworm", roster);      // optimistic tag, per-room cap spent

        // Same round, still tagged → won't re-offer (correct once-per-room).
        Assert.Null(sut.ChooseDebuff(settings, Ctx(enemies: 2, roomMobKeys: roster)));

        // The send was rejected — roll the mark back.
        sut.UnmarkCast(first.Value, "slimeworm", roster);

        // Re-fires next round as though never cast.
        CombatSpellDecision? again = sut.ChooseDebuff(settings, Ctx(enemies: 2, roomMobKeys: roster));
        Assert.Equal(CombatSpellAction.AreaDebuff, again?.Action);
    }

    [Fact]
    public void ChooseDebuff_RoomThinsBelowMinEnemies_SingleTakesOver()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            AreaDebuffSpell = Slot("stnk", minEnemies: 2, maxCasts: 1),
            SingleTargetDebuffSpell = Slot("vuln"),
        };
        string[] roster = { "ant one", "ant two" };
        CombatSpellDecision? aoe = sut.ChooseDebuff(settings, Ctx(enemies: 2, roomMobKeys: roster));
        Assert.Equal(CombatSpellAction.AreaDebuff, aoe?.Action);
        sut.MarkCast(aoe!.Value, "ant one", roster);

        // Room thins to one mob (below the AoE MinEnemies 2) → single-target on the survivor.
        CombatSpellDecision? single = sut.ChooseDebuff(
            settings, Ctx(enemies: 1, target: "queen ant", roomMobKeys: new[] { "queen ant" }));
        Assert.Equal(CombatSpellAction.SingleDebuff, single?.Action);
        Assert.Equal("vuln", single?.Spell);
    }

    [Fact]
    public void ChooseDebuff_Area_ManaShort_WhileCovering_Skips_NotSingle()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            AreaDebuffSpell = Slot("stnk", minEnemies: 1, minMana: 90),
            SingleTargetDebuffSpell = Slot("vuln"),
        };
        // AoE covers (configured, nukes on, min met) but mana is below its floor → skip;
        // the single rung must NOT fire while the AoE covers the room.
        Assert.Null(sut.ChooseDebuff(
            settings, Ctx(enemies: 3, mana: 50, roomMobKeys: new[] { "a", "b", "c" })));
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
    public void ChooseDebuff_Single_FiresWithNukesOff()
    {
        // The single-target debuff is gated by Auto-Combat, NOT Auto-Nuke, so it
        // still fires with nukes off (report paradigm-20260817-135739: the debuff
        // never fired at all because it was wrongly under the Auto-Nuke gate).
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            SingleTargetDebuffSpell = Slot("weaken"),
            NormalAttackSpell = Slot("harm"),
        };

        CombatSpellDecision? d = sut.ChooseDebuff(settings, Ctx(target: "a rat", allowNukes: false));
        Assert.Equal(CombatSpellAction.SingleDebuff, d?.Action);
    }

    [Fact]
    public void ChooseDebuff_Area_RequiresNukesOn()
    {
        // The AoE debuff stays gated by Auto-Nuke — off means it never offers.
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            AreaDebuffSpell = Slot("blindall", minEnemies: 2),
            NormalAttackSpell = Slot("harm"),
        };

        Assert.Null(sut.ChooseDebuff(settings, Ctx(enemies: 3, allowNukes: false)));
        Assert.Equal(CombatSpellAction.AreaDebuff,
            sut.ChooseDebuff(settings, Ctx(enemies: 3, allowNukes: true))?.Action);
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
    public void Choose_ManaGatedNormal_DoesNotBurnCount_PrefersNormalWhenManaRecovers()
    {
        // A mana-gated normal attack (lbol, MinMana 70%, MaxCasts 1) must not burn its
        // per-target cast count while the alternate covers the round — so the moment
        // mana recovers above the gate, the normal is re-preferred (count still 0 of
        // 1). This is why a fight that momentarily "opened on MMIS" (mana a hair under
        // the floor) self-corrects to LBOL the next round once mana regenerates
        // (reports paradigm-20260816-103418 / -103515).
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("lbol", maxCasts: 1, minMana: 70),
            AlternateAttackSpell = Slot("mmis", maxCasts: 99, minMana: 0),
        };

        // Round 1 — mana 68 (< 70% floor): the normal is gated, the alternate fires.
        CombatSpellDecision r1 = sut.Choose(settings, Ctx(mana: 68, maxMana: 100));
        Assert.Equal(CombatSpellAction.AlternateAttackSpell, r1.Action);
        Assert.Equal("mmis", r1.Spell);
        sut.MarkCast(r1, "a rat");   // only the ALTERNATE's count advances

        // Round 2 — mana recovered to 84: the normal (count still 0 of 1) is preferred.
        CombatSpellDecision r2 = sut.Choose(settings, Ctx(mana: 84, maxMana: 100));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, r2.Action);
        Assert.Equal("lbol", r2.Spell);
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

    [Fact]
    public void Alternation_PhysicalPhase_AttackOverrideStillFires()
    {
        // report paradigm-20260904-220509: a per-monster attack-spell override
        // must win over the round-alternation's physical phase the same way a
        // per-monster attack-COMMAND override already does — the whole point of
        // an "override" is that it isn't subject to the general ActionOrder
        // setting. Previously the override was only ever consulted when
        // preferSpell was already true, so half the rounds under an
        // Alternate*/CustomRoundCycle order silently fell back to the weapon
        // even with the override's cap nowhere near spent.
        CombatSpellChooser sut = new();
        CombatSettings settings = new() { NormalAttackSpell = Slot("harm") };
        CombatSpellContext ctx = new(
            EnemyCount: 1, TargetRawName: "big blood skeleton", Mana: 100, MaxMana: 100,
            BackstabPending: false,
            OverrideAttackSpell: "disr", OverrideAttackMaxCasts: 3,
            AlternationPreferSpell: false);   // physical-phase round

        CombatSpellDecision d = sut.Choose(settings, ctx);

        Assert.Equal(CombatSpellAction.NormalAttackSpell, d.Action);
        Assert.Equal("disr", d.Spell);   // the override, not a weapon swing
    }

    [Fact]
    public void Alternation_PhysicalPhase_AttackOverrideCapSpent_FallsToWeapon()
    {
        // Once the override's own cap is spent, a physical-phase round falls back
        // to the weapon exactly as before — the fix only removes the alternation
        // gate on an override that's still eligible, it doesn't make the override
        // unconditional.
        CombatSpellChooser sut = new();
        CombatSettings settings = new() { NormalAttackSpell = Slot("harm") };
        CombatSpellContext ctx = new(
            EnemyCount: 1, TargetRawName: "big blood skeleton", Mana: 100, MaxMana: 100,
            BackstabPending: false,
            OverrideAttackSpell: "disr", OverrideAttackMaxCasts: 1,
            AlternationPreferSpell: false);

        sut.MarkCast(new CombatSpellDecision(CombatSpellAction.NormalAttackSpell, "disr"),
            "big blood skeleton");   // spend the cap (1)

        CombatSpellDecision d = sut.Choose(settings, ctx);

        Assert.Equal(CombatSpellAction.WeaponAttack, d.Action);
    }

    // ----- Drain (life-steal) override ----------------------------------

    private static CombatSpellContext DrainCtx(
        int enemies = 1, int mana = 100, int maxMana = 100,
        bool hpBelow = true, bool eligible = true,
        IReadOnlySet<CombatSpellAction>? immune = null,
        System.Func<string, int?>? manaCostOf = null) =>
        new(enemies, "a rat", mana, maxMana, BackstabPending: false,
            ImmuneAttackSpells: immune,
            HpBelowDrainTrigger: hpBelow, DrainTargetEligible: eligible,
            ManaCostOf: manaCostOf);

    [Fact]
    public void Drain_FiresWhenHurtAndEligible_OverridesAttackSpell()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("mmis"),
            DrainSpell = Slot("vamp"),
        };

        CombatSpellDecision d = sut.Choose(settings, DrainCtx(hpBelow: true, eligible: true));

        Assert.Equal(CombatSpellAction.DrainSpell, d.Action);
        Assert.Equal("vamp", d.Spell);
    }

    [Fact]
    public void Drain_NotChosen_BelowRealManaCost_EvenWithZeroReserve()
    {
        // Report paradigm-20260820-082741: a DrainSpell with MinManaPerCast=0 was cast
        // at 5 mana vs its 25 cost — a silent no-op. The chooser now treats the spell's
        // real cost as a hard floor beneath the reserve, so it's skipped when unaffordable
        // and fires once affordable.
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("mmis"),   // cheap fallback
            DrainSpell = Slot("dtch"),          // MinManaPerCast defaults to 0
        };
        System.Func<string, int?> cost = code => code == "dtch" ? 25 : 2;

        // Below cost → drain skipped, cascade falls to the affordable normal attack.
        CombatSpellDecision below = sut.Choose(settings, DrainCtx(mana: 5, manaCostOf: cost));
        Assert.NotEqual(CombatSpellAction.DrainSpell, below.Action);

        // At/above cost → drain fires as before.
        CombatSpellDecision ok = sut.Choose(settings, DrainCtx(mana: 25, manaCostOf: cost));
        Assert.Equal(CombatSpellAction.DrainSpell, ok.Action);
    }

    [Fact]
    public void Drain_SkippedWhenHpAboveTrigger_FallsToNormalAttack()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("mmis"),
            DrainSpell = Slot("vamp"),
        };

        CombatSpellDecision d = sut.Choose(settings, DrainCtx(hpBelow: false));

        Assert.Equal(CombatSpellAction.NormalAttackSpell, d.Action);
        Assert.Equal("mmis", d.Spell);
    }

    [Fact]
    public void Drain_SkippedWhenTargetIneligible_FallsToNormalAttack()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("mmis"),
            DrainSpell = Slot("vamp"),
        };

        // Hurt, but the target is NonLiving / Undead — a drain can't affect it.
        CombatSpellDecision d = sut.Choose(settings, DrainCtx(hpBelow: true, eligible: false));

        Assert.Equal(CombatSpellAction.NormalAttackSpell, d.Action);
        Assert.Equal("mmis", d.Spell);
    }

    [Fact]
    public void Drain_SkippedWhenReactivelyImmune()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("mmis"),
            DrainSpell = Slot("vamp"),
        };

        // A prior "no effect" line marked this species drain-immune (backstop for
        // thin game data) — the proactive index said eligible but the drain still skips.
        var immune = new HashSet<CombatSpellAction> { CombatSpellAction.DrainSpell };
        CombatSpellDecision d = sut.Choose(settings, DrainCtx(hpBelow: true, eligible: true, immune: immune));

        Assert.Equal(CombatSpellAction.NormalAttackSpell, d.Action);
    }

    [Fact]
    public void Drain_SkippedBelowManaFloor_FallsToWeapon()
    {
        CombatSpellChooser sut = new();
        // Drain needs 50% mana; only 10% available; no attack spell configured.
        CombatSettings settings = new() { DrainSpell = Slot("vamp", minMana: 50) };

        CombatSpellDecision d = sut.Choose(settings, DrainCtx(hpBelow: true, mana: 10, maxMana: 100));

        Assert.Equal(CombatSpellAction.WeaponAttack, d.Action);
    }

    [Fact]
    public void Drain_OverridesPhysicalFirstWeaponWhenHurt()
    {
        CombatSpellChooser sut = new();
        // Physical-first build with no attack spell — the drain still pre-empts the swing.
        CombatSettings settings = new()
        {
            ActionOrder = CombatActionOrder.PhysicalFirst,
            DrainSpell = Slot("vamp"),
        };

        CombatSpellDecision d = sut.Choose(settings, DrainCtx(hpBelow: true, eligible: true));

        Assert.Equal(CombatSpellAction.DrainSpell, d.Action);
    }

    [Fact]
    public void Drain_YieldsToAoeByDefault()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            MultiAttackSpell = Slot("blad", minEnemies: 3),
            DrainSpell = Slot("vamp"),
            // DrainsOverrideAoe = false (default)
        };

        // Hurt + eligible, but 4 enemies means the AoE would fire — it wins.
        CombatSpellDecision d = sut.Choose(settings, DrainCtx(enemies: 4, hpBelow: true, eligible: true));

        Assert.Equal(CombatSpellAction.MultiAttack, d.Action);
        Assert.Equal("blad", d.Spell);
    }

    [Fact]
    public void Drain_OverridesAoeWhenOptedIn()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            MultiAttackSpell = Slot("blad", minEnemies: 3),
            DrainSpell = Slot("vamp"),
            DrainsOverrideAoe = true,
        };

        CombatSpellDecision d = sut.Choose(settings, DrainCtx(enemies: 4, hpBelow: true, eligible: true));

        Assert.Equal(CombatSpellAction.DrainSpell, d.Action);
    }

    [Fact]
    public void Drain_StillOverridesSingleTarget_WhenAoeBelowMinEnemies()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            MultiAttackSpell = Slot("blad", minEnemies: 3),
            NormalAttackSpell = Slot("mmis"),
            DrainSpell = Slot("vamp"),
        };

        // Only 1 enemy — the AoE can't fire, so there's nothing for the drain to
        // yield to; it overrides the single-target attack.
        CombatSpellDecision d = sut.Choose(settings, DrainCtx(enemies: 1, hpBelow: true, eligible: true));

        Assert.Equal(CombatSpellAction.DrainSpell, d.Action);
    }

    [Fact]
    public void Drain_RespectsPerTargetMaxCasts()
    {
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("mmis"),
            DrainSpell = Slot("vamp", maxCasts: 1),
        };

        // First round: drain fires; tally it.
        CombatSpellDecision first = sut.Choose(settings, DrainCtx(hpBelow: true, eligible: true));
        Assert.Equal(CombatSpellAction.DrainSpell, first.Action);
        sut.MarkCast(first, "a rat");

        // Second round: cap of 1 spent — drop to the normal attack even while still hurt.
        CombatSpellDecision second = sut.Choose(settings, DrainCtx(hpBelow: true, eligible: true));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, second.Action);

        // A fresh target resets the per-target cap — the drain can fire again.
        sut.ResetForNewTarget();
        CombatSpellDecision fresh = sut.Choose(settings, DrainCtx(hpBelow: true, eligible: true));
        Assert.Equal(CombatSpellAction.DrainSpell, fresh.Action);
    }

    [Fact]
    public void Drain_ReleasesAtTrigger_NoOvershootBand()
    {
        // Report -153630: the drain releases AT the trigger — the moment HP recovers
        // above it, the round goes back to the normal attack. There is no hysteresis
        // band keeping it draining above the trigger (which previously pinned to 100%
        // HP and drained all the way to full).
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("mmis"),
            DrainSpell = Slot("vamp"),
        };

        // HP at/under the trigger → drain.
        CombatSpellDecision engaged = sut.Choose(settings, DrainCtx(hpBelow: true, eligible: true));
        Assert.Equal(CombatSpellAction.DrainSpell, engaged.Action);
        sut.MarkCast(engaged, "a rat");

        // HP now above the trigger → straight back to the attack, no hold-above-trigger.
        CombatSpellDecision recovered = sut.Choose(settings, DrainCtx(hpBelow: false, eligible: true));
        Assert.Equal(CombatSpellAction.NormalAttackSpell, recovered.Action);
    }

    [Fact]
    public void Drain_FiresEveryRoundWhileBelowTrigger_Uncapped()
    {
        // The default drain slot has no cap, so while HP stays at/under the trigger it
        // takes the round every round (an emergency heal that keeps healing while hurt)
        // — the mirror of 101845 (VAMP kept choosing the normal attack because a
        // MaxCasts of 1 had spent; with no cap it fires each round).
        CombatSpellChooser sut = new();
        CombatSettings settings = new()
        {
            NormalAttackSpell = Slot("mmis"),
            DrainSpell = Slot("vamp"),   // no maxCasts → uncapped
        };

        for (int round = 0; round < 3; round++)
        {
            CombatSpellDecision d = sut.Choose(settings, DrainCtx(hpBelow: true, eligible: true));
            Assert.Equal(CombatSpellAction.DrainSpell, d.Action);
            sut.MarkCast(d, "a rat");
        }
    }
}
