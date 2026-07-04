using System.Text;
using Avalonia.Input;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class MacroDispatcherTests
{
    [Fact]
    public void TryHandleKey_ReturnsFalse_WhenNoSenderBound()
    {
        MacroStore store = new();
        store.Macros.Add(new Macro("NumPad8", false, false, false, "n", true));
        MacroDispatcher d = new(store);

        Assert.False(d.TryHandleKey(Key.NumPad8, KeyModifiers.None));
    }

    [Fact]
    public void TryHandleKey_ReturnsFalse_WhenChordHasNoMatch()
    {
        MacroStore store = new();
        store.Macros.Add(new Macro("NumPad8", false, false, false, "n", true));
        MacroDispatcher d = new(store);
        d.SetSender(_ => { });

        Assert.False(d.TryHandleKey(Key.NumPad9, KeyModifiers.None));
    }

    [Fact]
    public void TryHandleKey_FiresSingleStepMacro_WithCrTerminator()
    {
        MacroStore store = new();
        store.Macros.Add(new Macro("NumPad8", false, false, false, "n", true));
        List<byte[]> sent = new();
        MacroDispatcher d = new(store);
        d.SetSender(sent.Add);

        Assert.True(d.TryHandleKey(Key.NumPad8, KeyModifiers.None));
        Assert.Single(sent);
        Assert.Equal("n\r", Encoding.Latin1.GetString(sent[0]));
    }

    [Fact]
    public void TryHandleKey_FiresMultiStepMacro_OneSendPerFragment()
    {
        MacroStore store = new();
        store.Macros.Add(new Macro("F1", false, false, false,
            Command: "open chest;look in chest;take all", Enabled: true));
        List<byte[]> sent = new();
        MacroDispatcher d = new(store);
        d.SetSender(sent.Add);

        Assert.True(d.TryHandleKey(Key.F1, KeyModifiers.None));
        Assert.Equal(3, sent.Count);
        Assert.Equal("open chest\r",    Encoding.Latin1.GetString(sent[0]));
        Assert.Equal("look in chest\r", Encoding.Latin1.GetString(sent[1]));
        Assert.Equal("take all\r",      Encoding.Latin1.GetString(sent[2]));
    }

    [Fact]
    public void TryHandleKey_AcceptsCaretM_AsCarriageReturnDelimiter()
    {
        MacroStore store = new();
        store.Macros.Add(new Macro("F2", false, false, false,
            Command: "sneak^Mhide", Enabled: true));
        List<byte[]> sent = new();
        MacroDispatcher d = new(store);
        d.SetSender(sent.Add);

        Assert.True(d.TryHandleKey(Key.F2, KeyModifiers.None));
        Assert.Equal(2, sent.Count);
        Assert.Equal("sneak\r", Encoding.Latin1.GetString(sent[0]));
        Assert.Equal("hide\r",  Encoding.Latin1.GetString(sent[1]));
    }

    [Fact]
    public void TryHandleKey_DistinguishesModifierVariants()
    {
        MacroStore store = new();
        store.Macros.Add(new Macro("H", Ctrl: true,  Shift: false, Alt: false, Command: "cast heal me",  Enabled: true));
        store.Macros.Add(new Macro("H", Ctrl: true,  Shift: true,  Alt: false, Command: "cast nuke 1",  Enabled: true));
        List<byte[]> sent = new();
        MacroDispatcher d = new(store);
        d.SetSender(sent.Add);

        // Ctrl+H → heal
        Assert.True(d.TryHandleKey(Key.H, KeyModifiers.Control));
        Assert.Equal("cast heal me\r", Encoding.Latin1.GetString(sent[^1]));

        // Ctrl+Shift+H → nuke
        Assert.True(d.TryHandleKey(Key.H, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.Equal("cast nuke 1\r", Encoding.Latin1.GetString(sent[^1]));

        // Plain H → no match
        Assert.False(d.TryHandleKey(Key.H, KeyModifiers.None));
    }

    [Fact]
    public void TryHandleKey_IgnoresDisabledMacro()
    {
        MacroStore store = new();
        store.Macros.Add(new Macro("NumPad8", false, false, false, "n", Enabled: false));
        List<byte[]> sent = new();
        MacroDispatcher d = new(store);
        d.SetSender(sent.Add);

        Assert.False(d.TryHandleKey(Key.NumPad8, KeyModifiers.None));
        Assert.Empty(sent);
    }

    [Fact]
    public void TryHandleKey_NeverFiresMacroBoundToExcludedKey()
    {
        // A chord persisted before the keyboard period was excluded must not
        // hijack the keystroke — otherwise the slow-talk say-precursor `.`
        // gets swallowed and every say is rejected. The dispatcher enforces
        // the exclusion at fire time regardless of stale stored data.
        MacroStore store = new();
        store.Macros.Add(new Macro("OemPeriod", false, false, false, "d", Enabled: true));
        List<byte[]> sent = new();
        MacroDispatcher d = new(store);
        d.SetSender(sent.Add);

        Assert.False(d.TryHandleKey(Key.OemPeriod, KeyModifiers.None));
        Assert.Empty(sent);
    }
}
