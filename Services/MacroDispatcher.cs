using System.Text;
using Avalonia.Input;
using FujinTerm.Models.GameData;

namespace FujinTerm.Services;

/// <summary>
/// Runtime hook between a key-down event and the active Telnet
/// connection. Given an (<see cref="Key"/>, <see cref="KeyModifiers"/>)
/// chord, looks up a matching <see cref="Macro"/> in
/// <see cref="MacroStore"/>; if found, splits the command on
/// <c>^M</c> / <c>;</c> via <see cref="MacroStore.SplitCommandSteps"/>
/// and sends each step as its own CR-terminated line through the
/// injected byte sender.
/// </summary>
/// <remarks>
/// The sender is bound after construction (see <see cref="SetSender"/>)
/// because <see cref="AppServices"/> instantiates this service before
/// the main window's telnet client exists. Without a bound sender,
/// <see cref="TryHandleKey"/> is a silent no-op — keystrokes fall
/// through to their normal terminal handling. That's the right
/// pre-connection behaviour: the user can still type into the panel
/// even without a live socket.
/// </remarks>
public sealed class MacroDispatcher
{
    private readonly MacroStore _store;
    private Action<byte[]>? _sender;

    public MacroDispatcher(MacroStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>Bind the wire-send callback. Called once at main-window construction.</summary>
    public void SetSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _sender = sender;
    }

    /// <summary>
    /// Look up a matching macro for the chord. If found, fire each
    /// split step and return <c>true</c> so the caller can mark the
    /// keystroke handled. <c>false</c> when no macro matches or no
    /// sender is bound — caller should fall through to normal key
    /// handling.
    /// </summary>
    public bool TryHandleKey(Key key, KeyModifiers modifiers)
    {
        if (_sender is null) return false;

        // Never let a macro fire on a key the registry forbids from binding
        // (Enter, Escape, the keyboard period say-precursor, …). The edit
        // dialog already refuses these, but a chord persisted before the key
        // was excluded would otherwise still hijack the keystroke — this
        // keeps the invariant true at dispatch time regardless of stale data.
        if (KeybindRegistry.ExcludedKeys.Contains(key)) return false;

        bool ctrl  = modifiers.HasFlag(KeyModifiers.Control);
        bool shift = modifiers.HasFlag(KeyModifiers.Shift);
        bool alt   = modifiers.HasFlag(KeyModifiers.Alt);

        Macro? macro = _store.FindMatch(key.ToString(), ctrl, shift, alt);
        if (macro is null) return false;

        FireMacro(macro);
        return true;
    }

    /// <summary>
    /// Test hook — fires a macro directly via the bound sender. Used
    /// by <see cref="TryHandleKey"/> internally and by tests that
    /// want to assert on the bytes that go out for a given command
    /// without simulating a key event.
    /// </summary>
    internal void FireMacro(Macro macro)
    {
        if (_sender is null) return;

        foreach (string step in MacroStore.SplitCommandSteps(macro.Command))
        {
            // Latin-1: BBSes expect 8-bit clean bytes, not UTF-8 — same
            // encoding the terminal uses for ordinary keystrokes.
            byte[] bytes = Encoding.Latin1.GetBytes(step + "\r");
            _sender(bytes);
        }
    }
}
