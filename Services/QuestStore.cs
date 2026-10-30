using MudPlay.Game.Quests;
using MudPlay.Models.Profile;

namespace MudPlay.Services;

// Loads and resolves quest definitions. Two layers merge per (flag, step) in
// priority order:
//   1. the user's overlay, the active game-data set's quests.json — display name,
//      show/hide visibility, edited step markdown. A guide describes the game the
//      set holds, so it is the set's (it wins over the seed) and every realm and
//      character on the set shares it; which quests a character has finished is
//      kept on the character (CharacterProfile.QuestLog). The overlay was kept per
//      realm for a while: a set takes in each realm's old file once, as that realm
//      loads (the first one whole, later ones for the quests the set lacks);
//   2. the universal read-only seed QuestDefs.seed.json in Global, built
//      from the bundled Defaults seed, keyed by game-data flag numbers (custom
//      realms reuse the numbers) so a curated default ports across every board;
//   3. an auto-draft (blank name, shown, no edited steps) for any quest the
//      crawler discovers that neither layer names yet.
// A quest's displayed name (DisplayName, FlagName, NamedQuests) also takes the
// realm's own name, which a server sends over GMCP: it ranks below a name the user
// gave and above the seed's. It is held apart from both layers and never saved, so
// a realm's name is never taken for a user edit, and it goes with the session.
// The seed is never written. The mechanical data — ordered steps + stat bonuses —
// is crawled from the active set's TBInfo elsewhere; this store owns only the
// user/seed text layer.
//
// The overlay also holds two user-driven extras: manual quests the crawler
// never finds (identity in the reserved QuestDefinition.ManualFlagBase flag
// range, persisted verbatim since they have no crawl baseline — see
// ManualQuests), and a QuestDefinition.Blocked flag that suppresses a
// spuriously-crawled quest from the journal in sets where it shouldn't appear.
public sealed class QuestStore
{
    private readonly LogService? _log;
    private readonly string _seedPath;
    private readonly Func<string?>? _activeSet;
    private readonly Func<string?>? _activeRealmFolder;
    private readonly Dictionary<(int Flag, int Step), QuestDefinition> _seed = new();
    private readonly Dictionary<(int Flag, int Step), QuestDefinition> _overlay = new();
    private readonly SharedFileStamp _stamp = new();
    // Swapped whole, never changed in place, so a reader holds a consistent list.
    private IReadOnlyList<ServerQuestName> _server = Array.Empty<ServerQuestName>();

    // The game-data set whose overlay is loaded, or null when none.
    public string? ActiveSet { get; private set; }

    // The connected realm's quest names; empty when it sends none.
    public IReadOnlyList<ServerQuestName> ServerNames => _server;

    // Raised after the overlay reloads (set change, a realm's old file taken in,
    // another client's save) or the realm's names change, so consumers re-resolve
    // their displayed quest text.
    public event Action? Reloaded;

    // Production ctor: seed from Global; the overlay tracks the profile's
    // game-data set, reloading on ProfileService.ProfileLoaded / BbsPinApplied /
    // ProfileClosed. Without a ProfileService the overlay moves only on
    // OnActiveSetChanged. seedPath defaults to AppPaths.DefaultQuestDefsSeedFile.
    public QuestStore(ProfileService? profile = null, Func<string?>? activeSet = null,
                      Func<string?>? activeRealmFolder = null, LogService? log = null, string? seedPath = null)
    {
        _log = log;
        _activeSet = activeSet;
        _activeRealmFolder = activeRealmFolder;
        _seedPath = seedPath ?? AppPaths.DefaultQuestDefsSeedFile;
        LoadInto(_seed, _seedPath, "seed");
        if (profile is not null)
        {
            profile.ProfileLoaded += _ => ReloadForProfile();
            profile.BbsPinApplied += _ => ReloadForProfile();
            profile.ProfileClosed += ReloadForProfile;
        }
        ReloadForProfile();
    }

