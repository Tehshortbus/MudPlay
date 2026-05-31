using FujinTerm.Models.GameData;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class MacroStoreTests
{
    [Theory]
    [InlineData("n",                                   new[] { "n" })]
    [InlineData("open chest;look;take all",            new[] { "open chest", "look", "take all" })]
    [InlineData("open chest^Mlook^Mtake all",          new[] { "open chest", "look", "take all" })]
    [InlineData("open chest^M;look",                   new[] { "open chest", "look" })]    // mixed delimiters
    [InlineData("   sneak  ;  hide  ",                 new[] { "sneak", "hide" })]         // trims fragments
    [InlineData(";;;n;;;",                             new[] { "n" })]                     // drops empties
    [InlineData("",                                    new string[0])]
    [InlineData(null,                                  new string[0])]
    public void SplitCommandSteps_BehavesAsSpecified(string? input, string[] expected)
    {
        IReadOnlyList<string> steps = MacroStore.SplitCommandSteps(input);
        Assert.Equal(expected, steps);
    }

    [Fact]
    public void IsDuplicate_FlagsSameChord_FromAnotherMacro()
    {
        MacroStore store = new();
        store.Macros.Add(new Macro("NumPad8", false, false, false, "n", true));
        Assert.True(store.IsDuplicate("NumPad8", false, false, false));
    }

    [Fact]
    public void IsDuplicate_ExcludesSpecifiedMacro_DuringSelfEdit()
    {
        MacroStore store = new();
        Macro self = new("NumPad8", false, false, false, "n", true);
        store.Macros.Add(self);
        // Editing 'self' without changing the chord should not flag a self-duplicate.
        Assert.False(store.IsDuplicate("NumPad8", false, false, false, excluding: self));
    }

    [Fact]
    public void IsDuplicate_DistinguishesByModifier()
    {
        MacroStore store = new();
        store.Macros.Add(new Macro("F1", false, false, false, "say hi", true));
        Assert.False(store.IsDuplicate("F1", true,  false, false));  // Ctrl+F1 free
        Assert.False(store.IsDuplicate("F1", false, true,  false));  // Shift+F1 free
        Assert.True (store.IsDuplicate("F1", false, false, false));  // plain F1 taken
    }

    [Fact]
    public void FindMatch_ReturnsNull_WhenMacroDisabled()
    {
        MacroStore store = new();
        store.Macros.Add(new Macro("NumPad8", false, false, false, "n", Enabled: false));
        Assert.Null(store.FindMatch("NumPad8", false, false, false));
    }

    [Fact]
    public void DefaultMacros_SeedAllEightCompassDirsPlusUpDown()
    {
        IReadOnlyList<Macro> defaults = MacroStore.DefaultMacros();
        Assert.Equal(10, defaults.Count);
        // Every default macro has a single one-letter movement command.
        foreach (Macro m in defaults)
        {
            Assert.True(m.Enabled);
            Assert.False(m.Ctrl); Assert.False(m.Shift); Assert.False(m.Alt);
            Assert.InRange(m.Command.Length, 1, 2);
        }
    }
}
