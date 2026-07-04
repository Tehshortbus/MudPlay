using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FujinTerm.Services;

namespace FujinTerm.Game.Map.MpFile;

// Resolves a parsed MpLoopFile against the active RoomGraphManager and produces a
// ready-to-save Loop.
//
// The .mp format doesn't carry (map, room); it carries per-step hashExits tokens.
// Anchoring is therefore a two-stage process:
//   1. Candidate filter — decode the start hashExits into a (nameHash, exitSet)
//      and collect every room in the active graph that matches both. Multiple
//      matches are common because the 3-char hash is lossy.
//   2. Closure walk + per-step scoring — for each candidate, walk the recorded
//      direction sequence through the graph, verifying the per-step hashExits
//      matches what our graph produces for the room we're standing in
//      (informational), and verifying the final position equals the start room
//      (mandatory: a loop file must close). Candidates that fail to close are
//      discarded.
//
// Result: one MpImportResolution with the surviving candidates ranked by per-step
// mismatch count (fewer = better). The caller (UI) picks the unique best, prompts
// the user when several candidates tie for the best score, or surfaces the error
// reason when no candidate closes.
public sealed partial class MpFileImporter
{
    private readonly RoomGraphManager _graph;
    private readonly LogService? _log;

    public MpFileImporter(RoomGraphManager graph, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
        _log = log;
    }

    // Run anchor resolution for file against the active graph. Doesn't mutate any
    // state — the caller decides what to do with the result (open the editor, pop a
    // picker dialog, surface an error).
    public MpImportResolution Resolve(MpLoopFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        // Sanity-check the token shape before touching the graph.
        (string? nameHash, string? exitsCode) = MegaMudHash.Split(file.StartHashExits);
        if (nameHash is null || exitsCode is null)
        {
            return MpImportResolution.Fail(
                $"start hashExits '{file.StartHashExits}' isn't a parseable 8-char token");
        }

        // Filter: rooms whose full computed hashExits (door- and
        // hidden-aware) matches the file's startHashExits exactly.
        // Encoding doors ×2 and excluding hidden exits the same way
        // MegaMUD's calcMegaMUDExitsCode does is what makes the
        // 8-char compare actually agree with rooms.md.
        List<RoomKey> candidates = new();
        foreach (Room room in _graph.Rooms)
        {
            string roomHash = MegaMudHash.ComputeHashExits(room);
            if (string.Equals(roomHash, file.StartHashExits, StringComparison.OrdinalIgnoreCase))
                candidates.Add(room.Key);
        }

        _log?.Info("MpImporter",
            $"Resolve: label='{file.Label}' startHash={file.StartHashExits} → {candidates.Count} candidate(s)");

        if (candidates.Count == 0)
        {
            return MpImportResolution.Fail(
                $"no rooms in the active BBS graph produce hashExits {file.StartHashExits} "
              + $"(name hash {nameHash} + exits {exitsCode}). "
              + "Most likely cause: the .mp file was built against a different game-data set.");
        }

        // Closure walk + per-step scoring for each candidate. Walks
        // that fail log a Debug line per candidate with the step
        // number + reason so a stale-graph diagnosis is one log scan
        // away.
        List<MpImportCandidate> scored = new();
        List<string> walkFailures = new();
        foreach (RoomKey start in candidates)
        {
            (MpImportCandidate? ok, string? failReason) = WalkCandidate(file, start);
            if (ok is not null) { scored.Add(ok); continue; }
            string reason = failReason ?? "unknown";
            walkFailures.Add($"{start}: {reason}");
            _log?.Log(LogSeverity.Debug, "MpImporter",
                $"candidate {start} rejected: {reason}");
        }

        if (scored.Count == 0)
        {
            string detail = walkFailures.Count > 0
                ? " First failure: " + walkFailures[0]
                : string.Empty;
            return MpImportResolution.Fail(
                $"matched {candidates.Count} candidate room(s) on hash+exits but none closed the loop "
              + "(each candidate either hit a missing exit mid-walk or didn't land back at the start). "
              + "The .mp file was likely built against a different game-data set."
              + detail);
        }

        // Best = fewest per-step mismatches. Ties (same mismatch
        // count) all get returned so the UI can prompt the user.
        scored.Sort((a, b) =>
        {
            int c = a.HashMismatches.CompareTo(b.HashMismatches);
            if (c != 0) return c;
            // Stable secondary sort by RoomKey so order is
            // deterministic across runs.
            int m = a.AnchorKey.Map.CompareTo(b.AnchorKey.Map);
            return m != 0 ? m : a.AnchorKey.Room.CompareTo(b.AnchorKey.Room);
        });
        int bestScore = scored[0].HashMismatches;
        List<MpImportCandidate> best = scored
            .TakeWhile(c => c.HashMismatches == bestScore)
            .ToList();

        _log?.Info("MpImporter",
            $"Resolve: {scored.Count} closed-loop candidate(s); {best.Count} tied at best score (mismatches={bestScore})");

        return MpImportResolution.Success(file, best, scored.Count - best.Count);
    }

