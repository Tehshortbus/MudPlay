namespace FujinTerm.Models.GameData;

/// <summary>
/// One user-defined keybind. When the user presses
/// <see cref="Modifier"/>+<see cref="Key"/> while focus is on the
/// terminal canvas or the Conversation window's input field, the
/// matching <see cref="Command"/> is sent to the game in place of the
/// raw keystroke.
/// </summary>
/// <param name="Name">Display name shown in the Macros list.</param>
/// <param name="Key">The literal key captured by the editor (e.g. "F1", "K").</param>
/// <param name="Modifier">Modifier mask string (e.g. "Ctrl", "Ctrl+Shift") — empty when none.</param>
/// <param name="Command">
/// Command text to send. Supports variable substitution shared with
/// <see cref="Trigger"/> and <see cref="Alias"/>; the Phase 10 engine
/// applies it at fire time.
/// </param>
/// <param name="Enabled">Per-macro on / off without deleting.</param>
public sealed record Macro(
    string Name,
    string Key,
    string Modifier,
    string Command,
    bool Enabled);
