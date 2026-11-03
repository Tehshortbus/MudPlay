using System.Collections.Generic;
using System.Linq;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Per-realm gang-house (GH) room label set for Roomba Mode, persisted to the
// realm's roomba.json. Every character on a realm shares the same gang house, so
// labels + the sweep-tuning knobs are realm-wide: label a room once on any
// character and every other character on that realm sees it too. Mirrors
// RoomBlacklistStore's per-realm load/persist shape (OnRealmChanged + a Changed
// event), not SettingsResolver's tiered-delta merge — this is a single file, not
// a per-tab settings override.
public sealed class GhRoomLabelStore
{
    // Default when RoombaSettings.SearchesPerRoom is unset.
    public const int DefaultSearchesPerRoom = 3;

    private readonly ProfileService _profile;
    private readonly LogService? _log;
    private string? _realmFolder;
    private RoombaSettings _settings = new();
    private readonly Dictionary<RoomKey, GhRoomLabel> _labels = new();

    // Fires after every mutation, including a realm reload.
    public event Action? Changed;

    // profile is retained only to read (and, once, clear) the legacy
    // per-character GH fields during the one-time migration below — the store's
    // own state is otherwise entirely realm-scoped.
    public GhRoomLabelStore(ProfileService profile, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _log = log;
    }

    // Read-only snapshot of the currently-labeled rooms.
    public IReadOnlyCollection<GhRoomLabel> Labels => _labels.Values;

    // How many times each room on the expanded circuit is searched per recon visit
    // (only used when hidden-item search is on). Unset reads as the default. Recon
    // always walks the circuit exactly once — there is no lap-count setting.
    public int SearchesPerRoom => Math.Max(1, _settings.SearchesPerRoom ?? DefaultSearchesPerRoom);

    // Whether recon searches (`sea`) each room for hidden items. Off by default:
    // Roomba sorts only the visible floor unless the user opts in.
    public bool SearchForHidden => _settings.SearchForHidden ?? false;

    public void SetSearchesPerRoom(int count)
    {
        if (_realmFolder is null) return;
        TakeInOutsideChanges();
        _settings.SearchesPerRoom = Math.Max(1, count);
        Persist();
        _log?.Info("GhSweep", $"searches per room set to {_settings.SearchesPerRoom}");
        Changed?.Invoke();
    }

    public void SetSearchForHidden(bool on)
    {
        if (_realmFolder is null) return;
        TakeInOutsideChanges();
        _settings.SearchForHidden = on;
        Persist();
        _log?.Info("GhSweep", $"search for hidden items {(on ? "enabled" : "disabled")}");
        Changed?.Invoke();
    }

