using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FujinTerm.Game.Calculators;
using FujinTerm.Game.GameData;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game;

/// <summary>
/// The manual / armed entry point for "go train". Resolves the nearest allowed,
/// level-appropriate trainer (<see cref="TrainerCatalog.SelectNearest"/> + BFS
/// distance), detours a running loop / auto-lair to it, sends <c>train</c> on
/// arrival, and — once the "attain level N" line confirms the level-up —
/// refreshes stats and hands off to <see cref="AutoTrainManager"/> to apply the
/// CP plan, then resumes the engine.
/// </summary>
/// <remarks>
/// <para><b>Can-level detection without polling.</b> We keep a running exp total:
/// seeded from the current <see cref="PlayerStats.Exp"/>, re-synced on every
/// <see cref="StatParser.ScreenParsed"/>, and incremented by each captured
/// <c>UserGainExperience</c> line. "Can train" is that total meeting the next
/// level's cumulative threshold (<see cref="ExperienceTableCalculator.CalcExpNeeded"/>)
/// — exactly the top Level-Projection row's <i>Exp to level</i> hitting 0. A kill
/// that crosses the threshold fires the armed run immediately; no <c>exp</c> poll.</para>
/// <para><b>Engine detour</b> mirrors <c>AutoDepositManager</c>: snapshot the
/// running Loop / Auto-Lair, stop it (stop-and-restart, not a gate, so the detour
/// walk owns the wire), train, then restart it. Manual Train Now does the same
/// when an engine is running; with none it just walks + trains. The armed path
/// also gates the CP-apply on the Auto-train-stats toggle; manual always applies.</para>
/// </remarks>
public sealed class TrainerWalkManager : IDisposable
{
    private enum Phase { Idle, Walking, Training, RefreshingStats, ApplyingCp }
    private enum ResumeKind { None, Loop, Lair }
    private readonly record struct ResumeTarget(ResumeKind Kind, Loop? Loop);

    private readonly PlayerStats _stats;
    private readonly StatParser _statParser;
    private readonly GameDataCache _gameData;
    private readonly ProfileService _profile;
    private readonly RoomTracker _tracker;
    private readonly BfsMapper _bfs;
    private readonly AutoWalkManager _walker;
    private readonly LoopRunner _loopRunner;
    private readonly AutoLairManager _autoLair;
    private readonly AutoTrainManager _autoTrain;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();
    private readonly IDisposable _trainSub;
    private readonly IDisposable _expSub;

    private Phase _phase = Phase.Idle;
    private int _sessionId;
    private int _attainedLevel;
    private bool _armedRun;
    private TrainerShop? _target;
    private ResumeTarget _resume;
    private long _liveExp;   // baseline (stat/exp) + captured gains since

