using System.Collections.Generic;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

// Per-character avoided + stash room set. Implements IRoomFilter for BFS —
// the walker / loop runner / auto-lair scheduler all read IsAvoided at
// planning time so the avoided rooms are dropped from candidate paths.
//
// Scope: Char-only. Lives on CharacterProfile.AvoidedRooms +
// CharacterProfile.StashRooms, not in SettingsResolver — the avoided set is a
// personal no-go list, not a per-realm or per-BBS rule.
//
// Wiring: AppServices subscribes the filter to
// ProfileService.ProfileLoaded + ProfileService.ProfileClosed. Mutating
// methods (MarkAvoided, UnmarkAvoided, MarkStash, UnmarkStash) update the
// in-memory set, mirror the change back into the loaded profile, persist via
// ProfileService.Save, and fire AvoidedChanged / StashChanged so the map UI
// can recolour the affected cells.
public sealed class MovementFilter : IRoomFilter
{
    private readonly ProfileService _profile;
    private readonly LogService? _log;
    private readonly HashSet<RoomKey> _avoided = new();
    private readonly HashSet<RoomKey> _stash = new();

    // Supplies the player's current character level for Form-A exit
    // level-gate evaluation, or null when the level isn't known yet (no stat
    // screen parsed). Wired by AppServices to StatParser. When null,
    // IsExitBlocked never blocks — we don't refuse a walk on a gate we can't
    // yet evaluate.
    public Func<int?>? LevelProvider { get; set; }

    // Supplies the party's most-constraining (Low, High) level window when
    // this character is leading a party, or null when solo, not leading, or
    // nobody's level is known yet. Wired by AppServices to
    // Game.Remote.PartyLevelTracker. When non-null it takes precedence over
    // LevelProvider in IsExitBlocked: BFS routes the party around a gate that
    // would leave a member behind, instead of walking the leader through it.
    // The bounds already fold in the leader's own level, so the party branch
    // never waves the leader through a gate the leader can't cross either.
    public Func<(int Low, int High)?>? PartyLevelBoundsProvider { get; set; }

    // Supplies the player's current on-hand wealth in copper farthings (the
    // consolidated `Wealth:` value), or null when it isn't known yet (no
    // inventory parsed). Wired by AppServices to the live currency snapshot.
    // A (Toll: N) exit needs N*100 copper-value carried to cross (confirmed
    // mechanic — the game phrases the bar as "N gold crowns" but any coin mix
    // totalling that value passes), so IsExitBlocked routes around a toll we
    // can't afford. When null we don't gate — same rule as an unknown level.
    public Func<long?>? WealthProvider { get; set; }

    // Supplies the party's minimum on-hand wealth (copper) when this character
    // is leading a party, or null when solo, not leading, or our own wallet is
    // unknown. Wired by AppServices to
    // Game.Remote.PartyWealthTracker. When non-null it takes precedence over
    // WealthProvider in IsTollGateBlocked: BFS routes the party around a toll a
    // member can't afford, instead of walking the leader through and stranding
    // them at the gate. A toll is per-crosser, so this is a genuine second gate
    // over the self-only wallet check. Demand-driven — the tracker only probes
    // @wealth when this is invoked, i.e. only while BFS evaluates a toll exit,
    // and treats a member who hasn't reported fresh wealth as unaffordable.
    public Func<long?>? PartyWealthProvider { get; set; }

    // Supplies the player's own class Number (Classes.Number, 1-15), or null
    // when the class isn't parsed yet. Wired by AppServices to the live
    // PlayerStats.Class resolved through the Classes table. A "(Class: N OK)"
    // exit only admits class N (confirmed single-class gate, not a bitmask), so
    // IsExitBlocked routes around a class hall we can't enter. Evaluated against
    // the controlling character's own class only — a party's members may be
    // different classes, but class halls are single-class by design and a party
    // doesn't loop through one together, so there's no party-wide branch here.
    // When null we don't gate — same rule as an unknown level.
    public Func<int?>? ClassNumberProvider { get; set; }

    // Fires the party @wealth round-trip. Wired by AppServices to
    // PartyWealthTracker.Probe. Invoked only from WarmForRoute, and only when
    // the tolls-permitted shortest route actually crosses a toll — so an
    // off-path toll edge inside the BFS frontier never triggers a poll.
    public Action? TollWealthProbe { get; set; }

