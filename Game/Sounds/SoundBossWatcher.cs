using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Sounds;

// Fires the boss-timer cues off the Bosses table: once when a running timer reaches
// its first early spawn window, once when it reaches its guaranteed respawn. Each
// kill fires each cue once — the kill time keys it. A moment first seen more than
// StaleAfter late (the client wasn't running or connected then) stays silent, so
// logging in doesn't ring for every timer that ran out overnight.
//
// Walks the boss list rather than the store's running timers: a timer leaves that
// list the moment it expires, which is exactly when the respawn cue is due.
//
// Evaluated on the Events scheduler's 30 s clock. A timed boss's two moments are
// worked out once per kill and kept, so a tick is a few date comparisons with no
// game-data read.
public sealed class SoundBossWatcher
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    private readonly SoundCueEngine _sounds;
    private readonly BossStore _bosses;
    private readonly BossTimerStore _timers;
    private readonly GameDataCache _gameData;
    private readonly Func<bool> _inGame;

    private readonly record struct Moments(DateTimeOffset Killed, DateTimeOffset? Window, DateTimeOffset? Ready);
    private readonly Dictionary<string, Moments> _moments = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(string Boss, DateTimeOffset Killed, string Cue)> _fired = new();

    private readonly Func<DateTimeOffset> _now;

    public SoundBossWatcher(
        SoundCueEngine sounds, BossStore bosses, BossTimerStore timers, GameDataCache gameData, Func<bool> inGame,
        Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        ArgumentNullException.ThrowIfNull(sounds);
        ArgumentNullException.ThrowIfNull(bosses);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(inGame);
        _sounds = sounds;
        _bosses = bosses;
        _timers = timers;
        _gameData = gameData;
        _inGame = inGame;
    }

    public void Evaluate()
    {
        if (!_inGame()) return;
        DateTimeOffset now = _now();
        RealmType realm = _gameData.ActiveRealm;
        foreach (BossDef def in _bosses.ResolveForRealm(realm))
        {
            if (_timers.KilledAt(def.Name) is not { } killed) continue;
            if (def.RespawnType == BossRespawnType.Cleanup)
            {
                // Back alive at the nightly cleanup after its kill; no early window.
                Check(def.Name, killed, SoundCues.BossReady, _timers.NextCleanupFor(def.Name), now);
                continue;
            }
            if (!_moments.TryGetValue(def.Name, out Moments m) || m.Killed != killed)
                _moments[def.Name] = m = Compute(def, killed, realm);
            Check(def.Name, killed, SoundCues.BossWindow, m.Window, now);
            Check(def.Name, killed, SoundCues.BossReady, m.Ready, now);
        }
    }

    private void Check(string boss, DateTimeOffset killed, string cue, DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } when || now < when || now - when > StaleAfter) return;
        if (_fired.Add((boss, killed, cue))) _sounds.Fire(cue);
    }

    // A boss whose respawn length game data can't resolve has neither moment.
    private Moments Compute(BossDef def, DateTimeOffset killed, RealmType realm)
    {
        if (BossCatalog.EffectiveRegenHours(_gameData, def) is not { } hours || hours <= 0)
            return new Moments(killed, null, null);
        double first = BossTimerMath.SpawnFractions(realm)[0];
        return new Moments(killed, killed + TimeSpan.FromHours(hours * first), killed + TimeSpan.FromHours(hours));
    }
}
