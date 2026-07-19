using System.IO;
using System.Linq;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

// Decode coverage for the room-entry hazard index: which cast-on-enter spells
// resolve to a protectable hazard, and which item(s) counter each. Exercises the
// three protection shapes (damage/negate, textblock failitem, textblock
// checkspell), the EndCast chain walk, layered (multi-group) protections, and
// the room-spell candidate filter that keeps attack spells out of the scan.
public sealed class RoomHazardIndexTests : IDisposable
{
    private readonly string _root;

    public RoomHazardIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-hazard-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // Build an index over a one-set fixture. Every table is optional except
    // Rooms/Spells (the index bails without them); a null table isn't written.
    private RoomHazardIndex NewIndex(
        string roomsJson,
        string spellsJson,
        string? itemsJson = null,
        string? tbInfoJson = null,
        string set = "alpha")
    {
        string setDir = Path.Combine(_root, set);
        Directory.CreateDirectory(setDir);
        File.WriteAllText(Path.Combine(setDir, "Rooms.json"), roomsJson);
        File.WriteAllText(Path.Combine(setDir, "Spells.json"), spellsJson);
        if (itemsJson is not null)
            File.WriteAllText(Path.Combine(setDir, "Items.json"), itemsJson);
        if (tbInfoJson is not null)
            File.WriteAllText(Path.Combine(setDir, "TBInfo.json"), tbInfoJson);

        GameDataCache cache = new(_root);
        cache.SwitchSet(set);
        RoomHazardIndex index = new(cache);
        index.OnActiveSetChanged(set);
        return index;
    }

    // A room whose Spell column casts `spell` on entry.
    private static string Room(int spell) => $$"""
        [ { "Map Number": 1, "Room Number": 2, "Name": "R", "Spell": {{spell}} } ]
        """;

