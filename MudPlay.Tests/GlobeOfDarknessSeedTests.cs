using System;
using System.Collections.Generic;
using System.IO;
using MudPlay.Game.Conditions;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Globe of darkness against the shipped message records. It takes the light away
// and is no blindness (no Blindness ability, and cure blindness doesn't lift it),
// but its applied line is also blind globe's, which is one. A nanati shadow's
// globe set Blinded and had the party asked for a cure.
public sealed class GlobeOfDarknessSeedTests : IDisposable
{
    // The game's output, fed the way the client receives it.
    private readonly ServerFeed _feed = new();

    private readonly string _dir;

    public GlobeOfDarknessSeedTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mudplay-globe-seed-" + Path.GetRandomFileName());
        AppPaths.ExtractEmbeddedSeeds(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp cleanup */ }
    }

    private ConditionTracker TrackerOver(string seedFile, out List<MessageRecord> applied)
    {
        var store = new MessageStore();
        foreach (MessageRecord r in JsonStore.Load<List<MessageRecord>>(Path.Combine(_dir, seedFile)) ?? new())
            store.Messages.Add(r);
        var tracker = new ConditionTracker(store);
        tracker.AttachLineExtractor(_feed.Lines);
        List<MessageRecord> seen = new();
        tracker.ConditionApplied += seen.Add;
        applied = seen;
        return tracker;
    }

    private void Feed(string text) => _feed.Line(text);

    [Theory]
    [InlineData("Messages.paradigm.seed.json")]
    [InlineData("Messages.stock.seed.json")]
    public void TheShadowsGlobe_IsNotABlindness(string seedFile)
    {
        using ConditionTracker tracker = TrackerOver(seedFile, out List<MessageRecord> applied);

        Feed("The thin nanati shadow makes a clutching gesture!");
        Feed("A bubble of pure darkness appears and begins to expand!");
        Feed("You are enveloped in darkness!");

        Assert.False(tracker.IsBlinded);
        Assert.Contains(applied, r => r.Name == "globe of darkness");
        Assert.DoesNotContain(applied, r => r.Name == "blind globe");

        Feed("The unnatural darkness lifts.");
        Assert.DoesNotContain(applied, r => tracker.IsActiveByName(r.Name));
    }

    [Theory]
    [InlineData("Messages.paradigm.seed.json")]
    [InlineData("Messages.stock.seed.json")]
    public void ABlindGlobe_WhichPrintsNoCastLine_StillBlinds(string seedFile)
    {
        using ConditionTracker tracker = TrackerOver(seedFile, out _);

        Feed("You are enveloped in darkness!");

        Assert.True(tracker.IsBlinded);
    }
}
