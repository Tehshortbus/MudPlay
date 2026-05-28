using FujinTerm.Game;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

public sealed class PromptParserTests
{
    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: true);

    private static (MessageRouter router, PlayerState state, PromptParser parser) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerState state = new();
        PromptParser parser = new(router, state);
        return (router, state, parser);
    }

    [Fact]
    public void BasicHpMaPrompt_PopulatesHpAndMa()
    {
        var (router, state, _) = Setup();
        router.Dispatch(Line("[HP=779/MA=571]:"));

        Assert.Equal(779,            state.Hp);
        Assert.Equal(571,            state.Ma);
        Assert.Equal(ManaType.Mana,  state.ManaType);
        Assert.Equal(PlayerPosition.Standing, state.Position);
        Assert.True(state.HasPromptData);
    }

    [Fact]
    public void KaiPrompt_FlagsManaTypeKai()
    {
        var (router, state, _) = Setup();
        router.Dispatch(Line("[HP=44/KAI=2]:"));

        Assert.Equal(44, state.Hp);
        Assert.Equal(2,  state.Ma);
        Assert.Equal(ManaType.Kai, state.ManaType);
    }

    [Fact]
    public void HpOnlyPrompt_LeavesManaZeroAndManaTypeNone()
    {
        var (router, state, _) = Setup();
        router.Dispatch(Line("[HP=120]:"));

        Assert.Equal(120, state.Hp);
        Assert.Equal(0,   state.Ma);
        Assert.Equal(ManaType.None, state.ManaType);
    }

    [Fact]
    public void RestingTrailingParens_SetsPosition()
    {
        var (router, state, _) = Setup();
        router.Dispatch(Line("[HP=779/MA=571]: (Resting)"));
        Assert.Equal(PlayerPosition.Resting, state.Position);
    }

    [Fact]
    public void MeditatingLeadingParens_SetsPosition()
    {
        var (router, state, _) = Setup();
        router.Dispatch(Line("[HP=44/KAI=2 (Meditating) ]:"));
        Assert.Equal(PlayerPosition.Meditating, state.Position);
    }

    [Fact]
    public void MaxHpMaxMa_RatchetWithLargestObserved()
    {
        var (router, state, _) = Setup();
        router.Dispatch(Line("[HP=400/MA=100]:"));
        router.Dispatch(Line("[HP=500/MA=120]:"));
        router.Dispatch(Line("[HP=300/MA=80]:"));   // dropped lower; max stays put.

        Assert.Equal(500, state.MaxHp);
        Assert.Equal(120, state.MaxMa);
        Assert.Equal(300, state.Hp);                // current value reflects last prompt.
        Assert.Equal(80,  state.Ma);
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
        var (router, state, parser) = Setup();
        router.Dispatch(Line("[HP=100/MA=50]:"));
        Assert.Equal(100, state.Hp);

        parser.Dispose();
        router.Dispatch(Line("[HP=999/MA=999]:"));
        Assert.Equal(100, state.Hp);                // no further updates after dispose.
        Assert.Equal(50,  state.Ma);
    }
}
