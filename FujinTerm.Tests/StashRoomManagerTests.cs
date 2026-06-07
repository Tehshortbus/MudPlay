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
/// dispatch driven by user-marked rooms + per-currency keep-at-least
/// rules.
/// </summary>
public sealed class StashRoomManagerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public CashManager Cash { get; }
        public StashRoomManager Stash { get; }
        public List<byte[]> Sent { get; } = new();
        public StashRoomSettings Settings { get; set; } = new();
        public bool AutoGetCashEnabled { get; set; } = true;
        public List<(StashRoom Room, IReadOnlyList<(string Currency, long Amount)> Dispatch)> Executed { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Cash = new CashManager(Router,
                readSettings: () => new CashSettings(),
                isEnabled: () => true,
                log: Log);
            Stash = new StashRoomManager(Cash,
                readSettings: () => Settings,
                isEnabled: () => AutoGetCashEnabled,
                log: Log);
            Stash.SetWireSender(b => Sent.Add(b));
            Stash.StashExecuted += (r, d) => Executed.Add((r, d));
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

    private static StashRoom MakeRoom(int map, int room, string name,
                                       params (string Currency, long Keep)[] rules)
    {
        return new StashRoom
        {
            Room = new RoomRef(map, room),
            DisplayName = name,
            CurrencyRules = rules
                .Select(r => new StashCurrencyRule { Currency = r.Currency, KeepAtLeast = r.Keep })
                .ToList(),
        };
    }

    // ----- entry dispatch ---------------------------------------------

    [Fact]
    public void Enter_MatchingRoom_DispatchesHide()
    {
        using Harness h = new();
        h.Settings.Rooms.Add(MakeRoom(1, 42, "Sewers cache",
            ("gold", 100)));
        h.Feed("You picked up 500 gold pieces.");      // held=500

        h.Stash.NoteRoomEntered(new RoomKey(1, 42));

        Assert.Single(h.Sent);
        Assert.Equal("hide 400 gold", h.SentLines().First());
        Assert.Single(h.Executed);
        Assert.Equal(400, h.Executed[0].Dispatch[0].Amount);
    }

    [Fact]
    public void Enter_MatchingRoom_KeepZero_DumpsAll()
    {
        using Harness h = new();
        h.Settings.Rooms.Add(MakeRoom(1, 42, "Cache",
            ("gold", 0)));
        h.Feed("You picked up 250 gold pieces.");

        h.Stash.NoteRoomEntered(new RoomKey(1, 42));

        Assert.Single(h.Sent);
        Assert.Equal("hide 250 gold", h.SentLines().First());
    }

    [Fact]
    public void Enter_HeldAtOrBelowKeep_NoDispatch()
    {
        using Harness h = new();
        h.Settings.Rooms.Add(MakeRoom(1, 42, "Cache",
            ("gold", 100)));
        h.Feed("You picked up 80 gold pieces.");        // 80 <= 100 → skip

        h.Stash.NoteRoomEntered(new RoomKey(1, 42));

        Assert.Empty(h.Sent);
        Assert.Empty(h.Executed);
    }

    [Fact]
    public void Enter_NonMatchingRoom_NoDispatch()
    {
        using Harness h = new();
        h.Settings.Rooms.Add(MakeRoom(1, 42, "Cache",
            ("gold", 0)));
        h.Feed("You picked up 500 gold pieces.");

        h.Stash.NoteRoomEntered(new RoomKey(2, 99));    // different room

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Enter_MultipleCurrencies_DispatchesEach()
    {
        using Harness h = new();
        h.Settings.Rooms.Add(MakeRoom(1, 42, "Cache",
            ("gold", 100),
            ("platinum", 10)));
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
    public void Enter_OnlyOverThresholdCurrencyDispatched()
    {
        // Two rules — only gold is over, platinum is exactly at
        // keep — only one hide goes out.
        using Harness h = new();
        h.Settings.Rooms.Add(MakeRoom(1, 42, "Cache",
            ("gold",     100),
            ("platinum", 50)));
        h.Feed("You picked up 300 gold pieces.");
        h.Feed("You picked up 50 platinum pieces.");

        h.Stash.NoteRoomEntered(new RoomKey(1, 42));

        Assert.Single(h.Sent);
        Assert.Equal("hide 200 gold", h.SentLines().First());
    }

    // ----- master + empty settings ----------------------------------

    [Fact]
    public void AutoGetCashOff_NoDispatch()
    {
        using Harness h = new() { AutoGetCashEnabled = false };
        h.Settings.Rooms.Add(MakeRoom(1, 42, "Cache",
            ("gold", 0)));
        h.Feed("You picked up 100 gold pieces.");

        h.Stash.NoteRoomEntered(new RoomKey(1, 42));

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void EmptySettings_NoDispatch()
    {
        using Harness h = new();
        h.Feed("You picked up 100 gold pieces.");

        h.Stash.NoteRoomEntered(new RoomKey(1, 42));

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void RuleWithEmptyCurrency_Skipped()
    {
        // Malformed rule (empty Currency string) shouldn't crash or
        // emit a bogus `hide N ` command.
        using Harness h = new();
        h.Settings.Rooms.Add(new StashRoom
        {
            Room = new RoomRef(1, 42),
            DisplayName = "Cache",
            CurrencyRules = new()
            {
                new() { Currency = "", KeepAtLeast = 0 },
                new() { Currency = "gold", KeepAtLeast = 0 },
            },
        });
        h.Feed("You picked up 100 gold pieces.");

        h.Stash.NoteRoomEntered(new RoomKey(1, 42));

        Assert.Single(h.Sent);
        Assert.Equal("hide 100 gold", h.SentLines().First());
    }

    // ----- second visit produces no dispatch (after server confirms) -

    [Fact]
    public void SecondVisit_AfterServerConfirms_NoReDispatch()
    {
        // Real flow: enter room → hide 400 gold sent → server replies
        // "You hid 400 gold pieces." → CashManager decrements tally
        // to 100 (the keep floor) → next visit produces no dispatch.
        using Harness h = new();
        h.Settings.Rooms.Add(MakeRoom(1, 42, "Cache",
            ("gold", 100)));
        h.Feed("You picked up 500 gold pieces.");

        h.Stash.NoteRoomEntered(new RoomKey(1, 42));
        Assert.Single(h.Sent);

        // Server confirmation → CashManager decrements held to 100.
        h.Feed("You hid 400 gold pieces.");
        Assert.Equal(100, h.Cash.HeldCoin("gold"));

        h.Stash.NoteRoomEntered(new RoomKey(1, 42));    // visit #2
        Assert.Single(h.Sent);                           // no new dispatch
    }
}