    private void ReloadForProfile() => OnActiveSetChanged(_activeSet?.Invoke(), _activeRealmFolder?.Invoke());

    // Swap the loaded overlay to the set's quests.json (empty when the set has none
    // yet, or the name is blank), first taking in realmFolder's old per-realm file
    // when the set hasn't had it.
    public void OnActiveSetChanged(string? setName, string? realmFolder = null)
    {
        _overlay.Clear();
        ActiveSet = string.IsNullOrWhiteSpace(setName) ? null : setName;
        if (ActiveSet is not null)
        {
            TakeInRealmFile(ActiveSet, realmFolder);
            Load();
        }
        Reloaded?.Invoke();
    }

    // Re-read the overlay when another client on the set has saved it. Called on
    // the heartbeat. True when anything was re-read.
    public bool TakeInOutsideChanges()
    {
        if (ActiveSet is null || !_stamp.ChangedOutside(AppPaths.QuestsFile(ActiveSet))) return false;
        Load();
        Reloaded?.Invoke();
        return true;
    }

    private void Load()
    {
        string path = AppPaths.QuestsFile(ActiveSet!);
        _stamp.Mark(path);
        LoadInto(_overlay, path, "overlay");
    }

    // A realm's guide edits from when they were kept per realm. The first realm a
    // set takes in replaces the set's own file, which dates from before the guides
    // went to the realm and is older than any realm's; a later realm only adds the
    // quests the set still lacks, so the first one's edits stand.
    private void TakeInRealmFile(string setName, string? realmFolder)
    {
        if (string.IsNullOrWhiteSpace(realmFolder)) return;
        string realmFile = AppPaths.LegacyRealmQuestsFile(realmFolder);
        if (!System.IO.File.Exists(realmFile)) return;

        string marker = AppPaths.QuestsAdoptedRealmsFile(setName);
        try
        {
            List<string> taken = JsonStore.Load<List<string>>(marker) ?? new List<string>();
            if (taken.Contains(realmFolder, StringComparer.OrdinalIgnoreCase)) return;

            Dictionary<(int Flag, int Step), QuestDefinition> theirs = new();
            LoadInto(theirs, realmFile, "realm overlay");
            Dictionary<(int Flag, int Step), QuestDefinition> ours = new();
            string setFile = AppPaths.QuestsFile(setName);
            if (taken.Count > 0) LoadInto(ours, setFile, "overlay");
            else if (System.IO.File.Exists(setFile))
                System.IO.File.Copy(setFile, setFile + ".bak", overwrite: true);

            foreach (((int Flag, int Step) key, QuestDefinition def) in theirs) ours.TryAdd(key, def);
            JsonStore.Save(setFile, ours.Values.OrderBy(d => d.Flag).ThenBy(d => d.Step).ToList());
            taken.Add(realmFolder);
            JsonStore.Save(marker, taken);
            _log?.Info("Quests", $"quest guides from '{realmFolder}' taken into game-data set '{setName}'");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                       or System.IO.InvalidDataException)
        {
            // Left for the next load; the set's own overlay still loads.
            _log?.Warn("Quests", $"couldn't take in the quest guides from '{realmFolder}': {ex.Message}");
        }
    }

    // Resolve the effective definition for a quest: the user overlay if it names
    // this (flag, step); else the universal seed; else a blank-named, visible
    // auto-draft. Never returns null.
    public QuestDefinition Resolve(int flag, int step)
    {
        (int Flag, int Step) key = (flag, step);
        if (_overlay.TryGetValue(key, out QuestDefinition? user)) return user;
        if (_seed.TryGetValue(key, out QuestDefinition? seeded)) return seeded;
        return new QuestDefinition(flag, step);
    }

