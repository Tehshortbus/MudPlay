namespace FujinTerm.Game.Light;

// What the auto-light engine should do about the route ahead.
public enum AutoLightAction
{
    // Route is lit and any readied light is healthy — do nothing.
    None,

    // Ready a light already in the pack — the engine sends use <LightName>.
    // No shop trip needed.
    Ready,

    // Buy BuyCount of LightName (a shop detour), then ready one.
    Buy,

    // Preemptive restock: the readied light is dwindling below the reorder
    // threshold, so top the pack up to the carry target (BuyCount of LightName)
    // before it dies. A shop detour like Buy, but the still-lit light keeps
    // burning — the engine requests this at most once per readied-light instance.
    Reorder,
}

// The auto-light engine's decision for the current situation, produced by the
// pure AutoLightPlanner. Carries the action, the light it names, how many to buy
// (0 for Ready / None), and a human-readable reason for the log.
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
