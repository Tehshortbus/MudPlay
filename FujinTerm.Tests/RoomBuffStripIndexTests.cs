using System.IO;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

// Decode coverage for the room-entry buff-strip index: which cast-on-enter spells
// resolve to a buff-stripping room. Exercises the two buff-removal ability codes
// (RemovesSpell 122, DispellMagic 73), the EndCast chain walk, the room-spell
// candidate filter that keeps attack spells out of the scan, and the no-set clear.
public sealed class RoomBuffStripIndexTests : IDisposable
{
    private readonly string _root;

    public RoomBuffStripIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-buffstrip-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private RoomBuffStripIndex NewIndex(string roomsJson, string spellsJson, string set = "alpha")
    {
        string setDir = Path.Combine(_root, set);
        Directory.CreateDirectory(setDir);
        File.WriteAllText(Path.Combine(setDir, "Rooms.json"), roomsJson);
        File.WriteAllText(Path.Combine(setDir, "Spells.json"), spellsJson);

        GameDataCache cache = new(_root);
        cache.SwitchSet(set);
        RoomBuffStripIndex index = new(cache);
        index.OnActiveSetChanged(set);
        return index;
    }

    // A room whose Spell column casts `spell` on entry.
    private static string Room(int spell) => $$"""
        [ { "Map Number": 1, "Room Number": 2, "Name": "R", "Spell": {{spell}} } ]
        """;

    [Fact]
    public void RemovesSpell_StripsBuffs()
    {
        RoomBuffStripIndex idx = NewIndex(
            Room(700),
            // Abil 122 = RemovesSpell.
            """ [ { "Number": 700, "Abil-0": 122, "AbilVal-0": 55 } ] """);

        Assert.True(idx.StripsBuffs(700));
        Assert.Equal(1, idx.StripSpellCount);
    }

    [Fact]
    public void DispellMagic_StripsBuffs()
    {
        RoomBuffStripIndex idx = NewIndex(
            Room(700),
            // Abil 73 = DispellMagic.
            """ [ { "Number": 700, "Abil-0": 73, "AbilVal-0": 0 } ] """);

        Assert.True(idx.StripsBuffs(700));
    }

    [Fact]
    public void Benign_RoomSpell_NotStripping()
    {
        RoomBuffStripIndex idx = NewIndex(
            Room(700),
            // Abil 18 = Heal — neither removal ability.
            """ [ { "Number": 700, "Abil-0": 18, "AbilVal-0": 30 } ] """);

        Assert.False(idx.StripsBuffs(700));
        Assert.Equal(0, idx.StripSpellCount);
    }

    [Fact]
    public void EndCastChain_RemovesSpellMember_StripsBuffs()
    {
        RoomBuffStripIndex idx = NewIndex(
            Room(100),
            """
            [ { "Number": 100, "Abil-0": 151, "AbilVal-0": 101 },
              { "Number": 101, "Abil-0": 122, "AbilVal-0": 55  } ]
            """);

        Assert.True(idx.StripsBuffs(100));
    }

    [Fact]
    public void AttackSpell_NotACandidate()
    {
        RoomBuffStripIndex idx = NewIndex(
            Room(700),
            // Spell 800 removes buffs but is NOT any Room.Spell, so the
            // room-spell candidate filter keeps it out of the index.
            """
            [ { "Number": 700, "Abil-0": 122, "AbilVal-0": 55 },
              { "Number": 800, "Abil-0": 122, "AbilVal-0": 55 } ]
            """);

        Assert.True(idx.StripsBuffs(700));
        Assert.False(idx.StripsBuffs(800));
        Assert.Equal(1, idx.StripSpellCount);
    }

    [Fact]
    public void ZeroSpell_ReadsFalse()
    {
        RoomBuffStripIndex idx = NewIndex(
            Room(700),
            """ [ { "Number": 700, "Abil-0": 122, "AbilVal-0": 55 } ] """);

        Assert.False(idx.StripsBuffs(0));
    }

    [Fact]
    public void NoActiveSet_IsEmpty()
    {
        RoomBuffStripIndex idx = NewIndex(
            Room(700),
            """ [ { "Number": 700, "Abil-0": 122, "AbilVal-0": 55 } ] """);
        Assert.Equal(1, idx.StripSpellCount);

        idx.OnActiveSetChanged(null);
        Assert.Equal(0, idx.StripSpellCount);
        Assert.False(idx.StripsBuffs(700));
    }
}
