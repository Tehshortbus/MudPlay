using Avalonia.Input;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

// Single source of truth for which keys + key combinations the user is
// allowed to bind a macro to. Encodes:
//   Bindable keys — the picker list shown in the macro edit dialog (F1-F12,
//     Numpad 0-9 + operators, A-Z, 0-9, navigation cluster, common
//     punctuation).
//   Excluded keys — keys that can never be bound regardless of modifier
//     (Enter / Escape / Tab / Backspace / Delete / pure modifier keys).
//   Reserved combos — chord+modifier combinations hardcoded elsewhere (the
//     F2 / F3 / ... open-window shortcuts in Views.GlobalHotkeys, the
//     Ctrl+C / Ctrl+V copy/paste keys in Controls.TerminalControl). Macro
//     binding to a reserved combo would silently steal the keystroke from
//     the built-in handler.
//
// The excluded/bindable/reserved split enforces one threat model: don't let
// a user-bound macro accidentally hijack copy/paste or the OS close-window
// shortcut. Reserved-combo list mirrors what Views.GlobalHotkeys wires up.
public static class KeybindRegistry
{
    // Avalonia Key values offered in the edit dialog's picker. Display name
    // first, key second so the combo can show a friendly label ("Numpad 8"
    // instead of "NumPad8").
    public static readonly IReadOnlyList<(string DisplayName, Key Key)> BindableKeys = BuildBindableKeys();

    private static IReadOnlyList<(string DisplayName, Key Key)> BuildBindableKeys()
    {
        List<(string, Key)> keys = new();

        // Function keys
        for (int i = 1; i <= 12; i++)
            keys.Add(($"F{i}", Enum.Parse<Key>($"F{i}")));

        // Numpad digits
        for (int i = 0; i <= 9; i++)
            keys.Add(($"Numpad {i}", Enum.Parse<Key>($"NumPad{i}")));
        keys.Add(("Numpad *", Key.Multiply));
        keys.Add(("Numpad +", Key.Add));
        keys.Add(("Numpad -", Key.Subtract));
        keys.Add(("Numpad .", Key.Decimal));
        keys.Add(("Numpad /", Key.Divide));

        // Letters
        for (char c = 'A'; c <= 'Z'; c++)
            keys.Add((c.ToString(), Enum.Parse<Key>(c.ToString())));

        // Top-row digits — Avalonia names them D0..D9.
        for (int i = 0; i <= 9; i++)
            keys.Add((i.ToString(), Enum.Parse<Key>($"D{i}")));

        // Navigation cluster.
        keys.Add(("Space",     Key.Space));
        keys.Add(("Insert",    Key.Insert));
        keys.Add(("Home",      Key.Home));
        keys.Add(("End",       Key.End));
        keys.Add(("Page Up",   Key.PageUp));
        keys.Add(("Page Down", Key.PageDown));
        keys.Add(("Up",        Key.Up));
        keys.Add(("Down",      Key.Down));
        keys.Add(("Left",      Key.Left));
        keys.Add(("Right",     Key.Right));

        // OEM punctuation that survives Avalonia's cross-platform mapping.
        // The keyboard period (Key.OemPeriod) is deliberately absent — it's
        // the MajorMUD say-precursor in `set talk slow`, so it must always
        // reach the game as typed text and can never be a macro chord (see
        // ExcludedKeys). The numpad period (Key.Decimal) stays bindable.
        keys.Add((";",  Key.OemSemicolon));
        keys.Add(("=",  Key.OemPlus));
        keys.Add((",",  Key.OemComma));
        keys.Add(("-",  Key.OemMinus));
        keys.Add(("/",  Key.OemQuestion));
        keys.Add(("`",  Key.OemTilde));
        keys.Add(("[",  Key.OemOpenBrackets));
        keys.Add(("\\", Key.OemPipe));
        keys.Add(("]",  Key.OemCloseBrackets));
        keys.Add(("'",  Key.OemQuotes));

        return keys;
    }

    // Keys that can never be bound. Includes the obvious text-editing keys
    // plus the pure modifier keys (binding "Ctrl" alone makes no sense — the
    // user means "Ctrl+something").
    public static readonly IReadOnlySet<Key> ExcludedKeys = new HashSet<Key>
    {
        Key.Enter, Key.Return, Key.Escape, Key.Tab, Key.Back, Key.Delete, Key.None,
        Key.LeftCtrl, Key.RightCtrl, Key.LeftShift, Key.RightShift,
        Key.LeftAlt, Key.RightAlt, Key.LWin, Key.RWin, Key.CapsLock,
        Key.NumLock, Key.Scroll, Key.PrintScreen, Key.Pause,
        // Keyboard period is MajorMUD's `set talk slow` say-precursor — a
        // leading `.` marks the line as a say. Binding it to a macro would
        // swallow that keystroke before it reached the wire, so every
        // slow-talk say gets rejected. It must always pass through as text.
        // (The numpad period, Key.Decimal, is unaffected and stays bindable.)
        Key.OemPeriod,
    };

    // Chords reserved at the OS / terminal level — these never live in
    // KeybindingStore because the user can't rebind them, but they still need
    // to be rejected as macro keybinds. Alt+F4 stays the system's
    // window-close shortcut; Ctrl+C / Ctrl+V stay copy / paste in
    // Controls.TerminalControl.
    private static readonly IReadOnlyList<(Key Key, bool Ctrl, bool Shift, bool Alt, string Action)> _systemReserved = new[]
    {
        (Key.C,  true,  false, false, "Copy"),
        (Key.V,  true,  false, false, "Paste"),
        (Key.F4, false, false, true,  "Close window (OS)"),
    };

    // True when the supplied chord is reserved by the app or the OS /
    // terminal — returns a friendly action name via action. Queries the live
    // KeybindingStore for built-in-action collisions so a rebind takes effect
    // immediately for downstream conflict checks (the macro edit dialog stops
    // flagging the old chord the moment it's freed).
    public static bool IsReserved(KeybindingStore store, Key key, bool ctrl, bool shift, bool alt, out string? action)
    {
        ArgumentNullException.ThrowIfNull(store);
        foreach ((Key k, bool c, bool s, bool a, string label) in _systemReserved)
        {
            if (k == key && c == ctrl && s == shift && a == alt)
            {
                action = label;
                return true;
            }
        }
        BuiltInAction? hit = store.FindAction(new KeyChord(key, ctrl, shift, alt));
        if (hit is not null)
        {
            action = KeybindingStore.ActionLabel(hit.Value);
            return true;
        }
        action = null;
        return false;
    }

    // Refuse to bind a chord that's either an excluded key or a reserved combo.
    public static bool IsForbidden(KeybindingStore store, Key key, bool ctrl, bool shift, bool alt, out string? reason)
    {
        if (ExcludedKeys.Contains(key))
        {
            reason = $"'{key}' can't be bound as a macro.";
            return true;
        }
        if (IsReserved(store, key, ctrl, shift, alt, out string? action))
        {
            reason = $"Reserved by built-in action: {action}.";
            return true;
        }
        reason = null;
        return false;
    }

    // Look up a key in the bindable list by its enum value. null when not present.
    public static (string DisplayName, Key Key)? FindBindable(Key key)
    {
        foreach ((string display, Key k) in BindableKeys)
            if (k == key) return (display, k);
        return null;
    }

}
