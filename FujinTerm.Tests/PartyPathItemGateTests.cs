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
    /// captured wire / forwards directly. <see cref="IsLeader"/> defaults to
    /// <c>false</c> — the follower self-borrow path — so the E1 tests read
    /// unchanged; leader-provisioning tests flip it on.
    /// </summary>
    private sealed class Harness
    {
        public readonly HashSet<int> Carried = new();
        public readonly Dictionary<int, int> SelfCounts = new();
        public bool Enabled = true;
        public bool SearchEnabled = true;
        public bool InParty = true;
        public bool IsLeader;
        public string? SelfGiven = "Fujin";
        public readonly Dictionary<int, string> Names = new();
        public readonly Dictionary<int, PartyInventoryProbe.PartyItemResult> Results = new();
        public readonly List<int> Forwarded = new();
        public readonly List<(int Id, int Qty)> ForwardedReq = new();
        public readonly List<string> Sent = new();
        public int QueryCount;
        public Func<int, string, Task<PartyInventoryProbe.PartyItemResult>>? QueryOverride;
        public readonly PartyPathItemGate Gate;

        public Harness(bool bindWire = true)
        {
            Gate = new PartyPathItemGate(
                isCarried: id => Carried.Contains(id),
                selfCount: id => SelfCounts.TryGetValue(id, out int v) ? v : (Carried.Contains(id) ? 1 : 0),
                query: (id, name) =>
                {
                    QueryCount++;
                    if (QueryOverride is not null) return QueryOverride(id, name);
                    return Task.FromResult(Results.TryGetValue(id, out PartyInventoryProbe.PartyItemResult r)
                        ? r
                        : PartyInventoryProbe.PartyItemResult.Empty(id));
                },
                itemName: id => Names.TryGetValue(id, out string? n) ? n : null,
                isEnabled: _ => Enabled,
                searchEnabled: () => SearchEnabled,
                inParty: () => InParty,
                selfIsLeader: () => IsLeader,
                selfGivenName: () => SelfGiven,
                forward: (ids, qty) =>
                {
                    foreach (int id in ids) { Forwarded.Add(id); ForwardedReq.Add((id, qty)); }
                },
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

    // ----- Off / solo pass-through (leader-agnostic) --------------------------

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

    // ----- Follower self-borrow (E1 behaviour, IsLeader = false) --------------

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

    // ----- Leader provisioning (IsLeader = true) ------------------------------

    [Fact]
    public void Leader_EveryoneHasOne_NoGivesNoForward()
    {
        var h = new Harness { IsLeader = true };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 1;
        h.SetResult(1, ("Bob", 1));   // self 1 + Bob 1 == party size 2
        h.Gate.OnPathItemsRequired(new[] { 1 });

        // Provisioned even though we carry it (a follower might not) — but the
        // pool is already whole, so nothing is handed out or forwarded.
        Assert.Empty(h.Sent);
        Assert.Empty(h.Forwarded);
        Assert.False(h.Gate.SearchDemandActive);
    }

    [Fact]
    public void Leader_SelfHasSurplus_GivesOwnToZeroHolder()
    {
        var h = new Harness { IsLeader = true };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 2;          // we hold two
        h.SetResult(1, ("Bob", 0));   // Bob has none; pool = 2, size = 2
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Equal("give rope to Bob\r", Assert.Single(h.Sent));
        Assert.Empty(h.Forwarded);
    }

    [Fact]
    public void Leader_HolderSurplus_DirectsHolderToZeroHolders()
    {
        var h = new Harness { IsLeader = true };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 0;
        // self 0 + Bob 3 == party size 3 (self, Bob, Al); Al has none.
        h.SetResult(1, ("Bob", 3), ("Al", 0));
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Equal(
            new[] { "/Bob @do give rope to Fujin\r", "/Bob @do give rope to Al\r" },
            h.Sent);
        Assert.Empty(h.Forwarded);
    }

    [Fact]
    public void Leader_MultiSourceSpreadsAcrossZeroHolders()
    {
        var h = new Harness { IsLeader = true };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 1;
        // self 1 + Bob 2 + Al 2 + Cy 0 + Dan 0 == party size 5.
        h.SetResult(1, ("Bob", 2), ("Al", 2), ("Cy", 0), ("Dan", 0));
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Equal(
            new[] { "/Bob @do give rope to Cy\r", "/Al @do give rope to Dan\r" },
            h.Sent);
        Assert.Empty(h.Forwarded);
    }

    [Fact]
    public void Leader_Shortfall_ForwardsAndArmsSearch()
    {
        var h = new Harness { IsLeader = true };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 0;
        h.SetResult(1, ("Bob", 0));   // pool 0 < size 2 — genuine shortfall
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Empty(h.Sent);                       // no hand-off yet — nothing to give
        Assert.Equal(new[] { 1 }, h.Forwarded);     // shortfall goes to the demand pipeline
        Assert.True(h.Gate.SearchDemandActive);     // and search stays armed for more copies
    }

    [Fact]
    public void Leader_Shortfall_ForwardsWholePartyCount()
    {
        var h = new Harness { IsLeader = true };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 0;
        h.SetResult(1, ("Bob", 0));   // party of two, pool empty
        h.Gate.OnPathItemsRequired(new[] { 1 });

        // The leader's own bag must reach one-per-member: size 2 − 0 held = 2.
        Assert.Equal((1, 2), Assert.Single(h.ForwardedReq));
    }

    [Fact]
    public void Leader_PartialPool_ForwardsRemainingShortfallCount()
    {
        var h = new Harness { IsLeader = true };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 0;
        // self, Bob, Al, Cy = party of four; Bob holds one, the rest are short.
        h.SetResult(1, ("Bob", 1), ("Al", 0), ("Cy", 0));
        h.Gate.OnPathItemsRequired(new[] { 1 });

        // size 4 − 1 already held elsewhere = 3 copies the leader must acquire.
        Assert.Equal((1, 3), Assert.Single(h.ForwardedReq));
    }

    [Fact]
    public void OffAndSolo_ForwardWithCountOne()
    {
        var off = new Harness { Enabled = false };
        off.Gate.OnPathItemsRequired(new[] { 1, 2 });
        Assert.All(off.ForwardedReq, r => Assert.Equal(1, r.Qty));

        var solo = new Harness { InParty = false };
        solo.Gate.OnPathItemsRequired(new[] { 7 });
        Assert.Equal((7, 1), Assert.Single(solo.ForwardedReq));
    }

    [Fact]
    public void Leader_ShortfallSearchOff_ForwardsOnceNoSearchArm()
    {
        var h = new Harness { IsLeader = true, SearchEnabled = false };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 0;
        h.SetResult(1, ("Bob", 0));
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Empty(h.Sent);
        Assert.Equal(new[] { 1 }, h.Forwarded);
        Assert.False(h.Gate.SearchDemandActive);    // no multi-copy search without the toggle
    }

    [Fact]
    public void Leader_ShortfallThenAcquires_RedistributesOnInventoryChange()
    {
        var h = new Harness { IsLeader = true };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 0;
        h.SetResult(1, ("Bob", 0));   // pool 0 < size 2
        h.Gate.OnPathItemsRequired(new[] { 1 });
        Assert.True(h.Gate.SearchDemandActive);
        Assert.Empty(h.Sent);

        // We search up two copies; the pool is now whole (self 2 + Bob 0 = 2).
        h.SelfCounts[1] = 2;
        h.Gate.OnInventoryChanged();

        Assert.Equal("give rope to Bob\r", Assert.Single(h.Sent));
        Assert.False(h.Gate.SearchDemandActive);    // slot cleared once handed out
    }

    [Fact]
    public void Leader_InventoryChangeStillShort_KeepsSearching()
    {
        var h = new Harness { IsLeader = true };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 0;
        h.SetResult(1, ("Bob", 0), ("Al", 0));   // pool 0 < size 3
        h.Gate.OnPathItemsRequired(new[] { 1 });

        // One copy found — still short of the party of three.
        h.SelfCounts[1] = 1;
        h.Gate.OnInventoryChanged();

        Assert.Empty(h.Sent);
        Assert.True(h.Gate.SearchDemandActive);
    }

    [Fact]
    public void Leader_NoWireSender_ForwardsInsteadOfGiving()
    {
        var h = new Harness(bindWire: false) { IsLeader = true };
        h.Names[1] = "rope";
        h.SelfCounts[1] = 2;
        h.SetResult(1, ("Bob", 0));   // pool whole, but we can't send the give
        h.Gate.OnPathItemsRequired(new[] { 1 });

        Assert.Empty(h.Sent);
        Assert.Equal(new[] { 1 }, h.Forwarded);
    }
}
