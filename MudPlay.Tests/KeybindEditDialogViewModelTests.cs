using Avalonia.Input;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels.Keybind;
using Xunit;

namespace MudPlay.Tests;

// The rebind dialog's decision logic: a chord already owned by another built-in
// action is a non-blocking STEAL warning (saving unbinds that owner), while a
// macro or system-reserved collision stays a hard error that blocks Save.
public sealed class KeybindEditDialogViewModelTests
{
    private static KeybindEditDialogViewModel Editing(
        BuiltInAction action, KeybindingStore? store = null, MacroStore? macros = null)
        => new(action, store ?? new KeybindingStore(), macros ?? new MacroStore());

    [Fact]
    public void BuiltInConflict_IsWarningNotError_AllowsSave_NamesVictim()
    {
        // Editing OpenSpellBook (default F2); pick F1, which OpenWorkshop owns.
        KeybindEditDialogViewModel vm = Editing(BuiltInAction.OpenSpellBook);
        vm.SelectedKey = Key.F1;

        Assert.False(vm.HasError);
        Assert.True(vm.HasWarning);
        Assert.False(vm.HasInfo);
        Assert.True(vm.CanSave);
        Assert.Equal(
            $"{KeybindingStore.ActionLabel(BuiltInAction.OpenWorkshop)} is now unbound.",
            vm.StatusMessage);
    }

    [Fact]
    public void OwnCurrentChord_IsNeitherWarningNorError()
    {
        // Editing OpenWorkshop (default F1); re-selecting its own chord isn't a steal.
        KeybindEditDialogViewModel vm = Editing(BuiltInAction.OpenWorkshop);
        vm.SelectedKey = Key.F1;

        Assert.False(vm.HasError);
        Assert.False(vm.HasWarning);
        Assert.True(vm.HasInfo);
        Assert.True(vm.CanSave);
    }

    [Fact]
    public void FreeChord_IsPlainInfo()
    {
        // F8 is unbound by default.
        KeybindEditDialogViewModel vm = Editing(BuiltInAction.OpenSpellBook);
        vm.SelectedKey = Key.F8;

        Assert.False(vm.HasError);
        Assert.False(vm.HasWarning);
        Assert.True(vm.HasInfo);
        Assert.True(vm.CanSave);
    }

    [Fact]
    public void MacroConflict_StaysBlockingError()
    {
        MacroStore macros = new();
        macros.Macros.Add(new Macro("NumPad8", false, false, false, "n", true));
        KeybindEditDialogViewModel vm = Editing(BuiltInAction.OpenSpellBook, macros: macros);
        vm.SelectedKey = Key.NumPad8;

        Assert.True(vm.HasError);
        Assert.False(vm.HasWarning);
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void SystemReservedChord_StaysBlockingError()
    {
        // Alt+F4 is reserved by the OS — never stealable.
        KeybindEditDialogViewModel vm = Editing(BuiltInAction.OpenSpellBook);
        vm.SelectedKey = Key.F4;
        vm.Alt = true;

        Assert.True(vm.HasError);
        Assert.False(vm.HasWarning);
        Assert.False(vm.CanSave);
    }
}
