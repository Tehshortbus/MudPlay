using System.Collections.Generic;
using MudPlay.ViewModels;
using Xunit;

namespace MudPlay.Tests;

// The Add/Edit buff dialog's mana-regen reroll threshold: both realms edit the rolled
// value in a number box spanning the roll spell's level-scaled range (negatives
// included); Stock adds the tick steps, since its roll is read back off the tick.
public sealed class AddBuffDialogViewModelTests
{
    private static readonly BuffPickOption[] Picks = { new("flux", "mana flux (Lvl 16)", true) };

    private static AddBuffDialogViewModel Dialog(bool stock, string? steps = null,
        (int Min, int Max)? roll = null) =>
        new(Picks, isLightSpell: _ => false, isRollSpell: s => s == "flux",
            isStockRealm: stock, tickSteps: _ => steps,
            initial: new AddBuffResult("flux", 15, false, false, false, false, 3, null),
            rollRange: _ => roll);

    [Fact]
    public void ANewBuff_StartsAtHalfMana_AndHoldsInRestAndCombat()
    {
        AddBuffDialogViewModel d = new(Picks, _ => false, _ => false);

        Assert.Equal(50, d.BlessIfAboveMa);
        Assert.False(d.BlessWhileResting);
        Assert.False(d.BlessDuringCombat);
        Assert.Equal("Cast if mana ≥ %", d.BlessIfAboveLabel);
        Assert.Equal(100, d.BlessIfAboveMaximum);
    }

    [Fact]
    public void EditingABuff_ShowsItsConditions_AndOkHandsThemBack()
    {
        AddBuffDialogViewModel d = new(Picks, _ => false, _ => false,
            initial: new AddBuffResult("flux", 15, false, false, false, false, 0, null,
                BlessIfAboveMa: 35, BlessWhileResting: true, BlessDuringCombat: false));
        Assert.Equal(35, d.BlessIfAboveMa);
        Assert.True(d.BlessWhileResting);

        d.BlessIfAboveMa = 150;          // past a percent's ceiling
        d.BlessDuringCombat = true;
        AddBuffResult? result = null;
        d.CloseRequested += r => result = r;
        d.OkCommand.Execute(null);

        Assert.NotNull(result);
        Assert.Equal(100, result.BlessIfAboveMa);
        Assert.True(result.BlessWhileResting);
        Assert.True(result.BlessDuringCombat);
    }

    [Fact]
    public void TheManaFloor_IsShownAgainstTheCharactersMaxMana()
    {
        AddBuffDialogViewModel percent = new(Picks, _ => false, _ => false, maxMana: 250);
        Assert.Equal("125/250", percent.BlessIfAboveConverted);

        List<string?> raised = new();
        percent.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        percent.BlessIfAboveMa = 10;
        Assert.Equal("25/250", percent.BlessIfAboveConverted);
        Assert.Contains(nameof(AddBuffDialogViewModel.BlessIfAboveConverted), raised);

        AddBuffDialogViewModel amount = new(Picks, _ => false, _ => false,
            initial: new AddBuffResult("flux", 15, false, false, false, false, 0, null, BlessIfAboveMa: 100),
            manaFloorIsAbsolute: true, maxMana: 250);
        Assert.Equal("40% of 250", amount.BlessIfAboveConverted);

        AddBuffDialogViewModel unknown = new(Picks, _ => false, _ => false);
        Assert.Equal(string.Empty, unknown.BlessIfAboveConverted);
    }

    [Fact]
    public void AbsoluteManaThresholds_TakeTheFloorAsAnAmount()
    {
        AddBuffDialogViewModel d = new(Picks, _ => false, _ => false,
            initial: new AddBuffResult("flux", 15, false, false, false, false, 0, null, BlessIfAboveMa: 250),
            manaFloorIsAbsolute: true);

        Assert.Equal("Cast if mana ≥", d.BlessIfAboveLabel);
        AddBuffResult? result = null;
        d.CloseRequested += r => result = r;
        d.OkCommand.Execute(null);

        Assert.Equal(250, result!.BlessIfAboveMa);
    }

    [Fact]
    public void Stock_UsesTheRollBox_AndShowsTheTickSteps()
    {
        AddBuffDialogViewModel d = Dialog(stock: true, steps: "6 MP/tick at worst · 7 from 12", roll: (-64, 216));

        Assert.True(d.ShowRerollNumeric);
        Assert.Equal(-64m, d.RerollNumericMinimum);
        Assert.Equal("6 MP/tick at worst · 7 from 12", d.RerollTickStepsText);
        d.RerollThreshold = 75;
        AddBuffResult? result = null;
        d.CloseRequested += r => result = r;
        d.OkCommand.Execute(null);
        Assert.Equal(75, result!.RerollThreshold);
    }

