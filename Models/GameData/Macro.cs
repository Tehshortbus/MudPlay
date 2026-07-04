namespace FujinTerm.Models.GameData;

// One user-defined keybind. When the user presses Ctrl / Shift / Alt +
// Key while focus is on the terminal canvas or the Conversation window's
// input field, the matching Command is sent to the game in place of the
// raw keystroke.
//
// Command may contain multiple steps separated by either ^M (literal
// caret-M) or ;. Each split fragment is sent as its own line
// (CR-terminated) — same convention every other multi-step surface in the
// app uses (login automator, triggers, aliases). No per-step delay; if
// pacing matters use the triggers system instead.
//
// Key is the literal key captured by the editor (e.g. "F1", "NumPad8",
// "A"), mapping to Avalonia's Key enum names. Command supports variable
// substitution shared with Trigger and Alias, applied at fire time.
public sealed record Macro(
    string Key,
    bool Ctrl,
    bool Shift,
    bool Alt,
    string Command,
    bool Enabled)
{
    // "Ctrl+Shift+F1"-style display string for the Macros table + edit dialog header.
    public string KeyChordLabel
    {
        get
        {
            string mods = (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "");
            return mods + Key;
        }
    }

    // True when this macro's keybind matches the supplied chord (case-insensitive on key name).
    public bool Matches(string key, bool ctrl, bool shift, bool alt)
        => Enabled
        && Ctrl  == ctrl
        && Shift == shift
        && Alt   == alt
        && string.Equals(Key, key, StringComparison.OrdinalIgnoreCase);
}
