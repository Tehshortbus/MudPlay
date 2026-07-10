using System.Collections.Generic;
using System.Text;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Inventory;

// Auto-discard items engine. When the pack holds an item flagged
// ItemOverlay.AutoDiscard above its keep floor, drops the excess with
// drop <item name> — one command per copy (MajorMUD has no bulk-drop verb).
// The keep floor is MinToKeep when MustHaveMinimum is set; otherwise zero, so an
// unbanded flagged item is discarded entirely (the confirmed "no band → discard
// all" rule). A LoyalItem is never discarded even if also flagged AutoDiscard —
// loyalty (never-drop) is the safety flag and wins the contradiction.
//
// Exists to clean up chest dumps — open chest pours a set of random items
// straight into inventory that the player can't refuse — and unwanted
// auto-collected loot.
//
// Trigger: InventoryManager.Changed (wired in AppServices to OnInventoryChanged).
// Every inventory change re-evaluates the carried list. Drops the engine has
// sent but not yet seen confirmed are held in _inFlight and subtracted from the
// live count, so the Changed events its own drop confirmations raise don't
// re-send. Own "You dropped X." lines clear the in-flight count via the
// PlayerDrops subscription; other players' drops (same pattern, alternate
// branch) are ignored.
//
// Master switch: AutoActionDefaults.AutoDiscard (shared with the Settings and
// Action-menu toggle). Runs UI-thread only (MessageRouter + Inventory.Changed
// both marshal upstream), so _inFlight needs no lock.
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

    // item Number → drops sent but not yet confirmed by a self "You dropped X."
    private readonly Dictionary<int, int> _inFlight = new();

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
            for (int i = 0; i < toDrop; i++)
            {
                _log?.Info(LogCategory, $"discard item={item.Name} (keep {item.KeepCount})");
                Send($"drop {item.Name}");
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
        if (_resolve(m.Groups[1]) is not { } item) return;
        if (_inFlight.TryGetValue(item.Number, out int f) && f > 0)
        {
            if (f == 1) _inFlight.Remove(item.Number);
            else _inFlight[item.Number] = f - 1;
        }
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
    }
}
