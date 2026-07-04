using Avalonia.Input;

namespace FujinTerm.Models.Profile;

// One key combination — primary Key + three modifier flags. Serializes as JSON;
// matches the chord shape GameData.Macro stores so macros and built-in actions
// can share conflict-check machinery. Empty represents "unbound" so a built-in
// action with no chord can still live in the table.
public readonly record struct KeyChord(
    Key Key   = Key.None,
    bool Ctrl  = false,
    bool Shift = false,
    bool Alt   = false)
{
    // Sentinel: the chord is unset.
    public static readonly KeyChord Empty = default;

    // True when no key is set.
    public bool IsEmpty => Key == Key.None;

    // Display label like "Ctrl+Shift+F1" — used by menu InputGesture text and
    // the toolbar tooltip. Empty chord renders as an empty string so callers can
    // fall back to a placeholder.
    public string Label
    {
        get
        {
            if (IsEmpty) return string.Empty;
            string mods = (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "");
            return mods + KeyName;
        }
    }

    // Friendly name for the key — strips Avalonia's D0..D9 (top-row digits) and
    // NumPadN (numpad digits) to a shorter human form, and maps the common Oem*
    // punctuation keys back to their printable character. Falls back to the enum
    // name otherwise.
    public string KeyName
    {
        get
        {
            string raw = Key.ToString();
            if (raw.Length == 2 && raw[0] == 'D' && char.IsDigit(raw[1])) return raw[1..];
            if (raw.StartsWith("NumPad", StringComparison.Ordinal) &&
                raw.Length == "NumPad".Length + 1 && char.IsDigit(raw[^1]))
                return "Numpad " + raw[^1];
            return Key switch
            {
                Key.OemComma        => ",",
                Key.OemPeriod       => ".",
                Key.OemMinus        => "-",
                Key.OemPlus         => "=",
                Key.OemQuestion     => "/",
                Key.OemSemicolon    => ";",
                Key.OemQuotes       => "'",
                Key.OemOpenBrackets => "[",
                Key.OemCloseBrackets=> "]",
                Key.OemPipe         => "\\",
                Key.OemTilde        => "`",
                _ => raw,
            };
        }
    }

    // Avalonia gesture string for KeyGesture.Parse. Format: "Ctrl+Shift+F1" with
    // Key's enum name (NOT the friendly form — KeyGesture.Parse won't accept
    // "Numpad 8"). Returns null for an empty chord so the caller can skip
    // registration.
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