    [Fact]
    public void Paradigm_NumberBox_SpansTheLevelScaledRoll_IncludingNegatives()
    {
        AddBuffDialogViewModel d = Dialog(stock: false, roll: (-64, 216));

        Assert.Equal(-64m, d.RerollNumericMinimum);
        Assert.Equal(216m, d.RerollNumericMaximum);
        Assert.Equal("rolls -64 … 216 at your level", d.RerollRollRangeText);
    }

    [Fact]
    public void Paradigm_UnknownRange_AllowsNegatives()
    {
        AddBuffDialogViewModel d = Dialog(stock: false, roll: null);

        Assert.Equal(-999m, d.RerollNumericMinimum);
        Assert.Equal(999m, d.RerollNumericMaximum);
    }

    // A draw item (a deck of cards): one tick box per buff it can deal, each saying
    // what the buff does; the unticked ones are what the slot stores.
    private static readonly MudPlay.Game.Spells.CastOutcome[] Cards =
    {
        new(965, "card-wizard", 12, "Int +5", 240),
        new(966, "card-priest", 12, "Wis +5", 240),
    };

    private static AddBuffDialogViewModel DeckDialog(AddBuffResult? initial = null)
    {
        AddBuffDialogViewModel d = new(
            new[] { new BuffPickOption("#deck of cards", "deck of cards (Lvl 15)", true) },
            isLightSpell: _ => false, isRollSpell: _ => false, initial: initial,
            outcomesOf: code => code == "#deck of cards" ? Cards : System.Array.Empty<MudPlay.Game.Spells.CastOutcome>());
        if (initial is null) d.SelectedPick = d.PickOptions[0];
        return d;
    }

    [Fact]
    public void DrawItem_ListsEachOutcome_Ticked_WithWhatItApplies()
    {
        AddBuffDialogViewModel d = DeckDialog();

        Assert.True(d.HasOutcomes);
        Assert.Equal(new[] { "card-wizard (12%)", "card-priest (12%)" }, d.Outcomes.Select(o => o.Label));
        Assert.All(d.Outcomes, o => Assert.True(o.IsChecked));
        Assert.StartsWith("Wis +5", d.Outcomes[1].Tip);
        Assert.Contains("Lasts about", d.Outcomes[1].Tip);
    }

    [Fact]
    public void DrawItem_ReturnsTheUntickedOutcomes_AndNeedsOneTicked()
    {
        AddBuffDialogViewModel d = DeckDialog();
        AddBuffResult? result = null;
        d.CloseRequested += r => result = r;

        d.Outcomes[0].IsChecked = false;
        Assert.True(d.CanAdd);
        d.OkCommand.Execute(null);
        Assert.Equal(new[] { 965 }, result!.RejectedOutcomes);

        d.Outcomes[1].IsChecked = false;
        Assert.True(d.NoOutcomeTicked);
        Assert.False(d.CanAdd);
    }

    [Fact]
    public void DrawItem_EditingShowsTheSavedChoices()
    {
        AddBuffDialogViewModel d = DeckDialog(new AddBuffResult(
            "#deck of cards", 15, false, false, false, false, 0, null, RejectedOutcomes: new[] { 966 }));

        Assert.True(d.Outcomes[0].IsChecked);
        Assert.False(d.Outcomes[1].IsChecked);
    }

    // Stock's deck can't be used again while a card is up: nothing to pick, and a
    // recast that leads the wear-off would only be refused (user, 2026-10-02).
    [Fact]
    public void ADeckThatCantRedraw_HasNoTickBoxes_AndItsRecastStopsAtZero()
    {
        AddBuffResult saved = new("#deck of cards", 15, false, false, false, false, 0, null, false);
        AddBuffDialogViewModel d = new(
            new[] { new BuffPickOption("#deck of cards", "deck of cards (Lvl 35)", true) },
            isLightSpell: _ => false, isRollSpell: _ => false, initial: saved,
            outcomesOf: _ => System.Array.Empty<MudPlay.Game.Spells.CastOutcome>(),
            isNoRedrawDraw: code => code == "#deck of cards");

        Assert.True(d.IsNoRedrawDraw);
        Assert.False(d.HasOutcomes);
        Assert.Equal(0, d.RecastMarginMaximum);
        Assert.Equal(0, d.RecastMarginSec);
        Assert.True(d.CanAdd);
    }

    [Fact]
    public void AnOrdinaryBuff_HasNoOutcomes()
    {
        AddBuffDialogViewModel d = Dialog(stock: false);

        Assert.False(d.HasOutcomes);
        Assert.False(d.NoOutcomeTicked);
    }
}
