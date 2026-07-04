namespace FujinTerm.Models.Profile;

// One row of the Character Workshop's CP Allocation plan — the target value of
// each trainable attribute at a given level. Persisted as
// CharacterProfile.CharacterPlan (oldest → newest level) and consumed by the CP
// grid, auto-train, and @train.
//
// Values are the cumulative target stats at Level (not per-level deltas),
// measured against the live raw-base baseline. The baseline (current level's
// raw stats) is not stored here — it's recomputed live from Game.PlayerStats
// minus equipment bonuses.
public sealed class CpPlanEntry
{
    public int Level { get; set; }

    public int Strength { get; set; }
    public int Intellect { get; set; }
    public int Willpower { get; set; }
    public int Agility { get; set; }
    public int Health { get; set; }
    public int Charm { get; set; }

    public CpPlanEntry() { }

    public CpPlanEntry(int level, int strength, int intellect, int willpower, int agility, int health, int charm)
    {
        Level = level;
        Strength = strength;
        Intellect = intellect;
        Willpower = willpower;
        Agility = agility;
        Health = health;
        Charm = charm;
    }
}
