using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;

namespace MudPlay.Services;

// Persisted per-set boss kill-times. On a confirmed boss kill (a specific
// MonsterDied identity that matches a tracked boss AND lands in one of that boss's
// rooms) the kill time is stamped and written to {set}/boss-timers.json, so a long
// respawn timer survives an app restart. Timer VALUES aren't stored — the full
// respawn hours are resolved live from game data (BossCatalog), and the realm's
// early-window model comes from BossTimerMath. Realm-wide like BossStore: keyed to
// the active set, shared across the user's characters.
//
// Kill detection matches the in-game signal: we were ENGAGED with a monster by
// name, then it died (awarded exp). Monster numbers aren't observable in-game, so
// attribution is by NAME — the engaged target name (CombatManager.CurrentTarget,
// read live at death) matched against the boss list, confirmed by the current room
// being one of the boss's rooms. This covers the common "exp + *Combat Off*"
// fallback death, which carries no candidate identity of its own; a specific
// death-line candidate name is accepted as a secondary match. Deaths we can't
// attribute (no engaged name, no candidate) fall to the tab's manual override.
public sealed class BossTimerStore
{
    private readonly BossStore _bosses;
    private readonly GameDataCache _gameData;
    private readonly LogService? _log;

    // boss name (lowercase) -> UTC kill time.
    private Dictionary<string, DateTimeOffset> _killed =
        new(StringComparer.OrdinalIgnoreCase);

    // Active BBS's nightly-cleanup config (time-of-day + zone) for "Respawns @
    // Cleanup" bosses. Resolved live so a BBS / setting change takes effect without
    // re-wiring; null when unset or unparseable → cleanup bosses can't auto-flip.
    private Func<BossCleanupConfig?>? _cleanupConfig;

    public string? ActiveSet { get; private set; }

    public void SetCleanupConfig(Func<BossCleanupConfig?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _cleanupConfig = resolver;
    }

    // Fires after any change to the tracked set (kill stamped, reset, reload) so a
    // view can refresh its live status column off the store rather than polling.
    public event Action? Changed;

    public BossTimerStore(BossStore bosses, GameDataCache gameData, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(bosses);
        ArgumentNullException.ThrowIfNull(gameData);
        _bosses = bosses;
        _gameData = gameData;
        _log = log;
    }

    public void OnActiveSetChanged(string? setName)
    {
        ActiveSet = string.IsNullOrWhiteSpace(setName) ? null : setName;
        _killed = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        if (ActiveSet is not null &&
            JsonStore.Load<Dictionary<string, DateTimeOffset>>(AppPaths.BossTimersFile(ActiveSet)) is { } loaded)
        {
            foreach ((string name, DateTimeOffset at) in loaded) _killed[name] = at;
        }
        Changed?.Invoke();
    }

    public DateTimeOffset? KilledAt(string name)
        => _killed.TryGetValue(name, out DateTimeOffset at) ? at : null;

    // Stamp a kill at now (UTC) and persist. Used by auto-detection.
    public void MarkKilled(string name) => MarkKilled(name, DateTimeOffset.UtcNow);

    // Stamp a kill at a specific time and persist. Used by the tab's manual "Mark"
    // dialog, where the user can back-date the kill.
    public void MarkKilled(string name, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _killed[name] = at.ToUniversalTime();
        Persist();
        _log?.Info("Bosses", $"timer set for '{name}' at {at.ToUniversalTime():u}");
        Changed?.Invoke();
    }

    // Clear a boss's timer (manual reset / mistaken start).
    public void Reset(string name)
    {
        if (_killed.Remove(name))
        {
            Persist();
            _log?.Info("Bosses", $"timer cleared for '{name}'");
            Changed?.Invoke();
        }
    }

    // Live window state for a boss, or null when it has no active timer (never
    // killed, already expired / respawned, or no game-data respawn timer). A Cleanup
    // boss is "active" while it reads DEAD — its state carries the "cleanup" label
    // and the time until the next cleanup flips it back to ALIVE.
    public BossWindowState? StatusFor(BossDef def, RealmType realm)
    {
        ArgumentNullException.ThrowIfNull(def);
        if (KilledAt(def.Name) is not { } killed) return null;

        if (def.RespawnType == BossRespawnType.Cleanup)
        {
            if (CleanupRemainingFrom(killed) is not { } rem) return null;   // alive → not tracked
            return new BossWindowState(false, rem, "cleanup", rem);
        }

        if (BossCatalog.EffectiveRegenHours(_gameData, def) is not { } hours || hours <= 0) return null;
        BossWindowState state = BossTimerMath.Describe(realm, hours, DateTimeOffset.UtcNow - killed);
        return state.Expired ? null : state;
    }

