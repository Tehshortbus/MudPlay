namespace MudPlay.Models.Profile;

// How a party trip picks the trainer its members train at next. Stored as its
// number; the default (0) is the original most-members-first choice.
public enum PartyStopChoice
{
    // The trainer that trains the most members still short of their level, so the
    // trip makes as few stops as it can; TrainerChoice breaks a tie.
    MostMembers = 0,
    // Any trainer that trains at least one of them, picked by TrainerChoice alone.
    ByTrainerChoice = 1,
}
