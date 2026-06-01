using System.Collections.Generic;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class AliasEngineTests
{
    // ----- TryExpand: match shape ----------------------------------------

    [Fact]
    public void TryExpand_NoMatch_FallsThrough()
    {
        AliasEngine e = new();
        e.Aliases.Add(new Alias("gh", Enabled: true, Expansion: "get all here"));
        bool hit = e.TryExpand("hello world", out IReadOnlyList<string> steps);
        Assert.False(hit);
        Assert.Empty(steps);
    }

    [Fact]
    public void TryExpand_StaticAlias_SendsExpansionVerbatim()
    {
        AliasEngine e = new();
        e.Aliases.Add(new Alias("gh", Enabled: true, Expansion: "get all here"));
        Assert.True(e.TryExpand("gh", out IReadOnlyList<string> steps));
        Assert.Single(steps);
        Assert.Equal("get all here", steps[0]);
    }

    [Fact]
    public void TryExpand_DisabledAlias_FallsThrough()
    {
        AliasEngine e = new();
        e.Aliases.Add(new Alias("gh", Enabled: false, Expansion: "get all here"));
        Assert.False(e.TryExpand("gh", out _));
    }

    [Fact]
    public void TryExpand_MatchIsCaseInsensitive()
    {
        AliasEngine e = new();
        e.Aliases.Add(new Alias("gh", Enabled: true, Expansion: "get all here"));
        Assert.True(e.TryExpand("GH", out IReadOnlyList<string> steps));
        Assert.Equal("get all here", steps[0]);
    }

    [Fact]
    public void TryExpand_EmptyInput_FallsThrough()
    {
        AliasEngine e = new();
        e.Aliases.Add(new Alias("gh", Enabled: true, Expansion: "get all here"));
        Assert.False(e.TryExpand(string.Empty, out _));
        Assert.False(e.TryExpand("   ", out _));
    }

    // ----- Substitution: {0} = whole rest --------------------------------

    [Fact]
    public void Substitute_BraceZero_SubstitutesWholeRest()
    {
        AliasEngine e = new();
        e.Aliases.Add(new Alias("say", Enabled: true, Expansion: "yell {0}"));
        Assert.True(e.TryExpand("say hi everyone", out IReadOnlyList<string> steps));
        Assert.Equal("yell hi everyone", steps[0]);
    }

    [Fact]
    public void Substitute_BraceZero_EmptyRest_TrimsTrailingWhitespace()
    {
        AliasEngine e = new();
        e.Aliases.Add(new Alias("gh", Enabled: true, Expansion: "get all here {0}"));
        Assert.True(e.TryExpand("gh", out IReadOnlyList<string> steps));
        Assert.Equal("get all here", steps[0]);
    }

    // ----- Substitution: {N} positionals ---------------------------------

    [Fact]
    public void Substitute_PositionalArgs_BindByOrder()
    {
        AliasEngine e = new();
        e.Aliases.Add(new Alias("kk", Enabled: true, Expansion: "cast killgun on {1}"));
        Assert.True(e.TryExpand("kk goblin", out IReadOnlyList<string> steps));
        Assert.Equal("cast killgun on goblin", steps[0]);
    }

    [Fact]
    public void Substitute_MultiplePositionals_BindIndependently()
    {
        AliasEngine e = new();
        e.Aliases.Add(new Alias("c", Enabled: true, Expansion: "cast {1} on {2}"));
        Assert.True(e.TryExpand("c heal Bob", out IReadOnlyList<string> steps));
        Assert.Equal("cast heal on Bob", steps[0]);
    }

    [Fact]
    public void Substitute_MissingPositional_SubstitutesEmptyAndTrims()
    {
        AliasEngine e = new();
        e.Aliases.Add(new Alias("kk", Enabled: true, Expansion: "cast killgun on {1}"));
        Assert.True(e.TryExpand("kk", out IReadOnlyList<string> steps));
        // Trailing whitespace from the empty {1} is trimmed by the splitter.
        Assert.Equal("cast killgun on", steps[0]);
    }

    [Fact]
    public void Substitute_ExtraTokens_Ignored_WhenExpansionHasFewerPlaceholders()
    {
        AliasEngine e = new();
        e.Aliases.Add(new Alias("kk", Enabled: true, Expansion: "cast killgun on {1}"));
        // Extra tokens past {1} are dropped — alias doesn't reference {2} / {0}.
        Assert.True(e.TryExpand("kk goblin extra junk", out IReadOnlyList<string> steps));
        Assert.Equal("cast killgun on goblin", steps[0]);
    }

    [Fact]
    public void Substitute_BraceZero_IsNotTheSameAsBraceOne()
    {
        AliasEngine e = new();
        e.Aliases.Add(new Alias("a", Enabled: true, Expansion: "first={1} all={0}"));
        Assert.True(e.TryExpand("a one two three", out IReadOnlyList<string> steps));
        Assert.Equal("first=one all=one two three", steps[0]);
    }

    // ----- Multi-step ^M / ; splitting -----------------------------------

    [Fact]
    public void TryExpand_MultiStep_SplitsOnCaretM()
    {
        AliasEngine e = new();
        e.Aliases.Add(new Alias("prep", Enabled: true, Expansion: "wield sword^Mready shield"));
        Assert.True(e.TryExpand("prep", out IReadOnlyList<string> steps));
        Assert.Equal(2, steps.Count);
        Assert.Equal("wield sword",  steps[0]);
        Assert.Equal("ready shield", steps[1]);
    }

    [Fact]
    public void TryExpand_MultiStep_SplitsOnSemicolon()
    {
        AliasEngine e = new();
        e.Aliases.Add(new Alias("prep", Enabled: true, Expansion: "wield sword;ready shield"));
        Assert.True(e.TryExpand("prep", out IReadOnlyList<string> steps));
        Assert.Equal(2, steps.Count);
    }

    // ----- Isolation from trigger variables ------------------------------

    [Fact]
    public void Substitute_DoesNotReadFromTriggerVariableCache()
    {
        // Aliases use POSITIONAL substitution only. {name}-style references
        // (which work in triggers) are left as literal text — the alias
        // engine deliberately doesn't share the TriggerEngine variable
        // cache.
        AliasEngine e = new();
        e.Aliases.Add(new Alias("a", Enabled: true, Expansion: "hello {usr}"));
        Assert.True(e.TryExpand("a", out IReadOnlyList<string> steps));
        Assert.Equal("hello {usr}", steps[0]);
    }

    // ----- Whitespace handling -------------------------------------------

    [Fact]
    public void TryExpand_TolerantOfExtraWhitespaceBetweenAliasAndArgs()
    {
        AliasEngine e = new();
        e.Aliases.Add(new Alias("kk", Enabled: true, Expansion: "cast killgun on {1}"));
        Assert.True(e.TryExpand("kk    goblin", out IReadOnlyList<string> steps));
        Assert.Equal("cast killgun on goblin", steps[0]);
    }

    // ----- NameConflictReason: chat-channel guard ------------------------

    [Theory]
    [InlineData(".foo", "say")]
    [InlineData(".",    "say")]
    [InlineData("\"shout", "yell")]
    [InlineData("/Bob",  "telepath")]
    public void NameConflictReason_RejectsSingleCharPrefixes(string name, string channelSubstring)
    {
        string? reason = AliasEngine.NameConflictReason(name);
        Assert.NotNull(reason);
        Assert.Contains(channelSubstring, reason);
    }

    [Theory]
    [InlineData("gos")]
    [InlineData("goss")]
    [InlineData("gossi")]
    [InlineData("gossip")]
    [InlineData("GOSSIP")]  // case-insensitive
    public void NameConflictReason_RejectsGossipForms(string name)
    {
        Assert.NotNull(AliasEngine.NameConflictReason(name));
    }

    [Theory]
    [InlineData("auc")]
    [InlineData("auct")]
    [InlineData("aucti")]
    [InlineData("auctio")]
    [InlineData("auction")]
    public void NameConflictReason_RejectsAuctionForms(string name)
    {
        Assert.NotNull(AliasEngine.NameConflictReason(name));
    }

    [Theory]
    [InlineData("br")]
    [InlineData("bro")]
    [InlineData("broa")]
    [InlineData("broad")]
    [InlineData("broadc")]
    [InlineData("broadca")]
    [InlineData("broadcas")]
    [InlineData("broadcast")]
    public void NameConflictReason_RejectsBroadcastForms(string name)
    {
        Assert.NotNull(AliasEngine.NameConflictReason(name));
    }

    [Theory]
    [InlineData("bg")]
    [InlineData("gb")]
    [InlineData("broadg")]
    [InlineData("broadga")]
    [InlineData("broadgan")]
    [InlineData("broadgang")]
    public void NameConflictReason_RejectsGangpathForms(string name)
    {
        Assert.NotNull(AliasEngine.NameConflictReason(name));
    }

    [Theory]
    [InlineData("gh")]
    [InlineData("kk")]
    [InlineData("attack")]
    [InlineData("g")]   // single chars below the 2/3-char minimums for channels
    [InlineData("go")]
    [InlineData("ga")]
    [InlineData("au")]
    [InlineData("b")]
    public void NameConflictReason_AllowsCommonAliasNames(string name)
    {
        Assert.Null(AliasEngine.NameConflictReason(name));
    }

    [Fact]
    public void NameConflictReason_EmptyName_ReturnsNull()
    {
        // Empty-string validation is a separate concern (the dialog checks
        // non-empty independently); the chat-channel guard returns null
        // so it doesn't double-flag the same row.
        Assert.Null(AliasEngine.NameConflictReason(string.Empty));
    }

    // ----- IsDuplicate ---------------------------------------------------

    [Fact]
    public void IsDuplicate_FlagsAnotherAliasWithSameName_CaseInsensitive()
    {
        AliasEngine e = new();
        e.Aliases.Add(new Alias("gh", Enabled: true, Expansion: "get all here"));
        Assert.True(e.IsDuplicate("GH"));
        Assert.True(e.IsDuplicate("gh"));
        Assert.False(e.IsDuplicate("kk"));
    }

    [Fact]
    public void IsDuplicate_ExcludesSelf_WhenEditing()
    {
        AliasEngine e = new();
        Alias self = new("gh", Enabled: true, Expansion: "get all here");
        e.Aliases.Add(self);
        // Editing 'self' without changing the name should not flag a self-duplicate.
        Assert.False(e.IsDuplicate("gh", excluding: self));
    }
}
