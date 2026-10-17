using System;
using System.Collections.Generic;
using System.IO;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Runs the template index against the REAL shipped catalogue, which is the only way
// to check the two failure modes that matter. Under-matching put known casts into the
// unrecognized-line review queue; over-matching would be worse, silently swallowing
// the uncatalogued room-spell messages the queue exists to surface. A synthetic
// one-record catalogue can't expose either, so these load the stock seed.
public sealed class MessageTemplateIndexTests : IDisposable
{
    private readonly string _dir;

    public MessageTemplateIndexTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mudplay-templates-" + Path.GetRandomFileName());
        AppPaths.ExtractEmbeddedSeeds(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp cleanup */ }
    }

    // Both realms ship their own catalogue and they diverge, so every guard here runs
    // against each — a template thin enough to over-match in one may only exist there.
    private const string Stock = "Messages.stock.seed.json";
    private const string Paradigm = "Messages.paradigm.seed.json";

    private MessageTemplateIndex Index(string seed) => new(
        JsonStore.Load<List<MessageRecord>>(Path.Combine(_dir, seed))
        ?? new List<MessageRecord>());

    [Theory]
    [InlineData(Stock, "Raijin casts minor healing on Raijin!")]
    [InlineData(Stock, "Raijin casts bless on Suijin!")]
    [InlineData(Stock, "Raijin casts a spell on you!")]
    [InlineData(Stock, "You invoke the way of the swan.")]
    [InlineData(Paradigm, "Raijin casts minor healing on Raijin!")]
    [InlineData(Paradigm, "Raijin casts a spell on you!")]
    public void KnownCastLines_AreRecognized(string seed, string line) =>
        Assert.True(Index(seed).Matches(line), $"{seed} should describe: {line}");

    [Theory]
    // Room-spell triggers with no catalogue entry yet — the capture must keep
    // surfacing these, so no template may claim them.
    [InlineData("An ominous wind blows through the trees")]
    [InlineData("A flock of birds fly overhead.")]
    [InlineData("The forest becomes strangely silent.")]
    [InlineData("The leaves begin to rustle, as if some beast were about to spring forth!")]
    // Monster death flavour is never in the catalogue (it's positional).
    [InlineData("The dog yelps loudly, and dies.")]
    [InlineData("The dark goblin archer collapses with a spiteful hiss.")]
    // Client-side surfaces that must not be mistaken for catalogue messages.
    [InlineData("  Raijin WuzHere                 (Priest)     [M:100%] [H: 85%]   - Backrank")]
    [InlineData("The room is dimly lit")]
    public void UncataloguedLines_AreNotClaimedByAnyTemplate(string line)
    {
        foreach (string seed in new[] { Stock, Paradigm })
            Assert.False(Index(seed).Matches(line),
                $"{seed}: no template should claim this line, but one did: {line}");
    }

    [Fact]
    public void BlankAndEmptyInput_DoNotMatch()
    {
        MessageTemplateIndex index = Index(Stock);
        Assert.False(index.Matches(null));
        Assert.False(index.Matches(string.Empty));
        Assert.False(index.Matches("   "));
    }

    [Fact]
    public void PlaceholderOnlyTemplate_IsNotIndexed()
    {
        // It would match nearly any line, so it can never identify one. The stock
        // catalogue contains such a slot, which is why this is dropped rather than
        // treated as always-matching.
        MessageTemplateIndex index = new(new[]
        {
            new MessageRecord(
                Id: "x", Name: "Degenerate", Flags: MessageFlags.None, RawFlagsHex: 0,
                CasterMessage: "{source} {target}", TargetMessage: string.Empty,
                WitnessMessage: string.Empty, AppliedMessage: string.Empty,
                AppliedEndsWith: string.Empty),
        });

        Assert.False(index.Matches("Absolutely anything at all."));
    }

    [Fact]
    public void LongestLiteralWord_PicksTheMostSelectiveWord()
    {
        // The index files each matcher under this word, so it has to come from the
        // template's literal text and be the longest run of letters there.
        Assert.Equal("casts",
            CasterMessageMatcher.LongestLiteralWord("{source} casts {spellname} on {target}!"));
        Assert.Equal("invoke",
            CasterMessageMatcher.LongestLiteralWord("You invoke the {spellname}."));
        // No literal text at all → nothing to file it under.
        Assert.Null(CasterMessageMatcher.LongestLiteralWord("{source} {target}"));
        Assert.Null(CasterMessageMatcher.LongestLiteralWord("{s}"));
    }
}
