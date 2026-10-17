using System.IO;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The names seen for rooms the game data gives none, kept in the realm's folder
// and put back on the room map each time it is built. The imported tables are
// never written.
public sealed class LearnedRoomNameStoreTests : IDisposable
{
    private static readonly RoomKey Street = new(1, 321);
    private static readonly RoomKey House = new(15, 861);
    private static readonly RoomKey Cellar = new(15, 862);
    private static readonly RoomKey Attic = new(15, 863);
    private const string HouseName = "Shop White House Room";
    private const string CellarName = "Shop White House Cellar";
    private const string AtticName = "Shop White House Attic";

    private static readonly HashSet<Direction> HouseExits = new() { Direction.N, Direction.D, Direction.U };

    private readonly string _root;
    private readonly string _gameData;
    private readonly string _realmA;
    private readonly string _realmB;
    private readonly List<LogEntry> _log = new();
    private readonly LogService _logService = new();

    public LearnedRoomNameStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-learnedroomnames-" + Path.GetRandomFileName());
        _gameData = Path.Combine(_root, "game data");
        _realmA = Path.Combine(_root, "BBS", "board", "Realms", "one");
        _realmB = Path.Combine(_root, "BBS", "board", "Realms", "two");
        Directory.CreateDirectory(_gameData);
        _logService.EntryAdded += _log.Add;
        SeedSet("alpha");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
    }

    // Oak Street, with a house south of it, a cellar under the house and an attic
    // over it. The data names none of the three unless told to.
    private static string Rooms(string? houseName = null, string? cellarName = null) => $$"""
        [
          { "Map Number": 1, "Room Number": 321, "Name": "Oak Street",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "15/861", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 15, "Room Number": 861, "Name": {{Json(houseName)}},
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/321", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "15/863", "D": "15/862" },
          { "Map Number": 15, "Room Number": 862, "Name": {{Json(cellarName)}},
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "15/861", "D": "0" },
          { "Map Number": 15, "Room Number": 863, "Name": null,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "15/861" }
        ]
        """;

    private static string Json(string? text) => text is null ? "null" : $"\"{text}\"";

    private string RoomsFile(string set) => Path.Combine(_gameData, set, "Rooms.json");

    private void SeedSet(string set, string? houseName = null, string? cellarName = null)
    {
        Directory.CreateDirectory(Path.Combine(_gameData, set));
        File.WriteAllText(RoomsFile(set), Rooms(houseName, cellarName));
    }

    private static string NamesFile(string realm) => Path.Combine(realm, "learned-room-names.json");

    private int Warnings => _log.Count(e => e.Severity == LogSeverity.Warn);

    private int Said(string text) =>
        _log.Count(e => e.Source == LearnedRoomNameStore.LogCategory && e.Message.Contains(text, StringComparison.Ordinal));

    // One running client: its own cache, room map and store over the folders the
    // clients share, wired in the order AppServices wires them.
    private sealed class Client
    {
        public string? Realm;
        public GameDataCache Cache = null!;
        public RoomGraphManager Graph = null!;
        public LearnedRoomNameStore Store = null!;

        // The tracker learning a name: what puts it on the map for the session.
        public void See(RoomKey room, string name) => Assert.NotNull(Graph.LearnRoomName(room, name));

        // Save pressed at once: the realm and set are the ones the name was seen on.
        public LearnedRoomNameStore.KeepResult KeepHow(RoomKey room, string name) =>
            Store.Keep(room, name, Realm, Graph.ActiveSet);

        public bool Keep(RoomKey room, string name) => KeepHow(room, name) == LearnedRoomNameStore.KeepResult.Stored;

        public string NameOf(RoomKey room) => Graph.GetRoom(room)!.Name;

        public bool Unnamed(RoomKey room) => Graph.GetRoom(room)!.HasUnknownName;
    }

    private Client Start(string? realm, string? set = "alpha")
    {
        Client client = new() { Realm = realm };
        client.Cache = new GameDataCache(_gameData);
        client.Graph = new RoomGraphManager(client.Cache, _logService);
        client.Store = new LearnedRoomNameStore(client.Graph, () => client.Realm, _logService);
        client.Graph.GraphReloaded += client.Store.OnGraphReloaded;
        client.Store.OnRealmChanged();
        client.Cache.ActiveSetChanged += client.Graph.OnActiveSetChanged;
        client.Cache.SwitchSet(set);
        return client;
    }

    // ----- kept, and back on the map ---------------------------------

    [Fact]
    public void AKeptName_IsFiledInTheRealmsFolderUnderItsSet_AndIsOnTheMapOfTheNextSession()
    {
        Client first = Start(_realmA);
        first.See(House, HouseName);
        Assert.True(first.Keep(House, HouseName));
        Assert.Equal((1, 1), (first.Store.CountForActiveSet, first.Store.OnRoomMap));
        Assert.Equal(1, Said("kept: room 15/861 of 'alpha' is 'Shop White House Room'"));

        string file = File.ReadAllText(NamesFile(_realmA));
        Assert.Contains("\"Version\": 1", file);
        Assert.Contains("\"Set\": \"alpha\"", file);
        Assert.Contains("\"Room\": \"15/861\"", file);
        Assert.Contains("\"Name\": \"Shop White House Room\"", file);

        _log.Clear();
        Client next = Start(_realmA);

        Room house = next.Graph.GetRoom(House)!;
        Assert.Equal(HouseName, house.Name);
        Assert.False(house.HasUnknownName);
        // In the name indexes too: a search by the learned name finds the room.
        Assert.Equal(new[] { House }, next.Graph.FindCandidates(HouseName, HouseExits));
        Assert.Equal(new[] { House }, next.Graph.FindByName(HouseName).Select(r => r.Key));
        Assert.Equal(new[] { House }, next.Graph.FindByNameCoveringExits(HouseName, new HashSet<Direction> { Direction.N }));
        // The cellar was never named and stays so.
        Assert.True(next.Graph.GetRoom(Cellar)!.HasUnknownName);

        // Put back quietly: nothing was learned, and one line says how many.
        Assert.DoesNotContain(_log, e => e.Message.StartsWith("Learned name", StringComparison.Ordinal));
        Assert.Equal(1, Said("1 learned room name(s) put on the room map for 'alpha'"));
        Assert.Equal((1, 1), (next.Store.CountForActiveSet, next.Store.OnRoomMap));
    }

    // The tracker walks into the room in a later session: it is found by its
    // stored name like any room the data names, and nothing is learned or asked.
    [Fact]
    public void AStoredName_IsNotLearnedAgain_SoNothingIsAskedTwice()
    {
        Client first = Start(_realmA);
        RoomTracker tracker = new(first.Graph);
        NameLearnedEvent? asked = null;
        tracker.NameLearned += e => asked = e;
        tracker.SetLocated(Street);
        tracker.NoteRoomObserved(new RoomObservation(HouseName, HouseExits));
        Assert.Equal(new NameLearnedEvent(House, HouseName), asked);
        Assert.True(first.Keep(House, HouseName));

        Client next = Start(_realmA);
        RoomTracker later = new(next.Graph);
        int learned = 0;
        later.NameLearned += _ => learned++;
        later.SetLocated(Street);
        later.NoteRoomObserved(new RoomObservation(HouseName, HouseExits));

        Assert.Equal(RoomConfidence.Confirmed, later.State.Confidence);
        Assert.Equal(House, later.State.CurrentRoom!.Key);
        Assert.Equal(0, learned);
    }

    // A reload of the same set (a re-import, the Game Data menu) builds the map
    // from the data again; the names go back on it.
    [Fact]
    public void ARebuiltMap_GetsItsNamesBack()
    {
        Client client = Start(_realmA);
        client.See(House, HouseName);
        Assert.True(client.Keep(House, HouseName));

        client.Cache.ReloadActiveSet();

        Assert.Equal(HouseName, client.NameOf(House));
        Assert.Equal(new[] { House }, client.Graph.FindCandidates(HouseName, HouseExits));
    }

    // ----- the game data is not written ------------------------------

    [Fact]
    public void LearningAndKeepingAName_LeavesRoomsJsonAndItsStampAsRead_Untouched()
    {
        DateTime written = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(RoomsFile("alpha"), written);
        byte[] before = File.ReadAllBytes(RoomsFile("alpha"));
        Client client = Start(_realmA);
        GameDataCache.TableFileStamp? stamp = client.Cache.FileStampAsRead("Rooms");
        Assert.Equal(new GameDataCache.TableFileStamp(before.Length, written), stamp);
        string[] setFiles = Directory.GetFileSystemEntries(Path.Combine(_gameData, "alpha"));

        client.See(House, HouseName);
        Assert.True(client.Keep(House, HouseName));
        client.See(Cellar, CellarName);
        Assert.True(client.Keep(Cellar, CellarName));
        client.Store.TakeInOutsideChanges();

        Assert.Equal(before, File.ReadAllBytes(RoomsFile("alpha")));
        Assert.Equal(written, File.GetLastWriteTimeUtc(RoomsFile("alpha")));
        Assert.Equal(stamp, client.Cache.FileStampAsRead("Rooms"));
        // Nothing was made in the set's folder either: the names are the realm's.
        Assert.Equal(setFiles, Directory.GetFileSystemEntries(Path.Combine(_gameData, "alpha")));
        Assert.True(File.Exists(NamesFile(_realmA)));
        // And the session after reads the same Rooms.json and still has both.
        Client next = Start(_realmA);
        Assert.Equal(stamp, next.Cache.FileStampAsRead("Rooms"));
        Assert.Equal((HouseName, CellarName), (next.NameOf(House), next.NameOf(Cellar)));
    }

    // ----- sets and realms -------------------------------------------

    // Room numbers belong to a set: 15/861 of one set is not 15/861 of another.
    [Fact]
    public void TwoSetsInOneFile_KeepTheirOwnNames()
    {
        SeedSet("beta");
        Client client = Start(_realmA);
        client.See(House, "Alpha House");
        Assert.True(client.Keep(House, "Alpha House"));

        client.Cache.SwitchSet("beta");
        Assert.True(client.Graph.GetRoom(House)!.HasUnknownName);
        Assert.Equal(0, client.Store.CountForActiveSet);
        client.See(House, "Beta House");
        Assert.True(client.Keep(House, "Beta House"));

        string file = File.ReadAllText(NamesFile(_realmA));
        Assert.Contains("\"Set\": \"alpha\"", file);
        Assert.Contains("\"Set\": \"beta\"", file);

        Client next = Start(_realmA);
        Assert.Equal("Alpha House", next.NameOf(House));
        next.Cache.SwitchSet("beta");
        Assert.Equal("Beta House", next.NameOf(House));
        Assert.Equal(new[] { House }, next.Graph.FindCandidates("Beta House", HouseExits));
        Assert.Empty(next.Graph.FindCandidates("Alpha House", HouseExits));
        next.Cache.SwitchSet("alpha");
        Assert.Equal("Alpha House", next.NameOf(House));
    }

    // What a room is called is known from what one board printed. On another
    // realm the room is unnamed until it is seen there.
    [Fact]
    public void ANameKeptOnOneRealm_IsNotOnTheMapOfAnother_AndComesBackWithItsRealm()
    {
        Client client = Start(_realmA);
        client.See(House, HouseName);
        Assert.True(client.Keep(House, HouseName));
        // Learned this session and never kept (Skip, or the prompt still open).
        client.See(Cellar, CellarName);
        _log.Clear();

        client.Realm = _realmB;
        client.Store.OnRealmChanged();

        Assert.True(client.Graph.GetRoom(House)!.HasUnknownName);
        Assert.Empty(client.Graph.FindCandidates(HouseName, HouseExits));
        Assert.Empty(client.Graph.FindByName(HouseName));
        Assert.Equal((0, 0), (client.Store.CountForActiveSet, client.Store.OnRoomMap));
        // The unsaved one was that board's too: it comes off with the saved one.
        Assert.True(client.Unnamed(Cellar));
        Assert.Empty(client.Graph.FindByName(CellarName));
        Assert.False(File.Exists(NamesFile(_realmB)));

        // Seen there under another name, and kept for that realm.
        client.See(House, "Their House");
        Assert.True(client.Keep(House, "Their House"));

        client.Realm = _realmA;
        client.Store.OnRealmChanged();
        Assert.Equal(HouseName, client.NameOf(House));
        Assert.Equal(1, Said("1 learned room name(s) put on the room map for 'alpha'"));
        Assert.Equal("Their House", Start(_realmB).NameOf(House));

        // No realm: nothing held, nothing on the map.
        client.Realm = null;
        client.Store.OnRealmChanged();
        Assert.True(client.Graph.GetRoom(House)!.HasUnknownName);
        Assert.Null(client.Store.RealmFolder);
    }

    // ----- what is not kept -------------------------------------------

    [Fact]
    public void AnAnswerThatComesTooLate_OrForNothing_KeepsNothing()
    {
        Client client = Start(_realmA);
        client.See(House, HouseName);

        // No name.
        Assert.Equal(LearnedRoomNameStore.KeepResult.NotARoomName, client.KeepHow(House, ""));
        Assert.Equal(LearnedRoomNameStore.KeepResult.NotARoomName, client.KeepHow(House, "   "));
        // A room the map doesn't have, and one it has under no such name.
        Assert.Equal(LearnedRoomNameStore.KeepResult.NotOnTheMap, client.KeepHow(new RoomKey(99, 99), "Phantom Room"));
        Assert.Equal(LearnedRoomNameStore.KeepResult.NotOnTheMap, client.KeepHow(Cellar, CellarName));
        // Seen on another realm than the one played when the answer came.
        Assert.Equal(LearnedRoomNameStore.KeepResult.OtherRealm,
            client.Store.Keep(House, HouseName, seenOnRealm: _realmB, seenOnSet: "alpha"));
        // Seen under another set than the map is built from now.
        Assert.Equal(LearnedRoomNameStore.KeepResult.OtherSet,
            client.Store.Keep(House, HouseName, seenOnRealm: _realmA, seenOnSet: "beta"));
        Assert.Equal(LearnedRoomNameStore.KeepResult.OtherSet,
            client.Store.Keep(House, HouseName, seenOnRealm: _realmA, seenOnSet: null));
        // No realm to keep it in.
        client.Realm = null;
        Assert.Equal(LearnedRoomNameStore.KeepResult.NoRealmOrSet, client.KeepHow(House, HouseName));

        Assert.Equal(8, Said("is not kept: "));
        Assert.Equal(0, Said("kept: room"));
        Assert.False(File.Exists(NamesFile(_realmA)));
        Assert.False(File.Exists(NamesFile(_realmB)));
        Assert.True(Start(_realmA).Graph.GetRoom(House)!.HasUnknownName);
    }

    // The prompt stood open while the Game Data menu switched the set, and the
    // new set's data happens to give that room that very name. Save is refused:
    // the name was seen on the other set, and nothing is said to be kept.
    [Fact]
    public void ASaveThatArrivesUnderAnotherSet_IsRefused_EvenWhenThatSetsDataHasTheSameName()
    {
        SeedSet("beta", houseName: HouseName);
        Client client = Start(_realmA);
        client.See(House, HouseName);
        string seenOnSet = client.Graph.ActiveSet!;

        client.Cache.SwitchSet("beta");
        _log.Clear();
        Assert.Equal(LearnedRoomNameStore.KeepResult.OtherSet,
            client.Store.Keep(House, HouseName, seenOnRealm: _realmA, seenOnSet: seenOnSet));

        Assert.Equal(0, Said("kept: room"));
        Assert.Equal(0, Said("dropped"));
        Assert.Equal(1, Said("is not kept: the game data set has changed since it was seen"));
        Assert.False(File.Exists(NamesFile(_realmA)));
        client.Cache.SwitchSet("alpha");
        Assert.True(client.Unnamed(House));
    }

    // Every outcome but "stored" has a few words for the user who pressed Save.
    [Fact]
    public void EveryOutcomeButStored_HasWordsForTheUser()
    {
        foreach (LearnedRoomNameStore.KeepResult result in Enum.GetValues<LearnedRoomNameStore.KeepResult>())
        {
            string? why = LearnedRoomNameStore.WhyNotStored(result);
            if (result == LearnedRoomNameStore.KeepResult.Stored) Assert.Null(why);
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(why));
                // It goes into a terminal status line, written as Latin-1.
                Assert.All(why!, c => Assert.InRange(c, ' ', '~'));
            }
        }
        Assert.Equal(Enum.GetValues<LearnedRoomNameStore.KeepResult>().Length - 1,
            Enum.GetValues<LearnedRoomNameStore.KeepResult>()
                .Select(LearnedRoomNameStore.WhyNotStored).Where(w => w is not null).Distinct().Count());
    }

    // ----- the data comes to name the room ----------------------------

    [Fact]
    public void ARoomTheDataNowNames_KeepsTheDatasName_AndTheStoredOneIsDropped()
    {
        Client first = Start(_realmA);
        first.See(House, HouseName);
        Assert.True(first.Keep(House, HouseName));
        first.See(Cellar, CellarName);
        Assert.True(first.Keep(Cellar, CellarName));

        // A later import carries the house's name.
        SeedSet("alpha", houseName: "White House, Shop Floor");
        _log.Clear();
        Client next = Start(_realmA);

        Assert.Equal("White House, Shop Floor", next.NameOf(House));
        Assert.Empty(next.Graph.FindCandidates(HouseName, HouseExits));
        Assert.Equal(CellarName, next.NameOf(Cellar));
        Assert.Equal((1, 1), (next.Store.CountForActiveSet, next.Store.OnRoomMap));
        Assert.Equal(1, Said("dropped 1 stored name(s) for 'alpha'"));
        Assert.Equal(1, Said("15/861"));
        string file = File.ReadAllText(NamesFile(_realmA));
        Assert.DoesNotContain("15/861", file);
        Assert.Contains("15/862", file);

        // Dropped for good: it is not said, or looked for, again.
        _log.Clear();
        Assert.Equal(CellarName, Start(_realmA).NameOf(Cellar));
        Assert.Equal(0, Said("dropped"));
    }

    // ----- several clients on one realm --------------------------------

    [Fact]
    public void AnotherClientsName_IsTakenIn_AndAWriteIsLaidOverWhatIsOnFile()
    {
        Client one = Start(_realmA), two = Start(_realmA);
        Assert.False(one.Store.TakeInOutsideChanges());

        two.See(House, HouseName);
        Assert.True(two.Keep(House, HouseName));

        Assert.True(one.Graph.GetRoom(House)!.HasUnknownName);
        Assert.True(one.Store.TakeInOutsideChanges());
        Assert.Equal(HouseName, one.NameOf(House));
        Assert.Equal(new[] { House }, one.Graph.FindCandidates(HouseName, HouseExits));
        Assert.False(one.Store.TakeInOutsideChanges());

        // The second has not looked since the first wrote the cellar. Its own
        // write keeps the cellar, and puts the cellar's name on its map as well.
        // (The file's time is moved on by hand: two writes in one test can land
        // within the clock's step.)
        one.See(Cellar, CellarName);
        Assert.True(one.Keep(Cellar, CellarName));
        File.SetLastWriteTimeUtc(NamesFile(_realmA), DateTime.UtcNow.AddSeconds(5));
        Assert.True(two.Graph.GetRoom(Cellar)!.HasUnknownName);
        two.See(Attic, AtticName);
        Assert.True(two.Keep(Attic, AtticName));

        Assert.Equal(CellarName, two.NameOf(Cellar));
        Assert.Equal(new[] { Cellar }, two.Graph.FindCandidates(CellarName, new HashSet<Direction> { Direction.U }));
        Client fresh = Start(_realmA);
        Assert.Equal((HouseName, CellarName, AtticName),
            (fresh.NameOf(House), fresh.NameOf(Cellar), fresh.NameOf(Attic)));
        Assert.Equal((3, 3), (fresh.Store.CountForActiveSet, fresh.Store.OnRoomMap));
    }

    // ----- a file that can't be used -----------------------------------

    [Fact]
    public void ADamagedFile_PutsNothingOnTheMap_AndIsNeverWrittenOver()
    {
        Directory.CreateDirectory(_realmA);
        const string damaged = """{ "Version": 1, "Sets": [ { "Set": "alpha", "Rooms": [ { "Room": "15/861", "Name": "Shop Whi""";
        File.WriteAllText(NamesFile(_realmA), damaged);

        Client client = Start(_realmA);

        Assert.True(client.Graph.GetRoom(House)!.HasUnknownName);
        Assert.Equal((0, 0), (client.Store.CountForActiveSet, client.Store.OnRoomMap));
        Assert.Equal(1, Warnings);
        for (int i = 0; i < 5; i++) client.Store.TakeInOutsideChanges();
        client.Cache.ReloadActiveSet();
        Assert.Equal(1, Warnings);
        Assert.Equal(damaged, File.ReadAllText(NamesFile(_realmA)));
        Assert.False(File.Exists(NamesFile(_realmA) + ".bad"));

        // A name kept now is written, with the damaged file set aside whole first.
        client.See(Cellar, CellarName);
        Assert.True(client.Keep(Cellar, CellarName));

        Assert.Equal(damaged, File.ReadAllText(NamesFile(_realmA) + ".bad"));
        Assert.Equal(2, Warnings);
        Client next = Start(_realmA);
        Assert.Equal(CellarName, next.NameOf(Cellar));
        Assert.True(next.Graph.GetRoom(House)!.HasUnknownName);
    }

    [Fact]
    public void AFileOfALaterFormat_IsNeitherReadNorWritten()
    {
        Directory.CreateDirectory(_realmA);
        const string later = """{ "Version": 2, "Sets": [ { "Set": "alpha", "Rooms": [ { "Room": "15/861", "Name": "Later House" } ] } ] }""";
        File.WriteAllText(NamesFile(_realmA), later);

        Client client = Start(_realmA);
        Assert.True(client.Graph.GetRoom(House)!.HasUnknownName);
        Assert.Equal(1, Warnings);

        // Kept for the session, and said to be held only.
        client.See(House, HouseName);
        Assert.Equal(LearnedRoomNameStore.KeepResult.HeldLaterFormat, client.KeepHow(House, HouseName));
        client.Store.TakeInOutsideChanges();

        Assert.Equal(HouseName, client.NameOf(House));
        Assert.Equal(1, Said("on the map, not on file"));
        Assert.Equal(later, File.ReadAllText(NamesFile(_realmA)));
        Assert.Equal(1, Warnings);
    }

    // The realm's folder can't be written: keeping a name throws nothing, says so
    // once, holds the name for the session, and writes it when the folder can be
    // written again.
    [Fact]
    public void AFolderThatCantBeWritten_ThrowsNothing_AndIsWrittenLater()
    {
        // A file standing where the realm's folder should be.
        string blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, "not a folder");
        Client client = Start(blocked);

        client.See(House, HouseName);
        Assert.Equal(LearnedRoomNameStore.KeepResult.HeldUnwritten, client.KeepHow(House, HouseName));
        client.See(Cellar, CellarName);
        Assert.Equal(LearnedRoomNameStore.KeepResult.HeldUnwritten, client.KeepHow(Cellar, CellarName));

        Assert.Equal(1, Warnings);
        Assert.Equal(1, Said("can't be written"));
        client.Store.TakeInOutsideChanges();
        Assert.Equal(1, Warnings);
        Assert.Equal((HouseName, CellarName), (client.NameOf(House), client.NameOf(Cellar)));

        // The obstacle goes: the next heartbeat writes both.
        File.Delete(blocked);
        client.Store.TakeInOutsideChanges();
        Assert.Equal(1, Said("could be written again"));
        Client next = Start(blocked);
        Assert.Equal((HouseName, CellarName), (next.NameOf(House), next.NameOf(Cellar)));
    }

    // A file put together by hand: what can be read of it is, the rest skipped.
    [Fact]
    public void AHandMadeFile_IsReadForWhatItHolds()
    {
        Directory.CreateDirectory(_realmA);
        File.WriteAllText(NamesFile(_realmA), """
            { "Version": 1, "Sets": [
                null,
                { "Set": "", "Rooms": [ { "Room": "15/862", "Name": "No Set" } ] },
                { "Set": "ALPHA", "Rooms": [
                    null,
                    { "Room": "nowhere", "Name": "Bad Key" },
                    { "Room": "15/862", "Name": "  " },
                    { "Room": "77/7", "Name": "Not On The Map" },
                    { "Room": "1/321", "Name": "Not Oak Street" },
                    { "Room": "15/861", "Name": "Shop White House Room" } ] },
                { "Set": "gamma", "Rooms": null } ] }
            """);

        Client client = Start(_realmA);

        // A set's name is told apart the way the cache tells sets apart.
        Assert.Equal(HouseName, client.NameOf(House));
        Assert.True(client.Graph.GetRoom(Cellar)!.HasUnknownName);
        // The street has a name in the data: it stands, and that entry is dropped.
        Assert.Equal("Oak Street", client.NameOf(Street));
        Assert.Equal(1, Said("dropped 1 stored name(s) for 'alpha'"));
        // The room the map doesn't have is held, and said.
        Assert.Equal((2, 1), (client.Store.CountForActiveSet, client.Store.OnRoomMap));
        Assert.Equal(1, Said("1 more held for rooms the map doesn't have"));
        Assert.Equal(0, Warnings);
        Assert.Contains("77/7", File.ReadAllText(NamesFile(_realmA)));
    }

    // ----- the room map's quiet route ----------------------------------

    [Fact]
    public void ApplyStoredNames_NamesOnlyRoomsTheDataLeavesUnnamed_AndTakesBackWhatItNoLongerHolds()
    {
        Client client = Start(realm: null);
        RoomGraphManager graph = client.Graph;

        RoomGraphManager.StoredNames result = graph.ApplyStoredNames(new Dictionary<RoomKey, string>
        {
            [House] = HouseName,
            [Street] = "Not Oak Street",
            [new RoomKey(77, 7)] = "Not On The Map",
            [Cellar] = " ",
        });

        Assert.Equal(1, result.Applied);
        Assert.Equal(new[] { Street }, result.NamedByData);
        Assert.Equal(HouseName, client.NameOf(House));
        Assert.Equal("Oak Street", client.NameOf(Street));
        Assert.True(graph.GetRoom(Cellar)!.HasUnknownName);

        // Another name for the same room replaces the first in the indexes.
        Assert.Equal(1, graph.ApplyStoredNames(new Dictionary<RoomKey, string> { [House] = "Other" }).Applied);
        Assert.Empty(graph.FindCandidates(HouseName, HouseExits));
        Assert.Empty(graph.FindByName(HouseName));
        Assert.Equal(new[] { House }, graph.FindCandidates("Other", HouseExits));

        // A name the tracker learned is not this call's to take back.
        client.See(Cellar, CellarName);
        Assert.Equal(0, graph.ApplyStoredNames(new Dictionary<RoomKey, string>()).Applied);
        Assert.True(graph.GetRoom(House)!.HasUnknownName);
        Assert.Empty(graph.FindCandidates("Other", HouseExits));
        Assert.Empty(graph.FindByName("Other"));
        Assert.Equal(CellarName, client.NameOf(Cellar));

        // A change of realm takes those off, a learned name the store then took
        // included, and leaves every name the data gives.
        client.See(Attic, AtticName);
        graph.ApplyStoredNames(new Dictionary<RoomKey, string> { [Attic] = AtticName });
        graph.WithdrawLearnedNames();
        Assert.True(client.Unnamed(Cellar));
        Assert.True(client.Unnamed(Attic));
        Assert.Empty(graph.FindByName(CellarName));
        Assert.Empty(graph.FindByName(AtticName));
        Assert.Equal("Oak Street", client.NameOf(Street));
        Assert.Empty(graph.LearnedNames);
        // Nothing left to take off: the store's own names are untouched by it.
        graph.ApplyStoredNames(new Dictionary<RoomKey, string> { [House] = HouseName });
        graph.WithdrawLearnedNames();
        Assert.Equal(HouseName, client.NameOf(House));
        Assert.Equal(new[] { House }, graph.LearnedNames.Keys);
    }

    // ----- a change of realm takes the session's names off (the owner, 2026-10-11:
    //       "learned room names are per realm on a BBS") ------------------

    // Skip, or a prompt still open, on one realm; then a character of another
    // realm on the same set, so the map is not rebuilt. The room is nameless
    // there again: whatever that board prints is learned, asked about and kept
    // for it, whether it is another name or the same one.
    [Theory]
    [InlineData("Their House")]
    [InlineData(HouseName)]
    public void ANameLearnedAndNotSaved_ComesOffTheMapOnAnotherRealm_AndIsLearnedThereAfresh(string printedThere)
    {
        Client client = Start(_realmA);
        RoomTracker tracker = new(client.Graph);
        tracker.SetLocated(Street);
        tracker.NoteRoomObserved(new RoomObservation(HouseName, HouseExits));
        Assert.Equal(HouseName, client.NameOf(House));

        client.Realm = _realmB;
        client.Store.OnRealmChanged();
        Assert.True(client.Unnamed(House));

        RoomTracker there = new(client.Graph);
        NameLearnedEvent? asked = null;
        there.NameLearned += e => asked = e;
        there.SetLocated(Street);
        there.NoteRoomObserved(new RoomObservation(printedThere, HouseExits));

        Assert.Equal(RoomConfidence.Confirmed, there.State.Confidence);
        Assert.Equal(House, there.State.CurrentRoom!.Key);
        Assert.Equal(new NameLearnedEvent(House, printedThere), asked);
        Assert.True(client.Keep(House, printedThere));
        Assert.Contains(printedThere, File.ReadAllText(NamesFile(_realmB)));
        Assert.False(File.Exists(NamesFile(_realmA)));
    }

    // ----- a name is text someone supplied -------------------------------

    // A stored name is another client's or a hand edit's. One that could not be a
    // room's name is not put on the map, and the warning names the room without
    // repeating the text.
    [Fact]
    public void AStoredNameThatCouldNotBeARoomsName_IsNotUsed_AndNotEchoed()
    {
        Directory.CreateDirectory(_realmA);
        string huge = new('X', LearnedRoomNameStore.MaxNameLength + 1);
        string longest = new('Y', LearnedRoomNameStore.MaxNameLength);
        File.WriteAllText(NamesFile(_realmA),
            "{ \"Version\": 1, \"Sets\": [ { \"Set\": \"alpha\", \"Rooms\": ["
            + "{ \"Room\": \"15/861\", \"Name\": \"\\u001b[2J\\u001b[31mEvil\\r\\nsuicide\\u0000\" },"
            + "{ \"Room\": \"15/862\", \"Name\": \"" + huge + "\" },"
            + "{ \"Room\": \"15/863 (Door)\", \"Name\": \"An Exit Cell\" },"
            + "{ \"Room\": \" 15/863 \", \"Name\": \"A Padded Key\" } ] } ] }");

        Client client = Start(_realmA);

        Assert.True(client.Unnamed(House));
        Assert.True(client.Unnamed(Cellar));
        Assert.True(client.Unnamed(Attic));
        Assert.Equal(0, client.Store.CountForActiveSet);
        LogEntry warned = Assert.Single(_log, e => e.Severity == LogSeverity.Warn);
        Assert.Contains("15/861 of 'alpha', 15/862 of 'alpha'", warned.Message);
        Assert.DoesNotContain("Evil", warned.Message);
        Assert.DoesNotContain("XXXX", warned.Message);
        Assert.DoesNotContain(_log, e => e.Message.Contains('\u001b') || e.Message.Contains("suicide"));

        // At the cap and padded: used, trimmed.
        File.WriteAllText(NamesFile(_realmA),
            "{ \"Version\": 1, \"Sets\": [ { \"Set\": \"alpha\", \"Rooms\": ["
            + "{ \"Room\": \"15/861\", \"Name\": \"  " + longest + "  \" },"
            + "{ \"Room\": \"15/862\", \"Name\": \"\\tPadded Cellar \" } ] } ] }");
        File.SetLastWriteTimeUtc(NamesFile(_realmA), DateTime.UtcNow.AddSeconds(5));
        Assert.True(client.Store.TakeInOutsideChanges());
        Assert.Equal((longest, "Padded Cellar"), (client.NameOf(House), client.NameOf(Cellar)));
    }

    // The same check before a name is kept: the game's own text, but a name that
    // long or with such characters in it is no room name.
    [Fact]
    public void ANameThatCouldNotBeARoomsName_IsNotKept()
    {
        Client client = Start(_realmA);
        string huge = new('X', LearnedRoomNameStore.MaxNameLength + 1);
        client.See(House, huge);
        client.See(Cellar, "Cellar\u001b[31m");

        Assert.Equal(LearnedRoomNameStore.KeepResult.NotARoomName, client.KeepHow(House, huge));
        Assert.Equal(LearnedRoomNameStore.KeepResult.NotARoomName, client.KeepHow(Cellar, "Cellar\u001b[31m"));

        Assert.False(File.Exists(NamesFile(_realmA)));
        Assert.DoesNotContain(_log, e => e.Source == LearnedRoomNameStore.LogCategory
            && (e.Message.Contains("XXXX") || e.Message.Contains('\u001b')));
        Assert.Equal(2, Said("is not kept: it is too long or has characters a room name can't have"));
    }

    // ----- a file that can't be opened is not a damaged one ----------------

    // Another program holds the file: it may be whole. Nothing is put on the
    // map, nothing is set aside, and a name kept meanwhile is not written over
    // it. Once it opens, what it holds and what was kept are both on file.
    [Fact]
    public void AFileThatCantBeOpened_IsNotDamaged_AndIsNotWrittenUntilItHasBeenRead()
    {
        Client first = Start(_realmA);
        first.See(House, HouseName);
        Assert.True(first.Keep(House, HouseName));
        byte[] whole = File.ReadAllBytes(NamesFile(_realmA));
        _log.Clear();

        Client client;
        using (new FileStream(NamesFile(_realmA), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            client = Start(_realmA);
            Assert.True(client.Unnamed(House));
            Assert.Equal(1, Said("can't be opened"));

            client.See(Cellar, CellarName);
            Assert.Equal(LearnedRoomNameStore.KeepResult.HeldUnread, client.KeepHow(Cellar, CellarName));
            for (int i = 0; i < 5; i++) Assert.False(client.Store.TakeInOutsideChanges());

            Assert.Equal(CellarName, client.NameOf(Cellar));
            Assert.Equal(1, Warnings);
            Assert.False(File.Exists(NamesFile(_realmA) + ".bad"));
            Assert.Empty(Directory.GetFiles(_realmA, "*.tmp"));
        }
        Assert.Equal(whole, File.ReadAllBytes(NamesFile(_realmA)));

        // The next heartbeat reads it: its name goes on the map, the kept one on file.
        Assert.True(client.Store.TakeInOutsideChanges());
        Assert.Equal((HouseName, CellarName), (client.NameOf(House), client.NameOf(Cellar)));
        Assert.Equal(1, Said("could be opened again"));
        Assert.False(File.Exists(NamesFile(_realmA) + ".bad"));
        Client next = Start(_realmA);
        Assert.Equal((HouseName, CellarName), (next.NameOf(House), next.NameOf(Cellar)));
    }

    // ----- the realm's folder renamed --------------------------------------

    // A BBS renamed from Profile Management: the folder moves and no pin is
    // raised. The names stay on the map, an unsaved one too, and a later keep
    // goes to the file where it now is; the old folder is not made again.
    [Fact]
    public void ABbsRenamed_TakesTheStoreWithItsFolder_AndNothingComesOffTheMap()
    {
        Client client = Start(_realmA);
        client.See(House, HouseName);
        Assert.True(client.Keep(House, HouseName));
        client.See(Cellar, CellarName);

        string board = Path.Combine(_root, "BBS", "board"), renamed = Path.Combine(_root, "BBS", "renamed");
        Directory.Move(board, renamed);
        client.Realm = Path.Combine(renamed, "Realms", "one");
        client.Store.OnFolderRenamed(board, renamed);

        Assert.Equal(client.Realm, client.Store.RealmFolder);
        Assert.False(client.Store.TakeInOutsideChanges());
        Assert.Equal((HouseName, CellarName), (client.NameOf(House), client.NameOf(Cellar)));
        Assert.Equal(1, client.Store.CountForActiveSet);

        Assert.True(client.Keep(Cellar, CellarName));
        string file = File.ReadAllText(NamesFile(client.Realm));
        Assert.Contains("15/861", file);
        Assert.Contains("15/862", file);
        Assert.False(Directory.Exists(board));

        // Another BBS renamed: nothing of this realm's moves.
        client.Store.OnFolderRenamed(Path.Combine(_root, "BBS", "other"), Path.Combine(_root, "BBS", "another"));
        Assert.Equal(client.Realm, client.Store.RealmFolder);
        Assert.Equal((HouseName, CellarName), (client.NameOf(House), client.NameOf(Cellar)));
    }

    // A realm left with a name still to be written, its folder gone meanwhile
    // (removed, or renamed with no word of it): the folder is not made again.
    [Fact]
    public void LeavingARealmWhoseFolderIsGone_DoesNotMakeItAgain()
    {
        string blocked = Path.Combine(_root, "BBS", "gone", "Realms", "one");
        Directory.CreateDirectory(Path.GetDirectoryName(blocked)!);
        File.WriteAllText(blocked, "not a folder");
        Client client = Start(blocked);
        client.See(House, HouseName);
        Assert.Equal(LearnedRoomNameStore.KeepResult.HeldUnwritten, client.KeepHow(House, HouseName));
        Directory.Delete(Path.Combine(_root, "BBS", "gone"), recursive: true);

        client.Realm = _realmB;
        client.Store.OnRealmChanged();

        Assert.False(Directory.Exists(Path.Combine(_root, "BBS", "gone")));
        Assert.True(client.Unnamed(House));
    }

    // ----- the board gives such a room another name (the owner, 2026-10-11: "a
    //       gang's house name can change whenever the sysop edits it") ------

    private const string Renamed = "Shop Red House Room";

    // A session in which the house is on the map under its stored name.
    private Client WithHouseStored(bool cellarToo = false)
    {
        Client first = Start(_realmA);
        first.See(House, HouseName);
        Assert.True(first.Keep(House, HouseName));
        if (cellarToo)
        {
            first.See(Cellar, CellarName);
            Assert.True(first.Keep(Cellar, CellarName));
        }
        _log.Clear();
        Client client = Start(_realmA);
        Assert.Equal(HouseName, client.NameOf(House));
        return client;
    }

    private static RoomTracker TrackerAt(Client client, RoomKey room, List<NameLearnedEvent> asked)
    {
        RoomTracker tracker = new(client.Graph);
        tracker.NameLearned += asked.Add;
        tracker.SetLocated(room);
        return tracker;
    }

    private static void Step(RoomTracker tracker, Direction step, string printed, params Direction[] exits)
    {
        tracker.NoteMoveSent(step);
        tracker.NoteRoomObserved(new RoomObservation(printed, new HashSet<Direction>(exits)));
    }

    [Fact]
    public void ARoomUnderAStoredName_PrintingAnother_IsAdoptedOnItsExits_AndTheNewNameAskedAbout()
    {
        Client client = WithHouseStored();
        List<NameLearnedEvent> asked = new();
        RoomTracker tracker = TrackerAt(client, Street, asked);

        Step(tracker, Direction.S, Renamed, Direction.N, Direction.D, Direction.U);

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(House, tracker.State.CurrentRoom!.Key);
        Assert.Equal(new[] { new NameLearnedEvent(House, Renamed) }, asked);
        Assert.Equal(Renamed, client.NameOf(House));
        Assert.Equal(new[] { House }, client.Graph.FindCandidates(Renamed, HouseExits));
        Assert.Equal(new[] { House }, client.Graph.FindByName(Renamed).Select(r => r.Key));
        Assert.Empty(client.Graph.FindCandidates(HouseName, HouseExits));
        Assert.Empty(client.Graph.FindByName(HouseName));
        // What Save would replace is still on file.
        Assert.Equal(HouseName, client.Store.NameFor(House));

        // On into a room with no name at all: that one is learned too, where a
        // tracker left unsure in the house could not.
        Step(tracker, Direction.D, CellarName, Direction.U);
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(Cellar, tracker.State.CurrentRoom!.Key);
        Assert.Equal(new NameLearnedEvent(Cellar, CellarName), asked[^1]);
    }

    // Save: the new name replaces the stored one for the room on this realm and set.
    [Fact]
    public void KeepingTheNewName_ReplacesTheStoredOne()
    {
        Client client = WithHouseStored();
        Step(TrackerAt(client, Street, new()), Direction.S, Renamed, Direction.N, Direction.D, Direction.U);

        Assert.True(client.Keep(House, Renamed));

        string file = File.ReadAllText(NamesFile(_realmA));
        Assert.Contains(Renamed, file);
        Assert.DoesNotContain(HouseName, file);
        Assert.Equal((1, 1), (client.Store.CountForActiveSet, client.Store.OnRoomMap));
        Assert.Equal(Renamed, Start(_realmA).NameOf(House));
    }

    // Skip: the new name is the map's for the session, whatever the store does
    // meanwhile, and the old one stays on file, so the next session asks again.
    // Only a store that holds something newer puts another name on the room.
    [Fact]
    public void SkippingTheNewName_LeavesItOnTheMapForTheSession_AndTheOldOneOnFile()
    {
        Client client = WithHouseStored();
        Step(TrackerAt(client, Street, new()), Direction.S, Renamed, Direction.N, Direction.D, Direction.U);

        client.Store.OnRealmChanged();
        client.Store.TakeInOutsideChanges();
        client.See(Cellar, CellarName);
        Assert.True(client.Keep(Cellar, CellarName));

        Assert.Equal(Renamed, client.NameOf(House));
        Assert.Equal(HouseName, client.Store.NameFor(House));
        Assert.Contains(HouseName, File.ReadAllText(NamesFile(_realmA)));
        // What the bug report says of it: two held, the cellar's on the map, the
        // house's left off for the name printed since.
        Assert.Equal((2, 1, 1),
            (client.Store.CountForActiveSet, client.Store.OnRoomMap, client.Store.ReplacedThisSession));

        // Another client keeps a name for the room: newer than the one replaced.
        Client other = Start(_realmA);
        other.See(House, "Shop Blue House Room");
        Assert.True(other.Keep(House, "Shop Blue House Room"));
        File.SetLastWriteTimeUtc(NamesFile(_realmA), DateTime.UtcNow.AddSeconds(5));
        Assert.True(client.Store.TakeInOutsideChanges());
        Assert.Equal("Shop Blue House Room", client.NameOf(House));
        Assert.Equal((2, 2, 0),
            (client.Store.CountForActiveSet, client.Store.OnRoomMap, client.Store.ReplacedThisSession));
    }

    // A room met under two names in turn is asked about once for each, however
    // often it changes back; and each realm is asked for itself.
    [Fact]
    public void AName_IsAskedAboutOncePerRoomInASession()
    {
        Client client = Start(_realmA);

        Assert.True(client.Store.AskAbout(House, HouseName));
        Assert.False(client.Store.AskAbout(House, HouseName));
        Assert.True(client.Store.AskAbout(House, Renamed));
        for (int i = 0; i < 3; i++)
        {
            Assert.False(client.Store.AskAbout(House, HouseName));
            Assert.False(client.Store.AskAbout(House, Renamed));
        }
        // Another room under the same name is another question.
        Assert.True(client.Store.AskAbout(Cellar, HouseName));

        client.Realm = _realmB;
        Assert.True(client.Store.AskAbout(House, HouseName));
        Assert.False(client.Store.AskAbout(House, HouseName));
        Assert.Equal(_realmB, client.Store.RealmFolder);

        // Nowhere to keep a name: nothing to ask.
        client.Realm = null;
        Assert.False(client.Store.AskAbout(House, "Somewhere"));
        client.Realm = _realmA;
        client.Cache.SwitchSet(null);
        Assert.False(client.Store.AskAbout(House, "Somewhere"));
    }

    // ----- the realm renamed ------------------------------------------------

    // A realm renamed is told to the store (Profile.RealmRenamed) once its folder
    // has moved. The pin that follows finds the store there already, and nothing
    // comes off the map, an unsaved name included.
    private string RenameRealmA(Client client)
    {
        string renamed = Path.Combine(Path.GetDirectoryName(_realmA)!, "one renamed");
        if (Directory.Exists(_realmA)) Directory.Move(_realmA, renamed);
        client.Store.OnFolderRenamed(_realmA, renamed);
        client.Realm = renamed;
        client.Store.OnRealmChanged();
        return renamed;
    }

    [Fact]
    public void ARealmRenamed_IsFollowedToItsFolder_AndNothingComesOffTheMap()
    {
        Client client = Start(_realmA);
        client.See(House, HouseName);
        Assert.True(client.Keep(House, HouseName));
        client.See(Cellar, CellarName);
        _log.Clear();

        string renamed = RenameRealmA(client);

        Assert.Equal(renamed, client.Store.RealmFolder);
        Assert.Equal((HouseName, CellarName), (client.NameOf(House), client.NameOf(Cellar)));
        Assert.Equal((1, 1), (client.Store.CountForActiveSet, client.Store.OnRoomMap));
        // The question about the cellar was asked under the old folder; Save
        // answered now files it in the new one.
        Assert.Equal(LearnedRoomNameStore.KeepResult.Stored,
            client.Store.Keep(Cellar, CellarName, _realmA, client.Graph.ActiveSet));
        Assert.Contains("15/862", File.ReadAllText(NamesFile(renamed)));
        Assert.False(Directory.Exists(_realmA));
        Assert.Equal(1, Said("the realm's folder moved"));
    }

    // With no names file in the folder yet, the rename is followed all the same:
    // the names learned this session stay on the map, and the first one saved
    // goes to the new folder.
    [Fact]
    public void ARealmWithNoNamesFileRenamed_KeepsItsSessionNames()
    {
        Client client = Start(_realmA);
        Directory.CreateDirectory(_realmA);
        client.See(Cellar, CellarName);

        string renamed = RenameRealmA(client);

        Assert.Equal(renamed, client.Store.RealmFolder);
        Assert.Equal(CellarName, client.NameOf(Cellar));
        Assert.True(client.Keep(Cellar, CellarName));
        Assert.Contains("15/862", File.ReadAllText(NamesFile(renamed)));
        Assert.False(Directory.Exists(_realmA));
    }

    // The same, with no folder at all yet (nothing collected on the realm).
    [Fact]
    public void ARealmWithNoFolderRenamed_KeepsItsSessionNames()
    {
        Client client = Start(_realmA);
        client.See(Cellar, CellarName);

        string renamed = RenameRealmA(client);

        Assert.Equal(CellarName, client.NameOf(Cellar));
        Assert.True(client.Keep(Cellar, CellarName));
        Assert.True(File.Exists(NamesFile(renamed)));
        Assert.False(Directory.Exists(_realmA));
    }

    // A pin to another folder with no word of a rename is a change of realm,
    // whatever the folders hold: the store doesn't guess.
    [Fact]
    public void AFolderMovedWithNoWordOfARename_IsAChangeOfRealm()
    {
        Client client = Start(_realmA);
        client.See(House, HouseName);
        Assert.True(client.Keep(House, HouseName));
        client.See(Cellar, CellarName);

        string moved = Path.Combine(Path.GetDirectoryName(_realmA)!, "one moved");
        Directory.Move(_realmA, moved);
        client.Realm = moved;
        client.Store.OnRealmChanged();

        Assert.Equal(moved, client.Store.RealmFolder);
        // The kept name is read back from the file that moved; the unsaved one is
        // not carried.
        Assert.Equal(HouseName, client.NameOf(House));
        Assert.True(client.Unnamed(Cellar));
    }

    // A realm removed is a change of realm: the character is on another realm of
    // the BBS, whose file is that realm's.
    [Fact]
    public void ARealmRemoved_IsAChangeOfRealm_NotARename()
    {
        Client there = Start(_realmB);
        there.See(House, "Their House");
        Assert.True(there.Keep(House, "Their House"));
        File.SetLastWriteTimeUtc(NamesFile(_realmB), DateTime.UtcNow.AddSeconds(-30));

        Client client = Start(_realmA);
        client.See(House, HouseName);
        Assert.True(client.Keep(House, HouseName));
        client.See(Cellar, CellarName);

        Directory.Delete(_realmA, recursive: true);
        client.Realm = _realmB;
        client.Store.OnRealmChanged();

        Assert.Equal("Their House", client.NameOf(House));
        Assert.True(client.Unnamed(Cellar));
        Assert.DoesNotContain(HouseName, File.ReadAllText(NamesFile(_realmB)));
    }

    // A realm removed and another made beside it whose names file has the very
    // write time of the removed one's: still a change. Nothing of the removed
    // realm's session is carried over.
    [Fact]
    public void ARealmRemoved_AndAnotherMadeWithAFileOfTheSameWriteTime_InheritsNothing()
    {
        Client client = Start(_realmA);
        client.See(House, HouseName);
        Assert.True(client.Keep(House, HouseName));
        client.See(Cellar, CellarName);
        DateTime written = File.GetLastWriteTimeUtc(NamesFile(_realmA));

        Client there = Start(_realmB);
        there.See(House, "Their House");
        Assert.True(there.Keep(House, "Their House"));
        File.SetLastWriteTimeUtc(NamesFile(_realmB), written);

        Directory.Delete(_realmA, recursive: true);
        client.Realm = _realmB;
        client.Store.OnRealmChanged();

        Assert.Equal(_realmB, client.Store.RealmFolder);
        Assert.Equal("Their House", client.NameOf(House));
        Assert.True(client.Unnamed(Cellar));
    }

    // A realm renamed, and later a new realm given its old name and played: the
    // old name is that realm's now, and a name seen on it is saved there.
    [Fact]
    public void ARealmGivenARenamedRealmsOldName_LaterSavesItsOwnNames()
    {
        Client client = Start(_realmA);
        client.See(Cellar, CellarName);
        RenameRealmA(client);

        client.Realm = _realmA;
        client.Store.OnRealmChanged();

        Assert.Equal(_realmA, client.Store.RealmFolder);
        client.See(House, HouseName);
        Assert.Equal(LearnedRoomNameStore.KeepResult.Stored, client.KeepHow(House, HouseName));
        Assert.Contains("15/861", File.ReadAllText(NamesFile(_realmA)));
    }

    // In one Settings OK: the realm renamed, a new realm given its old name, and
    // the character moved to that one. The pin takes up the new realm, not the
    // renamed one.
    [Fact]
    public void ARealmGivenARenamedRealmsOldName_InTheSameOk_IsTakenUp()
    {
        Client client = Start(_realmA);
        client.See(Cellar, CellarName);
        string renamed = Path.Combine(Path.GetDirectoryName(_realmA)!, "one renamed");
        client.Store.OnFolderRenamed(_realmA, renamed);

        client.Realm = _realmA;
        client.Store.OnRealmChanged();

        Assert.Equal(_realmA, client.Store.RealmFolder);
        Assert.True(client.Unnamed(Cellar));
    }

    // Another realm renamed: nothing of this one's moves.
    [Fact]
    public void AnotherRealmRenamed_LeavesThisOneAlone()
    {
        Client client = Start(_realmA);
        client.See(Cellar, CellarName);

        client.Store.OnFolderRenamed(_realmB, _realmB + " renamed");
        client.Store.OnFolderRenamed(_realmA + "x", _realmA + "y");
        client.Store.OnRealmChanged();

        Assert.Equal(_realmA, client.Store.RealmFolder);
        Assert.Equal(CellarName, client.NameOf(Cellar));
    }

    // ----- several clients writing at once -------------------------------------

    // The second client's stamp can't see the first's write: it landed within the
    // file clock's step of the second's last read. The second's write is made
    // under the hold, over the file as it is then, so the first's name stays.
    [Fact]
    public void AWriteTheStampCantSee_IsNotWrittenOver()
    {
        Client one = Start(_realmA), two = Start(_realmA);
        one.See(Cellar, CellarName);
        Assert.True(one.Keep(Cellar, CellarName));
        Assert.True(two.Store.TakeInOutsideChanges());
        DateTime seen = File.GetLastWriteTimeUtc(NamesFile(_realmA));

        one.See(House, HouseName);
        Assert.True(one.Keep(House, HouseName));
        File.SetLastWriteTimeUtc(NamesFile(_realmA), seen);

        two.See(Attic, AtticName);
        Assert.True(two.Keep(Attic, AtticName));

        // And the house goes on the second's map with the write that found it.
        Assert.Equal(HouseName, two.NameOf(House));
        Client fresh = Start(_realmA);
        Assert.Equal((HouseName, CellarName, AtticName),
            (fresh.NameOf(House), fresh.NameOf(Cellar), fresh.NameOf(Attic)));
        Assert.True(File.Exists(NamesFile(_realmA) + ".lock"));
    }

    // Another client holds the file for its write: this one waits for it, and if
    // the hold outlasts the wait, the name is held and written on a later
    // heartbeat, the file never written without the hold.
    [Fact]
    public void AHoldThatOutlastsTheWait_HoldsTheName_AndItIsWrittenLater()
    {
        Client client = Start(_realmA);
        Directory.CreateDirectory(_realmA);
        client.See(Cellar, CellarName);
        using (SharedFileWrite.Hold(NamesFile(_realmA), out _))
        {
            Assert.Equal(LearnedRoomNameStore.KeepResult.HeldUnwritten, client.KeepHow(Cellar, CellarName));
            Assert.False(File.Exists(NamesFile(_realmA)));
        }
        client.Store.TakeInOutsideChanges();
        Assert.Equal(1, Said("could be written again"));
        Assert.Equal(CellarName, Start(_realmA).NameOf(Cellar));
    }

    // ----- what a write that never finished left -------------------------------

    [Fact]
    public void ADeadWritesTemporaryFile_IsRemovedAtLoad_AndOneUnderAWritersHoldIsLeft()
    {
        Directory.CreateDirectory(_realmA);
        string dead = $"{NamesFile(_realmA)}.{int.MaxValue}.tmp";
        string mine = $"{NamesFile(_realmA)}.{Environment.ProcessId}.tmp";
        string others = $"{NamesFile(_realmA)}.{int.MaxValue}.{Guid.NewGuid():N}.tmp";
        foreach (string temp in new[] { dead, mine, others }) File.WriteAllText(temp, "{");

        // A client is writing the file: its temporary file may be the one being
        // written, so none is touched.
        using (SharedFileWrite.Hold(NamesFile(_realmA), out _)) Start(_realmA);
        Assert.True(File.Exists(dead));
        Assert.Equal(0, Said("left by a write that never finished"));

        // No write in progress: whatever process made them, they are dead.
        Start(_realmA);
        Assert.False(File.Exists(dead));
        Assert.False(File.Exists(mine));
        Assert.True(File.Exists(others));
        Assert.Equal(2, Said("left by a write that never finished"));
    }

    [Fact]
    public void TheNextSessionAfterASkip_AsksAgain()
    {
        Client client = WithHouseStored();
        Step(TrackerAt(client, Street, new()), Direction.S, Renamed, Direction.N, Direction.D, Direction.U);

        Client next = Start(_realmA);
        Assert.Equal(HouseName, next.NameOf(House));
        List<NameLearnedEvent> asked = new();
        Step(TrackerAt(next, Street, asked), Direction.S, Renamed, Direction.N, Direction.D, Direction.U);
        Assert.Equal(new[] { new NameLearnedEvent(House, Renamed) }, asked);
    }

    // The same for a name learned this session and never saved.
    [Fact]
    public void ARoomUnderANameLearnedThisSession_PrintingAnother_IsRelearned()
    {
        Client client = Start(_realmA);
        List<NameLearnedEvent> asked = new();
        Step(TrackerAt(client, Street, asked), Direction.S, HouseName, Direction.N, Direction.D, Direction.U);
        Assert.Equal(HouseName, client.NameOf(House));

        RoomTracker tracker = TrackerAt(client, Street, asked);
        Step(tracker, Direction.S, Renamed, Direction.N, Direction.D, Direction.U);

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(House, tracker.State.CurrentRoom!.Key);
        Assert.Equal(new[] { new NameLearnedEvent(House, HouseName), new NameLearnedEvent(House, Renamed) }, asked);
        Assert.Equal(Renamed, client.NameOf(House));
        Assert.True(client.Keep(House, Renamed));
        string file = File.ReadAllText(NamesFile(_realmA));
        Assert.Contains(Renamed, file);
        Assert.DoesNotContain(HouseName, file);
    }

    // What is and isn't taken for a renamed room, and the walk through a whole
    // house: RoomRenameRelearnTests.

    // The prompt says what Save would replace, when there is a name on file.
    [Fact]
    public void ThePrompt_ShowsTheNameKeptBefore_WhenThereIsOne()
    {
        ViewModels.RoomNameLearnedDialogViewModel first = new(House, HouseName);
        Assert.False(first.HasKeptBefore);
        Assert.Equal("Save the name seen for room 15/861?", first.HeaderText);

        ViewModels.RoomNameLearnedDialogViewModel again = new(House, Renamed, keptBefore: HouseName);
        Assert.True(again.HasKeptBefore);
        Assert.Equal((HouseName, Renamed), (again.KeptBeforeValue, again.ObservedNameValue));
        Assert.Contains("replaces the name kept", again.WhereKeptText);

        // The same name on file is nothing to replace.
        Assert.False(new ViewModels.RoomNameLearnedDialogViewModel(House, HouseName, keptBefore: HouseName).HasKeptBefore);
    }
}
