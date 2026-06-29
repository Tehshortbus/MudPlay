using FujinTerm.Game.Spells;
using FujinTerm.ViewModels;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the level-scaled display formatting on <see cref="SpellBookRowViewModel"/>
/// — effect string (damage / heal / duration), mana, formula tooltip, and the
/// obtained glyph — independent of game-data loading. Magnitudes come from the
/// ported <see cref="SpellCalculator"/>; these tests assert the row's string
/// shaping, not the math (which <c>SpellCalculatorTests</c> owns).
/// </summary>
public sealed class SpellBookRowViewModelTests
{
    private static SpellFormulaInput Damage(int minBase, int maxBase) => new()
    {
        Number = 1,
        MinBase = minBase,
        MaxBase = maxBase,
        Abilities = [new SpellAbility(1, 0)], // code 1 = direct damage
    };

    private static SpellFormulaInput Heal(int minBase, int maxBase) => new()
    {
        Number = 2,
        MinBase = minBase,
        MaxBase = maxBase,
        Abilities = [new SpellAbility(18, 0)], // code 18 = heal
    };

    private static SpellFormulaInput Buff(int dur) => new()
    {
        Number = 3,
        Dur = dur,
        Abilities = [], // no damage/heal magnitude — duration only
    };

    private static KnownSpell Spell(string shortCode, string name, int reqLevel, SpellFormulaInput formula)
        => new(formula.Number, shortCode, name, Magery: 1, MageryLvl: 1, reqLevel, Targets: 0, formula);

    private static SpellFormulaInput? NoChain(int _) => null;

    [Fact]
    public void DamageRange_RendersAsDmg()
    {
        KnownSpell s = Spell("star", "starlight", 2, Damage(minBase: 6, maxBase: 10));
        SpellBookRowViewModel row = new(s, isObtained: true, level: 5, NoChain);

        Assert.Equal("Dmg 6–10", row.EffectText);
        Assert.Equal("star", row.Short);
        Assert.Equal("starlight", row.Name);
        Assert.Equal("2", row.ReqLevelText);
        Assert.Equal("✓", row.ObtainedGlyph);
    }

    [Fact]
    public void EqualMinMax_RendersSingleValue()
    {
        KnownSpell s = Spell("bolt", "bolt", 1, Damage(minBase: 8, maxBase: 8));
        SpellBookRowViewModel row = new(s, isObtained: false, level: 5, NoChain);

        Assert.Equal("Dmg 8", row.EffectText);
        Assert.Equal(string.Empty, row.ObtainedGlyph);
    }

    [Fact]
    public void HealRange_RendersAsHeal()
    {
        KnownSpell s = Spell("heal", "minor heal", 3, Heal(minBase: 20, maxBase: 30));
        SpellBookRowViewModel row = new(s, isObtained: true, level: 7, NoChain);

        Assert.Equal("Heal 20–30", row.EffectText);
    }

    [Fact]
    public void DurationOnly_RendersSeconds()
    {
        // Durations are stored in 3-second spell-round ticks: 8 × 3 = 24s.
        KnownSpell s = Spell("bless", "bless", 4, Buff(dur: 8));
        SpellBookRowViewModel row = new(s, isObtained: false, level: 10, NoChain);

        Assert.Equal("24 seconds", row.EffectText);
    }

    [Fact]
    public void NoMagnitude_RendersDash()
    {
        // No damage/heal ability and no duration → nothing to show.
        SpellFormulaInput empty = new() { Number = 9, Abilities = [] };
        KnownSpell s = Spell("misc", "misc", 1, empty);
        SpellBookRowViewModel row = new(s, isObtained: false, level: 5, NoChain);

        Assert.Equal("—", row.EffectText);
    }

    [Fact]
    public void StatAffect_RendersDecodedAbility()
    {
        // Pure buff: AC +10 (code 2). No damage / heal / duration figure, so
        // the decoded affect is the only thing the Effect column shows.
        SpellFormulaInput f = new()
        {
            Number = 5,
            Abilities = [new SpellAbility(2, 10)],
        };
        KnownSpell s = Spell("shld", "shield", 4, f);
        SpellBookRowViewModel row = new(s, isObtained: true, level: 6, NoChain);

        Assert.Equal("AC +10", row.EffectText);
    }