    [Fact]
    public void Damage_WithNegator_IsHazard()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            """ [ { "Number": 700, "Abil-0": 1, "AbilVal-0": 25 } ] """,
            """ [ { "Number": 42, "NegateSpell-0": 700 } ] """);

        RoomHazardIndex.RoomHazard? h = idx.HazardForSpell(700);
        Assert.NotNull(h);
        Assert.Contains(42, h!.ProtectingItems);
        Assert.True(h.IsSatisfiedBy(id => id == 42));
        Assert.False(h.IsSatisfiedBy(_ => false));
    }

    [Fact]
    public void Damage_NoNegator_NotIndexed()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            """ [ { "Number": 700, "Abil-0": 1, "AbilVal-0": 25 } ] """,
            """ [ { "Number": 42, "NegateSpell-0": 999 } ] """);

        // Damaging but no item counters it → deliberately not indexed.
        Assert.Null(idx.HazardForSpell(700));
        Assert.Equal(0, idx.HazardCount);
    }

    [Fact]
    public void Benign_RoomSpell_NotIndexed()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            // Abil 5 is neither damage (1), endcast (151), nor textblock (148).
            """ [ { "Number": 700, "Abil-0": 5, "AbilVal-0": 3 } ] """,
            """ [ { "Number": 42, "NegateSpell-0": 700 } ] """);

        Assert.Null(idx.HazardForSpell(700));
    }

    [Fact]
    public void EndCastChain_NegatedMember_IsHazard()
    {
        RoomHazardIndex idx = NewIndex(
            Room(100),
            """
            [ { "Number": 100, "Abil-0": 151, "AbilVal-0": 101 },
              { "Number": 101, "Abil-0": 1,   "AbilVal-0": 40  } ]
            """,
            // The negator cancels a downstream chain member (101), not the root.
            """ [ { "Number": 55, "NegateSpell-0": 101 } ] """);

        RoomHazardIndex.RoomHazard? h = idx.HazardForSpell(100);
        Assert.NotNull(h);
        Assert.Contains(55, h!.ProtectingItems);
    }

    [Fact]
    public void TextBlock_FailItem_IsHazard()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            // Abil 148 = TextBlock → TBInfo #50.
            """ [ { "Number": 700, "Abil-0": 148, "AbilVal-0": 50 } ] """,
            itemsJson: null,
            tbInfoJson: """ [ { "Number": 50, "Action": "failitem 55" } ] """);

        RoomHazardIndex.RoomHazard? h = idx.HazardForSpell(700);
        Assert.NotNull(h);
        Assert.Contains(55, h!.ProtectingItems);   // holding item 55 aborts the chain
    }

    [Fact]
    public void TextBlock_CheckSpell_IsHazard()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            """ [ { "Number": 700, "Abil-0": 148, "AbilVal-0": 50 } ] """,
            // Item 60 casts buff spell 300 (Abil 43 = CastsSp).
            """ [ { "Number": 60, "Abil-0": 43, "AbilVal-0": 300 } ] """,
            """ [ { "Number": 50, "Action": "checkspell 300" } ] """);

        RoomHazardIndex.RoomHazard? h = idx.HazardForSpell(700);
        Assert.NotNull(h);
        Assert.Contains(60, h!.ProtectingItems);   // carry the buff source
    }

    // A checkspell hazard also records a BuffCounter carrying the buff spell #,
    // its source item(s), and the computed protection window (the buff's Dur in
    // rounds × SpellRoundSeconds) — the timer the walk-time provisioner re-`use`s
    // the source on.
    [Fact]
    public void CheckSpell_BuildsBuffCounter_WithDuration()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            // Room spell 700 is a TextBlock (checkspell gate); buff spell 300 has
            // Dur 600 rounds → 1800s. Item 60 casts buff 300 (Abil 43 = CastsSp).
            """
            [ { "Number": 700, "Abil-0": 148, "AbilVal-0": 50 },
              { "Number": 300, "Dur": 600 } ]
            """,
            """ [ { "Number": 60, "Abil-0": 43, "AbilVal-0": 300 } ] """,
            """ [ { "Number": 50, "Action": "checkspell 300" } ] """);

        RoomHazardIndex.RoomHazard? h = idx.HazardForSpell(700);
        Assert.NotNull(h);
        RoomHazardIndex.BuffCounter counter = Assert.Single(h!.BuffCounters);
        Assert.Equal(300, counter.BuffSpell);
        Assert.Contains(60, counter.SourceItems);
        Assert.Equal(1800, counter.DurationSeconds);   // 600 rounds × 3s
    }

    // A checkspell whose buff spell has no Dur in the data → DurationSeconds 0.
    // The provisioner falls back to a periodic refresh rather than once-and-never.
    [Fact]
    public void CheckSpell_UnknownDuration_IsZero()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            """ [ { "Number": 700, "Abil-0": 148, "AbilVal-0": 50 } ] """,
            """ [ { "Number": 60, "Abil-0": 43, "AbilVal-0": 300 } ] """,
            """ [ { "Number": 50, "Action": "checkspell 300" } ] """);

        RoomHazardIndex.RoomHazard? h = idx.HazardForSpell(700);
        Assert.NotNull(h);
        RoomHazardIndex.BuffCounter counter = Assert.Single(h!.BuffCounters);
        Assert.Equal(0, counter.DurationSeconds);
    }

    [Fact]
    public void LayeredProtections_RequireOneFromEachGroup()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            // Damage AND a textblock failitem gate → two independent groups.
            """ [ { "Number": 700, "Abil-0": 1, "AbilVal-0": 25, "Abil-1": 148, "AbilVal-1": 50 } ] """,
            """ [ { "Number": 42, "NegateSpell-0": 700 } ] """,
            """ [ { "Number": 50, "Action": "failitem 55" } ] """);

        RoomHazardIndex.RoomHazard? h = idx.HazardForSpell(700);
        Assert.NotNull(h);
        Assert.Equal(2, h!.RequirementGroups.Count);
        Assert.False(h.IsSatisfiedBy(id => id == 42));            // negator only
        Assert.False(h.IsSatisfiedBy(id => id == 55));            // failitem only
        Assert.True(h.IsSatisfiedBy(id => id is 42 or 55));       // one from each
    }

    [Fact]
    public void AttackSpell_NotACandidate()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            // Spell 800 is damaging + negated but is NOT any Room.Spell, so the
            // room-spell candidate filter must keep it out of the index.
            """
            [ { "Number": 700, "Abil-0": 1, "AbilVal-0": 25 },
              { "Number": 800, "Abil-0": 1, "AbilVal-0": 99 } ]
            """,
            """
            [ { "Number": 42, "NegateSpell-0": 700 },
              { "Number": 43, "NegateSpell-0": 800 } ]
            """);

        Assert.NotNull(idx.HazardForSpell(700));
        Assert.Null(idx.HazardForSpell(800));
        Assert.Equal(1, idx.HazardCount);
    }

    [Fact]
    public void NoActiveSet_IsEmpty()
    {
        RoomHazardIndex idx = NewIndex(
            Room(700),
            """ [ { "Number": 700, "Abil-0": 1, "AbilVal-0": 25 } ] """,
            """ [ { "Number": 42, "NegateSpell-0": 700 } ] """);
        Assert.Equal(1, idx.HazardCount);

        idx.OnActiveSetChanged(null);
        Assert.Equal(0, idx.HazardCount);
        Assert.Null(idx.HazardForSpell(700));
    }

    // MandatoryItems is the auto-acquire set: single-counter groups only. An
    // any-of group (2+ options) is a manual choice, so it's excluded — feeding
    // it to the each-required demand pipeline would acquire every option.
    [Fact]
    public void MandatoryItems_ExcludesAnyOfGroups()
    {
        var hazard = new RoomHazardIndex.RoomHazard(new IReadOnlyList<int>[]
        {
            new[] { 42 },          // sole counter — mandatory
            new[] { 55, 60 },      // any-of choice — omitted
        });

        Assert.Equal(new[] { 42 }, hazard.MandatoryItems);
        Assert.Equal(new[] { 42, 55, 60 }, hazard.ProtectingItems.OrderBy(i => i).ToArray());
    }

    [Fact]
    public void MandatoryItems_DedupesRepeatedCounter()
    {
        var hazard = new RoomHazardIndex.RoomHazard(new IReadOnlyList<int>[]
        {
            new[] { 42 },
            new[] { 42 },
        });

        Assert.Equal(new[] { 42 }, hazard.MandatoryItems);
    }
}
