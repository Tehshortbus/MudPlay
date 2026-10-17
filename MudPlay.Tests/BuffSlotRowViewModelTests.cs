using System.Collections.Generic;
using MudPlay.Models.Profile;
using MudPlay.ViewModels;
using Xunit;

namespace MudPlay.Tests;

// Buff Watchdog config row targeting: the All/None master must be INDEPENDENT of
// the Self box (the reported bug — unchecking All cleared Self), gated to a party
// (solo shows only Self), and it drives the auto-adapt AllMembers flag that decides
// whether a joining member is blessed.
public sealed class BuffSlotRowViewModelTests
{
    private static BuffSlotRowViewModel Row(BuffSlot dto, BuffSlotScope scope = BuffSlotScope.SingleTarget)
        => new(dto, _ => scope, s => s ?? string.Empty, _ => (null, null), () => { }, _ => { }, _ => true);

    private static IReadOnlyList<(string Display, string Given)> Party(params string[] given)
    {
        var list = new List<(string, string)>();
        foreach (string g in given) list.Add((g, g.ToLowerInvariant()));
        return list;
    }

    private static BuffMemberToggle Toggle(BuffSlotRowViewModel row, string given)
    {
        foreach (BuffMemberToggle t in row.MemberTargets)
            if (t.Given == given) return t;
        throw new Xunit.Sdk.XunitException($"no member toggle for '{given}'");
    }

    // The row label splits into a NAME (abbreviated to the in-game shorthand, and it
    // trims with an ellipsis in the UI) and the recast (+ condition tag) pinned to its
    // right, so a long name like "protection from evil" can't push the "- 15s" off the
    // row. HeaderText keeps the FULL name for the tooltip.
    [Fact]
    public void BuffName_AbbreviatesToInGameShorthand_TooltipKeepsFullName()
    {
        var row = Row(new BuffSlot { Spell = "protection from evil", RecastMarginSec = 15, OnlyWhenHpFull = true });

        Assert.Equal("prot evil", row.BuffNameText);            // "protection from evil" → the spoken form
        Assert.StartsWith(" - 15s", row.RecastText);
        Assert.Contains("HP full", row.RecastText);             // the tag stays with the recast, not the name
        Assert.DoesNotContain("HP full", row.BuffNameText);
        Assert.StartsWith("protection from evil - 15s", row.HeaderText);   // tooltip = full name + recast
    }

    [Fact]
    public void BuffName_NoAbbreviationRule_LeftAsIs()
    {
        var row = Row(new BuffSlot { Spell = "holy armour", RecastMarginSec = 10 });
        Assert.Equal("holy armour", row.BuffNameText);
    }

    [Fact]
    public void UncheckingAll_LeavesSelfChecked()
    {
        var dto = new BuffSlot { Spell = "bless", CastOnSelf = true, AllMembers = true };
        var row = Row(dto);
        row.RebuildMemberTargets(Party("aragorn", "gimli"));
        Assert.True(row.AllTargets);
        Assert.True(row.CastOnSelf);

        row.AllTargets = false;   // the bug: this used to clear Self too

        Assert.False(row.AllTargets);
        Assert.True(row.CastOnSelf);     // Self untouched
        Assert.True(dto.CastOnSelf);
        Assert.False(dto.AllMembers);
        Assert.Empty(dto.Targets);
    }

    [Fact]
    public void CheckingAll_SetsAutoAdapt_SoAJoinerIsAssigned()
    {
        var dto = new BuffSlot { Spell = "bless" };
        var row = Row(dto);
        row.RebuildMemberTargets(Party("aragorn"));
        row.AllTargets = true;
        Assert.True(dto.AllMembers);     // casting layer blesses every current member
        Assert.Empty(dto.Targets);

        row.RebuildMemberTargets(Party("aragorn", "legolas"));   // legolas joins
        Assert.True(Toggle(row, "aragorn").IsChecked);
        Assert.True(Toggle(row, "legolas").IsChecked);           // auto-assigned
    }