    [Fact]
    public void ZeroValueAffect_RendersLevelScaledMinMax()
    {
        // Stealth (code 27) with AbilVal 0 — the magnitude lives in the
        // spell's Min/Max base (MME PullSpellEQ appends sMin/sMax). Equal
        // min/max collapse to a single signed value.
        SpellFormulaInput f = new()
        {
            Number = 10,
            MinBase = 10,
            MaxBase = 10,
            ReqLevel = 4,
            Abilities = [new SpellAbility(27, 0)],
        };
        KnownSpell s = Spell("hide", "shadow cloak", 4, f);
        SpellBookRowViewModel row = new(s, isObtained: true, level: 6, NoChain);

        Assert.Equal("Stealth +10", row.EffectText);
    }

    [Fact]
    public void ZeroValueAffect_BelowReqLevel_ClampsToObtainLevel()
    {
        // MaxDamage (code 4) gains +1 every level past base 0. Required at
        // level 6; evaluated at level 1 it must clamp UP to level 6 (→ +6),
        // never the out-of-range level-1 figure.
        SpellFormulaInput f = new()
        {
            Number = 11,
            MaxBase = 0,
            MaxInc = 1,
            MaxIncLVLs = 1,
            ReqLevel = 6,
            Abilities = [new SpellAbility(4, 0)],
        };
        KnownSpell s = Spell("edge", "keen edge", 6, f);
        SpellBookRowViewModel row = new(s, isObtained: false, level: 1, NoChain);

        // Min base 0/no slope → 0; Max → 6. Range shows "0 to +6".
        Assert.Equal("MaxDamage 0 to +6", row.EffectText);
    }

    [Fact]
    public void DurationPlusAffect_RendersBoth()
    {
        // Timed buff that grants Strength +3 (code 46) for 8 rounds.
        SpellFormulaInput f = new()
        {
            Number = 6,
            Dur = 8,
            Abilities = [new SpellAbility(46, 3)],
        };
        KnownSpell s = Spell("migt", "might", 5, f);
        SpellBookRowViewModel row = new(s, isObtained: false, level: 10, NoChain);

        Assert.Equal("24 seconds · Strength +3", row.EffectText);
    }

    [Fact]
    public void RemovesSpell_RendersTargetByName()
    {
        // Abil 122 (RemovesSpell) → resolve the AbilVal to the removed
        // spell's name via the supplied resolver (MME's GetSpellName path).
        SpellFormulaInput f = new()
        {
            Number = 8,
            Abilities = [new SpellAbility(122, 42)],
        };
        KnownSpell s = Spell("dpel", "dispel", 6, f);
        SpellBookRowViewModel row = new(
            s, isObtained: true, level: 10, NoChain,
            resolveSpellName: n => n == 42 ? "blindness" : null);

        Assert.Equal("Removes blindness", row.EffectText);
    }

    [Fact]
    public void RemovesClause_TrailsGainFigures_OnASingleSpell()
    {
        // A spell that both buffs and removes another spell shows the gain
        // figure first, then the "Removes …" clause — same ordering as a
        // multi-cast textblock expansion.
        SpellFormulaInput f = new()
        {
            Number = 30,
            Abilities = [new SpellAbility(2, 10), new SpellAbility(122, 42)], // AC +10 + RemovesSpell
        };
        KnownSpell s = Spell("ward", "warding", 6, f);
        SpellBookRowViewModel row = new(
            s, isObtained: true, level: 10, NoChain,
            resolveSpellName: n => n == 42 ? "curse" : null);

        Assert.Equal("AC +10 · Removes curse", row.EffectText);
    }

    [Fact]
    public void NegativeDrainRange_RendersAbilityVerbatim()
    {
        // sacrifice: DrainLife (8) with a raw negative range (-60..-20) and an
        // AffectsLivingOnly (108) flag. The negative range must not be folded
        // into a positive "Dmg" figure (which the maxDmg>0 gate hid entirely) —
        // MME shows "DrainLife -60 to -20", the signed range verbatim.
        SpellFormulaInput f = new()
        {
            Number = 404,
            MinBase = -60,
            MaxBase = -20,
            Abilities =
            [
                new SpellAbility(8, 0),    // DrainLife
                new SpellAbility(120, 1106), // StartMsg — hidden
                new SpellAbility(108, 0),  // AffectsLivingOnly — flag
            ],
        };
        KnownSpell s = Spell("sacr", "sacrifice", 18, f);
        SpellBookRowViewModel row = new(s, isObtained: false, level: 18, NoChain);

        Assert.Equal("DrainLife -60 to -20 · living only", row.EffectText);
    }

