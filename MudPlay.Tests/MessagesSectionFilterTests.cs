using System.Collections.Generic;
using MudPlay.Models.GameData;
using MudPlay.ViewModels.GameData.Tables;
using Xunit;

namespace MudPlay.Tests;

// The Messages tab hides a record claimed by a spell PRESENT in the set (it's edited
// from the Spells section), but keeps orphan-linked and standalone records visible so
// they aren't stranded with no reachable editor.
public sealed class MessagesSectionFilterTests
{
    private static MessageRecord WithLinks(string name, params GameDataLink[] links) =>
        new(Id: name, Name: name, Flags: MessageFlags.None,
            RawFlagsHex: 0, CasterMessage: "", TargetMessage: "",
            WitnessMessage: "", AppliedMessage: "", AppliedEndsWith: "",
            Links: links.Length == 0 ? null : links);

    private static MessageRecord WithLines(
        MessageFlags flags, string caster, string target, string witness,
        string applied, string wearsOff, string fumble = "") =>
        new(Id: "r", Name: "r", Flags: flags, RawFlagsHex: 0,
            CasterMessage: caster, TargetMessage: target, WitnessMessage: witness,
            AppliedMessage: applied, AppliedEndsWith: wearsOff,
            Links: new[] { new GameDataLink("Spells", 1) }, ConfuseFumbleLine: fumble);

    // ----- MissingSlots (the Incomplete worklist inclusion + Missing column) -----

    // ----- SpellNumbers (the leading "Spell #" column) -----

    [Fact]
    public void SpellNumbers_SingleSpellLink()
    {
        Assert.Equal("107",
            MessagesSectionViewModel.SpellNumbers(WithLinks("bless", new GameDataLink("Spells", 107))));
    }

    [Fact]
    public void SpellNumbers_MultipleSpellLinks_CommaJoined()
    {
        Assert.Equal("150, 223",
            MessagesSectionViewModel.SpellNumbers(WithLinks("disease",
                new GameDataLink("Spells", 150), new GameDataLink("Spells", 223))));
    }

    [Fact]
    public void SpellNumbers_NoSpellLink_Blank()
    {
        Assert.Equal("", MessagesSectionViewModel.SpellNumbers(WithLinks("plain")));
        Assert.Equal("", MessagesSectionViewModel.SpellNumbers(
            WithLinks("itemproc", new GameDataLink("Items", 438))));
    }

    [Fact]
    public void MissingSlots_AllBlank_ListsEveryRequiredSlot()
    {
        MessageRecord m = WithLines(MessageFlags.None, "", "", "", "", "");
        Assert.Equal(
            new[] { "Caster", "Target", "Witness", "Applied", "Wears-off" },
            MessagesSectionViewModel.MissingSlots(m));
    }

    [Fact]
    public void MissingSlots_Sentinels_CountAsFilled()
    {
        // {null}/{void}/{empty} in a slot marks it deliberately absent → filled, so it
        // drops off the missing list exactly as real text would.
        MessageRecord m = WithLines(MessageFlags.None,
            caster: "You cast {s}!", target: "{null}", witness: "{void}",
            applied: "{empty}", wearsOff: "The effect fades.");
        Assert.Empty(MessagesSectionViewModel.MissingSlots(m));
    }

    [Fact]
    public void MissingSlots_Confused_RequiresFumbleLine()
    {
        MessageRecord blank = WithLines(MessageFlags.Confused,
            "c", "t", "w", "a", "e", fumble: "");
        Assert.Equal(new[] { "Fumble" }, MessagesSectionViewModel.MissingSlots(blank));

        MessageRecord filled = WithLines(MessageFlags.Confused,
            "c", "t", "w", "a", "e", fumble: "You fumble in confusion!");
        Assert.Empty(MessagesSectionViewModel.MissingSlots(filled));
    }

    [Fact]
    public void MissingSlots_NonConfused_DoesNotRequireFumble()
    {
        MessageRecord m = WithLines(MessageFlags.None, "c", "t", "w", "a", "e", fumble: "");
        Assert.Empty(MessagesSectionViewModel.MissingSlots(m));
    }

    [Fact]
    public void ClaimedByExistingSpell_IsHidden()
    {
        HashSet<int> spells = new() { 107 };
        MessageRecord bless = WithLinks("bless", new GameDataLink("Spells", 107));
        Assert.True(MessagesSectionViewModel.IsClaimedByExistingSpell(bless, spells));
    }

    [Fact]
    public void OrphanSpellLink_StaysVisible()
    {
        // Links a spell that isn't in this set ⇒ unreachable from the Spells section, so
        // it must remain listed here.
        HashSet<int> spells = new() { 107 };
        MessageRecord orphan = WithLinks("gone", new GameDataLink("Spells", 999));
        Assert.False(MessagesSectionViewModel.IsClaimedByExistingSpell(orphan, spells));
    }

    [Fact]
    public void StandaloneOrNonSpellLinked_StaysVisible()
    {
        HashSet<int> spells = new() { 107 };
        Assert.False(MessagesSectionViewModel.IsClaimedByExistingSpell(WithLinks("plain"), spells));
        Assert.False(MessagesSectionViewModel.IsClaimedByExistingSpell(
            WithLinks("itemproc", new GameDataLink("Items", 107)), spells));
    }

    [Fact]
    public void ClaimedByExistingItem_IsHidden()
    {
        // An on-use buff / weapon-proc record anchored to an item present in the set is
        // edited from the item dialog's Message section, so the Messages tab hides it.
        HashSet<int> items = new() { 438 };
        MessageRecord belt = WithLinks("belt of might", new GameDataLink("Items", 438));
        Assert.True(MessagesSectionViewModel.IsClaimedByExistingItem(belt, items));
    }

    [Fact]
    public void OrphanItemLink_StaysVisible()
    {
        HashSet<int> items = new() { 438 };
        MessageRecord orphan = WithLinks("gone", new GameDataLink("Items", 9999));
        Assert.False(MessagesSectionViewModel.IsClaimedByExistingItem(orphan, items));
    }

    [Fact]
    public void StandaloneOrNonItemLinked_StaysVisible()
    {
        HashSet<int> items = new() { 438 };
        Assert.False(MessagesSectionViewModel.IsClaimedByExistingItem(WithLinks("plain"), items));
        Assert.False(MessagesSectionViewModel.IsClaimedByExistingItem(
            WithLinks("spellonly", new GameDataLink("Spells", 438)), items));
    }
}
