using System.Collections.Generic;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Inventory;

// Auto-sell engine. Arriving in a shop room whose shop trades an item the user
// flagged ItemOverlay.AutoSell sells every carried copy down to its keep floor (Min.
// to keep when above 0, otherwise everything — resolved upstream). A shop buys back
// what it has in its inventory listing, and `sell` needs no `list` first (user,
// 2026-09-28), so the engine sells straight away rather than spending a command on a
// readout (auto-buy still reads the `list`).
//
// One command per unit on Stock (no bulk-sell verb); Paradigm sells the whole
// quantity in one counted `sell N <item>`. Either way the engine sends once and waits
// for the result before the next — KnownPatterns.UserSells ("You sold X for …")
// advances the count; KnownPatterns.UserSellRefused ("You cannot sell X here.")
// abandons that item. While selling it holds movement (SellingGate) so a walk or loop
// can't step out mid-sale and land the rest of the sells in the next room; a result
// that never arrives lets go after ResultTimeout.
//
// LIGHT items are excluded upstream (Auto-light owns them) and a LoyalItem is
// never a sell target — both decided by the injected resolver.
//
// Gated by the Auto-Get Items auto-mode toggle (the master) and the per-item
// AutoSell flag. Runs UI-thread only (MessageRouter and RoomTracker marshal
// upstream), so the pump state needs no lock.
public sealed class AutoSellManager : IDisposable
{
    // LogService category — [AutoSell] rows per queued / sold item.
    public const string LogCategory = "AutoSell";

    private static readonly TimeSpan ResultTimeout = TimeSpan.FromSeconds(5);

    // One resolved carried entry: canonical item Number, the name to send to the
    // game, whether the user flagged it AutoSell, and how many copies to keep
    // (0 = sell all).
    public sealed record ResolvedSell(int Number, string Name, bool Sell, int KeepCount);

    private readonly Func<IReadOnlyList<string>> _carried;
    private readonly Func<string, ResolvedSell?> _resolve;
    private readonly Func<int, int, bool> _shopTradesItem;
    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _isParadigm;
    private readonly LogService? _log;
    private readonly IDisposable _soldSub;
    private readonly IDisposable _refusedSub;

    private sealed class SellOrder
    {
        public int Number;
        public string Name = string.Empty;
        public int Remaining;
    }

    private readonly List<SellOrder> _queue = new();
    private int _active = -1;

    private Action<byte[]>? _wireSender;
    private MovementCoordinator? _coordinator;
    private Action<TimeSpan, Action>? _schedule;
    private int _timeoutGeneration;
    private bool _gateHeld;

    // For a bug report: the sale in progress, or null.
    public string? DescribePending() =>
        _queue.Count == 0 && !_gateHeld ? null
        : $"{_queue.Count} sale(s) queued, sale {_active + 1} active" + (_gateHeld ? ", holding the Selling gate" : string.Empty);
    private bool _disposed;

    // shopTradesItem(shopNumber, itemNumber): whether that shop has the item in its
    // inventory listing, so it buys it back.
    public AutoSellManager(
        MessageRouter router,
        Func<IReadOnlyList<string>> carriedItems,
        Func<string, ResolvedSell?> resolve,
        Func<int, int, bool> shopTradesItem,
        Func<bool> isEnabled,
        LogService? log = null,
        Func<bool>? isParadigm = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(carriedItems);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(shopTradesItem);
        ArgumentNullException.ThrowIfNull(isEnabled);
        _carried = carriedItems;
        _resolve = resolve;
        _shopTradesItem = shopTradesItem;
        _isEnabled = isEnabled;
        // Unbound → Stock behaviour: one `sell` per copy, wait for each.
        _isParadigm = isParadigm ?? (static () => false);
        _log = log;

        _soldSub = router.Subscribe(KnownPatterns.UserSells, OnSold);
        _refusedSub = router.Subscribe(KnownPatterns.UserSellRefused, OnSellRefused);
    }

    // True while sells are going out here.
    public bool IsSelling => _active >= 0;

    // Raised when a round of selling ends (everything sold, refused or timed out).
    public event Action? Finished;

    // Raised with the item Number when the shop refuses to buy it.
    public event Action<int>? ItemRefused;

    // Bind the wire sender — the gate-wrapped engine pipeline from
    // MainWindowViewModel.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // The movement gate held while selling, and the one-shot scheduler for the
    // result timeout. Both optional.
    public void SetMovementGate(MovementCoordinator coordinator) => _coordinator = coordinator;
    public void SetScheduler(Action<TimeSpan, Action> schedule) => _schedule = schedule;