    [Fact]
    public void AllOff_JoinerIsNotAssigned()
    {
        var dto = new BuffSlot { Spell = "bless" };
        dto.Targets.Add("aragorn");
        var row = Row(dto);
        row.RebuildMemberTargets(Party("aragorn", "legolas"));   // legolas joins, All off
        Assert.True(Toggle(row, "aragorn").IsChecked);           // explicit target
        Assert.False(Toggle(row, "legolas").IsChecked);          // NOT auto-assigned
        Assert.False(row.AllTargets);
    }

    [Fact]
    public void ShowMemberTargets_HiddenSolo_ShownInParty()
    {
        var row = Row(new BuffSlot { Spell = "bless" });
        row.RebuildMemberTargets(Party());               // solo
        Assert.False(row.HasPartyMembers);
        Assert.False(row.ShowMemberTargets);
        Assert.True(row.ShowSelf);                       // Self still shows solo

        row.RebuildMemberTargets(Party("aragorn"));      // party
        Assert.True(row.HasPartyMembers);
        Assert.True(row.ShowMemberTargets);
    }

    [Fact]
    public void ShowMemberTargets_NeverForSelfOnlyBuff()
    {
        var row = Row(new BuffSlot { Spell = "shield" }, BuffSlotScope.SelfOnly);
        row.RebuildMemberTargets(Party("aragorn"));
        Assert.False(row.ShowMemberTargets);
        Assert.True(row.ShowSelf);
    }

    [Fact]
    public void UncheckingWholePartyMaster_ClearsSoloOptionAndPersistsOnce()
    {
        int persists = 0;
        var dto = new BuffSlot { Spell = "chan", WholePartyOn = true, CastSolo = true };
        var row = new BuffSlotRowViewModel(
            dto, _ => BuffSlotScope.WholeParty, s => s ?? string.Empty,
            _ => (null, null), () => persists++, _ => { }, _ => true);

        Assert.True(row.CanCastSolo);
        row.WholePartyOn = false;

        Assert.False(row.CanCastSolo);
        Assert.False(row.CastSolo);
        Assert.False(dto.WholePartyOn);
        Assert.False(dto.CastSolo);
        Assert.Equal(1, persists);
    }

    [Fact]
    public void OverwriteWarning_NoConflict_HiddenAndNullTooltip()
    {
        var row = Row(new BuffSlot { Spell = "bless" });
        Assert.False(row.HasOverwriteWarning);
        Assert.Null(row.OverwriteWarningTooltip);
    }

    [Fact]
    public void OverwriteWarning_BothDirections_CombinedIntoOneTooltip()
    {
        var dto = new BuffSlot { Spell = "bless" };
        var row = new BuffSlotRowViewModel(
            dto, _ => BuffSlotScope.SingleTarget, s => s ?? string.Empty,
            _ => ("chant", "poison"), () => { }, _ => { }, _ => true);

        Assert.True(row.HasOverwriteWarning);
        Assert.Equal("Removed by: chant\nRemoves: poison", row.OverwriteWarningTooltip);
    }

    // The live cross-row mutual-exclusion hook (BuffPanelViewModel.OnSelfCastActivated)
    // only reacts to a fresh Self CHECK, never an uncheck — otherwise unchecking a row
    // to let its conflict win would immediately re-fire and fight the user's own click.
    [Fact]
    public void CastOnSelf_CheckedOn_FiresOnSelfActivated()
    {
        int fired = 0;
        var dto = new BuffSlot { Spell = "grze" };
        var row = new BuffSlotRowViewModel(
            dto, _ => BuffSlotScope.SelfOnly, s => s ?? string.Empty,
            _ => (null, null), () => { }, _ => fired++, _ => true);

        row.CastOnSelf = true;
        Assert.Equal(1, fired);
    }

    [Fact]
    public void CastOnSelf_CheckedOff_DoesNotFireOnSelfActivated()
    {
        int fired = 0;
        var dto = new BuffSlot { Spell = "grze", CastOnSelf = true };
        var row = new BuffSlotRowViewModel(
            dto, _ => BuffSlotScope.SelfOnly, s => s ?? string.Empty,
            _ => (null, null), () => { }, _ => fired++, _ => true);

        row.CastOnSelf = false;
        Assert.Equal(0, fired);
    }

