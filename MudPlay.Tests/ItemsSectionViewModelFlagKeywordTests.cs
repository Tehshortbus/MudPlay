using MudPlay.Models.GameData;
using MudPlay.ViewModels.GameData.Tables;
using Xunit;

namespace MudPlay.Tests;

// The Items tab's auto-toggle filter words: typing one narrows the list to items with
// that flag set. Every user-settable flag has a word (with the in-game verb as a synonym
// where one fits), and each word resolves to its own flag — not an unrelated one.
public sealed class ItemsSectionViewModelFlagKeywordTests
{
    private static readonly System.Collections.Generic.IReadOnlyDictionary<string, System.Func<ItemOverlay, bool?>> Kw
        = ItemsSectionViewModel.FlagKeywords;

    [Fact]
    public void EveryFlag_HasAWord_ThatResolvesToIt()
    {
        Assert.True(Kw["get"](new ItemOverlay { AutoCollect = true }) == true);
        Assert.True(Kw["collect"](new ItemOverlay { AutoCollect = true }) == true);
        Assert.True(Kw["drop"](new ItemOverlay { AutoDiscard = true }) == true);
        Assert.True(Kw["discard"](new ItemOverlay { AutoDiscard = true }) == true);
        Assert.True(Kw["open"](new ItemOverlay { AutoOpen = true }) == true);
        Assert.True(Kw["buy"](new ItemOverlay { AutoBuy = true }) == true);
        Assert.True(Kw["sell"](new ItemOverlay { AutoSell = true }) == true);
        Assert.True(Kw["stash"](new ItemOverlay { AutoStash = true }) == true);
        Assert.True(Kw["keep"](new ItemOverlay { MustHaveMinimum = true }) == true);
        Assert.True(Kw["loyal"](new ItemOverlay { LoyalItem = true }) == true);
        Assert.True(Kw["notake"](new ItemOverlay { CannotBeTaken = true }) == true);
        Assert.True(Kw["one"](new ItemOverlay { CarryOnlyOne = true }) == true);
        Assert.True(Kw["only-one"](new ItemOverlay { CarryOnlyOne = true }) == true);
        Assert.True(Kw["path"](new ItemOverlay { AutoObtainForPath = true }) == true);
    }

    [Fact]
    public void Word_MatchesOnlyItsOwnFlag()
    {
        // "get" filters auto-collect, not some other flag that happens to be set.
        Assert.False(Kw["get"](new ItemOverlay { AutoDiscard = true }) == true);
        Assert.False(Kw["stash"](new ItemOverlay { AutoBuy = true }) == true);
    }

    [Fact]
    public void UnknownWord_IsNotAFlagKeyword()
    {
        // A non-keyword falls through to the normal name / column substring match.
        Assert.False(Kw.ContainsKey("weapon"));
        Assert.False(Kw.ContainsKey("plate"));
    }
}
