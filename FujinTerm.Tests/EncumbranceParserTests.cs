using FujinTerm.Game;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 7.18 — EncumbranceParser pulls the bracket token (None / Light /
/// Medium / Heavy / Encumbered) from the <c>Encumbrance:</c> stat line
/// and pushes it into <see cref="PlayerState.Encumbrance"/>. This is
/// the sole writer of that field per the Phase 3 single-writer
/// invariant.
/// </summary>
public sealed class EncumbranceParserTests
{
    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    private static (MessageRouter router, PlayerState state, EncumbranceParser parser) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerState state = new();
        EncumbranceParser parser = new(router, state);
        return (router, state, parser);
    }

    // ----- ParseLevel pure unit -------------------------------------

    [Theory]
    [InlineData("Encumbrance:    0/2880  -  None  [0%]",        EncumbranceLevel.None)]
    [InlineData("Encumbrance:  500/2880  -  Light [17%]",       EncumbranceLevel.Light)]
    [InlineData("Encumbrance: 1500/2880  -  Medium [52%]",      EncumbranceLevel.Medium)]
    [InlineData("Encumbrance: 2200/2880  -  Heavy [76%]",       EncumbranceLevel.Heavy)]
    [InlineData("Encumbrance: 2900/2880  -  Encumbered [100%]", EncumbranceLevel.Encumbered)]
    public void ParseLevel_RecognisesAllBrackets(string line, EncumbranceLevel expected)
    {
        Assert.Equal(expected, EncumbranceParser.ParseLevel(line));
    }

    [Fact]
    public void ParseLevel_CaseInsensitive()
    {
        // Defensive — server uses fixed casing, but a forked realm
        // could conceivably tweak it. We don't want to silently miss.
        Assert.Equal(EncumbranceLevel.Light,
                     EncumbranceParser.ParseLevel("Encumbrance: 1/2 - light [50%]"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not an encumbrance line")]
    [InlineData("Encumbrance: garbage with no dash bracket")]
    public void ParseLevel_Unrecognised_ReturnsUnknown(string? line)
    {
        Assert.Equal(EncumbranceLevel.Unknown, EncumbranceParser.ParseLevel(line));
    }

    // ----- Live dispatch path ---------------------------------------

    [Fact]
    public void Dispatch_WritesIntoPlayerState()
    {
        var (router, state, _) = Setup();

        router.Dispatch(Line("Encumbrance: 1500/2880 - Medium [52%]"));

        Assert.Equal(EncumbranceLevel.Medium, state.Encumbrance);
    }

    [Fact]
    public void Dispatch_NonEncumbranceLine_DoesNotWrite()
    {
        var (router, state, _) = Setup();
        // Seed with a known prior value so we can prove the state
        // wasn't touched.
        state.Encumbrance = EncumbranceLevel.Light;

        router.Dispatch(Line("Forged gossips: hello"));

        Assert.Equal(EncumbranceLevel.Light, state.Encumbrance);
    }

    [Fact]
    public void Dispatch_UnchangedLevel_NoReentrantWrite()
    {
        // Idempotence — re-observing the same level on every prompt
        // refresh shouldn't churn the observable. We can't easily
        // intercept the [ObservableProperty] setter from outside, so
        // just confirm the second dispatch leaves state stable.
        var (router, state, _) = Setup();
        router.Dispatch(Line("Encumbrance: 0/100 - None [0%]"));
        Assert.Equal(EncumbranceLevel.None, state.Encumbrance);

        router.Dispatch(Line("Encumbrance: 0/100 - None [0%]"));
        Assert.Equal(EncumbranceLevel.None, state.Encumbrance);
    }

    [Fact]
    public void Dispose_StopsListening()
    {
        var (router, state, parser) = Setup();
        parser.Dispose();

        router.Dispatch(Line("Encumbrance: 2200/2880 - Heavy [76%]"));

        // Default `[ObservableProperty]` value before any write.
        Assert.Equal(EncumbranceLevel.Unknown, state.Encumbrance);
    }
}
