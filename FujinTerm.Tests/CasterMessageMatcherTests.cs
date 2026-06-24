using System.Collections.Generic;
using System.Linq;
using FujinTerm.Game.Spells;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the <c>{s}</c>/<c>{d}</c>/<c>{target}</c>/<c>{source}</c>/<c>{dmg}</c>
/// caster-message template → regex matcher that <see cref="CastingDirector"/>
/// uses to confirm OUR party-buff cast landed before starting the duration
/// timer, plus the spell-aware <c>ConfirmsSpellTarget</c> used to clear a
/// member's ailment chip.
/// </summary>
public sealed class CasterMessageMatcherTests
{
    [Fact]
    public void Bless_TwoStringSlots_CapturesSpellAndTarget()
    {
        CasterMessageMatcher? m = CasterMessageMatcher.TryCreate("You cast {s} on {s}!");
        Assert.NotNull(m);

        Assert.True(m!.TryMatch("You cast bless on Raijin!", out IReadOnlyList<string> caps));
        Assert.Equal(new[] { "bless", "Raijin" }, caps);
    }

    [Fact]
    public void DamageLine_DropsNumericSlot_KeepsStringSlots()
    {
        CasterMessageMatcher? m = CasterMessageMatcher.TryCreate("You cast {s} on {s} for {d} healing!");
        Assert.NotNull(m);

        Assert.True(m!.TryMatch("You cast major healing on Raijin for 142 healing!",
            out IReadOnlyList<string> caps));
        // {d} is consumed but not surfaced — only the two {s} captures.
        Assert.Equal(new[] { "major healing", "Raijin" }, caps);
    }

    [Fact]
    public void UnanchoredMatch_ToleratesLeadingAndTrailingNoise()
    {
        CasterMessageMatcher? m = CasterMessageMatcher.TryCreate("You cast {s} on {s}!");
        Assert.NotNull(m);

        Assert.True(m!.TryMatch(">You cast bless on Raijin! [HP=70]",
            out IReadOnlyList<string> caps));
        Assert.Equal("Raijin", caps[1]);
    }

    [Fact]
    public void NonMatchingLine_ReturnsFalse()
    {
        CasterMessageMatcher? m = CasterMessageMatcher.TryCreate("You cast {s} on {s}!");
        Assert.NotNull(m);

        Assert.False(m!.TryMatch("Raijin gossips: hi", out IReadOnlyList<string> caps));
        Assert.Empty(caps);
    }

    [Fact]
    public void ConfirmsTarget_MatchesCapturedNameCaseInsensitively()
    {
        CasterMessageMatcher? m = CasterMessageMatcher.TryCreate("You cast {s} on {s}!");
        Assert.NotNull(m);

        Assert.True(m!.ConfirmsTarget("You cast bless on Raijin!", "raijin"));
        Assert.True(m.ConfirmsTarget("You cast bless on Raijin!", "RAIJIN"));
    }

    [Fact]
    public void ConfirmsTarget_RejectsWhenNamedSomeoneElse()
    {
        CasterMessageMatcher? m = CasterMessageMatcher.TryCreate("You cast {s} on {s}!");
        Assert.NotNull(m);

        // Line fits the template but names a different member — must not
        // confirm a cast we made on someone else.
        Assert.False(m!.ConfirmsTarget("You cast bless on Goldar!", "raijin"));
    }

    [Fact]
    public void TemplateWithoutStringSlot_ReturnsNull()
    {
        // No {s} ⇒ nothing to confirm a target against.
        Assert.Null(CasterMessageMatcher.TryCreate("You feel lucky!"));
        Assert.Null(CasterMessageMatcher.TryCreate("   "));
        Assert.Null(CasterMessageMatcher.TryCreate(null));
    }

    [Fact]
    public void NamedSlots_TargetAndDmg_TokenizeNotMatchedAsLiteralText()
    {
        // The shipped seed uses {target}/{dmg} verbatim; they must tokenize
        // (string + numeric) rather than be matched as the literal text
        // "{target}"/"{dmg}", which never appears in a server line.
        CasterMessageMatcher? m = CasterMessageMatcher.TryCreate("Fire burns {target} for {dmg} damage!");
        Assert.NotNull(m);

        Assert.True(m!.TryMatch("Fire burns Goblin for 37 damage!",
            out IReadOnlyList<string> caps));
        // {dmg} consumed but dropped — only the {target} string slot surfaces.
        Assert.Equal(new[] { "Goblin" }, caps);
    }

