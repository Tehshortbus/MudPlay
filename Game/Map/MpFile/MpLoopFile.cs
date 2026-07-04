using System.Collections.Generic;

namespace FujinTerm.Game.Map.MpFile;

// Parsed-but-unresolved representation of a single .mp loop file. Geometry only:
// no RoomKeys are assigned at this stage because the file doesn't carry
// coordinates — MpFileImporter resolves the start anchor against the active
// RoomGraphManager and walks the steps to fill in the actual room sequence.
//
// Label is the first bracket on line 1 (the loop's display label); Author is the
// second bracket on line 1, or empty; Code4 is the 4-char rooms.md room code from
// the second header line; GroupName is the rooms.md folder/group name; RoomName is
// the user-facing room name; StartHashExits is the 8-char hashExits the loop
// anchors on (also doubles as endHashExits); Steps are the per-step parsed rows
// (left-to-right walk).
public sealed record MpLoopFile(
    string Label,
    string Author,
    string Code4,
    string GroupName,
    string RoomName,
    string StartHashExits,
    IReadOnlyList<MpStep> Steps);

// One row from the .mp file's step section. HashExits is the 8-char hashExits of
// the room being left for this step. Compass is the parsed compass direction when
// the step's direction column is one of the canonical compass tokens
// (N/S/E/W/NE/NW/SE/SW/U/D); null for non-compass commands such as "go path",
// "climb wall", etc. that MegaMUD records verbatim when its engine couldn't infer
// a compass move. ActionText is the original direction token from the file,
// preserved for diagnostics — for compass moves the lowercase form ("ne"); for
// non-compass commands the raw text ("go path") so the log line points at what
// MegaMUD wanted to send.
public sealed record MpStep(string HashExits, Direction? Compass, string ActionText)
{
    // True when this step is a compass move; false for "go X" style commands.
    public bool IsCompass => Compass.HasValue;
}
