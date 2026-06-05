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
/// <param name="Direction">Movement direction (lowercase in the file; normalised here).</param>
public sealed record MpStep(string HashExits, Direction Direction);