    // While set, IsTollGateBlocked stands down (treats every toll as
    // crossable). WarmForRoute flips this on to plan the route the party WOULD
    // take if every toll were affordable, so it can tell whether a toll is
    // genuinely on the path before deciding to poll. Single-threaded planning
    // use — set and cleared inside WarmForRoute's own try/finally.
    private bool _tollGateSuspended;

    // Read-only snapshot of the currently-avoided room keys.
    public IReadOnlyCollection<RoomKey> Avoided => _avoided;

    // Read-only snapshot of the currently-flagged stash-room keys.
    public IReadOnlyCollection<RoomKey> Stash => _stash;

    // Fires after every mutation to the avoided set, including profile reload.
    public event Action? AvoidedChanged;

    // Fires after every mutation to the stash set, including profile reload.
    public event Action? StashChanged;

    public MovementFilter(ProfileService profile, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _log = log;

        _profile.ProfileLoaded  += OnProfileLoaded;
        _profile.ProfileClosed  += OnProfileClosed;

        // Pick up the already-loaded profile, if any (AppServices
        // wires this filter after ProfileService.LoadBlank fires).
        if (_profile.Current is { } current) OnProfileLoaded(current);
    }

    public bool IsAvoided(RoomKey key) => _avoided.Contains(key);

    // An exit is non-traversable for planning when its level window excludes
    // the crosser, it's a toll the crosser can't afford, or it's a class gate
    // for a class we aren't. The gate kinds are independent (a toll exit
    // carries Hint=Toll; a level or class gate is a plain cardinal carrying a
    // window / allowed-class), so each is checked.
    public bool IsExitBlocked(in RoomExit exit) =>
        IsLevelGateBlocked(in exit) || IsTollGateBlocked(in exit) || IsClassGateBlocked(in exit);

    // A "(Class: N OK)" exit only admits class Number N. Gate only when our
    // own class is known — an unparsed class never refuses a walk on a gate we
    // can't yet evaluate.
    private bool IsClassGateBlocked(in RoomExit exit)
    {
        if (!exit.HasClassGate) return false;
        if (ClassNumberProvider?.Invoke() is not { } myClass) return false;
        return myClass != exit.ClassGate;
    }

    private bool IsLevelGateBlocked(in RoomExit exit)
    {
        if (!exit.HasLevelGate) return false;

        // Party branch: when leading a party, route around a gate the
        // whole party can't clear so no member is left behind. The bounds
        // fold in the leader's own level, so this also covers the leader.
        // Falls through to the self-only branch when no bounds are known
        // (solo, not leading, or nobody's level parsed yet).
        if (PartyLevelBoundsProvider?.Invoke() is { } bounds)
        {
            if (exit.MinLevel > 0 && bounds.Low < exit.MinLevel) return true;
            if (exit.MaxLevel > 0 && bounds.High > exit.MaxLevel) return true;
            return false;
        }

        if (LevelProvider?.Invoke() is not { } level) return false;  // level unknown → don't gate

        // Form-A window: MinLevel>0 is a floor, MaxLevel>0 is a cap.
        // 0 in either slot means "no bound on that side" (the MDB's
        // 0/999 sentinels are normalised to 0 at parse time).
        if (exit.MinLevel > 0 && level < exit.MinLevel) return true;
        if (exit.MaxLevel > 0 && level > exit.MaxLevel) return true;
        return false;
    }

    // A (Toll: N) exit needs N*100 copper-value on hand to cross. Only gate
    // when we know our wealth — an unknown wallet never refuses a walk on a
    // bar we can't yet evaluate.
    private bool IsTollGateBlocked(in RoomExit exit)
    {
        if (exit.Hint != RoomExitHint.Toll || exit.TollGold <= 0) return false;
        if (_tollGateSuspended) return false;   // planning the tolls-permitted route (see WarmForRoute)
        long cost = (long)exit.TollGold * 100;

        // Party branch: a toll is per-crosser, so when leading a party route
        // around one a member can't afford rather than stranding them at the
        // gate. The provider (PartyWealthTracker.MinWealth) folds in our own
        // wallet too, and returns null — falling through to the self-only
        // branch — when solo, not leading, or our own wallet is unknown. It's
        // the demand trigger for the @wealth probe: invoked here only for a toll
        // exit, so nothing polls unless a toll is actually in play.
        if (PartyWealthProvider?.Invoke() is { } partyMin)
            return partyMin < cost;

        if (WealthProvider?.Invoke() is not { } wealth) return false;
        return wealth < cost;
    }

