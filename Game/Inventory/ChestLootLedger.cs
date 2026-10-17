using System;
using System.Collections.Generic;
using System.Linq;

namespace MudPlay.Game.Inventory;

// What the chests opened in one Chest Offload session gave, built one open at a
// time: each open is diffed against the inventory read just before it, so items
// already carried — or picked up between opens — never count as chest loot. Sales
// and drops the game confirms take items back off. Service-free; the window feeds it the before/after carried lists.
public sealed class ChestLootLedger
{
    private readonly Dictionary<string, int> _loot = new(StringComparer.OrdinalIgnoreCase);
    // First-seen order, so the list doesn't reshuffle as later opens add to it.
    private readonly List<string> _order = new();

    // Add what one open gave: the carried-item gains between the inventory read
    // just before the open and the one after it. Returns those gains.
    public IReadOnlyList<(string Name, int Count)> AddOpen(
        IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        IReadOnlyList<(string Name, int Count)> gains = ChestOffloadPlanner.CarriedGains(before, after);
        foreach ((string name, int count) in gains)
        {
            if (!_loot.ContainsKey(name)) _order.Add(name);
            _loot[name] = _loot.GetValueOrDefault(name) + count;
        }
        return gains;
    }

    // Every listed item and its count, in first-seen order — what gets saved.
    public IReadOnlyList<(string Name, int Count)> Entries
        => _order.Select(name => (name, _loot[name])).ToList();

    public bool IsEmpty => _order.Count == 0;

    // Replace the list with a saved one.
    public void Load(IEnumerable<(string Name, int Count)> entries)
    {
        Clear();
        foreach ((string name, int count) in entries)
        {
            if (count <= 0 || string.IsNullOrWhiteSpace(name)) continue;
            if (!_loot.ContainsKey(name)) _order.Add(name);
            _loot[name] = _loot.GetValueOrDefault(name) + count;
        }
    }

    public void Clear()
    {
        _loot.Clear();
        _order.Clear();
    }

    // The player took this item off the list (it stays in the pack).
    public bool RemoveAll(string name) => _loot.ContainsKey(name) && Remove(name, int.MaxValue);

    // A full inventory read is the truth: lower each count to what's carried, and drop
    // what isn't carried at all (given away, used up, sold while nothing was watching).
    // Returns true when anything changed.
    public bool Prune(IReadOnlyList<string> carried)
    {
        Dictionary<string, int> carriedCounts = ChestOffloadPlanner.CountByName(carried);
        bool changed = false;
        foreach (string name in _order.ToList())
        {
            int have = carriedCounts.GetValueOrDefault(name);
            if (have >= _loot[name]) continue;
            changed = true;
            if (have > 0) _loot[name] = have;
            else Remove(name, int.MaxValue);
        }
        return changed;
    }

    // The game confirmed `count` of `name` left the pack (sold or dropped).
    public bool Remove(string name, int count)
    {
        if (!_loot.TryGetValue(name, out int held)) return false;
        if (held <= count)
        {
            _loot.Remove(name);
            _order.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        }
        else _loot[name] = held - count;
        return true;
    }

    // The chest loot still in the pack, each capped at what's actually carried now,
    // so an item that left by a route the window didn't see (given away, used up)
    // stops being offered for sale.
    public IReadOnlyList<(string Name, int Count)> Current(IReadOnlyList<string> carried)
    {
        Dictionary<string, int> carriedCounts = ChestOffloadPlanner.CountByName(carried);
        return _order
            .Select(name => (Name: name, Count: Math.Min(_loot[name], carriedCounts.GetValueOrDefault(name))))
            .Where(t => t.Count > 0)
            .ToList();
    }
}
