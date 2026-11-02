using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Inventory;
using MudPlay.Game.Remote;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Per-realm "which rooms currently hold this item" log for Roomba Mode,
// persisted to the realm's roomba_items.json. Fed by GhSweepManager every
// time it merges a fresh floor survey into a labeled gang-house room (recon,
// an Inventory-only lap, or the post-sort final recon pass); read by
// RoombaQueryHandler to answer @roomba <item>. Realm-scoped for the same reason
// GhRoomLabelStore is — one shared gang house per realm, so a sighting recorded
// by any character is visible to every other character on that realm. Mirrors
// RoomBlacklistStore's per-realm load/persist shape (OnRealmChanged + a Changed
// event).
//
// Keyed two levels deep — canonical item name, then RoomKey — because a gang
// house can legitimately stock the same item in several rooms at once (e.g.
// torches in both a lighting-supplies room and a catch-all overflow room); a
// query needs every room a sweep actually found it in, not just whichever was
// scanned most recently.
public sealed class GhItemLocationStore
{
    private readonly ItemNameStore _itemNames;
    private readonly LogService? _log;
    private string? _realmFolder;
    private readonly Dictionary<string, Dictionary<RoomKey, GhItemSighting>> _sightings =
        new(StringComparer.OrdinalIgnoreCase);

    // Fires after every mutation, including a realm reload.
    public event Action? Changed;

    public GhItemLocationStore(ItemNameStore itemNames, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(itemNames);
        _itemNames = itemNames;
        _log = log;
    }

    // Read-only snapshot of every (item, room) sighting currently on record,
    // most recent first.
    public IReadOnlyList<GhItemSighting> Sightings
        => _sightings.Values.SelectMany(byRoom => byRoom.Values)
            .OrderByDescending(s => s.SeenAt).ToList();

    // Freshest observation across the whole log — how current this gang house's
    // item data is. A sync merges in other clients' SeenAt (newest wins), so this
    // reflects the newest sighting from any source, not just local sweeps. Null
    // when the log is empty.
    public DateTimeOffset? LatestSeenAt
    {
        get
        {
            DateTimeOffset? newest = null;
            foreach (Dictionary<RoomKey, GhItemSighting> byRoom in _sightings.Values)
                foreach (GhItemSighting s in byRoom.Values)
                    if (newest is null || s.SeenAt > newest) newest = s.SeenAt;
            return newest;
        }
    }

    // Record `room`'s currently-known floor as-is: every item in `items` gets
    // (or refreshes) its (item, room) sighting, and any item this room USED to
    // hold but that isn't in this fresh list anymore has its sighting for THIS
    // room dropped — a room's entry always reflects the last survey actually
    // taken of it, not everything ever seen there. Other rooms' sightings of
    // the same item name are untouched either way. Called by GhSweepManager once
    // its reads of `room` on a visit are done (after the last search, or at once
    // where it isn't searching) — `items` is what the room holds, its visible
    // stacks and its hidden ones added (count-prefixed entries allowed; the
    // count becomes Quantity).
    public void RecordRoom(RoomKey room, IReadOnlyList<string> items) =>
        RecordRooms(new[] { (room, items) });

    // Several rooms at once, for one write of the file between them.
    public void RecordRooms(IReadOnlyList<(RoomKey Room, IReadOnlyList<string> Items)> rooms)
    {
        if (_realmFolder is null || rooms.Count == 0) return;
        TakeInOutsideChanges();

        DateTimeOffset now = DateTimeOffset.Now;
        foreach ((RoomKey room, IReadOnlyList<string> items) in rooms) Apply(room, items, now);
        Persist();
        Changed?.Invoke();
    }

