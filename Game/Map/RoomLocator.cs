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

    public RoomLocator(RoomGraphManager graph, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
        _log = log;
    }

    // Every room consistent with one display. Exact (name, exit-set) first;
    // widen to the superset reading only if that admits nothing, since a
    // closed door or unsearched hidden exit drops a bit the graph still
    // carries — and "nowhere" is the wrong answer about a character that is
    // plainly somewhere.
    public IReadOnlyList<RoomKey> Seed(RoomObservation observation)
    {
        IReadOnlyList<RoomKey> exact = _graph.FindCandidates(observation.Name, observation.Exits);
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
}
