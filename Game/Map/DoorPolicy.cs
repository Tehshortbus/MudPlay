using FujinTerm.Game;

namespace FujinTerm.Game.Map;

// Verb selection + achievability helpers for the door FSM. Captures the
// decision matrix the walker consults at door-handling time.
//
// The "unbashable strength threshold" — the highest StatRequirement a
// door can carry and still be bashable by some reachable build — is
// supplied per game-data set by MaxStrengthIndex.MaxAchievableStrength,
// which walks the Races table and the item +Strength slot matrix. Both
// decision methods take it as a parameter and fall back to
// UnbashableStrengthThreshold when no set is loaded.
public static class DoorPolicy
{
    // Fallback ceiling for "is this door bashable by anyone on this
    // realm?", used when no game-data set is loaded so MaxStrengthIndex
    // can't compute the real maximum. Doors with StatRequirement above the
    // effective ceiling are treated as bash-impossible even when the data
    // marks them (picklocks/strength).
    public const int UnbashableStrengthThreshold = 200;

    // True when the door has at least one viable opening path for the
    // current character — bash, pick, or "no req at all". Consulted by the
    // walker before sending the first verb so an impossible door fails fast
    // with a clean reason instead of burning bash/pick attempts at the
    // server. maxBashableStrength is the active set's
    // MaxAchievableStrength — the highest Strength any build can reach; a
    // door needing more than this can't be bashed by anyone.
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

    // Decide which verb ("bash" or "pick") to attempt first for a door.
    // The walker calls this once per request; the FSM may fall back to the
    // other verb on repeated failure. maxBashableStrength is the active
    // set's MaxAchievableStrength — bash is never chosen for a door needing
    // more than that. Returns "bash", "pick", or null when neither verb can
    // succeed (caller surfaces a "no viable verb" failure).
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
