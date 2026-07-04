using System.Collections.Generic;

namespace FujinTerm.Game.Map;

// One party member's level estimate for level-gate evaluation. Prefer the
// Exact level (learned from an @level probe) when known; otherwise fall back to
// the TitleRange — the 5-level band the member's who title implies
// (ClassTitleTable.LookupLevelRange). A member with neither is a total unknown
// and is skipped by PartyLevelBounds.Compute, so we never route around a gate
// on a member we know nothing about (which would risk making a destination
// unreachable).
public readonly record struct PartyLevelEstimate(int? Exact, (int Min, int Max)? TitleRange)
{
    // Lowest level this member could be — exact if known, else the title band's
    // floor.
    public int? Low => Exact ?? TitleRange?.Min;

    // Highest level this member could be — exact if known, else the title
    // band's cap.
    public int? High => Exact ?? TitleRange?.Max;
}

// Pure fold of the leader's level and every party member's level estimate into
// the party's most-constraining (Low, High) window, used by
// MovementFilter.IsExitBlocked to route a party-following walk around a
// (Level: MIN to MAX) gate the whole party can't clear — rather than leaving a
// member behind. The point is to still reach the destination while keeping
// everyone together.
//
// Low is the minimum over members of their lowest-possible level; High is the
// maximum of their highest-possible level. A gate is then impassable for the
// party iff a floor excludes the lowest member (Low < MinLevel) or a cap
// excludes the highest (High > MaxLevel).
//
// The leader's own level is folded in (not just the followers): although the
// walker's self-only gate check already guarantees the leader clears any gate
// on its planned path, this fold is what carries the leader's constraint into
// the party branch — without it, a party branch that saw only followers could
// wave the leader through a gate the leader itself can't cross. Members with no
// level signal at all are skipped. Returns null when nothing is known about
// anyone, so the caller falls back to self-only evaluation.
public static class PartyLevelBounds
{
    // See the type comment. Skips members with no known level; folds in
    // selfLevel when positive.
    public static (int Low, int High)? Compute(int? selfLevel, IEnumerable<PartyLevelEstimate> members)
    {
        ArgumentNullException.ThrowIfNull(members);

        int? low = null;
        int? high = null;

        void Fold(int lo, int hi)
        {
            low = low is { } l ? Math.Min(l, lo) : lo;
            high = high is { } h ? Math.Max(h, hi) : hi;
        }

        if (selfLevel is { } self && self > 0) Fold(self, self);

        foreach (PartyLevelEstimate m in members)
        {
            if (m.Low is not { } lo || m.High is not { } hi) continue;
            if (lo <= 0 || hi <= 0) continue;
            Fold(lo, hi);
        }

        return low is { } finalLow && high is { } finalHigh ? (finalLow, finalHigh) : null;
    }
}