    private void Apply(RoomKey room, IReadOnlyList<string> items, DateTimeOffset now)
    {
        HashSet<string> freshNames = new(StringComparer.OrdinalIgnoreCase);

        foreach (string entry in items)
        {
            (int count, _) = CountedCommand.SplitLeadingCount(entry);
            string canonical = GhSurveyMerger.Canonical(entry, _itemNames);
            freshNames.Add(canonical);

            if (!_sightings.TryGetValue(canonical, out Dictionary<RoomKey, GhItemSighting>? byRoom))
            {
                byRoom = new Dictionary<RoomKey, GhItemSighting>();
                _sightings[canonical] = byRoom;
            }
            byRoom[room] = new GhItemSighting
            {
                ItemName = canonical,
                Map = room.Map,
                Room = room.Room,
                SeenAt = now,
                Quantity = Math.Max(1, count),
            };
        }

        // Drop this room's stale entry for anything it previously held but
        // this fresh survey no longer shows — items is always the room's full
        // current accumulated knowledge (not a delta), so anything absent here
        // is genuinely gone from the floor. Prune an item name entirely once
        // no room holds it anymore, so a stale key doesn't linger empty.
        List<string> emptied = new();
        foreach ((string name, Dictionary<RoomKey, GhItemSighting> byRoom) in _sightings)
        {
            if (freshNames.Contains(name)) continue;
            if (byRoom.Remove(room) && byRoom.Count == 0) emptied.Add(name);
        }
        foreach (string name in emptied) _sightings.Remove(name);
    }

