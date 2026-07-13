using System;
using System.Collections.Generic;
using System.Linq;
using FujinTerm.Game.Spells;
using FujinTerm.ViewModels.Settings;
using Xunit;

namespace FujinTerm.Tests;

// The Bless-slot typeahead offers the class's learnable spells PLUS its
// cast-on-use items (as "#item name" tokens the CastingDirector fires). These
// pin the pure composition logic: unlimited-use only, gated by the item's
// use-level, ordered, and labelled — without the AppServices-bound spellbook.
public sealed class BlessSuggestionTests
{
    private static readonly IReadOnlyList<SpellPick> NoSpells = Array.Empty<SpellPick>();

    private static ClassCastItem Item(
        string name, int spellNumber, string spellName, int manaCost, int useCount, int minLevel)
        => new(ItemNumber: 0, ItemName: name, SpellNumber: spellNumber, SpellName: spellName,
               ManaCost: manaCost, UseCount: useCount, MinLevel: minLevel);

    [Fact]
    public void ComposeBlessSuggestions_AppendsUnlimitedItems_AfterSpells_AsTokens()
    {
        SpellPick[] spells = { new("accu", "accuracy"), new("bles", "bless") };
        ClassCastItem[] items = { Item("Shimmering Longsword", 50, "accuracy", 8, 0, 12) };

        IReadOnlyList<SpellPick> picks =
            SpellsSectionViewModel.ComposeBlessSuggestions(spells, items, level: 20);

        // Spells kept in place, first; the item is appended and commits its token.
        Assert.Equal("accu", picks[0].Short);
        Assert.Equal("bles", picks[1].Short);
        Assert.Equal("#Shimmering Longsword", picks[^1].Short);
        Assert.Equal("casts accuracy · Lv 12 · 8 mana", picks[^1].Name);
    }

    [Fact]
    public void ComposeBlessSuggestions_ExcludesLimitedChargeItems()
    {
        ClassCastItem[] items =
        {
            Item("Wand of Stars", 60, "starlight", 0, 0, 5),  // unlimited
            Item("Scroll of Arc", 61, "arc", 0, 3, 5),         // 3 charges — excluded
        };

        IReadOnlyList<SpellPick> picks =
            SpellsSectionViewModel.ComposeBlessSuggestions(NoSpells, items, level: 20);

        Assert.Single(picks);
        Assert.Equal("#Wand of Stars", picks[0].Short);
    }

    [Fact]
    public void ComposeBlessSuggestions_HidesItemsAboveOurUseLevel()
    {
        ClassCastItem[] items =
        {
            Item("Low Wand", 1, "a", 0, 0, 5),
            Item("High Wand", 2, "b", 0, 0, 40),
        };

        IReadOnlyList<SpellPick> picks =
            SpellsSectionViewModel.ComposeBlessSuggestions(NoSpells, items, level: 10);

        Assert.Single(picks);
        Assert.Equal("#Low Wand", picks[0].Short);
    }

    [Fact]
    public void ComposeBlessSuggestions_UnknownLevel_ShowsAllUsableItems()
    {
        ClassCastItem[] items = { Item("High Wand", 2, "b", 0, 0, 40) };

        IReadOnlyList<SpellPick> picks =
            SpellsSectionViewModel.ComposeBlessSuggestions(NoSpells, items, level: 0);

        Assert.Single(picks); // level unknown → not gated out
    }

    [Fact]
    public void ComposeBlessSuggestions_OrdersItemsByLevelThenName()
    {
        ClassCastItem[] items =
        {
            Item("Zeta", 1, "z", 0, 0, 20),
            Item("Alpha", 2, "a", 0, 0, 5),
            Item("Mid", 3, "m", 0, 0, 12),
        };

        IReadOnlyList<SpellPick> picks =
            SpellsSectionViewModel.ComposeBlessSuggestions(NoSpells, items, level: 30);

        Assert.Equal(new[] { "#Alpha", "#Mid", "#Zeta" }, picks.Select(p => p.Short));
    }

    [Fact]
    public void BlessItemLabel_FreeItem_ShowsFree_OmitsLevelWhenUngated()
    {
        Assert.Equal("casts bless · free",
            SpellsSectionViewModel.BlessItemLabel(Item("Crozier", 50, "bless", 0, 0, 0)));
    }

    [Fact]
    public void BlessItemLabel_UnresolvedSpell_FallsBackToNumber()
    {
        Assert.Equal("casts spell #999 · Lv 8 · free",
            SpellsSectionViewModel.BlessItemLabel(Item("Mystery Rod", 999, "", 0, 0, 8)));
    }
}
