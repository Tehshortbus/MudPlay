namespace FujinTerm.Models.GameData;

/// <summary>
/// One user-defined keybind. When the user presses
/// <see cref="Ctrl"/> / <see cref="Shift"/> / <see cref="Alt"/> +
/// <see cref="Key"/> while focus is on the terminal canvas or the
/// Conversation window's input field, the matching
/// <see cref="Command"/> is sent to the game in place of the raw
/// keystroke.
/// </summary>
/// <remarks>
/// <see cref="Command"/> may contain multiple steps separated by
/// either <c>^M</c> (literal caret-M) or <c>;</c>. Each split fragment
/// is sent as its own line (CR-terminated) — same convention every
/// other multi-step surface in the app uses (login automator,
/// triggers, aliases). No per-step delay; if pacing matters use the
/// triggers system instead.
/// </remarks>
/// <param name="Key">The literal key captured by the editor (e.g. <c>"F1"</c>, <c>"NumPad8"</c>, <c>"A"</c>). Maps to Avalonia's <c>Key</c> enum names.</param>
/// <param name="Ctrl">Ctrl modifier required.</param>
/// <param name="Shift">Shift modifier required.</param>
/// <param name="Alt">Alt modifier required.</param>
/// <param name="Command">
/// Command text to send. Split on <c>^M</c> and <c>;</c> for
/// multi-step. Supports variable substitution shared with
/// <see cref="Trigger"/> and <see cref="Alias"/>; the Phase 10 engine
/// applies it at fire time.
/// </param>
/// <param name="Enabled">Per-macro on / off without deleting.</param>
public sealed record Macro(
    string Key,
    bool Ctrl,
    bool Shift,
    bool Alt,
    string Command,
    bool Enabled)
{
    /// <summary>"Ctrl+Shift+F1"-style display string for the Macros table + edit dialog header.</summary>
    public string KeyChordLabel
    {
        get
        {
            string mods = (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "");
            return mods + Key;
        }
    }

    /// <summary>True when this macro's keybind matches the supplied chord (case-insensitive on key name).</summary>
    public bool Matches(string key, bool ctrl, bool shift, bool alt)
        => Enabled
        && Ctrl  == ctrl
        && Shift == shift
        && Alt   == alt
        && string.Equals(Key, key, StringComparison.OrdinalIgnoreCase);
}
