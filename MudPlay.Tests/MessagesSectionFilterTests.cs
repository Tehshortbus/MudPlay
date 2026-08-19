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
