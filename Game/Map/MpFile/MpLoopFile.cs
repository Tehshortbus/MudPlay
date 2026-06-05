using System.Collections.Generic;

namespace FujinTerm.Game.Map.MpFile;

/// <summary>
/// Parsed-but-unresolved representation of a single <c>.mp</c> loop
/// file. Geometry only: no <see cref="RoomKey"/>s are assigned at this
/// stage because the file doesn't carry coordinates — the
/// <c>MpFileImporter</c> resolves the start anchor against the active
/// <see cref="RoomGraphManager"/> and walks the steps to fill in the
/// actual room sequence.
/// </summary>
/// <param name="Label">First bracket on line 1 — the loop's display label.</param>
/// <param name="Author">Second bracket on line 1, or empty.</param>
/// <param name="Code4">4-char rooms.md room code from the second header line.</param>
/// <param name="GroupName">rooms.md folder/group name from the second header line.</param>
/// <param name="RoomName">User-facing room name from the second header line.</param>
/// <param name="StartHashExits">8-char hashExits the loop anchors on (also doubles as endHashExits).</param>
/// <param name="Steps">Per-step parsed rows (left-to-right walk).</param>
public sealed record MpLoopFile(
    string Label,
    string Author,
    string Code4,
    string GroupName,
    string RoomName,
    string StartHashExits,
    IReadOnlyList<MpStep> Steps);

/// <summary>
/// One row from the <c>.mp</c> file's step section.
/// </summary>
/// <param name="HashExits">8-char hashExits of the room being left for this step.</param>
/// <param name="Compass">
/// Parsed compass direction when the step's direction column is one
/// of the canonical compass tokens (N/S/E/W/NE/NW/SE/SW/U/D);
/// <c>null</c> for non-compass commands such as <c>"go path"</c>,
/// <c>"climb wall"</c>, etc. that MegaMUD records verbatim when its
/// engine couldn't infer a compass move.
/// </param>
/// <param name="ActionText">
/// Original direction token from the file, preserved for diagnostics.
/// For compass moves this is the lowercase form (<c>"ne"</c>); for
/// non-compass commands it's the raw text (<c>"go path"</c>) so the
/// log line points at what MegaMUD wanted to send.
/// </param>
public sealed record MpStep(string HashExits, Direction? Compass, string ActionText)
{
    /// <summary>True when this step is a compass move; false for "go X" style commands.</summary>
    public bool IsCompass => Compass.HasValue;
}
