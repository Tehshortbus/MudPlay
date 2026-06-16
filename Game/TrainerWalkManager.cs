using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FujinTerm.Game.GameData;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game;

/// <summary>
/// The manual / auto entry point for "go train". Resolves the nearest allowed,
/// level-appropriate trainer from <see cref="TrainerCatalog"/>, walks there with
/// <see cref="AutoWalkManager"/>, sends <c>train</c> on arrival, and — once the
/// "attain level N" line confirms the level-up — refreshes stats and hands off
/// to <see cref="AutoTrainManager"/> to apply the CP plan for the new level.
/// </summary>
/// <remarks>
/// The level-up line + a fresh <c>stat</c> are the load-bearing signals:
/// <see cref="PlayerStats"/>'s Level and CP both lag the <c>train</c> until the
/// next stat screen, so the engine waits for <see cref="StatParser.ScreenParsed"/>
/// before applying CP. Sessions are id-tagged so a late timeout can't disturb a
/// newer run. A level with no matching trainer (a quest-gated gap) stops with a
/// log line rather than flailing. Wraps <see cref="AutoTrainManager"/> so the CP
/// Allocation tab can talk to one object (Train Now / busy / can-train).
/// </remarks>
public sealed class TrainerWalkManager : IDisposable
{
    private enum Phase { Idle, Walking, Training, RefreshingStats }

    private readonly PlayerStats _stats;
    private readonly StatParser _statParser;
    private readonly GameDataCache _gameData;
    private readonly ProfileService _profile;
    private readonly RoomTracker _tracker;
    private readonly BfsMapper _bfs;
    private readonly AutoWalkManager _walker;
    private readonly AutoTrainManager _autoTrain;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();
    private readonly IDisposable _trainSub;

    private Phase _phase = Phase.Idle;
    private int _sessionId;
    private int _attainedLevel;
    private TrainerShop? _target;

