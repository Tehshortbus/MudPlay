using System.Collections.Generic;
using System.Text;
using MudPlay.Game;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// The `stat` screen's modified-stat marks and its effect list (report
// paradigm-20260930-160602): Paradigm colours a modified value bright red and a
// trained one cyan; Stock also puts a `*` in front of a modified one.
public sealed class StatScreenMarksTests
{
    // The game's output, fed the way the client receives it.
    private readonly ServerFeed _feed = new();

    private const int Green = 2, Cyan = 6, Red = 1;

    private readonly PlayerStats _stats = new();
    private readonly StatParser _parser;

    public StatScreenMarksTests()
    {
        _parser = new StatParser(_stats);
        _parser.AttachLineExtractor(_feed.Lines);
        _parser.ObserveOutbound(Encoding.Latin1.GetBytes("stat\r"));
    }

    // A line built from coloured runs, the way the emulator hands it over: `1;31`
    // arrives as palette index 1 with the bold flag, `0;36` as index 6.
    private void Feed(params (string Text, int Colour)[] runs)
    {
        _feed.Coloured(runs.Select(r => (r.Text, r.Colour, Bold: r.Colour == Red)));
    }

    private void FeedHeader() =>
        Feed(("Name: ", Green), ("Someone Somewhere              ", Cyan), ("Lives/CP: ", Green), ("    9/26", Cyan));

    // The three stat rows as Paradigm prints them, Strength and Agility in strColour / aglColour.
    private void FeedParadigmStats(int str, int strColour, int agl, int aglColour)
    {
        Feed(("Strength:  ", Green), ($"{str,-7}", strColour), ("Agility: ", Green), ($"{agl,-12}", aglColour),
             ("Tracking:     ", Green), ("   0", Cyan));
        Feed(("Intellect: ", Green), ("40     ", Cyan), ("Health:  ", Green), ("60          ", Cyan),
             ("Martial Arts: ", Green), ("  97", Cyan));
        Feed(("Willpower: ", Green), ("30     ", Cyan), ("Charm:   ", Green), ("30          ", Cyan),
             ("MagicRes: ", Green), ("      47", Cyan));
    }

    private void ClosePrompt() => _feed.Line("[HP=318/KAI=14]:");

    [Fact]
    public void Paradigm_RedValuesAreModified_CyanAreNot()
    {
        FeedHeader();
        FeedParadigmStats(153, Red, 90, Red);
        ClosePrompt();

        Assert.Equal(153, _stats.Strength);
        Assert.Equal(90, _stats.Agility);
        Assert.True(_stats.ModifiedMarksRead);
        Assert.Equal(StatSet.Strength | StatSet.Agility, _stats.ModifiedStats);
    }

    [Fact]
    public void Paradigm_AllCyan_ReadsAsNothingModified()
    {
        FeedHeader();
        FeedParadigmStats(133, Cyan, 100, Cyan);
        ClosePrompt();

        Assert.True(_stats.ModifiedMarksRead);
        Assert.Equal(StatSet.None, _stats.ModifiedStats);
    }

    [Fact]
    public void EffectList_IsCapturedWithItsCountdownCutOff()
    {
        FeedHeader();
        FeedParadigmStats(153, Red, 90, Red);
        _feed.Line("You feel powerful! (54s)");
        _feed.Line("You feel strong, but clumsy! (1m 36s)");
        ClosePrompt();

        Assert.Equal(
            new[] { new StatusEffectLine("You feel powerful!", true), new StatusEffectLine("You feel strong, but clumsy!", true) },
            _stats.ActiveEffects);
    }

    [Fact]
    public void Stock_AsteriskMarksModified_AndBareLinesAreEffects()
    {
        FeedHeader();
        // Stock: cyan + a space for a trained value, red + `*` for a modified one.
        Feed(("Strength: ", Green), ("*80   ", Red), ("  Agility:", Green), (" 50   ", Cyan), ("       Tracking:    ", Green), ("    0", Cyan));
        Feed(("Intellect:", Green), (" 40   ", Cyan), ("  Health: ", Green), (" 60   ", Cyan), ("       Martial Arts:", Green), ("    0", Cyan));
        Feed(("Willpower:", Green), (" 30   ", Cyan), ("  Charm:  ", Green), (" 30   ", Cyan), ("       MagicRes:    ", Green), ("   47", Cyan));
        _feed.Line("You are in the front rank of your group.");
        _feed.Line("You feel strong!");
        ClosePrompt();

        Assert.Equal(80, _stats.Strength);
        Assert.True(_stats.ModifiedMarksRead);
        Assert.Equal(StatSet.Strength, _stats.ModifiedStats);
        Assert.Equal(new[] { new StatusEffectLine("You feel strong!", false) }, _stats.ActiveEffects);
    }

