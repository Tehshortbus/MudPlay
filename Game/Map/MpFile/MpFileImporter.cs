using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FujinTerm.Services;

namespace FujinTerm.Game.Map.MpFile;

/// <summary>
/// Resolves a parsed <see cref="MpLoopFile"/> against the active
/// <see cref="RoomGraphManager"/> and produces a ready-to-save
/// <see cref="Loop"/>. Heart of the .mp import flow described in the
/// Phase 7 plan (PR 7.9).
/// </summary>
/// <remarks>
/// <para>
/// The <c>.mp</c> format doesn't carry <c>(map, room)</c>; it carries
/// per-step <c>hashExits</c> tokens. Anchoring is therefore a two-stage
/// process:
/// </para>
/// <list type="number">
///   <item>
///     <b>Candidate filter</b> — decode the start hashExits into a
///     <c>(nameHash, exitSet)</c> and collect every room in the active
///     graph that matches both. Multiple matches are common because
///     the 3-char hash is lossy.
///   </item>
///   <item>
///     <b>Closure walk + per-step scoring</b> — for each candidate,
///     walk the recorded direction sequence through the graph,
///     verifying the per-step hashExits matches what our graph
///     produces for the room we're standing in (informational), and
///     verifying the final position equals the start room (mandatory:
///     a loop file must close). Candidates that fail to close are
///     discarded.
///   </item>
/// </list>
/// <para>
/// Result: one <see cref="MpImportResolution"/> with the surviving
/// candidates ranked by per-step mismatch count (fewer = better). The
/// caller (UI) picks the unique best, prompts the user when several
/// candidates tie for the best score, or surfaces the error reason
/// when no candidate closes.
/// </para>
/// </remarks>
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

    /// <summary>
    /// Run anchor resolution for <paramref name="file"/> against the
    /// active graph. Doesn't mutate any state — the caller decides
    /// what to do with the result (open the editor, pop a picker
    /// dialog, surface an error).
    /// </summary>
    public MpImportResolution Resolve(MpLoopFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        (string? nameHash, string? exitsCode) = MegaMudHash.Split(file.StartHashExits);
        if (nameHash is null || exitsCode is null)
        {
            return MpImportResolution.Fail(
                $"start hashExits '{file.StartHashExits}' isn't a parseable 8-char token");
        }

        IReadOnlySet<Direction>? wantedExits = MegaMudHash.DecodeExits(exitsCode);
        if (wantedExits is null)
        {
            return MpImportResolution.Fail(
                $"start exits code '{exitsCode}' doesn't decode into a known exit set");
        }

        // Filter: all rooms whose computed name hash matches AND
        // whose exit set decodes to the same shape. We don't trust
        // the user's `-mapNum roomNum` label suffix per the user's
        // direction; hash + exits is the only anchor.
        List<RoomKey> candidates = new();
        foreach (Room room in _graph.Rooms)
        {
            string rh = MegaMudHash.ComputeNameHash(room.Name);
            if (!string.Equals(rh, nameHash, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!ExitsMatch(room, wantedExits))
                continue;
            candidates.Add(room.Key);
        }

        _log?.Info("MpImporter",
            $"Resolve: label='{file.Label}' startHash={file.StartHashExits} → {candidates.Count} candidate(s)");

        if (candidates.Count == 0)
        {
            return MpImportResolution.Fail(
                $"no rooms in the active BBS graph match the .mp's start hash {nameHash} + exits {exitsCode}. "
              + "Most likely cause: the .mp file was built against a different game-data set.");
        }

        // Closure walk + per-step scoring for each candidate.
        List<MpImportCandidate> scored = new();
        foreach (RoomKey start in candidates)
        {
            MpImportCandidate? scoredCandidate = WalkCandidate(file, start);
            if (scoredCandidate is not null) scored.Add(scoredCandidate);
        }

        if (scored.Count == 0)
        {
            return MpImportResolution.Fail(
                $"matched {candidates.Count} candidate room(s) on hash+exits but none closed the loop "
              + "(each candidate either hit a missing exit mid-walk or didn't land back at the start). "
              + "The .mp file was likely built against a different game-data set.");
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

    /// <summary>
    /// Assemble the persisted <see cref="Loop"/> from a chosen
    /// anchor + the parsed file. Strips the trailing
    /// <c>-mapNum roomNum</c> hint from the label per the user's
    /// convention. Returns null when the walk doesn't actually close
    /// (defence in depth — the caller should already have filtered).
    /// </summary>
    public Loop? BuildLoop(MpLoopFile file, RoomKey anchor)
    {
        ArgumentNullException.ThrowIfNull(file);

        MpImportCandidate? walk = WalkCandidate(file, anchor);
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

    /// <summary>
    /// Walk <paramref name="file"/>'s step sequence from
    /// <paramref name="start"/>, counting per-step hash mismatches.
    /// Returns null when the walk hits an unresolvable exit OR fails
    /// to close on the start room.
    /// </summary>
    private MpImportCandidate? WalkCandidate(MpLoopFile file, RoomKey start)
    {
        if (_graph.GetRoom(start) is not { } startRoom) return null;

        List<RoomKey> visited = new(file.Steps.Count + 1) { start };
        int mismatches = 0;
        RoomKey cursor = start;
        Room cursorRoom = startRoom;

        for (int i = 0; i < file.Steps.Count; i++)
        {
            MpStep step = file.Steps[i];

            // Per-step hash compare (soft signal).
            string expected = MegaMudHash.ComputeHashExits(cursorRoom.Name,
                ExitMaskToSet(cursorRoom.ExitMask));
            if (!string.Equals(expected, step.HashExits, StringComparison.OrdinalIgnoreCase))
                mismatches++;

            // Destination of this step =
            //   - next step's source hashExits when there is one
            //   - the loop's startHashExits when this is the final step
            // We use it to disambiguate non-compass "go X" actions
            // (which our graph doesn't key by Direction).
            string destHash = i + 1 < file.Steps.Count
                ? file.Steps[i + 1].HashExits
                : file.StartHashExits;

            RoomKey dest;
            if (step.Compass is { } compass)
            {
                if (!cursorRoom.Exits.TryGetValue(compass, out RoomExit exit))
                    return null;
                dest = exit.Target;
            }
            else
            {
                // Non-compass action ("go path", "climb wall", etc.).
                // Pick the neighbour whose computed hashExits matches
                // the next step's source. MegaMUD records the verb
                // text because its engine needs to type it; our
                // walker reads room metadata and figures out the
                // command at run time, so we just need to walk through
                // the right exit.
                RoomKey? matched = null;
                foreach (RoomExit candidateExit in cursorRoom.Exits.Values)
                {
                    if (_graph.GetRoom(candidateExit.Target) is not { } cand) continue;
                    string candHash = MegaMudHash.ComputeHashExits(cand.Name,
                        ExitMaskToSet(cand.ExitMask));
                    if (!string.Equals(candHash, destHash, StringComparison.OrdinalIgnoreCase))
                        continue;
                    matched = candidateExit.Target;
                    break;
                }
                if (matched is null) return null;
                dest = matched.Value;
            }

            cursor = dest;
            if (_graph.GetRoom(cursor) is not { } nextRoom) return null;
            cursorRoom = nextRoom;
            visited.Add(cursor);
        }

        // The very last visited room is where step N landed. For a
        // closed loop that must equal the start. Drop the duplicate
        // tail so the waypoint list reads [W0, W1, …, W_{N-1}] (the
        // wrap is implicit in Loop's cycle semantics).
        if (!cursor.Equals(start)) return null;
        visited.RemoveAt(visited.Count - 1);

        return new MpImportCandidate(start, visited, mismatches);
    }

    private static bool ExitsMatch(Room room, IReadOnlySet<Direction> wanted)
    {
        if (room.Exits.Count != wanted.Count) return false;
        foreach (Direction d in wanted)
            if (!room.Exits.ContainsKey(d)) return false;
        return true;
    }

    private static IReadOnlySet<Direction> ExitMaskToSet(uint mask)
    {
        HashSet<Direction> set = new();
        for (int i = 0; i < 10; i++)
            if (((mask >> i) & 1u) != 0)
                set.Add((Direction)i);
        return set;
    }

    /// <summary>
    /// Strip a trailing <c>-mapNum roomNum</c> (or similar all-digit)
    /// suffix that the user's Room Definer V7.1 convention appends to
    /// labels and room names. Leaves the head alone when no suffix is
    /// present.
    /// </summary>
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

/// <summary>
/// One survivable anchor for an .mp loop import: the chosen start
/// room, the full ordered list of rooms walked from there, and the
/// count of per-step hash mismatches discovered along the way (a soft
/// "graph drift" signal — lower is better).
/// </summary>
/// <param name="AnchorKey">The candidate's start room.</param>
/// <param name="Visited">Every distinct room visited in order, length == file step count.</param>
/// <param name="HashMismatches">Per-step hash compares that didn't agree (0 = exact match throughout).</param>
public sealed record MpImportCandidate(
    RoomKey AnchorKey,
    IReadOnlyList<RoomKey> Visited,
    int HashMismatches);

/// <summary>
/// Result envelope from <see cref="MpFileImporter.Resolve"/>. Either
/// carries one-or-more candidates the UI can act on or an error
/// reason to surface to the user.
/// </summary>
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

    /// <summary>Parsed input file. Null when the resolution failed before walking.</summary>
    public MpLoopFile? File { get; }

    /// <summary>
    /// Candidates that closed AND tied for the lowest per-step
    /// mismatch count. Singleton when the importer found a unique
    /// best; multi-element when the UI must prompt the user.
    /// </summary>
    public IReadOnlyList<MpImportCandidate> BestCandidates { get; }

    /// <summary>
    /// Candidates that closed but lost on per-step score (i.e. the
    /// importer found a uniquely better match elsewhere). Exposed
    /// for diagnostics.
    /// </summary>
    public int DroppedCandidateCount { get; }

    /// <summary>Human-readable reason when <see cref="BestCandidates"/> is empty.</summary>
    public string? Error { get; }

    public bool HasUniqueBest => Error is null && BestCandidates.Count == 1;
    public bool NeedsUserPick => Error is null && BestCandidates.Count > 1;
    public bool Failed        => Error is not null;

    public static MpImportResolution Success(MpLoopFile file, IReadOnlyList<MpImportCandidate> best, int dropped)
        => new(file, best, dropped, error: null);

    public static MpImportResolution Fail(string error)
        => new(file: null, best: null, dropped: 0, error: error);
}
