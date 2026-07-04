using FujinTerm.Game;

namespace FujinTerm.Game.Map;

/// <summary>
/// Verb selection + achievability helpers for the door FSM. Captures
/// the decision matrix the walker consults at door-handling time.
/// </summary>
/// <remarks>
/// <para>
/// The "unbashable strength threshold" — the highest
/// <c>StatRequirement</c> a door can carry and still be bashable by
/// some reachable build — is supplied per game-data set by
/// <see cref="MaxStrengthIndex.MaxAchievableStrength"/>, which walks
/// the Races table and the item <c>+Strength</c> slot matrix. Both
/// decision methods take it as a parameter and fall back to
/// <see cref="UnbashableStrengthThreshold"/> when no set is loaded.
/// </para>
/// </remarks>
public static class DoorPolicy
{
    /// <summary>
    /// Fallback ceiling for "is this door bashable by anyone on this
    /// realm?", used when no game-data set is loaded so
    /// <see cref="MaxStrengthIndex"/> can't compute the real maximum.
    /// Doors with <c>StatRequirement &gt;</c> the effective ceiling are
    /// treated as bash-impossible even when the data marks them
    /// <c>(picklocks/strength)</c>.
    /// </summary>
    public const int UnbashableStrengthThreshold = 200;

    /// <summary>
    /// True when the door has at least one viable opening path for
    /// the current character — bash, pick, or "no req at all".
    /// Consulted by the walker before sending the first verb so an
    /// impossible door fails fast with a clean reason instead of
    /// burning bash/pick attempts at the server.
    /// </summary>
    /// <param name="maxBashableStrength">The active set's
    /// <see cref="MaxStrengthIndex.MaxAchievableStrength"/> — the highest
    /// Strength any build can reach. A door needing more than this can't be
    /// bashed by anyone. Defaults to <see cref="UnbashableStrengthThreshold"/>.</param>
    public static bool IsAchievable(
        int statRequirement, bool canBash, int playerStrength, int playerPicklocks,
        int maxBashableStrength = UnbashableStrengthThreshold)
    {
        if (statRequirement <= 0)
        {
            // "(Door)" / "(Door [any picklocks/strength])" — anyone
            // can open. Bash succeeds for any non-zero strength.
            return canBash || playerPicklocks > 0;
        }

        bool bashable = canBash
                     && statRequirement <= maxBashableStrength
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
    /// <param name="maxBashableStrength">The active set's
    /// <see cref="MaxStrengthIndex.MaxAchievableStrength"/> — the highest
    /// Strength any build can reach. Bash is never chosen for a door needing
    /// more than this. Defaults to <see cref="UnbashableStrengthThreshold"/>.</param>
    /// <returns>
    /// <c>"bash"</c>, <c>"pick"</c>, or <c>null</c> when neither verb
    /// can succeed (caller surfaces a "no viable verb" failure).
    /// </returns>
    public static string? ChooseVerb(
        int statRequirement,
        bool canBash,
        int playerStrength,
        int playerPicklocks,
        bool preferPickOverBash,
        int maxBashableStrength = UnbashableStrengthThreshold)
    {
        bool bashOk = canBash
                   && statRequirement <= maxBashableStrength
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
