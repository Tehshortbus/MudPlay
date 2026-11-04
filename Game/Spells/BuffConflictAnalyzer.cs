using System;
using System.Collections.Generic;
using System.Linq;

namespace MudPlay.Game.Spells;

// Who a configured buff slot's spell can land on, derived from its targeting
// flags — the shape needed to tell whether two slots could ever affect the same
// character (a precondition for a RemovesSpell conflict between their spells to
// matter in play). Built from a slot's raw flags, not live party state, so a
// specific-member list "safely outlives the party it was built for" the same way
// BuffSlot.Targets itself does.
public readonly struct BuffAffectSet
{
    public bool Everyone { get; init; }          // whole-party cast — lands on self + every member
    public bool Self { get; init; }              // CastOnSelf ticked
    public bool AllMembers { get; init; }         // single-target slot's AllMembers auto-adapt
    public IReadOnlyCollection<string> Members { get; init; } // single-target slot's explicit Targets (lower-cased)

    public static readonly BuffAffectSet None = new() { Members = Array.Empty<string>() };

    public bool IsEmpty => !Everyone && !Self && !AllMembers && Members.Count == 0;

    // isWholePartySpell: the slot's spell has Targets 10/13 (BuffClassifier.IsWholeParty).
    // wholePartyOn: the slot's master Party toggle. Off makes the slot inert everywhere.
    // A whole-party cast lands on the caster too, and its party use is the widest
    // possible affect set, so an enabled whole-party slot is represented as Everyone.
    // The subordinate Solo option cannot activate a master-disabled slot by itself.
    // castOnSelf/allMembers/targets: BuffSlot's targeting flags, meaningful only when
    // the spell isn't whole-party (a whole-party cast always includes self and needs
    // no member list).
    public static BuffAffectSet From(
        bool isWholePartySpell, bool wholePartyOn,
        bool castOnSelf, bool allMembers, IReadOnlyCollection<string> targets)
    {
        if (isWholePartySpell)
        {
            if (wholePartyOn) return new BuffAffectSet { Everyone = true, Members = Array.Empty<string>() };
            return None;
        }

        return new BuffAffectSet
        {
            Self = castOnSelf,
            AllMembers = allMembers,
            Members = allMembers
                ? Array.Empty<string>()
                : targets.Select(t => t.Trim().ToLowerInvariant()).ToArray(),
        };
    }
}

// Cross-references two configured buff slots against the spellbook's RemovesSpell
// (Abil 122) relation. Purely informational — see AppServices.BuffSlotOverwritePairs
// for the caller that builds these from live BuffSettings, and SelfBuffCoverage for
// the one case (self superseded by a whole-party buff) that also drives automation.
public readonly record struct BuffOverwritePair(
    string RemovingCode, string RemovingName, string RemovedCode, string RemovedName);

// A configured buff slot reduced to what the conflict pairing needs: its spell, what the
// spell removes, and who the slot lands on.
public readonly record struct ConfiguredBuff(
    string Code, string Name, int Number, IReadOnlyCollection<int> Removes, BuffAffectSet Affect);

// A self-castable buff eligible for the Buff Panel's "Add all blesses" bulk add
// — self-only or single-target-on-self, alignment-eligible, not already slotted.
// Removes is its Abil-122 RemovesSpell target numbers. This is a pure record; the
// CALLER decides the pool (the Buff Panel supplies only spells actually learned).
// IsObtained gates which candidate gets pre-checked (see SelectSelfBlessCandidates)
// — defaults true so callers that only care about the RemovesSpell math don't have
// to plumb it through.
public readonly record struct SelfBlessCandidate(
    string CastCode, string Name, int Number, int ReqLevel, int ManaCost,
    IReadOnlyCollection<int> Removes, bool IsObtained = true);

// One SelfBlessCandidate as "Add all blesses" presents it: always added as a row,
// but only Recommended candidates come pre-checked (Self ticked) — the rest are
// listed unchecked so the player can swap one in by hand.
public readonly record struct SelfBlessPick(SelfBlessCandidate Candidate, bool Recommended);

// An already-configured slot's spell identity + who it can land on + what it
// removes — enough to tell whether a self-bless candidate would clobber it, or be
// clobbered by it, without needing the slot DTO itself.
public readonly record struct ExistingBuffSlot(
    int Number, BuffAffectSet Affect, IReadOnlyCollection<int> Removes);

public static class BuffConflictAnalyzer
{
    // The Abil code the Spells table uses for RemovesSpell.
    public const int RemovesSpellAbil = 122;

