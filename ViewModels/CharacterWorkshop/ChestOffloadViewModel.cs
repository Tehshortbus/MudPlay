using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Cash;
using MudPlay.Game.GameData;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Views.CharacterWorkshop;

namespace MudPlay.ViewModels.CharacterWorkshop;

// Record Keeping → Chest Offload: a view over ChestOpenTracker's list of chest loot
// (which lives on past the Workshop and is saved on the profile) — open the containers
// you're holding, see the coin and items the chests gave grouped into the fewest shops
// with a charm picker and per-item sell quantities, and sell (walking to the shop first
// when you aren't standing in it), drop, or take items off the list. A Drop is a
// discard, so it goes out as the auto-discard engine's would: hidden instead when
// Settings → Other "Hide items when discarding" is on.
public sealed partial class ChestOffloadViewModel : WorkshopSectionViewModel
{
    public const string SectionId = "chestoffload";
    public const string SectionTitle = "Chest Offload";
    public override string Id => SectionId;
    public override string Title => SectionTitle;

    private Control? _view;
    public override Control View => _view ??= new ChestOffloadSectionView { DataContext = this };

    // A column of shop groups reads best at a set width; left to fit its content
    // the window would stretch to the longest item line.
    public override Size? PreferredSize => new Size(660, 780);

    private readonly InventoryManager _inventory;
    private readonly ShopStockIndex _shops;
    private readonly RoomGraphManager _rooms;
    private readonly ItemNameStore _itemNames;
    private readonly PlayerStats _stats;
    private readonly GameDataCache _gameData;
    private readonly RoomTracker _tracker;
    private readonly BfsMapper _bfs;
    private readonly MovementFilter _movement;
    private readonly CurrencyNaming _naming;
    private readonly Action<string> _send;
    // Discards go out through the sweeps' pacer: a whole shop group of copies, one
    // `drop` or `hide` per copy on Stock, would otherwise overflow the game's
    // command queue.
    private readonly Action<IReadOnlyList<string>> _sendPaced;
    // Owns the hide-or-drop choice for a discard, and words the command.
    private readonly AutoDiscardManager _discard;
    private readonly Action<RoomKey> _queueWalk;
    private readonly ChestSellTour _tour;
    // Walking time for a number of steps (Auto-Lair's live travel model).
    private readonly Func<int, TimeSpan> _hopEta;
    private readonly ChestOpenTracker _chests;
    private readonly LogDiagnosticState? _diagnostics;
    private readonly LogService? _log;
    private const string LogCategory = "ChestOffload";

    // Coin shown while the Simulate Chest test button drives the view.
    private CurrencyHoldings _simCoin = CurrencyHoldings.Empty;

    private bool _simulating;
    // The tab's own note on the status line: a refused hide (with the item it is
    // about, so it can come down when that hide is over) or the answer to the last
    // button press (no item; it comes down on the next press).
    private string _note = "";
    private string? _noteItem;
    private Dictionary<int, Room> _shopRoom = new();               // shop id → serving room (first found), rebuilt each render
    // Bumped by each render, a game-data reload and Dispose, so a trip worked out for
    // an earlier one is dropped when it comes back.
    private int _routeGeneration;
    private CancellationTokenSource? _routeCancel;
    // The trip being worked out: done once its order and steps are on the groups, or
    // it was called off. A Sell Tour waits for it, to walk the shops in trip order.
    private Task _routeLanded = Task.CompletedTask;
    // The last trip's place and steps by shop, which a re-render's groups keep until
    // their own trip comes back.
    private readonly Dictionary<int, (int Place, string Steps)> _lastTrip = new();
    // Cancelled when the tab closes: no distance lookup runs on, and no Sell Tour
    // starts, for a tab that is gone.
    private readonly CancellationTokenSource _lifetime = new();

