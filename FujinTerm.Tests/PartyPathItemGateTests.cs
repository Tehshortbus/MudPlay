using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using FujinTerm.Game.Map;
using FujinTerm.Game.Remote;
using Xunit;

namespace FujinTerm.Tests;

public sealed class PartyPathItemGateTests
{
    /// <summary>
    /// Harness driving the gate with fully synchronous seams: the probe
    /// <c>query</c> returns a pre-canned result via <see cref="SetResult"/>,
    /// <c>post</c> runs inline, and the give hand-off's wire is captured. Since
    /// the query task is already completed, <c>OnPathItemsRequired</c> runs the
    /// whole check-then-act path before it returns, so assertions read the
    /// captured wire / forwards directly.
    /// </summary>
    private sealed class Harness
    {
        public readonly HashSet<int> Carried = new();
        public bool Enabled = true;
        public bool InParty = true;
        public string? SelfGiven = "Fujin";
        public readonly Dictionary<int, string> Names = new();
        public readonly Dictionary<int, PartyInventoryProbe.PartyItemResult> Results = new();
        public readonly List<int> Forwarded = new();
        public readonly List<string> Sent = new();
        public int QueryCount;
        public Func<int, string, Task<PartyInventoryProbe.PartyItemResult>>? QueryOverride;
        public readonly PartyPathItemGate Gate;

        public Harness(bool bindWire = true)
        {
            Gate = new PartyPathItemGate(
                isCarried: id => Carried.Contains(id),
                query: (id, name) =>
                {
                    QueryCount++;
                    if (QueryOverride is not null) return QueryOverride(id, name);
                    return Task.FromResult(Results.TryGetValue(id, out PartyInventoryProbe.PartyItemResult r)
                        ? r
                        : PartyInventoryProbe.PartyItemResult.Empty(id));
                },
                itemName: id => Names.TryGetValue(id, out string? n) ? n : null,
                isEnabled: () => Enabled,
                inParty: () => InParty,
                selfGivenName: () => SelfGiven,
                forward: ids => Forwarded.AddRange(ids),
                post: a => a(),
                log: null);
            if (bindWire)
                Gate.SetWireSender(b => Sent.Add(Encoding.Latin1.GetString(b)));
        }

        public void SetResult(int id, params (string given, int count)[] counts)
        {
            var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int total = 0;
            foreach ((string given, int count) c in counts) { dict[c.given] = c.count; total += c.count; }
            Results[id] = new PartyInventoryProbe.PartyItemResult(id, total, counts.Length, counts.Length, dict);
        }
    }

    [Fact]
    public void FeatureOff_ForwardsWholeListUnchanged()
    {
        var h = new Harness { Enabled = false };
        h.Gate.OnPathItemsRequired(new[] { 1, 2 });
        Assert.Equal(new[] { 1, 2 }, h.Forwarded);
        Assert.Empty(h.Sent);
        Assert.Equal(0, h.QueryCount);
    }

    [Fact]
    public void Solo_ForwardsWholeListUnchanged()
    {
        var h = new Harness { InParty = false };
        h.Gate.OnPathItemsRequired(new[] { 7 });
        Assert.Equal(new[] { 7 }, h.Forwarded);
        Assert.Equal(0, h.QueryCount);
    }

    [Fact]
    public void AlreadyCarried_SkippedNotProbedNotForwarded()
    {
        var h = new Harness();
        h.Carried.Add(1);
        h.Names[1] = "rope";
        h.Gate.OnPathItemsRequired(new[] { 1 });
        Assert.Empty(h.Forwarded);
        Assert.Empty(h.Sent);
        Assert.Equal(0, h.QueryCount);
    }

    [Fact]
    public void NoItemName_ForwardsWithoutProbing()
    {
        var h = new Harness();   // Names has no entry for 5
        h.Gate.OnPathItemsRequired(new[] { 5 });
        Assert.Equal(new[] { 5 }, h.Forwarded);
        Assert.Equal(0, h.QueryCount);
    }

    [Fact]
    public void SingleHolderWithSpare_UsesPartyGive()
    {
        var h = new Harness();
        h.Names[1] = "rope";
        h.SetResult(1, ("Bob", 3));   // only Bob has it, and has a spare
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Equal("@party give rope to Fujin\r", Assert.Single(h.Sent));
        Assert.Empty(h.Forwarded);
    }