    // Assemble the persisted Loop from a chosen anchor + the parsed file. Strips
    // the trailing "-mapNum roomNum" hint from the label per the common naming
    // convention. Returns null when the walk doesn't actually close (defence in
    // depth — the caller should already have filtered).
    public Loop? BuildLoop(MpLoopFile file, RoomKey anchor)
    {
        ArgumentNullException.ThrowIfNull(file);

        (MpImportCandidate? walk, _) = WalkCandidate(file, anchor);
        if (walk is null) return null;

        // Every visited room becomes a waypoint — "faithful" import.
        // LoopExpander resolves each leg as a single-step BFS at
        // runtime so the runtime path matches the .mp exactly.
        List<LoopWaypoint> waypoints = walk.Visited
            .Select(k => new LoopWaypoint(k))
            .ToList();

        string cleanName = StripMapRoomSuffix(file.Label);
        if (string.IsNullOrWhiteSpace(cleanName))
            cleanName = $"Imported loop {DateTime.Now:HH-mm-ss}";

        Loop loop = new(cleanName, waypoints)
        {
            Notes = string.IsNullOrWhiteSpace(file.Author)
                ? $"Imported from .mp ({file.GroupName}/{file.Code4})"
                : $"Imported from .mp by {file.Author} ({file.GroupName}/{file.Code4})",
        };
        return loop;
    }

