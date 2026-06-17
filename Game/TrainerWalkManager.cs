using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
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
/// <para><b>Can-level detection without polling.</b> <see cref="PlayerStats.Exp"/>
/// is the live experience total — <see cref="StatParser"/> re-anchors it on every
/// stat/exp poll and accrues each "You gain N experience." line. "Can train" is that
/// total meeting the next level's cumulative threshold
/// (<see cref="ExperienceTableCalculator.CalcExpNeeded"/>) — exactly the top
/// Level-Projection row's <i>Exp to level</i> hitting 0. We watch
/// <see cref="PlayerStats.PropertyChanged"/>, so a kill that crosses the threshold
/// fires the armed run immediately; no <c>exp</c> poll.</para>
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

    /// <summary>Why a (looping) train run stopped — shapes the <c>@train</c> reply.</summary>
    private enum StopReason { None, ProgressedTooFar, NoMoney, Timeout }

    private readonly record struct ResumeTarget(ResumeKind Kind, Loop? Loop);

    /// <summary>Hard cap on loop iterations / the bankable-level scan — a safety
    /// net so a misbehaving trainer can never spin the train loop unbounded.</summary>
    private const int MaxTrainLoopSteps = 60;

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
    private readonly IDisposable _tooFarSub;
    private readonly IDisposable _noMoneySub;

    private Phase _phase = Phase.Idle;
    private int _sessionId;
    private int _attainedLevel;       // last level confirmed by an "attain level" line
    private int _startLevel;          // level when the run began (for "trained N levels")
    private int _levelsTrained;       // attain-level confirmations this run
    private int _trainSteps;          // train commands sent this run (safety cap)
    private int _cpTargetLevel;       // level whose CP plan row we're applying
    private bool _applyCp;            // commit the CP plan at the end of the run
    private bool _cpApplied;          // a CP plan row was actually applied this run
    private bool _loopTrain;          // keep training across banked levels (@train Mode B/C)
    private StopReason _stopReason;   // why a looping run stopped (shapes the reply)
    private Action<string>? _reply;   // @train deferred reply sink (null for local runs)
    private TrainerShop? _target;
    private ResumeTarget _resume;

    /// <summary>How long to wait for the "attain level" line after sending <c>train</c>.</summary>
    public TimeSpan TrainConfirmTimeout { get; } = TimeSpan.FromSeconds(8);
    /// <summary>How long to wait for the post-train <c>stat</c> refresh before giving up on CP.</summary>
    public TimeSpan StatRefreshTimeout { get; } = TimeSpan.FromSeconds(8);

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

        _walker.Event += OnWalkEvent;
        _trainSub = router.Subscribe(KnownPatterns.TrainAttainLevel, OnTrained);
        _tooFarSub = router.Subscribe(KnownPatterns.TrainProgressedTooFar, OnProgressedTooFar);
        _noMoneySub = router.Subscribe(KnownPatterns.TrainNoMoney, OnNoMoney);
        _stats.PropertyChanged += OnStatsPropertyChanged;
        _statParser.ScreenParsed += OnStatScreenParsed;
        _autoTrain.StateChanged += OnAutoTrainStateChanged;
    }

    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    /// <summary>True while a walk/train run (or the CP-apply it hands off) is in flight.</summary>
    public bool IsBusy => _phase != Phase.Idle || _autoTrain.IsBusy;

    /// <summary>True when Train Now would do something: a CP plan is applicable, or we can level.</summary>
    public bool CanTrainNow => _autoTrain.CanTrainNow || CanLevelNow();

    /// <summary>Manual trigger (CP Allocation tab) — walk to a trainer and train one
    /// level + always apply that level's CP plan.</summary>
    public void TrainNow() => Begin(loop: false, applyCp: true, reply: null);

    /// <summary>
    /// Remote <c>@train</c> trigger — train where we are (no walk; assumes we're
    /// already at a trainer). Behaviour follows the Auto-Trainer toggles:
    /// <list type="bullet">
    /// <item>neither set → a single <c>train</c>;</item>
    /// <item>Auto-train set → loop <c>train</c> across every banked level until the
    /// trainer rejects us (progressed-too-far / no-money) or the banked exp runs
    /// out, then report;</item>
    /// <item>both set → as above, plus apply the CP plan through the final level.</item>
    /// </list>
    /// <paramref name="reply"/> (when supplied) receives a one-line status report
    /// once the run settles. No engine detour — a remote train is used while parked.
    /// </summary>
    public void TrainInPlace(Action<string>? reply = null)
    {
        if (IsBusy || !_wire.IsBound) return;
        AutoTrainerSettings s = ReadSettings();
        // CP only applies in the looping "both toggles" mode (Mode C).
        ResetRunState(loop: s.AutoTrain, applyCp: s.AutoTrain && s.AutoTrainStats, reply);
        _target = null;       // no walk target — we're assumed to be at the trainer
        _resume = default;    // and not detouring a running engine
        _startLevel = _stats.Level;
        SendTrain();
    }

    private void Begin(bool loop, bool applyCp, Action<string>? reply)
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

        ResetRunState(loop, applyCp, reply);
        _target = t;
        _startLevel = _stats.Level;
        _resume = SnapshotEngine();
        StopEngine();   // free the wire for the detour (no-op when nothing's running)

        var room = new RoomKey(t.Map, t.Room);
        if (cur.Key == room)
        {
            SendTrain();
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

    private void ResetRunState(bool loop, bool applyCp, Action<string>? reply)
    {
        _loopTrain = loop;
        _applyCp = applyCp;
        _reply = reply;
        _levelsTrained = 0;
        _trainSteps = 0;
        _attainedLevel = 0;
        _cpTargetLevel = 0;
        _cpApplied = false;
        _stopReason = StopReason.None;
    }

    private void OnWalkEvent(WalkEvent e)
    {
        if (_phase != Phase.Walking) return;
        if (e.Kind == WalkEventKind.Finished)
        {
            if (_target is { } t && _tracker.State.CurrentRoom?.Key == new RoomKey(t.Map, t.Room))
                SendTrain();
            else
                Finish("Walk finished away from the trainer — aborting.");
        }
        else if (e.Kind == WalkEventKind.Failed)
        {
            Finish("Couldn't reach the trainer — aborting.");
        }
    }

    // Send one `train`. Reactive loop: the next `train` only goes out once the
    // server's response (attain-level / rejection) lands, so we never blind-spin.
    private void SendTrain()
    {
        _phase = Phase.Training;
        _trainSteps++;
        _wire.Send("train");
        _log?.Info("AutoTrain", $"At {_target?.Name ?? "the trainer"} — sent `train` (step {_trainSteps}).");
        StateChanged?.Invoke();
        int session = ++_sessionId;
        _ = TrainTimeoutAsync(session);
    }

    private async Task TrainTimeoutAsync(int session)
    {
        await Task.Delay(TrainConfirmTimeout);
        // No attain-level and no rejection line arrived — treat as "nothing more
        // to train here" and settle (reports for @train, resumes the engine).
        if (_sessionId == session && _phase == Phase.Training)
            StopLoop(StopReason.Timeout);
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
        _levelsTrained++;
        _log?.Info("AutoTrain", $"Trained to level {level} ({_levelsTrained} this run).");

        // Looping mode keeps training while banked exp can reach the next level
        // and the safety cap holds. We never refresh stats between loop steps —
        // PlayerStats.Level lags, so the next-level check rides _liveExp directly.
        if (_loopTrain && _trainSteps < MaxTrainLoopSteps && CanReachLevel(level + 1))
        {
            SendTrain();
            return;
        }

        // Single train, banked exp exhausted, or cap hit → settle the run.
        BeginStatRefresh();
    }

    // A trainer rejected the train: out-levelled here, or can't pay. Stop the loop
    // and settle with the appropriate reason (drives the @train reply).
    private void OnProgressedTooFar(MatchResult m) => StopLoop(StopReason.ProgressedTooFar);
    private void OnNoMoney(MatchResult m) => StopLoop(StopReason.NoMoney);

    private void StopLoop(StopReason reason)
    {
        if (_phase != Phase.Training) return;
        _stopReason = reason;
        _log?.Info("AutoTrain", $"Train run stopped: {reason} ({_levelsTrained} trained).");
        BeginStatRefresh();
    }

    // Settle a finished train run. We only need a `stat` refresh when we actually
    // levelled (to re-anchor Level/Exp/CP and feed the CP-apply); a run that
    // trained nothing reports straight away.
    private void BeginStatRefresh()
    {
        if (_levelsTrained == 0)
        {
            FinishWithReport();
            return;
        }
        int session = ++_sessionId;
        _phase = Phase.RefreshingStats;
        _wire.Send("stat");
        StateChanged?.Invoke();
        _ = StatTimeoutAsync(session);
    }

    private async Task StatTimeoutAsync(int session)
    {
        await Task.Delay(StatRefreshTimeout);
        if (_sessionId == session && _phase == Phase.RefreshingStats)
            FinishWithReport();   // give up on CP; still report what we trained
    }

    private void OnStatScreenParsed(LastKnownStats snapshot)
    {
        if (_phase != Phase.RefreshingStats || snapshot.Level != _attainedLevel) return;

        // Apply the CP plan when the run wants it (manual always; @train Mode C)
        // and a row exists for the final attained level. CpPlanEntry rows are
        // cumulative targets, so applying the final level's row against the
        // accumulated CP budget covers every level we trained through in one pass.
        bool hasPlan = _profile.Current?.CharacterPlan?.Any(e => e.Level == _attainedLevel) ?? false;
        if (_applyCp && hasPlan)
        {
            _log?.Info("AutoTrain", $"Applying CP plan through level {_attainedLevel}.");
            _cpTargetLevel = _attainedLevel;
            _autoTrain.TrainNow();
            if (_autoTrain.IsBusy)
            {
                _phase = Phase.ApplyingCp;
                StateChanged?.Invoke();
                return;
            }
            // Nothing affordable to apply — leave the plan rows in place.
        }

        // Levelled up but applied no CP — keep the plan rows (per spec); the
        // refreshed level still re-anchors the projection.
        FinishWithReport();
    }

    private void OnAutoTrainStateChanged()
    {
        if (_phase == Phase.ApplyingCp && !_autoTrain.IsBusy)
        {
            _cpApplied = true;
            RemoveFulfilledPlanRows(_cpTargetLevel);   // CP committed → consume those rows
            FinishWithReport();
            return;
        }
        StateChanged?.Invoke();
    }

    // CP through _cpTargetLevel has been applied (the train-stats screen committed).
    // Because plan rows are cumulative targets, applying the row for that level
    // satisfies every lower row too — drop all rows up to and including it from the
    // saved plan, then tell the CP Allocation tab to reload.
    private void RemoveFulfilledPlanRows(int throughLevel)
    {
        if (_profile.Current is not { CharacterPlan: { } plan }) return;
        if (plan.RemoveAll(e => e.Level <= throughLevel) == 0) return;
        if (plan.Count == 0) _profile.Current.CharacterPlan = null;
        _profile.Save();
        _log?.Info("AutoTrain", $"Applied + cleared CP plan rows through level {throughLevel}.");
        PlanApplied?.Invoke();
    }

    // PlayerStats.Exp is the live experience total — StatParser re-anchors it on
    // every stat/exp poll and accrues each "You gain N experience." line. We react
    // to its change so the armed run fires the instant a kill crosses the next-level
    // threshold (no exp poll), and so CanTrainNow refreshes for the UI.
    private void OnStatsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlayerStats.Exp)) return;
        if (!IsBusy && EngineActive && ReadSettings().AutoTrain && CanLevelNow())
            // Armed auto-train: detour + train one level, applying CP per the toggle.
            Begin(loop: false, applyCp: ReadSettings().AutoTrainStats, reply: null);
        else
            StateChanged?.Invoke();
    }

    // Build the @train status report (if a reply sink is bound) while run state is
    // intact, then tear the run down and resume the engine, then deliver the reply.
    private void FinishWithReport()
    {
        string? report = _reply is not null
            ? BuildReport(_levelsTrained, _stopReason, _cpApplied, _cpTargetLevel,
                          _attainedLevel > 0 ? _attainedLevel : _startLevel)
            : null;
        Action<string>? reply = _reply;
        Finish(null);
        if (reply is not null && report is not null) reply(report);
    }

    private void Finish(string? reason)
    {
        if (reason is not null) _log?.Info("AutoTrain", reason);
        ResumeTarget resume = _resume;
        _phase = Phase.Idle;
        _target = null;
        _resume = default;
        _applyCp = false;
        _loopTrain = false;
        _cpApplied = false;
        _reply = null;
        _levelsTrained = 0;
        _trainSteps = 0;
        _startLevel = 0;
        _attainedLevel = 0;
        _cpTargetLevel = 0;
        _stopReason = StopReason.None;
        StateChanged?.Invoke();
        ResumeEngine(resume);
    }

    // Compose the one-line @train status reply. Single-train mode (Mode A) gets a
    // terse outcome; looping modes (B/C) report the level count, why we stopped,
    // how many banked levels remain, and — Mode C — the CP level we allocated to.
    private string BuildReport(int trained, StopReason stop, bool cpApplied, int cpLevel, int finalLevel)
    {
        if (!_loopTrain)
        {
            if (trained > 0) return $"Trained to level {finalLevel}.";
            return stop switch
            {
                StopReason.ProgressedTooFar => "Can't train — you've progressed too far for this trainer.",
                StopReason.NoMoney => "Can't train — not enough money for training.",
                _ => "Nothing to train.",
            };
        }

        var sb = new StringBuilder();
        sb.Append(trained > 0
            ? $"I've trained {trained} level{(trained == 1 ? "" : "s")}"
            : "Couldn't train any levels");
        switch (stop)
        {
            case StopReason.ProgressedTooFar: sb.Append(", unable to train further"); break;
            case StopReason.NoMoney: sb.Append(", out of money"); break;
            case StopReason.Timeout: sb.Append(", training stalled"); break;
        }
        int remain = CountBankableAbove(finalLevel);
        if (remain > 0) sb.Append($", {remain} remain");
        if (cpApplied) sb.Append($", CP allocated through level {cpLevel}");
        sb.Append('.');
        return sb.ToString();
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
    private bool CanLevelNow() => _stats.Level > 0 && CanReachLevel(_stats.Level + 1);

    // Does the live exp total already meet <paramref name="level"/>'s cumulative
    // threshold? — the same test the Level Projection tab uses per row.
    private bool CanReachLevel(int level)
    {
        if (level <= 0) return false;
        int chart = Chart();
        if (chart <= 0) return false;
        return _stats.Exp >= ExperienceTableCalculator.CalcExpNeeded(level, chart, _gameData.ActiveRealm);
    }

    // How many further levels above <paramref name="level"/> the banked exp can
    // still reach — the count of Level-Projection rows past `level` whose "Exp to
    // level" is already 0. Thresholds are monotonic, so the first miss ends it.
    private int CountBankableAbove(int level)
    {
        if (level <= 0) return 0;
        int chart = Chart();
        if (chart <= 0) return 0;
        int count = 0;
        for (int l = level + 1; l <= level + MaxTrainLoopSteps; l++)
        {
            if (_stats.Exp < ExperienceTableCalculator.CalcExpNeeded(l, chart, _gameData.ActiveRealm)) break;
            count++;
        }
        return count;
    }

    private int Chart() => ExperienceTableCalculator.CalcExpChart(
        GetInt(_gameData.FindRowByName("Classes", _stats.Class), "ExpTable"),
        GetInt(_gameData.FindRowByName("Races", _stats.Race), "ExpTable"));

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

    private HashSet<string> ReadDisabledTrainers()
    {
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        if (ReadSettings().DisabledTrainers is { } list)
            foreach (string key in list) disabled.Add(key);
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
        _tooFarSub.Dispose();
        _noMoneySub.Dispose();
        _stats.PropertyChanged -= OnStatsPropertyChanged;
        _statParser.ScreenParsed -= OnStatScreenParsed;
        _autoTrain.StateChanged -= OnAutoTrainStateChanged;
    }
}
