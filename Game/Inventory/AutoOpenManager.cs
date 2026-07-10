using System.Collections.Generic;
using System.Text;
using FujinTerm.Services;

namespace FujinTerm.Game.Inventory;

// Auto-open containers engine. When a container item (ItemType == Container)
// flagged ItemOverlay.AutoOpen newly enters the pack, sends open <item name>
// once so its contents spill without the player opening it by hand.
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

    private readonly Func<IReadOnlyList<string>> _carried;
    private readonly Func<string, ResolvedOpen?> _resolve;
    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _isLoaded;
    private readonly LogService? _log;

    // item Number → count of that flagged container seen in the previous
    // carried snapshot. Rebuilt on every change once seeded.
    private readonly Dictionary<int, int> _prevCounts = new();
    private bool _seeded;

    private Action<byte[]>? _wireSender;
    private bool _disposed;

    public AutoOpenManager(
        Func<IReadOnlyList<string>> carriedItems,
        Func<string, ResolvedOpen?> resolve,
        Func<bool> isEnabled,
        Func<bool> isLoaded,
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

        foreach ((int number, (ResolvedOpen item, int count)) in current)
        {
            int delta = count - _prevCounts.GetValueOrDefault(number);
            for (int i = 0; i < delta; i++)
            {
                _log?.Info(LogCategory, $"open container item={item.Name}");
                Send($"open {item.Name}");
            }
        }

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

    private void Send(string text)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(text + "\r"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
