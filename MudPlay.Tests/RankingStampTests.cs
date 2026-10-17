using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Map;
using MudPlay.Game.Simulation;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// What a kept area ranking is compared by when it is listed again. The world it was
// made in: any difference drops it. The character it was made for, by groups: a
// difference marks it and names the group, and only the group that changed.
public sealed class RankingStampTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mudplay-stamps-" + Path.GetRandomFileName());

    public RankingStampTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    // ----- The world ---------

    private static RankingWorld World(int version = 1, string rooms = "10:1", string seed = "s") => new(
        version, RankingWorld.TableNames.ToDictionary(t => t, t => t == "Rooms" ? rooms : "5:5"), seed);

    [Fact]
    public void TheSameWorldDiffersInNothing() => Assert.Null(World().DiffersFrom(World()));

    [Fact]
    public void AnotherSimulatorVersionATableOrTheSeed_EachMakesItAnotherWorld()
    {
        Assert.Contains("simulator version 1 and this is version 2", World(version: 2).DiffersFrom(World()));
        Assert.Contains("Rooms table has changed", World(rooms: "11:1").DiffersFrom(World()));
        Assert.Contains("monster seed", World(seed: "t").DiffersFrom(World()));
        // A table the ranking was made without, or with and now gone.
        var without = new RankingWorld(1, new Dictionary<string, string>(), "s");
        Assert.NotNull(World().DiffersFrom(without));
        Assert.NotNull(without.DiffersFrom(World()));
    }

    // ----- The tables, as this client read them ---------

    // One client on a set: the files on disk, and what its cache says of the file
    // it last read each table from in the load it is on.
    private sealed class Client
    {
        private readonly string _setDir;
        private readonly Dictionary<string, string> _asRead = new(StringComparer.OrdinalIgnoreCase);

        public Client(string setDir)
        {
            _setDir = setDir;
            Watch = new RankingWorldWatch(_asRead.GetValueOrDefault, table => RankingStampReader.TableOnDisk(_setDir, table));
        }

        public RankingWorldWatch Watch { get; }
        public object Load { get; private set; } = new();

        // The cache reads the table's file as it is now, as a first lookup does and
        // as a lookup after the idle sweep dropped the table does.
        public void Read(params string[] tables)
        {
            foreach (string table in tables) _asRead[table] = RankingStampReader.TableOnDisk(_setDir, table)!;
        }

        // The set is read in again: a switch back, a reload, an import here.
        public void Reload()
        {
            _asRead.Clear();
            Load = new object();
        }

        public RankingWorld? World(out string? why) => Watch.Now(Load, "s", out why);
        public RankingWorld? World() => World(out _);
    }

    private string TableFile(string table) => Path.Combine(_dir, table + ".json");

    // A set with every table the simulator reads but Lairs, and one it doesn't.
    private Client OnASet()
    {
        foreach (string table in RankingWorld.TableNames.Where(t => t != "Lairs").Append("Shops"))
            File.WriteAllText(TableFile(table), """[{"Number":1}]""");
        return new Client(_dir);
    }

    // The file as another client's import leaves it: other rows, another length.
    private void ImportedElsewhere(params string[] tables)
    {
        foreach (string table in tables) File.WriteAllText(TableFile(table), """[{"Number":1},{"Number":2}]""");
    }

    [Fact]
    public void ATableIsStampedByTheFileItWasReadFrom_NotTheOneOnDiskNow()
    {
        Client client = OnASet();
        client.Read("Rooms", "Monsters");
        RankingWorld loaded = Assert.IsType<RankingWorld>(client.World());
        Assert.Equal(LoopSimulator.SimulatorVersion, loaded.SimulatorVersion);
        Assert.Equal(RankingWorldWatch.NoTable, loaded.Tables["Lairs"]);

        // Another client imports the set while this one runs on the rows it read.
        ImportedElsewhere("Rooms", "Shops");
        RankingWorld made = Assert.IsType<RankingWorld>(client.World(out string? why));
        Assert.Null(why);
        Assert.Null(made.DiffersFrom(loaded));
        Assert.Null(client.Watch.WhyUnfit);

        // After a restart the tables are the new ones, and that ranking is not of them.
        Client restarted = new(_dir);
        restarted.Read("Rooms", "Monsters");
        Assert.Contains("Rooms table has changed", restarted.World()!.DiffersFrom(made));
    }

    [Fact]
    public void ATableNotReadYetIsStampedFromItsFile_AndReadLaterFromThatFileIsTheSameStamp()
    {
        Client client = OnASet();
        client.Read("Rooms");
        RankingWorld before = client.World()!;

        // Read for the first time, or dropped from memory and read again, with the
        // file as it was: nothing has changed.
        client.Read("Items", "Rooms");
        Assert.Null(client.World()!.DiffersFrom(before));
        Assert.Null(client.Watch.WhyUnfit);
    }

    [Fact]
    public void ATableReadAgainFromAChangedFileLeavesNoWorld_UntilTheSetIsReadInAgain()
    {
        Client client = OnASet();
        client.Read("Rooms", "Monsters");
        RankingWorld loaded = client.World()!;

        // Dropped by the idle sweep, replaced on disk, read again: what was built at
        // load is from the old file and the rows from here on are the new one's.
        ImportedElsewhere("Rooms");
        client.Read("Rooms");
        Assert.Null(client.World(out string? why));
        Assert.Equal("Rooms was read again from a file that had changed since the set was loaded", why);
        Assert.StartsWith("Rooms ", client.Watch.WhyUnfit);

        // So is one stamped from its file before it was ever read, then read from another.
        ImportedElsewhere("Items");
        client.Read("Items");
        Assert.Null(client.World(out why));
        Assert.StartsWith("Items, Rooms were read again", why);

        // Nothing mends it within the load, the old file put back included.
        File.WriteAllText(TableFile("Rooms"), """[{"Number":1}]""");
        client.Read("Rooms");
        Assert.Null(client.World());

        // Read in again (an import here, a restart), everything is of the files now.
        client.Reload();
        client.Read("Rooms", "Monsters", "Items");
        RankingWorld reloaded = Assert.IsType<RankingWorld>(client.World());
        Assert.Null(client.Watch.WhyUnfit);
        Assert.Contains("Rooms, Items tables have changed", reloaded.DiffersFrom(loaded));
    }

    [Fact]
    public void TheFirstReadIsRememberedFromTheHeartbeat_NotOnlyWhenARankingAsks()
    {
        Client client = OnASet();
        client.Read("Rooms");
        client.Watch.Observe(client.Load);

        // No ranking has asked for the world yet; the re-read is caught all the same.
        ImportedElsewhere("Rooms");
        client.Read("Rooms");
        client.Watch.Observe(client.Load);

        Assert.StartsWith("Rooms ", client.Watch.WhyUnfit);
        Assert.Null(client.World());
    }

    [Fact]
    public void ATableThatArrivesInTheSetAfterItWasStampedAsMissing_IsAChangeLikeAnyOther()
    {
        Client client = OnASet();
        client.Read("Rooms");
        Assert.Equal(RankingWorldWatch.NoTable, client.World()!.Tables["Lairs"]);

        ImportedElsewhere("Lairs");
        client.Read("Lairs");

        Assert.Null(client.World(out string? why));
        Assert.StartsWith("Lairs was read again", why);
    }

    [Fact]
    public void ATableFileIsFoundWhateverItsCapitals_AndASetWithoutItHasNoStamp()
    {
        File.WriteAllText(Path.Combine(_dir, "rooms.json"), "[]");
        var file = new FileInfo(Path.Combine(_dir, "rooms.json"));

        Assert.Equal(RankingWorldWatch.Stamp(file.Length, file.LastWriteTimeUtc), RankingStampReader.TableOnDisk(_dir, "Rooms"));
        Assert.Null(RankingStampReader.TableOnDisk(_dir, "Monsters"));
        Assert.Null(RankingStampReader.TableOnDisk(Path.Combine(_dir, "missing"), "Rooms"));
    }

    // ----- The monster seed, as loaded ---------

    [Fact]
    public void TheSeedIsStampedByTheRecordsHeld_InAnyOrder()
    {
        static KeyValuePair<int, MonsterOverlay> Record(int number, string? area, MonsterAttackPriority? priority = null) =>
            new(number, new MonsterOverlay { Area = area, Priority = priority });

        string made = MonsterOverlaySeedStore.StampOf(new[] { Record(1, "Cave"), Record(2, "Marsh") });
        Assert.Equal(made, MonsterOverlaySeedStore.StampOf(new[] { Record(2, "Marsh"), Record(1, "Cave") }));
        Assert.NotEqual(made, MonsterOverlaySeedStore.StampOf(new[] { Record(1, "Cave"), Record(2, "Fen") }));
        Assert.NotEqual(made, MonsterOverlaySeedStore.StampOf(new[] { Record(1, "Cave"), Record(2, "Marsh", MonsterAttackPriority.High) }));
        Assert.NotEqual(made, MonsterOverlaySeedStore.StampOf(new[] { Record(1, "Cave") }));
        Assert.Equal("none", new MonsterOverlaySeedStore().ContentStamp);
    }

    [Fact]
    public void GameDataEditsCountByTierNameAndContent_AndAnEmptiedFileIsNoEdit()
    {
        var reader = new RankingStampReader();
        string global = Path.Combine(_dir, "global"), realm = Path.Combine(_dir, "realm"), own = Path.Combine(_dir, "own");
        foreach (string folder in new[] { global, realm, own }) Directory.CreateDirectory(folder);
        string[] tiers = { global, realm, own };
        string none = reader.GameDataEdits(tiers, "alpha");

        // Another set's edits, and a tier that isn't there, are nothing to this one.
        File.WriteAllText(Path.Combine(global, "monsters_overrides.beta.json"), """{"1":{"Hp":5}}""");
        Assert.Equal(none, reader.GameDataEdits(tiers.Append(Path.Combine(_dir, "missing")), "alpha"));

        string edit = Path.Combine(realm, "monsters_overrides.alpha.json");
        File.WriteAllText(edit, """{"1":{"Hp":5}}""");
        string edited = reader.GameDataEdits(tiers, "alpha");
        Assert.NotEqual(none, edited);

        // Saved again unchanged; then changed; then the same edit at another tier.
        File.WriteAllText(edit, """{"1":{"Hp":5}}""");
        File.SetLastWriteTimeUtc(edit, DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(edited, reader.GameDataEdits(tiers, "alpha"));
        File.WriteAllText(edit, """{"1":{"Hp":6}}""");
        Assert.NotEqual(edited, reader.GameDataEdits(tiers, "alpha"));
        File.Move(edit, Path.Combine(own, "monsters_overrides.alpha.json"));
        File.WriteAllText(Path.Combine(own, "monsters_overrides.alpha.json"), """{"1":{"Hp":5}}""");
        Assert.NotEqual(edited, reader.GameDataEdits(tiers, "alpha"));

        // The last edit undone leaves an empty file behind.
        File.WriteAllText(Path.Combine(own, "monsters_overrides.alpha.json"), "{}");
        Assert.Equal(none, reader.GameDataEdits(tiers, "alpha"));
    }

    // ----- The character ---------

    private static SimCharacter Character(int damage = 20, int maxHp = 200, string? spell = null,
        CombatSettings? combat = null, Func<int, double>? hpResting = null) => new(
        RealmType.ParaMud, Level: 30, MaxHp: maxHp, MaxMana: 100,
        Melee: new PlayerMatchupProfile(RealmType.ParaMud, NormalAccuracy: 100, AvgWeaponDamage: damage,
            SwingsPerRound: 2, HasWeapon: true, DamageResist: 2),
        WeaponHitMagic: 1,
        Defense: new PlayerDefenseProfile(30, 10, 0, 0, false, 0, EvilLevel.Saint, 0, AcExact: 30),
        Regen: new SimRegen(_ => 2, hpResting ?? (_ => 12), extra => extra * 0.2, 5, RealmRegenProfile.ParaMud, RestReplacesStanding: true),
        Spells: spell is null
            ? new Dictionary<string, SimSpell>()
            : new Dictionary<string, SimSpell>
            {
                [spell] = new(spell, 7, ManaPerCast: 5, FiresPerRound: 1, CastChance: 90, MinDamagePerRound: 10,
                    MaxDamagePerRound: 20, UsesMagicResist: true, TypeOfResists: 0, AttType: 0, MinHeal: 0, MaxHeal: 0),
            },
        Combat: combat ?? new CombatSettings(),
        Health: new HealthSettings(),
        SpellSlots: new SpellsSettings(),
        Overlay: _ => new MonsterOverlay(),
        SpellShortByNumber: _ => null,
        AlignmentValue: 0);

    private static RankingCharacter Of(SimCharacter c, string edits = "e", string reach = "r") =>
        RankingCharacter.Of(c, edits, reach);

    [Fact]
    public void TheSameCharacterReadTwiceHasChangedInNothing()
    {
        Assert.Empty(Of(Character(spell: "bolt")).ChangedSince(Of(Character(spell: "bolt"))));
        Assert.True(RankingCheck.Of(Of(Character()), Of(Character())).IsCurrent);
    }

    [Fact]
    public void EachGroupChangesByItselfAndIsNamed()
    {
        RankingCharacter made = Of(Character(spell: "bolt"));

        Assert.Equal(new[] { RankingCharacter.StatsOrGear }, Of(Character(damage: 25, spell: "bolt")).ChangedSince(made));
        Assert.Equal(new[] { RankingCharacter.StatsOrGear }, Of(Character(maxHp: 210, spell: "bolt")).ChangedSince(made));
        // Regen is functions: a different rate is a different character.
        Assert.Equal(new[] { RankingCharacter.StatsOrGear }, Of(Character(spell: "bolt", hpResting: _ => 13)).ChangedSince(made));
        Assert.Equal(new[] { RankingCharacter.Spells }, Of(Character(spell: "blst")).ChangedSince(made));
        Assert.Equal(new[] { RankingCharacter.Spells }, Of(Character()).ChangedSince(made));
        Assert.Equal(new[] { RankingCharacter.Settings },
            Of(Character(spell: "bolt", combat: new CombatSettings { RunDistance = 3 })).ChangedSince(made));
        Assert.Equal(new[] { RankingCharacter.GameDataEdits }, Of(Character(spell: "bolt"), edits: "other").ChangedSince(made));
        Assert.Equal(new[] { RankingCharacter.Reach }, Of(Character(spell: "bolt"), reach: "other").ChangedSince(made));

        // Several at once, in the order they are always named in.
        Assert.Equal(new[] { RankingCharacter.StatsOrGear, RankingCharacter.Settings, RankingCharacter.Reach },
            Of(Character(damage: 25, spell: "bolt", combat: new CombatSettings { RunDistance = 3 }), reach: "other").ChangedSince(made));
    }

    [Fact]
    public void AGroupEitherSideLacksCountsAsChanged_AndWithNoRecordNothingIsKnown()
    {
        RankingCharacter made = Of(Character());
        var partial = new RankingCharacter(made.Groups.Where(g => g.Key != RankingCharacter.Reach).ToDictionary(g => g.Key, g => g.Value));

        Assert.Equal(new[] { RankingCharacter.Reach }, made.ChangedSince(partial));
        Assert.Equal(new[] { RankingCharacter.Reach }, partial.ChangedSince(made));
        // The character can't be read now: not known yet. The ranking has no record
        // of it: never to be known. Neither is current.
        Assert.True(RankingCheck.Of(made, null).Unchecked);
        Assert.False(RankingCheck.Of(made, null).IsCurrent);
        Assert.True(RankingCheck.Of(null, made).NoRecord);
        Assert.False(RankingCheck.Of(null, made).Unchecked);
        Assert.False(RankingCheck.Of(null, made).IsCurrent);
        Assert.True(RankingCheck.Of(null, null).NoRecord);
    }

    // ----- Reach ---------

    [Fact]
    public void ReachChangesWithAvoidedAndBlacklistedRoomsClassRaceAndTheGateItemsCarried_NotWithTheirOrder()
    {
        RoomKey[] avoided = { new(1, 5), new(2, 9) };
        RoomKey[] blacklisted = { new(4, 1), new(3, 7) };
        string made = RankingReach.Hash(avoided, blacklisted, 3, 2, new[] { 807, 12 });

        Assert.Equal(made, RankingReach.Hash(avoided.Reverse(), blacklisted.Reverse(), 3, 2, new[] { 12, 807, 12 }));
        Assert.NotEqual(made, RankingReach.Hash(avoided.Take(1), blacklisted, 3, 2, new[] { 807, 12 }));
        Assert.NotEqual(made, RankingReach.Hash(avoided, blacklisted.Take(1), 3, 2, new[] { 807, 12 }));
        Assert.NotEqual(made, RankingReach.Hash(avoided, blacklisted.Append(new RoomKey(9, 9)), 3, 2, new[] { 807, 12 }));
        // A room avoided is not the same room blacklisted.
        Assert.NotEqual(made, RankingReach.Hash(blacklisted, avoided, 3, 2, new[] { 807, 12 }));
        Assert.NotEqual(made, RankingReach.Hash(avoided, blacklisted, 4, 2, new[] { 807, 12 }));
        Assert.NotEqual(made, RankingReach.Hash(avoided, blacklisted, 3, null, new[] { 807, 12 }));
        Assert.NotEqual(made, RankingReach.Hash(avoided, blacklisted, 3, 2, new[] { 807 }));
    }
}
