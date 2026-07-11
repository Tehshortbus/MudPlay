using System.Text;
using FujinTerm.Game.Inventory;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

// AutoOpenManager sends open <name> once for each flagged container that newly
// enters the pack, then a single 'i' so the client re-parses the post-open pack.
// The baseline is seeded silently on the first change after inventory loads, so
// containers already carried at connect aren't re-opened.
public sealed class AutoOpenManagerTests
{
    private sealed class Harness : IDisposable
    {
        public AutoOpenManager Open { get; }
        public List<byte[]> Sent { get; } = new();

        // Mutable carried list the manager reads on each change.
        public List<string> Carried { get; } = new();

        // canonical name -> (Number, AutoOpen flag). Absent = not an item.
        public Dictionary<string, (int Number, bool AutoOpen)> Items { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public bool Enabled { get; set; } = true;
        public bool Loaded { get; set; } = true;

        public Harness()
        {
            // useTimer: false — no Avalonia dispatcher in unit tests; the debounced
            // 'i' is driven explicitly via FlushInventoryForTests.
            Open = new AutoOpenManager(
                carriedItems: () => Carried,
                resolve: Resolve,
                isEnabled: () => Enabled,
                isLoaded: () => Loaded,
                useTimer: false);
            Open.SetWireSender(b => Sent.Add(b));
        }

        private AutoOpenManager.ResolvedOpen? Resolve(string entry)
        {
            if (!Items.TryGetValue(entry.Trim(), out (int Number, bool AutoOpen) g))
                return null;
            return new AutoOpenManager.ResolvedOpen(g.Number, entry.Trim(), g.AutoOpen);
        }

        public List<string> SentText => Sent
            .Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r'))
            .ToList();

        // Seed the silent baseline (first change after load), then run again.
        public void SeedThenChange()
        {
            Open.OnInventoryChanged();
        }

        public void Dispose() => Open.Dispose();
    }

    [Fact]
    public void NewFlaggedContainer_OpensThenRequestsInventory()
    {
        using Harness h = new();
        h.Items["small sack"] = (100, true);

        h.SeedThenChange();                 // empty pack — seeds baseline
        h.Carried.Add("small sack");        // sack picked up
        h.Open.OnInventoryChanged();

        Assert.Equal(new[] { "open small sack" }, h.SentText);   // 'i' still pending

        h.Open.FlushInventoryForTests();    // debounce window elapses
        Assert.Equal(new[] { "open small sack", "i" }, h.SentText);
    }

    [Fact]
    public void MultipleContainers_SeparatePasses_OpenEach_ThenSingleInventory()
    {
        using Harness h = new();
        h.Items["small sack"] = (100, true);
        h.Items["leather pouch"] = (101, true);

        h.SeedThenChange();

        // Each pickup arrives as its own inventory change (separate "You took"
        // lines), even with different names — the real get-burst shape.
        h.Carried.Add("small sack");
        h.Open.OnInventoryChanged();
        h.Carried.Add("leather pouch");
        h.Open.OnInventoryChanged();

        // Both opens went out immediately; the 'i' is debounced to the end.
        Assert.Equal(
            new[] { "open small sack", "open leather pouch" },
            h.SentText);

        h.Open.FlushInventoryForTests();

        // A single 'i' at the end — not one per container.
        Assert.Equal(
            new[] { "open small sack", "open leather pouch", "i" },
            h.SentText);
    }

    [Fact]
    public void NoOpenFired_NoInventoryRequest()
    {
        using Harness h = new();
        h.Items["torch"] = (200, false);    // not a flagged container

        h.SeedThenChange();
        h.Carried.Add("torch");
        h.Open.OnInventoryChanged();
        h.Open.FlushInventoryForTests();    // nothing pending — no 'i'

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void ContainerCarriedAtConnect_NotReopened()
    {
        using Harness h = new();
        h.Items["small sack"] = (100, true);
        h.Carried.Add("small sack");        // already carried before first change

        h.SeedThenChange();                 // baseline includes the sack
        h.Open.OnInventoryChanged();        // no new copy entered

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void MasterDisabled_NoOpenNoInventory()
    {
        using Harness h = new() { Enabled = false };
        h.Items["small sack"] = (100, true);

        h.SeedThenChange();
        h.Carried.Add("small sack");
        h.Open.OnInventoryChanged();

        Assert.Empty(h.Sent);
    }
}
