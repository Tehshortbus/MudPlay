using FujinTerm.ViewModels;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 9.0a sub-G — <see cref="UnknownEntityFixDialogViewModel"/>'s
/// three-button outcome paths. The dialog itself collects intent; the
/// PR 9.0b classifier commits the result. Pinning the contract here
/// makes the eventual classifier wiring deterministic.
/// </summary>
public sealed class UnknownEntityFixDialogViewModelTests
{
    [Fact]
    public void Ctor_PreservesAlsoHereLineAndUnknownName()
    {
        UnknownEntityFixDialogViewModel vm = new(
            "Also here: foozle, a giant rat, Bob",
            "foozle");

        Assert.Equal("Also here: foozle, a giant rat, Bob", vm.RawAlsoHereLine);
        Assert.Equal("foozle", vm.UnknownEntity);
    }

    [Fact]
    public void Ctor_NullArgsBecomeEmpty()
    {
        // Reaching the dialog with no Context is unexpected but the VM
        // shouldn't NRE — falling back to "" keeps the dialog renderable
        // (the classifier will log a Warn upstream if this happens).
        UnknownEntityFixDialogViewModel vm = new(null!, null!);
        Assert.Equal(string.Empty, vm.RawAlsoHereLine);
        Assert.Equal(string.Empty, vm.UnknownEntity);
    }

    [Fact]
    public void AddAsMonster_FiresCloseRequested_WithEntityNameAndMonsterAction()
    {
        // The Cave-Bear path — user sees an unknown name that's a
        // legitimately new species, not a flavor variant of anything
        // and not a player. The new third button maps to AddAsMonster
        // so the App-level handler can drop a placeholder
        // MonsterMessageRecord into the catalogue.
        UnknownEntityFixDialogViewModel vm = new("Also here: cave bear", "cave bear");
        UnknownEntityFixResult? observed = null;
        vm.CloseRequested += r => observed = r;

        vm.AddAsMonsterCommand.Execute(null);

        Assert.NotNull(observed);
        Assert.Equal(UnknownEntityFixAction.AddAsMonster, observed!.Action);
        Assert.Equal("cave bear", observed.EntityName);
    }

    [Fact]
    public void AddFlavorPrefix_FiresCloseRequested_WithEntityNameAndFlavorAction()
    {
        UnknownEntityFixDialogViewModel vm = new("Also here: foozle", "foozle");
        UnknownEntityFixResult? observed = null;
        vm.CloseRequested += r => observed = r;

        vm.AddFlavorPrefixCommand.Execute(null);

        Assert.NotNull(observed);
        Assert.Equal(UnknownEntityFixAction.AddFlavorPrefix, observed!.Action);
        Assert.Equal("foozle", observed.EntityName);
    }

    [Fact]
    public void AddPlayerObservation_FiresCloseRequested_WithEntityNameAndPlayerAction()
    {
        UnknownEntityFixDialogViewModel vm = new("Also here: Bob", "Bob");
        UnknownEntityFixResult? observed = null;
        vm.CloseRequested += r => observed = r;

        vm.AddPlayerObservationCommand.Execute(null);

        Assert.NotNull(observed);
        Assert.Equal(UnknownEntityFixAction.AddPlayerObservation, observed!.Action);
        Assert.Equal("Bob", observed.EntityName);
    }

    [Fact]
    public void Cancel_FiresCloseRequestedWithNullResult()
    {
        UnknownEntityFixDialogViewModel vm = new("Also here: foozle", "foozle");
        bool fired = false;
        UnknownEntityFixResult? observed = new(UnknownEntityFixAction.AddFlavorPrefix, "sentinel");
        vm.CloseRequested += r =>
        {
            fired = true;
            observed = r;
        };

        vm.CancelCommand.Execute(null);

        Assert.True(fired);
        Assert.Null(observed);
    }
}
