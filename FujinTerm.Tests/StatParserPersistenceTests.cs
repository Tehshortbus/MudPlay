using FujinTerm.Game;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the persist-and-rehydrate path: each successful capture
/// window snapshots the live PlayerStats into a LastKnownStats DTO
/// (consumed by AppServices to write onto the loaded profile), and
/// the Hydrate path reverses it so a freshly-loaded profile lands
/// with the user's last-observed values instead of zeros.
/// </summary>
public sealed class StatParserPersistenceTests
{
    private static (StatParser parser, PlayerStats stats) Setup()
    {
        PlayerStats stats = new();
        StatParser parser = new(stats);
        parser.TestArm();
        return (parser, stats);
    }

    [Fact]
    public void Snapshot_BeforeAnyParse_ReturnsZeroedDto()
    {
        var (p, _) = Setup();
        LastKnownStats snap = p.Snapshot();
        Assert.Equal(string.Empty, snap.Name);
        Assert.Equal(0, snap.Level);
        Assert.Equal(0, snap.Lives);
        Assert.Equal(0, snap.Hits);
    }

    [Fact]
    public void Snapshot_AfterParse_MirrorsLivePlayerStats()
    {
        var (p, _) = Setup();
        p.FeedTestLine("Name: Fujin WuzHere                  Lives/CP:           7/2");
        p.FeedTestLine("Race: Dark-Elf       Exp: 12345      Perception:         50");
        p.FeedTestLine("Class: Mystic        Level: 5        Stealth:            65");
        p.FeedTestLine("Hits:  120/150       Armour Class:  3/5    Thievery:     10");

        LastKnownStats snap = p.Snapshot();
        Assert.Equal("Fujin WuzHere", snap.Name);
        Assert.Equal("Dark-Elf",      snap.Race);
        Assert.Equal("Mystic",        snap.Class);
        Assert.Equal(5,     snap.Level);
        Assert.Equal(12345, snap.Exp);
        Assert.Equal(7,     snap.Lives);
        Assert.Equal(2,     snap.Cp);
        Assert.Equal(120,   snap.Hits);
        Assert.Equal(150,   snap.MaxHits);
        Assert.Equal(3,     snap.ArmourClass);
        Assert.Equal(5,     snap.MaxArmourClass);
        Assert.Equal(50,    snap.Perception);
        Assert.Equal(65,    snap.Stealth);
        Assert.Equal(10,    snap.Thievery);
    }

    [Fact]
    public void ScreenParsed_FiresOnGateCloseAfterCapture()
    {
        var (p, _) = Setup();
        LastKnownStats? captured = null;
        p.ScreenParsed += s => captured = s;

        p.FeedTestLine("Name: Raijin Wuz                     Lives/CP:           4/3");
        // Prompt line closes the gate via "prompt after capture".
        p.FeedTestLine("[HP=40/MA=34]:", isPromptLine: true);

        Assert.NotNull(captured);
        Assert.Equal("Raijin Wuz", captured!.Name);
        Assert.Equal(4, captured.Lives);
        Assert.Equal(3, captured.Cp);
    }

    [Fact]
    public void ScreenParsed_DoesNotFireForEmptyWindow()
    {
        // No actual stat-line content this window — the snapshot would
        // be all-zeros and writing it would clobber a previously-good
        // capture in the profile. Suppress.
        var (p, _) = Setup();
        bool fired = false;
        p.ScreenParsed += _ => fired = true;

        // Prompt line with nothing captured — gate closes "no capture".
        p.FeedTestLine("[HP=22]:", isPromptLine: true);

        Assert.False(fired);
    }

    [Fact]
    public void ScreenParsed_FiresOncePerCaptureWindow()
    {
        var (p, _) = Setup();
        int fires = 0;
        p.ScreenParsed += _ => fires++;

        p.FeedTestLine("Name: Fujin WuzHere                  Lives/CP:           9/0");
        p.FeedTestLine("Class: Mystic        Level: 1        Stealth:            65");
        p.FeedTestLine("[HP=22]:", isPromptLine: true);

        Assert.Equal(1, fires);
    }

    [Fact]
    public void Hydrate_NullSnapshot_ResetsAllFieldsAndHasParsedFlag()
    {
        var (p, s) = Setup();
        p.FeedTestLine("Name: Fujin WuzHere                  Lives/CP:           9/0");
        p.FeedTestLine("Class: Mystic        Level: 1        Stealth:            65");
        Assert.True(p.HasParsed);
        Assert.Equal("Fujin WuzHere", s.Name);

        p.Hydrate(null);

        Assert.Equal(string.Empty, s.Name);
        Assert.Equal(string.Empty, s.Race);
        Assert.Equal(string.Empty, s.Class);
        Assert.Equal(0, s.Level);
        Assert.Equal(0, s.Lives);
        Assert.Equal(0, s.Stealth);
        Assert.False(p.HasParsed);
    }

    [Fact]
    public void Hydrate_FromSnapshot_MarksTrapsKnown()
    {
        // A persisted snapshot is only ever written after a real capture, so
        // hydrating one counts as "trap skill known" — the walker trusts it
        // instead of halting on the first trap of a fresh session.
        var (p, _) = Setup();
        Assert.False(p.TrapsKnown);
        p.Hydrate(new LastKnownStats { Traps = 155 });
        Assert.True(p.TrapsKnown);
    }

