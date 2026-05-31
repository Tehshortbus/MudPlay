using Avalonia.Input;

namespace FujinTerm.Models.Profile;

/// <summary>
/// One key combination — primary <see cref="Key"/> + three modifier
/// flags. Serializes as JSON; matches the chord shape <see cref="GameData.Macro"/>
/// stores so macros and built-in actions can share conflict-check
/// machinery. <see cref="Empty"/> represents "unbound" so a built-in
/// action with no chord can still live in the table.
/// </summary>
public readonly record struct KeyChord(
    Key Key   = Key.None,
    bool Ctrl  = false,
    bool Shift = false,
    bool Alt   = false)
{
    /// <summary>Sentinel: the chord is unset.</summary>
    public static readonly KeyChord Empty = default;

    /// <summary>True when no key is set.</summary>
    public bool IsEmpty => Key == Key.None;

    /// <summary>
    /// Display label like <c>"Ctrl+Shift+F1"</c> — used by menu
    /// InputGesture text and the toolbar tooltip. Empty chord renders
    /// as an empty string so callers can fall back to a placeholder.
    /// </summary>
    public string Label
    {
        get
        {
            if (IsEmpty) return string.Empty;
            string mods = (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "");
            return mods + KeyName;
        }
    }

    /// <summary>
    /// Friendly name for the key — strips Avalonia's <c>D0..D9</c>
    /// (top-row digits) and <c>NumPadN</c> (numpad digits) to a
    /// shorter human form. Falls back to the enum name otherwise.
    /// </summary>
    public string KeyName
    {
        get
        {
            string raw = Key.ToString();
            if (raw.Length == 2 && raw[0] == 'D' && char.IsDigit(raw[1])) return raw[1..];
            if (raw.StartsWith("NumPad", StringComparison.Ordinal) &&
                raw.Length == "NumPad".Length + 1 && char.IsDigit(raw[^1]))
                return "Numpad " + raw[^1];
            return raw;
        }
    }

    /// <summary>
    /// Avalonia gesture string for <see cref="KeyGesture.Parse"/>.
    /// Format: <c>"Ctrl+Shift+F1"</c> with <see cref="Key"/>'s enum
    /// name (NOT the friendly form — KeyGesture.Parse won't accept
    /// "Numpad 8"). Returns <c>null</c> for an empty chord so the
    /// caller can skip registration.
    /// </summary>
    public string? GestureString
    {
        get
        {
            if (IsEmpty) return null;
            string mods = (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "");
            return mods + Key;
        }
    }
}
