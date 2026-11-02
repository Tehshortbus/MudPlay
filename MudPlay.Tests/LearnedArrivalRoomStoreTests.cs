using System.IO;
using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The readings the client has made of where the dead of a room arrive ("room died
// in → room arrived in"), kept in one file per game-data set and shared by every
// client on that set.
public sealed class LearnedArrivalRoomStoreTests : IDisposable
{
    private static readonly RoomKey TerraIsta = new(14, 1813);
    private static readonly RoomKey IstaGraveyard = new(14, 1900);
    private static readonly RoomKey Elsewhere = new(14, 1950);

    private readonly string _root;
    private readonly List<LogEntry> _log = new();
    private readonly LogService _logService = new();

    public LearnedArrivalRoomStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-learnedarrivals-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        _logService.EntryAdded += _log.Add;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
    }

    private string FileFor(string set) => Path.Combine(_root, set, "learned-arrival-rooms.json");

    private int Warnings => _log.Count(e => e.Severity == LogSeverity.Warn);

    // A client on a set: its own store over the shared folder.
    private LearnedArrivalRoomStore Client(string set = "paradigm")
    {
        LearnedArrivalRoomStore store = new(_logService, FileFor);
        store.OnActiveSetChanged(set);
        return store;
    }

    [Fact]
    public void AReading_IsLearned_SaidInTheLog_AndKeptWithTheSet()
    {
        LearnedArrivalRoomStore store = Client();
        Assert.Null(store.ArrivalsFor(TerraIsta));

        Assert.Equal(LearnedArrivalOutcome.Learned, store.Learn(TerraIsta, IstaGraveyard));

        Assert.Equal(new[] { IstaGraveyard }, store.ArrivalsFor(TerraIsta));
        Assert.Equal((1, 0), (store.Count, store.DisputedCount));
        Assert.Equal(new[] { (TerraIsta, IstaGraveyard) }, store.Known);
        Assert.Contains(_log, e => e.Severity == LogSeverity.Info
            && e.Message == "learned: the dead of 14/1813 arrive in 14/1900");
        // The file carries its format, so a later one can be told from it.
        Assert.Contains("\"Version\": 1", File.ReadAllText(FileFor("paradigm")));
        // Seen again: nothing new.
        Assert.Equal(LearnedArrivalOutcome.Known, store.Learn(TerraIsta, IstaGraveyard));
        Assert.Equal(1, store.Count);
    }

    // The file is the set's: a client started later reads what an earlier one
    // learned, and another set knows nothing of it.
    [Fact]
    public void WhatWasLearned_IsReloadedForItsSet_AndASetSwitchUsesTheOtherSetsReadings()
    {
        Client().Learn(TerraIsta, IstaGraveyard);

        LearnedArrivalRoomStore later = Client();
        Assert.Equal(new[] { IstaGraveyard }, later.ArrivalsFor(TerraIsta));

        later.OnActiveSetChanged("an edited board");
        Assert.Null(later.ArrivalsFor(TerraIsta));
        Assert.Equal(0, later.Count);
        later.Learn(TerraIsta, Elsewhere);
        Assert.Equal(new[] { Elsewhere }, later.ArrivalsFor(TerraIsta));

        later.OnActiveSetChanged("paradigm");
        Assert.Equal(new[] { IstaGraveyard }, later.ArrivalsFor(TerraIsta));

        // No set: nothing is held, and nothing can be kept.
        later.OnActiveSetChanged(null);
        Assert.Null(later.ArrivalsFor(TerraIsta));
        Assert.Equal(LearnedArrivalOutcome.NotKept, later.Learn(TerraIsta, IstaGraveyard));
    }

    // Two clients on one set: what one learns the other takes in on its next
    // heartbeat, and a write of its own merges with it.
    [Fact]
    public void ASecondClientsWrite_IsTakenIn_AndAWriteMergesWithIt()
    {
        LearnedArrivalRoomStore one = Client(), two = Client();
        Assert.False(one.TakeInOutsideChanges());

        two.Learn(TerraIsta, IstaGraveyard);

        Assert.Null(one.ArrivalsFor(TerraIsta));
        Assert.True(one.TakeInOutsideChanges());
        Assert.Equal(new[] { IstaGraveyard }, one.ArrivalsFor(TerraIsta));
        Assert.False(one.TakeInOutsideChanges());

        // Each learns another room without having looked: neither loses the other's.
        RoomKey fuego = new(14, 1812), talkiran = new(14, 15000);
        one.Learn(fuego, new RoomKey(14, 1850));
        two.Learn(talkiran, new RoomKey(14, 15010));
        one.TakeInOutsideChanges();
        Assert.Equal(3, one.Count);
        Assert.Equal(3, two.Count);
        Assert.Equal(3, Client().Count);
    }

    // A room has one assigned room for its dead, so two readings mean one was
    // misread. Both are kept and neither is used, until one is seen a second
    // time: that one stands and the other is dropped. No hand edit is needed.
    [Fact]
    public void AContradictingReading_DisputesTheRoom_UntilOneIsSeenASecondTime()
    {
        LearnedArrivalRoomStore store = Client();
        store.Learn(TerraIsta, IstaGraveyard);

        Assert.Equal(LearnedArrivalOutcome.Disputed, store.Learn(TerraIsta, Elsewhere));

        Assert.Null(store.ArrivalsFor(TerraIsta));
        Assert.Equal((0, 1), (store.Count, store.DisputedCount));
        (RoomKey diedIn, IReadOnlyList<RoomKey> arrivals) = Assert.Single(store.Disputed);
        Assert.Equal(TerraIsta, diedIn);
        Assert.Equal(new[] { IstaGraveyard, Elsewhere }, arrivals);
        LogEntry warning = Assert.Single(_log, e => e.Severity == LogSeverity.Warn);
        Assert.Contains("14/1900", warning.Message);
        Assert.Contains("14/1950", warning.Message);
        Assert.DoesNotContain("Remove", warning.Message);
        // It is disputed for a client that starts later too.
        Assert.Equal((0, 1), (Client().Count, Client().DisputedCount));

        // The second reading is seen again: it stands, and the first is dropped.
        Assert.Equal(LearnedArrivalOutcome.Settled, store.Learn(TerraIsta, Elsewhere));

        Assert.Equal(new[] { Elsewhere }, store.ArrivalsFor(TerraIsta));
        Assert.Equal((1, 0), (store.Count, store.DisputedCount));
        Assert.Contains(_log, e => e.Severity == LogSeverity.Info && e.Message.StartsWith("settled: the dead of 14/1813 arrive in 14/1950"));
        Assert.Equal(1, Warnings);
        Assert.Equal(new[] { Elsewhere }, Client().ArrivalsFor(TerraIsta));
    }

    // A third reading while disputed is kept with the others, and said no more.
    [Fact]
    public void AThirdReadingWhileDisputed_IsKept_AndNotSaidAgain()
    {
        LearnedArrivalRoomStore store = Client();
        store.Learn(TerraIsta, IstaGraveyard);
        store.Learn(TerraIsta, Elsewhere);

        Assert.Equal(LearnedArrivalOutcome.Disputed, store.Learn(TerraIsta, new RoomKey(14, 1960)));

        Assert.Equal(3, Assert.Single(store.Disputed).Arrivals.Count);
        Assert.Equal(1, Warnings);
        // The first is seen again and stands; both others go.
        Assert.Equal(LearnedArrivalOutcome.Settled, store.Learn(TerraIsta, IstaGraveyard));
        Assert.Equal(new[] { IstaGraveyard }, store.ArrivalsFor(TerraIsta));
    }

    // A reading already seen twice stays in use against a single sighting of
    // another room. That sighting is kept beside it, and said.
    [Fact]
    public void AReadingSeenTwice_StaysInUseAgainstOneStraySighting()
    {
        LearnedArrivalRoomStore store = Client();
        store.Learn(TerraIsta, IstaGraveyard);
        store.Learn(TerraIsta, IstaGraveyard);

        Assert.Equal(LearnedArrivalOutcome.Challenged, store.Learn(TerraIsta, Elsewhere));

        Assert.Equal(new[] { IstaGraveyard }, store.ArrivalsFor(TerraIsta));
        Assert.Equal((1, 0), (store.Count, store.DisputedCount));
        Assert.Equal(new[] { (TerraIsta, IstaGraveyard) }, store.Known);
        LogEntry warning = Assert.Single(_log, e => e.Severity == LogSeverity.Warn);
        Assert.Contains("14/1950", warning.Message);
        Assert.Contains("2 times", warning.Message);
        Assert.Equal(new[] { "14/1813 → 14/1900 (2), against 14/1950 (1)" }, store.Challenged);
        // The same for a client that starts later: the one in use is first on file.
        Assert.Equal(new[] { IstaGraveyard }, Client().ArrivalsFor(TerraIsta));
        // And the reading in use seen again is nothing new.
        Assert.Equal(LearnedArrivalOutcome.Known, store.Learn(TerraIsta, IstaGraveyard));
    }

    // No reading is fixed for good. Two sightings of one room and then ten of
    // another (the first two a misreading made twice, or a graveyard the board has
    // since moved): the second takes over when it has been seen as often.
    [Fact]
    public void TwoSightingsThenTenOfAnother_TheOtherTakesOverAtTwo()
    {
        LearnedArrivalRoomStore store = Client();
        store.Learn(TerraIsta, IstaGraveyard);
        store.Learn(TerraIsta, IstaGraveyard);

        Assert.Equal(LearnedArrivalOutcome.Challenged, store.Learn(TerraIsta, Elsewhere));
        Assert.Equal(new[] { IstaGraveyard }, store.ArrivalsFor(TerraIsta));

        Assert.Equal(LearnedArrivalOutcome.TookOver, store.Learn(TerraIsta, Elsewhere));
        Assert.Equal(new[] { Elsewhere }, store.ArrivalsFor(TerraIsta));
        LogEntry change = Assert.Single(_log, e => e.Message.StartsWith("changed:"));
        Assert.Equal(LogSeverity.Info, change.Severity);
        Assert.Equal("changed: the dead of 14/1813 arrive in 14/1950, now seen 2 times, as often as 14/1900 (2 times), "
            + "which was in use until now and is kept beside it", change.Message);

        for (int i = 0; i < 8; i++)
            Assert.Equal(LearnedArrivalOutcome.Known, store.Learn(TerraIsta, Elsewhere));
        Assert.Equal(new[] { Elsewhere }, store.ArrivalsFor(TerraIsta));
        Assert.Equal(new[] { "14/1813 → 14/1950 (10), against 14/1900 (2)" }, store.Challenged);
        Assert.Equal((1, 0), (store.Count, store.DisputedCount));
        // A client that starts later reads the same.
        Assert.Equal(new[] { Elsewhere }, Client().ArrivalsFor(TerraIsta));
        Assert.Equal(new[] { "14/1813 → 14/1950 (10), against 14/1900 (2)" }, Client().Challenged);
    }

    // Sightings that keep alternating: the room is disputed at one each, the
    // first seen twice stands, and from then on the other only takes over by
    // catching up, which the one in use being seen again keeps it from.
    [Fact]
    public void AlternatingSightings_TheReadingInUseChangesOnlyWhenTheOtherCatchesUp()
    {
        LearnedArrivalRoomStore store = Client();
        Assert.Equal(LearnedArrivalOutcome.Learned, store.Learn(TerraIsta, IstaGraveyard));
        Assert.Equal(LearnedArrivalOutcome.Disputed, store.Learn(TerraIsta, Elsewhere));
        Assert.Null(store.ArrivalsFor(TerraIsta));
        Assert.Equal(LearnedArrivalOutcome.Settled, store.Learn(TerraIsta, IstaGraveyard));

        // 2 against 1, 3 against 2, 4 against 3: never level, so never changed.
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(LearnedArrivalOutcome.Challenged, store.Learn(TerraIsta, Elsewhere));
            Assert.Equal(new[] { IstaGraveyard }, store.ArrivalsFor(TerraIsta));
            Assert.Equal(LearnedArrivalOutcome.Known, store.Learn(TerraIsta, IstaGraveyard));
        }
        Assert.Equal(new[] { "14/1813 → 14/1900 (5), against 14/1950 (3)" }, store.Challenged);

        // The other seen twice running draws level and takes over; the first
        // seen once more draws level again and takes it back.
        Assert.Equal(LearnedArrivalOutcome.Challenged, store.Learn(TerraIsta, Elsewhere));
        Assert.Equal(LearnedArrivalOutcome.TookOver, store.Learn(TerraIsta, Elsewhere));
        Assert.Equal(new[] { Elsewhere }, store.ArrivalsFor(TerraIsta));
        Assert.Equal(LearnedArrivalOutcome.Known, store.Learn(TerraIsta, Elsewhere));
        Assert.Equal(LearnedArrivalOutcome.TookOver, store.Learn(TerraIsta, IstaGraveyard));
        Assert.Equal(new[] { IstaGraveyard }, store.ArrivalsFor(TerraIsta));
        Assert.Equal(0, store.DisputedCount);
    }

    // A file put together by hand, with the reading seen most not first: the one
    // seen most is taken for the one in use.
    [Fact]
    public void AFileWithTheMostSeenReadingNotFirst_UsesTheMostSeen()
    {
        Directory.CreateDirectory(Path.Combine(_root, "paradigm"));
        File.WriteAllText(FileFor("paradigm"), """
            { "Version": 1, "Rooms": [ { "DiedIn": "14/1813", "Seen": [
                { "Arrival": "14/1900", "Times": 1 }, { "Arrival": "14/1950", "Times": 5 } ] } ] }
            """);

        LearnedArrivalRoomStore store = Client();

        Assert.Equal(new[] { Elsewhere }, store.ArrivalsFor(TerraIsta));
        Assert.Equal(0, store.DisputedCount);
    }

    // A death in another room of the land that ends where this room's dead do is
    // counted on this room's reading, and makes no entry of its own.
    [Fact]
    public void ADeathNearbyThatAgrees_IsCountedOnTheReadingItAgreesWith()
    {
        LearnedArrivalRoomStore store = Client();
        RoomKey path = new(14, 1814);
        store.Learn(TerraIsta, IstaGraveyard);
        int before = store.Revision;

        Assert.True(store.LearnNearby(TerraIsta, path, IstaGraveyard));
        Assert.True(store.LearnNearby(TerraIsta, path, IstaGraveyard));

        Assert.Equal(2, store.SeenNearby(TerraIsta));
        Assert.False(store.HasReadingOf(path));
        Assert.Equal(1, store.Count);
        Assert.True(store.Revision > before);
        Assert.Equal(2, Client().SeenNearby(TerraIsta));
        // Not what that room's dead do, or a room with nothing in use: not counted.
        Assert.False(store.LearnNearby(TerraIsta, path, Elsewhere));
        Assert.False(store.LearnNearby(path, TerraIsta, IstaGraveyard));
        Assert.Equal(2, store.SeenNearby(TerraIsta));
    }

    // Two clients that each saw it differently, neither having read the other's
    // yet: the second write finds the first on file and the room is disputed.
    [Fact]
    public void TwoClientsThatSawItDifferently_EndDisputed_NotWithTheLastWriteWinning()
    {
        LearnedArrivalRoomStore one = Client(), two = Client();
        one.Learn(TerraIsta, IstaGraveyard);

        Assert.Equal(LearnedArrivalOutcome.Disputed, two.Learn(TerraIsta, Elsewhere));

        one.TakeInOutsideChanges();
        Assert.Null(one.ArrivalsFor(TerraIsta));
        Assert.Equal(1, one.DisputedCount);
        // And each seeing its own again settles it for both, by the count on file.
        Assert.Equal(LearnedArrivalOutcome.Settled, one.Learn(TerraIsta, IstaGraveyard));
        two.TakeInOutsideChanges();
        Assert.Equal(new[] { IstaGraveyard }, two.ArrivalsFor(TerraIsta));
    }

    // A room that names no graveyard sends its dead to one of the engine's two
    // default rooms, by the character's evil points. Seen in either, it stands for
    // both; seen in each, it is one reading and no dispute.
    [Fact]
    public void TheTwoDefaultRooms_AreOneReading()
    {
        LearnedArrivalRoomStore store = Client();
        RoomKey room = new(13, 4000);

        store.Learn(room, HangupDeathArrivalRooms.EvilTomb);
        Assert.Equal(new[] { HangupDeathArrivalRooms.Temple, HangupDeathArrivalRooms.EvilTomb }, store.ArrivalsFor(room));

        Assert.Equal(LearnedArrivalOutcome.Known, store.Learn(room, HangupDeathArrivalRooms.Temple));
        Assert.Equal((1, 0), (store.Count, store.DisputedCount));
        Assert.Equal(0, Warnings);
        // Seen twice by now: a single other room changes nothing in use.
        Assert.Equal(LearnedArrivalOutcome.Challenged, store.Learn(room, Elsewhere));
        Assert.Equal(new[] { HangupDeathArrivalRooms.Temple, HangupDeathArrivalRooms.EvilTomb }, store.ArrivalsFor(room));
    }

    // ----- A file that can't be read or written ------------------------------

    // A file that doesn't parse (one comma lost in a hand edit) may hold every
    // room learned so far. It is never written over: said once, left as it is,
    // and set aside under another name before anything new is written.
    [Fact]
    public void AFileThatDoesNotParse_IsSetAside_NeverWrittenOver()
    {
        Client().Learn(TerraIsta, IstaGraveyard);
        string good = File.ReadAllText(FileFor("paradigm"));
        string broken = good.Replace("\"Version\": 1,", "\"Version\": 1");
        File.WriteAllText(FileFor("paradigm"), broken);

        LearnedArrivalRoomStore store = Client();
        Assert.Equal(0, store.Count);
        Assert.Equal(1, Warnings);
        // Heartbeat after heartbeat: still unreadable, and said no more.
        Assert.False(store.TakeInOutsideChanges());
        Assert.False(store.TakeInOutsideChanges());
        Assert.Equal(1, Warnings);
        Assert.Equal(broken, File.ReadAllText(FileFor("paradigm")));

        store.Learn(new RoomKey(14, 1812), new RoomKey(14, 1850));

        // The broken file is beside the new one, whole.
        Assert.Equal(broken, File.ReadAllText(FileFor("paradigm") + ".bad"));
        Assert.Equal(1, Client().Count);
        Assert.Equal(2, Warnings);
        Assert.Contains(_log, e => e.Severity == LogSeverity.Warn && e.Message.Contains(".bad"));

        // A second broken file doesn't take the first one's place.
        File.WriteAllText(FileFor("paradigm"), "{ not json");
        LearnedArrivalRoomStore again = Client();
        again.Learn(TerraIsta, Elsewhere);
        Assert.Equal(broken, File.ReadAllText(FileFor("paradigm") + ".bad"));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "paradigm"), "*.bad").Length);
    }

    // What is held is kept when the file goes bad under a running client.
    [Fact]
    public void AFileThatGoesBadUnderARunningClient_LeavesWhatIsHeld()
    {
        LearnedArrivalRoomStore store = Client();
        store.Learn(TerraIsta, IstaGraveyard);

        File.WriteAllText(FileFor("paradigm"), "{ \"Version\": 1, \"Rooms\": [ { \"DiedIn\": \"14/18");

        Assert.False(store.TakeInOutsideChanges());
        Assert.Equal(new[] { IstaGraveyard }, store.ArrivalsFor(TerraIsta));
    }

    // A later client's format is neither read nor written over.
    [Fact]
    public void AFileOfALaterFormat_IsLeftAlone()
    {
        Directory.CreateDirectory(Path.Combine(_root, "paradigm"));
        const string later = "{ \"Version\": 2, \"Rooms\": [], \"Lands\": [] }";
        File.WriteAllText(FileFor("paradigm"), later);

        LearnedArrivalRoomStore store = Client();
        store.Learn(TerraIsta, IstaGraveyard);

        Assert.Equal(later, File.ReadAllText(FileFor("paradigm")));
        // Kept for the session all the same.
        Assert.Equal(new[] { IstaGraveyard }, store.ArrivalsFor(TerraIsta));
        Assert.Equal(1, Warnings);
    }

    // The set's folder can't be written: learning a room throws nothing, says so
    // once, keeps the room for the session, and writes it when the folder can be
    // written again.
    [Fact]
    public void AFolderThatCantBeWritten_ThrowsNothing_AndIsWrittenLater()
    {
        // A file standing where the set's folder should be: no file can be made in it.
        string blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, "not a folder");
        LearnedArrivalRoomStore store = Client("blocked");

        Assert.Equal(LearnedArrivalOutcome.Learned, store.Learn(TerraIsta, IstaGraveyard));
        Assert.Equal(LearnedArrivalOutcome.Learned, store.Learn(new RoomKey(14, 1812), new RoomKey(14, 1850)));

        Assert.Equal(2, store.Count);
        Assert.Equal(1, Warnings);
        store.TakeInOutsideChanges();
        Assert.Equal(1, Warnings);

        // The obstacle goes: the next heartbeat writes both.
        File.Delete(blocked);
        store.TakeInOutsideChanges();
        Assert.Equal(2, Client("blocked").Count);
    }

    // A file that can't be read and can't be moved aside either: each is said
    // once, however many heartbeats come and go.
    [Fact]
    public void AFileThatCanBeNeitherReadNorSetAside_IsSaidOnce_NotOnEveryHeartbeat()
    {
        // A file that doesn't parse, and a folder standing under the name it would
        // be moved aside to.
        string path = FileFor("paradigm");
        Directory.CreateDirectory(path + ".bad");
        File.WriteAllText(path, "{ not json");
        LearnedArrivalRoomStore store = Client();
        Assert.Equal(1, Warnings);

        store.Learn(TerraIsta, IstaGraveyard);
        Assert.Equal(2, Warnings);
        for (int i = 0; i < 10; i++) store.TakeInOutsideChanges();

        Assert.Equal(2, Warnings);
        Assert.Equal("{ not json", File.ReadAllText(path));
        Assert.Equal(new[] { IstaGraveyard }, store.ArrivalsFor(TerraIsta));

        // The folder goes: the broken file is set aside and the room written.
        Directory.Delete(path + ".bad");
        store.TakeInOutsideChanges();
        Assert.Equal("{ not json", File.ReadAllText(path + ".bad"));
        Assert.Equal(new[] { IstaGraveyard }, Client().ArrivalsFor(TerraIsta));
    }

    // Another program holds the file: it may be whole. It is not set aside, and
    // nothing is written over it; what is learned meanwhile is held. Once it
    // opens, what it holds and what was learned are both on file.
    [Fact]
    public void AFileThatCantBeOpened_IsNotDamaged_AndIsNotWrittenUntilItHasBeenRead()
    {
        Client().Learn(TerraIsta, IstaGraveyard);
        string path = FileFor("paradigm");
        byte[] whole = File.ReadAllBytes(path);
        RoomKey fuego = new(14, 1812);

        LearnedArrivalRoomStore store;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            store = Client();
            Assert.Equal(0, store.Count);
            store.Learn(fuego, new RoomKey(14, 1850));
            for (int i = 0; i < 5; i++) Assert.False(store.TakeInOutsideChanges());
            Assert.Equal(new[] { new RoomKey(14, 1850) }, store.ArrivalsFor(fuego));
            Assert.Equal(1, Warnings);
            Assert.Contains(_log, e => e.Severity == LogSeverity.Warn && e.Message.Contains("can't be opened"));
        }
        Assert.Equal(whole, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "paradigm"), "*.bad"));

        Assert.True(store.TakeInOutsideChanges());
        Assert.Equal(2, store.Count);
        Assert.Contains(_log, e => e.Message.Contains("could be opened again"));
        LearnedArrivalRoomStore fresh = Client();
        Assert.Equal(new[] { IstaGraveyard }, fresh.ArrivalsFor(TerraIsta));
        Assert.Equal(new[] { new RoomKey(14, 1850) }, fresh.ArrivalsFor(fuego));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "paradigm"), "*.bad"));
    }

    // Two clients learning at the same instant, over and over: each write is made
    // under the hold, over what the other has just written, so nothing is lost.
    [Fact]
    public async Task TwoClientsWritingAtOnce_LoseNothing()
    {
        const int each = 40;
        LearnedArrivalRoomStore one = new(null, FileFor), two = new(null, FileFor);
        one.OnActiveSetChanged("paradigm");
        two.OnActiveSetChanged("paradigm");
        using Barrier start = new(2);
        void Run(LearnedArrivalRoomStore store, int map)
        {
            start.SignalAndWait();
            for (int i = 1; i <= each; i++) store.Learn(new RoomKey(map, i), new RoomKey(14, 1900));
        }
        Task first = Task.Run(() => Run(one, 1)), second = Task.Run(() => Run(two, 2));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(60));
        // Anything a hold kept from being written goes on the next heartbeat.
        one.TakeInOutsideChanges();
        two.TakeInOutsideChanges();

        Assert.Equal(2 * each, Client().Count);
    }

    // A write that never finished left its temporary file: removed when the set
    // is taken up, unless a client is writing the file at that moment.
    [Fact]
    public void ADeadWritesTemporaryFile_IsRemovedAtLoad_AndOneUnderAWritersHoldIsLeft()
    {
        string path = FileFor("paradigm");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string dead = $"{path}.{int.MaxValue}.tmp";
        File.WriteAllText(dead, "{");

        using (SharedFileWrite.Hold(path, out _)) Client();
        Assert.True(File.Exists(dead));

        Client();
        Assert.False(File.Exists(dead));
        Assert.Contains(_log, e => e.Severity == LogSeverity.Info && e.Message.Contains("left by a write that never finished"));
    }

    // A hold that timed out is not waited on again until one is had: whatever
    // keeps it may not be another client, and each wait is on the UI thread.
    [Fact]
    public void AHoldThatTimedOut_IsTriedOnceAfter_UntilItIsHadAgain()
    {
        string path = FileFor("paradigm");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (SharedFileWrite.Hold(path, out _))
        {
            System.Diagnostics.Stopwatch first = System.Diagnostics.Stopwatch.StartNew();
            Assert.ThrowsAny<IOException>(() => SharedFileWrite.Hold(path, out _));
            Assert.True(first.ElapsedMilliseconds >= 200);

            System.Diagnostics.Stopwatch again = System.Diagnostics.Stopwatch.StartNew();
            for (int beat = 0; beat < 5; beat++) Assert.ThrowsAny<IOException>(() => SharedFileWrite.Hold(path, out _));
            Assert.True(again.ElapsedMilliseconds < 200);
        }
        using (SharedFileWrite.Hold(path, out _)) { }
        // Had again: the next one that has to wait, waits.
        using (SharedFileWrite.Hold(path, out _))
        {
            System.Diagnostics.Stopwatch waited = System.Diagnostics.Stopwatch.StartNew();
            Assert.ThrowsAny<IOException>(() => SharedFileWrite.Hold(path, out _));
            Assert.True(waited.ElapsedMilliseconds >= 200);
        }
    }

    // A hand-edited file can hold a null where the rooms, a room or its readings go;
    // each is passed over like an entry naming no room, and the rest loads.
    [Fact]
    public void NullsInAFilePutTogetherByHand_ArePassedOver()
    {
        Directory.CreateDirectory(Path.Combine(_root, "paradigm"));
        File.WriteAllText(FileFor("paradigm"), """
            { "Version": 1, "Rooms": [ null, { "DiedIn": "14/1950", "Seen": null },
                { "DiedIn": "14/1813", "Seen": [ null, { "Arrival": "14/1900", "Times": 2 } ] } ] }
            """);

        LearnedArrivalRoomStore store = Client();

        Assert.Equal(new[] { IstaGraveyard }, store.ArrivalsFor(TerraIsta));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void ANullRoomList_LoadsAsNone()
    {
        Directory.CreateDirectory(Path.Combine(_root, "paradigm"));
        File.WriteAllText(FileFor("paradigm"), """{ "Version": 1, "Rooms": null }""");

        LearnedArrivalRoomStore store = Client();

        Assert.Equal(0, store.Count);
        Assert.Equal(LearnedArrivalOutcome.Learned, store.Learn(TerraIsta, IstaGraveyard));
    }
}
