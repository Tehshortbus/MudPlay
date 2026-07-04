using System.Text;
using FujinTerm.Game;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class PromptParserTests
{
    private static (WirePromptScanner scanner, PlayerState state, PromptParser parser) Setup()
    {
        WirePromptScanner scanner = new();
        PlayerState state = new();
        PromptParser parser = new(scanner, state);
        return (scanner, state, parser);
    }

    private static void Feed(WirePromptScanner scanner, string text)
        => scanner.Append(Encoding.Latin1.GetBytes(text));

    [Fact]
    public void BasicHpMaPrompt_PopulatesHpAndMa()
    {
        var (scanner, state, _) = Setup();
        Feed(scanner, "[HP=779/MA=571]:");

        Assert.Equal(779,            state.Hp);
        Assert.Equal(571,            state.Ma);
        Assert.Equal(ManaType.Mana,  state.ManaType);
        Assert.Equal(PlayerPosition.Standing, state.Position);
        Assert.True(state.HasPromptData);
    }

    [Fact]
    public void KaiPrompt_FlagsManaTypeKai()
    {
        var (scanner, state, _) = Setup();
        Feed(scanner, "[HP=44/KAI=2]:");

        Assert.Equal(44, state.Hp);
        Assert.Equal(2,  state.Ma);
        Assert.Equal(ManaType.Kai, state.ManaType);
    }

    [Fact]
    public void HpOnlyPrompt_LeavesManaZeroAndManaTypeNone()
    {
        var (scanner, state, _) = Setup();
        Feed(scanner, "[HP=120]:");

        Assert.Equal(120, state.Hp);
        Assert.Equal(0,   state.Ma);
        Assert.Equal(ManaType.None, state.ManaType);
    }

    [Fact]
    public void RestingTrailingParens_SetsPosition()
    {
        var (scanner, state, _) = Setup();
        Feed(scanner, "[HP=779/MA=571]: (Resting)");
        Assert.Equal(PlayerPosition.Resting, state.Position);
    }

    [Fact]
    public void MeditatingLeadingParens_SetsPosition()
    {
        var (scanner, state, _) = Setup();
        Feed(scanner, "[HP=44/KAI=2 (Meditating) ]:");
        Assert.Equal(PlayerPosition.Meditating, state.Position);
    }

    [Fact]
    public void MaxHpMaxMa_RatchetWithLargestObserved()
    {
        var (scanner, state, _) = Setup();
        Feed(scanner, "[HP=400/MA=100]:");
        Feed(scanner, "[HP=500/MA=120]:");
        Feed(scanner, "[HP=300/MA=80]:");   // dropped lower; max stays put.

        Assert.Equal(500, state.MaxHp);
        Assert.Equal(120, state.MaxMa);
        Assert.Equal(300, state.Hp);                // current value reflects last prompt.
        Assert.Equal(80,  state.Ma);
    }

    [Fact]
    public void StatScreenMax_SnapsMaxAbovePromptHighWaterMark()
    {
        var (scanner, state, parser) = Setup();
        Feed(scanner, "[HP=240/MA=90]:");           // prompt learns a low HWM.
        Assert.Equal(240, state.MaxHp);

        parser.ApplyStatScreenMax(320, 140);         // stat screen is authoritative.
        Assert.Equal(320, state.MaxHp);
        Assert.Equal(140, state.MaxMa);
    }

    [Fact]
    public void StatScreenMax_OverridesDownward_BecauseStatScreenIsTruth()
    {
        var (scanner, state, parser) = Setup();
        Feed(scanner, "[HP=500/MA=200]:");           // a spuriously-high HWM.
        parser.ApplyStatScreenMax(320, 140);
        Assert.Equal(320, state.MaxHp);
        Assert.Equal(140, state.MaxMa);
    }

    [Fact]
    public void StatScreenMax_LaterLowHp_DoesNotDropTheMax()
    {
        var (scanner, state, parser) = Setup();
        parser.ApplyStatScreenMax(320, 140);
        Feed(scanner, "[HP=100/MA=30]:");            // a low current reading.
        Assert.Equal(100, state.Hp);
        Assert.Equal(320, state.MaxHp);              // max stays authoritative.
        Assert.Equal(140, state.MaxMa);
    }

    [Fact]
    public void StatScreenMax_NonPositive_LeavesLearnedMaxIntact()
    {
        var (scanner, state, parser) = Setup();
        Feed(scanner, "[HP=320/MA=140]:");
        parser.ApplyStatScreenMax(0, 0);             // failed / absent parse.
        Assert.Equal(320, state.MaxHp);
        Assert.Equal(140, state.MaxMa);
    }

    [Fact]
    public void NoPromptDataYet_HasPromptDataIsFalse()
    {
        var (_, state, _) = Setup();
        Assert.False(state.HasPromptData);
        Assert.Equal(0, state.Hp);
    }

    [Fact]
    public void Dispose_UnsubscribesAndStopsUpdates()
    {
        var (scanner, state, parser) = Setup();
        Feed(scanner, "[HP=100/MA=50]:");
        Assert.Equal(100, state.Hp);

        parser.Dispose();
        Feed(scanner, "[HP=999/MA=999]:");
        Assert.Equal(100, state.Hp);                // no further updates after dispose.
        Assert.Equal(50,  state.Ma);
    }
}