    /// <summary>How long to wait for the "attain level" line after sending <c>train</c>.</summary>
    public TimeSpan TrainConfirmTimeout { get; set; } = TimeSpan.FromSeconds(8);
    /// <summary>How long to wait for the post-train <c>stat</c> refresh before giving up on CP.</summary>
    public TimeSpan StatRefreshTimeout { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Raised when <see cref="IsBusy"/> / <see cref="CanTrainNow"/> may have changed.</summary>
    public event Action? StateChanged;

    /// <summary>Raised after a level's CP plan row is applied + removed — the CP tab reloads.</summary>
    public event Action? PlanApplied;

    public TrainerWalkManager(PlayerStats stats, StatParser statParser, GameDataCache gameData,
                              ProfileService profile, RoomTracker tracker, BfsMapper bfs,
                              AutoWalkManager walker, LoopRunner loopRunner, AutoLairManager autoLair,
                              AutoTrainManager autoTrain, MessageRouter router, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(statParser);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(walker);
        ArgumentNullException.ThrowIfNull(loopRunner);
        ArgumentNullException.ThrowIfNull(autoLair);
        ArgumentNullException.ThrowIfNull(autoTrain);
        ArgumentNullException.ThrowIfNull(router);
        _stats = stats;
        _statParser = statParser;
        _gameData = gameData;
        _profile = profile;
        _tracker = tracker;
        _bfs = bfs;
        _walker = walker;
        _loopRunner = loopRunner;
        _autoLair = autoLair;
        _autoTrain = autoTrain;
        _log = log;
        _liveExp = stats.Exp;

        _walker.Event += OnWalkEvent;
        _trainSub = router.Subscribe(KnownPatterns.TrainAttainLevel, OnTrained);
        _expSub = router.Subscribe(KnownPatterns.UserGainExperience, OnExpGain);
        _statParser.ScreenParsed += OnStatScreenParsed;
        _autoTrain.StateChanged += OnAutoTrainStateChanged;
    }

    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    /// <summary>True while a walk/train run (or the CP-apply it hands off) is in flight.</summary>
    public bool IsBusy => _phase != Phase.Idle || _autoTrain.IsBusy;

    /// <summary>True when Train Now would do something: a CP plan is applicable, or we can level.</summary>
    public bool CanTrainNow => _autoTrain.CanTrainNow || CanLevelNow();

    /// <summary>Manual trigger (CP Allocation tab) — walk to a trainer and train + apply the plan.</summary>
    public void TrainNow() => Begin(armed: false);

    /// <summary>
    /// Remote <c>@train</c> trigger — train where we are (no walk; assumes we're
    /// already at a trainer). Sends <c>train</c>, then applies the CP plan only
    /// when Auto-train-stats is on + a plan row exists (the armed-style gate).
    /// No engine detour — a remote train is used while parked, not mid-loop.
    /// </summary>
    public void TrainInPlace()
    {
        if (IsBusy || !_wire.IsBound) return;
        _armedRun = true;     // CP step follows the Auto-train-stats toggle
        _target = null;       // no walk target — we're assumed to be at the trainer
        _resume = default;    // and not detouring a running engine
        SendTrain(++_sessionId);
    }

    private void Begin(bool armed)
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

        _armedRun = armed;
        _target = t;
        _resume = SnapshotEngine();
        StopEngine();   // free the wire for the detour (no-op when nothing's running)

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
            Finish($"No path to trainer {t.Name} ({t.Map}/{t.Room}).");
        }
    }

    private void OnWalkEvent(WalkEvent e)
    {
        if (_phase != Phase.Walking) return;
        if (e.Kind == WalkEventKind.Finished)
        {
            if (_target is { } t && _tracker.State.CurrentRoom?.Key == new RoomKey(t.Map, t.Room))
                SendTrain(_sessionId);
            else
                Finish("Walk finished away from the trainer — aborting.");
        }
        else if (e.Kind == WalkEventKind.Failed)
        {
            Finish("Couldn't reach the trainer — aborting.");
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
            Finish("No training confirmation (not enough experience, or wrong trainer).");
    }

    // "...you receive training to attain level N." — level-up confirmation.
    private void OnTrained(MatchResult m)
    {
        if (_phase != Phase.Training) return;
        if (m.Groups.Count == 0 || !int.TryParse(m.Groups[0], out int level))
        {
            Finish("Couldn't parse the trained level.");
            return;
        }
        _attainedLevel = level;
        _log?.Info("AutoTrain", $"Trained to level {level}.");

        // Always refresh after a level-up so PlayerStats (Level / Exp / CP) is
        // current — that re-anchors the Level Projection's top row and feeds the
        // CP-apply decision (both Level and CP lag the train line).
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
            Finish("Stat refresh didn't arrive — apply CP from the CP Allocation tab.");
    }

    private void OnStatScreenParsed(LastKnownStats snapshot)
    {
        _liveExp = snapshot.Exp;   // authoritative resync of the running total
        if (_phase != Phase.RefreshingStats || snapshot.Level != _attainedLevel) return;

        // Manual Train Now always applies the plan; the armed path needs the
        // Auto-train-stats toggle. Only when there's a row for the new level.
        bool wantStats = !_armedRun || ReadSettings().AutoTrainStats;
        bool hasPlan = _profile.Current?.CharacterPlan?.Any(e => e.Level == _attainedLevel) ?? false;
        if (wantStats && hasPlan)
        {
            _log?.Info("AutoTrain", $"Applying CP plan for level {_attainedLevel}.");
            _autoTrain.TrainNow();
            if (_autoTrain.IsBusy)
            {
                _phase = Phase.ApplyingCp;
                StateChanged?.Invoke();
                return;
            }
            // Nothing affordable to apply — leave the plan row in place.
        }

        // Levelled up but applied no CP — keep the plan row (per spec); the
        // refreshed level still re-anchors the projection.
        Finish(null);
    }

    private void OnAutoTrainStateChanged()
    {
        if (_phase == Phase.ApplyingCp && !_autoTrain.IsBusy)
        {
            RemoveAppliedPlanRow();   // CP committed → consume that level's plan row
            Finish(null);
            return;
        }
        StateChanged?.Invoke();
    }

    // CP for _attainedLevel has been applied (the train-stats screen committed) —
    // drop that level's row from the saved plan so it isn't re-applied, and tell
    // the CP Allocation tab to reload. A level-up that applied no CP keeps its row.
    private void RemoveAppliedPlanRow()
    {
        if (_profile.Current is not { CharacterPlan: { } plan }) return;
        if (plan.RemoveAll(e => e.Level == _attainedLevel) == 0) return;
        if (plan.Count == 0) _profile.Current.CharacterPlan = null;
        _profile.Save();
        _log?.Info("AutoTrain", $"Applied + cleared CP plan row for level {_attainedLevel}.");
        PlanApplied?.Invoke();
    }

    // "You gain N experience." — keep the running total live so the armed run
    // fires the instant a kill crosses the next-level threshold (no exp poll).
    private void OnExpGain(MatchResult m)
    {
        if (m.Groups.Count > 0 && long.TryParse(m.Groups[0], out long gained))
            _liveExp += gained;

        if (!IsBusy && EngineActive && ReadSettings().AutoTrain && CanLevelNow())
            Begin(armed: true);
        else
            StateChanged?.Invoke();
    }

    private void Finish(string? reason)
    {
        if (reason is not null) _log?.Info("AutoTrain", reason);
        ResumeTarget resume = _resume;
        _phase = Phase.Idle;
        _target = null;
        _resume = default;
        _armedRun = false;
        StateChanged?.Invoke();
        ResumeEngine(resume);
    }

    // ----- engine detour (mirrors AutoDepositManager) --------------------

    private bool EngineActive => _loopRunner.State != LoopState.Idle || _autoLair.IsActive;

    private ResumeTarget SnapshotEngine()
    {
        if (_autoLair.IsActive) return new ResumeTarget(ResumeKind.Lair, null);
        if (_loopRunner.State != LoopState.Idle && _loopRunner.CurrentLoop is { } loop)
            return new ResumeTarget(ResumeKind.Loop, loop);
        return new ResumeTarget(ResumeKind.None, null);
    }

    private void StopEngine()
    {
        if (_autoLair.IsActive) _autoLair.Stop("trainer detour");
        if (_loopRunner.State != LoopState.Idle) _loopRunner.Stop("trainer detour");
    }

    private void ResumeEngine(ResumeTarget resume)
    {
        switch (resume.Kind)
        {
            case ResumeKind.Lair:
                _autoLair.Start();
                break;
            case ResumeKind.Loop:
                if (resume.Loop is { } loop) _loopRunner.Start(loop);
                break;
        }
    }

    // ----- resolution / detection ----------------------------------------

    private TrainerShop? SelectNearest(RoomKey from) =>
        TrainerCatalog.SelectNearest(
            TrainerCatalog.Enumerate(_gameData), _stats.Level, ResolveClassNumber(), ReadDisabledTrainers(),
            t => _bfs.DistanceBetween(from, new RoomKey(t.Map, t.Room)));

    // The top Level-Projection row's "Exp to level" reaching 0: the running exp
    // total already meets the next level's cumulative threshold.
    private bool CanLevelNow()
    {
        if (_stats.Level <= 0) return false;
        int chart = ExperienceTableCalculator.CalcExpChart(
            GetInt(_gameData.FindRowByName("Classes", _stats.Class), "ExpTable"),
            GetInt(_gameData.FindRowByName("Races", _stats.Race), "ExpTable"));
        if (chart <= 0) return false;
        return _liveExp >= ExperienceTableCalculator.CalcExpNeeded(_stats.Level + 1, chart, _gameData.ActiveRealm);
    }

    private int ResolveClassNumber() => GetInt(_gameData.FindRowByName("Classes", _stats.Class), "Number");

    private AutoTrainerSettings ReadSettings()
    {
        if (_profile.Current?.Settings is { } settings && settings.TryGetValue("AutoTrainer", out JsonElement json))
        {
            try { return JsonSerializer.Deserialize<AutoTrainerSettings>(json) ?? new AutoTrainerSettings(); }
            catch { /* malformed settings → defaults */ }
        }
        return new AutoTrainerSettings();
    }

    private HashSet<int> ReadDisabledTrainers()
    {
        var disabled = new HashSet<int>();
        if (ReadSettings().DisabledTrainers is { } list)
            foreach (int n in list) disabled.Add(n);
        return disabled;
    }

    private static int GetInt(JsonElement? rowOpt, string prop)
    {
        if (rowOpt is not JsonElement row || row.ValueKind != JsonValueKind.Object) return 0;
        if (!row.TryGetProperty(prop, out JsonElement v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
    }

    public void Dispose()
    {
        _walker.Event -= OnWalkEvent;
        _trainSub.Dispose();
        _expSub.Dispose();
        _statParser.ScreenParsed -= OnStatScreenParsed;
        _autoTrain.StateChanged -= OnAutoTrainStateChanged;
    }
}
