using System.Collections.Generic;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

/// <summary>
/// Per-character avoided + stash room set. Implements
/// <see cref="IRoomFilter"/> for BFS — the walker / loop runner /
/// auto-lair scheduler all read <see cref="IsAvoided"/> at planning
/// time so the avoided rooms are dropped from candidate paths.
/// </summary>
/// <remarks>
/// <para>
/// Scope: Char-only (per the planning conversation). Lives on
/// <see cref="CharacterProfile.AvoidedRooms"/> +
/// <see cref="CharacterProfile.StashRooms"/>, not in
/// <see cref="SettingsResolver"/> — the avoided set is a personal
/// no-go list, not a per-realm or per-BBS rule.
/// </para>
/// <para>
/// Wiring: <see cref="AppServices"/> subscribes the filter to
/// <see cref="ProfileService.ProfileLoaded"/> +
/// <see cref="ProfileService.ProfileClosed"/>. Mutating methods
/// (<see cref="MarkAvoided"/>, <see cref="UnmarkAvoided"/>,
/// <see cref="MarkStash"/>, <see cref="UnmarkStash"/>) update the
/// in-memory set, mirror the change back into the loaded profile,
/// persist via <see cref="ProfileService.Save"/>, and fire
/// <see cref="AvoidedChanged"/> / <see cref="StashChanged"/> so the
/// map UI can recolour the affected cells.
/// </para>
/// </remarks>
public sealed class MovementFilter : IRoomFilter
{
    private readonly ProfileService _profile;
    private readonly LogService? _log;
    private readonly HashSet<RoomKey> _avoided = new();
    private readonly HashSet<RoomKey> _stash = new();

    /// <summary>
    /// Supplies the player's current character level for Form-A exit
    /// level-gate evaluation, or <c>null</c> when the level isn't known
    /// yet (no <c>stat</c> screen parsed). Wired by
    /// <see cref="AppServices"/> to <c>StatParser</c>. When null,
    /// <see cref="IsExitBlocked"/> never blocks — we don't refuse a walk
    /// on a gate we can't yet evaluate.
    /// </summary>
    public Func<int?>? LevelProvider { get; set; }

    /// <summary>
    /// Supplies the party's most-constraining <c>(Low, High)</c> level
    /// window when this character is leading a party, or <c>null</c> when
    /// solo, not leading, or nobody's level is known yet. Wired by
    /// <see cref="AppServices"/> to <see cref="Game.Remote.PartyLevelTracker"/>.
    /// When non-null it takes precedence over <see cref="LevelProvider"/>
    /// in <see cref="IsExitBlocked"/>: BFS routes the party <b>around</b> a
    /// gate that would leave a member behind, instead of walking the leader
    /// through it. The bounds already fold in the leader's own level, so
    /// the party branch never waves the leader through a gate the leader
    /// can't cross either.
    /// </summary>
    public Func<(int Low, int High)?>? PartyLevelBoundsProvider { get; set; }

    /// <summary>Read-only snapshot of the currently-avoided room keys.</summary>
    public IReadOnlyCollection<RoomKey> Avoided => _avoided;

    /// <summary>Read-only snapshot of the currently-flagged stash-room keys.</summary>
    public IReadOnlyCollection<RoomKey> Stash => _stash;

    /// <summary>Fires after every mutation to the avoided set, including profile reload.</summary>
    public event Action? AvoidedChanged;

    /// <summary>Fires after every mutation to the stash set, including profile reload.</summary>
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

    /// <inheritdoc/>
    public bool IsAvoided(RoomKey key) => _avoided.Contains(key);

    /// <inheritdoc/>
    public bool IsExitBlocked(in RoomExit exit)
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

    /// <summary>True when the user has flagged this room as a stash drop-off point.</summary>
    public bool IsStash(RoomKey key) => _stash.Contains(key);

    /// <summary>
    /// Add the room to the avoided set. No-op when already avoided or
    /// when no profile is loaded. Persists immediately.
    /// </summary>
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

    /// <summary>Remove the room from the avoided set. Persists immediately.</summary>
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

    /// <summary>Flag the room as a stash drop-off point. Persists immediately.</summary>
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

    /// <summary>Clear the room's stash-room flag. Persists immediately.</summary>
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