    [Fact]
    public void NamedSlot_Source_IsAStringCapture()
    {
        CasterMessageMatcher? m = CasterMessageMatcher.TryCreate("{source} wears bleeding main-gauche!");
        Assert.NotNull(m);

        Assert.True(m!.TryMatch("Raijin wears bleeding main-gauche!",
            out IReadOnlyList<string> caps));
        Assert.Equal(new[] { "Raijin" }, caps);
    }

    [Fact]
    public void ConfirmsSpellTarget_RequiresBothSpellAndTarget()
    {
        CasterMessageMatcher? m = CasterMessageMatcher.TryCreate("You cast {s} on {s}!");
        Assert.NotNull(m);

        // The cure line names both the cure spell and the member ⇒ confirm.
        Assert.True(m!.ConfirmsSpellTarget("You cast cure-poison on Forged!", "cure-poison", "forged"));
        // A different spell on the same member fits the template + names the
        // member, but the spell slot doesn't match ⇒ must NOT confirm.
        Assert.False(m.ConfirmsSpellTarget("You cast bless on Forged!", "cure-poison", "forged"));
        // Right spell, wrong member ⇒ must NOT confirm.
        Assert.False(m.ConfirmsSpellTarget("You cast cure-poison on Goblin!", "cure-poison", "forged"));
    }

    // ----- Semantic placeholders ({spellname}/{target}/{source}/{damage}) -----

    [Fact]
    public void Damage_NumericSynonym_TokenizesAndDrops()
    {
        // {damage} is a numeric synonym of {dmg}/{d} — consumed but not surfaced.
        CasterMessageMatcher? m = CasterMessageMatcher.TryCreate("{source} blasts {target} for {damage} damage!");
        Assert.NotNull(m);

        Assert.True(m!.TryMatch("Raijin blasts Goblin for 142 damage!",
            out IReadOnlyList<string> caps));
        // Two string slots ({source}, {target}); {damage} dropped.
        Assert.Equal(new[] { "Raijin", "Goblin" }, caps);
    }

    [Fact]
    public void Spellname_IsAStringCapture()
    {
        CasterMessageMatcher? m = CasterMessageMatcher.TryCreate("You cast {spellname} on {target}!");
        Assert.NotNull(m);

        Assert.True(m!.TryMatch("You cast cure-poison on Forged!",
            out IReadOnlyList<string> caps));
        Assert.Equal(new[] { "cure-poison", "Forged" }, caps);
    }

    [Fact]
    public void ConfirmsSpellTarget_SemanticTemplate_PinsExactSlots()
    {
        // Semantic witness template: source is the caster, target is who it
        // landed on. The pinned slots must be matched exactly.
        CasterMessageMatcher? m =
            CasterMessageMatcher.TryCreate("{source} casts {spellname} on {target}!");
        Assert.NotNull(m);

        // Mage cures Forged ⇒ spell + target both match the pinned slots.
        Assert.True(m!.ConfirmsSpellTarget("Mage casts cure-poison on Forged!", "cure-poison", "Forged"));
    }

    [Fact]
    public void ConfirmsSpellTarget_SemanticTemplate_SourceCannotBeMistakenForTarget()
    {
        // The crux of the semantic roles: the caster's name sits in {source}.
        // A position-agnostic matcher could mistake it for the target and
        // false-confirm; pinning {target} prevents that.
        CasterMessageMatcher? m =
            CasterMessageMatcher.TryCreate("{source} casts {spellname} on {target}!");
        Assert.NotNull(m);

        // Spell matches, but the requested target is the CASTER's name, which
        // lives in the {source} slot ⇒ must NOT confirm.
        Assert.False(m!.ConfirmsSpellTarget("Mage casts cure-poison on Forged!", "cure-poison", "Mage"));
    }

