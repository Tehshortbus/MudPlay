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
}
