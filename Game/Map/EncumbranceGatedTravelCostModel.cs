using MudPlay.Models.Profile;

namespace MudPlay.Game.Map;

// ITravelCostModel driven by the live PlayerState.Encumbrance reading.
// Each query reads the player's current encumbrance bucket, looks up that
// bucket's seconds-per-hop in EncumbranceHopTimes, and converts the
// supplied hop count to a TimeSpan.
//
// The lookup table is a snapshot owned by the model — a settings edit builds a
// fresh model (AppServices applying AutoLairSettings). The PlayerState reference is
// long-lived; we don't subscribe to it because the scheduler asks for
// fresh estimates on every tick, so polling on demand stays accurate.
public sealed class EncumbranceGatedTravelCostModel : ITravelCostModel
{
    private readonly PlayerState _state;

    // Per-bucket seconds-per-hop.
    public EncumbranceHopTimes Times { get; }

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