    // A genuine room change (driven ahead of the movement engines so the gate is up
    // before the next step). shopNumber is the room's shop, 0 for none. Leaving a
    // room mid-sale abandons what was left there.
    public void OnRoomEntered(int shopNumber)
    {
        if (IsSelling) Finish("left the shop");
        if (shopNumber == 0 || !_isEnabled() || _wireSender is null) return;

        foreach ((ResolvedSell item, int count) in CarriedFlagged())
        {
            int target = count - item.KeepCount;
            if (target <= 0 || !_shopTradesItem(shopNumber, item.Number)) continue;
            _queue.Add(new SellOrder { Number = item.Number, Name = item.Name, Remaining = target });
            _log?.Info(LogCategory, $"queue sell item={item.Name} count={target} (carried {count}, keep {item.KeepCount}) at shop #{shopNumber}");
        }
        if (_queue.Count == 0) return;

        _active = 0;
        if (_coordinator is { } c && !_gateHeld)
        {
            _gateHeld = true;
            c.AssertGate(MovementCoordinator.SellingGate, nameof(AutoSellManager), "selling");
        }
        PumpActive();
    }

    // Reset States / disconnect: drop the pump and let movement go.
    public void Cancel()
    {
        if (IsSelling) Finish("cancelled");
    }

    // Carried AutoSell items, grouped by item Number so duplicate name strings count
    // as copies of one item. A stack is one entry carrying its count ("2 crude stone
    // club"), so each entry adds its own count (report paradigm-20260929-015929).
    private IEnumerable<(ResolvedSell Item, int Count)> CarriedFlagged()
    {
        Dictionary<int, (ResolvedSell Item, int Count)> groups = new();
        foreach (string entry in _carried())
        {
            if (_resolve(entry) is not { Sell: true } item) continue;
            int copies = CountedCommand.SplitLeadingCount(entry).Count;
            groups[item.Number] = groups.TryGetValue(item.Number, out (ResolvedSell Item, int Count) g)
                ? (g.Item, g.Count + copies)
                : (item, copies);
        }
        return groups.Values;
    }

    // Send one `sell` for the active item and stop; the next unit fires when the
    // result line lands. Advances past drained / refused items.
    private void PumpActive()
    {
        while (_active >= 0 && _active < _queue.Count)
        {
            SellOrder order = _queue[_active];
            if (order.Remaining <= 0) { _active++; continue; }
            // Paradigm sells the whole remaining quantity in one `sell N <item>`
            // and gets one counted `You sold N <item> for …` back; Stock sells one
            // at a time. Either way the pump sends once and waits for the reply.
            int qty = _isParadigm() ? order.Remaining : 1;
            _log?.Info(LogCategory, $"sell {qty}x item={order.Name}");
            Send(qty > 1 ? $"sell {qty} {order.Name}" : $"sell {order.Name}");
            ArmTimeout();
            return;
        }
        Finish(null);
    }

    private void ArmTimeout()
    {
        if (_schedule is not { } later) return;
        int generation = ++_timeoutGeneration;
        later(ResultTimeout, () =>
        {
            if (generation == _timeoutGeneration && IsSelling) Finish("no sale result in time");
        });
    }

    private void Finish(string? why)
    {
        _timeoutGeneration++;
        _queue.Clear();
        _active = -1;
        if (why is not null) _log?.Info(LogCategory, $"selling stopped — {why}");
        if (_gateHeld)
        {
            _gateHeld = false;
            _coordinator?.ClearGate(MovementCoordinator.SellingGate, nameof(AutoSellManager), why ?? "sold");
        }
        Finished?.Invoke();
    }

    private void OnSold(MatchResult m)
    {
        if (_active < 0 || _active >= _queue.Count) return;
        if (m.Groups.Count < 1) return;                 // Groups: [item, price]
        (int count, string name) = CountedCommand.SplitLeadingCount(m.Groups[0]);
        if (_resolve(name) is not { } r) return;
        SellOrder order = _queue[_active];
        if (r.Number != order.Number) return;           // a sale we didn't drive
        order.Remaining -= count;                        // batched reply reports the actual count
        if (order.Remaining <= 0) _active++;
        PumpActive();
    }

    private void OnSellRefused(MatchResult m)
    {
        if (_active < 0 || _active >= _queue.Count) return;
        if (m.Groups.Count < 1) return;                 // Groups: [item]
        (_, string name) = CountedCommand.SplitLeadingCount(m.Groups[0]);
        if (_resolve(name) is not { } r) return;
        if (r.Number != _queue[_active].Number) return;
        _log?.Info(LogCategory, $"shop refuses item={_queue[_active].Name} — stopping this item");
        ItemRefused?.Invoke(r.Number);
        _active++;
        PumpActive();
    }

    private void Send(string text)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(text + "\r"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _soldSub.Dispose();
        _refusedSub.Dispose();
    }
}
