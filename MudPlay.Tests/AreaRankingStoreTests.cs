using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Simulation;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The Simulator's finished rankings, kept by the game data and character each was
// made on: a pairing gets its own back and no other's, names match without case, a
// set read in again keeps the rankings whose tables it still has, and only the last
// few made or shown are held.
public sealed class AreaRankingStoreTests : IDisposable
{
    // Set and BBS names carry the test's own tag: the store keeps every ranking in
    // a file under its set, and a rename by character walks every set under the
    // data root, other test classes' included.
    private readonly string _tag = Guid.NewGuid().ToString("N")[..10];

    private string S(string set) => $"{set}-{_tag}";

    private string C(string character)
    {
        int slash = character.IndexOf('/');
        return $"{character[..slash]}-{_tag}{character[slash..]}";
    }

    // The store as the client runs it, with its file work done in turn on the
    // calling thread and what a read finds handed straight back.
    private static AreaRankingStore NewStore() =>
        new(new AreaRankingFiles(), act => act(), runFileWork: work => work());

    public void Dispose()
    {
        if (!Directory.Exists(AppPaths.GameDataRoot)) return;
        foreach (string dir in Directory.GetDirectories(AppPaths.GameDataRoot, $"*-{_tag}"))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    private static AreaRanking Ranking(string area) => new(
        new[]
        {
            new AreaRank(area, 40, new[] { new RoomKey(1, 1), new RoomKey(1, 2) }, 2,
                new LoopSimSummary(Array.Empty<LoopSimRun>())),
        },
        40, 1.0, 1.0, 3, new RoomKey(1, 1), "Stock", area, DateTimeOffset.Now, 40);

    [Fact]
    public void APairingGetsItsOwnRankingBackAndNoOthers()
    {
        AreaRankingStore store = NewStore();
        AreaRanking hills = Ranking("Hills"), marsh = Ranking("Marsh"), fen = Ranking("Fen");
        store.Keep(new SimulatorOwner(S("alpha"), C("board/one")), hills);
        store.Keep(new SimulatorOwner(S("beta"), C("board/one")), marsh);
        store.Keep(new SimulatorOwner(S("alpha"), C("board/two")), fen);

        Assert.Same(hills, store.Get(new SimulatorOwner(S("ALPHA"), C("Board/One"))));
        Assert.Same(marsh, store.Get(new SimulatorOwner(S("beta"), C("board/one"))));
        Assert.Same(fen, store.Get(new SimulatorOwner(S("alpha"), C("board/two"))));
        Assert.Null(store.Get(new SimulatorOwner(S("beta"), C("board/two"))));
        Assert.Null(store.Get(new SimulatorOwner(S("alpha"), null)));
        Assert.Null(store.Get(new SimulatorOwner(null, C("board/one"))));
    }

    [Fact]
    public void RankingAgainReplacesThePairingsRanking()
    {
        AreaRankingStore store = NewStore();
        var owner = new SimulatorOwner(S("alpha"), C("board/one"));
        AreaRanking second = Ranking("Marsh");
        store.Keep(owner, Ranking("Hills"));
        store.Keep(owner, second);

        Assert.Equal(1, store.Count);
        Assert.Same(second, store.Get(owner));

        store.Forget(owner);
        Assert.Null(store.Get(owner));
    }

    private static readonly RankingWorld Tables = new(
        1, RankingWorld.TableNames.ToDictionary(t => t, _ => "10:1"), "seed");

    private static readonly RankingWorld OtherTables = Tables with
    {
        Tables = RankingWorld.TableNames.ToDictionary(t => t, t => t == "Rooms" ? "11:2" : "10:1"),
    };

    // A switch drops nothing, and neither does the same set read in again: a
    // ranking that says which tables it was made on is kept, and handed back while
    // the load in hand has those tables.
    [Fact]
    public void NeitherASwitchOfGameDataNorTheSameSetReadInAgainDropsARankingOfTheSameTables()
    {
        AreaRankingStore store = NewStore();
        store.NoteActiveSet(S("alpha"));
        AreaRanking hills = Ranking("Hills") with { World = Tables };
        var one = new SimulatorOwner(S("alpha"), C("board/one"));
        store.Keep(one, hills);
        store.Keep(new SimulatorOwner(S("alpha"), C("board/two")), Ranking("Marsh") with { World = Tables });
        store.Keep(new SimulatorOwner(S("beta"), C("board/one")), Ranking("Fen") with { World = Tables });

        Assert.Equal(0, store.NoteActiveSet(S("beta")));
        Assert.Equal(0, store.NoteActiveSet(S("alpha")));
        Assert.Equal(0, store.NoteActiveSet(S("Alpha")));

        Assert.Equal(3, store.Count);
        Assert.Same(hills, store.Get(one, Tables));
        // Compared once with the load in hand, it is handed back for the rest of it.
        Assert.Same(hills, store.Get(one));
    }

    // Read in again, the set's tables may be other files. A ranking made on the old
    // ones is dropped when it is next asked for, with the world in hand: memory
    // hands back nothing a read from disk would refuse. Until it can be compared it
    // is neither handed back nor dropped.
    [Fact]
    public void ARankingHeldThroughAReloadIsComparedWithTheNewLoadBeforeItIsHandedBack()
    {
        AreaRankingStore store = NewStore();
        store.NoteActiveSet(S("alpha"));
        var one = new SimulatorOwner(S("alpha"), C("board/one"));
        var two = new SimulatorOwner(S("alpha"), C("board/two"));
        store.Keep(one, Ranking("Hills") with { World = Tables });
        store.Keep(two, Ranking("Marsh") with { World = Tables });

        store.NoteActiveSet(S("alpha"));

        // No world to compare with (the load can't be stamped): not shown, still held.
        Assert.Null(store.Get(one));
        Assert.Equal(2, store.Count);

        Assert.Null(store.Get(one, OtherTables));
        Assert.Equal(1, store.Count);
        Assert.Contains(store.DiskNotes, n => n.Contains("was dropped: the game data's Rooms table has changed since"));
        Assert.NotNull(store.Get(two, Tables));
    }

    // A world handed in is the active set's: it says nothing of a ranking held for
    // another set, which is handed back as it is and never dropped by it.
    [Fact]
    public void TheWorldInHandIsNotComparedWithARankingOfAnotherSet()
    {
        AreaRankingStore store = NewStore();
        store.NoteActiveSet(S("alpha"));
        var onBeta = new SimulatorOwner(S("beta"), C("board/one"));
        store.Keep(onBeta, Ranking("Fen") with { World = Tables });

        Assert.NotNull(store.Get(onBeta, OtherTables));
        Assert.Equal(1, store.Count);
    }

    // A ranking with no record of its tables can be compared with nothing, so it
    // does not outlive the load of its set it was made in: it goes when the set is
    // read in again, and it goes on a switch back to the set, whose files may have
    // been replaced meanwhile. The other set's, made in a load still in hand, stays.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARankingWithNoRecordOfItsTablesGoesAtTheNextLoadOfItsSet(bool readAgain)
    {
        AreaRankingStore store = NewStore();
        store.NoteActiveSet(S("alpha"));
        var one = new SimulatorOwner(S("alpha"), C("board/one"));
        store.Keep(one, Ranking("Hills"));
        store.Keep(new SimulatorOwner(S("alpha"), C("board/two")), Ranking("Marsh"));

        if (readAgain)
        {
            Assert.Equal(2, store.NoteActiveSet(S("Alpha")));
            Assert.Equal(0, store.Count);
            return;
        }
        // Away to beta, where one is made, and back.
        Assert.Equal(0, store.NoteActiveSet(S("beta")));
        Assert.Equal(2, store.Count);
        var onBeta = new SimulatorOwner(S("beta"), C("board/one"));
        store.Keep(onBeta, Ranking("Fen"));

        Assert.Equal(2, store.NoteActiveSet(S("alpha")));

        Assert.Null(store.Get(one));
        Assert.Null(store.Get(one, Tables));
        Assert.Equal(1, store.Count);
        Assert.NotNull(store.Get(onBeta));
    }

    // Route (a): the load of a set turned unfit under a ranking (another client
    // imported the set and a table was read again from the new file), so it was
    // made with no record of its tables; then a switch away and back. Route (b): the
    // same kind of ranking, a switch away, an import over its set from there, and
    // the switch to the set the import ends with. Either way the set's tables are
    // not the ones it was made on and nothing says so: it is not listed again. The
    // store is told the same in both, since an import tells it nothing: a switch
    // away, and a switch back.
    [Fact]
    public void ARankingWithNoRecordOfItsTables_IsNotHandedBackUnderALaterLoadOfChangedTables()
    {
        AreaRankingStore store = NewStore();
        store.NoteActiveSet(S("alpha"));
        var owner = new SimulatorOwner(S("alpha"), C("board/one"));
        store.Keep(owner, Ranking("Hills"));
        Assert.NotNull(store.Get(owner, Tables));   // listed while its own load lasts

        store.NoteActiveSet(S("beta"));
        store.NoteActiveSet(S("alpha"));

        Assert.Null(store.Get(owner, OtherTables));
        Assert.Null(store.Get(owner));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void ASetWhoseDataIsReplacedWhileAnotherIsActiveLosesItsRankings()
    {
        AreaRankingStore store = NewStore();
        store.NoteActiveSet(S("alpha"));
        store.Keep(new SimulatorOwner(S("alpha"), C("board/one")), Ranking("Hills"));
        store.Keep(new SimulatorOwner(S("alpha"), C("board/two")), Ranking("Marsh"));
        store.NoteActiveSet(S("beta"));
        store.Keep(new SimulatorOwner(S("beta"), C("board/one")), Ranking("Fen"));

        // alpha deleted, or its data replaced in Manage Sets, from beta.
        Assert.Equal(2, store.ForgetSet(S("ALPHA")));

        // Coming back to it is a plain switch, with nothing left to list.
        Assert.Equal(0, store.NoteActiveSet(S("alpha")));
        Assert.Null(store.Get(new SimulatorOwner(S("alpha"), C("board/one"))));
        Assert.Null(store.Get(new SimulatorOwner(S("alpha"), C("board/two"))));
        Assert.NotNull(store.Get(new SimulatorOwner(S("beta"), C("board/one"))));
    }

    [Fact]
    public void ARenamedCharactersRankingsGoWithItOnEverySetAndTheOldNameKeepsNone()
    {
        AreaRankingStore store = NewStore();
        AreaRanking hills = Ranking("Hills"), marsh = Ranking("Marsh");
        store.Keep(new SimulatorOwner(S("alpha"), C("board/one")), hills);
        store.Keep(new SimulatorOwner(S("beta"), C("board/one")), marsh);
        store.Keep(new SimulatorOwner(S("alpha"), C("board/two")), Ranking("Fen"));
        // Kept for a character that had the new name before: no longer that name's.
        store.Keep(new SimulatorOwner(S("alpha"), C("board/new")), Ranking("Dunes"));

        Assert.Equal(2, store.RenameCharacter(C("Board/One"), C("board/new")));

        Assert.Same(hills, store.Get(new SimulatorOwner(S("alpha"), C("board/new"))));
        Assert.Same(marsh, store.Get(new SimulatorOwner(S("beta"), C("board/new"))));
        Assert.Null(store.Get(new SimulatorOwner(S("alpha"), C("board/one"))));
        Assert.Null(store.Get(new SimulatorOwner(S("beta"), C("board/one"))));
        Assert.NotNull(store.Get(new SimulatorOwner(S("alpha"), C("board/two"))));
        Assert.Equal(3, store.Count);

        // The default profile given a name, and a name that is the same but for case.
        store.Keep(new SimulatorOwner(S("alpha"), null), Ranking("Moor"));
        Assert.Equal(1, store.RenameCharacter(null, C("board/named")));
        Assert.Null(store.Get(new SimulatorOwner(S("alpha"), null)));
        Assert.Equal(0, store.RenameCharacter(C("board/named"), C("BOARD/NAMED")));
        Assert.NotNull(store.Get(new SimulatorOwner(S("alpha"), C("board/named"))));
    }

    [Fact]
    public void OnlyTheLastFewMadeOrShownAreKept()
    {
        AreaRankingStore store = NewStore();
        List<SimulatorOwner> owners = Enumerable.Range(0, AreaRankingStore.MaxKept)
            .Select(i => new SimulatorOwner(S($"set-{i}"), C("board/one"))).ToList();
        foreach (SimulatorOwner owner in owners) Assert.Null(store.Keep(owner, Ranking("Hills")));
        // The oldest is shown again, so the next oldest is the one let go.
        Assert.NotNull(store.Get(owners[0]));

        SimulatorOwner? dropped = store.Keep(new SimulatorOwner(S("one-more"), C("board/one")), Ranking("Marsh"));

        Assert.Equal(owners[1], dropped);
        Assert.Equal(AreaRankingStore.MaxKept, store.Count);
        Assert.NotNull(store.Get(owners[0]));
        Assert.Null(store.Get(owners[1]));
        Assert.NotNull(store.Get(new SimulatorOwner(S("one-more"), C("board/one"))));
    }
}
