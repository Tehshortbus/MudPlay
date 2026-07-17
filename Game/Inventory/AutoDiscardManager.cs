using System.Collections.Generic;
using System.Text;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Inventory;

// Auto-discard items engine. When the pack holds an item flagged
// ItemOverlay.AutoDiscard above its keep floor, offloads the excess with
// drop <item name> — or hide <item name> when HideMode is set (OtherSettings
// "hide when discarding") — one command per copy (MajorMUD has no bulk-drop
// verb). The keep floor is MinToKeep when MustHaveMinimum is set; otherwise
// zero, so an unbanded flagged item is discarded entirely (the confirmed
// "no band → discard all" rule). A LoyalItem is never discarded even if also
// flagged AutoDiscard — loyalty (never-drop) is the safety flag and wins the
// contradiction.
//
// Exists to clean up chest dumps — open chest pours a set of random items
// straight into inventory that the player can't refuse — and unwanted
// auto-collected loot.
//
// Trigger: InventoryManager.Changed (wired in AppServices to OnInventoryChanged).
// Every inventory change re-evaluates the carried list. Offloads the engine has
// sent but not yet seen confirmed are held in _inFlight and subtracted from the
// live count, so the Changed events its own confirmations raise don't re-send.
// Own "You dropped X." lines clear the in-flight count via the PlayerDrops
// subscription; in HideMode the "You hid X." confirmation clears it via the
// UserHides subscription instead (both routes fold through ClearInFlight).
// Other players' drops (same PlayerDrops pattern, alternate branch) are ignored.
//
// HideMode also registers each hide in _suppressLog so the transaction-history
// forwarder can tell an auto-discard hide from a genuine stash — an engine hide
// is a discard, not a "stashed" item, so TryConsumeSuppressedHide lets the
// forwarder drop that ledger row while manual / stash-room hides (never
// registered here) still record.
//
// Master switch: AutoActionDefaults.AutoDiscard (shared with the Settings and
// Action-menu toggle). Runs UI-thread only (MessageRouter + Inventory.Changed
// both marshal upstream), so the dictionaries need no lock.
public sealed class AutoDiscardManager : IDisposable
{
    // LogService category — [AutoDiscard] rows per dropped item.
    public const string LogCategory = "AutoDiscard";

    // One resolved carried entry: the canonical item Number, the name to send to
    // the game, whether the user flagged it for discard, and how many copies to
    // keep (0 = discard all).
    public sealed record ResolvedDiscard(int Number, string Name, bool Discard, int KeepCount);

    private readonly Func<IReadOnlyList<string>> _carried;
    private readonly Func<string, ResolvedDiscard?> _resolve;
    private readonly Func<bool> _isEnabled;
    private readonly LogService? _log;
    private readonly IDisposable _dropSub;
    private readonly IDisposable _hideSub;

    // item Number → offloads sent but not yet confirmed by a self
    // "You dropped X." (drop mode) / "You hid X." (hide mode).
    private readonly Dictionary<int, int> _inFlight = new();

    // item Number → engine hides not yet claimed by the transaction-log
    // forwarder, so an auto-discard hide is kept out of the stash ledger.
    private readonly Dictionary<int, int> _suppressLog = new();

    // When true, offload with hide <item> (conceal on the ground) instead of
    // drop <item>. Live-mirrored from OtherSettings via AppServices ApplyToServices.
    public bool HideMode { get; set; }

    private Action<byte[]>? _wireSender;
    private bool _disposed;

    public AutoDiscardManager(
        MessageRouter router,
        Func<IReadOnlyList<string>> carriedItems,
        Func<string, ResolvedDiscard?> resolve,
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

        _dropSub = router.Subscribe(KnownPatterns.PlayerDrops, OnDropLine);
        _hideSub = router.Subscribe(KnownPatterns.UserHides, OnHideLine);
    }

