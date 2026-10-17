using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

public sealed class ChestSellTourTests : IDisposable
{
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
    // Whether a walk the tour asks for gets under way (false: the route picker was
    // cancelled, a held trip wasn't resumed).
    private bool _walksStart = true;
    private readonly ChestSellTour _tour;

    public ChestSellTourTests()
    {
        _inv.AttachLineExtractor(_feed.Lines);
        _tour = new ChestSellTour(
            currentShop: () => _shopHere,
            goWalk: room => { _walks.Add(room); return System.Threading.Tasks.Task.FromResult(_walksStart); },
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

    private void Arrive(RoomKey room, int shop)
    {
        _shopHere = shop;
        _tour.OnWalkerEvent(new WalkEvent(WalkEventKind.Finished, "", room));
    }

    // The route picker cancelled (or a held trip not resumed): no walker event ever
    // comes, and the tour used to sit on "walking to…" until someone cancelled it.
    [Fact]
    public void AWalkThatNeverStarts_EndsTheTour()
    {
        _fromChests["moonstone"] = 2;
        _walksStart = false;

        _tour.Start(new[] { new ChestSellTour.Stop(Jeweler, 1, "Jeweler", new[] { ("moonstone", 2) }) });

        Assert.Equal(new[] { Jeweler }, _walks);
        Assert.False(_tour.IsRunning);
        Assert.Equal("The walk to Jeweler didn't start — nothing more sold.", _tour.Status);
        Assert.Empty(_sent);
    }

    [Fact]
    public void NeverSellsMoreThanTheChestsGave()
    {
        // The plan asks for 5 moonstone, but the chests only gave 2 — the other 3 were
        // the player's own. Only 2 may be sold. A ruby the chests never gave sells 0.
        _fromChests["moonstone"] = 2;
        _shopHere = 1;

        _tour.Start(new[] { new ChestSellTour.Stop(Jeweler, 1, "Jeweler", new[] { ("moonstone", 5), ("ruby", 1) }) });

        Assert.Equal(new[] { "sell 2 moonstone" }, _sent);
    }

    [Fact]
    public void WalksToEachShopInTurn_SellsOnArrival_MovesOnWhenTheSalesConfirm()
    {
        _fromChests["moonstone"] = 2;
        _fromChests["war hammer"] = 1;
        _tour.Start(new[]
        {
            new ChestSellTour.Stop(Jeweler, 1, "Jeweler", new[] { ("moonstone", 2) }),
            new ChestSellTour.Stop(Weapons, 2, "Weapons", new[] { ("war hammer", 1) }),
        });

        Assert.Equal(new[] { Jeweler }, _walks);
        Assert.Empty(_sent);                          // nothing sold on the way

        Arrive(Jeweler, 1);
        Assert.Equal(new[] { "sell 2 moonstone" }, _sent);

        Feed("You sold 2 moonstone for 400 copper farthings.");
        Assert.Equal(new[] { Jeweler, Weapons }, _walks);

        Arrive(Weapons, 2);
        Feed("You sold war hammer for 90 copper farthings.");
        Assert.Equal(new[] { "sell 2 moonstone", "sell war hammer" }, _sent);
        Assert.False(_tour.IsRunning);
        Assert.Equal("Sell tour done.", _tour.Status);
    }

    [Fact]
    public void AShopThatConfirmsNothing_IsLeftAfterTheQuietWindow()
    {
        _fromChests["moonstone"] = 2;
        _fromChests["war hammer"] = 1;
        _shopHere = 1;
        _tour.Start(new[]
        {
            new ChestSellTour.Stop(Jeweler, 1, "Jeweler", new[] { ("moonstone", 2) }),
            new ChestSellTour.Stop(Weapons, 2, "Weapons", new[] { ("war hammer", 1) }),
        });

        _scheduled.Last()();                          // no "You sold" — the quiet window lapses

        Assert.Equal(new[] { Weapons }, _walks);
    }

    [Fact]
    public void AStoppedWalk_EndsTheTour_WithoutSelling()
    {
        _fromChests["moonstone"] = 2;
        _tour.Start(new[] { new ChestSellTour.Stop(Jeweler, 1, "Jeweler", new[] { ("moonstone", 2) }) });

        _tour.OnWalkerEvent(new WalkEvent(WalkEventKind.Stopped, "user", Jeweler));

        Assert.False(_tour.IsRunning);
        Assert.Empty(_sent);
    }

    [Fact]
    public void AShopWhoseItemsWereRemoved_IsSkipped_NotWalkedTo()
    {
        // The tunic was taken off the list (✕) after the tour was planned: its shop is
        // never walked to, and the tour goes straight to the next one.
        _fromChests["moonstone"] = 2;
        _tour.Start(new[]
        {
            new ChestSellTour.Stop(Weapons, 2, "Armoury", new[] { ("tunic", 1) }),
            new ChestSellTour.Stop(Jeweler, 1, "Jeweler", new[] { ("moonstone", 2) }),
        });

        Assert.Equal(new[] { Jeweler }, _walks);
    }

    [Fact]
    public void AnItemRemovedMidTour_IsNotSold()
    {
        _fromChests["moonstone"] = 2;
        _fromChests["ruby"] = 1;
        _tour.Start(new[] { new ChestSellTour.Stop(Jeweler, 1, "Jeweler", new[] { ("moonstone", 2), ("ruby", 1) }) });

        _fromChests.Remove("ruby");                   // ✕ on the ruby while walking
        Arrive(Jeweler, 1);

        Assert.Equal(new[] { "sell 2 moonstone" }, _sent);
    }

    [Fact]
    public void ArrivingSomewhereElse_SellsNothing()
    {
        _fromChests["moonstone"] = 2;
        _tour.Start(new[] { new ChestSellTour.Stop(Jeweler, 1, "Jeweler", new[] { ("moonstone", 2) }) });

        Arrive(Jeweler, shop: 7);                     // the room isn't that shop after all

        Assert.Empty(_sent);
        Assert.False(_tour.IsRunning);
    }
}
