namespace FujinTerm.Game.Map;

/// <summary>
/// Terminal outcome of a <see cref="HiddenExitRevealManager"/>
/// request. <see cref="Revealed"/> means the hidden exit now appears
/// in the tracker's current room and the walker can send the
/// cardinal move; <see cref="Failed"/> carries a single-line reason
/// (search attempts exhausted, exit never revealed, etc.).
/// </summary>
public abstract record HiddenSearchResult
{
    /// <summary>Hidden exit is now visible — walker can proceed with the cardinal step.</summary>
    public sealed record Revealed : HiddenSearchResult
    {
        public static readonly Revealed Instance = new();
        private Revealed() { }
    }

    /// <summary>Exit couldn't be revealed within the configured cap.</summary>
    public sealed record Failed(string Reason) : HiddenSearchResult;
}
