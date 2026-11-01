using System.IO;
using System.Text.Json;
using MudPlay.Models.GameData;

namespace MudPlay.Services;

// Persists a seed-backed per-set catalogue (message records, monster-name records) as
// the user's DELTA against the shipped seed instead of a full snapshot. A snapshot froze
// the catalogue at the user's first save: every later seed fix — including an in-place
// text fix that keeps a record's Id — never reached them. The per-set file now holds
// only what the user changed, and every load rebuilds the catalogue from the CURRENT
// seed.
//
// A seed record is addressed by its Id plus its Links. The Id alone isn't unique — the
// seeds reuse one Id across records whose text is identical but whose Links differ (a
// spell's priest and druid copies, one monster name on several Monsters rows) — so the
// Links break the tie. When the exact Links no longer match but the Id is unique in the
// seed, the Id alone still finds the record, so a seed fix that re-links a record
// doesn't strand the user's delta on it.
//
// Diff rules:
//   * a record matching a seed record, equal to it once the user's editable fields are
//     applied → omitted; it follows the seed.
//   * a record matching a seed record whose editable (non-identity) fields differ → an
//     Override. Only those fields are applied onto the CURRENT seed record on load, so
//     the seed's text always wins: a user can't change a record's text without
//     changing its Id, so differing text under the same Id can only be a seed fix
//     landing underneath.
//   * any other record → one of the user's own Records (added, or an edited copy).
//   * a seed record nothing in the catalogue matches → Removed.
public sealed class SeedDelta<T> where T : class
{
    // On-disk shape of a per-set delta file. Format 1 was the bare JSON array (a full
    // snapshot); Load migrates it.
    public sealed class DeltaFile
    {
        public int Format { get; init; } = 2;
        public List<string> Removed { get; init; } = [];
        public List<T> Overrides { get; init; } = [];
        public List<T> Records { get; init; } = [];
    }

    public sealed record MergeResult(List<T> Records, int OrphanedOverrides);

    public sealed record MigrationResult(DeltaFile Delta, int Kept, int FollowingSeed, int Removed, int NewInSeed);

    // One message that differs from the seed: the seed record and the catalogue record
    // standing in for it. Seed is null for an Added record, Current for a Removed one.
    public sealed record Difference(SeedDifferenceKind Kind, T? Seed, T? Current);

    public sealed record RevertResult(List<T> Records, int Reverted);

    private static readonly JsonDocumentOptions DocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly Func<T, string> _id;
    private readonly Func<T, string> _name;
    private readonly Func<T, IReadOnlyList<GameDataLink>?> _links;
    private readonly Func<T, T> _withoutLinks;
    private readonly Func<T, T, T> _applyOverride;

    // withoutLinks returns the record with Links nulled, so the rest compares by record
    // value equality while Links compare as a sequence. applyOverride(seed, user) copies
    // the user's editable fields onto the seed record, keeping the seed's identity text.
    public SeedDelta(
        Func<T, string> id,
        Func<T, string> name,
        Func<T, IReadOnlyList<GameDataLink>?> links,
        Func<T, T> withoutLinks,
        Func<T, T, T> applyOverride)
    {
        _id = id;
        _name = name;
        _links = links;
        _withoutLinks = withoutLinks;
        _applyOverride = applyOverride;
    }

    // ----- Pure diff / merge / migrate ---------

    public DeltaFile Diff(IEnumerable<T> current, IReadOnlyList<T> seed)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(seed);
        List<T> items = current.ToList();
        int[] slotOf = MatchSeed(items, seed, out bool[] claimed);

