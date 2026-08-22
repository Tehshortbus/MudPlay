using MudPlay.Services;

namespace MudPlay.Game.Map;

// Answers "which rooms could this display be?" from the room graph, and
// picks the exit that best tells the survivors apart.
//
// Pure: it sends nothing, holds no state and owns no observable field.
// The narrowing itself is FootprintMatcher's job — this type only seeds
// the set and ranks directions.
public sealed class RoomLocator
{
    // Steps a walk may take before the room is called indistinguishable.
    // Past twelve the resolution curve is flat, and every step is a room
    // the character did not choose to be in.
    public const int DefaultBudget = 12;

    private readonly RoomGraphManager _graph;
    private readonly LogService? _log;

    // Own exact-bucket index, keyed on what a display would actually show —
    // NOT RoomGraphManager's (Name, full ExitMask) index, which is keyed on
    // every exit the graph carries, hidden and text ones included.
    // RoomTracker's tier-1 promotion depends on that full-mask index staying
    // exactly as it is, so this seeds from a private one instead of touching
    // it. Without this a room with a hidden or text exit is never a member
    // of its own exact bucket by its own displayed mask — and if some
    // unrelated room's full mask happens to match, Seed would return THAT
    // room and never fall through to the superset search that would have
    // found the true one. Rebuilt whenever the graph reloads.
    private readonly Dictionary<(string Name, uint DisplayedMask), List<RoomKey>> _byDisplayedExits = new();

    public RoomLocator(RoomGraphManager graph, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
        _log = log;
        _graph.GraphReloaded += RebuildDisplayedIndex;
        RebuildDisplayedIndex();
    }

    // Every room consistent with one display. Exact (name, displayed-mask)
    // first, against the index above; widen to the superset reading only if
    // that admits nothing, since a closed door or unsearched hidden exit
    // drops a bit the graph still carries — and "nowhere" is the wrong
    // answer about a character that is plainly somewhere.
    public IReadOnlyList<RoomKey> Seed(RoomObservation observation)
    {
        IReadOnlyList<RoomKey> exact = LookupDisplayed(observation.Name, observation.Exits);
        if (exact.Count > 0) return exact;

        IReadOnlyList<RoomKey> wide = _graph.FindByNameCoveringExits(observation.Name, observation.Exits);
        if (wide.Count > 0 && _log?.IsDebugEnabled == true)
            _log.Debug("RoomLocator",
                $"Seed('{observation.Name}'): exact empty, superset gave {wide.Count}.");
        return wide;
    }

    // The listed exit that splits the candidate set furthest.
    //
    // Every candidate must have the exit and the graph must know where it
    // leads — otherwise taking it presupposes which candidate we are, which
    // is the question being asked. Among the usable ones, best is whichever
    // reaches the most different-LOOKING rooms, since only a difference the
    // board can show is evidence.
    //
    // A direction that splits nothing is still worth taking: it carries the
    // whole set forward to a room where the neighbours do differ. Ties go to
    // the first direction in compass order so a walk is reproducible.
    //
    // Null when no listed exit is usable at all — the walk should stop.
    public Direction? ChooseSplittingExit(IReadOnlyCollection<RoomKey> candidates, RoomObservation here)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0) return null;

        Direction? best = null;
        int bestShapes = 0;

        // 0..9 only: Teleport (10) is synthesized and never a listed exit.
        for (int i = 0; i <= (int)Direction.D; i++)
        {
            var dir = (Direction)i;
            if (!here.Exits.Contains(dir)) continue;

            var shapes = new HashSet<(string Name, uint ExitMask)>();
            bool usable = true;

            foreach (RoomKey candidate in candidates)
            {
                Room? source = _graph.GetRoom(candidate);
                if (source is null || !source.Exits.TryGetValue(dir, out RoomExit exit))
                {
                    usable = false;
                    break;
                }
                Room? destination = _graph.GetRoom(exit.Target);
                if (destination is null)
                {
                    usable = false;
                    break;
                }
                shapes.Add((destination.Name, destination.ExitMask));
            }

            if (!usable) continue;
            if (best is null || shapes.Count > bestShapes)
            {
                best = dir;
                bestShapes = shapes.Count;
            }
        }

        return best;
    }

    private IReadOnlyList<RoomKey> LookupDisplayed(string name, IReadOnlySet<Direction> exits)
    {
        if (string.IsNullOrEmpty(name)) return Array.Empty<RoomKey>();
        uint mask = 0;
        foreach (Direction d in exits) mask |= 1u << (int)d;
        return _byDisplayedExits.TryGetValue((name, mask), out List<RoomKey>? keys)
            ? keys
            : Array.Empty<RoomKey>();
    }

    private void RebuildDisplayedIndex()
    {
        _byDisplayedExits.Clear();
        foreach (Room room in _graph.Rooms)
        {
            var key = (room.Name, DisplayedMask(room));
            if (!_byDisplayedExits.TryGetValue(key, out List<RoomKey>? bucket))
                _byDisplayedExits[key] = bucket = new List<RoomKey>();
            bucket.Add(room.Key);
        }
    }

    // What "Obvious exits:" actually prints for room, not every exit the row
    // carries: SearchableHidden and MultiActionHidden don't appear on that
    // line at all, and a Text exit crosses via a typed command rather than
    // showing as a listed compass direction. 0..9 only — Teleport (10) is
    // synthesized and never a row exit to begin with.
    private static uint DisplayedMask(Room room)
    {
        uint mask = 0;
        for (int i = 0; i <= (int)Direction.D; i++)
        {
            var dir = (Direction)i;
            if (!room.Exits.TryGetValue(dir, out RoomExit exit)) continue;
            if (exit.Hint is RoomExitHint.SearchableHidden or RoomExitHint.MultiActionHidden or RoomExitHint.Text)
                continue;
            mask |= 1u << i;
        }
        return mask;
    }
}
