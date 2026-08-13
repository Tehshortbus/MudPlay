using MudPlay.ViewModels.GameData.Edit;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// The "Override Attack" box disambiguation (<see cref="MonsterEditDialogViewModel.ParseAttackOverride"/>):
/// a positive integer is a Spell.Number (routed through the mana-gated attack-spell
/// rung). Text that resolves to a known spell's cast-code (via the injected
/// resolver) ALSO lands on that rung, via its resolved Number — someone typing
/// the code they'd actually cast in-game (report paradigm-20260813-070249)
/// shouldn't silently lose mana/cap gating for it. Only text matching no known
/// spell (or no resolver supplied) is a raw command sent as-is; blank is no
/// override. This is also what lets "attack" persist (report
/// paradigm-20260809-131642 — it used to be silently dropped by an int-only parse).
/// </summary>
public sealed class MonsterEditDialogViewModelTests
{
    [Theory]
    [InlineData("42")]
    [InlineData("  42  ")]   // trimmed
    public void ParseAttackOverride_PositiveInteger_IsSpellId(string text)
    {
        (int? spellId, string? command) = MonsterEditDialogViewModel.ParseAttackOverride(text);
        Assert.Equal(42, spellId);
        Assert.Null(command);
    }

    [Theory]
    [InlineData("attack", "attack")]
    [InlineData("  harm  ", "harm")]   // trimmed, kept as a command — no resolver supplied
    [InlineData("bash", "bash")]
    [InlineData("0", "0")]             // non-positive int is not a spell id → command
    [InlineData("-3", "-3")]
    public void ParseAttackOverride_NonNumericText_NoResolver_IsCommand(string text, string expected)
    {
        (int? spellId, string? command) = MonsterEditDialogViewModel.ParseAttackOverride(text);
        Assert.Null(spellId);
        Assert.Equal(expected, command);
    }

    [Fact]
    public void ParseAttackOverride_CastCodeMatchesKnownSpell_ResolvesToSpellId()
    {
        // Report paradigm-20260813-070249: typing "turn" (the cast-code you'd
        // actually type in-game) must land on the mana-gated spell rung, same
        // as typing its Spell.Number directly — not silently become an
        // ungated raw command just because it's text, not digits.
        (int? spellId, string? command) = MonsterEditDialogViewModel.ParseAttackOverride(
            "turn", code => code == "turn" ? 18 : null);

        Assert.Equal(18, spellId);
        Assert.Null(command);
    }

    [Fact]
    public void ParseAttackOverride_CastCodeMatch_IsCaseAndWhitespaceInsensitive()
    {
        (int? spellId, string? command) = MonsterEditDialogViewModel.ParseAttackOverride(
            "  TuRn  ", code => string.Equals(code, "turn", StringComparison.OrdinalIgnoreCase) ? 18 : null);

        Assert.Equal(18, spellId);
        Assert.Null(command);
    }

    [Fact]
    public void ParseAttackOverride_ResolverSupplied_NoMatch_FallsBackToCommand()
    {
        // A resolver is wired, but this text isn't a spell anyone knows —
        // still a legitimate raw command (e.g. "bash").
        (int? spellId, string? command) = MonsterEditDialogViewModel.ParseAttackOverride(
            "bash", _ => null);

        Assert.Null(spellId);
        Assert.Equal("bash", command);
    }

    [Fact]
    public void ParseAttackOverride_NumericText_ResolverNeverConsulted()
    {
        // A positive integer is always read as a literal Spell.Number —
        // the resolver (cast-code → number) isn't relevant here and must not
        // be invoked.
        bool resolverCalled = false;
        (int? spellId, string? command) = MonsterEditDialogViewModel.ParseAttackOverride(
            "18", _ => { resolverCalled = true; return 999; });

        Assert.Equal(18, spellId);
        Assert.Null(command);
        Assert.False(resolverCalled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ParseAttackOverride_Blank_IsNoOverride(string? text)
    {
        (int? spellId, string? command) = MonsterEditDialogViewModel.ParseAttackOverride(text);
        Assert.Null(spellId);
        Assert.Null(command);
    }

    [Fact]
    public void ParseAttackOverride_SetsExactlyOneOfThePair()
    {
        // The two backing fields are mutually exclusive — a species never carries
        // both a spell id and a command.
        (int? spellId, string? command) = MonsterEditDialogViewModel.ParseAttackOverride("attack");
        Assert.True(spellId is null ^ command is null || (spellId is null && command is null));
        Assert.Null(spellId);
        Assert.NotNull(command);
    }
}