    // Warm the party @wealth reading before a walk, but only when it's
    // actually needed. BFS explores off-path toll edges (any toll exit inside
    // the search frontier, in any direction), so probing from the per-exit
    // gate fired an @wealth for tolls the party would never walk through. Here
    // we plan the route ONCE with the toll gate suspended — the path the party
    // WOULD take if every toll were affordable — and probe only when that path
    // genuinely crosses a toll. No party gate in play (solo, not leading, or
    // our own wallet unknown) → nothing to warm.
    public void WarmForRoute(BfsMapper bfs, RoomKey source, RoomKey destination)
    {
        ArgumentNullException.ThrowIfNull(bfs);
        if (TollWealthProbe is null) return;
        if (PartyWealthProvider?.Invoke() is null) return;   // party toll gate doesn't apply

        _tollGateSuspended = true;
        try
        {
            if (bfs.RouteUsesToll(source, destination, this))
                TollWealthProbe();
        }
        finally { _tollGateSuspended = false; }
    }

    // True when the user has flagged this room as a stash drop-off point.
    public bool IsStash(RoomKey key) => _stash.Contains(key);

    // Add the room to the avoided set. No-op when already avoided or when no
    // profile is loaded. Persists immediately.
    public void MarkAvoided(RoomKey key)
    {
        if (_profile.Current is not { } current) return;
        if (!_avoided.Add(key)) return;

        current.AvoidedRooms ??= new List<RoomRef>();
        current.AvoidedRooms.Add(new RoomRef(key.Map, key.Room));
        _profile.Save();
        _log?.Info("MovementFilter", $"avoided {key}");
        AvoidedChanged?.Invoke();
    }

    // Remove the room from the avoided set. Persists immediately.
    public void UnmarkAvoided(RoomKey key)
    {
        if (_profile.Current is not { } current) return;
        if (!_avoided.Remove(key)) return;

        if (current.AvoidedRooms is { } list)
            list.RemoveAll(r => r.Map == key.Map && r.Room == key.Room);

        _profile.Save();
        _log?.Info("MovementFilter", $"unavoided {key}");
        AvoidedChanged?.Invoke();
    }

    // Flag the room as a stash drop-off point. Persists immediately.
    public void MarkStash(RoomKey key)
    {
        if (_profile.Current is not { } current) return;
        if (!_stash.Add(key)) return;

        current.StashRooms ??= new List<RoomRef>();
        current.StashRooms.Add(new RoomRef(key.Map, key.Room));
        _profile.Save();
        _log?.Info("MovementFilter", $"stash {key}");
        StashChanged?.Invoke();
    }

    // Clear the room's stash-room flag. Persists immediately.
    public void UnmarkStash(RoomKey key)
    {
        if (_profile.Current is not { } current) return;
        if (!_stash.Remove(key)) return;

        if (current.StashRooms is { } list)
            list.RemoveAll(r => r.Map == key.Map && r.Room == key.Room);

        _profile.Save();
        _log?.Info("MovementFilter", $"unstashed {key}");
        StashChanged?.Invoke();
    }

    private void OnProfileLoaded(CharacterProfile profile)
    {
        _avoided.Clear();
        _stash.Clear();

        if (profile.AvoidedRooms is { } a)
            foreach (RoomRef r in a) _avoided.Add(new RoomKey(r.Map, r.Room));
        if (profile.StashRooms is { } s)
            foreach (RoomRef r in s) _stash.Add(new RoomKey(r.Map, r.Room));

        AvoidedChanged?.Invoke();
        StashChanged?.Invoke();
    }

    private void OnProfileClosed()
    {
        bool hadAvoided = _avoided.Count > 0;
        bool hadStash   = _stash.Count > 0;
        _avoided.Clear();
        _stash.Clear();
        if (hadAvoided) AvoidedChanged?.Invoke();
        if (hadStash)   StashChanged?.Invoke();
    }
}
