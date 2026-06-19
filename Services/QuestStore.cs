using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

/// <summary>
/// Loads and resolves quest definitions for the active game-data set. Two layers
/// merge per (flag, step) in priority order:
/// <list type="number">
///   <item>the user's per-set overlay <c>{set}/quests.json</c> — display name,
///   show/hide visibility, edited step markdown;</item>
///   <item>the universal read-only seed <c>QuestDefs.seed.json</c>, keyed by the
///   same game-data flag numbers (custom realms reuse the numbers), so a curated
///   default ports across every set;</item>
///   <item>an auto-draft (blank name, shown, no edited steps) for any quest the
///   crawler discovers that neither layer names yet.</item>
/// </list>
/// The seed is never written; the overlay travels with the set (sibling to
/// <c>triggers.json</c>) and reloads on <see cref="OnActiveSetChanged"/>. The
/// mechanical data — ordered steps + stat bonuses — is crawled from the set's
/// <c>TBInfo</c> elsewhere; this store owns only the user/seed text layer.
/// <para>
/// The overlay also holds two user-driven extras: <b>manual quests</b> the crawler
/// never finds (identity in the reserved <see cref="QuestDefinition.ManualFlagBase"/>
/// flag range, persisted verbatim since they have no crawl baseline — see
/// <see cref="ManualQuests"/>), and a <see cref="QuestDefinition.Blocked"/> flag that
/// suppresses a spuriously-crawled quest from the journal in sets where it shouldn't
/// appear.
/// </para>
/// </summary>
public sealed class QuestStore
{
    private readonly LogService? _log;
    private readonly string _seedPath;
    private readonly Dictionary<(int Flag, int Step), QuestDefinition> _seed = new();
    private readonly Dictionary<(int Flag, int Step), QuestDefinition> _overlay = new();

    /// <summary>Active set whose overlay is loaded, or <c>null</c> when none.</summary>
    public string? ActiveSet { get; private set; }

    /// <param name="log">Optional log sink for load diagnostics.</param>
    /// <param name="seedPath">Universal seed path; defaults to
    /// <see cref="AppPaths.DefaultQuestDefsSeedFile"/>. Parameterized so tests can
    /// point at a scratch seed without touching the shared Global copy.</param>
    public QuestStore(LogService? log = null, string? seedPath = null)
    {
        _log = log;
        _seedPath = seedPath ?? AppPaths.DefaultQuestDefsSeedFile;
        LoadInto(_seed, _seedPath, "seed");
    }

    /// <summary>
    /// Swap the active set: drop the previous overlay and load
    /// <c>{set}/quests.json</c> (empty when the set has no overlay yet, or when
    /// <paramref name="setName"/> is blank).
    /// </summary>
    public void OnActiveSetChanged(string? setName)
    {
        _overlay.Clear();
        ActiveSet = string.IsNullOrWhiteSpace(setName) ? null : setName;
        if (ActiveSet is null) return;
        LoadInto(_overlay, AppPaths.QuestsFile(ActiveSet), "overlay");
    }

    /// <summary>
    /// Resolve the effective definition for a quest: the user overlay if it names
    /// this (flag, step); else the universal seed; else a blank-named, visible
    /// auto-draft. Never returns <c>null</c>.
    /// </summary>
    public QuestDefinition Resolve(int flag, int step)
    {
        (int Flag, int Step) key = (flag, step);
        if (_overlay.TryGetValue(key, out QuestDefinition? user)) return user;
        if (_seed.TryGetValue(key, out QuestDefinition? seeded)) return seeded;
        return new QuestDefinition(flag, step);
    }