    /// <summary>How long to wait for the "attain level" line after sending <c>train</c>.</summary>
    public TimeSpan TrainConfirmTimeout { get; set; } = TimeSpan.FromSeconds(8);
    /// <summary>How long to wait for the post-train <c>stat</c> refresh before giving up on CP.</summary>
    public TimeSpan StatRefreshTimeout { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Raised when <see cref="IsBusy"/> / <see cref="CanTrainNow"/> may have changed.</summary>
    public event Action? StateChanged;

    public TrainerWalkManager(PlayerStats stats, StatParser statParser, GameDataCache gameData,
                              ProfileService profile, RoomTracker tracker, BfsMapper bfs,
                              AutoWalkManager walker, AutoTrainManager autoTrain,
                              MessageRouter router, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(statParser);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(walker);
        ArgumentNullException.ThrowIfNull(autoTrain);
        ArgumentNullException.ThrowIfNull(router);
        _stats = stats;
        _statParser = statParser;
        _gameData = gameData;
        _profile = profile;
        _tracker = tracker;
        _bfs = bfs;
        _walker = walker;
        _autoTrain = autoTrain;
        _log = log;

        _walker.Event += OnWalkEvent;
        _trainSub = router.Subscribe(KnownPatterns.TrainAttainLevel, OnTrained);
        _statParser.ScreenParsed += OnStatScreenParsed;
        _autoTrain.StateChanged += OnAutoTrainStateChanged;
    }

    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    /// <summary>True while a walk/train run (or the CP-apply it hands off) is in flight.</summary>
    public bool IsBusy => _phase != Phase.Idle || _autoTrain.IsBusy;

    /// <summary>
    /// True when Train Now would do something: a CP plan is applicable now, or
    /// the character has enough experience to train a level.
    /// </summary>
    public bool CanTrainNow => _autoTrain.CanTrainNow || (_stats.Level > 0 && _stats.ExpToNext == 0);

    /// <summary>
    /// Begin a manual train run: walk to the nearest allowed, level-appropriate
    /// trainer and train (then apply the CP plan for the new level). No-op when
    /// busy, no wire is bound, our location is unknown, or no trainer qualifies.
    /// </summary>
    public void TrainNow()
    {
        if (IsBusy || !_wire.IsBound) return;
        if (_tracker.State.CurrentRoom is not { } cur)
        {
            _log?.Info("AutoTrain", "Can't train — current room unknown (walk a step to locate).");
            return;
        }

        TrainerShop? target = SelectNearest(cur.Key);
        if (target is not { } t)
        {
            _log?.Info("AutoTrain",
                $"No reachable allowed trainer serves level {_stats.Level} (quest-gated, or none discovered).");
            return;
        }

        _target = t;
        int session = ++_sessionId;
        var room = new RoomKey(t.Map, t.Room);

        if (cur.Key == room)
        {
            SendTrain(session);
        }
        else if (_walker.WalkTo(room))
        {
            _phase = Phase.Walking;
            _log?.Info("AutoTrain", $"Walking to {t.Name} ({t.Map}/{t.Room}) to train.");
            StateChanged?.Invoke();
        }
        else
        {
            _log?.Info("AutoTrain", $"No path to trainer {t.Name} ({t.Map}/{t.Room}).");
            _target = null;
        }
    }

    private TrainerShop? SelectNearest(RoomKey from) =>
        TrainerCatalog.SelectNearest(
            TrainerCatalog.Enumerate(_gameData), _stats.Level, ResolveClassNumber(), ReadDisabledTrainers(),
            t => _bfs.DistanceBetween(from, new RoomKey(t.Map, t.Room)));

    private void OnWalkEvent(WalkEvent e)
    {
        if (_phase != Phase.Walking) return;
        if (e.Kind == WalkEventKind.Finished)
        {
            if (_target is { } t && _tracker.State.CurrentRoom?.Key == new RoomKey(t.Map, t.Room))
                SendTrain(_sessionId);
            else
                Reset("Walk finished but not at the trainer room — aborting train.");
        }
        else if (e.Kind == WalkEventKind.Failed)
        {
            Reset("Couldn't reach the trainer — aborting train.");
        }
    }

    private void SendTrain(int session)
    {
        _phase = Phase.Training;
        _wire.Send("train");
        _log?.Info("AutoTrain", $"At {_target?.Name ?? "the trainer"} — sent `train`.");
        StateChanged?.Invoke();
        _ = TrainTimeoutAsync(session);
    }

    private async Task TrainTimeoutAsync(int session)
    {
        await Task.Delay(TrainConfirmTimeout);
        if (_sessionId == session && _phase == Phase.Training)
            Reset("No training confirmation (not enough experience, or wrong trainer).");
    }

    // "...you receive training to attain level N." — the level-up confirmation.
    private void OnTrained(MatchResult m)
    {
        if (_phase != Phase.Training) return;
        if (m.Groups.Count == 0 || !int.TryParse(m.Groups[0], out int level))
        {
            Reset("Couldn't parse the trained level.");
            return;
        }
        _attainedLevel = level;
        _log?.Info("AutoTrain", $"Trained to level {level}.");

        // Only chase the train-stats screen when the plan has a row for the new
        // level; otherwise we've done our job (levelled up).
        bool hasPlan = _profile.Current?.CharacterPlan?.Any(e => e.Level == level) ?? false;
        if (!hasPlan)
        {
            Reset(null);
            return;
        }

        // Level + CP both lag the train line — refresh before applying the plan.
        int session = _sessionId;
        _phase = Phase.RefreshingStats;
        _wire.Send("stat");
        StateChanged?.Invoke();
        _ = StatTimeoutAsync(session);
    }

    private async Task StatTimeoutAsync(int session)
    {
        await Task.Delay(StatRefreshTimeout);
        if (_sessionId == session && _phase == Phase.RefreshingStats)
            Reset("Stat refresh didn't arrive — apply CP from the CP Allocation tab.");
    }

    private void OnStatScreenParsed(LastKnownStats snapshot)
    {
        if (_phase == Phase.RefreshingStats && snapshot.Level == _attainedLevel)
        {
            // Stats are fresh (Level + CP). Hand off to the train-stats engine,
            // then we're done — it owns the screen from here.
            _phase = Phase.Idle;
            _target = null;
            _log?.Info("AutoTrain", $"Applying CP plan for level {_attainedLevel}.");
            StateChanged?.Invoke();
            _autoTrain.TrainNow();
        }
    }

    private void OnAutoTrainStateChanged() => StateChanged?.Invoke();

    private void Reset(string? reason)
    {
        if (reason is not null) _log?.Info("AutoTrain", reason);
        _phase = Phase.Idle;
        _target = null;
        StateChanged?.Invoke();
    }

    private int ResolveClassNumber()
    {
        if (string.IsNullOrEmpty(_stats.Class)) return 0;
        if (_gameData.FindRowByName("Classes", _stats.Class) is not JsonElement row) return 0;
        return row.TryGetProperty("Number", out JsonElement n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out int v)
            ? v : 0;
    }

    private HashSet<int> ReadDisabledTrainers()
    {
        var disabled = new HashSet<int>();
        if (_profile.Current?.Settings is not { } settings) return disabled;
        if (!settings.TryGetValue("AutoTrainer", out JsonElement json)) return disabled;
        try
        {
            if (JsonSerializer.Deserialize<AutoTrainerSettings>(json)?.DisabledTrainers is { } list)
                foreach (int n in list) disabled.Add(n);
        }
        catch { /* malformed settings → treat as none disabled */ }
        return disabled;
    }

    public void Dispose()
    {
        _walker.Event -= OnWalkEvent;
        _trainSub.Dispose();
        _statParser.ScreenParsed -= OnStatScreenParsed;
        _autoTrain.StateChanged -= OnAutoTrainStateChanged;
    }
}