    [Fact]
    public void ConfirmsTarget_SemanticTemplate_MatchesOnlyTheTargetSlot()
    {
        CasterMessageMatcher? m =
            CasterMessageMatcher.TryCreate("{source} casts {spellname} on {target}!");
        Assert.NotNull(m);

        // The member named in {target} confirms.
        Assert.True(m!.ConfirmsTarget("Mage casts cure-poison on Forged!", "Forged"));
        // The caster (in {source}) must NOT satisfy a target check.
        Assert.False(m.ConfirmsTarget("Mage casts cure-poison on Forged!", "Mage"));
    }

    [Fact]
    public void Placeholders_ExposeAuthorableVocabulary()
    {
        // The editor legend is sourced from this list — it must carry the
        // semantic tokens (and their legacy generics) so authors can wire them.
        IEnumerable<string> tokens =
            CasterMessageMatcher.Placeholders.Select(p => p.Token);
        Assert.Contains("{spellname}", tokens);
        Assert.Contains("{target}", tokens);
        Assert.Contains("{source}", tokens);
        Assert.Contains("{damage}", tokens);
        Assert.Contains("{s}", tokens);
        Assert.Contains("{d}", tokens);
    }

    [Fact]
    public void RegexMetacharactersInLiteral_AreEscaped()
    {
        // Parentheses + dots in the literal must match verbatim, not act
        // as regex syntax.
        CasterMessageMatcher? m = CasterMessageMatcher.TryCreate("You cast {s} (holy)... at {s}.");
        Assert.NotNull(m);

        Assert.True(m!.TryMatch("You cast smite (holy)... at Raijin.",
            out IReadOnlyList<string> caps));
        Assert.Equal(new[] { "smite", "Raijin" }, caps);
        Assert.False(m.TryMatch("You cast smite XholyX at Raijin!", out _));
    }

    // ----- TryMatchDamage (Phase 11 proc / attack-spell recogniser) -----

    [Fact]
    public void TryMatchDamage_SurfacesTheNumericCapture()
    {
        CasterMessageMatcher? m =
            CasterMessageMatcher.TryCreate("You cast {s} at {target} for {damage} damage!");
        Assert.NotNull(m);

        Assert.True(m!.TryMatchDamage("You cast fireball at the kobold for 37 damage!", out int dmg));
        Assert.Equal(37, dmg);
    }

    [Fact]
    public void TryMatchDamage_StripsThousandsSeparators()
    {
        CasterMessageMatcher? m =
            CasterMessageMatcher.TryCreate("Your weapon sears {target} for {damage} damage!");
        Assert.NotNull(m);

        Assert.True(m!.TryMatchDamage("Your weapon sears the dragon for 1,204 damage!", out int dmg));
        Assert.Equal(1204, dmg);
    }

    [Fact]
    public void TryMatchDamage_NumericOnlyTemplate_Compiles()
    {
        // A template with no string slot is still accepted so the damage
        // recogniser can compile it (the confirmation helpers simply decline).
        CasterMessageMatcher? m =
            CasterMessageMatcher.TryCreate("The flames burst for {damage} damage!");
        Assert.NotNull(m);

        Assert.True(m!.TryMatchDamage("The flames burst for 9 damage!", out int dmg));
        Assert.Equal(9, dmg);
    }

    [Fact]
    public void TryMatchDamage_NoNumericSlot_MatchesWithZeroDamage()
    {
        // A recognised cast that carries no number still matches the template;
        // damage stays 0.
        CasterMessageMatcher? m = CasterMessageMatcher.TryCreate("You blast {target}!");
        Assert.NotNull(m);

        Assert.True(m!.TryMatchDamage("You blast the kobold!", out int dmg));
        Assert.Equal(0, dmg);
    }

    [Fact]
    public void TryMatchDamage_NonMatchingLine_ReturnsFalse()
    {
        CasterMessageMatcher? m =
            CasterMessageMatcher.TryCreate("You cast {s} at {target} for {damage} damage!");
        Assert.NotNull(m);

        Assert.False(m!.TryMatchDamage("Raijin gossips: hi", out int dmg));
        Assert.Equal(0, dmg);
    }
}
