namespace MudPlay.Models.Profile;

// Which trainer auto-train goes to when more than one can teach the level. Stored as
// its number, so the default (0) keeps a profile saved before this existed on the
// original nearest-first choice.
public enum TrainerChoice
{
    // The fewest steps away; the lower markup breaks a tie.
    Closest = 0,
    // The lowest markup, the cheapest lesson; the nearer breaks a tie.
    Cheapest = 1,
}
