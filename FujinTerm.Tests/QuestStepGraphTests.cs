using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FujinTerm.Game.Quests;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 10.10b — <see cref="QuestStepGraph"/> drafts an ordered, followable step
/// checklist for a quest flag from its <c>TBInfo</c> chains. Cases reproduce the
/// real data-v1.11p shapes: a verb-led chain exposes its player command, a
/// guard-led chain does not; <c>minlevel</c>/<c>class</c>/alignment gates,
/// cross-flag prerequisites, and the <c>checkitem</c>→<c>takeitem</c>→<c>giveitem</c>
/// turn-in flow are surfaced; same-flag gates are folded into give-step order; and
/// the same step echoed from many rooms collapses to one entry. Synthetic seeded
/// caches keep it deterministic and CI-portable.
/// </summary>
public sealed class QuestStepGraphTests : IDisposable
{
    private readonly string _root;

    public QuestStepGraphTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-queststep-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // Seeds a TBInfo table from (Action, CalledFrom) chains and activates the set.
    private GameDataCache CacheWith(params (string Action, string? CalledFrom)[] chains)
    {
        string dir = Path.Combine(_root, "test-set");
        Directory.CreateDirectory(dir);
        var rows = chains.Select(c => new Dictionary<string, object?>
        {
            ["Action"] = c.Action,
            ["Called From"] = c.CalledFrom,
        }).ToArray();
        File.WriteAllText(Path.Combine(dir, "TBInfo.json"), JsonSerializer.Serialize(rows));
        GameDataCache cache = new(_root);
        cache.SwitchSet("test-set");
        return cache;
    }

    [Fact]
    public void Build_NoTbInfoTable_ReturnsEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_root, "test-set"));
        GameDataCache cache = new(_root);
        cache.SwitchSet("test-set");

        Assert.Empty(QuestStepGraph.Build(cache, flag: 50));
    }

    [Fact]
    public void Build_GuardLedChain_SurfacesLevelClassItemButNoCommand()
    {
        // Flag 50 (MageBane): chain leads with a failability guard, so it's reached
        // by dialogue branch — no player command, location is the anchor.
        GameDataCache cache = CacheWith(
            ("failability 50 850:class 2:minlevel 15 851:giveability 50 1:giveitem 646:text 400", "Textblock #398"));

        QuestStep step = Assert.Single(QuestStepGraph.Build(cache, 50));
        Assert.Equal(1, step.Order);
        Assert.Null(step.Command);
        Assert.Equal("Textblock #398", step.Location);
        Assert.Equal(15, step.RequiredLevel);
        Assert.Equal(new[] { 2 }, step.RequiredClasses);
        Assert.Equal(new[] { 646 }, step.GrantedItems);
        Assert.Empty(step.TurnInItems);
    }

    [Fact]
    public void Build_VerbLedChain_ExposesPlayerCommand()
    {
        // Flag 125 (Ice Sorceress): "sit throne" leads, so it's the command; the
        // same-flag checkability/testability gates are implied by order, not prereqs.
        GameDataCache cache = CacheWith(
            ("sit throne:nomonsters 1093:checkability 125 1:testability 125 1:minlevel 15 3383:addexp 750000:giveability 125 2:message 233",
             "Room 10/245"));

        QuestStep step = Assert.Single(QuestStepGraph.Build(cache, 125));
        Assert.Equal(2, step.Order);
        Assert.Equal("sit throne", step.Command);
        Assert.Equal("Room 10/245", step.Location);
        Assert.Equal(15, step.RequiredLevel);
        Assert.Empty(step.RequiredQuests);
    }

    [Fact]
    public void Build_TurnInFlow_CapturesCheckTakeGiveItemsAndAlignment()
    {
        // Flag 126 step 6: prove the item you were sent for, hand it over, get the
        // reward — checkitem 622, takeitem 622, giveitem 642 under a good-align gate.
        GameDataCache cache = CacheWith(
            ("goodaligned -51 801:testability 126 5:checkability 126 5:checkitem 622 3208:takeitem 622:giveability 126 6:giveitem 642:addexp 150000:text 354",
             "Textblock #352"));

        QuestStep step = Assert.Single(QuestStepGraph.Build(cache, 126));
        Assert.Equal(6, step.Order);
        Assert.Equal(QuestAlignment.Good, step.Alignment);
        Assert.Equal(new[] { 622 }, step.RequiredItems);
        Assert.Equal(new[] { 622 }, step.TurnInItems);
        Assert.Equal(new[] { 642 }, step.GrantedItems);
    }

    [Fact]
    public void Build_CrossFlagGates_BecomePrerequisites_SameFlagOmitted()
    {
        // A step gated on other quests' progress lists those as prerequisites; a
        // gate on its own flag is omitted (implied by give-step order).
        GameDataCache cache = CacheWith(
            ("giveability 200 3:checkability 156 1:testability 50 2:checkability 200 2", null));

        QuestStep step = Assert.Single(QuestStepGraph.Build(cache, 200));
        Assert.Equal(3, step.Order);
        Assert.Equal(new[] { (156, 1), (50, 2) }, step.RequiredQuests);
    }

    [Fact]
    public void Build_OrdersByGiveStep()
    {
        GameDataCache cache = CacheWith(
            ("go altar:giveability 133 9", "Room 1/3"),
            ("giveability 133 1", "Room 1/1"),
            ("giveability 133 5", "Room 1/2"));

        int[] order = QuestStepGraph.Build(cache, 133).Select(s => s.Order).ToArray();
        Assert.Equal(new[] { 1, 5, 9 }, order);
    }

    [Fact]
    public void Build_DeduplicatesIdenticalStepEchoedAcrossRooms()
    {
        // The two real flag-125 chains differ only in their guard style (failability
        // vs checkability/testability) — captured fields are identical, so one entry.
        GameDataCache cache = CacheWith(
            ("sit throne:failability 125:minlevel 15 3383:addexp 750000:addability 2 1:giveability 125 2:message 233", "Room 10/245"),
            ("sit throne:checkability 125 1:testability 125 1:minlevel 15 3383:addexp 750000:giveability 125 2:message 3384", "Room 10/245"));

        Assert.Single(QuestStepGraph.Build(cache, 125));
    }

    [Fact]
    public void Build_OnlyReturnsRequestedFlag_LastGrantWins()
    {
        // A chain terminally grants its last quest flag; querying the earlier flag
        // returns nothing, querying the terminal flag returns the step.
        GameDataCache cache = CacheWith(
            ("giveability 125 1:giveability 130 2", "Room 2/2"));

        Assert.Empty(QuestStepGraph.Build(cache, 125));
        QuestStep step = Assert.Single(QuestStepGraph.Build(cache, 130));
        Assert.Equal(2, step.Order);
    }
}
