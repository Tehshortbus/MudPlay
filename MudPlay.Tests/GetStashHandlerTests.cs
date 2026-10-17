using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// @get-stash: the member's side of a leader's stash transfer. One command makes it
// search, read the pile with pickup held, take what its weight limits allow, and
// only then answer — the answer is the leader's cue that this member is loaded.
public sealed class GetStashHandlerTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Harness
    {
        public RemoteCommandManager Engine = null!;
        // The router the engine's chat lines arrive through.
        private readonly MessageRouter _router = new();
        // What Engine put on the wire, through the sender the client binds.
        public readonly List<byte[]> EngineWire = new();
        public PlayerDatabase Players = new(new ProfileService(), () => null);
        public List<string> Sent = new();
        public List<long?> Limits = new();
        public List<bool> AutoGetCashWrites = new();
        public long Purse = 100;
        public long PileShows;
        public long CanCarry = long.MaxValue;
        public bool CollectionInUse;
        private readonly List<Action> _timers = new();

        public void FireTimers()
        {
            Action[] due = _timers.ToArray();
            _timers.Clear();
            foreach (Action a in due) a();
        }

        public Harness()
        {
            DefaultPatterns.Seed(_router);
            Engine = new RemoteCommandManager(new ChatRouter(_router), new PartyState(), Players);
            Engine.SetWireSender(EngineWire.Add);
            _ = new GetStashHandler(Engine,
                onHandCopper: () => Purse,
                send: Sent.Add,
                armTimer: (_, a) => _timers.Add(a),
                limitCollection: Limits.Add,
                surveyedCopper: () => PileShows,
                collectSurveyed: copper => Purse += Math.Min(copper, CanCarry),
                forceAutoGetCash: AutoGetCashWrites.Add,
                collectionInUse: () => CollectionInUse);
            Players.RecordObservation("Bob", null, null, null, null, null, null, Now);
            Players.EditCustomization("Bob", new PlayerCustomization(RemoteControls: PlayerRemoteControls.ExecuteCommands));
        }

        public void Ask() => ChatLines.Hear(_router, ChatChannel.TelepathIncoming, "Bob", "@get-stash");

        public List<string> Replies() => EngineWire
            .Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r'))
            .Select(w => w[(w.IndexOf('{') + 1)..w.LastIndexOf('}')])
            .ToList();
    }

    [Fact]
    public void SearchesThenTakesThePile_AndAnswersOnlyOnceItIsLoaded()
    {
        Harness h = new() { PileShows = 30_000 };
        h.Ask();

        Assert.Equal(new[] { "sea" }, h.Sent);
        Assert.Equal(new long?[] { 0 }, h.Limits);        // pickup held while the pile is read
        Assert.Equal(new[] { true }, h.AutoGetCashWrites);
        Assert.Empty(h.Replies());

        h.FireTimers();                                   // the search answered
        Assert.Empty(h.Replies());                        // still taking
        h.FireTimers();                                   // the gets landed

        Assert.Equal("ok - took 3 platinum", Assert.Single(h.Replies()));
        Assert.Equal(30_100, h.Purse);
        Assert.Null(h.Limits[^1]);                        // ceiling lifted
        Assert.False(h.AutoGetCashWrites[^1]);            // toggle put back
    }

    [Fact]
    public void TakesOnlyWhatItsWeightLimitsAllow_AndSaysWhatItLeft()
    {
        Harness h = new() { PileShows = 30_000, CanCarry = 10_000 };
        h.Ask();
        h.FireTimers();
        h.FireTimers();

        Assert.Equal("ok - took 1 platinum, left 2 platinum", Assert.Single(h.Replies()));
    }

    [Fact]
    public void NothingFound_StillAnswersOk()
    {
        Harness h = new() { PileShows = 0 };
        h.Ask();
        h.FireTimers();

        Assert.Equal("ok - found no coin here", Assert.Single(h.Replies()));
        Assert.Null(h.Limits[^1]);
    }

    [Fact]
    public void AtTheWeightLimit_AnswersOkWithNothingTaken()
    {
        Harness h = new() { PileShows = 30_000, CanCarry = 0 };
        h.Ask();
        h.FireTimers();
        h.FireTimers();

        Assert.Equal("ok - at my coin weight limit, took nothing", Assert.Single(h.Replies()));
    }

    // Our own funding errand or stash transfer holds the same pickup ceiling.
    [Fact]
    public void BusyWithAnErrandOfItsOwn_Refuses()
    {
        Harness h = new() { PileShows = 30_000, CollectionInUse = true };
        h.Ask();

        Assert.Empty(h.Sent);
        Assert.StartsWith("busy", Assert.Single(h.Replies()));
    }

    [Fact]
    public void WithoutPermission_DoesNothing()
    {
        Harness h = new() { PileShows = 30_000 };
        h.Players.EditCustomization("Bob", new PlayerCustomization(RemoteControls: PlayerRemoteControls.None));
        h.Ask();

        Assert.Empty(h.Sent);
    }
}
