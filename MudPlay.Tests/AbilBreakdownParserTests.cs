using System.Collections.Generic;
using MudPlay.Game;
using Xunit;

namespace MudPlay.Tests;

public sealed class AbilBreakdownParserTests
{
    // The game's output, fed the way the client receives it.
    private readonly ServerFeed _feed = new();

    private (AbilBreakdownParser parser, List<AbilBreakdown> captured) NewParser()
    {
        AbilBreakdownParser parser = new();
        parser.AttachLineExtractor(_feed.Lines);
        List<AbilBreakdown> captured = new();
        parser.BreakdownParsed += captured.Add;
        return (parser, captured);
    }

    // The exact four-source capture from a live Paradigm `abil 145`.
    private static readonly string[] LiveCapture =
    {
        "granted:  ManaRegen(145)              0005",
        "worn:     ManaRegen(145)              0185",
        "spells:   ManaRegen(145)              0011",
        "race:     ManaRegen(145)              0010",
    };

    [Fact]
    public void ParsesEverySourceFromTheLiveCapture()
    {
        (AbilBreakdownParser parser, List<AbilBreakdown> captured) = NewParser();

        _feed.Rows(LiveCapture);
        _feed.Line("[HP=899/MA=573]: (Resting)");

        AbilBreakdown b = Assert.Single(captured);
        Assert.Equal(145, b.Code);
        Assert.Equal(5, b.From("granted"));
        Assert.Equal(185, b.From("worn"));
        Assert.Equal(11, b.Spells);
        Assert.Equal(10, b.From("race"));
        Assert.Equal(211, b.Total);
    }

    [Fact]
    public void SpellsSliceIsTheRolledContribution()
    {
        // The `spells:` line is exactly what nature tap / mana flux rolled.
        (AbilBreakdownParser parser, List<AbilBreakdown> captured) = NewParser();

        _feed.Rows(LiveCapture);
        _feed.Prompt();

        Assert.Equal(11, Assert.Single(captured).Spells);
    }

    [Fact]
    public void OmittedSourcesReadAsZero()
    {
        // The server only prints affected sources; a character with just a worn
        // bonus yields a single-row block.
        (AbilBreakdownParser parser, List<AbilBreakdown> captured) = NewParser();

        _feed.Line("worn:     ManaRegen(145)              0185");
        _feed.Prompt();

        AbilBreakdown b = Assert.Single(captured);
        Assert.Equal(185, b.From("worn"));
        Assert.Equal(0, b.Spells);
        Assert.Equal(0, b.From("granted"));
        Assert.Equal(0, b.From("race"));
        Assert.Equal(185, b.Total);
    }

    [Fact]
    public void TotalRowIsHeldAsideNotDoubleCounted()
    {
        (AbilBreakdownParser parser, List<AbilBreakdown> captured) = NewParser();

        _feed.Rows(new[]
        {
            "granted:  ManaRegen(145)              0005",
            "spells:   ManaRegen(145)              0011",
            "total:    ManaRegen(145)              0016",
        });
        _feed.Prompt();

        AbilBreakdown b = Assert.Single(captured);
        Assert.Equal(16, b.Total);            // 5 + 11, total row excluded from the sum
        Assert.Equal(2, b.Contributions.Count);
    }

    [Fact]
    public void ParsesNegativeSpellRoll()
    {
        // A bad nature-tap / mana-flux roll subtracts from mana regen.
        (AbilBreakdownParser parser, List<AbilBreakdown> captured) = NewParser();

        _feed.Line("spells:   ManaRegen(145)             -0050");
        _feed.Prompt();

        Assert.Equal(-50, Assert.Single(captured).Spells);
    }

    [Fact]
    public void PromptFlushesTheBlock()
    {
        (AbilBreakdownParser parser, List<AbilBreakdown> captured) = NewParser();

        _feed.Line("worn:     ManaRegen(145)              0185");
        Assert.Empty(captured);               // not yet closed

        _feed.Line("[HP=1/MA=1]: (Standing)");
        Assert.Single(captured);
    }

    [Fact]
    public void CommandEchoBeforeRowsDoesNotStartASpuriousBlock()
    {
        (AbilBreakdownParser parser, List<AbilBreakdown> captured) = NewParser();

        // The echoed command line is a non-matching content line; it must not
        // emit an empty breakdown.
        _feed.Line("[HP=899/MA=573]: (Resting) abil 145");
        _feed.Rows(LiveCapture);
        _feed.Prompt();

        Assert.Equal(211, Assert.Single(captured).Total);
    }

    [Fact]
    public void BackToBackDifferentCodesEmitTwoBreakdowns()
    {
        (AbilBreakdownParser parser, List<AbilBreakdown> captured) = NewParser();

        _feed.Line("worn:     ManaRegen(145)              0185");
        _feed.Line("worn:     HPRegen(123)                0040");
        _feed.Prompt();

        Assert.Equal(2, captured.Count);
        Assert.Equal(145, captured[0].Code);
        Assert.Equal(185, captured[0].From("worn"));
        Assert.Equal(123, captured[1].Code);
        Assert.Equal(40, captured[1].From("worn"));
    }

    [Fact]
    public void NonRowLineClosesAnInProgressBlock()
    {
        (AbilBreakdownParser parser, List<AbilBreakdown> captured) = NewParser();

        _feed.Line("spells:   ManaRegen(145)              0011");
        _feed.Line("You feel the surge fade.");   // arbitrary game text
        Assert.Single(captured);
        Assert.Equal(11, captured[0].Spells);
    }

    [Fact]
    public void FromLookupIsCaseInsensitive()
    {
        (AbilBreakdownParser parser, List<AbilBreakdown> captured) = NewParser();

        _feed.Line("spells:   ManaRegen(145)              0011");
        _feed.Prompt();

        AbilBreakdown b = Assert.Single(captured);
        Assert.Equal(11, b.From("SPELLS"));
        Assert.Equal(0, b.From("granted"));
    }
}
