using System.Text;
using FujinTerm.Game.Cash;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 9.E follow-up — <see cref="StashRoomManager"/> on-entry stash
/// dispatch driven by user-marked rooms from
/// <see cref="CharacterProfile.StashRooms"/> + per-currency
/// keep-on-hand rules on <see cref="CashSettings"/>.
/// </summary>
public sealed class StashRoomManagerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public ProfileService Profile { get; } = new();
        public CashManager Cash { get; }
        public StashRoomManager Stash { get; }
        public List<byte[]> Sent { get; } = new();
        public CashSettings CashSettings { get; set; } = new();
        public bool AutoGetCashEnabled { get; set; } = true;
        public List<(RoomKey Room, IReadOnlyList<(string Currency, long Amount)> Dispatch)> Executed { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Profile.LoadBlank();
            Cash = new CashManager(Router,
                readSettings: () => CashSettings,
                isEnabled: () => true,
                log: Log);
            Stash = new StashRoomManager(Cash, Profile,
                readCash: () => CashSettings,
                isEnabled: () => AutoGetCashEnabled,
                log: Log);
            Stash.SetWireSender(b => Sent.Add(b));
            Stash.StashExecuted += (r, d) => Executed.Add((r, d));
        }

        public void MarkRoomAsStash(int map, int room)
        {
            CharacterProfile p = Profile.Current!;
            p.StashRooms ??= new List<RoomRef>();
            p.StashRooms.Add(new RoomRef(map, room));
        }

        public void Feed(string line)
        {
            Router.Dispatch(new LineExtractor.EmittedLine(
                line, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false));
        }

        public IEnumerable<string> SentLines() =>
            Sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r'));

        public void Dispose()
        {
            Stash.Dispose();
            Cash.Dispose();
        }
    }

    [Fact]
    public void Enter_MatchingRoom_DumpsAll_WhenNoKeep()
    {
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.Feed("You picked up 500 gold pieces.");

        h.Stash.NoteRoomEntered(new RoomKey(1, 42));

        Assert.Single(h.Sent);
        Assert.Equal("hide 500 gold", h.SentLines().First());
    }

    [Fact]
    public void Enter_MatchingRoom_KeepsConfiguredAmount()
    {
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.CashSettings.KeepGoldOnHand = 100;
        h.Feed("You picked up 500 gold pieces.");

        h.Stash.NoteRoomEntered(new RoomKey(1, 42));

        Assert.Single(h.Sent);
        Assert.Equal("hide 400 gold", h.SentLines().First());
    }

    [Fact]
    public void Enter_HeldAtOrBelowKeep_NoDispatch()
    {
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.CashSettings.KeepGoldOnHand = 100;
        h.Feed("You picked up 80 gold pieces.");

        h.Stash.NoteRoomEntered(new RoomKey(1, 42));

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Enter_NonMatchingRoom_NoDispatch()
    {
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.Feed("You picked up 500 gold pieces.");

        h.Stash.NoteRoomEntered(new RoomKey(2, 99));

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Enter_MultipleCurrencies_DispatchesEach()
    {
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.CashSettings.KeepGoldOnHand     = 100;
        h.CashSettings.KeepPlatinumOnHand = 10;
        h.Feed("You picked up 300 gold pieces.");
        h.Feed("You picked up 50 platinum pieces.");

        h.Stash.NoteRoomEntered(new RoomKey(1, 42));

        Assert.Equal(2, h.Sent.Count);
        List<string> lines = h.SentLines().ToList();
        Assert.Contains("hide 200 gold", lines);
        Assert.Contains("hide 40 platinum", lines);
        Assert.Equal(2, h.Executed[0].Dispatch.Count);
    }

    [Fact]
    public void AutoGetCashOff_NoDispatch()
    {
        using Harness h = new() { AutoGetCashEnabled = false };
        h.MarkRoomAsStash(1, 42);
        h.Feed("You picked up 100 gold pieces.");

        h.Stash.NoteRoomEntered(new RoomKey(1, 42));

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void NoStashRoomsConfigured_NoDispatch()
    {
        using Harness h = new();
        h.Feed("You picked up 100 gold pieces.");

        h.Stash.NoteRoomEntered(new RoomKey(1, 42));

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void SecondVisit_AfterServerConfirms_NoReDispatch()
    {
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.CashSettings.KeepGoldOnHand = 100;
        h.Feed("You picked up 500 gold pieces.");

        h.Stash.NoteRoomEntered(new RoomKey(1, 42));
        Assert.Single(h.Sent);

        h.Feed("You hid 400 gold pieces.");
        Assert.Equal(100, h.Cash.HeldCoin("gold"));

        h.Stash.NoteRoomEntered(new RoomKey(1, 42));
        Assert.Single(h.Sent);
    }
}