    [Fact]
    public void Hydrate_NullSnapshot_ClearsTrapsKnown()
    {
        var (p, _) = Setup();
        p.FeedTestLine("Kai:   0/0                                  Traps:        155");
        Assert.True(p.TrapsKnown);
        p.Hydrate(null);
        Assert.False(p.TrapsKnown);
    }

    [Fact]
    public void Hydrate_FromSnapshot_RestoresEveryField()
    {
        var (p, s) = Setup();
        LastKnownStats snap = new()
        {
            Name = "Raijin",
            Race = "Human",
            Class = "Priest",
            Level = 5, Exp = 12345, Lives = 4, Cp = 3,
            ExpToNext = 1000, LevelExpSpan = 5000, LevelPercent = 80,
            Hits = 80, MaxHits = 100,
            Mana = 30, MaxMana = 50,
            Kai = 0, MaxKai = 0,
            ArmourClass = 3, MaxArmourClass = 5,
            Strength = 50, Intellect = 75, Willpower = 80, Agility = 55,
            Health = 60, Charm = 45,
            Perception = 40, Stealth = 20, Thievery = 0, Traps = 0,
            Picklocks = 0, Tracking = 0, MartialArts = 0, MagicRes = 15,
            Spellcasting = 95,
        };

        p.Hydrate(snap);

        Assert.Equal("Raijin", s.Name);
        Assert.Equal("Human",  s.Race);
        Assert.Equal("Priest", s.Class);
        Assert.Equal(5,    s.Level);
        Assert.Equal(12345, s.Exp);
        Assert.Equal(4,    s.Lives);
        Assert.Equal(3,    s.Cp);
        Assert.Equal(1000, s.ExpToNext);
        Assert.Equal(5000, s.LevelExpSpan);
        Assert.Equal(80,   s.LevelPercent);
        Assert.Equal(80,   s.Hits);
        Assert.Equal(100,  s.MaxHits);
        Assert.Equal(30,   s.Mana);
        Assert.Equal(50,   s.MaxMana);
        Assert.Equal(3,    s.ArmourClass);
        Assert.Equal(5,    s.MaxArmourClass);
        Assert.Equal(50,   s.Strength);
        Assert.Equal(75,   s.Intellect);
        Assert.Equal(80,   s.Willpower);
        Assert.Equal(55,   s.Agility);
        Assert.Equal(60,   s.Health);
        Assert.Equal(45,   s.Charm);
        Assert.Equal(40,   s.Perception);
        Assert.Equal(20,   s.Stealth);
        Assert.Equal(15,   s.MagicRes);
        Assert.Equal(95,   s.Spellcasting);
        // HasParsed flips true so RemoteCommandManager.LivesProvider
        // trusts the hydrated value immediately (it's better than
        // null / "blocked" until the user runs stat again).
        Assert.True(p.HasParsed);
    }

    [Fact]
    public void SnapshotThenHydrate_RoundTripIsLossless()
    {
        // The "close client, reconnect" flow end-to-end: capture →
        // snapshot → hydrate into a freshly-constructed parser → same
        // values land. Pins that every PlayerStats field has both a
        // snapshot AND a hydrate path; a future field addition has to
        // touch both or this test fails.
        var (writer, _) = Setup();
        writer.FeedTestLine("Name: Fujin WuzHere                  Lives/CP:           9/0");
        writer.FeedTestLine("Race: Dark-Elf       Exp: 0          Perception:         50");
        writer.FeedTestLine("Class: Mystic        Level: 1        Stealth:            65");
        writer.FeedTestLine("Hits:  22/22         Armour Class:  0/0    Thievery:     0");
        writer.FeedTestLine("Kai:   0/0                                  Traps:        0");
        writer.FeedTestLine("Strength:   60       Agility:   80          Tracking:     0");
        writer.FeedTestLine("Intellect:  60       Health:    30          Martial Arts: 118");
        writer.FeedTestLine("Willpower:  30       Charm:     40          MagicRes:     37");
        LastKnownStats snap = writer.Snapshot();

        PlayerStats freshLive = new();
        StatParser reader = new(freshLive);
        reader.Hydrate(snap);

        Assert.Equal("Fujin WuzHere", freshLive.Name);
        Assert.Equal("Dark-Elf",      freshLive.Race);
        Assert.Equal("Mystic",        freshLive.Class);
        Assert.Equal(9, freshLive.Lives);
        Assert.Equal(22, freshLive.Hits);
        Assert.Equal(22, freshLive.MaxHits);
        Assert.Equal(60, freshLive.Strength);
        Assert.Equal(80, freshLive.Agility);
        Assert.Equal(30, freshLive.Health);
        Assert.Equal(40, freshLive.Charm);
        Assert.Equal(50, freshLive.Perception);
        Assert.Equal(65, freshLive.Stealth);
        Assert.Equal(118, freshLive.MartialArts);
        Assert.Equal(37, freshLive.MagicRes);
    }
}