    // The Abil-122 (RemovesSpell) target spell numbers carried in a spell's formula.
    public static HashSet<int> RemovedSpellNumbers(SpellFormulaInput formula)
    {
        HashSet<int> nums = new();
        foreach (SpellAbility a in formula.Abilities)
            if (a.Code == RemovesSpellAbil) nums.Add(a.Value);
        return nums;
    }

    // True when two slots' targeting could ever land on the same character — the
    // precondition for a RemovesSpell relation between their spells to matter.
    public static bool CanCoLand(in BuffAffectSet a, in BuffAffectSet b)
    {
        if (a.IsEmpty || b.IsEmpty) return false;
        if (a.Everyone || b.Everyone) return true;
        if (a.Self && b.Self) return true;
        if (a.AllMembers && (b.AllMembers || b.Members.Count > 0)) return true;
        if (b.AllMembers && a.Members.Count > 0) return true;
        foreach (string m in a.Members)
            if (b.Members.Contains(m)) return true;
        return false;
    }

    // Every ordered pair of configured buffs where one's spell removes the other's and
    // their affect sets can land on the same character.
    public static IReadOnlyList<BuffOverwritePair> OverwritePairs(IReadOnlyList<ConfiguredBuff> buffs)
    {
        List<BuffOverwritePair> pairs = new();
        for (int i = 0; i < buffs.Count; i++)
        {
            if (buffs[i].Removes.Count == 0) continue;
            for (int j = 0; j < buffs.Count; j++)
            {
                if (i == j || !buffs[i].Removes.Contains(buffs[j].Number)) continue;
                if (!CanCoLand(buffs[i].Affect, buffs[j].Affect)) continue;
                pairs.Add(new BuffOverwritePair(buffs[i].Code, buffs[i].Name, buffs[j].Code, buffs[j].Name));
            }
        }
        return pairs;
    }

    // Combines a conflict-pair list into a per-cast-code lookup: the name of another
    // configured buff that removes castCode (RemovedBy) and/or the name of another it
    // removes (Removes). Shared by the Buff Panel's per-row warning and the Buff
    // Watchdog's row annotation so "which pairs conflict" is answered in one place.
    public static (string? RemovedBy, string? Removes) Resolve(
        IReadOnlyList<BuffOverwritePair> pairs, string castCode)
    {
        string? removedBy = null, removes = null;
        foreach (BuffOverwritePair p in pairs)
        {
            if (removedBy is null && string.Equals(p.RemovedCode, castCode, StringComparison.OrdinalIgnoreCase))
                removedBy = p.RemovingName;
            if (removes is null && string.Equals(p.RemovingCode, castCode, StringComparison.OrdinalIgnoreCase))
                removes = p.RemovedName;
        }
        return (removedBy, removes);
    }

