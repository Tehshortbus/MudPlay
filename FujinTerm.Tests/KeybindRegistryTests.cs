using Avalonia.Input;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class KeybindRegistryTests
{
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
        Assert.True(KeybindRegistry.IsForbidden(key, false, false, false, out _));
        Assert.True(KeybindRegistry.IsForbidden(key, true,  true,  true,  out _));
    }

    [Fact]
    public void IsReserved_FlagsBuiltInChord_WithActionName()
    {
        Assert.True(KeybindRegistry.IsReserved(Key.F2, false, false, false, out string? action));
        Assert.Equal("Open Conversation", action);

        Assert.True(KeybindRegistry.IsReserved(Key.G, true, false, false, out action));
        Assert.Equal("Open Game Data Browser", action);

        Assert.True(KeybindRegistry.IsReserved(Key.S, true, true, false, out action));
        Assert.Equal("Save profile as", action);
    }

    [Fact]
    public void IsReserved_DistinguishesByModifier()
    {
        // Ctrl+S is reserved (Save profile); plain S is not.
        Assert.True (KeybindRegistry.IsReserved(Key.S, ctrl: true,  shift: false, alt: false, out _));
        Assert.False(KeybindRegistry.IsReserved(Key.S, ctrl: false, shift: false, alt: false, out _));
    }

    [Fact]
    public void IsForbidden_ReportsReservedReason_WhenChordIsBuiltIn()
    {
        Assert.True(KeybindRegistry.IsForbidden(Key.F4, false, false, false, out string? reason));
        Assert.Contains("Open Workshop", reason);
    }

    [Fact]
    public void IsForbidden_AllowsPlainFunctionKey_WhenNotReserved()
    {
        // F1 / F6 / F8 / F12 aren't in the built-in shortcut list, so they're free.
        Assert.False(KeybindRegistry.IsForbidden(Key.F1,  false, false, false, out _));
        Assert.False(KeybindRegistry.IsForbidden(Key.F6,  false, false, false, out _));
        Assert.False(KeybindRegistry.IsForbidden(Key.F8,  false, false, false, out _));
        Assert.False(KeybindRegistry.IsForbidden(Key.F12, false, false, false, out _));
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
}
