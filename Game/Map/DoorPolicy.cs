using FujinTerm.Game;

namespace FujinTerm.Game.Map;

/// <summary>
/// Verb selection + achievability helpers for the door FSM. Captures
/// the decision matrix the walker consults at door-handling time.
/// </summary>
/// <remarks>
/// <para>
/// The "unbashable strength threshold" is a v1 hardcode per user
/// direction — doors whose <c>StatRequirement</c> exceeds it can't
/// be bashed by any reachable build. Future work (gated on the
/// Phase 9 Workshop landing) replaces this with a proper
/// max-strength calc that walks the Races table and the
/// item-bonus slot matrix.
/// </para>
/// </remarks>
public static class DoorPolicy
{
    /// <summary>
    /// Conservative ceiling for "is this door bashable by anyone on
    /// this realm?" Doors with <c>StatRequirement &gt;</c> this value
    /// are treated as bash-impossible even when the data marks them
    /// <c>(picklocks/strength)</c>. v1 placeholder; supersede with
    /// the Workshop-driven max-strength calc.
    /// </summary>
    public const int UnbashableStrengthThreshold = 200;

    /// <summary>
    /// True when the door has at least one viable opening path for
    /// the current character — bash, pick, or "no req at all".
    /// Consulted by the walker before sending the first verb so an
    /// impossible door fails fast with a clean reason instead of
    /// burning bash/pick attempts at the server.
    /// </summary>
    public static bool IsAchievable(int statRequirement, bool canBash, int playerStrength, int playerPicklocks)
    {
        if (statRequirement <= 0)
        {
            // "(Door)" / "(Door [any picklocks/strength])" — anyone
            // can open. Bash succeeds for any non-zero strength.
            return canBash || playerPicklocks > 0;
        }

        bool bashable = canBash
                     && statRequirement <= UnbashableStrengthThreshold
                     && playerStrength >= statRequirement;
        bool pickable = playerPicklocks >= statRequirement;
        return bashable || pickable;
    }

    /// <summary>
    /// Decide which verb (<c>"bash"</c> or <c>"pick"</c>) to attempt
    /// first for a door. The walker calls this once per request; the
    /// FSM may fall back to the other verb on repeated failure.
    /// </summary>
    /// <param name="statRequirement">Door's required strength / picklock skill (0 = none).</param>
    /// <param name="canBash">True when the modifier reads "picklocks/strength" — bash possible.</param>
    /// <param name="playerStrength">Live <see cref="PlayerStats.Strength"/>.</param>
    /// <param name="playerPicklocks">Live <see cref="PlayerStats.Picklocks"/>.</param>
    /// <param name="preferPickOverBash">User setting from Settings.Other.</param>
    /// <returns>
    /// <c>"bash"</c>, <c>"pick"</c>, or <c>null</c> when neither verb
    /// can succeed (caller surfaces a "no viable verb" failure).
    /// </returns>
    public static string? ChooseVerb(
        int statRequirement,
        bool canBash,
        int playerStrength,
        int playerPicklocks,
        bool preferPickOverBash)
    {
        bool bashOk = canBash
                   && statRequirement <= UnbashableStrengthThreshold
                   && (statRequirement <= 0 || playerStrength >= statRequirement);
        bool pickOk = statRequirement <= 0 || playerPicklocks >= statRequirement;

        if (preferPickOverBash)
        {
            if (pickOk) return "pick";
            if (bashOk) return "bash";
        }
        else
        {
            if (bashOk) return "bash";
            if (pickOk) return "pick";
        }
        return null;
    }
}
