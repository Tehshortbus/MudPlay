using System.Collections.Generic;

namespace MudPlay.Game.Map.MpFile;

// Everything a MegaMUD .mp path file says, decoded but not yet resolved against our
// map: the file carries no (map, room), only per-step hashExits tokens, so
// MpFileImporter walks the steps through the graph to find the actual rooms.
//
// Label is the first bracket on line 1; Author the second (a second [label][author]
// line, which some files carry, fills an empty author). Start / End are the header
// room lines ([code:group:name]); a single-header file uses the start for both.
// Use is the metadata field after the step count (always -1 in the files seen; not
// named in MegaMUD's help); Gold, RequiredItem, FailPath and SuccessPath are the
// path-details fields MegaMUD's help documents. Problems lists anything malformed
// that didn't stop the decode — the review window shows it rather than refusing
// the file.
public sealed record MpLoopFile(
    string Label,
    string Author,
    MpHeaderRoom Start,
    MpHeaderRoom End,
    string StartHashExits,
    string EndHashExits,
    int DeclaredStepCount,
    string Use,
    int Gold,
    string RequiredItem,
    string FailPath,
    string SuccessPath,
    IReadOnlyList<MpStep> Steps,
    IReadOnlyList<string> Problems)
{
    // A loop starts and ends in the same room; a goto path doesn't.
    public bool IsLoop => string.Equals(StartHashExits, EndHashExits, StringComparison.OrdinalIgnoreCase);
}

// A header room line: [code:group:name], the Rooms.md code, folder and name.
public sealed record MpHeaderRoom(string Code, string Group, string Name);

// One step row: "hashExits:flags:action". HashExits is the 8-char hash of the room
// being left; Flags the per-step options (MpStepFlags); RawAction the action column
// verbatim. Compass is set when the action is a compass move, optionally followed by
// MegaMUD's bracketed extra commands ("s[search s]", "e[use black star key e]") in
// PreActions, or by a " -- note" some editors append. Anything else ("go path",
// "pull lever", "@party go portal") is a Command sent as-is.
public sealed record MpStep(
    string HashExits,
    MpStepFlags Flags,
    string RawAction,
    Direction? Compass,
    IReadOnlyList<string> PreActions,
    string? Command,
    string? Note);

// MegaMUD's per-step options, from its Path Steps help and the STEPF_ constants:
// Dark room, Rest up here, Don't rest here, Pitch black, Stash point, Don't attack,
// Re-learn room, Disarm trap, Pick lock. Suspect and Cycled are MegaMUD's own
// bookkeeping.
[Flags]
public enum MpStepFlags
{
    None       = 0,
    Dark       = 0x0001,
    RestHere   = 0x0002,
    DontRest   = 0x0004,
    PitchBlack = 0x0008,
    Stash      = 0x0010,
    NoAttack   = 0x0040,
    Relearn    = 0x0080,
    Suspect    = 0x0100,
    Disarm     = 0x0200,
    CanPick    = 0x0400,
    Cycled     = 0x8000,
}