    // From a set of configured overwrite pairs, the buffs that a PERMANENT winner removes
    // one-directionally: loser cast code → winning buff name. X is a permanent loser when
    // some Y removes X AND X does NOT remove Y back — Y stays up and re-strips X, so X can
    // never hold. Mutual pairs (each removes the other, e.g. bless ↔ greater bless) are
    // excluded: those are last-cast-wins, not a permanent loss. Case-insensitive on codes;
    // first winner encountered per loser wins the label. Pure — the CALLER decides whether
    // to apply it (AppServices gates on the Paradigm realm, where removes re-fire ~3s).
    public static IReadOnlyDictionary<string, string> OneDirectionalLosers(
        IReadOnlyList<BuffOverwritePair> pairs)
    {
        Dictionary<string, string> losers = new(StringComparer.OrdinalIgnoreCase);
        bool Removes(string remover, string removed) =>
            pairs.Any(q => string.Equals(q.RemovingCode, remover, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(q.RemovedCode, removed, StringComparison.OrdinalIgnoreCase));
        foreach (BuffOverwritePair p in pairs)
        {
            if (Removes(p.RemovedCode, p.RemovingCode)) continue;   // mutual → last-cast-wins
            if (!losers.ContainsKey(p.RemovedCode)) losers[p.RemovedCode] = p.RemovingName;
        }
        return losers;
    }

    // The one-directional loser → REMOVER cast-code map, the ordering counterpart to
    // OneDirectionalLosers (which returns the winner's NAME for display). Same relation
    // — X is a one-way loser when some Y removes X and X doesn't remove Y back, mutual
    // pairs excluded — but keyed to the remover's cast CODE so a caller can act on it
    // (e.g. cast the remover before the loser). Used on STOCK, where removes fire only at
    // cast: casting the remover first lets the loser coexist, so instead of dropping the
    // loser (the Paradigm branch) the director orders the remover ahead of it.
    public static IReadOnlyDictionary<string, string> OneDirectionalRemoverCodes(
        IReadOnlyList<BuffOverwritePair> pairs)
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        bool Removes(string remover, string removed) =>
            pairs.Any(q => string.Equals(q.RemovingCode, remover, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(q.RemovedCode, removed, StringComparison.OrdinalIgnoreCase));
        foreach (BuffOverwritePair p in pairs)
        {
            if (Removes(p.RemovedCode, p.RemovingCode)) continue;   // mutual → last-cast-wins
            if (!map.ContainsKey(p.RemovedCode)) map[p.RemovedCode] = p.RemovingCode;
        }
        return map;
    }

    // One combined tooltip covering both directions, or null when neither applies.
    public static string? FormatTooltip(string? removedBy, string? removes)
    {
        if (removedBy is null && removes is null) return null;
        List<string> lines = new();
        if (removedBy is not null) lines.Add($"Removed by: {removedBy}");
        if (removes is not null) lines.Add($"Removes: {removes}");
        return string.Join("\n", lines);
    }

    // The Buff Panel's "Add all blesses" pick: EVERY candidate in the pool gets a
    // row so any auto-pick can be swapped for another family member by hand — but
    // only one per RemovesSpell family (typically a tiered pair like zeal / greater
    // zeal, which mutually remove each other) comes pre-checked: the highest-ReqLevel
    // OBTAINED member (an unlearned pick — if the caller ever supplies one — is never
    // auto-checked, since the game would just refuse the cast), and only when it
    // wouldn't immediately clobber — or be clobbered by — something already active
    // in an existing slot. A cluster with no obtained member recommends nothing. A
    // self candidate is always assumed able to co-land with itself, so CanCoLand
    // only needs to gate against the EXISTING slot's own targeting (a single-target
    // slot aimed at other members only, for instance, can never conflict with a
    // self cast).
    public static IReadOnlyList<SelfBlessPick> SelectSelfBlessCandidates(
        IReadOnlyList<SelfBlessCandidate> pool, IReadOnlyList<ExistingBuffSlot> existing)
    {
        if (pool.Count == 0) return Array.Empty<SelfBlessPick>();

        BuffAffectSet selfAffect = new() { Self = true, Members = Array.Empty<string>() };
        bool ClobbersExisting(SelfBlessCandidate cand)
        {
            foreach (ExistingBuffSlot slot in existing)
            {
                if (!CanCoLand(selfAffect, slot.Affect)) continue;
                if (cand.Removes.Contains(slot.Number) || slot.Removes.Contains(cand.Number)) return true;
            }
            return false;
        }

        Dictionary<int, int> parent = pool.ToDictionary(s => s.Number, s => s.Number);
        int Find(int x) => parent[x] == x ? x : (parent[x] = Find(parent[x]));
        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb) parent[ra] = rb;
        }
        HashSet<int> poolNumbers = pool.Select(s => s.Number).ToHashSet();
        foreach (SelfBlessCandidate cand in pool)
            foreach (int removed in cand.Removes)
                if (poolNumbers.Contains(removed)) Union(cand.Number, removed);

        HashSet<int> recommended = new();
        foreach (IGrouping<int, SelfBlessCandidate> cluster in pool.GroupBy(s => Find(s.Number)))
        {
            SelfBlessCandidate? preferred = null;
            foreach (SelfBlessCandidate cand in cluster)
            {
                if (!cand.IsObtained) continue;
                if (preferred is not { } cur
                    || cand.ReqLevel > cur.ReqLevel
                    || (cand.ReqLevel == cur.ReqLevel && cand.ManaCost > cur.ManaCost))
                    preferred = cand;
            }
            if (preferred is { } p && !ClobbersExisting(p)) recommended.Add(p.Number);
        }

        // Ordered by ReqLevel so the list reads low-level to high-level (a level-50
        // pick doesn't land in the middle of a batch of level-5 ones just because
        // its name sorts earlier); same-level ties break alphabetically for a
        // stable, predictable order.
        return pool
            .Select(s => new SelfBlessPick(s, recommended.Contains(s.Number)))
            .OrderBy(p => p.Candidate.ReqLevel)
            .ThenBy(p => p.Candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