    [Fact]
    public void InvertedDamageRange_RendersAbilityVerbatim()
    {
        // dragonfire: Damage(-MR) (17) with an inverted stored range (min 100 >
        // max -50). MME renders "Damage(-MR) 100 to -50" rather than a "Dmg"
        // figure that would mis-order or hide it.
        SpellFormulaInput f = new()
        {
            Number = 500,
            MinBase = 100,
            MaxBase = -50,
            Abilities = [new SpellAbility(17, 0)],
        };
        KnownSpell s = Spell("dfir", "dragonfire", 20, f);
        SpellBookRowViewModel row = new(s, isObtained: false, level: 20, NoChain);

        Assert.Equal("Damage(-MR) 100 to -50", row.EffectText);
    }

    [Fact]
    public void NonMagicalDamage_RewritesMrSuffixAway()
    {
        // A NonMagicalSpell (144) slot alongside Damage(-MR) (17) rewrites the
        // label back to plain "Damage", matching MME's post-pass Replace.
        SpellFormulaInput f = new()
        {
            Number = 501,
            MinBase = -15,
            MaxBase = 20,
            Abilities = [new SpellAbility(17, 0), new SpellAbility(144, 0)],
        };
        KnownSpell s = Spell("nmag", "nonmagic blast", 10, f);
        SpellBookRowViewModel row = new(s, isObtained: false, level: 10, NoChain);

        Assert.Equal("Damage -15 to 20 · ignores magic resistance", row.EffectText);
    }

    [Fact]
    public void DispellMagic_RendersTargetAbilityNameInParens()
    {
        // Abil 73 (DispellMagic): AbilVal is an ability-code pointer naming
        // what's dispelled, not a magnitude. MME renders "DispellMagic (Poison)"
        // — never "DispellMagic +19". Pairs with a CurePoison gain (20) so the
        // render order (gains, then the dispel) is also exercised.
        SpellFormulaInput f = new()
        {
            Number = 50,
            Abilities = [new SpellAbility(20, 8), new SpellAbility(73, 19)], // CurePoison +8 + DispellMagic(Poison)
        };
        KnownSpell s = Spell("cpoi", "cure poison", 6, f);
        SpellBookRowViewModel row = new(s, isObtained: true, level: 10, NoChain);

        Assert.Equal("CurePoison +8, DispellMagic (Poison)", row.EffectText);
    }

    [Fact]
    public void DispellMagic_RendersBareName_WhenAbilValIsZero()
    {
        // Abil 73 with AbilVal 0 (e.g. "dispel magic"): MME gates the parens
        // behind If Not nValue = 0, so a zero pointer renders the bare name
        // with no number and no parens — never inheriting a coexisting range.
        SpellFormulaInput f = new()
        {
            Number = 51,
            Abilities = [new SpellAbility(73, 0)],
        };
        KnownSpell s = Spell("disp", "dispel magic", 6, f);
        SpellBookRowViewModel row = new(s, isObtained: true, level: 10, NoChain);

        Assert.Equal("DispellMagic", row.EffectText);
    }

    [Fact]
    public void NegateAbility_RendersTargetAbilityNameInParens()
    {
        // Abil 124 (NegateAbility) shares DispellMagic's ability-pointer
        // rendering: "NegateAbility (HoldPerson)".
        SpellFormulaInput f = new()
        {
            Number = 52,
            Abilities = [new SpellAbility(124, 74)], // NegateAbility(HoldPerson)
        };
        KnownSpell s = Spell("free", "freedom", 6, f);
        SpellBookRowViewModel row = new(s, isObtained: true, level: 10, NoChain);

        Assert.Equal("NegateAbility (HoldPerson)", row.EffectText);
    }

    [Fact]
    public void MessageOnlySlots_AreNotSurfaced()
    {
        // DescMsg (115) / StartMsg (120) / ShockMsg (137) are display-only
        // message slots MME hides — they must not appear as effect labels.
        SpellFormulaInput f = new()
        {
            Number = 9,
            MinBase = 6,
            MaxBase = 10,
            Abilities =
            [
                new SpellAbility(1, 0),
                new SpellAbility(115, 3),
                new SpellAbility(120, 4),
                new SpellAbility(137, 5),
            ],
        };
        KnownSpell s = Spell("bolt", "bolt", 3, f);
        SpellBookRowViewModel row = new(s, isObtained: false, level: 5, NoChain);

        Assert.Equal("Dmg 6–10", row.EffectText);
    }