    // Resolve query (a player-typed item name, possibly partial or differently
    // worded) to every room currently holding a match, ordered by map/room.
    // Preference order: (1) canonical item-id match via ItemNameStore, so any
    // wording that resolves to the same game-data item hits regardless of how
    // it was recorded; (2) exact case-insensitive name match; (3) a substring
    // match against every tracked item NAME — a family of similarly-named
    // items (e.g. "severed head of goru-nezar", "severed head of darksong")
    // means a loose query like "severed" or "head" can match several distinct
    // items at once, so ALL of them come back (the caller groups by ItemName;
    // see RoombaQueryHandler) rather than this silently reporting nothing just
    // because more than one name matched. Empty only when nothing matches at
    // all.
    public IReadOnlyList<GhItemSighting> FindSightings(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<GhItemSighting>();

        if (_itemNames.FindByName(query) is int number)
        {
            string canonical = _itemNames.GetName(number) ?? query;
            if (_sightings.TryGetValue(canonical, out Dictionary<RoomKey, GhItemSighting>? byId))
                return Ordered(byId);
        }

        if (_sightings.TryGetValue(query, out Dictionary<RoomKey, GhItemSighting>? exact))
            return Ordered(exact);

        return _sightings.Keys
            .Where(name => name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .SelectMany(name => Ordered(_sightings[name]))
            .ToList();
    }

    private static IReadOnlyList<GhItemSighting> Ordered(Dictionary<RoomKey, GhItemSighting> byRoom)
        => byRoom.Values.OrderBy(s => s.Map).ThenBy(s => s.Room).ToList();

    // This client's sightings encoded for the @roomba sync wire — only the ones
    // whose canonical name resolves back to an item record number (the sync
    // format has no name fallback; an unresolvable name can't happen for
    // anything Roomba itself recorded, since GhSurveyMerger.Canonical only
    // returns a game-data name when ItemNameStore can already resolve it back).
    public IReadOnlyList<GhItemSyncRecord> ToSyncRecords()
    {
        List<GhItemSyncRecord> records = new();
        foreach ((string canonical, Dictionary<RoomKey, GhItemSighting> byRoom) in _sightings)
        {
            if (_itemNames.FindByName(canonical) is not int number) continue;
            foreach (GhItemSighting s in byRoom.Values)
                records.Add(new GhItemSyncRecord(s.Map, s.Room, number, s.Quantity, s.SeenAt));
        }
        return records;
    }

    // Merge sightings received from another MudPlay client's @roomba sync
    // reply. Newest SeenAt per (item, room) wins — unlike a boss kill time
    // there's no meaningful "conflict" for a room-contents sighting, so this
    // never needs a user-facing merge decision, just silent adoption of
    // whichever side saw that room more recently. Records naming an item
    // number outside our active game-data set are skipped (nothing to resolve
    // a name from). Returns the count actually applied, for the program log.
    public int MergeSyncRecords(IReadOnlyList<GhItemSyncRecord> records)
    {
        if (_realmFolder is null) return 0;
        TakeInOutsideChanges();
        int applied = 0;
        foreach (GhItemSyncRecord r in records)
        {
            // Reject implausible coordinates / stack sizes before they reach the
            // store. The codec already rejects out-of-int wire values, but a peer's
            // payload can still name an in-range but nonsensical room; bound it so a
            // crafted or corrupt sighting can't pollute @roomba / the Master List.
            if (r.Map is <= 0 or > 100_000 || r.Room is <= 0 or > 1_000_000) continue;
            string? name = _itemNames.GetName(r.ItemNumber);
            if (name is null) continue;
            RoomKey room = new(r.Map, r.Room);
            int quantity = Math.Clamp(r.Quantity, 1, 1_000_000);

            if (!_sightings.TryGetValue(name, out Dictionary<RoomKey, GhItemSighting>? byRoom))
            {
                byRoom = new Dictionary<RoomKey, GhItemSighting>();
                _sightings[name] = byRoom;
            }
            if (byRoom.TryGetValue(room, out GhItemSighting? existing) && existing.SeenAt >= r.SeenAt) continue;

            byRoom[room] = new GhItemSighting
            {
                ItemName = name,
                Map = r.Map,
                Room = r.Room,
                Quantity = quantity,
                SeenAt = r.SeenAt,
            };
            applied++;
        }
        if (applied > 0) { Persist(); Changed?.Invoke(); }
        return applied;
    }

    // Load the item-sighting log for the active realm. Called by AppServices on
    // ProfileService.ProfileLoaded / BbsPinApplied with the active realm's folder;
    // resets the in-memory store when there's none (null / blank).
    public void OnRealmChanged(string? realmFolder)
    {
        if (string.IsNullOrWhiteSpace(realmFolder))
        {
            if (_realmFolder is not null)
            {
                _realmFolder = null;
                _sightings.Clear();
                Changed?.Invoke();
            }
            return;
        }

        _realmFolder = realmFolder;
        Load();
        Changed?.Invoke();
    }

    private readonly SharedFileStamp _stamp = new();

    // Re-read the sightings when another client on the realm has written them: every
    // character on it sweeps into the one file, and the room a character saw last
    // is what stands for that room. Called on the heartbeat and before a change.
    public bool TakeInOutsideChanges()
    {
        if (_realmFolder is null) return false;
        if (!_stamp.ChangedOutside(AppPaths.RealmRoombaItemsFile(_realmFolder))) return false;
        Load();
        _log?.Debug("GhSweep", "item sightings re-read: another client on this realm changed them");
        Changed?.Invoke();
        return true;
    }

    private void Load()
    {
        _sightings.Clear();
        if (_realmFolder is null) return;
        string path = AppPaths.RealmRoombaItemsFile(_realmFolder);
        _stamp.Mark(path);
        List<GhItemSighting>? loaded = JsonStore.Load<List<GhItemSighting>>(path);
        if (loaded is null) return;
        // Keyed by item name: a hand-edited file's null sighting or name is passed over.
        int dropped = loaded.RemoveAll(s => s?.ItemName is null);
        if (dropped > 0)
            _log?.Warn("GhSweep", $"'{path}': left out {dropped} item sighting(s) with no item name.");
        foreach (GhItemSighting s in loaded)
        {
            if (!_sightings.TryGetValue(s.ItemName, out Dictionary<RoomKey, GhItemSighting>? byRoom))
            {
                byRoom = new Dictionary<RoomKey, GhItemSighting>();
                _sightings[s.ItemName] = byRoom;
            }
            byRoom[new RoomKey(s.Map, s.Room)] = s;
        }
    }

    private void Persist()
    {
        if (_realmFolder is null) return;
        List<GhItemSighting> flat = _sightings.Values.SelectMany(byRoom => byRoom.Values).ToList();
        string path = AppPaths.RealmRoombaItemsFile(_realmFolder);
        JsonStore.Save(path, flat);
        _stamp.Mark(path);
    }
}