    // Bind the wire sender — the gate-wrapped engine pipeline from
    // MainWindowViewModel.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Re-evaluate the carried list on any inventory change and drop each flagged
    // item down to its keep floor.
    public void OnInventoryChanged()
    {
        if (!_isEnabled() || _wireSender is null) return;

        // Group carried copies by resolved item Number so duplicate name strings
        // ("a torch", "a torch") count as two of one item.
        Dictionary<int, (ResolvedDiscard Item, int Count)> groups = new();
        foreach (string entry in _carried())
        {
            if (_resolve(entry) is not { Discard: true } item) continue;
            if (groups.TryGetValue(item.Number, out (ResolvedDiscard Item, int Count) g))
                groups[item.Number] = (g.Item, g.Count + 1);
            else
                groups[item.Number] = (item, 1);
        }

        foreach ((int number, (ResolvedDiscard item, int count)) in groups)
        {
            int inFlight = _inFlight.GetValueOrDefault(number);
            // Count that will remain once the outstanding drops land.
            int projected = count - inFlight;
            int toDrop = projected - item.KeepCount;
            if (toDrop <= 0) continue;

            _inFlight[number] = inFlight + toDrop;
            // Hide-mode offloads register so the transaction log can skip them.
            if (HideMode)
                _suppressLog[number] = _suppressLog.GetValueOrDefault(number) + toDrop;

            string verb = HideMode ? "hide" : "drop";
            for (int i = 0; i < toDrop; i++)
            {
                _log?.Info(LogCategory, $"discard item={item.Name} via {verb} (keep {item.KeepCount})");
                Send($"{verb} {item.Name}");
            }
        }
    }

    // Clear a pending drop when our own "You dropped X." confirmation arrives.
    // PlayerDrops is a combined pattern (Groups[0] = the other-player name for
    // "<name> drops X.", empty for the self "You dropped X." branch); only the
    // self branch confirms a command we sent.
    private void OnDropLine(MatchResult m)
    {
        if (m.Groups.Count < 2) return;
        if (!string.IsNullOrEmpty(m.Groups[0])) return;   // another player's drop
        ClearInFlight(m.Groups[1]);
    }

    // Clear a pending offload when our own "You hid X." confirmation arrives.
    // UserHides is a self-only single-group pattern (Groups[0] = the hidden item),
    // so unlike PlayerDrops there's no other-player branch to filter out. Coin
    // hides ("You hid 10 gold.") don't resolve to a flagged item, so they fall
    // through ClearInFlight's resolve guard harmlessly.
    private void OnHideLine(MatchResult m)
    {
        if (m.Groups.Count < 1) return;
        ClearInFlight(m.Groups[0]);
    }

    // Decrement the in-flight count for the item named in a self drop/hide
    // confirmation, so the Changed event that confirmation raises doesn't re-send.
    private void ClearInFlight(string itemName)
    {
        if (_resolve(itemName) is not { } item) return;
        if (_inFlight.TryGetValue(item.Number, out int f) && f > 0)
        {
            if (f == 1) _inFlight.Remove(item.Number);
            else _inFlight[item.Number] = f - 1;
        }
    }

    // The transaction-history forwarder calls this on every "You hid X." to ask
    // whether the hide was an auto-discard offload (skip the stash ledger) or a
    // genuine manual / stash-room hide (record it). An engine hide was registered
    // in _suppressLog at send time; claim one here (returns true). A hide we never
    // initiated isn't registered (returns false), so it still records.
    public bool TryConsumeSuppressedHide(string itemName)
    {
        if (_resolve(itemName) is not { } item) return false;
        if (_suppressLog.TryGetValue(item.Number, out int n) && n > 0)
        {
            if (n == 1) _suppressLog.Remove(item.Number);
            else _suppressLog[item.Number] = n - 1;
            return true;
        }
        return false;
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
        _dropSub.Dispose();
        _hideSub.Dispose();
    }
}
