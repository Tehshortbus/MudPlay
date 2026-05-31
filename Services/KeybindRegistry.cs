using Avalonia.Input;

namespace FujinTerm.Services;

/// <summary>
/// Single source of truth for which keys + key combinations the user
/// is allowed to bind a macro to. Encodes:
/// <list type="bullet">
///   <item><b>Bindable keys</b> — the picker list shown in the macro
///         edit dialog (F1-F12, Numpad 0-9 + operators, A-Z, 0-9,
///         navigation cluster, common punctuation).</item>
///   <item><b>Excluded keys</b> — keys that can never be bound regardless
///         of modifier (Enter / Escape / Tab / Backspace / Delete /
///         pure modifier keys).</item>
///   <item><b>Reserved combos</b> — chord+modifier combinations
///         hardcoded elsewhere (the F2 / F3 / ... open-window
///         shortcuts in <see cref="Views.GlobalHotkeys"/>, the
///         Ctrl+C / Ctrl+V copy/paste keys in
///         <see cref="Controls.TerminalControl"/>). Macro binding to a
///         reserved combo would silently steal the keystroke from the
///         built-in handler.</item>
/// </list>
/// </summary>
/// <remarks>
/// Adapted from MudProxyViewer's <c>MacroManager.ExcludedKeys</c> +
/// <c>BindableKeys</c> + <c>IsExcludedCombo</c> design — same threat
/// model (don't let a user-bound macro accidentally hijack copy/paste
/// or the OS close window). Reserved-combo list mirrors what
/// <see cref="Views.GlobalHotkeys"/> wires up.
/// </remarks>
public static class KeybindRegistry
{
    /// <summary>
    /// Avalonia <see cref="Key"/> values offered in the edit dialog's
    /// picker. Display name first, key second so the combo can show a
    /// friendly label ("Numpad 8" instead of "NumPad8").
    /// </summary>
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
        keys.Add((";",  Key.OemSemicolon));
        keys.Add(("=",  Key.OemPlus));
        keys.Add((",",  Key.OemComma));
        keys.Add(("-",  Key.OemMinus));
        keys.Add((".",  Key.OemPeriod));
        keys.Add(("/",  Key.OemQuestion));
        keys.Add(("`",  Key.OemTilde));
        keys.Add(("[",  Key.OemOpenBrackets));
        keys.Add(("\\", Key.OemPipe));
        keys.Add(("]",  Key.OemCloseBrackets));
        keys.Add(("'",  Key.OemQuotes));

        return keys;
    }

    /// <summary>
    /// Keys that can never be bound. Includes the obvious text-editing
    /// keys plus the pure modifier keys (binding "Ctrl" alone makes no
    /// sense — the user means "Ctrl+something").
    /// </summary>
    public static readonly IReadOnlySet<Key> ExcludedKeys = new HashSet<Key>
    {
        Key.Enter, Key.Return, Key.Escape, Key.Tab, Key.Back, Key.Delete, Key.None,
        Key.LeftCtrl, Key.RightCtrl, Key.LeftShift, Key.RightShift,
        Key.LeftAlt, Key.RightAlt, Key.LWin, Key.RWin, Key.CapsLock,
        Key.NumLock, Key.Scroll, Key.PrintScreen, Key.Pause,
    };

    /// <summary>
    /// Combinations reserved by the app itself — built-in shortcuts in
    /// <see cref="Views.GlobalHotkeys"/> + TerminalControl-level copy/paste.
    /// If a macro could bind to these the built-in action would silently
    /// stop working. Order doesn't matter; lookup is by exact match.
    /// </summary>
    private static readonly IReadOnlyList<ReservedChord> _reservedCombos = new ReservedChord[]
    {
        // Built-in window-toggle / app-action shortcuts (mirror GlobalHotkeys).
        new(Key.F2,        Ctrl: false, Shift: false, Alt: false, Action: "Open Conversation"),
        new(Key.F3,        Ctrl: false, Shift: false, Alt: false, Action: "Open Party"),
        new(Key.F4,        Ctrl: false, Shift: false, Alt: false, Action: "Open Workshop"),
        new(Key.F5,        Ctrl: false, Shift: false, Alt: false, Action: "Open Navigation"),
        new(Key.F7,        Ctrl: false, Shift: false, Alt: false, Action: "Open Spell Book"),
        new(Key.F9,        Ctrl: false, Shift: false, Alt: false, Action: "Open Program Log"),
        new(Key.F10,       Ctrl: false, Shift: false, Alt: false, Action: "Open Backscroll"),
        new(Key.F11,       Ctrl: false, Shift: false, Alt: false, Action: "Open Session Stats"),
        new(Key.OemComma,  Ctrl: true,  Shift: false, Alt: false, Action: "Open Settings"),
        new(Key.G,         Ctrl: true,  Shift: false, Alt: false, Action: "Open Game Data Browser"),
        new(Key.K,         Ctrl: true,  Shift: false, Alt: false, Action: "Toggle connection"),
        new(Key.Q,         Ctrl: true,  Shift: false, Alt: false, Action: "Quit"),
        new(Key.N,         Ctrl: true,  Shift: false, Alt: false, Action: "New profile"),
        new(Key.O,         Ctrl: true,  Shift: false, Alt: false, Action: "Open profile"),
        new(Key.S,         Ctrl: true,  Shift: false, Alt: false, Action: "Save profile"),
        new(Key.S,         Ctrl: true,  Shift: true,  Alt: false, Action: "Save profile as"),
        // Terminal-level reserved combos (handled in TerminalControl).
        new(Key.C,         Ctrl: true,  Shift: false, Alt: false, Action: "Copy"),
        new(Key.V,         Ctrl: true,  Shift: false, Alt: false, Action: "Paste"),
        // OS-level — taking Alt+F4 over from the window manager is hostile.
        new(Key.F4,        Ctrl: false, Shift: false, Alt: true,  Action: "Close window (OS)"),
    };

    /// <summary>
    /// True when the supplied chord is reserved by the app — returns
    /// the action name via <paramref name="action"/> so the edit
    /// dialog can show <i>"reserved: Open Conversation"</i> instead of
    /// just refusing silently.
    /// </summary>
    public static bool IsReserved(Key key, bool ctrl, bool shift, bool alt, out string? action)
    {
        foreach (ReservedChord r in _reservedCombos)
        {
            if (r.Key == key && r.Ctrl == ctrl && r.Shift == shift && r.Alt == alt)
            {
                action = r.Action;
                return true;
            }
        }
        action = null;
        return false;
    }

    /// <summary>Convenience: refuse to bind a chord that's either an excluded key or a reserved combo.</summary>
    public static bool IsForbidden(Key key, bool ctrl, bool shift, bool alt, out string? reason)
    {
        if (ExcludedKeys.Contains(key))
        {
            reason = $"'{key}' can't be bound as a macro.";
            return true;
        }
        if (IsReserved(key, ctrl, shift, alt, out string? action))
        {
            reason = $"Reserved by built-in action: {action}.";
            return true;
        }
        reason = null;
        return false;
    }

    /// <summary>Look up a key in the bindable list by its enum value. <c>null</c> when not present.</summary>
    public static (string DisplayName, Key Key)? FindBindable(Key key)
    {
        foreach ((string display, Key k) in BindableKeys)
            if (k == key) return (display, k);
        return null;
    }

    private readonly record struct ReservedChord(Key Key, bool Ctrl, bool Shift, bool Alt, string Action);
}
