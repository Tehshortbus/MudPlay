namespace FujinTerm.Game.Map;

/// <summary>
/// Terminal outcome of a <see cref="DoorOpenManager"/> request.
/// <see cref="Opened"/> means the walker can now safely send the
/// cardinal move command; <see cref="Failed"/> carries a single-line
/// reason the walker surfaces in its <see cref="WalkEventKind.Failed"/>
/// event and the system log.
/// </summary>
public abstract record DoorOpenResult
{
    /// <summary>Door is open and ready for the cardinal move.</summary>
    public sealed record Opened : DoorOpenResult
    {
        public static readonly Opened Instance = new();
        private Opened() { }
    }

    /// <summary>
    /// Door couldn't be opened — bash exhausted, pick exhausted, key
    /// required but unavailable, or an unknown server reply broke the
    /// FSM.
    /// </summary>
    /// <param name="Reason">Short user-facing failure detail.</param>
    public sealed record Failed(string Reason) : DoorOpenResult;
}