    // The realm's quest names, replacing any held; an empty list clears them.
    public void ApplyServerNames(IReadOnlyList<ServerQuestName> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.SequenceEqual(_server)) return;
        _server = names.ToArray();
        _log?.Info("Quests", _server.Count == 0
            ? "the realm's quest names cleared"
            : $"the realm named {_server.Count} quest range(s) over {_server.Select(n => n.Flag).Distinct().Count()} flag(s)");
        Reloaded?.Invoke();
    }

    // The name a quest is shown under: the user's own, else the realm's, else the
    // seed's unless the user cleared it; empty when none names it (the caller builds
    // a title from the flag). startValue is the flag value the quest's stage starts
    // at (a band's CrawledQuest.StepRangeStart), which the realm's ranges are in; 0
    // for a quest that is the whole flag.
    public string DisplayName(int flag, int step, int startValue) =>
        UserName(flag, step) is { Length: > 0 } user ? user
        : ServerName(flag, startValue) ?? ShownSeedName(flag, step);

    // One name for the whole flag, when only the flag is known (the @quest reply):
    // a name the user gave any of its steps, else the realm's for the range the
    // character's flag value is in (its lowest range before a value is read), else
    // the seed's. log is the character's quest log; its steps for the flag may be
    // ones neither layer lists.
    public string FlagName(int flag, IEnumerable<QuestProgress> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        List<QuestProgress> mine = log.Where(p => p.Flag == flag).ToList();
        List<int> steps = StepsForFlag(flag).Concat(mine.Select(p => p.Step)).Distinct().ToList();
        int reached = mine.Select(p => p.FlagValue ?? 0).DefaultIfEmpty(0).Max();
        return steps.Select(s => UserName(flag, s)).FirstOrDefault(n => n.Length > 0)
            ?? (reached > 0 ? ServerName(flag, reached) : null)
            ?? ServerName(flag, 0)
            ?? steps.Select(s => ShownSeedName(flag, s)).FirstOrDefault(n => n.Length > 0)
            ?? string.Empty;
    }

    // The realm's name for the range that holds value, or for a whole-flag lookup
    // (value 0) the range starting lowest, whatever order the server listed them in.
    private string? ServerName(int flag, int value)
    {
        ServerQuestName? lowest = null;
        foreach (ServerQuestName name in _server)
        {
            if (name.Flag != flag) continue;
            if (name.Covers(value)) return name.Name;
            if (lowest is not { } low || name.From < low.From) lowest = name;
        }
        return value > 0 ? null : lowest?.Name;
    }

    // A name in the overlay counts as the user's only when it isn't the seed's: an
    // edit to anything else on a seeded quest saves the seed's name with it, and
    // that copy mustn't hide the realm's name.
    private string UserName(int flag, int step)
    {
        if (!_overlay.TryGetValue((flag, step), out QuestDefinition? user) || string.IsNullOrWhiteSpace(user.Name))
            return string.Empty;
        string name = user.Name.Trim();
        return string.Equals(name, SeedName(flag, step), StringComparison.Ordinal) ? string.Empty : name;
    }

    private string SeedName(int flag, int step) =>
        _seed.TryGetValue((flag, step), out QuestDefinition? seeded) && !string.IsNullOrWhiteSpace(seeded.Name)
            ? seeded.Name.Trim()
            : string.Empty;

    // The seed's name unless the user cleared it: the editor saves a blank name only
    // when the user emptied the box (or typed the auto-draft title back), so the quest
    // goes back to its flag's title. The clearing was of the seed's name, the only
    // one the editor offers, so the realm's name still ranks above it.
    private string ShownSeedName(int flag, int step) =>
        _overlay.TryGetValue((flag, step), out QuestDefinition? user) && string.IsNullOrWhiteSpace(user.Name)
            ? string.Empty
            : SeedName(flag, step);

    // Persist the user's edited definitions to the active set's overlay
    // (its quests.json) and refresh the in-memory layer so later
    // Resolve calls see the edits immediately. The overlay stays a delta: a
    // definition that matches what Resolve would return with no overlay (the seed
    // entry, or a blank auto-draft) is dropped rather than frozen into the file,
    // so a later seed update still flows through for untouched quests. No-op when
    // no set is active.
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
            string path = AppPaths.QuestsFile(ActiveSet);
            JsonStore.Save(path, list);
            _stamp.Mark(path);
        }
        catch (Exception ex)
        {
            // A failed write (permissions, disk) shouldn't crash the editor — the
            // in-memory overlay still reflects the edits for this session.
            _log?.Warn("Quests", $"Failed to save overlay for '{ActiveSet}': {ex.Message}");
        }
    }

    // Every user-added (manual) quest the store knows for the active set,
    // resolved (overlay over seed) and ordered by flag. These carry no crawl
    // backing, so the Quest Status tab and editor materialize them straight from
    // the definition.
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

    // Ordered distinct step (band) values known for a flag across seed + overlay.
    // Drives the ordinal band numbering in the @quest report — the Nth step in this
    // list is band N. Empty when the flag is unknown to both layers.
    public IReadOnlyList<int> StepsForFlag(int flag)
    {
        SortedSet<int> steps = new();
        foreach ((int Flag, int Step) k in _seed.Keys) if (k.Flag == flag) steps.Add(k.Step);
        foreach ((int Flag, int Step) k in _overlay.Keys) if (k.Flag == flag) steps.Add(k.Step);
        return steps.ToList();
    }

    // Every named quest known as (flag, name) — the candidate set the @quest name
    // resolver matches against, in DisplayName's order: the user's names, the
    // realm's, then the seed's for the quests the user didn't rename.
    public IEnumerable<(int Flag, string Name)> NamedQuests()
    {
        HashSet<(int Flag, int Step)> keys = new(_seed.Keys);
        foreach ((int Flag, int Step) k in _overlay.Keys) keys.Add(k);
        List<(int Flag, string Name)> seeded = new();
        foreach ((int Flag, int Step) k in keys)
        {
            if (UserName(k.Flag, k.Step) is { Length: > 0 } user) yield return (k.Flag, user);
            else if (ShownSeedName(k.Flag, k.Step) is { Length: > 0 } seed) seeded.Add((k.Flag, seed));
        }
        foreach (ServerQuestName name in _server) yield return (name.Flag, name.Name);
        foreach ((int Flag, string Name) seed in seeded) yield return seed;
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
            d.Blocked,
            d.CompleteValueOverride)
        { ClassRestrict = d.ClassRestrict is { Count: > 0 } ? d.ClassRestrict : null };

    // A manual row the user added but left wholly blank — nothing worth persisting.
    private static bool IsEmptyManual(QuestDefinition d) =>
        string.IsNullOrWhiteSpace(d.Name) && d.Steps is null && d.Rewards is null
        && d.RequiredLevel is null && !d.Blocked && d.CompleteValueOverride is null
        && (d.ClassRestrict is null || d.ClassRestrict.Count == 0);

    private static bool SameContent(QuestDefinition a, QuestDefinition b) =>
        string.Equals(a.Name, b.Name, StringComparison.Ordinal)
        && a.Visible == b.Visible
        && string.Equals(a.Steps, b.Steps, StringComparison.Ordinal)
        && string.Equals(a.Rewards, b.Rewards, StringComparison.Ordinal)
        && a.RequiredLevel == b.RequiredLevel
        && a.Blocked == b.Blocked
        && a.CompleteValueOverride == b.CompleteValueOverride
        && SameClassRestrict(a.ClassRestrict, b.ClassRestrict);

    // Order-insensitive equality of two class-restriction lists, treating null and
    // empty alike (neither is a restriction).
    private static bool SameClassRestrict(List<int>? a, List<int>? b)
    {
        int ca = a?.Count ?? 0, cb = b?.Count ?? 0;
        if (ca == 0) return cb == 0;
        return ca == cb && a!.OrderBy(x => x).SequenceEqual(b!.OrderBy(x => x));
    }

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