    [Fact]
    public void TextBlock_RendersUnsignedRecordNumber()
    {
        // Abil 148 (TextBlock) carries a TextBlock *record number*, not a
        // magnitude — it must render "TextBlock 869", never "TextBlock +869"
        // (MME's NO-HEADER group: GetAbilityName(148) & " " & nValue).
        SpellFormulaInput f = new()
        {
            Number = 12,
            Abilities = [new SpellAbility(148, 869)],
        };
        KnownSpell s = Spell("wood", "wooden box gems", 5, f);
        SpellBookRowViewModel row = new(s, isObtained: true, level: 6, NoChain);

        Assert.Equal("TextBlock 869", row.EffectText);
    }

    [Fact]
    public void TextBlock_ExpandsToLinkedCastSpellEffects_WhenResolverSupplied()
    {
        // Abil 148 carries a TextBlock record number. When the Casted-By
        // reverse link resolves that textblock to the real spell(s) it casts,
        // the row surfaces those effects inline (duration + stat bonus) instead
        // of the opaque "TextBlock N". General — keyed only on the textblock
        // number, no per-spell special-casing.
        SpellFormulaInput tb = new()
        {
            Number = 20,
            Abilities = [new SpellAbility(148, 2910)],
        };
        KnownSpell s = Spell("dfrm", "form of the dragon", 30, tb);

        // The textblock casts a single buff: Strength +5 for 8 rounds (24s).
        SpellFormulaInput linked = new()
        {
            Number = 858,
            Dur = 8,
            Abilities = [new SpellAbility(46, 5)], // 46 = Strength
        };
        KnownSpell linkedSpell = new(858, "drgn", "form of the dragon",
            Magery: 1, MageryLvl: 1, ReqLevel: 30, Targets: 0, linked);

        SpellBookRowViewModel row = new(
            s, isObtained: true, level: 30, NoChain,
            resolveTextblockCasts: n => n == 2910
                ? new[] { linkedSpell }
                : System.Array.Empty<KnownSpell>());

        Assert.Equal("24 seconds · Strength +5", row.EffectText);
    }

    [Fact]
    public void TextBlock_PlacesRemovesAfterGainFigures_RegardlessOfCastOrder()
    {
        // A form textblock casts both the buff and a pure "remove forms"
        // cleanup spell. The cleanup's "Removes …" clause must trail the gain
        // figures even when the reverse-link returns the cleanup first.
        SpellFormulaInput tb = new()
        {
            Number = 22,
            Abilities = [new SpellAbility(148, 2910)],
        };
        KnownSpell s = Spell("dfrm", "form of the dragon", 30, tb);

        KnownSpell buff = new(858, "drgn", "form of the dragon",
            Magery: 1, MageryLvl: 1, ReqLevel: 30, Targets: 0,
            new SpellFormulaInput { Number = 858, Dur = 8, Abilities = [new SpellAbility(46, 5)] });
        KnownSpell cleanup = new(878, "rfrm", "remove forms",
            Magery: 1, MageryLvl: 1, ReqLevel: 30, Targets: 0,
            new SpellFormulaInput { Number = 878, Abilities = [new SpellAbility(122, 858)] });

        SpellBookRowViewModel row = new(
            s, isObtained: true, level: 30, NoChain,
            resolveSpellName: n => n == 858 ? "form of the dragon" : null,
            resolveTextblockCasts: n => n == 2910
                ? new[] { cleanup, buff } // cleanup deliberately first
                : System.Array.Empty<KnownSpell>());

        Assert.Equal("24 seconds · Strength +5, Removes form of the dragon", row.EffectText);
    }

    [Fact]
    public void TextBlock_FallsBackToRecordNumber_WhenResolverFindsNoCasts()
    {
        // Resolver supplied but the textblock links to nothing → keep the
        // unsigned record number (never silently blank the effect).
        SpellFormulaInput tb = new()
        {
            Number = 21,
            Abilities = [new SpellAbility(148, 1234)],
        };
        KnownSpell s = Spell("misc", "misc", 5, tb);
        SpellBookRowViewModel row = new(
            s, isObtained: false, level: 6, NoChain,
            resolveTextblockCasts: _ => System.Array.Empty<KnownSpell>());

        Assert.Equal("TextBlock 1234", row.EffectText);
    }

