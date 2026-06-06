using FujinTerm.Models.Profile;

namespace FujinTerm.Game.Map;

/// <summary>
/// <see cref="ITravelCostModel"/> driven by the live
/// <see cref="PlayerState.Encumbrance"/> reading. Each query reads the
/// player's current encumbrance bucket, looks up that bucket's
/// seconds-per-hop in <see cref="EncumbranceHopTimes"/>, and converts
/// the supplied hop count to a <see cref="TimeSpan"/>.
/// </summary>
/// <remarks>
/// <para>
/// The lookup table itself is a snapshot owned by the model — the
/// caller (typically <see cref="Services.AppServices"/> applying
/// <see cref="AutoLairSettings"/>) writes a fresh table when the user
/// edits the Settings tab. The <see cref="PlayerState"/> reference is
/// long-lived; we don't subscribe to it because the scheduler asks for
/// fresh estimates on every tick, so polling on demand stays accurate.
/// </para>
/// </remarks>
public sealed class EncumbranceGatedTravelCostModel : ITravelCostModel
{
    private readonly PlayerState _state;

    /// <summary>Per-bucket seconds-per-hop. Replace via the property to apply a settings edit live.</summary>
    public EncumbranceHopTimes Times { get; set; }

    public EncumbranceGatedTravelCostModel(PlayerState state, EncumbranceHopTimes times)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(times);
        _state = state;
        Times = times;
    }

    public TimeSpan EstimateTravel(int hopCount)
    {
        if (hopCount <= 0) return TimeSpan.Zero;
        double seconds = Math.Max(0.1, Times.For(_state.Encumbrance));
        return TimeSpan.FromSeconds(hopCount * seconds);
    }
}
