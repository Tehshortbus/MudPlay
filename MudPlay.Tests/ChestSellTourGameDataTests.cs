using System;
using System.Collections.Generic;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// The Chest Offload sell tour walks to its shops by room number. A switch to another
// game-data set cancels it in whichever phase it is: walking to a shop, or selling
// at one, where the walker's stop doesn't reach it and it would walk on to the next
// shop when the sales came in.
public sealed class ChestSellTourGameDataTests : IDisposable
{
    private const string Reason = "game data changed from 'alpha' to 'beta'";
    private static readonly RoomKey Jeweler = new(1, 100);
    private static readonly RoomKey Weapons = new(1, 200);

    private readonly InventoryManager _inv = new(log: null, itemWeightResolver: null, slotResolver: null);
    // The game's output, fed the way the client receives it.
    private readonly ServerFeed _feed = new();
    private readonly Dictionary<string, int> _fromChests = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _sent = new();
    private readonly List<RoomKey> _walks = new();
    private readonly List<Action> _scheduled = new();
    private int? _shopHere;
    private readonly ChestSellTour _tour;

    public ChestSellTourGameDataTests()
    {
        _inv.AttachLineExtractor(_feed.Lines);
        _tour = new ChestSellTour(
            currentShop: () => _shopHere,
            goWalk: room => { _walks.Add(room); return System.Threading.Tasks.Task.FromResult(true); },
            chestCount: name => _fromChests.GetValueOrDefault(name),
            sendPaced: _sent.AddRange,
            isParadigm: () => true,
            inventory: _inv,
            schedule: (_, a) => _scheduled.Add(a),
            post: a => a());
    }

    public void Dispose()
    {
        _tour.Dispose();
        _inv.Dispose();
    }

    private void Feed(string text) => _feed.Line(text);

    private void StartTwoShopTour()
    {
        _fromChests["moonstone"] = 2;
        _fromChests["war hammer"] = 1;
        _tour.Start(new[]
        {
            new ChestSellTour.Stop(Jeweler, 1, "Jeweler", new[] { ("moonstone", 2) }),
            new ChestSellTour.Stop(Weapons, 2, "Weapons", new[] { ("war hammer", 1) }),
        });
    }

    private const string Stopped = "Sell tour stopped: " + Reason + " — nothing more sold.";

    [Fact]
    public void WalkingToAShop_TheTourIsCancelledWithTheReason()
    {
        StartTwoShopTour();
        Assert.Equal(new[] { Jeweler }, _walks);

        _tour.Cancel(Reason);
        // The walker's own stop arrives after: the tour is over, and its line stays.
        _tour.OnWalkerEvent(new WalkEvent(WalkEventKind.Stopped, Reason, Jeweler));

        Assert.False(_tour.IsRunning);
        Assert.Equal(Stopped, _tour.Status);
        Assert.Equal(new[] { Jeweler }, _walks);
        Assert.Empty(_sent);
    }

    [Fact]
    public void SellingAtAShop_TheTourIsCancelled_AndTheSalesComingInWalkNowhere()
    {
        StartTwoShopTour();
        _shopHere = 1;
        _tour.OnWalkerEvent(new WalkEvent(WalkEventKind.Finished, "", Jeweler));
        Assert.Equal(new[] { "sell 2 moonstone" }, _sent);

        _tour.Cancel(Reason);

        Assert.False(_tour.IsRunning);
        Assert.Equal(Stopped, _tour.Status);

        // The sale confirms, and the quiet timer the sale armed fires: neither moves on.
        Feed("You sold 2 moonstone for 400 copper farthings.");
        foreach (Action timer in _scheduled.ToArray()) timer();

        Assert.Equal(new[] { Jeweler }, _walks);
        Assert.Equal(new[] { "sell 2 moonstone" }, _sent);
        Assert.Equal(Stopped, _tour.Status);
    }

    [Fact]
    public void WithNoTourRunning_TheCancelSaysNothing()
    {
        _tour.Cancel(Reason);

        Assert.Equal("", _tour.Status);
    }
}