    // Walk file's step sequence from start, counting per-step hash mismatches.
    // Returns (candidate, null) when the walk closes; returns (null, reason) with a
    // human-readable step-level reason when it doesn't — the importer surfaces this
    // to the user when every candidate fails so they can spot whether it was a
    // missing compass exit, an unresolvable "go X" target, or a non-closure.
    private (MpImportCandidate? Candidate, string? FailureReason)
        WalkCandidate(MpLoopFile file, RoomKey start)
    {
        if (_graph.GetRoom(start) is not { } startRoom)
            return (null, "start room not in graph (data swap mid-import?)");

        List<RoomKey> visited = new(file.Steps.Count + 1) { start };
        int mismatches = 0;
        RoomKey cursor = start;
        Room cursorRoom = startRoom;

        for (int i = 0; i < file.Steps.Count; i++)
        {
            MpStep step = file.Steps[i];

            // Per-step hash compare (soft signal). Uses the door-aware
            // overload so rooms with doors are encoded exactly the
            // way MegaMUD's rooms.md did.
            string expectedHash = MegaMudHash.ComputeHashExits(cursorRoom);
            if (!string.Equals(expectedHash, step.HashExits, StringComparison.OrdinalIgnoreCase))
                mismatches++;

            // Destination of this step =
            //   - next step's source hashExits when there is one
            //   - the loop's startHashExits when this is the final step
            string destHash = i + 1 < file.Steps.Count
                ? file.Steps[i + 1].HashExits
                : file.StartHashExits;

            RoomKey dest;
            if (step.Compass is { } compass)
            {
                if (!cursorRoom.Exits.TryGetValue(compass, out RoomExit exit))
                {
                    LogNeighbourSnapshot(cursor, cursorRoom, destHash, step);
                    return (null,
                        $"step {i + 1}: room {cursor} ('{cursorRoom.Name}') has no {compass} exit "
                      + "(graph data is missing this transition — see Info log for the room's actual neighbours)");
                }
                dest = exit.Target;
            }
            else
            {
                // Non-compass action ("go path", "climb wall", etc.).
                // Pick the neighbour whose door-aware hashExits matches
                // the next step's source. MegaMUD records the verb
                // text because its engine has to type it; our walker
                // reads room metadata at run time, so the verb is
                // informational — the next-step hash is the
                // authoritative target.
                //
                // Two-pass match (strict → tolerant):
                //   1. Exact 8-char hashExits compare. When this
                //      hits the candidate is definitively the right
                //      room — no fuzz.
                //   2. When no neighbour matches exactly, fall back
                //      to the 3-char name-hash alone. The exits
                //      portion of the recorded hash drifts whenever
                //      a room's exit set differs from what MegaMUD
                //      had at .mp-build time (a single exit added or
                //      removed flips the 5-char code) — but the
                //      name hash is far more stable. A unique
                //      name-hash hit among the neighbours is almost
                //      always the right room; the per-step
                //      mismatch counter ticks so the candidate is
                //      ranked lower than an exact-walk peer.
                (string? destNameHash, _) = MegaMudHash.Split(destHash);
                RoomKey? matchedExact = null;
                RoomKey? matchedByName = null;
                int nameMatchCount = 0;
                foreach (RoomExit candidateExit in cursorRoom.Exits.Values)
                {
                    if (_graph.GetRoom(candidateExit.Target) is not { } cand) continue;
                    string candHash = MegaMudHash.ComputeHashExits(cand);
                    if (string.Equals(candHash, destHash, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedExact = candidateExit.Target;
                        break;
                    }
                    if (destNameHash is not null)
                    {
                        string candNameHash = MegaMudHash.ComputeNameHash(cand.Name);
                        if (string.Equals(candNameHash, destNameHash, StringComparison.OrdinalIgnoreCase))
                        {
                            matchedByName = candidateExit.Target;
                            nameMatchCount++;
                        }
                    }
                }

                if (matchedExact is { } exactPick)
                {
                    dest = exactPick;
                }
                else if (nameMatchCount == 1 && matchedByName is { } namePick)
                {
                    _log?.Log(LogSeverity.Info, "MpImporter",
                        $"step {i + 1}: exact hashExits {destHash} not on any neighbour of {cursor}; "
                      + $"falling back to unique name-hash match → {namePick} "
                      + $"('{_graph.GetRoom(namePick)?.Name}'). Exit-set drift is the likely cause.");
                    dest = namePick;
                    mismatches++;
                }
                else
                {
                    LogNeighbourSnapshot(cursor, cursorRoom, destHash, step);
                    string nameHashHint = destNameHash is null
                        ? string.Empty
                        : nameMatchCount > 1
                            ? $" ({nameMatchCount} neighbours matched on name-hash {destNameHash} alone — too ambiguous to fall back)"
                            : string.Empty;
                    return (null,
                        $"step {i + 1}: room {cursor} ('{cursorRoom.Name}') has no neighbour matching "
                      + $"hashExits {destHash} for non-compass action '{step.ActionText}'"
                      + nameHashHint
                      + " (see Info log for the room's actual neighbours)");
                }
            }

            cursor = dest;
            if (_graph.GetRoom(cursor) is not { } nextRoom)
                return (null, $"step {i + 1}: graph lookup failed for {cursor}");
            cursorRoom = nextRoom;
            visited.Add(cursor);
        }

        // For a closed loop the final position must equal the start.
        if (!cursor.Equals(start))
            return (null,
                $"walked all {file.Steps.Count} steps but landed at {cursor} ('{cursorRoom.Name}'), not back at start {start}");

        visited.RemoveAt(visited.Count - 1);
        return (new MpImportCandidate(start, visited, mismatches), null);
    }

    // Log every outgoing exit on room with its target's computed hashExits so a
    // user staring at a failed non-compass step can compare their graph against
    // what MegaMUD's .mp expects. Logged at Info (not Debug) because it's the one
    // diagnostic that actually fingers the data gap — without it the user can't
    // tell whether the room is missing the action exit entirely OR has it pointed
    // at a different room than MegaMUD's record.
    private void LogNeighbourSnapshot(RoomKey from, Room room, string expectedHash, MpStep step)
    {
        if (_log is null) return;
        _log.Log(LogSeverity.Info, "MpImporter",
            $"non-compass walk failed at {from} ('{room.Name}'): expected neighbour hashExits {expectedHash} "
          + $"for action '{step.ActionText}'. Actual neighbours:");
        foreach (KeyValuePair<Direction, RoomExit> kv in room.Exits)
        {
            string targetSummary;
            if (_graph.GetRoom(kv.Value.Target) is { } target)
            {
                string targetHash = MegaMudHash.ComputeHashExits(target);
                targetSummary = $"{kv.Value.Target} ('{target.Name}') hash={targetHash}";
            }
            else
            {
                targetSummary = $"{kv.Value.Target} (not in graph)";
            }
            _log.Log(LogSeverity.Info, "MpImporter",
                $"  {kv.Key,-2} → {targetSummary} hint={kv.Value.Hint}");
        }
    }

    // Strip a trailing "-mapNum roomNum" (or similar all-digit) suffix that the
    // common room-naming convention appends to labels and room names. Leaves the
    // head alone when no suffix is present.
    internal static string StripMapRoomSuffix(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string trimmed = raw.Trim();
        Match m = SuffixRegex().Match(trimmed);
        if (!m.Success) return trimmed;
        return trimmed[..m.Index].TrimEnd();
    }

    // Matches "-N M" or "-N" at end of string. N and M are positive
    // integers. The dash is required so we don't strip legitimate
    // trailing numbers ("Crypt Level 1" → don't strip the "1").
    [GeneratedRegex(@"-\s*\d+(?:\s+\d+)?\s*$", RegexOptions.Compiled)]
    private static partial Regex SuffixRegex();
}

// One survivable anchor for an .mp loop import: the chosen start room (AnchorKey),
// the full ordered list of rooms walked from there (Visited, length == file step
// count), and the count of per-step hash mismatches discovered along the way
// (HashMismatches — a soft "graph drift" signal, 0 = exact match throughout, lower
// is better).
public sealed record MpImportCandidate(
    RoomKey AnchorKey,
    IReadOnlyList<RoomKey> Visited,
    int HashMismatches);

// Result envelope from MpFileImporter.Resolve. Either carries one-or-more
// candidates the UI can act on or an error reason to surface to the user.
public sealed class MpImportResolution
{
    private MpImportResolution(MpLoopFile? file, IReadOnlyList<MpImportCandidate>? best,
        int dropped, string? error)
    {
        File = file;
        BestCandidates = best ?? Array.Empty<MpImportCandidate>();
        DroppedCandidateCount = dropped;
        Error = error;
    }

    // Parsed input file. Null when the resolution failed before walking.
    public MpLoopFile? File { get; }

    // Candidates that closed AND tied for the lowest per-step mismatch count.
    // Singleton when the importer found a unique best; multi-element when the UI
    // must prompt the user.
    public IReadOnlyList<MpImportCandidate> BestCandidates { get; }

    // Candidates that closed but lost on per-step score (i.e. the importer found a
    // uniquely better match elsewhere). Exposed for diagnostics.
    public int DroppedCandidateCount { get; }

    // Human-readable reason when BestCandidates is empty.
    public string? Error { get; }

    public bool HasUniqueBest => Error is null && BestCandidates.Count == 1;
    public bool NeedsUserPick => Error is null && BestCandidates.Count > 1;
    public bool Failed        => Error is not null;

    public static MpImportResolution Success(MpLoopFile file, IReadOnlyList<MpImportCandidate> best, int dropped)
        => new(file, best, dropped, error: null);

    public static MpImportResolution Fail(string error)
        => new(file: null, best: null, dropped: 0, error: error);
}
