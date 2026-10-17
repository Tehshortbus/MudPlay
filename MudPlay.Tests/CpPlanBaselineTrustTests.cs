using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Inventory;
using MudPlay.Game.Spells;
using MudPlay.Game.Train;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels.CharacterWorkshop;
using Xunit;

namespace MudPlay.Tests;

// When the CP Allocation tab may act on its baseline by itself: rewrite plan rows
// against it and prune rows as trained. Only a `stat` reading whose marks are all
// accounted for, and on Stock one with no mark at all.
public sealed class CpPlanBaselineTrustTests : IDisposable
{
    // The game's output, fed the way the client receives it.
    private readonly ServerFeed _feed = new();

    private const int Green = 2, Cyan = 6, Red = 1;

    private readonly string _root;
    private readonly PlayerStats _stats = new();
    private readonly StatParser _parser;
    private readonly InventoryManager _inventory = new();
    private readonly MessageStore _messages = new();

    public CpPlanBaselineTrustTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-cptrust-" + Guid.NewGuid().ToString("N"));
        _parser = new StatParser(_stats);
        _parser.AttachLineExtractor(_feed.Lines);
    }

    public void Dispose()
    {
        _parser.Dispose();
        _inventory.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private (GameDataCache GameData, ListedEffectCatalog Effects) Set(bool paradigm)
    {
        string dir = Path.Combine(_root, "set");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Info.json"), paradigm ? "[{\"Legit\":2}]" : "[{\"Legit\":1}]");
        File.WriteAllText(Path.Combine(dir, "Races.json"),
            "[{\"Name\":\"Half-Orc\",\"mSTR\":55,\"xSTR\":160,\"mINT\":30,\"xINT\":130,\"mWIL\":30,\"xWIL\":135,"
            + "\"mAGL\":45,\"xAGL\":145,\"mHEA\":55,\"xHEA\":165,\"mCHM\":30,\"xCHM\":120}]");
        File.WriteAllText(Path.Combine(dir, "Spells.json"),
            "[{\"Number\":298,\"Name\":\"way of the bear\",\"MinBase\":20,\"MaxBase\":20,"
            + "\"Abil-0\":46,\"AbilVal-0\":20,\"Abil-1\":48,\"AbilVal-1\":-10}]");
        var gameData = new GameDataCache(_root);
        gameData.SwitchSet("set");
        return (gameData, new ListedEffectCatalog(_messages, gameData));
    }

    private void KnowTheBear() => _messages.Messages.Add(new MessageRecord(
        Id: "bear", Name: "way of the bear", Flags: MessageFlags.None, RawFlagsHex: 0, CasterMessage: string.Empty,
        TargetMessage: string.Empty, WitnessMessage: string.Empty, AppliedMessage: "You feel strong, but clumsy",
        AppliedEndsWith: string.Empty, Links: new[] { new GameDataLink("Spells", 298) }));

    private void Feed(params (string Text, int Colour)[] runs)
    {
        _feed.Coloured(runs.Select(r => (r.Text, r.Colour, Bold: false)));
    }

    private void FeedStat(string str, int strColour, string agl, int aglColour, string? effect = null)
    {
        _parser.ObserveOutbound(Encoding.Latin1.GetBytes("stat\r"));
        Feed(("Name: ", Green), ("Someone Somewhere              ", Cyan), ("Lives/CP: ", Green), ("    9/26", Cyan));
        Feed(("Race: ", Green), ("Half-Orc    ", Cyan), ("Exp: ", Green), ("125379954       ", Cyan), ("Perception: ", Green), ("    50", Cyan));
        Feed(("Class: ", Green), ("Mystic     ", Cyan), ("Level: ", Green), ("31            ", Cyan), ("Stealth: ", Green), ("      107", Cyan));
        Feed(("Strength: ", Green), (str.PadRight(7), strColour), ("Agility:", Green), (agl.PadRight(12), aglColour), ("Tracking:     ", Green), ("   0", Cyan));
        Feed(("Intellect: ", Green), ("40     ", Cyan), ("Health:  ", Green), ("60          ", Cyan), ("Martial Arts: ", Green), ("  97", Cyan));
        Feed(("Willpower: ", Green), ("30     ", Cyan), ("Charm:   ", Green), ("30          ", Cyan), ("MagicRes: ", Green), ("      47", Cyan));
        if (effect is not null) _feed.Line(effect);
        _feed.Line("[HP=318/KAI=14]:");
    }

    private CharacterPlanContext Resolve((GameDataCache GameData, ListedEffectCatalog Effects) set) =>
        CharacterPlanContext.Resolve(_stats, set.GameData, _inventory, set.Effects);

    [Fact]
    public void ACleanScreen_IsTrusted()
    {
        var set = Set(paradigm: true);
        FeedStat(" 133", Cyan, " 100", Cyan);

        CharacterPlanContext ctx = Resolve(set);

        Assert.True(ctx.BaselineTrusted);
        Assert.Equal(133, ctx.Baseline.Strength);
        Assert.Null(CpAllocationSectionViewModel.DescribeReading(ctx));
    }

    [Fact]
    public void Paradigm_ABuffTheCatalogueKnows_IsTrusted_AndGivesTheTrainedStats()
    {
        var set = Set(paradigm: true);
        KnowTheBear();
        FeedStat(" 153", Red, " 90", Red, "You feel strong, but clumsy! (96s)");

        CharacterPlanContext ctx = Resolve(set);

        Assert.True(ctx.BaselineTrusted);
        Assert.Equal(133, ctx.Baseline.Strength);
        Assert.Equal(100, ctx.Baseline.Agility);
    }

    [Fact]
    public void AStatNothingExplains_IsNotTrusted_AndTheTabSaysSo()
    {
        var set = Set(paradigm: true);
        FeedStat(" 153", Red, " 90", Red, "You feel strong, but clumsy! (96s)");

        CharacterPlanContext ctx = Resolve(set);

        Assert.False(ctx.BaselineTrusted);
        Assert.Contains("Rows aren't checked", CpAllocationSectionViewModel.DescribeReading(ctx));
    }

    [Fact]
    public void Stock_AnyMarkedStat_IsNotTrusted_EvenWhenTheBuffIsKnown()
    {
        // What a Stock cast adds to a stat isn't settled, and Stock doesn't train
        // with a stat altered: nothing is pruned or rewritten on such a reading.
        var set = Set(paradigm: false);
        KnowTheBear();
        FeedStat("*153", Red, "*90", Red, "You feel strong, but clumsy!");

        CharacterPlanContext ctx = Resolve(set);

        Assert.Equal(StatReadingState.Accounted, ctx.Reading.State);
        Assert.False(ctx.BaselineTrusted);
        Assert.Contains("won't open `train stats`", CpAllocationSectionViewModel.DescribeReading(ctx));
    }

    [Fact]
    public void AScreenWithNoMarksOnRecord_IsNotTrusted()
    {
        var set = Set(paradigm: true);
        FeedStat(" 133", Cyan, " 100", Cyan);
        LastKnownStats old = _parser.Snapshot();
        old.ModifiedStats = null;
        _parser.Hydrate(old);

        CharacterPlanContext ctx = Resolve(set);

        Assert.Equal(StatReadingState.Unverified, ctx.Reading.State);
        Assert.False(ctx.BaselineTrusted);
        Assert.Contains("No `stat` screen is on record", CpAllocationSectionViewModel.DescribeReading(ctx));
    }

    [Fact]
    public void TheEffectAnswer_IsKeptPerScreen_AndDroppedWhenTheCatalogueChanges()
    {
        var set = Set(paradigm: true);
        FeedStat(" 153", Red, " 90", Red, "You feel strong, but clumsy! (96s)");

        IReadOnlyList<ListedEffect> first = set.Effects.Read(_stats.ActiveEffects);
        Assert.Same(first, set.Effects.Read(_stats.ActiveEffects));
        Assert.Empty(first[0].Readings);

        KnowTheBear();
        Assert.Single(set.Effects.Read(_stats.ActiveEffects)[0].Readings);
    }

    // ----- what the grid does with an untrusted baseline -----------------------

    [Fact]
    public void AnUntrustedBaseline_NeverRewritesARow()
    {
        // Trained Strength 133, plan row 136; an unexplained +5 reads 138, and the
        // clamp would lift the row to it.
        var row = new CpPlanEntry(31, 136, 40, 30, 100, 60, 30);
        var clamped = new CpRowResult(31, 138, 40, 30, 100, 60, 30, CpEarnedTotal: 26, CpLeft: 26);

        Assert.Same(row, CpAllocationSectionViewModel.RowToShow(row, clamped, baselineTrusted: false));
        Assert.Equal(138, CpAllocationSectionViewModel.RowToShow(row, clamped, baselineTrusted: true).Strength);
    }

    // ----- the wait at a Stock trainer ----------------------------------------

    [Fact]
    public void TheWaitForAlteredStats_IsBounded_AndStartsOverWithANewRun()
    {
        var wait = new AlteredStatsWait();
        int rereads = AlteredStatsWait.Rereads(new AutoTrainerSettings().AlteredStatsWaitSeconds, TimeSpan.FromSeconds(20));
        Assert.Equal(6, rereads);   // the default two minutes, read every 20 s

        Assert.True(wait.TryClaimReread(rereads));
        Assert.True(wait.JustBegan);
        for (int i = 1; i < rereads; i++)
        {
            Assert.True(wait.TryClaimReread(rereads));
            Assert.False(wait.JustBegan);
        }
        Assert.False(wait.TryClaimReread(rereads));

        wait.Reset();
        Assert.True(wait.TryClaimReread(rereads));
        Assert.True(wait.JustBegan);
    }

    [Theory]
    [InlineData(0, 0)]      // don't wait
    [InlineData(1, 1)]      // any wait gets at least one more read
    [InlineData(45, 3)]     // covers the whole wait
    [InlineData(600, 30)]
    public void TheWaitSetting_BecomesAStatReadEveryInterval(int seconds, int expected)
    {
        Assert.Equal(expected, AlteredStatsWait.Rereads(seconds, TimeSpan.FromSeconds(20)));
        Assert.False(new AlteredStatsWait().TryClaimReread(AlteredStatsWait.Rereads(0, TimeSpan.FromSeconds(20))));
    }
}
