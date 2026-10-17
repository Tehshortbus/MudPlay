using Avalonia.Input;
using MudPlay.Controls;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The keyboard-fallthrough policy (which keys a focused dialog forwards to the
// terminal) and the router that carries them. The focus check and event routing
// are UI plumbing (smoke-tested), but the pure decision + gate logic is pinned
// here.
public sealed class TerminalInputRoutingTests
{
    // ----- ShouldForwardKey policy ---------------------------------------

    [Theory]
    [InlineData(Key.A, KeyModifiers.None, true)]                 // typing forwards
    [InlineData(Key.Left, KeyModifiers.None, true)]              // arrows forward
    [InlineData(Key.Enter, KeyModifiers.None, true)]             // enter forwards
    [InlineData(Key.Back, KeyModifiers.None, true)]              // backspace forwards
    [InlineData(Key.F1, KeyModifiers.None, true)]                // function keys forward
    [InlineData(Key.C, KeyModifiers.Control, true)]             // Ctrl-chord forwards
    [InlineData(Key.A, KeyModifiers.Control | KeyModifiers.Alt, true)]  // Ctrl+Alt still forwards
    [InlineData(Key.Tab, KeyModifiers.None, false)]             // focus nav stays
    [InlineData(Key.Escape, KeyModifiers.None, false)]          // cancel stays
    [InlineData(Key.LWin, KeyModifiers.None, false)]            // system key stays
    [InlineData(Key.Apps, KeyModifiers.None, false)]            // context-menu key stays
    [InlineData(Key.F, KeyModifiers.Alt, false)]               // Alt-menu accelerator stays
    public void ShouldForwardKey_Policy(Key key, KeyModifiers mods, bool expected)
        => Assert.Equal(expected, DialogKeyboardFallthrough.ShouldForwardKey(key, mods));

    // ----- TerminalInputRouter gating ------------------------------------

    [Fact]
    public void Router_NoTerminal_ForwardsAreNoOps()
    {
        var router = new TerminalInputRouter();
        Assert.False(router.HasTerminal);
        Assert.False(router.ForwardKey(Key.A, KeyModifiers.None));
        Assert.False(router.ForwardText("a"));
    }

    [Fact]
    public void Router_Disabled_DoesNotForward()
    {
        var router = new TerminalInputRouter { Enabled = false };
        router.RegisterTerminal((_, _) => true, _ => true);
        Assert.False(router.ForwardKey(Key.A, KeyModifiers.None));
        Assert.False(router.ForwardText("a"));
    }

    [Fact]
    public void Router_EnabledAndRegistered_DelegatesToCore()
    {
        Key? sawKey = null;
        string? sawText = null;
        var router = new TerminalInputRouter();
        router.RegisterTerminal(
            (k, _) => { sawKey = k; return true; },
            t => { sawText = t; return true; });

        Assert.True(router.ForwardKey(Key.Up, KeyModifiers.None));
        Assert.True(router.ForwardText("hi"));
        Assert.Equal(Key.Up, sawKey);
        Assert.Equal("hi", sawText);
    }

    [Fact]
    public void Router_Unregister_StopsForwarding()
    {
        var router = new TerminalInputRouter();
        router.RegisterTerminal((_, _) => true, _ => true);
        Assert.True(router.HasTerminal);

        router.UnregisterTerminal();
        Assert.False(router.HasTerminal);
        Assert.False(router.ForwardKey(Key.A, KeyModifiers.None));
    }

    [Fact]
    public void Router_ForwardReturnsCoreResult()
    {
        // A printable key the terminal doesn't map returns false from the core,
        // so the caller leaves the KeyDown unhandled for the TextInput to carry.
        var router = new TerminalInputRouter();
        router.RegisterTerminal((_, _) => false, _ => true);
        Assert.False(router.ForwardKey(Key.A, KeyModifiers.None));
    }
}
