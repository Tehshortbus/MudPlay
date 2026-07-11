using System.Collections.Generic;
using System.Text;
using Avalonia.Threading;
using FujinTerm.Services;

namespace FujinTerm.Game.Inventory;

// Auto-open containers engine. When a container item (ItemType == Container)
// flagged ItemOverlay.AutoOpen newly enters the pack, sends open <item name>
// once so its contents spill without the player opening it by hand, then a
// single 'i' so the client re-parses the post-open pack.
//
// Trigger: InventoryManager.Changed. Each change groups the current carried
// list by resolved item Number and diffs the flagged-container counts against
// the previous snapshot; a count increase fires one open per new copy. The
// baseline is seeded silently on the first change seen once inventory is loaded
// (a full 'i'), so containers already carried at connect aren't re-opened —
// only genuine new acquisitions trigger an open. Rebasing the baseline right
// after firing stops the open's own inventory-change echo (its contents
// pouring in) from re-firing.
//
// Inventory refresh is DEBOUNCED, not sent per open: picking up several
// containers at once arrives as separate "You took" lines — one Changed (and
// one open) per container — so firing an 'i' inside each pass would spam one
// dump per container. Instead each open (re)starts a short timer and a single
// 'i' goes out once the burst settles, re-parsing the whole post-open pack in
// one shot.
//
// Master switch: AutoActionDefaults.AutoGetItems — the umbrella item-automation
// toggle shared with auto-get / discard / buy / sell. The per-item AutoOpen
// overlay flag is the real per-item gate. Runs UI-thread only (Inventory.Changed
// marshals upstream), so the count maps need no lock.
public sealed class AutoOpenManager : IDisposable
{
    // LogService category — [AutoOpen] rows per opened container.
    public const string LogCategory = "AutoOpen";

    // One resolved carried entry: the item Number, the name to send to the
    // game, and whether it's a flagged container the engine should auto-open.
    public sealed record ResolvedOpen(int Number, string Name, bool AutoOpen);

    // Debounce window for the post-open inventory refresh — long enough to span
    // the gaps between the "You took" lines of a single get-burst, short enough
    // that the trailing 'i' still feels immediate.
    private static readonly TimeSpan InventoryRefreshDebounce =
        TimeSpan.FromMilliseconds(500);

    private readonly Func<IReadOnlyList<string>> _carried;
    private readonly Func<string, ResolvedOpen?> _resolve;
    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _isLoaded;
    private readonly LogService? _log;

    // item Number → count of that flagged container seen in the previous
    // carried snapshot. Rebuilt on every change once seeded.
    private readonly Dictionary<int, int> _prevCounts = new();
    private bool _seeded;

    // Coalesces the post-open 'i' across a burst of pickups. Null in tests
    // (useTimer: false) — FlushInventoryForTests drives the refresh instead.
    private readonly DispatcherTimer? _refreshTimer;
    private bool _inventoryPending;

    private Action<byte[]>? _wireSender;
    private bool _disposed;

    // Default constructor — wires the in-process debounce timer. Use the
    // test-seam ctor (useTimer: false) for unit tests, where no Avalonia
    // dispatcher is running.
    public AutoOpenManager(
        Func<IReadOnlyList<string>> carriedItems,
        Func<string, ResolvedOpen?> resolve,
        Func<bool> isEnabled,
        Func<bool> isLoaded,
        LogService? log = null)
        : this(carriedItems, resolve, isEnabled, isLoaded, useTimer: true, log) { }

    internal AutoOpenManager(
        Func<IReadOnlyList<string>> carriedItems,
        Func<string, ResolvedOpen?> resolve,
        Func<bool> isEnabled,
        Func<bool> isLoaded,
        bool useTimer,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(carriedItems);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(isLoaded);
        _carried = carriedItems;
        _resolve = resolve;
        _isEnabled = isEnabled;
        _isLoaded = isLoaded;
        _log = log;

        if (useTimer)
        {
            _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = InventoryRefreshDebounce,
            };
            _refreshTimer.Tick += (_, _) => FlushInventoryRefresh();
        }
    }

    // Bind the wire sender — the gate-wrapped engine pipeline from
    // MainWindowViewModel.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Re-evaluate the pack on any inventory change and open each flagged
    // container that newly entered it since the last change.
    public void OnInventoryChanged()
    {
        if (_wireSender is null) return;
        // Wait for the first full 'i' so the baseline reflects the real pack —
        // a coin pickup can fire Changed before any inventory dump.
        if (!_isLoaded()) return;

        // Group current carried copies by resolved item Number, keeping only
        // flagged containers.
        Dictionary<int, (ResolvedOpen Item, int Count)> current = new();
        foreach (string entry in _carried())
        {
            if (_resolve(entry) is not { AutoOpen: true } item) continue;
            if (current.TryGetValue(item.Number, out (ResolvedOpen Item, int Count) g))
                current[item.Number] = (g.Item, g.Count + 1);
            else
                current[item.Number] = (item, 1);
        }

        // Seed the baseline silently the first time (once loaded) so containers
        // already carried at connect aren't opened — only later acquisitions do.
        if (!_seeded)
        {
            _seeded = true;
            RebaseTo(current);
            return;
        }

        // Master toggle off: keep the baseline in step with the pack (so
        // re-enabling doesn't retroactively open a container acquired while
        // disabled) but send nothing.
        if (!_isEnabled())
        {
            RebaseTo(current);
            return;
        }

        bool openedAny = false;
        foreach ((int number, (ResolvedOpen item, int count)) in current)
        {
            int delta = count - _prevCounts.GetValueOrDefault(number);
            for (int i = 0; i < delta; i++)
            {
                _log?.Info(LogCategory, $"open container item={item.Name}");
                Send($"open {item.Name}");
                openedAny = true;
            }
        }

        // A container's contents don't surface in the parsed pack until a fresh
        // inventory dump, so schedule one 'i' after any auto-open. The refresh is
        // debounced (see ScheduleInventoryRefresh) so a burst of container
        // pickups — each its own Changed/open — collapses to a single trailing
        // 'i'. Its eventual re-parse fires Changed again, but the baseline rebased
        // below already accounts for the opened containers, so that echo can't
        // re-open or re-schedule.
        if (openedAny)
            ScheduleInventoryRefresh();

        RebaseTo(current);
    }

    // Replace the baseline with the current flagged-container counts. Containers
    // that left the pack drop out (so re-acquiring one opens it again).
    private void RebaseTo(Dictionary<int, (ResolvedOpen Item, int Count)> current)
    {
        _prevCounts.Clear();
        foreach ((int number, (ResolvedOpen _, int count)) in current)
            _prevCounts[number] = count;
    }

    // Arm (or re-arm) the debounced inventory refresh. Each open restarts the
    // window, so a run of pickups fires a single 'i' once the last one settles.
    private void ScheduleInventoryRefresh()
    {
        _inventoryPending = true;
        if (_refreshTimer is null) return;   // test path drives FlushInventoryForTests
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    // Send the coalesced 'i' if one is pending. Idempotent — a spurious tick with
    // nothing pending is a no-op.
    private void FlushInventoryRefresh()
    {
        _refreshTimer?.Stop();
        if (!_inventoryPending) return;
        _inventoryPending = false;
        Send("i");
    }

    // Test seam — fire the debounced 'i' as if the coalescing window elapsed.
    internal void FlushInventoryForTests() => FlushInventoryRefresh();

    private void Send(string text)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(text + "\r"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer?.Stop();
    }
}
