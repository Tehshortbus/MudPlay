namespace FujinTerm.Game.Quests;

/// <summary>
/// Alignment a quest step gates on, read from the chain's
/// <c>goodaligned</c> / <c>evilaligned</c> / <c>neutralaligned</c> directive.
/// <see cref="None"/> when the step imposes no alignment requirement. Distinct
/// from <c>EvilLevel</c> (evil-rank bands) and the monster-aggression tri-state —
/// this is only the quest's "you must be good/evil/neutral to advance" gate.
/// </summary>
public enum QuestAlignment
{
    None = 0,
    Good = 1,
    Evil = 2,
    Neutral = 3,
}