    [Fact]
    public void TextBlock_FoldsOwnAndChildDurationIntoOneFigure_EqualDurations()
    {
        // Mystic "form of the crane": the form spell itself carries Dur 300
        // (900s) AND casts a TextBlock buff that also carries Dur 300. Both
        // durations are the same length — surface a single "900 seconds", not
        // "900 seconds · 900 seconds".
        SpellFormulaInput form = new()
        {
            Number = 838,
            Dur = 300,
            Abilities = [new SpellAbility(148, 2911)],
        };
        KnownSpell s = Spell("cfrm", "form of the crane", 40, form);

        KnownSpell buff = new(879, "crn", "form of the crane",
            Magery: 1, MageryLvl: 1, ReqLevel: 40, Targets: 0,
            new SpellFormulaInput { Number = 879, Dur = 300, Abilities = [new SpellAbility(46, 4)] });

        SpellBookRowViewModel row = new(
            s, isObtained: true, level: 40, NoChain,
            resolveTextblockCasts: n => n == 2911 ? new[] { buff } : System.Array.Empty<KnownSpell>());

        Assert.Equal("900 seconds · Strength +4", row.EffectText);
    }

    [Fact]
    public void TextBlock_KeepsLongerOfFormAndChildDuration()
    {
        // "form of the dragon": the form spell's own Dur is just 1 tick (3s, the
        // transform animation) while the TextBlock buff lasts Dur 300 (900s).
        // Show the longer of the two — "900 seconds", never "3 seconds · 900…".
        SpellFormulaInput form = new()
        {
            Number = 839,
            Dur = 1,
            Abilities = [new SpellAbility(148, 2910)],
        };
        KnownSpell s = Spell("dfrm", "form of the dragon", 50, form);

        KnownSpell buff = new(858, "drgn", "form of the dragon",
            Magery: 1, MageryLvl: 1, ReqLevel: 50, Targets: 0,
            new SpellFormulaInput { Number = 858, Dur = 300, Abilities = [new SpellAbility(46, 10)] });

        SpellBookRowViewModel row = new(
            s, isObtained: true, level: 50, NoChain,
            resolveTextblockCasts: n => n == 2910 ? new[] { buff } : System.Array.Empty<KnownSpell>());

        Assert.Equal("900 seconds · Strength +10", row.EffectText);
    }

    [Fact]
    public void NonMagicalSpellFlag_DoesNotDuplicateDamageRange()
    {
        // "way of the dragon" (drgn): Damage (1) + NonMagicalSpell (144) flag,
        // MinBase 13 / MaxBase 14. Code 144 is a pure flag — it must render
        // name-only, NOT inherit the level-scaled damage range (which would
        // print the bogus "NonMagicalSpell +13 to +22" beside "Dmg 13–…").
        SpellFormulaInput f = new()
        {
            Number = 13,
            MinBase = 13,
            MaxBase = 14,
            ReqLevel = 8,
            Abilities = [new SpellAbility(1, 0), new SpellAbility(144, 0)],
        };
        KnownSpell s = Spell("drgn", "way of the dragon", 8, f);
        SpellBookRowViewModel row = new(s, isObtained: true, level: 8, NoChain);

        Assert.Equal("Dmg 13–14 · ignores magic resistance", row.EffectText);
    }

    [Fact]
    public void DamageAbility_NotDoublePrintedAsAffect()
    {
        // Code 1 (Damage) is folded into the Dmg figure; an accompanying
        // Slowness -5 (code 68) debuff still surfaces as an affect.
        SpellFormulaInput f = new()
        {
            Number = 7,
            MinBase = 6,
            MaxBase = 10,
            Abilities = [new SpellAbility(1, 0), new SpellAbility(68, -5)],
        };
        KnownSpell s = Spell("frst", "frost", 3, f);
        SpellBookRowViewModel row = new(s, isObtained: true, level: 5, NoChain);

        Assert.Equal("Dmg 6–10 · Slowness -5", row.EffectText);
    }

    [Fact]
    public void Formula_ShowsBaseAndSlope()
    {
        SpellFormulaInput f = new()
        {
            Number = 1,
            MinBase = 6,
            MaxBase = 10,
            MaxInc = 2,
            MaxIncLVLs = 1,
            Abilities = [new SpellAbility(1, 0)],
        };
        KnownSpell s = Spell("star", "starlight", 2, f);
        SpellBookRowViewModel row = new(s, isObtained: true, level: 5, NoChain);

        Assert.Contains("base 6–10", row.FormulaText);
        Assert.Contains("max +2/1lv", row.FormulaText);
    }
}
