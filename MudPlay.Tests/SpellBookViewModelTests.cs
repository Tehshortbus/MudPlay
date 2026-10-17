using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MudPlay.Game.Spells;
using MudPlay.Services;
using MudPlay.ViewModels;
using Xunit;

namespace MudPlay.Tests;

// Pins SpellBookViewModel — the projection of
// SpellbookState into rendered, filtered rows. Reuses the
// synthetic Mage / Warrior class + spell rows from the other spellbook tests.
public sealed class SpellBookViewModelTests : IDisposable
{
    private readonly string _root;

    public SpellBookViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-spellbookvm-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private static readonly object[] _classes =
    [
        ClassRow(1, "Warrior", magery: 0, mageryLvl: 0),
        ClassRow(12, "Mage", magery: 1, mageryLvl: 3),
    ];

    private static readonly object[] _spells =
    [
        SpellRow(100, "starlight", "star", magery: 1, mageryLvl: 1, reqLevel: 2),
        SpellRow(101, "high arc", "high", magery: 1, mageryLvl: 3, reqLevel: 5, manaCost: 8),
        SpellRow(103, "gated", "lvlg", magery: 1, mageryLvl: 1, reqLevel: 20),
    ];

    private SpellbookState NewBook(int classNumber, int level, object[]? items = null,
                                   object[]? spells = null, object[]? tbInfo = null)
    {
        string dir = Path.Combine(_root, "set");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), JsonSerializer.Serialize(spells ?? _spells));
        File.WriteAllText(Path.Combine(dir, "Classes.json"), JsonSerializer.Serialize(_classes));
        if (items is not null)
            File.WriteAllText(Path.Combine(dir, "Items.json"), JsonSerializer.Serialize(items));
        if (tbInfo is not null)
            File.WriteAllText(Path.Combine(dir, "TBInfo.json"), JsonSerializer.Serialize(tbInfo));

        GameDataCache cache = new(_root);
        cache.SwitchSet("set");
        KnownSpellCatalog catalog = new(cache);
        SpellbookState book = new(catalog);
        book.Refresh(classNumber, level);
        return book;
    }

    // ----- alignment boxes -------------------------------------------------

    private static readonly object[] _alignedSpells =
    [
        AffectSpellRow(300, "smite", "smit", magery: 1, mageryLvl: 1, reqLevel: 1, abilCode: 97, abilVal: 0),   // good only
        AffectSpellRow(301, "blight", "blit", magery: 1, mageryLvl: 1, reqLevel: 1, abilCode: 98, abilVal: 0),  // evil only
        AffectSpellRow(302, "ward", "ward", magery: 1, mageryLvl: 1, reqLevel: 1, abilCode: 111, abilVal: 0),   // not evil
        SpellRow(303, "plain bolt", "bolt", magery: 1, mageryLvl: 1, reqLevel: 1),                              // ungated
    ];

    // The book lists the whole class whatever alignment the client last read: that
    // reading can lag, and hiding rows on it made a learned spell look missing
    // (report paradigm-20261003-215442).
    [Fact]
    public void Rows_ListEveryClassSpell_WhateverOurAlignmentReading()
    {
        SpellbookState book = NewBook(classNumber: 12, level: 5, spells: _alignedSpells);
        book.Refresh(classNumber: 12, level: 5, charAlign: 1); // read as Good
        using SpellBookViewModel vm = new(book);

        Assert.Equal(new[] { "blight", "plain bolt", "smite", "ward" }, vm.Rows.Select(r => r.Name));
        Assert.StartsWith("0 of 4 learned", vm.StatusText);
    }

    [Fact]
    public void AlignmentBoxes_ShowSpellsAnyTickedAlignmentCanUse()
    {
        SpellbookState book = NewBook(classNumber: 12, level: 5, spells: _alignedSpells);
        using SpellBookViewModel vm = new(book) { ShowGood = false, ShowNeutral = false };

        // Evil only: its own spell and the ungated one; not the good-only or not-evil ones.
        Assert.Equal(new[] { "blight", "plain bolt" }, vm.Rows.Select(r => r.Name));

        vm.ShowNeutral = true;   // neutral can use the not-evil spell too
        Assert.Equal(new[] { "blight", "plain bolt", "ward" }, vm.Rows.Select(r => r.Name));

        vm.ShowEvil = false;
        vm.ShowNeutral = false;  // nothing ticked: nothing to show
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public void Rows_MirrorAvailableList_SortedByLevel()
    {
        SpellbookState book = NewBook(classNumber: 12, level: 5);
        using SpellBookViewModel vm = new(book) { ShowAllSpells = true };

        Assert.Equal(3, vm.Rows.Count); // Show-all = the full class list
        Assert.Equal(new[] { "starlight", "high arc", "gated" }, vm.Rows.Select(r => r.Name));
    }

    [Fact]
    public void LevelGate_HidesAboveLevelSpells_UnlessShowAll()
    {
        SpellbookState book = NewBook(classNumber: 12, level: 5);
        using SpellBookViewModel vm = new(book);   // default: level-gated

        // "gated" (ReqLevel 20) is hidden at level 5; req-2 + req-5 spells show.
        Assert.Equal(2, vm.Rows.Count);
        Assert.DoesNotContain(vm.Rows, r => r.Name == "gated");

        vm.ShowAllSpells = true;
        Assert.Equal(3, vm.Rows.Count);
        Assert.Contains(vm.Rows, r => r.Name == "gated");
    }

    [Fact]
    public void ObtainedGlyph_TracksBookState()
    {
        SpellbookState book = NewBook(classNumber: 12, level: 5);
        book.SetObtainedByNames(new[] { "starlight" });
        using SpellBookViewModel vm = new(book);

        SpellBookRowViewModel star = vm.Rows.Single(r => r.Name == "starlight");
        SpellBookRowViewModel high = vm.Rows.Single(r => r.Name == "high arc");
        Assert.True(star.IsObtained);
        Assert.False(high.IsObtained);
    }

    [Fact]
    public void BookChanged_RebuildsRows()
    {
        SpellbookState book = NewBook(classNumber: 12, level: 5);
        using SpellBookViewModel vm = new(book);
        Assert.False(vm.Rows.Single(r => r.Name == "high arc").IsObtained);

        book.MarkObtainedByName("high arc"); // fires Changed

        Assert.True(vm.Rows.Single(r => r.Name == "high arc").IsObtained);
    }

    [Fact]
    public void Search_FiltersByNameAndShort()
    {
        SpellbookState book = NewBook(classNumber: 12, level: 5);
        using SpellBookViewModel vm = new(book) { ShowAllSpells = true };

        vm.SearchText = "high";   // matches name "high arc"
        Assert.Single(vm.Rows);
        Assert.Equal("high arc", vm.Rows[0].Name);

        vm.SearchText = "star";   // matches short "star" / name "starlight"
        Assert.Single(vm.Rows);
        Assert.Equal("starlight", vm.Rows[0].Name);

        vm.SearchText = "";       // cleared → all back
        Assert.Equal(3, vm.Rows.Count);
    }

    [Fact]
    public void ShowObtainedOnly_HidesUnlearned()
    {
        SpellbookState book = NewBook(classNumber: 12, level: 5);
        book.SetObtainedByNames(new[] { "starlight" });
        using SpellBookViewModel vm = new(book);

        vm.ShowObtainedOnly = true;
        Assert.Single(vm.Rows);
        Assert.Equal("starlight", vm.Rows[0].Name);
    }

    [Fact]
    public void Header_UsesClassNameProviderAndLevel()
    {
        SpellbookState book = NewBook(classNumber: 12, level: 7);
        using SpellBookViewModel vm = new(book, () => "Mage");

        Assert.Equal("Mage — Level 7", vm.HeaderText);
    }

    [Fact]
    public void Status_ShowsObtainedOfTotal()
    {
        SpellbookState book = NewBook(classNumber: 12, level: 5);
        book.SetObtainedByNames(new[] { "starlight" });
        using SpellBookViewModel vm = new(book) { ShowAllSpells = true };

        Assert.Equal("1 of 3 learned", vm.StatusText);
    }

    [Fact]
    public void NonMageryClass_HasEmptyBook()
    {
        SpellbookState book = NewBook(classNumber: 1, level: 5); // Warrior
        using SpellBookViewModel vm = new(book, () => "Warrior");

        Assert.Empty(vm.Rows);
        Assert.Equal("Spell Book — no spells for this class", vm.HeaderText);
        Assert.Equal("This class has no spell book.", vm.StatusText);
    }

    // ----- cast-on-use item section --------------------------------------

    private static readonly object[] _items =
    [
        // Mage-only (ClassRest 12), casts starlight (100), unlimited — MajorMUD's
        // -1 sentinel (the real-world unlimited value, not 0).
        ItemRow(200, "Wand of Stars", castSpell: 100, useCount: -1, 12),
        // Mage-only (ClassRest 12), casts high arc (101), 3 uses.
        ItemRow(201, "Scroll of Arc", castSpell: 101, useCount: 3, 12),
        // Warrior-only (ClassRest 1), casts starlight — excluded for the Mage.
        ItemRow(202, "Warrior Wand", castSpell: 100, useCount: 5, 1),
        // Mage-only but no CastsSp ability — excluded (not a spell source).
        PlainItemRow(203, "Plain Dagger", 12),
        // Mage-only, casts starlight, single charge — exercises singular "use".
        ItemRow(204, "Single Charge Rod", castSpell: 100, useCount: 1, 12),
        // Unrestricted (no ClassRest) — usable by every class, so the Spell Book
        // lists it for none: the display shows only class-specific sources.
        ItemRow(205, "Universal Charm", castSpell: 101, useCount: 0),
    ];

    [Fact]
    public void CastItems_SurfaceClassUsableCastOnUseItems()
    {
        SpellbookState book = NewBook(classNumber: 12, level: 5, items: _items); // Mage
        using SpellBookViewModel vm = new(book);

        // Mage sees its class-specific sources 200 / 201 / 204; the warrior-only
        // 202, non-casting 203, and universal 205 are all excluded. Sorted by
        // item name.
        Assert.True(vm.HasCastItems);
        Assert.Equal(new[] { "Scroll of Arc", "Single Charge Rod", "Wand of Stars" },
            vm.CastItems.Select(r => r.ItemName));

        SpellBookItemRowViewModel wand = vm.CastItems.Single(r => r.ItemName == "Wand of Stars");
        Assert.Equal("casts starlight", wand.CastsText);
        // The -1 sentinel renders as the word "Unlimited", not a raw "-1 uses".
        Assert.Equal("Unlimited", wand.ChargesText);

        Assert.Equal("3 uses", vm.CastItems.Single(r => r.ItemName == "Scroll of Arc").ChargesText);
        Assert.Equal("1 use", vm.CastItems.Single(r => r.ItemName == "Single Charge Rod").ChargesText);

        // Mana cost comes from the cast spell: starlight (100) is free, while
        // high arc (101 → Scroll of Arc) costs 8 mana.
        Assert.Equal("free", wand.ManaText);
        SpellBookItemRowViewModel scroll = vm.CastItems.Single(r => r.ItemName == "Scroll of Arc");
        Assert.Equal("8 mana", scroll.ManaText);
    }

    [Fact]
    public void CastItems_ShowOnlyClassSpecificItems_ForNonMageryClass()
    {
        // A Warrior (non-magery, empty spell book) surfaces only its
        // class-specific cast item — the warrior-only wand. Mage-restricted
        // rows and the universal charm anyone can use are both hidden.
        SpellbookState book = NewBook(classNumber: 1, level: 5, items: _items);
        using SpellBookViewModel vm = new(book, () => "Warrior");

        Assert.Empty(vm.Rows); // no spells for this class
        Assert.Equal(new[] { "Warrior Wand" }, vm.CastItems.Select(r => r.ItemName));
    }

    [Fact]
    public void CastItems_FilteredBySearchText()
    {
        SpellbookState book = NewBook(classNumber: 12, level: 5, items: _items);
        using SpellBookViewModel vm = new(book) { ShowAllSpells = true };

        vm.SearchText = "scroll"; // matches the item name only
        Assert.Single(vm.CastItems);
        Assert.Equal("Scroll of Arc", vm.CastItems[0].ItemName);

        vm.SearchText = "starlight"; // matches the cast-spell name on 200 + 204
        Assert.Equal(new[] { "Single Charge Rod", "Wand of Stars" },
            vm.CastItems.Select(r => r.ItemName));
    }

    [Fact]
    public void CastItems_SortedByLevelRequirement_LowestFirst()
    {
        // Names are deliberately reverse-alphabetical to the level order so a
        // by-name sort would produce the wrong sequence — the display must
        // order by the item's MinLevel gate, lowest first, with a 0-level
        // (ungated) item ahead of every gated one.
        object[] items =
        [
            CastItemWithLevel(210, "Zeta Wand", minLevel: 20, classRest: 12),
            CastItemWithLevel(211, "Alpha Rod", minLevel: 5, classRest: 12),
            CastItemWithLevel(212, "Mid Charm", minLevel: 12, classRest: 12),
            CastItemWithLevel(213, "Ungated Wand", minLevel: 0, classRest: 12),
        ];
        SpellbookState book = NewBook(classNumber: 12, level: 5, items: items); // Mage
        using SpellBookViewModel vm = new(book);

        Assert.Equal(new[] { "Ungated Wand", "Alpha Rod", "Mid Charm", "Zeta Wand" },
            vm.CastItems.Select(r => r.ItemName));

        Assert.Equal("Lv —", vm.CastItems[0].LevelText);
        Assert.Equal("Lv 5", vm.CastItems[1].LevelText);
        Assert.Equal(20, vm.CastItems[3].MinLevel);
    }

    [Fact]
    public void CastItems_ExcludeUniversalItems_ButAutomationKeepsThem()
    {
        SpellbookState book = NewBook(classNumber: 12, level: 5, items: _items); // Mage
        using SpellBookViewModel vm = new(book);

        // The universal charm is usable by the Mage, so the casting automation
        // still sees it in the shared list — it's only the display that narrows
        // to class-specific sources.
        Assert.Contains("Universal Charm", book.GetCastItems().Select(i => i.ItemName));
        Assert.DoesNotContain("Universal Charm", vm.CastItems.Select(r => r.ItemName));
    }

    // Double-click-to-teaching-item lookup: the item carrying a LearnSp (42) ability
    // for the spell wins; a CastsSp (43) item that only casts it is NOT a teacher.
    [Fact]
    public void GetTeachingItemNumber_FindsLearnSpItem_ZeroWhenNoneTeaches()
    {
        object[] items =
        [
            TeachItemRow(300, "Tome of Starlight", teachesSpell: 100), // LearnSp → starlight (100)
            ItemRow(301, "Wand of Arc", castSpell: 101, useCount: -1), // only CASTS arc, doesn't teach
        ];
        SpellbookState book = NewBook(classNumber: 12, level: 5, items: items); // Mage

        Assert.Equal(300, book.GetTeachingItemNumber(100)); // taught by the tome
        Assert.Equal(0, book.GetTeachingItemNumber(101));   // cast-on-use only → no teacher
        Assert.Equal(0, book.GetTeachingItemNumber(999));   // unknown spell → none
    }

    // Two items teach the spell: an out-of-play tome first in the table, then the
    // shop scroll. The spell's own "Learned From" item wins, and without one an item in
    // play still beats the out-of-play one (lesser arcane enchantment: tome #3656 vs
    // scroll #3678).
    [Fact]
    public void GetTeachingItemNumber_PrefersLearnedFrom_ThenInPlay()
    {
        Dictionary<string, object> tome = TeachItemRow(300, "tome of starlight", teachesSpell: 100);
        tome["In Game"] = 0;
        Dictionary<string, object> scroll = TeachItemRow(301, "scroll of starlight", teachesSpell: 100);
        scroll["In Game"] = 1;
        object[] items = [tome, scroll];

        SpellbookState plain = NewBook(classNumber: 12, level: 5, items: items);
        Assert.Equal(301, plain.GetTeachingItemNumber(100));   // in play beats out of play

        Dictionary<string, object> starlight = SpellRow(100, "starlight", "star", magery: 1, mageryLvl: 1, reqLevel: 2);
        starlight["Learned From"] = "Item #301";
        Dictionary<string, object> tome2 = TeachItemRow(300, "tome of starlight", teachesSpell: 100);
        SpellbookState named = NewBook(classNumber: 12, level: 5,
            items: [tome2, TeachItemRow(301, "scroll of starlight", teachesSpell: 100)],
            spells: [starlight, _spells[1], _spells[2]]);
        Assert.Equal(301, named.GetTeachingItemNumber(100));   // the spell's own Learned From
    }

    // A trainer-taught spell (no teaching item) resolves the NPC that teaches it from
    // its "Learned From" annotation, so the Spell Book's double-click can open the
    // NPC's record instead of an item's.
    [Fact]
    public void GetTeachingNpcNumber_FromLearnedFrom_ZeroWhenNotNpcTaught()
    {
        object[] spells =
        [
            new Dictionary<string, object>
            {
                ["Number"] = 100, ["Name"] = "divine disfavour", ["Short"] = "disf",
                ["Magery"] = 2, ["MageryLVL"] = 1, ["ReqLevel"] = 19,
                ["Learned From"] = "NPC #696",
            },
            new Dictionary<string, object>
            {
                ["Number"] = 101, ["Name"] = "starlight", ["Short"] = "star",
                ["Magery"] = 1, ["MageryLVL"] = 1, ["ReqLevel"] = 2,
                ["Learned From"] = "Item #396",
            },
        ];
        SpellbookState book = NewBook(classNumber: 12, level: 5, spells: spells);

        Assert.Equal(696, book.GetTeachingNpcNumber(100)); // trainer-taught → the NPC
        Assert.Equal(0, book.GetTeachingNpcNumber(101));   // item-taught → no NPC
        Assert.Equal(0, book.GetTeachingNpcNumber(999));   // unknown spell → none
    }

    // A spell learned from an NPC trainer is gated by a TBInfo `check class:class
    // N:minlevel M ... learnspell S` line at a level often HIGHER than the spell's
    // ReqLevel (a Paladin's divine disfavour: ReqLevel 19 but trainer-gated to 50).
    // The book must show that real unlock level and hide the spell below it.
    [Fact]
    public void TeachLevel_TrainerGate_RaisesUnlockLevel_AndFiltersBelowIt()
    {
        object[] tb =
        [
            new Dictionary<string, object>
            {
                ["Number"] = 1,
                // starlight (#100, ReqLevel 2) is trainer-gated to level 40 for Mage (class 12).
                ["Action"] = "check class:class 12:minlevel 40 999:price 5 1:learnspell 100:text 1\n",
            },
        ];
        SpellbookState book = NewBook(classNumber: 12, level: 12, tbInfo: tb); // Mage, level 12

        Assert.Equal(40, book.GetTeachLevels()[100]);

        using SpellBookViewModel vm = new(book); // Show all OFF by default
        // Hidden: effective unlock level 40 > character level 12 (ReqLevel 2 would have shown it).
        Assert.DoesNotContain(vm.Rows, r => r.Short == "star");

        vm.ShowAllSpells = true;
        SpellBookRowViewModel star = vm.Rows.Single(r => r.Short == "star");
        Assert.Equal(40, star.EffectiveLevel);   // max(ReqLevel 2, trainer 40)
        Assert.Equal("40", star.ReqLevelText);
    }

    [Fact]
    public void TeachLevel_NoTrainerGate_UsesSpellReqLevel()
    {
        SpellbookState book = NewBook(classNumber: 12, level: 50); // no TBInfo seeded
        using SpellBookViewModel vm = new(book) { ShowAllSpells = true };

        SpellBookRowViewModel star = vm.Rows.Single(r => r.Short == "star");
        Assert.Equal(2, star.EffectiveLevel);   // falls back to ReqLevel
    }

    [Fact]
    public void CastItems_EmptyWhenNoItemsTable()
    {
        SpellbookState book = NewBook(classNumber: 12, level: 5); // no Items.json seeded
        using SpellBookViewModel vm = new(book);

        Assert.False(vm.HasCastItems);
        Assert.Empty(vm.CastItems);
    }

    [Fact]
    public void CastItems_ExcludeNonEquippableUseItems()
    {
        // A cast-on-use item must be equippable in an equipment slot. An
        // equippable wand surfaces; a potion (Drink, worn nowhere) and a room
        // Sign (left in a room to "use", never carried) are use-but-not-equip
        // items and must be filtered out.
        object[] items =
        [
            ItemRow(300, "Healing Wand", castSpell: 100, useCount: 0, 12),
            NonEquippableCastItemRow(301, "Healing Potion", castSpell: 100, itemType: 5, 12), // Drink
            NonEquippableCastItemRow(302, "Dark Warchest", castSpell: 100, itemType: 3, 12),  // Sign
        ];
        SpellbookState book = NewBook(classNumber: 12, level: 5, items: items); // Mage
        using SpellBookViewModel vm = new(book);

        Assert.Equal(new[] { "Healing Wand" }, vm.CastItems.Select(r => r.ItemName));
    }

    // Report paradigm-20261002-114043: a Gypsy's deck of cards casts on use but has
    // no equip slot, so the equippable-only list left it out. A carried item kept
    // after use is listed; the cast engines' list stays readied-only.
    [Fact]
    public void CastItems_IncludeACarriedItemKeptAfterUse_ButTheEnginesDoNotSeeIt()
    {
        Dictionary<string, object> deck = NonEquippableCastItemRow(301, "Deck Of Cards", castSpell: 100, itemType: 10, 12);
        deck["Retain After Uses"] = 1;
        deck["UseCount"] = 9999;
        object[] items =
        [
            ItemRow(300, "Healing Wand", castSpell: 100, useCount: 0, 12),
            deck,
            NonEquippableCastItemRow(302, "Healing Potion", castSpell: 100, itemType: 5, 12), // consumed
        ];
        SpellbookState book = NewBook(classNumber: 12, level: 5, items: items);
        using SpellBookViewModel vm = new(book);

        Assert.Equal(new[] { "Deck Of Cards", "Healing Wand" }, vm.CastItems.Select(r => r.ItemName).OrderBy(n => n));
        Assert.Contains("carried, not worn", vm.CastItems.Single(r => r.ItemName == "Deck Of Cards").CastsText);
        Assert.Equal(new[] { "Healing Wand" }, book.GetCastItems().Select(i => i.ItemName));
    }

    // A draw item's use-spell has no effect of its own: its textblock runs a
    // `random <table>` step and the spells that table casts are what a use can deal.
    // The deck of cards' cards are read that way, with each one's chance and effect.
    [Fact]
    public void DrawItem_ListsTheSpellsItsRandomTableDeals()
    {
        Dictionary<string, object> draw = SpellRow(700, "card deck draw", "", magery: 0, mageryLvl: 0, reqLevel: 0);
        draw["Abil-0"] = 148;
        draw["AbilVal-0"] = 9000;
        Dictionary<string, object> wizard = SpellRow(701, "card-wizard", "", magery: 0, mageryLvl: 0, reqLevel: 0);
        wizard["Dur"] = 240;
        wizard["Casted By"] = "Textblock #9001(12%)";
        Dictionary<string, object> wheel = SpellRow(702, "card-wheel of fortune", "", magery: 0, mageryLvl: 0, reqLevel: 0);
        wheel["Dur"] = 240;
        wheel["Casted By"] = "Textblock #9001(16%)";
        object[] tbInfo =
        [
            new Dictionary<string, object> { ["Number"] = "9000", ["Action"] = "message 8815:cast 5145:random 9001\n" },
        ];
        Dictionary<string, object> deck = NonEquippableCastItemRow(301, "Deck Of Cards", castSpell: 700, itemType: 10, 12);
        deck["Retain After Uses"] = 1;
        deck["UseCount"] = 9999;

        SpellbookState book = NewBook(classNumber: 12, level: 20, items: [deck],
            spells: [draw, wizard, wheel], tbInfo: tbInfo);

        ClassCastItem item = Assert.Single(book.GetDrawCastItems());
        Assert.True(item.Carried);
        Assert.Equal(new[] { ("card-wizard", 12), ("card-wheel of fortune", 16) },
            item.Outcomes!.Select(o => (o.Name, o.ChancePercent)));
        Assert.All(item.Outcomes!, o => Assert.Equal(240, o.DurationRounds));
        // The Buff Watchdog and the cast engines can resolve its token.
        Assert.Contains(book.GetCastItems(), i => i.ItemName == "Deck Of Cards");
    }

    // Every card carries ability 15, and the deck's textblock ends the use on
    // `failability 15` while the user has it. Paradigm's chain casts a shuffle first
    // that removes the cards, so a redraw gets through; Stock's has no shuffle, so the
    // deck can't be used again until the card is gone (`wccmmud.dll` 1.11p: a
    // failability that finds the ability prints its message and stops the chain).
    [Theory]
    [InlineData("message 8815:message 2780:cast 705:failability 15 1114:random 9001\n", true)]
    [InlineData("message 2780:failability 15 1114:random 9001\n", false)]
    [InlineData("message 2780:random 9001\n", true)]
    public void DrawItem_CanRedraw_OnlyWhenItsOwnCardCantBlockTheUse(string action, bool canRedraw)
    {
        Dictionary<string, object> draw = SpellRow(700, "card deck draw", "", magery: 0, mageryLvl: 0, reqLevel: 0);
        draw["Abil-0"] = 148;
        draw["AbilVal-0"] = 9000;
        Dictionary<string, object> wizard = SpellRow(701, "card-wizard", "", magery: 0, mageryLvl: 0, reqLevel: 0);
        wizard["Dur"] = 240;
        wizard["Casted By"] = "Textblock #9001(100%)";
        wizard["Abil-0"] = 44;
        wizard["AbilVal-0"] = 5;
        wizard["Abil-1"] = 15;
        Dictionary<string, object> shuffle = SpellRow(705, "card shuffle", "", magery: 0, mageryLvl: 0, reqLevel: 0);
        shuffle["Abil-0"] = 122;
        shuffle["AbilVal-0"] = 701;
        object[] tbInfo = [new Dictionary<string, object> { ["Number"] = "9000", ["Action"] = action }];
        Dictionary<string, object> deck = NonEquippableCastItemRow(301, "Deck Of Cards", castSpell: 700, itemType: 10, 12);
        deck["Retain After Uses"] = 1;
        deck["UseCount"] = 9999;

        SpellbookState book = NewBook(classNumber: 12, level: 20, items: [deck],
            spells: [draw, wizard, shuffle], tbInfo: tbInfo);

        Assert.Equal(canRedraw, Assert.Single(book.GetDrawCastItems()).CanRedraw);
    }

    [Fact]
    public void CastItems_ExcludeAutomaticProcs_KeepOnlyCommandCasts()
    {
        // Three Mage-usable, equippable items each carry a CastsSp(43) → starlight,
        // but only one is a genuine command-cast. A %Spell(114) before the CastsSp
        // makes it a per-swing combat proc (extra damage while fighting); a
        // CastOnKill%(1114) before it makes it an on-kill proc. Both fire
        // automatically, so the Spell Book must list only the bare-CastsSp wand.
        object[] items =
        [
            ItemRow(400, "Plain Wand", castSpell: 100, useCount: 0, 12),
            ProcCastItemRow(401, "Proc Blade", modifierCode: 114, proc: 25, castSpell: 100, 12),
            ProcCastItemRow(402, "Kill Mask", modifierCode: 1114, proc: 50, castSpell: 100, 12),
        ];
        SpellbookState book = NewBook(classNumber: 12, level: 5, items: items); // Mage
        using SpellBookViewModel vm = new(book);

        Assert.Equal(new[] { "Plain Wand" }, vm.CastItems.Select(r => r.ItemName));
        // The automation-facing list drops them too — a proc never command-casts.
        Assert.DoesNotContain("Proc Blade", book.GetCastItems().Select(i => i.ItemName));
        Assert.DoesNotContain("Kill Mask", book.GetCastItems().Select(i => i.ItemName));
    }

    [Fact]
    public void CastItems_UnlimitedSentinel_TreatsZeroAndNegativeAsUnlimited()
    {
        // MajorMUD stores -1 for an unlimited item and (rarely) 0; both mean
        // unlimited. Only a
        // positive count is a real charge count.
        object[] items =
        [
            ItemRow(500, "Neg Wand", castSpell: 100, useCount: -1, 12),
            ItemRow(501, "Zero Wand", castSpell: 100, useCount: 0, 12),
            ItemRow(502, "Charged Wand", castSpell: 100, useCount: 7, 12),
        ];
        SpellbookState book = NewBook(classNumber: 12, level: 5, items: items); // Mage
        using SpellBookViewModel vm = new(book);

        SpellBookItemRowViewModel neg = vm.CastItems.Single(r => r.ItemName == "Neg Wand");
        SpellBookItemRowViewModel zero = vm.CastItems.Single(r => r.ItemName == "Zero Wand");
        SpellBookItemRowViewModel charged = vm.CastItems.Single(r => r.ItemName == "Charged Wand");

        Assert.Equal("Unlimited", neg.ChargesText);
        Assert.Equal("Unlimited", zero.ChargesText);
        Assert.Equal("7 uses", charged.ChargesText);

        // The underlying model flags both <= 0 items unlimited (buff-loop safe) and
        // the positive count limited — the display "Unlimited" mirrors that.
        IReadOnlyList<ClassCastItem> model = book.GetCastItems();
        Assert.True(model.Single(i => i.ItemName == "Neg Wand").Unlimited);
        Assert.True(model.Single(i => i.ItemName == "Zero Wand").Unlimited);
        Assert.False(model.Single(i => i.ItemName == "Charged Wand").Unlimited);
    }

    [Fact]
    public void CastItems_ShowCastSpellEffect_ParenthesisedBetweenNameAndMana()
    {
        // A cast item surfaces its spell's decoded effect inline, wrapped in
        // parentheses, so the reader sees what the item actually does. Spell 600
        // grants AC +10; a spell with no renderable figure collapses the label.
        object[] spells =
        [
            AffectSpellRow(600, "iron skin", "iron", magery: 1, mageryLvl: 1, reqLevel: 1,
                abilCode: 2, abilVal: 10), // AC +10
            SpellRow(100, "starlight", "star", magery: 1, mageryLvl: 1, reqLevel: 2),
        ];
        object[] items =
        [
            ItemRow(600, "Iron Ring", castSpell: 600, useCount: -1, 12),
        ];
        SpellbookState book = NewBook(classNumber: 12, level: 5, items: items, spells: spells); // Mage
        using SpellBookViewModel vm = new(book);

        SpellBookItemRowViewModel ring = vm.CastItems.Single(r => r.ItemName == "Iron Ring");
        Assert.True(ring.HasAffects);
        Assert.StartsWith("(", ring.AffectsText);
        Assert.EndsWith(")", ring.AffectsText);
        Assert.Contains("AC", ring.AffectsText);
        Assert.Contains("+10", ring.AffectsText);
    }

    // ----- synthetic-row builders (mirror SpellListParserTests) ----------

    // An equippable item whose CastsSp(43) rides on a preceding proc modifier —
    // %Spell (114, per-swing combat proc) or CastOnKill% (1114, on-kill proc).
    // Both fire automatically, so GetClassCastItems must NOT surface them as
    // command-cast spell sources. Worn 16 keeps it equippable so the proc filter
    // is the only reason it's excluded.
    private static Dictionary<string, object> ProcCastItemRow(
        int number, string name, int modifierCode, int proc, int castSpell, params int[] classRest)
    {
        Dictionary<string, object> row = new()
        {
            ["Number"] = number,
            ["Name"] = name,
            ["UseCount"] = 0,
            ["Worn"] = 16,
        };
        for (int i = 0; i < 10; i++)
            row[$"ClassRest-{i}"] = i < classRest.Length ? classRest[i] : 0;
        for (int i = 0; i < 20; i++)
        {
            row[$"Abil-{i}"] = 0;
            row[$"AbilVal-{i}"] = 0;
        }
        // The modifier immediately precedes the CastsSp it reframes (real data
        // always places them adjacent).
        row["Abil-0"] = modifierCode;
        row["AbilVal-0"] = proc;
        row["Abil-1"] = 43;
        row["AbilVal-1"] = castSpell;
        return row;
    }

    // A cast-on-use item the player can't equip — a non-zero ItemType worn
    // Nowhere (Worn 0). Used to prove the equippable filter drops potions /
    // food / Signs even though they carry a CastsSp ability.
    private static Dictionary<string, object> NonEquippableCastItemRow(
        int number, string name, int castSpell, int itemType, params int[] classRest)
    {
        Dictionary<string, object> row = new()
        {
            ["Number"] = number,
            ["Name"] = name,
            ["UseCount"] = 0,
            ["ItemType"] = itemType,
            ["Worn"] = 0, // Nowhere — not equippable
        };
        for (int i = 0; i < 10; i++)
            row[$"ClassRest-{i}"] = i < classRest.Length ? classRest[i] : 0;
        for (int i = 0; i < 20; i++)
        {
            row[$"Abil-{i}"] = i == 0 ? 43 : 0;
            row[$"AbilVal-{i}"] = i == 0 ? castSpell : 0;
        }
        return row;
    }

    private static Dictionary<string, object> ItemRow(
        int number, string name, int castSpell, int useCount, params int[] classRest)
    {
        Dictionary<string, object> row = new()
        {
            ["Number"] = number,
            ["Name"] = name,
            ["UseCount"] = useCount,
            ["Worn"] = 16, // equippable (generic "Worn" slot) — cast items must be wearable
        };
        for (int i = 0; i < 10; i++)
            row[$"ClassRest-{i}"] = i < classRest.Length ? classRest[i] : 0;
        // 20 ability slots; slot 0 = CastsSp (43) → the cast spell number.
        for (int i = 0; i < 20; i++)
        {
            row[$"Abil-{i}"] = i == 0 ? 43 : 0;
            row[$"AbilVal-{i}"] = i == 0 ? castSpell : 0;
        }
        return row;
    }

    // An item that TEACHES a spell — a LearnSp ability (code 42) in slot 0 whose
    // value is the taught Spells.Number. No class/equippable gating: the teaching
    // lookup scans every item for the ability.
    private static Dictionary<string, object> TeachItemRow(int number, string name, int teachesSpell)
    {
        Dictionary<string, object> row = new()
        {
            ["Number"] = number,
            ["Name"] = name,
        };
        for (int i = 0; i < 20; i++)
        {
            row[$"Abil-{i}"] = i == 0 ? 42 : 0;
            row[$"AbilVal-{i}"] = i == 0 ? teachesSpell : 0;
        }
        return row;
    }

    // A class-restricted cast item carrying a MinLevel gate (Items ability code
    // 135 in slot 1) on top of the CastsSp slot 0 that ItemRow already sets.
    private static Dictionary<string, object> CastItemWithLevel(
        int number, string name, int minLevel, int classRest)
    {
        Dictionary<string, object> row = ItemRow(number, name, castSpell: 100, useCount: 0, classRest);
        row["Abil-1"] = 135;
        row["AbilVal-1"] = minLevel;
        return row;
    }

    private static Dictionary<string, object> PlainItemRow(int number, string name, params int[] classRest)
    {
        Dictionary<string, object> row = new()
        {
            ["Number"] = number,
            ["Name"] = name,
            ["UseCount"] = 0,
        };
        for (int i = 0; i < 10; i++)
            row[$"ClassRest-{i}"] = i < classRest.Length ? classRest[i] : 0;
        for (int i = 0; i < 20; i++)
        {
            row[$"Abil-{i}"] = 0;
            row[$"AbilVal-{i}"] = 0;
        }
        return row;
    }


    private static Dictionary<string, object> ClassRow(int number, string name, int magery, int mageryLvl)
        => new()
        {
            ["Number"] = number,
            ["Name"] = name,
            ["MageryType"] = magery,
            ["MageryLVL"] = mageryLvl,
        };

    // A spell whose sole effect is a single stat affect (Abil code + value) with
    // no damage/heal base — so SpellEffectFormatter renders exactly that affect
    // ("AC +10"), letting a cast item's inline effect be asserted deterministically.
    private static Dictionary<string, object> AffectSpellRow(
        int number, string name, string shortCode, int magery, int mageryLvl, int reqLevel,
        int abilCode, int abilVal)
    {
        Dictionary<string, object> row = SpellRow(number, name, shortCode, magery, mageryLvl, reqLevel);
        row["MinBase"] = 0; // clear the Damage base SpellRow seeds so only the affect renders
        row["Abil-0"] = abilCode;
        row["AbilVal-0"] = abilVal;
        return row;
    }

    private static Dictionary<string, object> SpellRow(
        int number, string name, string shortCode, int magery, int mageryLvl, int reqLevel,
        int manaCost = 0)
    {
        Dictionary<string, object> row = new()
        {
            ["Number"] = number,
            ["Name"] = name,
            ["Short"] = shortCode,
            ["Magery"] = magery,
            ["MageryLVL"] = mageryLvl,
            ["ReqLevel"] = reqLevel,
            ["Learnable"] = 1,
            ["Learned From"] = "\0",
            ["Classes"] = "(*)",
            ["MinBase"] = 1,
            ["MaxBase"] = 0,
            ["MinInc"] = 0,
            ["MinIncLVLs"] = 0,
            ["MaxInc"] = 0,
            ["MaxIncLVLs"] = 0,
            ["Dur"] = 0,
            ["DurInc"] = 0,
            ["DurIncLVLs"] = 0,
            ["Cap"] = 0,
            ["EnergyCost"] = 0,
            ["ManaCost"] = manaCost,
        };
        for (int x = 0; x < 10; x++)
        {
            row[$"Abil-{x}"] = x == 0 ? 1 : 0;
            row[$"AbilVal-{x}"] = 0;
        }
        return row;
    }

    // ----- category tabs (SelectedCategory) --------------------------------

    // "fireball": EnergyCost 500 — Attacks only, matching god's wrath's real shape.
    private static Dictionary<string, object> AttackSpellRow(int number, string name, string shortCode)
    {
        Dictionary<string, object> row = SpellRow(number, name, shortCode, magery: 1, mageryLvl: 1, reqLevel: 1);
        row["EnergyCost"] = 500;
        row["MaxBase"] = 20;
        row["Abil-0"] = 17; // Damage(-MR)
        return row;
    }

    // "heal touch": Abil 18 (Heal) — Heals only, matching major healing's real shape.
    private static Dictionary<string, object> HealSpellRow(int number, string name, string shortCode)
    {
        Dictionary<string, object> row = SpellRow(number, name, shortCode, magery: 1, mageryLvl: 1, reqLevel: 1);
        row["MaxBase"] = 20;
        row["Abil-0"] = 18; // Heal
        return row;
    }

    // "party ward": Dur 40, Targets 13 (Full Party Area) — Buffs AND PartyOrAoe,
    // matching chant / mass frenzy's real shape.
    private static Dictionary<string, object> PartyBuffSpellRow(int number, string name, string shortCode)
    {
        Dictionary<string, object> row = SpellRow(number, name, shortCode, magery: 1, mageryLvl: 1, reqLevel: 1);
        row["MinBase"] = 0;
        row["Dur"] = 40;
        row["Targets"] = 13;
        row["Abil-0"] = 22; // Accuracy — an arbitrary non-damage/heal affect, like bless's own shape
        return row;
    }

    [Fact]
    public void SelectedCategory_FiltersRowsByClassification()
    {
        object[] spells =
        [
            AttackSpellRow(100, "fireball", "fire"),
            HealSpellRow(101, "heal touch", "heal"),
            PartyBuffSpellRow(102, "party ward", "ward"),
        ];
        SpellbookState book = NewBook(classNumber: 12, level: 5, spells: spells);
        using SpellBookViewModel vm = new(book) { ShowAllSpells = true };

        Assert.Equal(SpellBookCategory.All, vm.SelectedCategory); // default
        Assert.Equal(3, vm.Rows.Count);

        vm.SelectedCategory = SpellBookCategory.Attacks;
        Assert.Equal(new[] { "fireball" }, vm.Rows.Select(r => r.Name));

        vm.SelectedCategory = SpellBookCategory.Heals;
        Assert.Equal(new[] { "heal touch" }, vm.Rows.Select(r => r.Name));

        vm.SelectedCategory = SpellBookCategory.Buffs;
        Assert.Equal(new[] { "party ward" }, vm.Rows.Select(r => r.Name));

        // "party ward" satisfies both Buffs and PartyOrAoe — same spell, different tab.
        vm.SelectedCategory = SpellBookCategory.PartyOrAoe;
        Assert.Equal(new[] { "party ward" }, vm.Rows.Select(r => r.Name));

        vm.SelectedCategory = SpellBookCategory.All;
        Assert.Equal(3, vm.Rows.Count);
    }
}