    [Fact]
    public void MultipleHolders_TargetsChosenHolderWithDo()
    {
        var h = new Harness();
        h.Names[1] = "rope";
        h.SetResult(1, ("Bob", 3), ("Al", 2));   // both hold copies
        h.Gate.OnPathItemsRequired(new[] { 1 });

        // Chosen holder is the one with the most copies; targeted @do avoids
        // collecting a duplicate from every holder.
        Assert.Equal("/Bob @do give rope to Fujin\r", Assert.Single(h.Sent));
        Assert.Empty(h.Forwarded);
    }

    [Fact]
    public void HolderHasOnlyOne_NoSpare_Forwards()
    {
        var h = new Harness();
        h.Names[1] = "rope";
        h.SetResult(1, ("Bob", 1));   // one copy — Bob keeps it for their own gate
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Empty(h.Sent);
        Assert.Equal(new[] { 1 }, h.Forwarded);
    }

    [Fact]
    public void NoMemberHasAny_Forwards()
    {
        var h = new Harness();
        h.Names[1] = "rope";
        h.SetResult(1, ("Bob", 0), ("Al", 0));
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Empty(h.Sent);
        Assert.Equal(new[] { 1 }, h.Forwarded);
    }

    [Fact]
    public void EmptyPartyResult_Forwards()
    {
        var h = new Harness();
        h.Names[1] = "rope";
        // No SetResult → probe returns Empty (nobody answered).
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Empty(h.Sent);
        Assert.Equal(new[] { 1 }, h.Forwarded);
    }

    [Fact]
    public void ItemArrivedDuringProbe_NoGiveNoForward()
    {
        var h = new Harness();
        h.Names[1] = "rope";
        // The item lands in inventory while the @have round-trip is in flight.
        h.QueryOverride = (id, _) =>
        {
            h.Carried.Add(id);
            var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Bob"] = 3 };
            return Task.FromResult(new PartyInventoryProbe.PartyItemResult(id, 3, 1, 1, dict));
        };
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Empty(h.Sent);       // no give — we already have it
        Assert.Empty(h.Forwarded);  // and no need posted
    }

    [Fact]
    public void NoWireSender_Forwards()
    {
        var h = new Harness(bindWire: false);
        h.Names[1] = "rope";
        h.SetResult(1, ("Bob", 3));
        h.Gate.OnPathItemsRequired(new[] { 1 });

        // Can't send the give with no wire — fall back to the demand pipeline.
        Assert.Empty(h.Sent);
        Assert.Equal(new[] { 1 }, h.Forwarded);
    }

    [Fact]
    public void BlankSelfName_Forwards()
    {
        var h = new Harness { SelfGiven = null };
        h.Names[1] = "rope";
        h.SetResult(1, ("Bob", 3));
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Empty(h.Sent);
        Assert.Equal(new[] { 1 }, h.Forwarded);
    }

    [Fact]
    public void MultipleDistinctIds_ProbedIndependently()
    {
        var h = new Harness();
        h.Names[1] = "rope";
        h.Names[2] = "boat";
        h.SetResult(1, ("Bob", 2));           // covered by a give
        h.SetResult(2, ("Bob", 0));           // shortfall — forwarded
        h.Gate.OnPathItemsRequired(new[] { 1, 2 });

        Assert.Equal("@party give rope to Fujin\r", Assert.Single(h.Sent));
        Assert.Equal(new[] { 2 }, h.Forwarded);
        Assert.Equal(2, h.QueryCount);
    }

    [Fact]
    public void DuplicateIds_ProbedOnce()
    {
        var h = new Harness();
        h.Names[1] = "rope";
        h.SetResult(1, ("Bob", 3));
        h.Gate.OnPathItemsRequired(new[] { 1, 1, 1 });

        Assert.Equal(1, h.QueryCount);
        Assert.Single(h.Sent);
    }

    [Fact]
    public void NonPositiveIds_Skipped()
    {
        var h = new Harness();
        h.Names[1] = "rope";
        h.SetResult(1, ("Bob", 2));
        h.Gate.OnPathItemsRequired(new[] { 0, -3, 1 });

        Assert.Equal(1, h.QueryCount);
        Assert.Single(h.Sent);
    }
}