    [Fact]
    public void Asterisk_AloneIsEnoughToMarkAStat_ButAColourlessScreenIsNotRead()
    {
        _feed.Line("Name: Someone Somewhere                  Lives/CP:      9/26");
        _feed.Line("Strength: *80     Agility:  50          Tracking:        0");
        _feed.Line("Intellect: 40     Health:   60          Martial Arts:    0");
        _feed.Line("Willpower: 30     Charm:    30          MagicRes:       47");
        ClosePrompt();

        // Strength is known modified, but nothing says the other five are not.
        Assert.Equal(StatSet.Strength, _stats.ModifiedStats);
        Assert.False(_stats.ModifiedMarksRead);
    }

    [Fact]
    public void ANewScreen_TakesTheMarksDownUntilItHasBeenReadWhole()
    {
        FeedHeader();
        FeedParadigmStats(133, Cyan, 100, Cyan);
        ClosePrompt();
        Assert.True(_stats.ModifiedMarksRead);

        _parser.ObserveOutbound(Encoding.Latin1.GetBytes("stat\r"));
        FeedHeader();
        // Mid-screen: this screen's values are landing, the marks are the last screen's.
        Assert.False(_stats.ModifiedMarksRead);
        FeedParadigmStats(153, Red, 90, Red);
        Assert.False(_stats.ModifiedMarksRead);

        ClosePrompt();
        Assert.True(_stats.ModifiedMarksRead);
        Assert.Equal(StatSet.Strength | StatSet.Agility, _stats.ModifiedStats);
    }

    [Fact]
    public void AnExpLine_LeavesTheMarksAlone()
    {
        FeedHeader();
        FeedParadigmStats(153, Red, 90, Red);
        ClosePrompt();

        _parser.ObserveOutbound(Encoding.Latin1.GetBytes("stat\r"));
        _feed.Line("Exp: 125379954 Level: 31 Exp needed for next level: 100 (200) [50%]");
        ClosePrompt();

        Assert.True(_stats.ModifiedMarksRead);
        Assert.Equal(StatSet.Strength | StatSet.Agility, _stats.ModifiedStats);
    }

    [Fact]
    public void TheCapturedParadigmStream_ReadsRedAsModified_ThroughTheEmulator()
    {
        // The wire as report paradigm-20260930-160602 captured it (name aside), fed
        // through the emulator and line extractor so the colours arrive as they do
        // live: `1;31` on Strength and Agility, `0;36` on the rest.
        const string E = "\x1b[";
        string wire =
            $"{E}0m{E}79D{E}K{E}0;32mName: {E}0;36mSomeone Somewhere              {E}0;32mLives/CP: {E}0;36m    9/26\r\n"
            + $"{E}0;32mRace: {E}0;36mHalf-Orc    {E}0;32mExp: {E}0;36m125379954       {E}0;32mPerception: {E}0;36m    50\r\n"
            + $"{E}0;32mClass: {E}0;36mMystic     {E}0;32mLevel: {E}0;36m31            {E}0;32mStealth: {E}0;36m      107\r\n"
            + $"{E}0;32mHits: {E}0;36m  318/321   {E}0;32mArmour Class: {E}0;36m 26/6  {E}0;32mThievery: {E}0;36m       0\r\n"
            + $"{E}0;32mKai:   {E}0;36m  14/30                         {E}0;32mTraps: {E}0;36m          0\r\n"
            + $"{E}0;32m                                       Picklocks: {E}0;36m      0\r\n"
            + $"{E}0;32mStrength:  {E}1;31m153    {E}0;32mAgility: {E}1;31m90          {E}0;32mTracking:     {E}0;36m   0\r\n"
            + $"{E}0;32mIntellect: {E}0;36m40     {E}0;32mHealth:  {E}0;36m60          {E}0;32mMartial Arts: {E}0;36m  97\r\n"
            + $"{E}0;32mWillpower: {E}0;36m30     {E}0;32mCharm:   {E}0;36m30          {E}0;32mMagicRes: {E}0;36m      47\r\n"
            + $"{E}0mYou feel powerful! (54s)\r\n"
            + $"{E}0mYou feel strong, but clumsy! (96s)\r\n";

        var stats = new PlayerStats();
        ManualTimers timers = new();
        using var parser = new StatParser(stats, schedule: timers.Arm);
        parser.AttachLineExtractor(_feed.Lines);
        var emulator = new TerminalEmulator(80, 24);
        parser.AttachLineExtractor(new LineExtractor(emulator));
        emulator.Feed(System.Text.Encoding.Latin1.GetBytes(wire));
        timers.RunAll();

        Assert.Equal(153, stats.Strength);
        Assert.Equal(26, stats.Cp);
        Assert.True(stats.ModifiedMarksRead);
        Assert.Equal(StatSet.Strength | StatSet.Agility, stats.ModifiedStats);
        Assert.Equal(
            new[] { new StatusEffectLine("You feel powerful!", true), new StatusEffectLine("You feel strong, but clumsy!", true) },
            stats.ActiveEffects);
    }

