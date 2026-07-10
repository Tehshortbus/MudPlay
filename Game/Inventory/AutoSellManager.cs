using System.Collections.Generic;
using System.Text;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Inventory;

// Auto-sell engine. When a shop `list` readout appears — the signal that the
// player is standing at a merchant — sells each carried item the user flagged
// ItemOverlay.AutoSell down to its keep floor. The floor is MinToKeep when
// MustHaveMinimum is set, otherwise zero (no band → sell every copy). Selling
// happens at whatever merchant surfaced the list; there is no bank/stash detour
// — `sell <item>` transacts against the shop the player is already in.
//
// One command per unit: MajorMUD has no bulk-sell verb, so `sell <item>` sells
// exactly one copy. The engine sends a single `sell <name>`, then waits for the
// result before the next unit — KnownPatterns.UserSells ("You sold X for …")
// advances the count; KnownPatterns.UserSellRefused ("You cannot sell X here.")
// abandons that item (this shop won't buy the type) and moves to the next
// flagged item. Each fresh `list` resets the pump, so a stalled queue — a
// dropped result line — self-heals on the next shop visit.
//
// LIGHT items are excluded upstream (Auto-light owns them) and a LoyalItem is
// never a sell target — both decided by the injected resolver.
//
// Master switch: AutoActionDefaults.AutoSell. Runs UI-thread only (MessageRouter
// marshals upstream), so the pump state needs no lock.
public sealed class AutoSellManager : IDisposable
{
    // LogService category — [AutoSell] rows per queued / sold item.
    public const string LogCategory = "AutoSell";

    // One resolved carried entry: canonical item Number, the name to send to the
    // game, whether the user flagged it AutoSell, and how many copies to keep
    // (0 = sell all).
    public sealed record ResolvedSell(int Number, string Name, bool Sell, int KeepCount);

    private readonly Func<IReadOnlyList<string>> _carried;
    private readonly Func<string, ResolvedSell?> _resolve;
    private readonly Func<bool> _isEnabled;
    private readonly LogService? _log;
    private readonly IDisposable _headerSub;
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
    private bool _disposed;

    public AutoSellManager(
        MessageRouter router,
        Func<IReadOnlyList<string>> carriedItems,
        Func<string, ResolvedSell?> resolve,
        Func<bool> isEnabled,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(carriedItems);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(isEnabled);
        _carried = carriedItems;
        _resolve = resolve;
        _isEnabled = isEnabled;
        _log = log;

        _headerSub = router.Subscribe(KnownPatterns.ShopListHeader, OnShopHeader);
        _soldSub = router.Subscribe(KnownPatterns.UserSells, OnSold);
        _refusedSub = router.Subscribe(KnownPatterns.UserSellRefused, OnSellRefused);
    }

    // Bind the wire sender — the gate-wrapped engine pipeline from
    // MainWindowViewModel.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // The shop list arrived — rebuild the sell queue from the carried pack and
    // start the transaction pump.
    private void OnShopHeader(MatchResult m)
    {
        if (!_isEnabled() || _wireSender is null) return;

        _queue.Clear();
        _active = -1;

        // Group carried copies by resolved item Number so duplicate name strings
        // count as multiple copies of one item.
        Dictionary<int, (ResolvedSell Item, int Count)> groups = new();
        foreach (string entry in _carried())
        {
            if (_resolve(entry) is not { Sell: true } item) continue;
            if (groups.TryGetValue(item.Number, out (ResolvedSell Item, int Count) g))
                groups[item.Number] = (g.Item, g.Count + 1);
            else
                groups[item.Number] = (item, 1);
        }

        foreach ((int number, (ResolvedSell item, int count)) in groups)
        {
            int target = count - item.KeepCount;
            if (target <= 0) continue;
            _queue.Add(new SellOrder { Number = number, Name = item.Name, Remaining = target });
            _log?.Info(LogCategory, $"queue sell item={item.Name} count={target} (carried {count}, keep {item.KeepCount})");
        }

        if (_queue.Count == 0) return;
        _active = 0;
        PumpActive();
    }

    // Send one `sell` for the active item and stop; the next unit fires when the
    // result line lands. Advances past drained / refused items.
    private void PumpActive()
    {
        while (_active >= 0 && _active < _queue.Count)
        {
            SellOrder order = _queue[_active];
            if (order.Remaining <= 0) { _active++; continue; }
            _log?.Info(LogCategory, $"sell item={order.Name}");
            Send($"sell {order.Name}");
            return;
        }
        _active = -1;
    }

    private void OnSold(MatchResult m)
    {
        if (_active < 0 || _active >= _queue.Count) return;
        if (m.Groups.Count < 1) return;                 // Groups: [item, price]
        if (_resolve(m.Groups[0]) is not { } r) return;
        SellOrder order = _queue[_active];
        if (r.Number != order.Number) return;           // a sale we didn't drive
        order.Remaining--;
        if (order.Remaining <= 0) _active++;
        PumpActive();
    }

    private void OnSellRefused(MatchResult m)
    {
        if (_active < 0 || _active >= _queue.Count) return;
        if (m.Groups.Count < 1) return;                 // Groups: [item]
        if (_resolve(m.Groups[0]) is not { } r) return;
        if (r.Number != _queue[_active].Number) return;
        _log?.Info(LogCategory, $"shop refuses item={_queue[_active].Name} — stopping this item");
        _active++;                                      // this shop won't buy the type
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
        _headerSub.Dispose();
        _soldSub.Dispose();
        _refusedSub.Dispose();
    }
}
