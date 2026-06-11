using System.Collections.Generic;
using FujinTerm.Game.Spells;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the <c>{s}</c>/<c>{d}</c> caster-message template → regex matcher
/// that <see cref="CastingDirector"/> uses to confirm OUR party-buff cast
/// landed before starting the duration timer.
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
}
