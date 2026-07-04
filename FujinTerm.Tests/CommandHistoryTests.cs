using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the shared command-recall ring: newest-last ordering, a 10-entry
/// cap that drops the oldest, blank / consecutive-duplicate suppression,
/// and a Changed event on every real mutation.
/// </summary>
public sealed class CommandHistoryTests
{
    [Fact]
    public void Record_AppendsNewestLast()
    {
        CommandHistory h = new();
        h.Record("north");
        h.Record("look");
        Assert.Equal(new[] { "north", "look" }, h.Entries);
    }

    [Fact]
    public void Record_BlankOrWhitespace_Ignored()
    {
        CommandHistory h = new();
        h.Record("");
        h.Record("   ");
        h.Record("\t");
        Assert.Empty(h.Entries);
    }

    [Fact]
    public void Record_ConsecutiveDuplicate_Ignored()
    {
        // Re-sending the same move shouldn't bury recall under repeats…
        CommandHistory h = new();
        h.Record("kill rat");
        h.Record("kill rat");
        Assert.Single(h.Entries);

        // …but a non-adjacent repeat is kept (it's genuinely "again, later").
        h.Record("flee");
        h.Record("kill rat");
        Assert.Equal(new[] { "kill rat", "flee", "kill rat" }, h.Entries);
    }

    [Fact]
    public void Record_PastCapacity_DropsOldest()
    {
        CommandHistory h = new();
        for (int i = 0; i < CommandHistory.Capacity + 3; i++)
            h.Record($"cmd{i}");

        Assert.Equal(CommandHistory.Capacity, h.Entries.Count);
        Assert.Equal("cmd3", h.Entries[0]);                                  // 0..2 dropped
        Assert.Equal($"cmd{CommandHistory.Capacity + 2}", h.Entries[^1]);    // newest kept
    }

    [Fact]
    public void Changed_FiresOnlyOnRealMutation()
    {
        CommandHistory h = new();
        int events = 0;
        h.Changed += () => events++;

        h.Record("a");      // +1
        h.Record("");       // ignored — no event
        h.Record("a");      // duplicate — no event
        h.Record("b");      // +1
        Assert.Equal(2, events);
    }
}