        DeltaFile delta = new();
        for (int i = 0; i < items.Count; i++)
        {
            if (slotOf[i] < 0) { delta.Records.Add(items[i]); continue; }
            T baseline = seed[slotOf[i]];
            T merged = _applyOverride(baseline, items[i]);
            if (!SameContent(merged, baseline)) delta.Overrides.Add(merged);
        }
        for (int s = 0; s < seed.Count; s++)
            if (!claimed[s]) delta.Removed.Add(KeyOf(seed[s]));
        return delta;
    }

    // Every way current departs from seed, one entry per message, in catalogue order
    // with the removed seed records last. An own record is paired with the removed seed
    // record it was edited from — same Name and Links (the pairing Migrate uses), else
    // the only own record and only removed seed record sharing those Links (the text
    // and Name were both edited) — so the pair reads as one Edited message rather than
    // an unrelated Added + Removed.
    public List<Difference> Compare(IEnumerable<T> current, IReadOnlyList<T> seed)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(seed);
        List<T> items = current.ToList();
        int[] slotOf = MatchSeed(items, seed, out bool[] claimed);

        int[] editedFrom = new int[items.Count];
        Array.Fill(editedFrom, -1);
        bool[] paired = new bool[seed.Count];
        PairOwnRecords(NameLinkKey);
        PairOwnRecords(r => LinkKey(_links(r)), uniqueOnly: true);

        List<Difference> differences = [];
        for (int i = 0; i < items.Count; i++)
        {
            if (slotOf[i] >= 0)
            {
                T baseline = seed[slotOf[i]];
                if (!SameContent(_applyOverride(baseline, items[i]), baseline))
                    differences.Add(new Difference(SeedDifferenceKind.Override, baseline, items[i]));
            }
            else if (editedFrom[i] >= 0)
                differences.Add(new Difference(SeedDifferenceKind.Edited, seed[editedFrom[i]], items[i]));
            else
                differences.Add(new Difference(SeedDifferenceKind.Added, null, items[i]));
        }
        for (int s = 0; s < seed.Count; s++)
            if (!claimed[s] && !paired[s])
                differences.Add(new Difference(SeedDifferenceKind.Removed, seed[s], null));
        return differences;

        void PairOwnRecords(Func<T, string> key, bool uniqueOnly = false)
        {
            Dictionary<string, List<int>> unpairedSeed = new(StringComparer.Ordinal);
            for (int s = 0; s < seed.Count; s++)
            {
                if (claimed[s] || paired[s]) continue;
                string k = key(seed[s]);
                if (!unpairedSeed.TryGetValue(k, out List<int>? slots)) unpairedSeed[k] = slots = [];
                slots.Add(s);
            }
            Dictionary<string, int> ownPerKey = new(StringComparer.Ordinal);
            for (int i = 0; i < items.Count; i++)
            {
                if (slotOf[i] >= 0 || editedFrom[i] >= 0) continue;
                string k = key(items[i]);
                ownPerKey[k] = ownPerKey.GetValueOrDefault(k) + 1;
            }

            for (int i = 0; i < items.Count; i++)
            {
                if (slotOf[i] >= 0 || editedFrom[i] >= 0) continue;
                string k = key(items[i]);
                // Links alone only tie a pair when nothing else could claim either side,
                // and never on an empty key (every unlinked record would share it).
                if (uniqueOnly && (k.Length == 0 || ownPerKey[k] != 1)) continue;
                if (!unpairedSeed.TryGetValue(k, out List<int>? slots) || slots.Count == 0) continue;
                if (uniqueOnly && slots.Count != 1) continue;
                editedFrom[i] = slots[0];
                paired[slots[0]] = true;
                slots.RemoveAt(0);
            }
        }
    }

    // current with each of the chosen differences put back to the seed: an edited copy
    // or an override is replaced in place by its seed record, an added record is dropped,
    // and a removed seed record is restored (appended — the next load rebuilds seed
    // order). Records are matched by reference, so an entry that no longer applies — its
    // catalogue record was edited or deleted since the comparison, or its seed record
    // isn't in this seed (the set switched) — is skipped rather than guessed at.
    public RevertResult Revert(IReadOnlyList<T> current, IReadOnlyList<T> seed, IEnumerable<Difference> useSeed)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(useSeed);
        List<T> records = current.ToList();
        HashSet<T> inSeed = new(seed, ReferenceEqualityComparer.Instance);
        HashSet<T> present = new(records, ReferenceEqualityComparer.Instance);
        int reverted = 0;

        foreach (Difference d in useSeed)
        {
            if (d.Seed is { } s && (!inSeed.Contains(s) || present.Contains(s))) continue;
            if (d.Current is { } c && !present.Contains(c)) continue;
            switch (d.Kind)
            {
                case SeedDifferenceKind.Removed when d.Seed is { } restored:
                    records.Add(restored);
                    present.Add(restored);
                    break;
                case SeedDifferenceKind.Added when d.Current is { } added:
                    records.RemoveAt(records.FindIndex(r => ReferenceEquals(r, added)));
                    present.Remove(added);
                    break;
                case SeedDifferenceKind.Edited or SeedDifferenceKind.Override
                    when d.Seed is { } original && d.Current is { } mine:
                    records[records.FindIndex(r => ReferenceEquals(r, mine))] = original;
                    present.Remove(mine);
                    present.Add(original);
                    break;
                default:
                    continue;
            }
            reverted++;
        }
        return new RevertResult(records, reverted);
    }

    // Seed order (minus Removed, Overrides applied), then the user's own records.
    public MergeResult Merge(IReadOnlyList<T> seed, DeltaFile delta)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(delta);
        SeedIndex index = new(this, seed);
        bool[] removed = new bool[seed.Count];
        foreach (string key in delta.Removed)
        {
            int bar = key.IndexOf('|', StringComparison.Ordinal);
            int s = bar < 0 ? index.ById(key) : index.Resolve(key[..bar], key[(bar + 1)..]);
            if (s >= 0) removed[s] = true;
        }

        T?[] overrides = new T?[seed.Count];
        int orphaned = 0;
        foreach (T o in delta.Overrides)
        {
            // An override whose seed record is gone (dropped from the seed, or re-linked
            // under a now-shared Id) has nothing left to apply to.
            int s = index.Resolve(_id(o), LinkKey(_links(o)));
            if (s < 0 || removed[s] || overrides[s] is not null) { orphaned++; continue; }
            overrides[s] = o;
        }

        List<T> own = [];
        foreach (T r in delta.Records)
        {
            // An own record the seed now carries verbatim (Id + Links) was folded into the
            // seed after the user saved it: adopt the seed's copy so it isn't listed twice.
            int s = index.Exact(_id(r), LinkKey(_links(r)));
            if (s >= 0 && !removed[s] && overrides[s] is null) { overrides[s] = r; continue; }
            own.Add(r);
        }

        List<T> records = new(seed.Count + own.Count);
        for (int s = 0; s < seed.Count; s++)
        {
            if (removed[s]) continue;
            records.Add(overrides[s] is { } o ? _applyOverride(seed[s], o) : seed[s]);
        }
        records.AddRange(own);
        return new MergeResult(records, orphaned);
    }

    // Convert a legacy full snapshot into a delta. A snapshot carries no removal info and
    // can't say which of its fields the user changed, so: a record whose Id is in the
    // seed follows the seed; any other record is kept as the user's own; a seed record
    // absent from the snapshot counts as removed only when a kept record has its Name and
    // Links (the user edited it into that copy) — otherwise it was added to the seed after
    // the user saved, and is included.
    public MigrationResult Migrate(IReadOnlyList<T> legacy, IReadOnlyList<T> seed)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(seed);
        HashSet<string> seedIds = seed.Select(_id).ToHashSet(StringComparer.Ordinal);
        HashSet<string> legacyIds = legacy.Select(_id).ToHashSet(StringComparer.Ordinal);

        DeltaFile delta = new();
        int following = 0;
        foreach (T r in legacy)
        {
            if (seedIds.Contains(_id(r))) following++;
            else delta.Records.Add(r);
        }

        HashSet<string> keptNameLinks = delta.Records.Select(NameLinkKey).ToHashSet(StringComparer.Ordinal);
        int newInSeed = 0;
        foreach (T s in seed)
        {
            if (legacyIds.Contains(_id(s))) continue;
            if (keptNameLinks.Contains(NameLinkKey(s))) delta.Removed.Add(KeyOf(s));
            else newInSeed++;
        }
        return new MigrationResult(delta, delta.Records.Count, following, delta.Removed.Count, newInSeed);
    }

    // ----- File I/O ---------

    // The catalogue at path layered over seed, or null when the file is missing or
    // unreadable (logged) so the caller falls back to the seed alone. A legacy snapshot
    // file is migrated once: backed up beside itself as "<file>.pre-delta", rewritten as
    // a delta, and the outcome logged.
    public List<T>? Load(string path, IReadOnlyList<T> seed, LogService? log, string category)
    {
        if (!File.Exists(path)) return null;
        List<T>? legacy;
        DeltaFile? delta;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), DocOptions);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                legacy = doc.RootElement.Deserialize<List<T>>(JsonStore.Options) ?? [];
                delta = null;
            }
            else
            {
                legacy = null;
                delta = doc.RootElement.Deserialize<DeltaFile>(JsonStore.Options) ?? new DeltaFile();
            }
        }
        catch (Exception ex)
        {
            // Corrupt or schema-drifted file (e.g. a retired enum name) — surface it and
            // let the caller fall back to the seed rather than load a partial catalogue.
            log?.Log(LogSeverity.Warn, category, $"Failed to load '{path}': {ex.Message}");
            return null;
        }

        // Every record of ours has an Id. One without is a game table's row: a database
        // table of the same name was imported over this file where file names ignore case
        // (Crash-20261101-094634). It can't be addressed, and a diff over it throws, so
        // it's dropped and the file written back without it.
        int stray = delta is null
            ? legacy!.RemoveAll(HasNoId)
            : delta.Records.RemoveAll(HasNoId) + delta.Overrides.RemoveAll(HasNoId);
        if (stray > 0)
            log?.Log(LogSeverity.Warn, category,
                $"'{path}': dropped {stray} record(s) with no Id — not records of this catalogue.");

        if (delta is null) return MigrateLegacy(path, legacy!, seed, log, category);
        if (stray > 0)
        {
            try { JsonStore.Save(path, delta); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Log(LogSeverity.Warn, category,
                    $"'{path}': couldn't write it back ({ex.Message}); written on the next save.");
            }
        }

        MergeResult merged = Merge(seed, delta);
        if (merged.OrphanedOverrides > 0)
            log?.Log(LogSeverity.Info, category,
                $"'{path}': {merged.OrphanedOverrides} edit(s) apply to records no longer in the seed and were dropped.");
        return merged.Records;
    }

    public void Save(string path, IEnumerable<T> current, IReadOnlyList<T> seed, LogService? log, string category)
    {
        DeltaFile delta = Diff(current, seed);
        JsonStore.Save(path, delta);
        log?.Log(LogSeverity.Debug, category,
            $"saved '{path}': {delta.Records.Count} own record(s), {delta.Overrides.Count} override(s), " +
            $"{delta.Removed.Count} removed seed record(s); everything else follows the seed.");
    }

    private List<T> MigrateLegacy(string path, List<T> legacy, IReadOnlyList<T> seed, LogService? log, string category)
    {
        if (seed.Count == 0)
        {
            // Migrating against nothing would file every seed copy as the user's own.
            // Keep the snapshot until a launch that can read the seed.
            log?.Log(LogSeverity.Warn, category,
                $"'{path}' is an old full-snapshot file but no seed could be read — loaded as-is, not migrated.");
            return legacy;
        }

        MigrationResult m = Migrate(legacy, seed);
        string backup = path + ".pre-delta";
        try
        {
            if (!File.Exists(backup)) File.Copy(path, backup);
            JsonStore.Save(path, m.Delta);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Log(LogSeverity.Warn, category,
                $"'{path}': couldn't rewrite the migrated file ({ex.Message}); migrated in memory, written on the next save.");
        }
        log?.Log(LogSeverity.Info, category,
            $"migrated '{path}' to edits-over-seed: kept {m.Kept} record(s) of your own, {m.FollowingSeed} now follow " +
            $"the seed, {m.Removed} seed record(s) stay removed (edited into your copies), {m.NewInSeed} seed record(s) " +
            $"added since the file was saved; old file backed up to '{backup}'.");
        return Merge(seed, m.Delta).Records;
    }

    // ----- Keys ---------

    // Each catalogue record's seed slot (-1 = none), claimed[s] set for every taken slot.
    // Exact matches (Id + Links) claim first, so an Id-only fallback never takes a seed
    // record a later catalogue record matches exactly.
    private int[] MatchSeed(List<T> items, IReadOnlyList<T> seed, out bool[] claimed)
    {
        SeedIndex index = new(this, seed);
        int[] slotOf = new int[items.Count];
        Array.Fill(slotOf, -1);
        bool[] taken = new bool[seed.Count];

        for (int i = 0; i < items.Count; i++)
            Claim(i, index.Exact(_id(items[i]), LinkKey(_links(items[i]))));
        for (int i = 0; i < items.Count; i++)
            if (slotOf[i] < 0) Claim(i, index.ById(_id(items[i])));

        claimed = taken;
        return slotOf;

        void Claim(int item, int slot)
        {
            if (slot < 0 || taken[slot]) return;
            taken[slot] = true;
            slotOf[item] = slot;
        }
    }

    private bool HasNoId(T r) => string.IsNullOrEmpty(_id(r));

    private bool SameContent(T a, T b)
        => EqualityComparer<T>.Default.Equals(_withoutLinks(a), _withoutLinks(b))
           && LinkKey(_links(a), sorted: false) == LinkKey(_links(b), sorted: false);

    private string KeyOf(T r) => _id(r) + "|" + LinkKey(_links(r));

    private string NameLinkKey(T r) => _name(r).Trim().ToLowerInvariant() + "|" + LinkKey(_links(r));

    // Sorted for addressing (order carries no meaning there); unsorted for content
    // equality, so a reorder the user saved still counts as their edit.
    private static string LinkKey(IReadOnlyList<GameDataLink>? links, bool sorted = true)
    {
        if (links is null || links.Count == 0) return string.Empty;
        IEnumerable<string> parts = links.Select(l => $"{l.Table.ToLowerInvariant()}#{l.Number}");
        return string.Join(",", sorted ? parts.Order(StringComparer.Ordinal) : parts);
    }

    private sealed class SeedIndex
    {
        private readonly Dictionary<string, int> _byKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<int>> _byId = new(StringComparer.Ordinal);

        public SeedIndex(SeedDelta<T> spec, IReadOnlyList<T> seed)
        {
            for (int i = 0; i < seed.Count; i++)
            {
                _byKey.TryAdd(spec.KeyOf(seed[i]), i);
                string id = spec._id(seed[i]);
                if (!_byId.TryGetValue(id, out List<int>? slots)) _byId[id] = slots = [];
                slots.Add(i);
            }
        }

        public int Exact(string id, string linkKey)
            => _byKey.TryGetValue(id + "|" + linkKey, out int i) ? i : -1;

        public int ById(string id)
            => _byId.TryGetValue(id, out List<int>? slots) && slots.Count == 1 ? slots[0] : -1;

        public int Resolve(string id, string linkKey)
        {
            int exact = Exact(id, linkKey);
            return exact >= 0 ? exact : ById(id);
        }
    }
}
