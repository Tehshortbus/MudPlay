using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MudPlay.Game.Map;
using MudPlay.Game.Map.MpFile;
using MudPlay.Services;
using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

// The .mp import review stays open through a switch of game data. Its rows are rooms
// of the set the file was translated on: under another set nothing is accepted (the
// save would land in that set's Loops folder), verified or translated again, and a
// start room picked meanwhile is never saved before the rows have shown it.
public sealed class MpImportReviewOtherGameDataTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mudplay-mpreview-otherset-" + Path.GetRandomFileName());
    private readonly string _opened = "test-mpreview-" + Guid.NewGuid().ToString("N")[..12];
    private string Other => _opened + "-other";

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
        foreach (string set in new[] { _opened, Other })
        {
            try
            {
                string dir = Path.Combine(AppPaths.GameDataRoot, set);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch { /* best-effort temp cleanup */ }
        }
    }

    // The same square on two maps, so the file's start room matches a room on each:
    // n/1 Start —N→ n/2 North —E→ n/4 Corner —S→ n/3 East —W→ n/1.
    private static string Square(int map) => $$"""
          { "Map Number": {{map}}, "Room Number": 1, "Name": "Start",
            "N": "{{map}}/2", "S": "0", "E": "{{map}}/3", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": {{map}}, "Room Number": 2, "Name": "North",
            "N": "0", "S": "{{map}}/1", "E": "{{map}}/4", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": {{map}}, "Room Number": 3, "Name": "East",
            "N": "{{map}}/4", "S": "0", "E": "0", "W": "{{map}}/1", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": {{map}}, "Room Number": 4, "Name": "Corner",
            "N": "0", "S": "{{map}}/3", "E": "0", "W": "{{map}}/2", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        """;

    // The other set's rooms: 1/1 is another room there.
    private const string OtherRooms = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Elsewhere",
            "N": "0", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private sealed record Opened(
        MpImportReviewViewModel Review, LoopManager Loops, LogService Log, GameDataCache Cache, RoomGraphManager Graph,
        MovementFilter Filter);

    private Opened Open()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), $"[\n{Square(1)},\n{Square(2)}\n]");
        Directory.CreateDirectory(Path.Combine(_root, "beta"));
        File.WriteAllText(Path.Combine(_root, "beta", "Rooms.json"), OtherRooms);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager g = new(cache);
        g.OnActiveSetChanged("alpha");
        string H(int room) => MegaMudHash.ComputeHashExits(g.GetRoom(new RoomKey(1, room))!);
        string[] steps = { $"{H(1)}:0000:n", $"{H(2)}:0000:e", $"{H(4)}:0000:s", $"{H(3)}:0000:w" };
        MpLoopFile file = MpFileParser.Parse(
            $"[Square][Tester]\n[SQRE:Test:Start]\n{H(1)}:{H(1)}:4:-1:0:::\n" + string.Join("\n", steps));
        LoopManager loops = new(new BfsMapper(g), g);
        loops.LoadAll(_opened);
        LogService log = new();
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        MovementFilter filter = new(profile);
        return new Opened(
            new MpImportReviewViewModel("square.mp", file, null, new MpFileImporter(g), g, loops, filter, log),
            loops, log, cache, g, filter);
    }

    private static RoomKey?[] Rooms(MpImportReviewViewModel vm) =>
        vm.Rows.Select(r => r.ToWaypoint()?.Key).ToArray();

    private string Refusal =>
        $"This loop's rooms belong to game data '{_opened}', and '{Other}' is active now — "
        + "switch back to that game data to change or save it.";

    private const string WaitingNote =
        "The start room picked meanwhile is not in the rows yet: the next Verify or Accept on this loop's own game data rebuilds them from it and stops there.";

    private const string Rebuilt =
        "The rows were rebuilt from the start room picked while other game data was active, and nothing was ";

    private void AssertRefused(MpImportReviewViewModel review)
    {
        Assert.False(review.StatusIsGood);
        Assert.Equal(Refusal, review.Status);
    }

    [Fact]
    public void Accept_UnderOtherGameData_IsRefusedAndWritesNothingThere()
    {
        Opened o = Open();

        o.Loops.LoadAll(Other);
        bool closed = false;
        o.Review.CloseRequested += _ => closed = true;
        o.Review.AcceptCommand.Execute(null);

        AssertRefused(o.Review);
        Assert.False(closed);
        Assert.Empty(o.Loops.Loops);
        Assert.False(Directory.Exists(AppPaths.GameDataSetLoopsFolder(Other)));
        Assert.Contains(o.Log.Snapshot(), e => e.Severity == LogSeverity.Info && e.Source == "MpImporter"
            && e.Message == $"square.mp: accept refused: its rooms are game data '{_opened}''s and '{Other}' is active now");
    }

    [Fact]
    public void Accept_BackOnTheGameDataItWasOpenedOn_Saves()
    {
        Opened o = Open();
        o.Loops.LoadAll(Other);
        o.Review.AcceptCommand.Execute(null);
        AssertRefused(o.Review);

        o.Loops.LoadAll(_opened);
        Loop? accepted = null;
        o.Review.CloseRequested += loop => accepted = loop;
        o.Review.AcceptCommand.Execute(null);

        Assert.NotNull(accepted);
        Assert.Equal(4, o.Loops.Get(o.Review.LoopName.Trim())?.Waypoints.Count);
    }

    [Fact]
    public void Verify_UnderOtherGameData_IsRefusedAndTranslatesNothing()
    {
        Opened o = Open();
        // A room typed on a step re-translates every step's row on Verify.
        o.Review.Rows[0].RoomText = "1/1";
        MpTranslatedRowViewModel[] rows = o.Review.Rows.ToArray();

        o.Loops.LoadAll(Other);
        o.Review.VerifyCommand.Execute(null);

        AssertRefused(o.Review);
        Assert.Equal(rows, o.Review.Rows);

        o.Loops.LoadAll(_opened);
        o.Review.VerifyCommand.Execute(null);

        Assert.True(o.Review.StatusIsGood);
        Assert.NotSame(rows[0], o.Review.Rows[0]);
    }

    [Fact]
    public async Task RoomsMdIsNotLoadedUnderOtherGameData()
    {
        Opened o = Open();

        o.Loops.LoadAll(Other);
        await o.Review.LoadRoomsMdCommand.ExecuteAsync(null);

        AssertRefused(o.Review);
    }

    // ----- a start room picked while the other game data was active -----------------

    // Picks the other start room, under the other game data when away is set, makes
    // the edit, and comes back.
    private static MpAnchorChoice PickOtherStart(Opened o, string otherSet, string ownSet, bool away,
        Action<MpImportReviewViewModel>? edit = null)
    {
        MpAnchorChoice other = o.Review.AnchorChoices.Single(a => !a.Equals(o.Review.SelectedAnchor));
        if (away) o.Loops.LoadAll(otherSet);
        o.Review.SelectedAnchor = other;
        edit?.Invoke(o.Review);
        if (away) o.Loops.LoadAll(ownSet);
        return other;
    }

    private static string Shape(Loop? loop) => loop is null ? "(nothing saved)" : string.Join(" | ",
        loop.Waypoints.Select(w => $"{w.Room} cmd={w.Command ?? "-"} delay={w.DelayMs} nr={w.DoNotRest}"));

    private static string Shape(MpImportReviewViewModel review) => string.Join(" | ",
        review.Rows.Select(r => r.ToWaypoint()).OfType<LoopWaypoint>()
            .Select(w => $"{w.Room} cmd={w.Command ?? "-"} delay={w.DelayMs} nr={w.DoNotRest}"));

    [Fact]
    public void AStartRoomPickedUnderOtherGameData_Waits_AndTheStatusKeepsSayingSo()
    {
        Opened o = Open();
        Assert.Equal(2, o.Review.AnchorChoices.Count);
        RoomKey?[] before = Rooms(o.Review);

        o.Loops.LoadAll(Other);
        o.Review.SelectedAnchor = o.Review.AnchorChoices.Single(a => !a.Equals(o.Review.SelectedAnchor));

        Assert.Equal($"{Refusal} {WaitingNote}", o.Review.Status);
        Assert.Equal(before, Rooms(o.Review));

        // An edit of the rows puts its own line up; the wait is still said after it.
        o.Review.MoveDownCommand.Execute(o.Review.Rows[1]);
        Assert.EndsWith(WaitingNote, o.Review.Status);
        Assert.False(o.Review.StatusIsGood);
    }

    [Fact]
    public void AWaitingStartRoom_TheFirstAcceptRebuildsTheRowsAndSavesNothing()
    {
        Opened o = Open();
        MpAnchorChoice other = PickOtherStart(o, Other, _opened, away: true);
        Assert.All(Rooms(o.Review), k => Assert.NotEqual(other.Anchor.Map, k?.Map));

        Loop? accepted = null;
        bool closed = false;
        o.Review.CloseRequested += loop => { closed = true; accepted = loop; };
        o.Review.AcceptCommand.Execute(null);

        Assert.False(closed);
        Assert.Empty(o.Loops.Loops);
        Assert.False(o.Review.StatusIsGood);
        Assert.Equal(Rebuilt + "saved — check them, then press Verify or Accept again.", o.Review.Status);
        Assert.Equal(other.Anchor, Rooms(o.Review)[0]);
        Assert.All(Rooms(o.Review), k => Assert.Equal(other.Anchor.Map, k?.Map));

        // The second click saves the rows as they stand, the loop a pick with no
        // switch of game data would have saved.
        string shown = Shape(o.Review);
        o.Review.AcceptCommand.Execute(null);

        Assert.True(closed);
        Assert.Equal(shown, Shape(accepted));
        Assert.All(accepted!.Waypoints, w => Assert.Equal(other.Anchor.Map, w.Key.Map));
    }

    [Fact]
    public void AWaitingStartRoom_TheFirstVerifyRebuildsTheRowsAndVerifiesNothing()
    {
        Opened o = Open();
        MpAnchorChoice other = PickOtherStart(o, Other, _opened, away: true);

        o.Review.VerifyCommand.Execute(null);

        Assert.False(o.Review.StatusIsGood);
        Assert.Equal(Rebuilt + "verified — check them, then press Verify or Accept again.", o.Review.Status);
        Assert.Equal(other.Anchor, Rooms(o.Review)[0]);
        Assert.Contains(o.Log.Snapshot(), e => e.Severity == LogSeverity.Info && e.Source == "MpImporter"
            && e.Message.StartsWith("square.mp: verify stopped, rows rebuilt from the start room picked under other game data", StringComparison.Ordinal));

        o.Review.VerifyCommand.Execute(null);

        Assert.True(o.Review.StatusIsGood);
    }

    [Fact]
    public void AWaitingStartRoom_SettingsMadeSinceThePickAreSaidLost_AndNotSaved()
    {
        void Edit(MpImportReviewViewModel r)
        {
            r.Rows[1].Command = "bash door";
            r.Rows[1].DelayMs = 500;
            r.Rows[2].DoNotRest = true;
        }
        Opened o = Open();
        PickOtherStart(o, Other, _opened, away: true, Edit);

        Loop? accepted = null;
        bool closed = false;
        o.Review.CloseRequested += loop => { closed = true; accepted = loop; };
        o.Review.AcceptCommand.Execute(null);

        Assert.False(closed);
        Assert.Empty(o.Loops.Loops);
        Assert.Equal(
            Rebuilt + "saved — check them, then press Verify or Accept again."
            + " The command, delay or ticks set on 2 step(s) were not carried over.",
            o.Review.Status);
        Assert.Null(o.Review.Rows[1].Command);
        Assert.False(o.Review.Rows[2].DoNotRest);

        string shown = Shape(o.Review);
        o.Review.AcceptCommand.Execute(null);

        Assert.True(closed);
        Assert.Equal(shown, Shape(accepted));
        Assert.Null(accepted!.Waypoints[1].Command);
    }

    [Fact]
    public void AWaitingStartRoom_ARoomTypedSinceThePickIsKept()
    {
        void Edit(MpImportReviewViewModel r) => r.Rows[3].RoomText = "2/2";

        // What the same pick and room save with no switch of game data.
        Opened plain = Open();
        PickOtherStart(plain, Other, _opened, away: false, Edit);
        Loop? never = null;
        plain.Review.CloseRequested += loop => never = loop;
        plain.Review.AcceptCommand.Execute(null);
        Assert.NotNull(never);
        plain.Loops.Delete(never!.Name);

        Opened o = Open();
        PickOtherStart(o, Other, _opened, away: true, Edit);
        Loop? accepted = null;
        bool closed = false;
        o.Review.CloseRequested += loop => { closed = true; accepted = loop; };
        o.Review.AcceptCommand.Execute(null);

        Assert.False(closed);
        Assert.Equal(Rebuilt + "saved — check them, then press Verify or Accept again.", o.Review.Status);
        Assert.Equal(new RoomKey(2, 2), o.Review.Rows[3].Room);
        Assert.Equal("✎", o.Review.Rows[3].StatusText);

        o.Review.AcceptCommand.Execute(null);

        Assert.True(closed);
        Assert.Equal(Shape(never), Shape(accepted));
    }

    [Fact]
    public void AWaitingStartRoom_AStashTickMadeSinceThePickIsSaidLost_AndNoStashRoomIsAdded()
    {
        Opened o = Open();
        PickOtherStart(o, Other, _opened, away: true, r => r.Rows[3].MarkStash = true);
        RoomKey ticked = o.Review.Rows[3].Room!.Value;

        bool closed = false;
        o.Review.CloseRequested += _ => closed = true;
        o.Review.AcceptCommand.Execute(null);

        Assert.False(closed);
        Assert.EndsWith(" The command, delay or ticks set on 1 step(s) were not carried over.", o.Review.Status);
        Assert.False(o.Review.Rows[3].MarkStash);
        Assert.False(o.Filter.IsStash(ticked));

        o.Review.AcceptCommand.Execute(null);

        Assert.True(closed);
        Assert.False(o.Filter.IsStash(ticked));
        Assert.False(o.Filter.IsStash(o.Review.Rows[3].Room!.Value));
    }

    // A room typed into an added row while the other game data's rooms were loaded
    // was named from them.
    [Fact]
    public void ARoomTypedIntoAnAddedRowUnderOtherGameData_IsNamedFromItsOwnOnVerify()
    {
        Opened o = Open();

        o.Cache.SwitchSet("beta");
        o.Graph.OnActiveSetChanged("beta");
        o.Loops.LoadAll(Other);
        o.Review.InsertBelowCommand.Execute(o.Review.Rows[0]);
        MpTranslatedRowViewModel added = o.Review.Rows[1];
        added.RoomText = "1/1";
        Assert.StartsWith("Elsewhere", added.RoomName);

        o.Cache.SwitchSet("alpha");
        o.Graph.OnActiveSetChanged("alpha");
        o.Loops.LoadAll(_opened);
        o.Review.VerifyCommand.Execute(null);

        Assert.Same(added, o.Review.Rows[1]);
        Assert.StartsWith("Start", added.RoomName);
    }
}
