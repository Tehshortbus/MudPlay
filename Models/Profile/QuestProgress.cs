using System.Collections.Generic;

namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character completion state for one crawled quest (or alignment band),
/// keyed by the same (<see cref="Flag"/>, <see cref="Step"/>) identity
/// <c>QuestCrawler</c> emits. Persisted as <see cref="CharacterProfile.QuestLog"/>
/// — separate from the per-set quest <i>definitions</i> (names / visibility), so a
/// completion follows the character across sets.
/// </summary>
/// <remarks>
/// <see cref="Complete"/> is the effective, bonus-applying state. It's set either
/// by ticking the quest's manual complete box or, for a single-part quest with a
/// followable checklist, by ticking every step (<see cref="CheckedSteps"/> holds
/// the give-step orders the user has ticked so partial progress survives a reload).
/// Unticking a step never auto-clears <see cref="Complete"/> — the manual box owns
/// that.
/// </remarks>
public sealed class QuestProgress
{
    /// <summary>Quest-flag ability id (the <c>giveability &lt;flag&gt;</c> target).</summary>
    public int Flag { get; set; }

    /// <summary>Band level for a multi-part quest; <c>0</c> for a single-part quest.</summary>
    public int Step { get; set; }

    /// <summary>True when the quest counts as done — applies its bonus to Character Info.</summary>
    public bool Complete { get; set; }

    /// <summary>
    /// Give-step orders the user has ticked in the followable checklist (single-part
    /// quests only). <c>null</c> / empty when nothing's ticked.
    /// </summary>
    public List<int>? CheckedSteps { get; set; }

    public QuestProgress() { }

    public QuestProgress(int flag, int step)
    {
        Flag = flag;
        Step = step;
    }
}