    // A Cleanup boss reads DEAD once marked, until the next cleanup. True only when
    // marked and the cleanup that clears it hasn't arrived (or can't be computed —
    // no cleanup time set keeps it DEAD until manually cleared).
    public bool IsCleanupDead(string name)
    {
        if (KilledAt(name) is not { } killed) return false;
        if (_cleanupConfig?.Invoke() is not { } cfg) return true;   // marked, no cleanup time → stays dead
        return DateTimeOffset.UtcNow < BossTimerMath.NextCleanup(killed, cfg.TimeOfDay, cfg.Tz);
    }

    // Time until a marked Cleanup boss flips back to ALIVE, or null when it isn't
    // marked, the cleanup has already passed, or no cleanup time is configured.
    public TimeSpan? CleanupRemaining(string name)
        => KilledAt(name) is { } killed ? CleanupRemainingFrom(killed) : null;

    private TimeSpan? CleanupRemainingFrom(DateTimeOffset killed)
    {
        if (_cleanupConfig?.Invoke() is not { } cfg) return null;
        TimeSpan rem = BossTimerMath.NextCleanup(killed, cfg.TimeOfDay, cfg.Tz) - DateTimeOffset.UtcNow;
        return rem > TimeSpan.Zero ? rem : null;
    }

    // Every boss on the given realm with a running timer, paired with its state,
    // soonest guaranteed spawn first. Drives the tab summary + @timer no-arg report.
    public IReadOnlyList<(BossDef Def, BossWindowState State)> ActiveTimers(RealmType realm)
    {
        var active = new List<(BossDef, BossWindowState)>();
        foreach (BossDef def in _bosses.ResolveForRealm(realm))
            if (StatusFor(def, realm) is { } state) active.Add((def, state));
        return active.OrderBy(a => a.Item2.FullRemaining).ToList();
    }

    // Auto-start on a boss death. Attribution is by NAME: the monster we were
    // engaged with (engagedName = CombatManager.CurrentTarget, read live — this is
    // what a fallback exp+Off death is, a death of whatever we were fighting), or a
    // specific death-line candidate name, matched against a boss whose rooms include
    // the current room. key/engagedName are read live at the death (the event itself
    // carries neither location nor the engaged name).
    // Fires when a death is attributed to a tracked boss in the current room — the
    // matched BossDef (whichever respawn type). Fires for EVERY matched boss; a
    // subscriber (the Grab-All engine) gates on def.GrabAll. Kept separate from the
    // timer marking so a cleanup boss (no timer) still notifies.
    public event Action<BossDef>? BossKilled;

    public void OnMonsterDied(MonsterDeathEvent evt, RoomKey? key, string? engagedName)
    {
        if (key is not { } here) return;   // can't confirm placement

        foreach (BossDef def in _bosses.Resolve())
        {
            if (!RoomsContain(def, here)) continue;
            if (!(NameMatches(def.Name, engagedName)
                  || evt.Candidates.Any(c => NameMatches(def.Name, c.Name)))) continue;

            // A timed boss starts its respawn countdown; a cleanup boss has none. Both
            // notify BossKilled so Grab-All can fire regardless of respawn type.
            if (def.RespawnType == BossRespawnType.Timed) MarkKilled(def.Name);
            BossKilled?.Invoke(def);
            return;
        }
    }

    private static bool RoomsContain(BossDef def, RoomKey key)
    {
        foreach (string wire in def.Rooms)
            if (RoomKey.TryParseWire(wire, out RoomKey k) && k == key) return true;
        return false;
    }

    // Case-insensitive name match tolerant of a leading article ("the ogre king"
    // matches boss "ogre king"). The observed name (engaged target / death line)
    // must contain the canonical boss name.
    private static bool NameMatches(string bossName, string? observed)
    {
        if (string.IsNullOrWhiteSpace(observed)) return false;
        string boss = StripArticle(bossName);
        return boss.Length > 0 && StripArticle(observed).Contains(boss, StringComparison.Ordinal);
    }

    private static string StripArticle(string s)
    {
        s = s.Trim().ToLowerInvariant();
        if (s.StartsWith("the ", StringComparison.Ordinal)) return s[4..];
        if (s.StartsWith("an ", StringComparison.Ordinal)) return s[3..];
        if (s.StartsWith("a ", StringComparison.Ordinal)) return s[2..];
        return s;
    }

    private void Persist()
    {
        if (ActiveSet is null) return;

        // Boss-timer persistence is convenience bookkeeping fired from the combat
        // death-line path — a write failure (transient IO, a filesystem hiccup)
        // must never crash the client mid-fight. Log it and carry on; the in-memory
        // timer still stands and the next kill/reset re-attempts the write.
        try
        {
            JsonStore.Save(AppPaths.BossTimersFile(ActiveSet), _killed);
        }
        catch (Exception ex)
        {
            _log?.Warn("Bosses", $"failed to persist boss timers: {ex.Message}");
        }
    }
}

// Resolved nightly-cleanup config for the active BBS — the daily wall-clock time
// and the zone it's in. Built by AppServices from the BBS profile's
// CleanupTimeOfDay + CleanupTimeZoneId.
public sealed record BossCleanupConfig(TimeSpan TimeOfDay, TimeZoneInfo Tz);