    // Canonical names of the items a sweep never picks up, compared ignoring case.
    public IReadOnlyCollection<string> IgnoredItems => _ignored;
    private HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase);

    public bool IsIgnored(string itemName) => _ignored.Contains(itemName);

    public void SetIgnored(string itemName, bool ignored)
    {
        if (_realmFolder is null || string.IsNullOrWhiteSpace(itemName)) return;
        TakeInOutsideChanges();
        if (!(ignored ? _ignored.Add(itemName) : _ignored.Remove(itemName))) return;

        _settings.IgnoredItems = _ignored.Count == 0
            ? null
            : _ignored.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        Persist();
        _log?.Info("GhSweep", $"{(ignored ? "ignoring" : "no longer ignoring")} '{itemName}'");
        Changed?.Invoke();
    }

    public bool TryGetLabel(RoomKey key, out GhRoomLabel label)
        => _labels.TryGetValue(key, out label!);

    // Set (or replace) key's label. Any number of rooms may be catch-alls: they
    // form an overflow chain, tried in order, so a house whose first overflow room
    // fills up has somewhere else to put things. It used to be single-owner
    // (ticking one silently un-ticked the last), which meant a user who marked ten
    // rooms ended up with one and no indication the other nine were discarded.
    // Persists immediately.
    public void SetLabel(RoomKey key, IReadOnlyList<GhCategoryRule> rules, bool isCatchAll)
    {
        if (_realmFolder is null) return;
        TakeInOutsideChanges();

        GhRoomLabel label = new(key.Map, key.Room) { Rules = rules.ToList(), IsCatchAll = isCatchAll };
        _labels[key] = label;

        _settings.RoomLabels ??= new List<GhRoomLabel>();
        _settings.RoomLabels.RemoveAll(l => l.Map == key.Map && l.Room == key.Room);
        _settings.RoomLabels.Add(label);
        Persist();
        _log?.Info("GhSweep",
            $"labeled {key}: {rules.Count} rule(s){(isCatchAll ? " [catch-all]" : "")}");
        Changed?.Invoke();
    }

    // Clear key's label. Persists immediately.
    public void ClearLabel(RoomKey key)
    {
        if (_realmFolder is null) return;
        TakeInOutsideChanges();
        if (!_labels.Remove(key)) return;

        _settings.RoomLabels?.RemoveAll(l => l.Map == key.Map && l.Room == key.Room);
        Persist();
        _log?.Info("GhSweep", $"cleared label {key}");
        Changed?.Invoke();
    }

    // Adopt gang-house room labels received via @roomba sync — ADD-IF-ABSENT, so a
    // room the receiver has already labeled keeps its own (a shared gang house is
    // the same for everyone, and this way a re-sync never clobbers local edits).
    // Fires Changed once. Returns the
    // count adopted, for the program log.
    public int MergeSyncLabels(IReadOnlyList<GhRoomLabel> incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (_realmFolder is null || incoming.Count == 0) return 0;
        TakeInOutsideChanges();

        int added = 0;
        foreach (GhRoomLabel lbl in incoming)
        {
            if (lbl.Map <= 0 || lbl.Room <= 0) continue;
            RoomKey key = new(lbl.Map, lbl.Room);
            if (_labels.ContainsKey(key)) continue;   // don't overwrite a local label

            GhRoomLabel adopted = new(key.Map, key.Room)
            {
                Rules = lbl.Rules ?? new List<GhCategoryRule>(),
                IsCatchAll = lbl.IsCatchAll,
            };
            _labels[key] = adopted;
            _settings.RoomLabels ??= new List<GhRoomLabel>();
            _settings.RoomLabels.RemoveAll(l => l.Map == key.Map && l.Room == key.Room);
            _settings.RoomLabels.Add(adopted);
            added++;
        }

        if (added > 0)
        {
            Persist();
            _log?.Info("GhSweep", $"adopted {added} gang-house room label(s) from @roomba sync");
            Changed?.Invoke();
        }
        return added;
    }

    // Load the Roomba settings for the active realm. Called by AppServices on
    // ProfileService.ProfileLoaded / BbsPinApplied with the active realm's folder;
    // resets the in-memory store when there's none (null / blank).
    public void OnRealmChanged(string? realmFolder)
    {
        if (string.IsNullOrWhiteSpace(realmFolder))
        {
            if (_realmFolder is not null)
            {
                _realmFolder = null;
                _settings = new RoombaSettings();
                _labels.Clear();
                _ignored.Clear();
                Changed?.Invoke();
            }
            return;
        }

        _realmFolder = realmFolder;
        Load();
        MigrateLegacyCharacterData();
        RebuildLabelIndex();
        Changed?.Invoke();
    }

    private readonly SharedFileStamp _stamp = new();

    // Re-read the labels and sweep settings when another client on the realm has
    // saved them. Called on the heartbeat and before a change.
    public bool TakeInOutsideChanges()
    {
        if (_realmFolder is null) return false;
        if (!_stamp.ChangedOutside(AppPaths.RealmRoombaFile(_realmFolder))) return false;
        Load();
        RebuildLabelIndex();
        _log?.Debug("GhSweep", "room labels re-read: another client on this realm changed them");
        Changed?.Invoke();
        return true;
    }

    private void Load()
    {
        string path = AppPaths.RealmRoombaFile(_realmFolder!);
        _stamp.Mark(path);
        _settings = JsonStore.Load<RoombaSettings>(path) ?? new RoombaSettings();
    }

    // One-time lift of a pre-upgrade character's GH data into this realm's
    // roomba.json: only runs when the realm file is still empty AND the currently
    // loaded character profile still carries the legacy fields. Clears the
    // character-tier copy afterward (and saves) so this never re-fires and the
    // old data doesn't linger duplicated in two places.
    //
    // First-writer-wins by design: if several characters on one realm each labeled
    // rooms before the per-character → shared move, the first to load seeds the
    // shared file and later characters' distinct labels aren't merged in (their
    // legacy fields just sit unread). Collapsing per-char data into one shared
    // gang house can't preserve conflicting sets, and in practice one character
    // does the labeling — an accepted tradeoff, not a bug to reconcile.
    private void MigrateLegacyCharacterData()
    {
        if (_settings.RoomLabels is { Count: > 0 }) return;   // realm file already has real data
        if (_profile.Current is not { } current) return;
        if (current.GhRoomLabels is not { Count: > 0 } legacyLabels) return;

        _settings.RoomLabels = legacyLabels;
        _settings.SearchesPerRoom = current.GhSearchesPerRoom;
        _settings.SearchForHidden = current.GhSearchForHidden;
        Persist();

        current.GhRoomLabels = null;
        current.GhSearchesPerRoom = null;
        current.GhSearchForHidden = null;
        _profile.Save();

        _log?.Info("GhSweep",
            $"migrated {legacyLabels.Count} legacy per-character GH room label(s) to '{_realmFolder}'");
    }

    private void RebuildLabelIndex()
    {
        _labels.Clear();
        if (_settings.RoomLabels is { } list)
            foreach (GhRoomLabel l in list) _labels[new RoomKey(l.Map, l.Room)] = l;
        _ignored = new HashSet<string>(_settings.IgnoredItems ?? [], StringComparer.OrdinalIgnoreCase);
    }

    private void Persist()
    {
        if (_realmFolder is null) return;
        string path = AppPaths.RealmRoombaFile(_realmFolder);
        JsonStore.Save(path, _settings);
        _stamp.Mark(path);
    }
}