    [Fact]
    public void IsLearned_ReflectsResolver()
    {
        var dto = new BuffSlot { Spell = "grze" };
        var row = new BuffSlotRowViewModel(
            dto, _ => BuffSlotScope.SelfOnly, s => s ?? string.Empty,
            _ => (null, null), () => { }, onSelfActivated: _ => { },
            resolveLearned: code => code != "grze");

        Assert.False(row.IsLearned);
    }

    [Fact]
    public void HeaderText_IsNameAndRecast_NoLevelTag()
    {
        // The configured-row header is name + recast + the buff's own cast
        // conditions — no "(Lvl N)" tag (that lives in the Add-buff dropdown, where
        // it helps you pick; on a slotted row it only read as confusing).
        var row = Row(new BuffSlot { Spell = "bless", RecastMarginSec = 15 });
        Assert.Equal("bless - 15s · mana ≥ 50", row.HeaderText);
    }

    [Fact]
    public void HeaderText_NamesTheBuffsOwnRestAndCombatSwitches()
    {
        var row = Row(new BuffSlot
        {
            Spell = "bless", RecastMarginSec = 15,
            BlessIfAboveMa = 30, BlessWhileResting = true, BlessDuringCombat = true,
        });
        Assert.Equal("bless - 15s · mana ≥ 30 · while resting · in combat", row.HeaderText);
        Assert.Equal(" - 15s", row.RecastText);   // the narrow row itself stays short
    }

    [Theory]
    [InlineData("#emerald-tipped crozier", true)]
    [InlineData("bless", false)]
    [InlineData(null, false)]
    public void IsItemCast_ReflectsSpellToken(string? spell, bool expected)
    {
        var row = Row(new BuffSlot { Spell = spell });
        Assert.Equal(expected, row.IsItemCast);
    }

    // A member who negated the buff shows red on their box, and unticking then ticking
    // it is the way back (user, 2026-10-17).
    [Fact]
    public void NegatedMember_ShowsMarked_UntickThenTickLiftsTheMark()
    {
        var dto = new BuffSlot { Spell = "bless" };
        dto.Targets.Add("aragorn");
        dto.NegatedTargets.Add("aragorn");
        var row = Row(dto);
        row.RebuildMemberTargets(Party("aragorn", "gimli"));

        BuffMemberToggle aragorn = Toggle(row, "aragorn");
        Assert.True(aragorn.IsNegated);
        Assert.Contains("negated bless", aragorn.ToolTipText);
        Assert.Contains("Untick and tick", aragorn.ToolTipText);
        Assert.False(Toggle(row, "gimli").IsNegated);
        Assert.Equal("gimli", Toggle(row, "gimli").ToolTipText);

        aragorn.IsChecked = false;
        Assert.False(aragorn.IsNegated);
        Assert.Empty(dto.NegatedTargets);

        aragorn.IsChecked = true;
        Assert.Contains("aragorn", dto.Targets);
        Assert.Empty(dto.NegatedTargets);
    }

    [Fact]
    public void NegatedMember_UntickedOutOfAllMembers_LiftsOnlyTheirMark()
    {
        var dto = new BuffSlot { Spell = "bless", AllMembers = true };
        dto.NegatedTargets.AddRange(new[] { "aragorn", "gimli" });
        var row = Row(dto);
        row.RebuildMemberTargets(Party("aragorn", "gimli"));

        Toggle(row, "aragorn").IsChecked = false;

        Assert.Equal(new[] { "gimli" }, dto.NegatedTargets);
        Assert.True(Toggle(row, "gimli").IsNegated);
    }

    [Fact]
    public void NegatedMark_AddedByTheEngine_ShowsWithoutARebuild()
    {
        var dto = new BuffSlot { Spell = "bless", AllMembers = true };
        var row = Row(dto);
        row.RebuildMemberTargets(Party("aragorn"));
        BuffMemberToggle aragorn = Toggle(row, "aragorn");
        Assert.False(aragorn.IsNegated);

        dto.NegatedTargets.Add("aragorn");
        row.RefreshNegatedMarks();

        Assert.Same(aragorn, Toggle(row, "aragorn"));
        Assert.True(aragorn.IsNegated);
    }
}
