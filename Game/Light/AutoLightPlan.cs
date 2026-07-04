namespace FujinTerm.Game.Light;

/// <summary>What the auto-light engine should do about the route ahead.</summary>
public enum AutoLightAction
{
    /// <summary>Route is lit and any readied light is healthy — do nothing.</summary>
    None,

    /// <summary>Ready a light already in the pack — the engine sends
    /// <c>use &lt;LightName&gt;</c>. No shop trip needed.</summary>
    Ready,

    /// <summary>Buy <see cref="AutoLightPlan.BuyCount"/> of
    /// <see cref="AutoLightPlan.LightName"/> (a shop detour), then ready one.</summary>
    Buy,

    /// <summary>Preemptive restock: the readied light is dwindling below the
    /// reorder threshold, so top the pack up to the carry target
    /// (<see cref="AutoLightPlan.BuyCount"/> of
    /// <see cref="AutoLightPlan.LightName"/>) before it dies. A shop detour like
    /// <see cref="Buy"/>, but the still-lit light keeps burning — the engine
    /// requests this at most once per readied-light instance.</summary>
    Reorder,
}

/// <summary>
/// The auto-light engine's decision for the current situation, produced by the
/// pure <see cref="AutoLightPlanner"/>. Carries the action, the light it names,
/// how many to buy (0 for <see cref="AutoLightAction.Ready"/> /
/// <see cref="AutoLightAction.None"/>), and a human-readable reason for the log.
/// </summary>
public readonly record struct AutoLightPlan(
    AutoLightAction Action,
    string? LightName,
    int BuyCount,
    string Reason)
{
    public static AutoLightPlan Nothing(string reason) =>
        new(AutoLightAction.None, null, 0, reason);

    public static AutoLightPlan ReadyLight(string name, string reason) =>
        new(AutoLightAction.Ready, name, 0, reason);

    public static AutoLightPlan BuyLight(string name, int count, string reason) =>
        new(AutoLightAction.Buy, name, count, reason);

    public static AutoLightPlan ReorderLight(string name, int count, string reason) =>
        new(AutoLightAction.Reorder, name, count, reason);
}
