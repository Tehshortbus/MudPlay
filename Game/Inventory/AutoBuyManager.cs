using System.Collections.Generic;
using System.Text;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Inventory;

// Auto-buy engine. When a shop `list` readout appears, buys each stocked item
// the user flagged ItemOverlay.AutoBuy up to its MaxToGet cap, honouring the
// live stock count and — reactively — the player's purse.
//
// Trigger is reactive: the engine watches the emitted line stream for the shop
// header ("The following items are for sale here:") and parses the three-column
// body that follows via ShopListParser. It does not proactively navigate to
// shops or auto-send `list` — like AutoGetItemsManager it acts on server output
// the player (or a macro) surfaced. This is a design choice (reactive-on-list,
// not proactive-on-shop-entry), not a game constraint.
//
// One command per unit: MajorMUD has no bulk-buy verb, so `buy <item>` transacts
// exactly one copy. The engine sends a single `buy <name>`, then waits for the
// result before the next unit — KnownPatterns.UserBuys ("You just bought X …")
// advances the count; KnownPatterns.UserBuyFailed ("You cannot afford X.")
// abandons that ware (purse spent) and moves to the next flagged item. The
// charm-scaled buy price isn't cheaply predictable, so affordability is read off
// the live result rather than computed up front (matching the confirmed
// mechanic). Each fresh `list` resets the pump, so a stalled queue — a dropped
// result line — self-heals on the next shop visit.
//
// LIGHT items are excluded upstream (Auto-light owns them) and a LoyalItem is
// never a buy target — both decided by the injected resolver.
//
// Master switch: AutoActionDefaults.AutoBuy. UI-thread only (MessageRouter +
// LineExtractor both marshal upstream), so the pump state needs no lock.
public sealed class AutoBuyManager : IDisposable
{
    // LogService category — [AutoBuy] rows per queued / bought item.
    public const string LogCategory = "AutoBuy";

    // One resolved shop ware: canonical item Number, the name to send to the
    // game, whether the user flagged it AutoBuy, and the MaxToGet cap
    // (int.MaxValue = unbounded → buy the whole affordable stock).
    public sealed record ResolvedBuy(int Number, string Name, bool Buy, int MaxToGet);

    private const string ShopHeader = "The following items are for sale here:";
    // A shop lists at most twenty wares; cap the capture so a missing terminator
    // can't buffer unbounded output.
    private const int MaxBodyLines = 48;

    private readonly Func<string, ResolvedBuy?> _resolve;
    private readonly Func<int, int> _countCarried;
    private readonly Func<bool> _isEnabled;
    private readonly LogService? _log;
    private readonly IDisposable _boughtSub;
    private readonly IDisposable _failedSub;

    private Terminal.LineExtractor? _lines;
    private bool _capturing;
    private readonly List<string> _body = new();

    // Per-visit buy queue; _active indexes the ware currently transacting (-1 = idle).
    private sealed class BuyOrder
    {
        public int Number;
        public string Name = string.Empty;
        public int Remaining;
    }

    private readonly List<BuyOrder> _queue = new();
    private int _active = -1;

    private Action<byte[]>? _wireSender;
    private bool _disposed;

    public AutoBuyManager(
        MessageRouter router,
        Func<string, ResolvedBuy?> resolve,
        Func<int, int> countCarried,
        Func<bool> isEnabled,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(countCarried);
        ArgumentNullException.ThrowIfNull(isEnabled);
        _resolve = resolve;
        _countCarried = countCarried;
        _isEnabled = isEnabled;
        _log = log;

        _boughtSub = router.Subscribe(KnownPatterns.UserBuys, OnBought);
        _failedSub = router.Subscribe(KnownPatterns.UserBuyFailed, OnBuyFailed);
    }

    // Bind the wire sender — the gate-wrapped engine pipeline from
    // MainWindowViewModel.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Bind the per-session LineExtractor so the manager can watch for the shop
    // header and gather the stock table that follows.
    public void AttachLineExtractor(Terminal.LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (ReferenceEquals(_lines, lines)) return;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    // ----- capture ------------------------------------------------------

    private void OnLine(Terminal.LineExtractor.EmittedLine line)
    {
        string text = line.Text.TrimEnd();

        if (!_capturing)
        {
            if (text == ShopHeader && _isEnabled())
            {
                _capturing = true;
                _body.Clear();
            }
            return;
        }

        // A prompt, or a blank line after the table body, closes the readout.
        if (line.IsPromptLine) { FinishCapture(); return; }
        if (text.Length == 0 && _body.Count > 0) { FinishCapture(); return; }

        _body.Add(line.Text);
        if (_body.Count >= MaxBodyLines) FinishCapture();
    }

    private void FinishCapture()
    {
        _capturing = false;
        IReadOnlyList<ShopListParser.StockRow> stock = ShopListParser.Parse(_body);
        _body.Clear();

        if (!_isEnabled() || _wireSender is null) return;

        // A fresh readout supersedes any in-progress pump from a prior shop.
        _queue.Clear();
        _active = -1;

        foreach (ShopListParser.StockRow s in stock)
        {
            if (_resolve(s.Name) is not { Buy: true } buy) continue;
            int carried = _countCarried(buy.Number);
            int want = buy.MaxToGet == int.MaxValue ? s.Quantity : buy.MaxToGet - carried;
            int target = Math.Min(want, s.Quantity);
            if (target <= 0) continue;

            _queue.Add(new BuyOrder { Number = buy.Number, Name = buy.Name, Remaining = target });
            _log?.Info(LogCategory,
                $"queue buy item={buy.Name} count={target} (stock {s.Quantity}, carried {carried}, "
                + $"cap {(buy.MaxToGet == int.MaxValue ? "all" : buy.MaxToGet.ToString())})");
        }

        if (_queue.Count == 0) return;
        _active = 0;
        PumpActive();
    }

    // ----- transaction pump ---------------------------------------------

    // Send one `buy` for the active ware and stop; the next unit fires when the
    // result line lands. Advances past drained / skipped wares.
    private void PumpActive()
    {
        while (_active >= 0 && _active < _queue.Count)
        {
            BuyOrder order = _queue[_active];
            if (order.Remaining <= 0) { _active++; continue; }
            _log?.Info(LogCategory, $"buy item={order.Name}");
            Send($"buy {order.Name}");
            return;
        }
        _active = -1;
    }

    private void OnBought(MatchResult m)
    {
        if (_active < 0 || _active >= _queue.Count) return;
        if (m.Groups.Count < 2) return;                 // Groups: [qty, item, price]
        if (_resolve(m.Groups[1]) is not { } r) return;
        BuyOrder order = _queue[_active];
        if (r.Number != order.Number) return;           // a buy we didn't drive
        order.Remaining--;
        if (order.Remaining <= 0) _active++;
        PumpActive();
    }

    private void OnBuyFailed(MatchResult m)
    {
        if (_active < 0 || _active >= _queue.Count) return;
        if (m.Groups.Count < 1) return;                 // Groups: [item]
        if (_resolve(m.Groups[0]) is not { } r) return;
        if (r.Number != _queue[_active].Number) return;
        _log?.Info(LogCategory, $"cannot afford item={_queue[_active].Name} — stopping this ware");
        _active++;                                      // purse spent for this ware
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
        _boughtSub.Dispose();
        _failedSub.Dispose();
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = null;
    }
}