    /// <summary>
    /// Persist the user's edited definitions to the active set's overlay
    /// (<c>{set}/quests.json</c>) and refresh the in-memory layer so later
    /// <see cref="Resolve"/> calls see the edits immediately. The overlay stays a
    /// delta: a definition that matches what <see cref="Resolve"/> would return
    /// with no overlay (the seed entry, or a blank auto-draft) is dropped rather
    /// than frozen into the file, so a later seed update still flows through for
    /// untouched quests. No-op when no set is active.
    /// </summary>
    public void Save(IEnumerable<QuestDefinition> defs)
    {
        ArgumentNullException.ThrowIfNull(defs);
        if (ActiveSet is null) return;

        _overlay.Clear();
        foreach (QuestDefinition raw in defs)
        {
            QuestDefinition def = Normalize(raw);
            if (QuestDefinition.IsManual(def.Flag))
            {
                // A manual quest has no crawl/seed baseline to regenerate it, so it persists
                // verbatim rather than as a delta — except a wholly-blank "Add Quest" row the
                // user never filled in, which is dropped so it doesn't clutter the overlay.
                if (IsEmptyManual(def)) continue;
            }
            else if (SameContent(def, Baseline(def.Flag, def.Step))) continue; // delta-only
            _overlay[(def.Flag, def.Step)] = def;
        }

        List<QuestDefinition> list = _overlay.Values
            .OrderBy(d => d.Flag).ThenBy(d => d.Step)
            .ToList();
        try
        {
            JsonStore.Save(AppPaths.QuestsFile(ActiveSet), list);
        }
        catch (Exception ex)
        {
            // A failed write (permissions, disk) shouldn't crash the editor — the
            // in-memory overlay still reflects the edits for this session.
            _log?.Warn("Quests", $"Failed to save overlay for '{ActiveSet}': {ex.Message}");
        }
    }

    /// <summary>
    /// Every user-added (manual) quest the store knows for the active set, resolved
    /// (overlay over seed) and ordered by flag. These carry no crawl backing, so the
    /// Quest Status tab and editor materialize them straight from the definition.
    /// </summary>
    public IReadOnlyList<QuestDefinition> ManualQuests()
    {
        var keys = new HashSet<(int Flag, int Step)>();
        foreach ((int Flag, int Step) k in _seed.Keys) if (QuestDefinition.IsManual(k.Flag)) keys.Add(k);
        foreach ((int Flag, int Step) k in _overlay.Keys) if (QuestDefinition.IsManual(k.Flag)) keys.Add(k);
        return keys
            .OrderBy(k => k.Flag).ThenBy(k => k.Step)
            .Select(k => Resolve(k.Flag, k.Step))
            .ToList();
    }

    // The no-overlay resolution for a quest: the seed entry if one exists, else a
    // blank auto-draft. Save compares each edited def against this to decide whether
    // the def is a genuine user delta worth writing.
    private QuestDefinition Baseline(int flag, int step) =>
        _seed.TryGetValue((flag, step), out QuestDefinition? seeded)
            ? Normalize(seeded)
            : new QuestDefinition(flag, step);

    private static QuestDefinition Normalize(QuestDefinition d) =>
        new(d.Flag, d.Step,
            (d.Name ?? string.Empty).Trim(),
            d.Visible,
            string.IsNullOrWhiteSpace(d.Steps) ? null : d.Steps,
            string.IsNullOrWhiteSpace(d.Rewards) ? null : d.Rewards,
            d.RequiredLevel,
            d.Blocked);

    // A manual row the user added but left wholly blank — nothing worth persisting.
    private static bool IsEmptyManual(QuestDefinition d) =>
        string.IsNullOrWhiteSpace(d.Name) && d.Steps is null && d.Rewards is null
        && d.RequiredLevel is null && !d.Blocked;

    private static bool SameContent(QuestDefinition a, QuestDefinition b) =>
        string.Equals(a.Name, b.Name, StringComparison.Ordinal)
        && a.Visible == b.Visible
        && string.Equals(a.Steps, b.Steps, StringComparison.Ordinal)
        && string.Equals(a.Rewards, b.Rewards, StringComparison.Ordinal)
        && a.RequiredLevel == b.RequiredLevel
        && a.Blocked == b.Blocked;

    private void LoadInto(Dictionary<(int Flag, int Step), QuestDefinition> target, string path, string label)
    {
        target.Clear();
        List<QuestDefinition>? defs;
        try
        {
            defs = JsonStore.Load<List<QuestDefinition>>(path);
        }
        catch (Exception ex)
        {
            // A hand-edited overlay/seed with malformed JSON shouldn't crash the
            // workshop — log and fall through to an empty layer.
            _log?.Warn("Quests", $"Failed to load {label} '{path}': {ex.Message}");
            return;
        }
        if (defs is null) return;
        foreach (QuestDefinition def in defs)
            target[(def.Flag, def.Step)] = def;
    }
}
