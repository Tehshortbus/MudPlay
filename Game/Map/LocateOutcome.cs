namespace MudPlay.Game.Map;

// Why a walk stopped.
public enum LocateOutcomeKind
{
    // Exactly one room survived. Room carries it.
    Converged = 0,

    // Several rooms survived and walking cannot tell them apart.
    // CandidateCount carries how many: "one of 212" and "one of 2" are
    // different situations, and the user is entitled to know which.
    Ambiguous = 1,

    // No room in the graph matches. Walking cannot fix this — the world
    // loaded is not the world the character is standing in.
    Unknown = 2,
}

// Where a character turned out to be, and what it cost to find out. Steps
// is not decoration: it is how far the character was MOVED to answer the
// question, which the operator who asked is entitled to know.
public readonly record struct LocateOutcome(
    LocateOutcomeKind Kind,
    RoomKey Room,
    int CandidateCount,
    int Steps)
{
    public static LocateOutcome Converged(RoomKey room, int steps)
        => new(LocateOutcomeKind.Converged, room, 1, steps);

    public static LocateOutcome Ambiguous(int candidateCount, int steps)
        => new(LocateOutcomeKind.Ambiguous, default, candidateCount, steps);

    public static LocateOutcome Unknown(int steps)
        => new(LocateOutcomeKind.Unknown, default, 0, steps);
}
