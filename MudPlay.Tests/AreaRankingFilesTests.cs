using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Simulation;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Area rankings on disk, and the store that keeps them there: a ranking written is
// read back whole by a client started later; a file is never trusted (another
// world, another format, another owner, damage, age all drop and delete it); the
// files follow a character through a rename and go with a deleted one; and nothing
// is read until a pairing is shown.
//
// Sets are throwaway folders under the data root (AppPaths can't be pointed
// elsewhere), removed in Dispose.
public sealed class AreaRankingFilesTests : IDisposable
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..10];
    private readonly List<string> _sets = new();

    private string NewSet(string name)
    {
        string set = $"test-rank-{name}-{_tag}";
        Directory.CreateDirectory(AppPaths.GameDataSetDir(set));
        _sets.Add(set);
        return set;
    }

    public void Dispose()
    {
        foreach (string set in _sets)
        {
            try
            {
                string dir = AppPaths.GameDataSetDir(set);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch { /* best-effort temp cleanup */ }
        }
    }

    // BBS names carry the test's own tag: a move or delete by character or BBS walks
    // every set under the data root, other test classes' included.
    private string Board => $"board-{_tag}";
    private string Tavern => $"tavern-{_tag}";
    private string One => $"{Board}/one";
    private string Two => $"{Board}/two";
    private string Renamed => $"{Board}/renamed";
    private string OtherRenamed => $"other-{_tag}/renamed";
    private string ElsewhereOne => $"elsewhere-{_tag}/one";

    private static readonly RankingWorld World = new(
        LoopSimulator.SimulatorVersion, RankingWorld.TableNames.ToDictionary(t => t, _ => "10:1"), "seed");

    private static readonly RankingCharacter Character = new(RankingCharacter.Order.ToDictionary(g => g, g => "h-" + g));

    private static AreaRanking Ranking(string area = "Hills", DateTimeOffset? madeAt = null) => new(
        new[]
        {
            new AreaRank(area, 42, new[] { new RoomKey(1, 5), new RoomKey(12, 3401), new RoomKey(1, 7) }, 17,
                new LoopSimSummary(
                    new[]
                    {
                        new LoopSimRun(3600, 3600, 512_345, 310, 12, 900.5, 1800.25, 300, 0, 599.25, 31, 8, null,
                            new Dictionary<string, int> { ["bolt"] = 40, ["a"] = 280 }, Flees: 2, HungUpAtSeconds: null, DamageTaken: 9001),
                        new LoopSimRun(3600, 1200.5, 90_000, 44, 3, 200, 700, 100, 50, 150.5, 0, 2, 1200.5,
                            new Dictionary<string, int>(), Flees: 0, HungUpAtSeconds: 1100.25, DamageTaken: 4000),
                    },
                    new[] { new ExpBossStat("the warden", 1234.5, 6) })),
            new AreaRank("My loop", 42, new[] { new RoomKey(2, 2), new RoomKey(2, 3) }, 9,
                new LoopSimSummary(new[] { new LoopSimRun(3600, 3600, 1, 1, 1, 1, 1, 1, 1, 1, 90, 90, null, new Dictionary<string, int>()) }),
                IsLoop: true, Live: new[] { new LiveLoopRecord("My loop", 41, 3, 5.5, 2_000_000, 900, 1) }),
        },
        Level: 42, Step: 1.15, Hours: 1.5, Runs: 2, From: new RoomKey(1, 5), Realm: "Paradigm",
        Summary: "L42 from 1/5: 1 safe option(s)", MadeAt: madeAt ?? new DateTimeOffset(2026, 6, 18, 14, 32, 0, TimeSpan.FromHours(2)),
        CharacterLevel: 41, World: World, Character: Character);

    private static AreaRankingFiles Files(DateTimeOffset? now = null) =>
        new(() => now ?? new DateTimeOffset(2026, 6, 19, 9, 0, 0, TimeSpan.FromHours(2)));

    // ----- The files ---------

    [Fact]
    public void ARankingWrittenIsReadBackWhole()
    {
        var owner = new SimulatorOwner(NewSet("a"), One);
        AreaRanking made = Ranking();

        (string? error, long bytes) = Files().Write(owner, made);
        AreaRankingFiles.ReadResult read = Files().Read(owner, World);

        Assert.Null(error);
        Assert.True(bytes > 0);
        Assert.Null(read.Dropped);
        AreaRanking back = Assert.IsType<AreaRanking>(read.Ranking);
        Assert.True(back.FromDisk);
        Assert.Equal((made.Level, made.Step, made.Hours, made.Runs, made.From, made.Realm, made.Summary, made.MadeAt, made.CharacterLevel),
            (back.Level, back.Step, back.Hours, back.Runs, back.From, back.Realm, back.Summary, back.MadeAt, back.CharacterLevel));
        Assert.Null(World.DiffersFrom(back.World!));
        Assert.Empty(back.Character!.ChangedSince(Character));
        Assert.Equal(made.Rows.Count, back.Rows.Count);
        foreach ((AreaRank was, AreaRank now) in made.Rows.Zip(back.Rows))
        {
            Assert.Equal((was.Area, was.Level, was.LapRooms, was.IsLoop), (now.Area, now.Level, now.LapRooms, now.IsLoop));
            Assert.Equal(was.Tour, now.Tour);
            Assert.Equal(was.Result.Bosses ?? Array.Empty<ExpBossStat>(), now.Result.Bosses ?? Array.Empty<ExpBossStat>());
            Assert.Equal(was.Live ?? Array.Empty<LiveLoopRecord>(), now.Live ?? Array.Empty<LiveLoopRecord>());
            foreach ((LoopSimRun a, LoopSimRun b) in was.Result.Runs.Zip(now.Result.Runs))
            {
                // A record compares its Casts by which dictionary it is, so that is
                // set aside and compared by what it holds on the next line.
                var none = new Dictionary<string, int>();
                Assert.Equal(a with { Casts = none }, b with { Casts = none });
                Assert.Equal(a.Casts.OrderBy(c => c.Key), b.Casts.OrderBy(c => c.Key));
            }
            // What the table shows comes out the same.
            Assert.Equal(was.Label, now.Label);
        }
    }

    [Fact]
    public void TheLoopsARankingRankedAreReadBackWithIt_LookedUpByNameWhateverItsCapitals()
    {
        string set = NewSet("loops");
        var owner = new SimulatorOwner(set, One);
        AreaRankingFiles files = Files();
        files.Write(owner, Ranking() with { Loops = new Dictionary<string, string> { ["Crypt run"] = "abc", ["Marsh run"] = "def" } });

        AreaRanking read = Assert.IsType<AreaRanking>(files.Read(owner, World).Ranking);
        Assert.Equal(2, read.Loops!.Count);
        Assert.Equal("abc", read.Loops["crypt RUN"]);

        // One made before loops were recorded has none, which is not the same as no loops.
        files.Write(owner, Ranking());
        Assert.Null(files.Read(owner, World).Ranking!.Loops);
    }

    [Fact]
    public void TheFileSitsInItsSetsFolderUnderItsBbs_AndNoneIsKeptForNoCharacterOrNoSet()
    {
        string set = NewSet("a");
        Files().Write(new SimulatorOwner(set, One), Ranking());

        Assert.True(File.Exists(Path.Combine(AppPaths.GameDataSetDir(set), "Simulator", Board, "one.ranking.json")));
        // Not among the set's own files, where tables are listed and counted.
        Assert.Empty(Directory.EnumerateFiles(AppPaths.GameDataSetDir(set)));
        Assert.Null(AreaRankingFiles.FileFor(new SimulatorOwner(set, null)));
        Assert.Null(AreaRankingFiles.FileFor(new SimulatorOwner(null, One)));
        Assert.Equal((null, 0L), Files().Write(new SimulatorOwner(set, null), Ranking()));
    }

    [Fact]
    public void NothingIsWrittenToASetWhoseFolderIsGone_OrForARankingWithNoRecordOfItsWorld()
    {
        var gone = new SimulatorOwner($"test-rank-gone-{_tag}", One);
        var owner = new SimulatorOwner(NewSet("a"), One);

        Assert.Equal((null, 0L), Files().Write(gone, Ranking()));
        Assert.False(Directory.Exists(AppPaths.GameDataSetDir(gone.Set!)));
        Assert.Equal((null, 0L), Files().Write(owner, Ranking() with { World = null }));
        Assert.False(File.Exists(AreaRankingFiles.FileFor(owner)));
    }

    public static TheoryData<string, RankingWorld, string> OtherWorlds => new()
    {
        { "simulator", World with { SimulatorVersion = World.SimulatorVersion + 1 }, "simulator version" },
        { "table", World with { Tables = World.Tables.ToDictionary(t => t.Key, t => t.Key == "Monsters" ? "11:2" : t.Value) }, "Monsters table has changed" },
        { "seed", World with { Seed = "another" }, "monster seed" },
    };

    [Theory]
    [MemberData(nameof(OtherWorlds))]
    public void ARankingMadeInAnotherWorldIsDroppedAndItsFileDeleted(string what, RankingWorld now, string why)
    {
        var owner = new SimulatorOwner(NewSet(what), One);
        Files().Write(owner, Ranking());

        AreaRankingFiles.ReadResult read = Files().Read(owner, now);

        Assert.Null(read.Ranking);
        Assert.Contains(why, read.Dropped);
        Assert.False(File.Exists(AreaRankingFiles.FileFor(owner)));
    }

    [Fact]
    public void AFileForAnotherOwnerOrOfAnotherFormatOrTooOld_IsDropped()
    {
        string set = NewSet("a");
        var owner = new SimulatorOwner(set, One);
        string path = AreaRankingFiles.FileFor(owner)!;

        // Another character's file copied into this one's place, by hand.
        Files().Write(new SimulatorOwner(set, Two), Ranking());
        File.Copy(AreaRankingFiles.FileFor(new SimulatorOwner(set, Two))!, path);
        Assert.Contains("it says it is for", Files().Read(owner, World).Dropped);
        Assert.False(File.Exists(path));

        // A file from a client with a later format.
        Files().Write(owner, Ranking());
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"Format\":1", "\"Format\":2"));
        Assert.Contains("file format 2", Files().Read(owner, World).Dropped);
        Assert.False(File.Exists(path));

        // Made more than the keeping time ago.
        Files().Write(owner, Ranking());
        var later = new DateTimeOffset(2026, 6, 18, 0, 0, 0, TimeSpan.Zero) + AreaRankingFiles.MaxAge + TimeSpan.FromDays(2);
        Assert.Contains("days ago", Files(later).Read(owner, World).Dropped);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("null")]
    [InlineData("{}")]
    public void AFileThatIsNotARankingIsDroppedNotTrusted(string contents)
    {
        var owner = new SimulatorOwner(NewSet("a"), One);
        string path = AreaRankingFiles.FileFor(owner)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);

        AreaRankingFiles.ReadResult read = Files().Read(owner, World);

        Assert.Null(read.Ranking);
        Assert.NotNull(read.Dropped);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ATruncatedFile_AndOneWhoseRowsAreNotRows_AreDropped()
    {
        var owner = new SimulatorOwner(NewSet("a"), One);
        string path = AreaRankingFiles.FileFor(owner)!;
        Files().Write(owner, Ranking());
        string whole = File.ReadAllText(path);

        File.WriteAllText(path, whole[..(whole.Length / 2)]);
        Assert.Contains("could not be read", Files().Read(owner, World).Dropped);
        Assert.False(File.Exists(path));

        File.WriteAllText(path, whole.Replace("12/3401", "12/x"));
        Assert.Contains("not a ranking", Files().Read(owner, World).Dropped);
        Assert.False(File.Exists(path));

        // No file at all is not a drop.
        Assert.Equal(default, Files().Read(owner, World));
    }

    [Fact]
    public void ACharactersFilesGoWithItOnEverySet_AndAreReadBackAsItsOwnUnderTheNewName()
    {
        string a = NewSet("a"), b = NewSet("b");
        AreaRankingFiles files = Files();
        files.Write(new SimulatorOwner(a, One), Ranking("Hills"));
        files.Write(new SimulatorOwner(b, One), Ranking("Marsh"));
        files.Write(new SimulatorOwner(a, Two), Ranking("Fen"));
        // Kept under the new name by whoever had it before.
        files.Write(new SimulatorOwner(b, OtherRenamed), Ranking("Dunes"));
        files.Write(new SimulatorOwner(a, OtherRenamed), Ranking("Moor"));

        files.MoveCharacter(One, OtherRenamed);

        Assert.Equal("Hills", files.Read(new SimulatorOwner(a, OtherRenamed), World).Ranking?.Rows[0].Area);
        Assert.Equal("Marsh", files.Read(new SimulatorOwner(b, OtherRenamed), World).Ranking?.Rows[0].Area);
        Assert.Equal(default, files.Read(new SimulatorOwner(a, One), World));
        Assert.Equal(default, files.Read(new SimulatorOwner(b, One), World));
        Assert.Equal("Fen", files.Read(new SimulatorOwner(a, Two), World).Ranking?.Rows[0].Area);

        // A deleted profile's go with it.
        files.MoveCharacter(Two, null);
        Assert.Equal(default, files.Read(new SimulatorOwner(a, Two), World));
    }

    [Fact]
    public void ARenamedBbsTakesEveryCharactersFiles_AndARemovedOneLeavesNone()
    {
        string a = NewSet("a"), b = NewSet("b");
        AreaRankingFiles files = Files();
        files.Write(new SimulatorOwner(a, One), Ranking("Hills"));
        files.Write(new SimulatorOwner(b, Two), Ranking("Marsh"));
        files.Write(new SimulatorOwner(a, ElsewhereOne), Ranking("Fen"));
        // The loaded character has told of itself first, and is there already.
        files.MoveCharacter(One, $"{Tavern}/one");

        files.MoveBbs(Board, Tavern);

        Assert.Equal("Hills", files.Read(new SimulatorOwner(a, $"{Tavern}/one"), World).Ranking?.Rows[0].Area);
        Assert.Equal("Marsh", files.Read(new SimulatorOwner(b, $"{Tavern}/two"), World).Ranking?.Rows[0].Area);
        Assert.False(Directory.Exists(Path.Combine(AppPaths.SimulatorRankingsFolder(a), Board)));
        Assert.False(Directory.Exists(Path.Combine(AppPaths.SimulatorRankingsFolder(b), Board)));

        files.DeleteBbs(Tavern);
        Assert.Equal(default, files.Read(new SimulatorOwner(a, $"{Tavern}/one"), World));
        Assert.Equal(default, files.Read(new SimulatorOwner(b, $"{Tavern}/two"), World));
        Assert.Equal("Fen", files.Read(new SimulatorOwner(a, ElsewhereOne), World).Ranking?.Rows[0].Area);
    }

    [Fact]
    public void ACopyOfASetsEditsAndLoops_CarriesNoRankings_AndDeletingTheSetTakesThem()
    {
        string source = NewSet("src"), dest = NewSet("dst");
        Files().Write(new SimulatorOwner(source, One), Ranking());
        File.WriteAllText(AppPaths.MessagesFile(source), "m");
        Directory.CreateDirectory(AppPaths.GameDataSetLoopsFolder(source));
        File.WriteAllText(Path.Combine(AppPaths.GameDataSetLoopsFolder(source), "a.loop"), "x");
        var manager = new GameDataSetManager(new GameDataCache(), _ => { }, _ => { });

        Assert.True(manager.Copy(source, dest, GameDataSetPart.Loops | GameDataSetPart.Messages | GameDataSetPart.RecordOverrides).Ok);
        Assert.True(manager.Move(source, dest, GameDataSetPart.Loops | GameDataSetPart.Messages).Ok);

        Assert.False(Directory.Exists(AppPaths.SimulatorRankingsFolder(dest)));
        Assert.True(File.Exists(AreaRankingFiles.FileFor(new SimulatorOwner(source, One))));

        Assert.True(manager.DeleteSet(source).Ok);
        Assert.False(File.Exists(AreaRankingFiles.FileFor(new SimulatorOwner(source, One))));
    }

    // ----- The store over them ---------

    private sealed class Client
    {
        public List<Action> Posted { get; } = new();
        public LogService Log { get; } = new();
        public AreaRankingStore Store { get; }

        // The file work runs in turn on the calling thread; what it hands back to the
        // UI thread waits in Posted until Settle runs it.
        public Client(AreaRankingFiles? files = null) =>
            Store = new AreaRankingStore(files ?? Files(), act => Posted.Add(act), Log, runFileWork: work => work());

        // Let what the file work handed back run, and whatever that hands back.
        public void Settle()
        {
            for (int round = 0; round < 10; round++)
            {
                Action[] batch = Posted.ToArray();
                Posted.Clear();
                if (batch.Length == 0) return;
                foreach (Action act in batch) act();
            }
        }

        public AreaRanking? Load(SimulatorOwner owner, RankingWorld? world = null)
        {
            AreaRanking? got = null;
            Store.Load(owner, world ?? World, r => got = r);
            Settle();
            return got;
        }

        public string[] Said(LogSeverity severity) =>
            Log.Snapshot().Where(e => e.Severity == severity).Select(e => e.Message).ToArray();
    }

    [Fact]
    public void ARankingKeptIsThereForAClientStartedLater_WhichReadsNothingUntilAsked()
    {
        var owner = new SimulatorOwner(NewSet("a"), One);
        var first = new Client();
        first.Store.Keep(owner, Ranking());
        first.Settle();
        Assert.Contains(first.Said(LogSeverity.Info), m => m.StartsWith("saved the area ranking for"));

        var later = new Client();
        later.Settle();
        Assert.Equal(0, later.Store.Count);
        Assert.Empty(later.Store.DiskNotes);
        Assert.Null(later.Store.Get(owner));

        AreaRanking? back = later.Load(owner);

        Assert.Equal("Hills", back?.Rows[0].Area);
        Assert.True(back!.FromDisk);
        // Held from here on: asking again reads nothing.
        Assert.Same(back, later.Store.Get(owner));
    }

    [Fact]
    public void ADroppedFileIsSaidWhy_AndHandsBackNothing()
    {
        var owner = new SimulatorOwner(NewSet("a"), One);
        var first = new Client();
        first.Store.Keep(owner, Ranking());
        first.Settle();

        var later = new Client();
        Assert.Null(later.Load(owner, World with { Seed = "another" }));

        Assert.Contains(later.Said(LogSeverity.Info), m => m.Contains("was dropped: the monster seed"));
        Assert.Contains(later.Store.DiskNotes, n => n.Contains("was dropped"));
        Assert.False(File.Exists(AreaRankingFiles.FileFor(owner)));
    }

    [Fact]
    public void RankingAgainAndReplacedGameData_DeleteTheFile_ButBeingLetGoFromMemoryDoesNot()
    {
        string a = NewSet("a"), b = NewSet("b");
        var client = new Client();
        var onA = new SimulatorOwner(a, One);
        client.Store.Keep(onA, Ranking("Hills"));
        client.Store.Keep(new SimulatorOwner(b, One), Ranking("Marsh"));
        client.Store.Keep(new SimulatorOwner(b, Two), Ranking("Fen"));
        client.Settle();

        // More rankings than memory holds, on sets with no folder (so no files).
        for (int i = 0; i < AreaRankingStore.MaxKept; i++)
            client.Store.Keep(new SimulatorOwner($"test-rank-none-{i}-{_tag}", One), Ranking());
        client.Settle();
        Assert.Null(client.Store.Get(onA));
        Assert.True(File.Exists(AreaRankingFiles.FileFor(onA)));
        // Shown again, it is read back.
        Assert.Equal("Hills", client.Load(onA)?.Rows[0].Area);

        // A re-rank starts.
        client.Store.Forget(onA);
        client.Settle();
        Assert.False(File.Exists(AreaRankingFiles.FileFor(onA)));

        // The set's data is replaced: every character's go.
        client.Store.ForgetSet(b);
        client.Settle();
        Assert.False(Directory.Exists(AppPaths.SimulatorRankingsFolder(b)));
        Assert.Contains(client.Said(LogSeverity.Info), m => m.StartsWith("deleted 2 saved area ranking(s)"));
    }

    [Fact]
    public void ARankingMadeWhileAFileIsBeingReadStands()
    {
        var owner = new SimulatorOwner(NewSet("a"), One);
        var first = new Client();
        first.Store.Keep(owner, Ranking("Hills"));
        first.Settle();

        var later = new Client();
        AreaRanking? listed = Ranking("unset");
        later.Store.Load(owner, World, r => listed = r);
        AreaRanking fresh = Ranking("Marsh");
        later.Store.Keep(owner, fresh);
        later.Settle();

        Assert.Equal("unset", listed?.Rows[0].Area);
        Assert.Same(fresh, later.Store.Get(owner));
    }

    // A read is still out when the ranking it is reading is forgotten. The delete is
    // queued behind it, so the read finds the file; what it found must not come back.
    [Fact]
    public void ARankingForgottenWhileItIsBeingRead_IsNotPutBack()
    {
        var owner = new SimulatorOwner(NewSet("a"), One);
        var first = new Client();
        first.Store.Keep(owner, Ranking("Hills"));
        first.Settle();

        // A re-rank starts before the read of the earlier session's file is back.
        var later = new Client();
        var handed = new List<AreaRanking?>();
        later.Store.Load(owner, World, handed.Add);
        later.Store.Forget(owner);
        later.Settle();

        Assert.DoesNotContain(handed, r => r is not null);
        Assert.Null(later.Store.Get(owner));
        Assert.Equal(0, later.Store.Count);
        Assert.False(File.Exists(AreaRankingFiles.FileFor(owner)));
    }

    // A set read in again is not a set whose data was replaced: its saved rankings
    // stay on disk. A read that was out at the time was compared with the load that
    // has gone, so what it found is not handed back or held; asked again with the
    // world of the new load, the ranking comes back if its tables are that load's.
    [Fact]
    public void ARankingBeingReadWhenItsSetIsReadInAgain_IsNotHandedBack_AndItsFileStays()
    {
        string set = NewSet("a");
        var owner = new SimulatorOwner(set, One);
        var first = new Client();
        first.Store.Keep(owner, Ranking("Hills"));
        first.Settle();

        var later = new Client();
        later.Store.NoteActiveSet(set);
        var handed = new List<AreaRanking?>();
        later.Store.Load(owner, World, handed.Add);
        later.Store.NoteActiveSet(set);
        later.Settle();

        Assert.Empty(handed);
        Assert.Null(later.Store.Get(owner));
        Assert.True(File.Exists(AreaRankingFiles.FileFor(owner)));

        Assert.Equal("Hills", later.Load(owner)?.Rows[0].Area);
        Assert.True(File.Exists(AreaRankingFiles.FileFor(owner)));
    }

    // The same set read in again, twice over, with a client holding its rankings in
    // memory and on disk: nothing is deleted from either.
    [Fact]
    public void TheSameSetReadInAgain_DeletesNoSavedRanking()
    {
        string set = NewSet("a");
        var owner = new SimulatorOwner(set, One);
        var other = new SimulatorOwner(set, Two);
        var client = new Client();
        client.Store.NoteActiveSet(set);
        client.Store.Keep(owner, Ranking("Hills"));
        client.Store.Keep(other, Ranking("Marsh"));
        client.Settle();

        Assert.Equal(0, client.Store.NoteActiveSet(set));
        Assert.Equal(0, client.Store.NoteActiveSet(set));
        client.Settle();

        Assert.Equal(2, client.Store.Count);
        Assert.True(File.Exists(AreaRankingFiles.FileFor(owner)));
        Assert.True(File.Exists(AreaRankingFiles.FileFor(other)));
        Assert.DoesNotContain(client.Store.DiskNotes, n => n.Contains("deleted") || n.Contains("dropped"));
        Assert.NotNull(client.Store.Get(owner, World));
    }

    // ----- The sweep of old files ---------
    //
    // The sweep walks every set under the data root, other test classes' included,
    // so it is run here on a clock decades back: only the files these tests date
    // before it are old to it.

    private static readonly DateTimeOffset SweepNow = new(1995, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset OldThen = SweepNow - AreaRankingFiles.MaxAge - TimeSpan.FromDays(1);
    private static readonly DateTimeOffset FreshThen = SweepNow - AreaRankingFiles.MaxAge + TimeSpan.FromDays(1);

    private static string Save(SimulatorOwner owner, DateTimeOffset madeAt)
    {
        Files().Write(owner, Ranking(madeAt: madeAt));
        string file = AreaRankingFiles.FileFor(owner)!;
        Assert.True(File.Exists(file));
        return file;
    }

    // Every set, every BBS, every profile: a file made more than MaxAge ago by its
    // own stamp goes, whoever it belongs to, and a BBS folder that leaves empty
    // goes with it. One day inside the limit stays, and so does its folder.
    [Fact]
    public void TheSweep_DeletesFilesOlderThanTheLimitOnEverySetAndBbs_AndTheFoldersItEmpties()
    {
        string a = NewSet("a"), b = NewSet("b");
        string oldOne = Save(new SimulatorOwner(a, One), OldThen);
        string freshTwo = Save(new SimulatorOwner(a, Two), FreshThen);
        string oldElsewhere = Save(new SimulatorOwner(a, ElsewhereOne), OldThen);
        string oldOnB = Save(new SimulatorOwner(b, OtherRenamed), OldThen);
        var skipped = new List<string>();

        int deleted = Files(SweepNow).SweepOld(Array.Empty<string>(), skipped.Add);

        Assert.Equal(3, deleted);
        Assert.False(File.Exists(oldOne));
        Assert.False(File.Exists(oldElsewhere));
        Assert.False(File.Exists(oldOnB));
        Assert.True(File.Exists(freshTwo));
        Assert.False(Directory.Exists(Path.GetDirectoryName(oldElsewhere)));
        Assert.False(Directory.Exists(Path.GetDirectoryName(oldOnB)));
        Assert.True(Directory.Exists(Path.GetDirectoryName(freshTwo)));
        Assert.DoesNotContain(skipped, s => s.Contains(_tag));

        // A second sweep, as another client starting would make, finds nothing.
        Assert.Equal(0, Files(SweepNow).SweepOld(Array.Empty<string>()));
        Assert.True(File.Exists(freshTwo));
    }

    // Age is the stamp inside the file, as the read judges it, never the file's
    // time: an old ranking in a file written a moment ago goes, and a new ranking
    // in a file dated long ago stays.
    [Fact]
    public void TheSweep_GoesByTheStampInsideTheFile_NotTheFilesTime()
    {
        string set = NewSet("a");
        string old = Save(new SimulatorOwner(set, One), OldThen);
        string fresh = Save(new SimulatorOwner(set, Two), FreshThen);
        File.SetLastWriteTimeUtc(fresh, new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(1, Files(SweepNow).SweepOld(Array.Empty<string>()));

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    // What is not a ranking is not the sweep's: another file beside the rankings, a
    // ranking file that can't be parsed or that carries no stamp (the read judges
    // those), and a file of a ranking the client holds in memory.
    [Fact]
    public void TheSweep_LeavesWhatIsNotAnOldRanking_AndWhatIsHeld()
    {
        string set = NewSet("a");
        string held = Save(new SimulatorOwner(set, One), OldThen);
        string folder = Path.GetDirectoryName(held)!;
        string notes = Path.Combine(folder, "notes.txt");
        string damaged = Path.Combine(folder, "damaged.ranking.json");
        string unstamped = Path.Combine(folder, "unstamped.ranking.json");
        File.WriteAllText(notes, "MadeAt 1900");
        File.WriteAllText(damaged, "{ not json");
        File.WriteAllText(unstamped, "{\"Format\":1}");
        var skipped = new List<string>();

        Assert.Equal(0, Files(SweepNow).SweepOld(new[] { held.ToUpperInvariant() }, skipped.Add));

        Assert.All(new[] { held, notes, damaged, unstamped }, f => Assert.True(File.Exists(f), f));
        Assert.Contains(skipped, s => s.StartsWith(damaged, StringComparison.Ordinal));
        Assert.Contains(skipped, s => s.StartsWith(unstamped, StringComparison.Ordinal) && s.EndsWith("it carries no made-at stamp"));

        // Not held, the old one goes; the folder keeps what is left in it.
        Assert.Equal(1, Files(SweepNow).SweepOld(Array.Empty<string>()));
        Assert.False(File.Exists(held));
        Assert.True(File.Exists(notes));
    }

    // Two clients sweeping at once: a file is counted by the one that got it. While
    // another holds it open it is left and not counted; once gone, a pass that had
    // listed it counts nothing for it. The two logs never add up to more than went.
    [Fact]
    public void TheSweep_CountsOnlyAFileItTookItself()
    {
        string set = NewSet("a");
        string old = Save(new SimulatorOwner(set, One), OldThen);
        var skipped = new List<string>();

        using (new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(0, Files(SweepNow).SweepOld(Array.Empty<string>(), skipped.Add));
        Assert.True(File.Exists(old));
        Assert.Contains(skipped, s => s.StartsWith(old, StringComparison.Ordinal));

        Assert.Equal(1, Files(SweepNow).SweepOld(Array.Empty<string>()));
        Assert.False(File.Exists(old));
        Assert.Equal(0, Files(SweepNow).SweepOld(Array.Empty<string>()));
    }

    // A file that will not open (another client holds it to itself) is passed by at
    // once, not waited on as a read waits on a file being replaced: the sweep runs at
    // every start, ahead of the first ranking listed. Each such file once cost about
    // half a second. It is left in place and said.
    [Fact]
    public void TheSweep_PassesAFileThatWillNotOpenWithoutWaiting_AndLeavesIt()
    {
        string set = NewSet("a");
        string[] locked = new[] { One, Two, Renamed }.Select(c => Save(new SimulatorOwner(set, c), OldThen)).ToArray();
        var skipped = new List<string>();
        var holds = locked.Select(f => new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None)).ToList();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Assert.Equal(0, Files(SweepNow).SweepOld(Array.Empty<string>(), skipped.Add));
            clock.Stop();
        }
        finally
        {
            foreach (FileStream hold in holds) hold.Dispose();
        }

        // Waited on, the three would take well over a second; the bound sits well
        // past scheduler noise and still under that.
        Assert.True(clock.ElapsedMilliseconds < 1000, $"{clock.ElapsedMilliseconds} ms");
        Assert.All(locked, f => Assert.True(File.Exists(f), f));
        Assert.All(locked, f => Assert.Contains(skipped, s => s.StartsWith(f, StringComparison.Ordinal)));

        // Let go, they are old rankings like any other.
        Assert.Equal(3, Files(SweepNow).SweepOld(Array.Empty<string>()));
    }

    // A file that cannot be deleted (its folder cannot be written) is opened all the
    // same, and on Linux the delete then fails without a word. It is not counted,
    // it is said, and the file stays.
    [Fact]
    public void TheSweep_DoesNotCountAFileItCouldNotDelete()
    {
        // No such thing as a folder that cannot be written for root, and Windows
        // refuses the open itself.
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        string set = NewSet("a");
        string old = Save(new SimulatorOwner(set, One), OldThen);
        string folder = Path.GetDirectoryName(old)!;
        var skipped = new List<string>();
        UnixFileMode before = File.GetUnixFileMode(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.Equal(0, Files(SweepNow).SweepOld(Array.Empty<string>(), skipped.Add));
        }
        finally
        {
            File.SetUnixFileMode(folder, before);
        }

        Assert.True(File.Exists(old));
        Assert.Contains(skipped, s => s.StartsWith(old, StringComparison.Ordinal) && s.Contains("could not be deleted"));
        Assert.True(Directory.Exists(folder));
    }

    // What the sweep left is one line of the log for the pass, with the count: two
    // clients sweeping together leave each other nearly every old file.
    [Fact]
    public void TheStoresSweep_SaysWhatItLeftInOneLine()
    {
        string set = NewSet("a");
        string folder = Path.GetDirectoryName(Save(new SimulatorOwner(set, One), FreshThen))!;
        File.WriteAllText(Path.Combine(folder, "one.ranking.json"), "{\"Format\":1}");
        File.WriteAllText(Path.Combine(folder, "two.ranking.json"), "{ not json");
        var client = new Client(Files(SweepNow));

        client.Store.SweepOld();
        client.Settle();

        LogEntry line = Assert.Single(client.Log.Snapshot(), l => l.Message.StartsWith("the sweep of saved area rankings left"));
        Assert.Equal(LogSeverity.Debug, line.Severity);
        // At least these two: the sweep walks every set under the data root, other
        // test classes' included.
        int left = int.Parse(System.Text.RegularExpressions.Regex.Match(line.Message, @"left (\d+) file\(s\)").Groups[1].Value);
        Assert.True(left >= 2, line.Message);
    }

    // A Simulator folder or a BBS folder that is a link leads wherever it likes:
    // the sweep does not look through it, however old what lies behind it.
    [Fact]
    public void TheSweep_DoesNotLookThroughALinkedFolder()
    {
        if (OperatingSystem.IsWindows()) return;   // making a link needs a privilege there
        string source = NewSet("src"), viaBbs = NewSet("bbs"), viaSimulator = NewSet("sim");
        // Old rankings in a real folder that is no set's Simulator folder.
        string victim = Path.Combine(AppPaths.GameDataSetDir(source), "elsewhere");
        Directory.CreateDirectory(Path.Combine(victim, "inner"));
        string direct = Path.Combine(victim, "one.ranking.json"), inner = Path.Combine(victim, "inner", "one.ranking.json");
        string real = Save(new SimulatorOwner(source, One), OldThen);
        File.Copy(real, direct);
        File.Copy(real, inner);
        File.Delete(real);

        Directory.CreateDirectory(AppPaths.SimulatorRankingsFolder(viaBbs));
        Directory.CreateSymbolicLink(Path.Combine(AppPaths.SimulatorRankingsFolder(viaBbs), $"linked-{_tag}"), victim);
        Directory.CreateSymbolicLink(AppPaths.SimulatorRankingsFolder(viaSimulator), victim);

        Files(SweepNow).SweepOld(Array.Empty<string>());

        Assert.True(File.Exists(direct));
        Assert.True(File.Exists(inner));
    }

    // Through the store it is one piece of file work in the queue: a ranking saved
    // just before is on disk when the sweep looks, and one held in memory keeps its
    // file whatever its age. Only the count is logged, and only when something went.
    [Fact]
    public void TheStoresSweep_RunsInTheQueue_KeepsWhatIsHeld_AndLogsTheCountOnce()
    {
        string set = NewSet("a");
        string gone = Save(new SimulatorOwner(set, Two), OldThen);
        var held = new SimulatorOwner(set, One);
        var client = new Client(Files(SweepNow));
        client.Store.Keep(held, Ranking(madeAt: OldThen));

        client.Store.SweepOld();
        client.Settle();

        Assert.False(File.Exists(gone));
        Assert.True(File.Exists(AreaRankingFiles.FileFor(held)));
        LogEntry line = Assert.Single(client.Log.Snapshot(), l => l.Message.Contains("made more than 180 days ago"));
        Assert.Equal(LogSeverity.Info, line.Severity);
        Assert.Contains("deleted 1 saved area ranking(s)", line.Message);

        client.Store.SweepOld();
        client.Settle();
        Assert.Single(client.Log.Snapshot(), l => l.Message.Contains("made more than 180 days ago"));
    }

    [Fact]
    public void ARankingWhoseSetIsReplacedWhileItIsBeingRead_IsNotPutBack()
    {
        string set = NewSet("a");
        var owner = new SimulatorOwner(set, One);
        var first = new Client();
        first.Store.Keep(owner, Ranking("Hills"));
        first.Settle();

        foreach (Action<AreaRankingStore> replaced in new Action<AreaRankingStore>[]
                 {
                     s => s.ForgetSet(set),
                     s => s.ForgetCharacter(One),
                     s => s.ForgetBbs(Board),
                 })
        {
            first.Store.Keep(owner, Ranking("Hills"));
            first.Settle();
            var later = new Client();
            var handed = new List<AreaRanking?>();
            later.Store.Load(owner, World, handed.Add);
            replaced(later.Store);
            later.Settle();

            Assert.DoesNotContain(handed, r => r is not null);
            Assert.Null(later.Store.Get(owner));
            Assert.False(File.Exists(AreaRankingFiles.FileFor(owner)));
        }
    }

    [Fact]
    public void AReadThatComesBackAfterSomethingElseWasForgotten_IsAskedAgain_AndStillListed()
    {
        string set = NewSet("a"), other = NewSet("b");
        var owner = new SimulatorOwner(set, One);
        var first = new Client();
        first.Store.Keep(owner, Ranking("Hills"));
        first.Store.Keep(new SimulatorOwner(other, One), Ranking("Marsh"));
        first.Settle();

        var later = new Client();
        var handed = new List<AreaRanking?>();
        later.Store.Load(owner, World, handed.Add);
        later.Store.ForgetSet(other);
        later.Settle();

        Assert.Equal("Hills", Assert.Single(handed)?.Rows[0].Area);
        Assert.NotNull(later.Store.Get(owner));
    }

    [Fact]
    public void AWriteThatFailsDoesNotThrow_IsSaidOnce_AndTheRankingIsStillHeld()
    {
        string set = NewSet("a");
        var client = new Client();
        // A file where the rankings folder would be: nothing can be written under it.
        File.WriteAllText(AppPaths.SimulatorRankingsFolder(set), "in the way");

        AreaRanking made = Ranking();
        client.Store.Keep(new SimulatorOwner(set, One), made);
        client.Store.Keep(new SimulatorOwner(set, Two), Ranking());
        client.Settle();

        Assert.Same(made, client.Store.Get(new SimulatorOwner(set, One)));
        Assert.Single(client.Said(LogSeverity.Warn), m => m.Contains("could not be saved"));
        Assert.Equal(2, client.Store.DiskNotes.Count(n => n.Contains("could not be saved")));
    }

    [Fact]
    public void ARenameThatOnlyChangesCapitals_MovesTheFile_SoALaterClientFindsIt()
    {
        string set = NewSet("a");
        string capitals = $"{Board}/ONE";
        var first = new Client();
        AreaRanking made = Ranking("Hills");
        first.Store.Keep(new SimulatorOwner(set, One), made);
        first.Settle();

        // Nothing to carry in memory, which matches names without case.
        Assert.Equal(0, first.Store.RenameCharacter(One, capitals));
        first.Settle();
        Assert.Same(made, first.Store.Get(new SimulatorOwner(set, capitals)));

        // On disk the file is under the new spelling, to the letter.
        string kept = AreaRankingFiles.FileFor(new SimulatorOwner(set, capitals))!;
        Assert.Contains(Path.GetFileName(kept), Directory.EnumerateFiles(Path.GetDirectoryName(kept)!).Select(Path.GetFileName));
        Assert.Equal("Hills", new Client().Load(new SimulatorOwner(set, capitals))?.Rows[0].Area);

        // Said twice, or with nothing under the old spelling, it loses nothing.
        first.Store.RenameCharacter(One, capitals);
        first.Settle();
        Assert.Equal("Hills", new Client().Load(new SimulatorOwner(set, capitals))?.Rows[0].Area);
    }

    [Fact]
    public void ABbsRenameThatOnlyChangesCapitals_KeepsEveryRankingUnderIt()
    {
        string set = NewSet("a");
        string capitals = Board.ToUpperInvariant();
        Assert.NotEqual(Board, capitals);
        var first = new Client();
        first.Store.Keep(new SimulatorOwner(set, One), Ranking("Hills"));
        first.Store.Keep(new SimulatorOwner(set, Two), Ranking("Fen"));
        first.Settle();
        string oldFolder = Path.GetDirectoryName(AreaRankingFiles.FileFor(new SimulatorOwner(set, One))!)!;
        // Not a ranking: where capitals are told apart it stays behind, and is not
        // taken with the folder.
        File.WriteAllText(Path.Combine(oldFolder, "notes.txt"), "kept");

        var later = new Client();
        later.Store.RenameBbs(Board, capitals);
        later.Settle();

        string one = One[(Board.Length + 1)..], two = Two[(Board.Length + 1)..];
        Assert.Equal("Hills", new Client().Load(new SimulatorOwner(set, $"{capitals}/{one}"))?.Rows[0].Area);
        Assert.Equal("Fen", new Client().Load(new SimulatorOwner(set, $"{capitals}/{two}"))?.Rows[0].Area);
        Assert.True(File.Exists(Path.Combine(oldFolder, "notes.txt")));
    }

    [Fact]
    public void ACharacterRenamedMovedOrDeleted_TakesItsFilesOrLosesThem_WhetherOrNotHeldInMemory()
    {
        string a = NewSet("a"), b = NewSet("b");
        var first = new Client();
        first.Store.Keep(new SimulatorOwner(a, One), Ranking("Hills"));
        first.Store.Keep(new SimulatorOwner(b, One), Ranking("Marsh"));
        first.Store.Keep(new SimulatorOwner(a, Two), Ranking("Fen"));
        first.Settle();

        // A later client holds none of them in memory: these are profiles not loaded.
        var later = new Client();
        later.Store.RenameCharacter(One, Renamed);
        later.Store.ForgetCharacter(Two);
        later.Settle();

        Assert.Equal("Hills", later.Load(new SimulatorOwner(a, Renamed))?.Rows[0].Area);
        Assert.Equal("Marsh", later.Load(new SimulatorOwner(b, Renamed))?.Rows[0].Area);
        Assert.Null(later.Load(new SimulatorOwner(a, One)));
        Assert.Null(later.Load(new SimulatorOwner(a, Two)));

        // Its BBS renamed, then removed: held in memory or not.
        later.Store.RenameBbs(Board, Tavern);
        later.Settle();
        Assert.Equal("Hills", later.Store.Get(new SimulatorOwner(a, $"{Tavern}/renamed"))?.Rows[0].Area);
        Assert.Null(later.Store.Get(new SimulatorOwner(a, Renamed)));
        Assert.True(File.Exists(AreaRankingFiles.FileFor(new SimulatorOwner(b, $"{Tavern}/renamed"))));

        later.Store.ForgetBbs(Tavern);
        later.Settle();
        Assert.Equal(0, later.Store.Count);
        Assert.False(File.Exists(AreaRankingFiles.FileFor(new SimulatorOwner(a, $"{Tavern}/renamed"))));
        Assert.False(File.Exists(AreaRankingFiles.FileFor(new SimulatorOwner(b, $"{Tavern}/renamed"))));
    }

    // ----- Stamped with the tables this client read, over the real cache ---------

    // A running client on a set: the cache, with the set's tables read as a load
    // reads them, and the watch the app puts over it.
    private sealed class Running
    {
        public GameDataCache Cache { get; } = new(AppPaths.GameDataRoot);
        public RankingWorldWatch Watch { get; }

        public Running(string set, params string[] read)
        {
            Cache.SwitchSet(set);
            foreach (string table in read) Assert.NotNull(Cache.GetTable(table));
            Watch = RankingWorldWatch.Over(Cache);
            Watch.Observe(Cache.ActiveLoad);
        }

        // What a ranking finishing now is stamped with.
        public RankingWorld? MadeIn(out string? why) => Watch.NowForSaving(Cache.ActiveLoad, "seed", out why);
        public RankingWorld? MadeIn() => MadeIn(out _);
        // What a saved ranking is compared with.
        public RankingWorld? World() => Watch.Now(Cache.ActiveLoad, "seed", out _);
    }

    private static readonly string[] Tables = RankingWorld.TableNames.Where(t => t != "Lairs").ToArray();

    private string NewSetWithTables(string name = "tables")
    {
        string set = NewSet(name);
        foreach (string table in Tables) File.WriteAllText(TableFile(set, table), """[{"Number":1}]""");
        return set;
    }

    private static string TableFile(string set, string table) => Path.Combine(AppPaths.GameDataSetDir(set), table + ".json");

    // The file as another client's import leaves it: other rows, another length.
    private static void ImportedElsewhere(string set, string table) =>
        File.WriteAllText(TableFile(set, table), """[{"Number":1},{"Number":2}]""");

    private static string StampOf(string file) =>
        RankingWorldWatch.Stamp(new FileInfo(file).Length, new FileInfo(file).LastWriteTimeUtc);

    [Fact]
    public void ARankingFinishedAfterATableWasReplacedOnDisk_IsStampedWithTheFileThatWasRead()
    {
        string set = NewSetWithTables();
        var owner = new SimulatorOwner(set, One);
        string rooms = TableFile(set, "Rooms");
        string asLoaded = StampOf(rooms);
        var running = new Running(set, "Rooms", "Monsters");

        // Another client imports the set; this one is not told and reloads nothing.
        ImportedElsewhere(set, "Rooms");
        Assert.NotEqual(asLoaded, StampOf(rooms));

        RankingWorld madeIn = Assert.IsType<RankingWorld>(running.MadeIn(out string? why));
        Assert.Null(why);
        Assert.Equal(asLoaded, madeIn.Tables["Rooms"]);
        Assert.Equal(RankingWorldWatch.NoTable, madeIn.Tables["Lairs"]);
        // Every other table was read to be stamped: none is vouched for off the disk.
        Assert.All(Tables, t => Assert.NotNull(running.Cache.FileStampAsRead(t)));

        var client = new Client();
        client.Store.Keep(owner, Ranking() with { World = madeIn });
        client.Settle();
        Assert.True(File.Exists(AreaRankingFiles.FileFor(owner)));

        // The same client lists it again as its own: it is still on those tables.
        var sameSession = new Client();
        Assert.NotNull(sameSession.Load(owner, running.World()));

        // A client started afterwards reads the imported Rooms, and drops it.
        client.Store.Keep(owner, Ranking() with { World = madeIn });
        client.Settle();
        var restarted = new Running(set, "Rooms", "Monsters");
        var later = new Client();
        Assert.Null(later.Load(owner, restarted.World()));
        Assert.Contains(later.Said(LogSeverity.Info), m => m.Contains("was dropped") && m.Contains("Rooms table has changed"));
        Assert.False(File.Exists(AreaRankingFiles.FileFor(owner)));
    }

    [Fact]
    public void OnceTheChangedFileIsReadAgainInTheSameLoad_NoRankingIsSaved_UntilTheSetIsReadInAgain()
    {
        string set = NewSetWithTables();
        var owner = new SimulatorOwner(set, One);
        var running = new Running(set, Tables);
        GameDataCache.Load load = running.Cache.ActiveLoad;
        ImportedElsewhere(set, "Rooms");

        // The idle sweep drops the table and the next lookup reads the new file: the
        // room map built at load is of the old one, the rows from here on of the new.
        running.Cache.EvictIdle(TimeSpan.Zero);
        Assert.False(running.Cache.IsTableLoaded("Rooms"));
        Assert.NotNull(running.Cache.GetTable("Rooms"));
        Assert.Equal(load, running.Cache.ActiveLoad);

        Assert.Null(running.MadeIn(out string? why));
        Assert.Equal("Rooms was read again from a file that had changed since the set was loaded", why);
        Assert.Null(running.World());
        Assert.Equal("Rooms was read again from a file that had changed since the set was loaded", running.Watch.WhyUnfit);

        // A ranking made now carries no world, and one with none is never written.
        var client = new Client();
        client.Store.Keep(owner, Ranking() with { World = running.MadeIn() });
        client.Settle();
        Assert.False(File.Exists(AreaRankingFiles.FileFor(owner)));
        Assert.NotNull(client.Store.Get(owner));

        // Read in again, it is one version once more, and that is the imported one.
        running.Cache.ReloadActiveSet();
        Assert.NotEqual(load, running.Cache.ActiveLoad);
        foreach (string table in Tables) running.Cache.GetTable(table);
        RankingWorld reloaded = Assert.IsType<RankingWorld>(running.MadeIn());
        Assert.Equal(StampOf(TableFile(set, "Rooms")), reloaded.Tables["Rooms"]);
        Assert.Null(running.Watch.WhyUnfit);
    }

    [Fact]
    public void ATableThatWasNeverRead_IsReadToBeStamped_AndOneThatWillNotReadStopsTheSave()
    {
        string set = NewSetWithTables();
        var running = new Running(set, "Rooms");

        // Not read yet and readable: read now, and stamped by that read.
        Assert.Null(running.Cache.FileStampAsRead("Items"));
        RankingWorld madeIn = Assert.IsType<RankingWorld>(running.MadeIn());
        Assert.Equal(StampOf(TableFile(set, "Items")), madeIn.Tables["Items"]);
        Assert.NotNull(running.Cache.FileStampAsRead("Items"));

        // Not read yet and not a table: no stamp is taken off the disk for it.
        string other = NewSetWithTables("broken");
        File.WriteAllText(TableFile(other, "Items"), "[{\"Name\":\"Go" + (char)0x1E + "blin\"}]");
        var broken = new Running(other, "Rooms");
        Assert.Null(broken.MadeIn(out string? why));
        Assert.Equal("Items could not be read", why);
        // To compare a saved ranking with, nothing is read and the file stands in.
        Assert.NotNull(broken.World());
    }

    [Fact]
    public void ATableReadAndThenNotReadableAgain_LeavesTheLoadUnfit()
    {
        string set = NewSetWithTables();
        var running = new Running(set, Tables);
        Assert.NotNull(running.MadeIn());

        // Dropped, its file replaced by one that will not parse, and asked for again.
        running.Cache.EvictIdle(TimeSpan.Zero);
        File.WriteAllText(TableFile(set, "Spells"), "[{\"Name\":\"Go" + (char)0x1E + "blin\"}]");
        Assert.Null(running.Cache.GetTable("Spells"));
        Assert.Null(running.Cache.FileStampAsRead("Spells"));

        Assert.Null(running.MadeIn(out string? why));
        Assert.Equal("Spells could not be read again (the file is gone or would not read)", why);
        Assert.Null(running.World());

        // A table whose file goes missing after it was read is the same.
        var gone = new Running(NewSetWithTables("gone"), Tables);
        gone.Cache.EvictIdle(TimeSpan.Zero);
        File.Delete(TableFile(gone.Cache.ActiveSet!, "Races"));
        Assert.Null(gone.Cache.GetTable("Races"));
        Assert.Null(gone.MadeIn(out why));
        Assert.StartsWith("Races could not be read again", why);
    }
}
