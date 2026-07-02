using Avalonia.Input;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class KeybindRegistryTests
{
    /// <summary>Fresh KeybindingStore seeded with the default chords — same as a brand-new profile.</summary>
    private static KeybindingStore DefaultStore() => new();

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Escape)]
    [InlineData(Key.Tab)]
    [InlineData(Key.Back)]
    [InlineData(Key.Delete)]
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.CapsLock)]
    public void ExcludedKeys_AreForbidden_RegardlessOfModifiers(Key key)
    {
        KeybindingStore store = DefaultStore();
        Assert.True(KeybindRegistry.IsForbidden(store, key, false, false, false, out _));
        Assert.True(KeybindRegistry.IsForbidden(store, key, true,  true,  true,  out _));
    }

    [Fact]
    public void IsReserved_FlagsBuiltInChord_WithActionName()
    {
        KeybindingStore store = DefaultStore();
        Assert.True(KeybindRegistry.IsReserved(store, Key.F2, false, false, false, out string? action));
        Assert.Equal("Conversation", action);

        Assert.True(KeybindRegistry.IsReserved(store, Key.G, true, false, false, out action));
        Assert.Equal("Game Data Browser", action);

        Assert.True(KeybindRegistry.IsReserved(store, Key.S, true, true, false, out action));
        Assert.Equal("Save profile as", action);
    }

    [Fact]
    public void IsReserved_DistinguishesByModifier()
    {
        KeybindingStore store = DefaultStore();
        // Ctrl+S is reserved (Save profile); plain S is not.
        Assert.True (KeybindRegistry.IsReserved(store, Key.S, ctrl: true,  shift: false, alt: false, out _));
        Assert.False(KeybindRegistry.IsReserved(store, Key.S, ctrl: false, shift: false, alt: false, out _));
    }

    [Fact]
    public void IsReserved_ReleasesChord_AfterRebind()
    {
        // Rebinding a built-in action away from its default frees that
        // chord — the macro edit dialog should immediately stop flagging
        // it as reserved. This is the live-query behaviour the rewrite
        // ships (vs the old hardcoded list which couldn't track rebinds).
        KeybindingStore store = DefaultStore();
        Assert.True(KeybindRegistry.IsReserved(store, Key.F2, false, false, false, out _));
        store.Rebind(Models.Profile.BuiltInAction.OpenConversation,
                     new Models.Profile.KeyChord(Key.F8));
        Assert.False(KeybindRegistry.IsReserved(store, Key.F2, false, false, false, out _));
        Assert.True (KeybindRegistry.IsReserved(store, Key.F8, false, false, false, out _));
    }

    [Fact]
    public void IsForbidden_ReportsReservedReason_WhenChordIsBuiltIn()
    {
        KeybindingStore store = DefaultStore();
        Assert.True(KeybindRegistry.IsForbidden(store, Key.F4, false, false, false, out string? reason));
        Assert.Contains("Player Workshop", reason);
    }

    [Fact]
    public void IsForbidden_AllowsPlainFunctionKey_WhenNotReserved()
    {
        KeybindingStore store = DefaultStore();
        // F1 / F8 / F12 aren't in the built-in shortcut list, so they're free.
        Assert.False(KeybindRegistry.IsForbidden(store, Key.F1,  false, false, false, out _));
        Assert.False(KeybindRegistry.IsForbidden(store, Key.F8,  false, false, false, out _));
        Assert.False(KeybindRegistry.IsForbidden(store, Key.F12, false, false, false, out _));
    }

    [Fact]
    public void IsReserved_FlagsSystemReservedChords_EvenWithoutStoreEntry()
    {
        KeybindingStore store = DefaultStore();
        // Alt+F4 is OS-level — never lives in the store.
        Assert.True(KeybindRegistry.IsReserved(store, Key.F4, ctrl: false, shift: false, alt: true, out string? a));
        Assert.Equal("Close window (OS)", a);
        // Ctrl+C / Ctrl+V are TerminalControl-level copy/paste.
        Assert.True(KeybindRegistry.IsReserved(store, Key.C, ctrl: true,  shift: false, alt: false, out a));
        Assert.Equal("Copy", a);
        Assert.True(KeybindRegistry.IsReserved(store, Key.V, ctrl: true,  shift: false, alt: false, out a));
        Assert.Equal("Paste", a);
    }

    [Fact]
    public void BindableKeys_IncludesEveryFunctionKey_PlusNumpadAndLetters()
    {
        Assert.Contains(KeybindRegistry.BindableKeys, b => b.Key == Key.F1);
        Assert.Contains(KeybindRegistry.BindableKeys, b => b.Key == Key.F12);
        Assert.Contains(KeybindRegistry.BindableKeys, b => b.Key == Key.NumPad0);
        Assert.Contains(KeybindRegistry.BindableKeys, b => b.Key == Key.NumPad9);
        Assert.Contains(KeybindRegistry.BindableKeys, b => b.Key == Key.A);
        Assert.Contains(KeybindRegistry.BindableKeys, b => b.Key == Key.Z);
    }

    [Fact]
    public void KeyboardPeriod_IsForbidden_ButNumpadPeriodStaysBindable()
    {
        KeybindingStore store = DefaultStore();
        // The keyboard period is the slow-talk say-precursor — it must never
        // be bindable, so a macro can't swallow the leading `.` of a say.
        Assert.True(KeybindRegistry.IsForbidden(store, Key.OemPeriod, false, false, false, out _));
        Assert.DoesNotContain(KeybindRegistry.BindableKeys, b => b.Key == Key.OemPeriod);
        // The numpad period is a normal movement key and stays bindable.
        Assert.False(KeybindRegistry.IsForbidden(store, Key.Decimal, false, false, false, out _));
        Assert.Contains(KeybindRegistry.BindableKeys, b => b.Key == Key.Decimal);
    }
}
