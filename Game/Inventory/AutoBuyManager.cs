using System.Collections.Generic;
using System.Text;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Inventory;

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
// One command per unit on Stock: its `buy <item>` transacts exactly one copy.
// Paradigm buys the whole remaining quantity in one `buy N <item>`. Either way
// the engine sends one command, then waits for the result before the next —
// KnownPatterns.UserBuys ("You just bought X …") advances the count;
// KnownPatterns.UserBuyFailed ("You cannot afford X.") abandons that ware (purse
// spent) and moves to the next flagged item, and so does
// KnownPatterns.UserBuyRefused (out of stock, not a ware here, can't be
// carried). The charm-scaled buy price isn't cheaply predictable, so
// affordability is read off the live result rather than computed up front
// (matching the confirmed mechanic). Each fresh `list` resets the pump, so a
// stalled queue — a dropped result line — self-heals on the next shop visit.
//
// The queue belongs to the shop it was read in. Leaving the room drops it, and so
// does the game saying we aren't in a shop: a `buy` sent anywhere else buys
// nothing, and a queue left standing would send its next one off whatever
// refusal happened to arrive later.
//
// LIGHT items are excluded upstream (Auto-light owns them) and a LoyalItem is
// never a buy target — both decided by the injected resolver.
//
// Master switch: Auto Get Items (AutoActionDefaults.AutoGetItems). UI-thread only
// (MessageRouter + LineExtractor both marshal upstream), so the pump state needs no
// lock.
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
    private readonly Func<bool> _isParadigm;
    private readonly LogService? _log;
    private readonly IDisposable _boughtSub;
    private readonly IDisposable _failedSub;
    private readonly IDisposable _refusedSub;
    private readonly IDisposable _notInShopSub;

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

    // What followed `buy` in the command the pump is waiting on, and when it went
    // out; null once that command has been answered. Two refusals name no item and
    // also answer a `get`, so they count as this buy's reply only while it is
    // unanswered and recent.
    private string? _awaitingArg;
    private DateTimeOffset _sentAt;

    // For a bug report: the buy in progress, or null.
    public string? DescribePending() =>
        _queue.Count == 0 && _active < 0 ? null
        : $"{_queue.Count} order(s) queued, order {_active + 1} active"
          + (_awaitingArg is { } arg ? $", awaiting the game's answer for '{arg}' since {_sentAt.ToLocalTime():HH:mm:ss}" : string.Empty);

    // How long a `buy` is taken to be still unanswered. A buy is answered at once
    // unless an action delay or commands queued ahead hold it for a second or two
    // (BulkCommandPacer has the queue's rules); past this a nameless refusal is
    // taken to be some other command's.
    public static readonly TimeSpan ReplyWindow = TimeSpan.FromSeconds(5);

    private readonly Func<DateTimeOffset> _now;

    private Action<byte[]>? _wireSender;
    private bool _disposed;

    // A shop's `list` readout, parsed — raised for every readout, whether or not
    // Auto-buy is on, so an errand that sent the `list` itself can read the stock.
    public event Action<IReadOnlyList<ShopListParser.StockRow>>? StockListed;

    public AutoBuyManager(
        MessageRouter router,
        Func<string, ResolvedBuy?> resolve,
        Func<int, int> countCarried,
        Func<bool> isEnabled,
        LogService? log = null,
        Func<bool>? isParadigm = null,
        Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.Now);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(countCarried);
        ArgumentNullException.ThrowIfNull(isEnabled);
        _resolve = resolve;
        _countCarried = countCarried;
        _isEnabled = isEnabled;
        // Unbound → Stock behaviour: one `buy` per copy, wait for each.
        _isParadigm = isParadigm ?? (static () => false);
        _log = log;

        _boughtSub = router.Subscribe(KnownPatterns.UserBuys, OnBought);
        _failedSub = router.Subscribe(KnownPatterns.UserBuyFailed, OnBuyFailed);
        _refusedSub = router.Subscribe(KnownPatterns.UserBuyRefused, OnBuyRefused);
        _notInShopSub = router.Subscribe(KnownPatterns.UserBuyNotInShop, _ => DropQueue("the game says we aren't in a shop"));
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
            if (text == ShopHeader)
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

    // A prompt reached the wire. The readout's last row is followed by the prompt
    // on a line of its own that isn't ended, so it is only emitted as a line once
    // something else is printed — which, for an errand that sent the `list` and is
    // waiting on the stock, is never: every spell-shop stop waited out its window
    // and bought blind (report paradigm-20261005-091552). The owner calls this one
    // dispatch hop after the prompt was seen, when the rows that arrived in the same
    // read have been emitted.
    public void NotePromptSeen()
    {
        if (_capturing && _body.Count > 0) FinishCapture();
    }

    private void FinishCapture()
    {
        _capturing = false;
        IReadOnlyList<ShopListParser.StockRow> stock = ShopListParser.Parse(_body);
        _body.Clear();
        StockListed?.Invoke(stock);

        if (!_isEnabled() || _wireSender is null) return;

        // A fresh readout supersedes any in-progress pump from a prior shop.
        _queue.Clear();
        _active = -1;
        _awaitingArg = null;

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
            // Paradigm buys the whole remaining quantity in one `buy N <item>` and
            // gets one `You bought N <item> for …` back; Stock buys one at a time.
            // Either way the pump sends once and waits for the reply.
            int qty = _isParadigm() ? order.Remaining : 1;
            _log?.Info(LogCategory, $"buy {qty}x item={order.Name}");
            _awaitingArg = qty > 1 ? $"{qty} {order.Name}" : order.Name;
            _sentAt = _now();
            Send($"buy {_awaitingArg}");
            return;
        }
        _active = -1;
        _awaitingArg = null;
    }

    // The room changed under the pump (an errand that read the same `list` walked
    // on, a teleport, a death). Bound to RoomTracker's genuine room changes.
    public void OnRoomChanged() => DropQueue("left the shop");

    private void DropQueue(string why)
    {
        if (_active < 0) return;
        int left = _queue.Count - _active;
        _queue.Clear();
        _active = -1;
        _awaitingArg = null;
        _log?.Info(LogCategory, $"{why} — dropping {left} ware(s) still queued");
    }

    private void OnBought(MatchResult m)
    {
        if (_active < 0 || _active >= _queue.Count) return;
        if (m.Groups.Count < 2) return;                 // Groups: [qty, item, price]
        if (_resolve(m.Groups[1]) is not { } r) return;
        BuyOrder order = _queue[_active];
        if (r.Number != order.Number) return;           // a buy we didn't drive
        // The reply's qty group is the actual count bought (empty → 1 on Stock).
        int bought = int.TryParse(m.Groups[0], out int q) && q > 0 ? q : 1;
        _awaitingArg = null;
        order.Remaining -= bought;
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
        _awaitingArg = null;
        _active++;                                      // purse spent for this ware
        PumpActive();
    }

    // Out of stock, not a ware of this shop, or it can't be carried: the ware is
    // done for this visit and the next one goes out. Other engines buy too (a spell
    // errand, the light and path-item detours) and the carry lines answer a `get`,
    // so each line has to be shown to be this pump's: by the ware it names, by the
    // words it echoes, or, for the two that name nothing, by a buy of ours still
    // waiting for its answer.
    private void OnBuyRefused(MatchResult m)
    {
        if (_active < 0 || _active >= _queue.Count) return;
        BuyOrder order = _queue[_active];
        string named = m.Groups.Count > 0 ? m.Groups[0] : string.Empty;   // "You cannot buy <item> here!"
        string typed = m.Groups.Count > 1 ? m.Groups[1] : string.Empty;   // "<typed> is not a known item."
        if (named.Length > 0)
        {
            if (_resolve(named) is not { } r || r.Number != order.Number) return;
        }
        else if (typed.Length > 0)
        {
            if (!typed.Equals(_awaitingArg, StringComparison.OrdinalIgnoreCase)
                && !typed.Equals(order.Name, StringComparison.OrdinalIgnoreCase)) return;
        }
        else if (_awaitingArg is null || _now() - _sentAt > ReplyWindow)
        {
            return;
        }

        _log?.Info(LogCategory,
            $"buy refused item={order.Name} (\"{m.Text.Trim()}\") — stopping this ware");
        _awaitingArg = null;
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
        _boughtSub.Dispose();
        _failedSub.Dispose();
        _refusedSub.Dispose();
        _notInShopSub.Dispose();
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = null;
    }
}