    [Fact]
    public void Stock_TheMarkSitsRightAfterTheColon_OnIntellectWillpowerAndAgility()
    {
        // Those three rows have no space between the colon and the value's colour
        // (`%sIntellect:%s%-5d`), so a modified one reads `Intellect:*40`.
        FeedHeader();
        Feed(("Strength: ", Green), (" 80   ", Cyan), ("  Agility:", Green), ("*50   ", Red), ("       Tracking:    ", Green), ("    0", Cyan));
        Feed(("Intellect:", Green), ("*40   ", Red), ("  Health: ", Green), (" 60   ", Cyan), ("       Martial Arts:", Green), ("    0", Cyan));
        Feed(("Willpower:", Green), ("*30   ", Red), ("  Charm:  ", Green), (" 30   ", Cyan), ("       MagicRes:    ", Green), ("   47", Cyan));
        ClosePrompt();

        Assert.Equal(50, _stats.Agility);
        Assert.Equal(40, _stats.Intellect);
        Assert.Equal(30, _stats.Willpower);
        Assert.True(_stats.ModifiedMarksRead);
        Assert.Equal(StatSet.Intellect | StatSet.Willpower | StatSet.Agility, _stats.ModifiedStats);
    }

    [Theory]
    [InlineData("Intellect:*40     Health:   60          Martial Arts:    0")]
    [InlineData("Willpower:*30     Charm:    30          MagicRes:       47")]
    [InlineData("Strength:  80     Agility:*50          Tracking:        0")]
    public void Stock_AMarkedRowIsStillRecognisedAsAStatRow(string row)
    {
        Assert.True(StatParser.IsStatScreenLine(row));
    }

    [Fact]
    public void ASavedSnapshot_BringsItsMarksAndEffectsBack()
    {
        FeedHeader();
        FeedParadigmStats(153, Red, 90, Red);
        _feed.Line("You feel strong, but clumsy! (96s)");
        ClosePrompt();

        // Through JSON, as the profile keeps it.
        var saved = System.Text.Json.JsonSerializer.Deserialize<MudPlay.Models.Profile.LastKnownStats>(
            System.Text.Json.JsonSerializer.Serialize(_parser.Snapshot()))!;
        var stats = new PlayerStats();
        using var parser = new StatParser(stats);
        parser.AttachLineExtractor(_feed.Lines);
        parser.Hydrate(saved);

        Assert.True(stats.ModifiedMarksRead);
        Assert.Equal(StatSet.Strength | StatSet.Agility, stats.ModifiedStats);
        Assert.Equal(new[] { new StatusEffectLine("You feel strong, but clumsy!", true) }, stats.ActiveEffects);
    }

    [Fact]
    public void ASnapshotSavedWithoutMarks_CarriesNone()
    {
        FeedHeader();
        FeedParadigmStats(153, Red, 90, Red);
        ClosePrompt();

        var old = _parser.Snapshot();
        old.ModifiedStats = null;   // as every snapshot was before marks were kept
        _parser.Hydrate(old);

        Assert.False(_stats.ModifiedMarksRead);
        Assert.Equal(StatSet.None, _stats.ModifiedStats);
        Assert.Empty(_stats.ActiveEffects);
    }
}