    [ObservableProperty] private int _charm;
    [ObservableProperty] private string _sellTotal = "—";
    // What a Sell is doing when it isn't instant: walking to the shop, or why it
    // stopped. Also where a refused hide, or a Drop that sent nothing, is named.
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSellStatus))] private string _sellStatus = "";
    public bool HasSellStatus => SellStatus.Length > 0;
    [ObservableProperty] private bool _isTourRunning;
    // Say each opened chest's contents to the room. The tracker keeps it on the profile.
    [ObservableProperty] private bool _sayLootToRoom;
    // Coin the chest gave, one denomination per line, most-expensive first.
    public ObservableCollection<string> CoinGains { get; } = new();
    public ObservableCollection<ChestContainerRow> Containers { get; } = new();
    public ObservableCollection<ChestOffloadShopGroup> ShopGroups { get; } = new();
    public ObservableCollection<ChestOffloadItemRow> Unsellable { get; } = new();

    public bool HasContainers => Containers.Count > 0;
    public bool HasCoinGain => CoinGains.Count > 0;
    public bool HasLoot => ShopGroups.Count > 0 || Unsellable.Count > 0;
    public bool HasUnsellable => Unsellable.Count > 0;

    public ChestOffloadViewModel() : this(
        AppServices.Current.Inventory, AppServices.Current.ShopStock, AppServices.Current.RoomGraph,
        AppServices.Current.ItemNames, AppServices.Current.PlayerStats, AppServices.Current.GameData,
        AppServices.Current.RoomTracker, AppServices.Current.Bfs, AppServices.Current.Movement,
        AppServices.Current.Currency,
        cmd => AppServices.Current.SendGameCommand(cmd), AppServices.Current.QueueWalkTo,
        cmds => AppServices.Current.InventoryAction.SendPaced(cmds),
        AppServices.Current.AutoDiscard,
        AppServices.Current.ChestOpens, AppServices.Current.ChestSellTour,
        hops => AppServices.Current.AutoLair.TravelCostModel.EstimateTravel(hops))
    { }

    public ChestOffloadViewModel(
        InventoryManager inventory, ShopStockIndex shops, RoomGraphManager rooms,
        ItemNameStore itemNames, PlayerStats stats, GameDataCache gameData,
        RoomTracker tracker, BfsMapper bfs, MovementFilter movement, CurrencyNaming naming,
        Action<string> send, Action<RoomKey> queueWalk,
        Action<IReadOnlyList<string>> sendPaced, AutoDiscardManager discard,
        ChestOpenTracker chests, ChestSellTour tour,
        Func<int, TimeSpan> hopEta)
    {
        _sendPaced = sendPaced;
        _discard = discard;
        _chests = chests;
        _inventory = inventory;
        _shops = shops;
        _rooms = rooms;
        _itemNames = itemNames;
        _stats = stats;
        _gameData = gameData;
        _tracker = tracker;
        _bfs = bfs;
        _movement = movement;
        _naming = naming;
        _send = send;
        _queueWalk = queueWalk;
        _tour = tour;
        _hopEta = hopEta;

        _charm = stats.Charm > 0 ? stats.Charm : 50;

        _chests.Changed += OnChestsChanged;
        _inventory.Changed += OnInventoryChanged;
        _rooms.GraphReloaded += OnGraphReloaded;
        _tour.Changed += OnTourChanged;
        _sellStatus = _tour.Status;
        _isTourRunning = _tour.IsRunning;
        _sayLootToRoom = _chests.SayLootToRoom;
        // The tracker lowers the list on the game's OWN confirmed sell / drop / hide /
        // give lines, window open or not; the rows here follow it, so a refused sale
        // changes nothing and a partial one reduces only that item.
        _chests.ItemLeft += OnItemLeft;
        _discard.HideRefused += OnHideRefused;
        _discard.HeldHideEnded += OnHeldHideEnded;

        // Test-only "Simulate Chest" button, revealed by the Log pane's "Simulate
        // Chest button" toggle (session-only, off by default). Mirror its live value.
        _diagnostics = AppServices.CurrentOrNull?.LogDiagnostics;
        if (_diagnostics is not null) _diagnostics.Changed += OnDiagnosticsChanged;
        _log = AppServices.CurrentOrNull?.Log;

        RebuildLoot();
        _log?.Info(LogCategory,
            $"tab opened — charm {_charm}, {Containers.Count} container(s) held, " +
            $"{ShopGroups.Sum(g => g.Items.Count) + Unsellable.Count} item(s) on the list");
    }

    // Reveal the test-only "Simulate Chest" button — gated by the Log pane toggle,
    // so a normal user never sees it. Hidden by default (session-only + off).
    public bool ShowSimulateChest => _diagnostics?.ShowSimulateChest ?? false;

    private void OnDiagnosticsChanged()
        => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(ShowSimulateChest)));

    private void RebuildContainers(InventorySnapshot snap)
    {
        using PerformanceMonitor.Scope? scope = AppServices.CurrentOrNull?.Performance.Measure("Chest Offload containers");
        Containers.Clear();
        foreach (string token in snap.CarriedItems)
        {
            (int count, string name) = CountedCommand.SplitLeadingCount(token);
            if (_itemNames.FindByName(name) is int n && _itemNames.ItemTypeOf(n) == ChestOffloadPlanner.ContainerItemType)
                Containers.Add(new ChestContainerRow(name, count, () => OpenContainer(name)));
        }
        OnPropertyChanged(nameof(HasContainers));
    }

    private void OpenContainer(string name)
    {
        _simulating = false;   // a real open returns to the tracker's list
        _chests.Open(name);
    }

    // The tracker's list changed as a whole (an open settled, a row was taken off, the
    // list was cleared, or its last item left and took the coin tally with it). A
    // sale, drop, hide or give that leaves items listed doesn't come through here —
    // it moves its one row (OnItemLeft) so the user's sell quantities and ⇄ shop
    // moves survive. Simulated chests keep their own view.
    private void OnChestsChanged()
    {
        using PerformanceMonitor.Scope? scope = AppServices.CurrentOrNull?.Performance.Measure("Chest Offload list changed");
        SayLootToRoom = _chests.SayLootToRoom;   // a profile swap brings its own setting
        if (!_simulating) RebuildLoot();
    }

    partial void OnSayLootToRoomChanged(bool value) => _chests.SayLootToRoom = value;

    // The containers held can change any time (a chest picked up or opened).
    private void OnInventoryChanged() => Dispatcher.UIThread.Post(() => RebuildContainers(_inventory.Snapshot));

    // The game confirmed `count` of a listed item gone (the player's own "You sold …",
    // "You dropped …", "You hid …" or a give) and the tracker has lowered its list.
    // Reduce that row and drop it at zero, leaving every other row's edits intact.
    private void OnItemLeft(string name, int count, ChestOpenTracker.Departure how)
        => ReconcileConfirmed(name, count, how);

    // A full room refused a discard's hide. The row is left as it is (nothing
    // confirmed, so the item is still carried); AutoDiscardManager holds the copy
    // and sends the hide again in each new room until it lands. A running tour
    // keeps the status line for itself.
    private void OnHideRefused(string name) => Dispatcher.UIThread.Post(() =>
    {
        if (_simulating || FindRow(name) is null) return;
        _log?.Info(LogCategory, $"hide of {name} refused (no room to hide it here) — row kept, tried again in the next room");
        if (_tour.IsRunning) return;
        ShowNote($"No room to hide {name} here — it stays in your pack and is tried again in the next room you enter.", name);
    });

    // The held hide is over, landed or called off: its note no longer holds.
    private void OnHeldHideEnded(string name) => Dispatcher.UIThread.Post(() =>
    {
        if (string.Equals(_noteItem, name, StringComparison.OrdinalIgnoreCase)) ClearNote();
    });

    private void ShowNote(string text, string? heldItem = null)
    {
        _note = text;
        _noteItem = heldItem;
        SellStatus = text;
    }

    // Take the tab's note down, unless something else has taken the line since.
    private void ClearNote()
    {
        if (_note.Length > 0 && SellStatus == _note) SellStatus = "";
        _note = "";
        _noteItem = null;
    }

    // A button was pressed: what the last press was told is stale. A refused-hide
    // note stays, since its hide is still waiting.
    private void ClearActionNote()
    {
        if (_noteItem is null) ClearNote();
    }

    // The player has other plans for these items (a sale, off the list): a hide of
    // theirs still waiting for a room would take the copy first, or take one of
    // the player's own once the chest's is gone.
    private void CallOffHeldHides(IEnumerable<string> names)
    {
        foreach (string name in names) _discard.ReleaseHeld(name, int.MaxValue, byHandOnly: true);
    }

    private void ReconcileConfirmed(string name, int count, ChestOpenTracker.Departure how)
    {
        if (_simulating || count <= 0) return;
        using PerformanceMonitor.Scope? scope = AppServices.CurrentOrNull?.Performance.Measure("Chest Offload reconcile", name);
        string did = how.ToString().ToLowerInvariant();
        if (FindRow(name) is not (ChestOffloadShopGroup group, ChestOffloadItemRow row))
        {
            // No shop buys it, so it sits in the list below the shops.
            ChestOffloadItemRow? unsold = Unsellable.FirstOrDefault(
                r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            if (unsold is null) _log?.Debug(LogCategory, $"{did} {count} {name} — no row to move");
            else if (unsold.ApplyDiscarded(count)) RemoveRow(unsold);
            return;
        }

        int before = row.Gained;
        bool empty = how == ChestOpenTracker.Departure.Sold ? row.ApplySold(count) : row.ApplyDiscarded(count);
        // The tracker has logged the confirmation; this is the row's side of it.
        _log?.Debug(LogCategory,
            $"{did} {count} {name} — row held {before}→{row.Gained}" +
            (empty ? " (row cleared)" : $", sell qty now {row.SellQty}"));

        if (empty)
        {
            group.Items.Remove(row);
            if (group.Items.Count == 0) ShopGroups.Remove(group);
        }
        group.Retotal();
        UpdateGrandTotal();
        OnPropertyChanged(nameof(HasLoot));
    }

    // Locate the sellable row for an item by name (chest-loot rows only; the
    // unsellable list carries no sell/drop buttons). First match wins — an item maps
    // to a single shop group at a time.
    private (ChestOffloadShopGroup Group, ChestOffloadItemRow Row)? FindRow(string name)
    {
        foreach (ChestOffloadShopGroup g in ShopGroups)
            foreach (ChestOffloadItemRow r in g.Items)
                if (string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
                    return (g, r);
        return null;
    }

    private void RebuildLoot()
    {
        InventorySnapshot snap = _inventory.Snapshot;
        RebuildContainers(snap);
        RebuildCoinGains(_chests.Coin);
        RenderLoot(_chests.Loot(snap.CarriedItems));
    }

    // Turn a set of (item name, count) gains into the priced, shop-grouped view.
    // Shared by the real inventory diff and the "Simulate Chest" test button.
    private void RenderLoot(IReadOnlyList<(string Name, int Count)> gains)
    {
        using PerformanceMonitor.Scope? scope = AppServices.CurrentOrNull?.Performance.Measure("Chest Offload render");
        // A shop can serve several rooms; take the first (kept as a field so the
        // "sell elsewhere" popup and item moves can look shops up later). UI thread.
        _shopRoom = new Dictionary<int, Room>();
        foreach (Room room in _rooms.Rooms)
            if (room.Shop > 0 && !_shopRoom.ContainsKey(room.Shop))
                _shopRoom[room.Shop] = room;

        var loot = new List<LootItem>();
        foreach ((string name, int count) in gains)
        {
            if (_itemNames.FindByName(name) is not int number) continue;
            if (_itemNames.ItemTypeOf(number) == ChestOffloadPlanner.ContainerItemType) continue;   // don't sell chests
            // A shop no room of the map holds can't be walked to or sold at: an item
            // only it buys is one no shop buys (report paradigm-20261101-080712).
            loot.Add(new LootItem(name, count, BaseCopperOf(number),
                _shops.ShopsSelling(number).Where(_shopRoom.ContainsKey).ToArray()));
        }

        IReadOnlyList<(int Shop, IReadOnlyList<LootItem> Items)> groups =
            ChestOffloadPlanner.GroupByFewestShops(loot, li => li.Shops, out IReadOnlyList<LootItem> noShop);

        // Until this render's trip comes back, the shops the last one ordered keep
        // their place and steps, so a ✕ doesn't scramble the list.
        if (_tracker.State.CurrentRoom is null) _lastTrip.Clear();
        ShopGroups.Clear();
        foreach ((int shopNum, IReadOnlyList<LootItem> items) in groups
                     .Select((g, i) => (Group: g, Index: i))
                     .OrderBy(t => _lastTrip.TryGetValue(t.Group.Shop, out (int Place, string _) last) ? last.Place : int.MaxValue)
                     .ThenBy(t => t.Index)
                     .Select(t => t.Group))
        {
            _shopRoom.TryGetValue(shopNum, out Room? room);
            string steps = _lastTrip.TryGetValue(shopNum, out (int _, string Steps) known) ? known.Steps : "";
            var group = new ChestOffloadShopGroup(
                ShopLabel(shopNum, room), shopNum, ShopLocation(room), steps, room?.Key,
                _queueWalk, SellGroup, DropAllGroup);
            foreach (LootItem li in items)
                group.Items.Add(new ChestOffloadItemRow(li.Name, li.Count, li.BaseCopper,
                    li.Shops, shopNum,
                    it => { GroupOf(it)?.Retotal(); UpdateGrandTotal(); },
                    DropItem, BuildShopChoices, RankShopChoicesAsync, MoveItemToShop, SellItem, RemoveItem));
            group.Reprice(Charm, _gameData.ActiveRealm);
            ShopGroups.Add(group);
        }

        Unsellable.Clear();
        foreach (LootItem li in noShop)
            Unsellable.Add(new ChestOffloadItemRow(li.Name, li.Count, li.BaseCopper, li.Shops, 0, null,
                onDrop: DropItem, onRemove: RemoveItem));

        UpdateGrandTotal();
        OnPropertyChanged(nameof(HasLoot));
        OnPropertyChanged(nameof(HasUnsellable));
        RouteShopGroups();
    }

    // Coin the chest gave as a vertical list, most-expensive denomination first,
    // skipping any that came up empty. Shorthand names; the runic tier uses the
    // active board's word (some realms rename it).
    private void RebuildCoinGains(CurrencyHoldings g)
    {
        CoinGains.Clear();
        if (g.Runic > 0)    CoinGains.Add($"{g.Runic:N0} {_naming.RunicName}");
        if (g.Platinum > 0) CoinGains.Add($"{g.Platinum:N0} plat");
        if (g.Gold > 0)     CoinGains.Add($"{g.Gold:N0} gold");
        if (g.Silver > 0)   CoinGains.Add($"{g.Silver:N0} silver");
        if (g.Copper > 0)   CoinGains.Add($"{g.Copper:N0} copper");
        OnPropertyChanged(nameof(HasCoinGain));
    }

    // Grand total: everything selected across every shop at the current charm.
    private void UpdateGrandTotal()
    {
        long total = 0;
        foreach (ChestOffloadShopGroup group in ShopGroups)
            foreach (ChestOffloadItemRow item in group.Items)
                total += item.LineCopper;
        SellTotal = total > 0 ? ShopPriceCalculator.FormatCopper(total) : "—";
    }

    // Order the shop groups into a short trip (ChestOffloadPlanner.RouteShops: nearest
    // shop first, then the nearest not yet visited from there) and give each its steps
    // from here. Distances use the same routing the walker does, under the
    // MovementFilter, so they honour avoid rooms, usable teleport gates, and
    // item/hazard/boat gates (rope & grapple, ferries…) rather than raw hops. No known
    // current room (offline / unknown position) leaves the grouping order unlabelled.
    //
    // Each distance is a search of the whole map, and a dozen shops asked for about a
    // hundred: on the UI thread one chest open froze the client for 49.7 s (report
    // paradigm-20261026-201217). So the trip is worked out on a worker, as a route
    // card's plan is, mapping each room once (BfsMapper.DistanceMemo), and the groups
    // take their order and steps when it comes back. The rows stay live meanwhile, so
    // a sale or hide confirmed in between still finds its row. Every other distance
    // the tab shows (the Sell Tour's legs, the ⇄ list, a group an item is moved into)
    // is asked on a worker too (DistancesAsync).
    private void RouteShopGroups()
    {
        int generation = NewRouteGeneration();
        if (_tracker.State.CurrentRoom?.Key is not { } here || ShopGroups.Count == 0)
        {
            _routeLanded = Task.CompletedTask;
            return;
        }
        List<(int Shop, RoomKey? Room)> shops = ShopGroups
            .Select(g => (g.Shop, _shopRoom.TryGetValue(g.Shop, out Room? r) ? r.Key : (RoomKey?)null))
            .ToList();
        var cancel = new CancellationTokenSource();
        _routeCancel = cancel;
        _routeLanded = RouteAsync(generation, here, shops, cancel.Token);
    }

    private async Task RouteAsync(int generation, RoomKey here, List<(int Shop, RoomKey? Room)> shops, CancellationToken cancel)
    {
        try
        {
            long started = Stopwatch.GetTimestamp();
            IReadOnlyList<(int Shop, int? Steps)> trip = await Task.Run(() =>
            {
                using PerformanceMonitor.Scope? timed = AppServices.CurrentOrNull?.Performance.Measure("Chest Offload trip");
                Func<RoomKey, RoomKey, int?> memo = _bfs.DistanceMemo(_movement);
                return ChestOffloadPlanner.RouteShops(here, shops, (a, b) =>
                {
                    cancel.ThrowIfCancellationRequested();
                    return memo(a, b);
                });
            }, cancel);
            TimeSpan took = Stopwatch.GetElapsedTime(started);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _routeGeneration) return;
                ApplyRoute(here, trip);
                _log?.Debug(LogCategory,
                    $"trip to {trip.Count} shop(s) worked out in {took.TotalMilliseconds:F0} ms, off the UI thread");
            });
        }
        catch (OperationCanceledException)
        {
            // A re-render, a game-data reload or the tab closing called it off; a
            // re-render has its own trip coming.
        }
        catch (Exception ex)
        {
            // A failure of a trip that has since been taken over by another is no news.
            Dispatcher.UIThread.Post(() =>
            {
                if (generation == _routeGeneration)
                    _log?.Warn(LogCategory, $"the trip to {shops.Count} shop(s) could not be worked out ({ex.GetType().Name}: {ex.Message}); the shops keep their order");
            });
        }
    }

    private void ApplyRoute(RoomKey here, IReadOnlyList<(int Shop, int? Steps)> trip)
    {
        using PerformanceMonitor.Scope? scope = AppServices.CurrentOrNull?.Performance.Measure("Chest Offload trip applied");
        _lastTrip.Clear();
        int at = 0;
        foreach ((int shop, int? steps) in trip)
        {
            int index = -1;
            for (int i = 0; i < ShopGroups.Count && index < 0; i++)
                if (ShopGroups[i].Shop == shop) index = i;
            if (index < 0) continue;   // its last row left meanwhile
            if (index != at) ShopGroups.Move(index, at);
            _shopRoom.TryGetValue(shop, out Room? room);
            ShopGroups[at].Steps = StepsText(room, here, steps);
            _lastTrip[shop] = (at, ShopGroups[at].Steps);
            at++;
        }
    }

    // A new trip takes over from the one being worked out, which is called off.
    private int NewRouteGeneration()
    {
        _routeCancel?.Cancel();
        _routeCancel?.Dispose();
        _routeCancel = null;
        return ++_routeGeneration;
    }

    // The trip was worked out on the graph a reload has replaced, and is dropped, as a
    // route card's plan is. The list is shown again against the new graph (its shop
    // rooms, then a trip of its own); a simulated list only loses its trip.
    private void OnGraphReloaded() => Dispatcher.UIThread.Post(() =>
    {
        NewRouteGeneration();
        _lastTrip.Clear();
        if (!_simulating) RebuildLoot();
    });

    // Hop counts for each pair, worked out on a worker with a memo of its own (a memo
    // is for one pass, BfsMapper.DistanceMemo); null for a pair with no destination or
    // no route. Null as a whole when the tab closed meanwhile, or the lookup failed,
    // which is logged. Awaited from the UI thread, where Avalonia's synchronization
    // context brings the caller's continuation back.
    private async Task<int?[]?> DistancesAsync(IReadOnlyList<(RoomKey From, RoomKey? To)> pairs, string what)
    {
        CancellationToken closing = _lifetime.Token;
        try
        {
            return await Task.Run(() =>
            {
                Func<RoomKey, RoomKey, int?> memo = _bfs.DistanceMemo(_movement);
                return pairs.Select(p =>
                {
                    closing.ThrowIfCancellationRequested();
                    return p.To is { } to ? memo(p.From, to) : null;
                }).ToArray();
            }, closing);
        }
        catch (OperationCanceledException)
        {
            return null;   // the tab closed
        }
        catch (Exception ex)
        {
            _log?.Warn(LogCategory, $"the steps for {what} could not be worked out ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    private string StepsText(Room? room, RoomKey? here, int? dist)
    {
        if (room is null || here is null) return "";
        if (dist is not { } steps) return "unreachable";
        return steps == 0 ? "you're here" : StepsAndEta(steps);
    }

    // "12 steps · ~45s": the steps plus their walking time under the same per-step
    // travel model the Navigation status ETA uses. Fights on the way aren't counted.
    private string StepsAndEta(int steps)
    {
        string count = steps == 1 ? "1 step" : $"{steps} steps";
        TimeSpan eta = _hopEta(steps);
        return eta > TimeSpan.Zero ? $"{count} · ~{RouteEtaEstimator.FormatCompact(eta)}" : count;
    }

    partial void OnCharmChanged(int value)
    {
        foreach (ChestOffloadShopGroup group in ShopGroups) group.Reprice(value, _gameData.ActiveRealm);
        UpdateGrandTotal();
    }

    // Test aid: seed the container list with a handful of random real containers.
    // Clicking one still sends the real "open" (below), but its loot is rolled from
    // the chest's own table rather than waiting on a live drop, so the tab can be
    // exercised without hunting chests. Re-clicking re-randomises the simulated set.
    private System.Collections.Generic.IReadOnlyDictionary<int, ChestContents>? _chestTables;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void SimulateChest()
    {
        _simulating = true;   // hold the simulated view against the tracker's list
        _simCoin = CurrencyHoldings.Empty;                 // fresh test run
        RebuildCoinGains(_simCoin);
        _chestTables ??= ChestContentsReader.ReadAll(_gameData);
        for (int i = Containers.Count - 1; i >= 0; i--)
            if (Containers[i].Simulated) Containers.RemoveAt(i);

        foreach (int id in _chestTables.Keys.OrderBy(_ => Random.Shared.Next()).Take(4))
        {
            if (_itemNames.GetName(id) is not { } name) continue;
            int number = id;
            Containers.Add(new ChestContainerRow(name, 1, () => SimulateOpen(name, number), simulated: true));
        }
        OnPropertyChanged(nameof(HasContainers));
    }

    // Send the real "open", then roll this chest's loot table: pick an item count in
    // its [MinItems, MaxItems] range, then draw that many items weighted by each
    // drop's chance (with replacement, so a chest can hand out multiples). Renders
    // the rolled loot through the same shop-grouping pipeline as a real open.
    private void SimulateOpen(string name, int number)
    {
        _simulating = true;
        _send($"open {name}");

        // No real currency change to diff in simulation, so add a believable spill.
        CurrencyHoldings spill = RandomCoinGain();
        _simCoin = new CurrencyHoldings(
            _simCoin.Copper + spill.Copper, _simCoin.Silver + spill.Silver, _simCoin.Gold + spill.Gold,
            _simCoin.Platinum + spill.Platinum, _simCoin.Runic + spill.Runic,
            _simCoin.TotalCopperValue + spill.TotalCopperValue);
        RebuildCoinGains(_simCoin);

        if (_chestTables is null || !_chestTables.TryGetValue(number, out ChestContents? contents)
            || contents.Drops.Count == 0)
        {
            RenderLoot(Array.Empty<(string, int)>());
            return;
        }

        int count = Random.Shared.Next(contents.MinItems, contents.MaxItems + 1);
        double totalWeight = contents.Drops.Sum(d => d.Probability);
        var rolled = new Dictionary<string, int>();
        for (int i = 0; i < count && totalWeight > 0; i++)
        {
            double r = Random.Shared.NextDouble() * totalWeight;
            foreach (ChestDrop drop in contents.Drops)
            {
                r -= drop.Probability;
                if (r <= 0) { rolled[drop.ItemName] = rolled.GetValueOrDefault(drop.ItemName) + 1; break; }
            }
        }
        RenderLoot(rolled.Select(kv => (kv.Key, kv.Value)).ToList());
    }

    // A believable per-denomination coin spill for the Simulate Chest test path.
    private static CurrencyHoldings RandomCoinGain()
    {
        int copper = Random.Shared.Next(0, 200);
        int silver = Random.Shared.Next(0, 80);
        int gold = Random.Shared.Next(0, 40);
        int plat = Random.Shared.Next(0, 12);
        int runic = Random.Shared.Next(0, 2);
        long total = copper + silver * 10L + gold * 100L + plat * 10_000L + runic * 1_000_000L;
        return new CurrencyHoldings(copper, silver, gold, plat, runic, total);
    }

    // Take one item off the list (it stays in the pack). The tracker's Changed
    // rebuilds the view.
    private void RemoveItem(ChestOffloadItemRow item)
    {
        ClearActionNote();
        if (_simulating) { RemoveRow(item); return; }
        CallOffHeldHides(new[] { item.Name });
        _chests.RemoveItem(item.Name);
    }

    // Empty the whole list and the coin tally.
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ClearList()
    {
        ClearActionNote();
        if (_simulating)
        {
            _simulating = false;
            _simCoin = CurrencyHoldings.Empty;
        }
        else
        {
            CallOffHeldHides(ShopGroups.SelectMany(g => g.Items).Select(r => r.Name));
        }
        _chests.Clear();
        RebuildLoot();
    }

    // A simulated row has nothing in the tracker behind it — just drop it from view.
    private void RemoveRow(ChestOffloadItemRow item)
    {
        if (GroupOf(item) is { } group)
        {
            group.Items.Remove(item);
            if (group.Items.Count == 0) ShopGroups.Remove(group);
            else group.Retotal();
        }
        else Unsellable.Remove(item);
        UpdateGrandTotal();
        OnPropertyChanged(nameof(HasLoot));
        OnPropertyChanged(nameof(HasUnsellable));
    }

    // Sell All: a one-stop tour of this shop (sells here when standing in it).
    private void SellGroup(ChestOffloadShopGroup group)
    {
        ClearActionNote();
        _log?.Info(LogCategory, $"Sell All '{group.ShopName}' — {group.Items.Count} item(s)");
        if (StopFor(group.Shop, group.ShopName, group.Items) is { } stop) StartTour(new[] { stop });
    }

    private void SellItem(ChestOffloadItemRow item)
    {
        ClearActionNote();
        if (item.SellQty <= 0) return;
        string shopName = GroupOf(item)?.ShopName ?? $"Shop #{item.CurrentShop}";
        if (StopFor(item.CurrentShop, shopName, new[] { item }) is { } stop) StartTour(new[] { stop });
    }

    // A sale walks rooms before it sells, and a held hide goes out in the first new
    // room: it would hide the item on the way to the shop.
    private void StartTour(IReadOnlyList<ChestSellTour.Stop> stops)
    {
        // The tour walks from shop to shop: a walk started by hand.
        if (Services.AppServices.Current.RefuseStartForMasterSwitch("Sell Tour")) return;
        CallOffHeldHides(stops.SelectMany(s => s.Items).Select(i => i.Name));
        _tour.Start(stops);
    }

    // A line on the status while the tour is being worked out; the line that was
    // there comes back after, unless something else has taken the line meanwhile.
    private string ShowPassing(string text)
    {
        string before = SellStatus;
        SellStatus = text;
        return before;
    }

    private void EndPassing(string text, string before)
    {
        if (SellStatus == text) SellStatus = before;
    }

    // How long a Sell Tour waits for a trip still being worked out.
    private static readonly TimeSpan SellTourTripWait = TimeSpan.FromSeconds(5);

    // Sell Tour: read the inventory fresh (so the list's counts are the truth), show
    // exactly what each shop will be sold, and on Yes walk and sell them all in route
    // order. Quantities are capped at what the chests gave and are still carried, here
    // for the prompt and again by the tour right before each sell.
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async System.Threading.Tasks.Task SellTour()
    {
        ClearActionNote();
        if (_tour.IsRunning) { SellStatus = "A sell tour is already running — cancel it first."; return; }
        if (_simulating) { SellStatus = "Simulated chests can't be sold — Clear list first."; return; }
        await ReadInventoryAsync();
        if (_lifetime.IsCancellationRequested) return;
        // The stops go in the tab's order, so a trip still being worked out (the read
        // may have pruned the list and started one) is waited for, and so is any
        // re-render's trip that takes over from it meanwhile, for a few seconds at
        // most: after that the tour goes in the order the tab shows.
        if (!_routeLanded.IsCompleted)
        {
            string shown = ShowPassing("Working out the trip…");
            Task giveUp = Task.Delay(SellTourTripWait);
            while (!_routeLanded.IsCompleted && !giveUp.IsCompleted) await Task.WhenAny(_routeLanded, giveUp);
            if (!_routeLanded.IsCompleted)
                _log?.Info(LogCategory, $"sell tour: the trip was still being worked out after {SellTourTripWait.TotalSeconds:F0} s; the tour goes in the order shown");
            EndPassing("Working out the trip…", shown);
            if (_lifetime.IsCancellationRequested) return;
        }

        var stops = new List<ChestSellTour.Stop>();
        foreach (ChestOffloadShopGroup group in ShopGroups)
            if (StopFor(group.Shop, group.ShopName, group.Items) is { } stop) stops.Add(stop);
        if (stops.Count == 0) { SellStatus = "Nothing on the list to sell."; return; }

        // Each leg's steps and walking time from the stop before it (the first from
        // where you stand), so a long walk for one cheap item is plain before you go.
        var body = new System.Text.StringBuilder();
        body.Append("Walk to each shop below in this order and sell exactly these items:\n");
        int n = 0, totalSteps = 0;
        bool anyUnknown = false;
        RoomKey? here = _tracker.State.CurrentRoom?.Key;
        var pairs = new List<(RoomKey From, RoomKey? To)>(stops.Count);
        for (int i = 0; i < stops.Count; i++)
            pairs.Add(i > 0 ? (stops[i - 1].Room, stops[i].Room)
                : here is { } start ? (start, stops[0].Room)
                : (stops[0].Room, null));   // no first leg while we don't know where we are
        string before = ShowPassing("Working out the tour's steps…");
        int?[]? legs = await DistancesAsync(pairs, "the sell tour's legs");
        EndPassing("Working out the tour's steps…", before);
        if (_lifetime.IsCancellationRequested) return;
        foreach (ChestSellTour.Stop stop in stops)
        {
            int? leg = legs?[n];
            string legText = leg switch
            {
                null => "steps unknown",
                0 => "you're here",
                _ => StepsAndEta(leg.Value),
            };
            if (leg is { } l) totalSteps += l; else anyUnknown = true;
            body.Append($"\n{++n}. {stop.ShopName} ({stop.Room.Map}/{stop.Room.Room}) — {legText}, sells for {ValueOf(stop)}\n   ")
                .Append(string.Join(", ", stop.Items.Select(i => $"{i.Qty} {i.Name}")))
                .Append('\n');
        }
        body.Append($"\nWhole tour: {(anyUnknown ? "at least " : "")}{StepsAndEta(totalSteps)} of walking (fights on the way not counted).\n");
        body.Append("\nOnly items the chests gave are sold, never more than the chests gave — " +
                    "anything you already had stays in your pack. A running loop or Auto-Lair stops for the walk.");
        bool go = await AppServices.Current.Confirm.ConfirmAsync("Sell Tour", body.ToString(), "Start tour");
        if (!go || _lifetime.IsCancellationRequested) return;
        StartTour(stops);
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void CancelTour() => _tour.Cancel();

    // What a stop's items fetch at the current charm: each row's line price, scaled to
    // the (possibly capped) quantity the stop sells.
    private string ValueOf(ChestSellTour.Stop stop)
    {
        double copper = 0;
        foreach ((string name, int qty) in stop.Items)
            if (FindRow(name) is (_, ChestOffloadItemRow row) && row.SellQty > 0)
                copper += (double)row.LineCopper * qty / row.SellQty;
        return copper > 0 ? ShopPriceCalculator.FormatCopper((long)Math.Round(copper)) : "—";
    }

    // One shop's stop: each row's picked quantity, capped at what the chests gave and
    // are still carried. Null when nothing is left to sell there, or the shop has no
    // known room to walk to.
    private ChestSellTour.Stop? StopFor(int shop, string shopName, IEnumerable<ChestOffloadItemRow> rows)
    {
        IReadOnlyList<(string Name, int Count)> loot = _chests.Loot(_inventory.Snapshot.CarriedItems);
        var items = new List<(string Name, int Qty)>();
        foreach (ChestOffloadItemRow row in rows)
        {
            int fromChests = loot.FirstOrDefault(l => string.Equals(l.Name, row.Name, StringComparison.OrdinalIgnoreCase)).Count;
            int qty = Math.Min(row.SellQty, fromChests);
            if (qty > 0) items.Add((row.Name, qty));
        }
        if (items.Count == 0) return null;
        if (!_shopRoom.TryGetValue(shop, out Room? room))
        {
            SellStatus = $"{shopName}: no known room for this shop — walk there and sell by hand.";
            _log?.Info(LogCategory, $"sell at '{shopName}' skipped — no room serves shop #{shop}");
            return null;
        }
        return new ChestSellTour.Stop(room.Key, shop, shopName, items);
    }

    // Send `i` and wait for it to parse (or 3 s), so the tracker has pruned the list to
    // what's really carried before quantities are worked out.
    private async System.Threading.Tasks.Task ReadInventoryAsync()
    {
        var parsed = new System.Threading.Tasks.TaskCompletionSource();
        void OnParsed() => parsed.TrySetResult();
        _inventory.FullInventoryParsed += OnParsed;
        try
        {
            _send("i");
            await System.Threading.Tasks.Task.WhenAny(parsed.Task, System.Threading.Tasks.Task.Delay(3000));
            // The tracker prunes on the same parse through a UI-thread post; let it run.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        finally { _inventory.FullInventoryParsed -= OnParsed; }
    }

    private void OnTourChanged() => Dispatcher.UIThread.Post(() =>
    {
        SellStatus = _tour.Status;
        IsTourRunning = _tour.IsRunning;
    });

    private ChestOffloadShopGroup? GroupOf(ChestOffloadItemRow item)
        => ShopGroups.FirstOrDefault(g => g.Items.Contains(item));

    // Discard this one item's whole held stack (the per-item counterpart to a shop's
    // Drop All, which discards every item's stack). The row is NOT removed here — it
    // reconciles when the game's "You dropped …" / "You hid …" lands
    // (ReconcileConfirmed), so a blocked discard leaves the plan intact.
    private void DropItem(ChestOffloadItemRow item)
    {
        ClearActionNote();
        if (item.Gained <= 0 || RefuseSimulatedDiscard()) return;
        List<string> commands = new();
        QueueDiscard(commands, item);
        _sendPaced(commands);
    }

    // A simulated row stands for nothing in the pack, and a hide for an item that
    // isn't held is not refused by the game but spoken to the room.
    private bool RefuseSimulatedDiscard()
    {
        if (!_simulating) return false;
        ShowNote("Simulated chests can't be dropped — Clear list first.");
        return true;
    }

    // Word one row's discard, hide or drop as the auto-discard engine would send
    // it, and log which it was. Only copies still carried and not already on their
    // way go out, so a second press before the game answers the first sends nothing.
    private void QueueDiscard(List<string> commands, ChestOffloadItemRow item)
    {
        (string verb, int sent, int held, string? blocked) = _discard.EmitDiscard(commands.Add, item.Name, item.Gained);
        if (sent > 0)
            _log?.Info(LogCategory, $"discard {sent} {item.Name} via {verb}" +
                (sent < item.Gained ? $" (of {item.Gained}; the rest held, already sent or not carried)" : " (whole stack)"));
        if (held > 0)
        {
            // A hide that could get no answer where the character stands (the dark,
            // blind, the game busy with a menu), or one auto-discard had waiting.
            _log?.Info(LogCategory, $"hide of {held} {item.Name} held: it goes out when a room can take it");
            if (!_tour.IsRunning)
                ShowNote($"{item.Name}: can't be hidden here right now — it stays in your pack and is hidden as soon as it can be.", item.Name);
            return;
        }
        if (sent > 0) return;

        // Until an `i` has been read the pack is unknown, and nothing is sent for a
        // copy that can't be shown to be carried.
        bool waiting = blocked is null && _inventory.IsLoaded && _discard.HeldFor(item.Name) > 0;
        string why = blocked
            ?? (!_inventory.IsLoaded ? "your inventory hasn't been read yet (type i)"
            : waiting ? "it is waiting for the next room to be hidden"
            : "it was already sent, or is no longer in your pack");
        _log?.Info(LogCategory, $"discard of {item.Name} not sent: {why}");
        // A waiting hide's note comes down with the hide; any other on the next press.
        if (!_tour.IsRunning) ShowNote($"{item.Name}: nothing sent — {why}.", waiting ? item.Name : null);
    }

    // Alternate shops that also buy this item, minus the one it's in, as the ⇄ list
    // shows them at once: by name, with no steps yet.
    private IReadOnlyList<ShopChoiceRow> BuildShopChoices(ChestOffloadItemRow item)
        => OtherShops(item).Select(s => ChoiceRow(s.Shop, s.Room, ""))
            .OrderBy(r => r.ShopName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Shop)
            .ToList();

    // The same shops nearest first, each with its steps, worked out on a worker.
    private async Task<IReadOnlyList<ShopChoiceRow>> RankShopChoicesAsync(ChestOffloadItemRow item)
    {
        List<(int Shop, Room? Room)> shops = OtherShops(item);
        if (_tracker.State.CurrentRoom?.Key is not { } here) return BuildShopChoices(item);
        int?[]? dists = await DistancesAsync(
            shops.Select(s => (here, s.Room?.Key)).ToList(), $"the other shops buying {item.Name}");
        if (dists is null) return BuildShopChoices(item);
        return shops.Select((s, i) => (Dist: dists[i], Row: ChoiceRow(s.Shop, s.Room, StepsText(s.Room, here, dists[i]))))
            .OrderBy(t => t.Dist is null)          // reachable first
            .ThenBy(t => t.Dist ?? int.MaxValue)   // then nearest
            .Select(t => t.Row)
            .ToList();
    }

    private List<(int Shop, Room? Room)> OtherShops(ChestOffloadItemRow item)
        => item.CandidateShops.Where(shop => shop != item.CurrentShop)
            .Select(shop => (shop, _shopRoom.TryGetValue(shop, out Room? room) ? room : null))
            .ToList();

    private ShopChoiceRow ChoiceRow(int shop, Room? room, string steps)
        => new(shop, ShopLabel(shop, room), ShopLocation(room), steps);

    // A shop by its own name in the game data, with the room it stands in by number
    // and name.
    private string ShopLabel(int shop, Room? room) => _shops.ShopName(shop) ?? room?.Name ?? $"Shop #{shop}";

    private static string ShopLocation(Room? room)
        => room is { } r ? $"{r.Key.Map}/{r.Key.Room} {r.Name}" : "";

    // A group an item was moved into, for a shop the last trip didn't visit, gets its
    // steps from here when they come back.
    private async Task FillStepsAsync(ChestOffloadShopGroup group, Room? room)
    {
        if (room is null || _tracker.State.CurrentRoom?.Key is not { } here) return;
        int?[]? dist = await DistancesAsync(new[] { (here, (RoomKey?)room.Key) }, $"shop #{group.Shop}");
        if (dist is null || ShopGroups.IndexOf(group) is not (>= 0 and int place)) return;
        group.Steps = StepsText(room, here, dist[0]);
        // Kept like a trip's, so a re-render before the next trip doesn't blank it.
        _lastTrip[group.Shop] = (place, group.Steps);
    }

    // Move an item to another shop it can be sold at: pull it from its current group
    // (dropping the group if that empties it), and drop it into the target group,
    // creating that group if this is the first item headed there.
    private void MoveItemToShop(ChestOffloadItemRow item, int targetShop)
    {
        if (targetShop == item.CurrentShop) return;

        ChestOffloadShopGroup? source = GroupOf(item);
        source?.Items.Remove(item);

        ChestOffloadShopGroup? target = ShopGroups.FirstOrDefault(g => g.Shop == targetShop);
        if (target is null)
        {
            _shopRoom.TryGetValue(targetShop, out Room? room);
            string steps = _lastTrip.TryGetValue(targetShop, out (int _, string Steps) known) ? known.Steps : "";
            target = new ChestOffloadShopGroup(ShopLabel(targetShop, room), targetShop, ShopLocation(room),
                steps, room?.Key, _queueWalk, SellGroup, DropAllGroup);
            ShopGroups.Add(target);
            if (steps.Length == 0) _ = FillStepsAsync(target, room);
        }

        item.CurrentShop = targetShop;
        target.Items.Add(item);

        if (source is not null && source.Items.Count == 0) ShopGroups.Remove(source);
        else source?.Retotal();
        target.Retotal();
        UpdateGrandTotal();
        OnPropertyChanged(nameof(HasLoot));
    }

    // Discard every item in this shop's listing — the whole held stack of each. The
    // rows are NOT removed here; each clears when its "You dropped …" / "You hid …"
    // confirms, so a blocked discard stays visible.
    private void DropAllGroup(ChestOffloadShopGroup group)
    {
        ClearActionNote();
        if (RefuseSimulatedDiscard()) return;
        _log?.Info(LogCategory, $"Drop All '{group.ShopName}' — {group.Items.Count} item(s)");
        List<string> commands = new();
        foreach (ChestOffloadItemRow item in group.Items) QueueDiscard(commands, item);
        _sendPaced(commands);
    }

    private double BaseCopperOf(int number)
        => _gameData.FindRowByNumber("Items", number) is { } el
            ? ShopPriceCalculator.ToCopper(ReadInt(el, "Price"), ReadInt(el, "Currency"))
            : 0;

    private static int ReadInt(GameRow el, string field)
        => el.TryGetProperty(field, out GameValue v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)
            ? n : 0;

    public override void Dispose()
    {
        NewRouteGeneration();
        _lifetime.Cancel();
        _rooms.GraphReloaded -= OnGraphReloaded;
        _chests.Changed -= OnChestsChanged;
        _inventory.Changed -= OnInventoryChanged;
        _tour.Changed -= OnTourChanged;
        _chests.ItemLeft -= OnItemLeft;
        _discard.HideRefused -= OnHideRefused;
        _discard.HeldHideEnded -= OnHeldHideEnded;
        if (_diagnostics is not null) _diagnostics.Changed -= OnDiagnosticsChanged;
        _log?.Info(LogCategory, "tab closed");
    }

    private readonly record struct LootItem(string Name, int Count, double BaseCopper, IReadOnlyCollection<int> Shops);
}
