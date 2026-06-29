using FujinTerm.Game.Spells;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins <see cref="SpellEffectFormatter"/>'s EndCast (Abil 151) expansion —
/// the path that surfaces a weapon's chained on-hit / on-use spell (e.g. the
/// Nexus Spear's "spear slam knockdown") inline instead of dropping the slot.
/// Covers the percentage prefix (Abil 164), nested-chain recursion, the
/// cycle guard, and the name-fallback when a chain can't resolve.
/// </summary>
public sealed class SpellEffectFormatterTests
{
    private static SpellAbility Ab(int code, int value) => new(code, value);

    // ----- EndCast expands the chained debuff inline ----------------------

    [Fact]
    public void EndCast_WithPercent_ExpandsChainedEffectsInline()
    {
        // Chained "spear slam knockdown": no damage, a 12-second hold +
        // a stack of debuffs. Duration 4 ticks * 3s = 12 seconds.
        SpellFormulaInput chained = new()
        {
            Number = 1169,
            Dur = 4,
            Abilities =
            [
                Ab(74, 100),  // HoldPerson +100
                Ab(34, -10),  // Dodge -10
                Ab(2, -10),   // AC -10
                Ab(22, -10),  // Accuracy -10
            ],
        };

        // Parent on-use cast: Dmg 100–150, 20% EndCast → chained 1169.
        SpellFormulaInput parent = new()
        {
            Number = 72,
            MinBase = 100,
            MaxBase = 150,
            Abilities =
            [
                Ab(1, 0),       // Damage
                Ab(164, 20),    // EndCast% = 20
                Ab(151, 1169),  // EndCast → spell 1169
            ],
        };

        string result = SpellEffectFormatter.Format(
            parent,
            level: 0,
            resolveChain: n => n == 1169 ? chained : n == 72 ? parent : null,
            resolveSpellName: n => n == 1169 ? "spear slam knockdown" : null);

        Assert.Equal(
            "Dmg 100–150 · 20% EndCast spear slam knockdown "
            + "(12 seconds · HoldPerson +100, Dodge -10, AC -10, Accuracy -10)",
            result);
    }

    [Fact]
    public void EndCast_WithoutPercent_OmitsPercentPrefix()
    {
        SpellFormulaInput chained = new()
        {
            Number = 200,
            Abilities = [Ab(2, -5)], // AC -5
        };
        SpellFormulaInput parent = new()
        {
            Number = 10,
            MinBase = 4,
            MaxBase = 8,
            Abilities = [Ab(1, 0), Ab(151, 200)], // Damage + EndCast (no 164)
        };

        string result = SpellEffectFormatter.Format(
            parent,
            level: 0,
            resolveChain: n => n == 200 ? chained : n == 10 ? parent : null,
            resolveSpellName: n => n == 200 ? "frost bite" : null);

        Assert.Equal("Dmg 4–8 · EndCast frost bite (AC -5)", result);
    }

    [Fact]
    public void EndCast_UnresolvedName_FallsBackToSpellNumber()
    {
        SpellFormulaInput parent = new()
        {
            Number = 10,
            MinBase = 4,
            MaxBase = 8,
            Abilities = [Ab(1, 0), Ab(164, 30), Ab(151, 555)],
        };

        // No resolveChain hit (returns null) and no name resolver: only the
        // "#555" prefix shows.
        string result = SpellEffectFormatter.Format(
            parent,
            level: 0,
            resolveChain: _ => null);

        Assert.Equal("Dmg 4–8 · 30% EndCast #555", result);
    }

    [Fact]
    public void EndCast_NestedChain_RecursesThroughFollowUp()
    {
        // A → EndCast B → EndCast C; each step expands.
        SpellFormulaInput c = new()
        {
            Number = 3,
            Abilities = [Ab(2, -10)], // AC -10
        };
        SpellFormulaInput b = new()
        {
            Number = 2,
            Abilities = [Ab(34, -5), Ab(151, 3)], // Dodge -5, EndCast → C
        };
        SpellFormulaInput a = new()
        {
            Number = 1,
            MinBase = 1,
            MaxBase = 2,
            Abilities = [Ab(1, 0), Ab(151, 2)], // Damage, EndCast → B
        };

        string result = SpellEffectFormatter.Format(
            a,
            level: 0,
            resolveChain: n => n switch { 1 => a, 2 => b, 3 => c, _ => (SpellFormulaInput?)null },
            resolveSpellName: n => n switch { 2 => "step two", 3 => "step three", _ => null });

        Assert.Equal(
            "Dmg 1–2 · EndCast step two (Dodge -5 · EndCast step three (AC -10))",
            result);
    }

    [Fact]
    public void EndCast_Cycle_StopsWithoutInfiniteRecursion()
    {
        // A → EndCast B → EndCast A: the loop back to A must terminate.
        SpellFormulaInput a = new()
        {
            Number = 1,
            Abilities = [Ab(2, -1), Ab(151, 2)], // AC -1, EndCast → B
        };
        SpellFormulaInput b = new()
        {
            Number = 2,
            Abilities = [Ab(34, -1), Ab(151, 1)], // Dodge -1, EndCast → A (cycle)
        };

        string result = SpellEffectFormatter.Format(
            a,
            level: 0,
            resolveChain: n => n switch { 1 => a, 2 => b, _ => (SpellFormulaInput?)null },
            resolveSpellName: n => n switch { 1 => "alpha", 2 => "beta", _ => null });

        // B's chain back to A is dropped by the cycle guard, so B renders only
        // its own affect — no runaway recursion.
        Assert.Equal("AC -1 · EndCast beta (Dodge -1)", result);
    }

    // ----- Summon resolves to the monster name ----------------------------

    [Fact]
    public void Summon_ResolvesMonsterName()
    {
        // Summon (12) AbilVal is a monster number — render the creature's
        // name, not "Summon +590".
        SpellFormulaInput f = new() { Number = 90, Abilities = [Ab(12, 590)] };

        string result = SpellEffectFormatter.Format(
            f, level: 0, resolveChain: _ => null,
            resolveMonsterName: n => n == 590 ? "hydra" : null);

        Assert.Equal("Summon hydra", result);
    }

    [Fact]
    public void Summon_NoResolver_FallsBackToNumber()
    {
        SpellFormulaInput f = new() { Number = 90, Abilities = [Ab(12, 590)] };
        string result = SpellEffectFormatter.Format(f, level: 0, resolveChain: _ => null);
        Assert.Equal("Summon +590", result);
    }

    // ----- friendly jargon wording ----------------------------------------

    [Fact]
    public void NonMagicalSpell_RendersFriendlyPhrase()
    {
        // 144 (NonMagicalSpell) → plain English in the effect summary.
        SpellFormulaInput f = new()
        {
            Number = 1, MinBase = 5, MaxBase = 10,
            Abilities = [Ab(1, 0), Ab(144, 0)],   // Damage + NonMagicalSpell flag
        };
        string result = SpellEffectFormatter.Format(f, level: 0, resolveChain: _ => null);
        Assert.Equal("Dmg 5–10 · ignores magic resistance", result);
    }
}
