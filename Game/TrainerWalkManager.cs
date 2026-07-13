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

// The manual / armed entry point for "go train". Resolves the nearest
// allowed, level-appropriate trainer (TrainerCatalog.SelectNearest + BFS
// distance), detours a running loop / auto-lair to it, sends train on arrival,
// and — once a train-success line confirms the level-up (stock "attain level N"
// or the level-less Paradigm "train to the next level" wording) — refreshes
// stats and hands off to AutoTrainManager to apply the CP plan, then resumes
// the engine.
//
// Can-level detection without polling. PlayerStats.Exp is the live experience
// total — StatParser re-anchors it on every stat/exp poll and accrues each
// "You gain N experience." line. "Can train" is that total meeting the next
// level's cumulative threshold (ExperienceTableCalculator.CalcExpNeeded) —
// exactly the top Level-Projection row's Exp to level hitting 0. We watch
// PlayerStats.PropertyChanged, so a kill that crosses the threshold fires the
// armed run immediately; no exp poll.
//
// Engine detour mirrors AutoDepositManager: snapshot the running Loop /
// Auto-Lair, stop it (stop-and-restart, not a gate, so the detour walk owns
// the wire), train, then restart it. Manual Train Now does the same when an
// engine is running; with none it just walks + trains. Both the armed run and
// Train Now stop once only AutoTrainerSettings.LevelsToKeep bankable levels
// remain (the reserve), and both gate the CP-apply on the Auto-train-stats
// toggle. The one always-applies path is the Train Now CP-only reconcile
// (SetCpSpendConfirm): when no banked level is left to train but the current
// level has an unapplied, affordable plan raise, it prompts and — on yes —
// allocates CP without a fresh level-up.
public sealed class TrainerWalkManager : IDisposable
{
    private enum Phase { Idle, Walking, Training, RefreshingStats, ApplyingCp }
    private enum ResumeKind { None, Loop, Lair }

    // Why a (looping) train run stopped — shapes the @train reply.
    private enum StopReason { None, ProgressedTooFar, NoMoney, Timeout }

    private readonly record struct ResumeTarget(ResumeKind Kind, Loop? Loop);

    // Hard cap on loop iterations / the bankable-level scan — a safety net so a
    // misbehaving trainer can never spin the train loop unbounded.
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
    private readonly IDisposable _nextLevelSub;
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
    private bool _loopTrain;          // keep training across banked levels (looping @train / Train Now)
    private bool _cpOnlyRun;          // CP reconcile — allocate CP without a fresh level-up
    private int _keepLevels;          // bankable levels to leave untrained this run (the reserve)
    private StopReason _stopReason;   // why a looping run stopped (shapes the reply)
    private Action<string>? _reply;   // @train deferred reply sink (null for local runs)
    private TrainerShop? _target;
    private ResumeTarget _resume;

    // UI prompt for the CP reconcile (Train Now path only); abstract delegate so
    // the Game layer stays UI-free. Null until MainWindowViewModel wires it.
    private Func<Task<bool>>? _confirmCpSpend;
    private bool _confirming;         // a CP-spend confirm dialog is open (gates IsBusy)

    // How long to wait for the "attain level" line after sending train.
    public TimeSpan TrainConfirmTimeout { get; } = TimeSpan.FromSeconds(8);
    // How long to wait for the post-train stat refresh before giving up on CP.
    public TimeSpan StatRefreshTimeout { get; } = TimeSpan.FromSeconds(8);

    // Raised when IsBusy / CanTrainNow may have changed.
    public event Action? StateChanged;

    // Raised after a level's CP plan row is applied + removed — the CP tab reloads.
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
        _nextLevelSub = router.Subscribe(KnownPatterns.TrainAttainNextLevel, OnTrainedNextLevel);
        _tooFarSub = router.Subscribe(KnownPatterns.TrainProgressedTooFar, OnProgressedTooFar);
        _noMoneySub = router.Subscribe(KnownPatterns.TrainNoMoney, OnNoMoney);
        _stats.PropertyChanged += OnStatsPropertyChanged;
        _statParser.ScreenParsed += OnStatScreenParsed;
        _autoTrain.StateChanged += OnAutoTrainStateChanged;
        _autoTrain.PlanCommitted += OnCpPlanCommitted;
    }

    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    // Wires the prompt shown when Train Now finds no banked level to train but the
    // current level has an unapplied, affordable CP plan raise (the stuck-at-level-N
    // reconcile). Returns the user's yes/no. Kept as an abstract delegate so the
    // Game layer never references the UI dialog directly.
    public void SetCpSpendConfirm(Func<Task<bool>> confirm) => _confirmCpSpend = confirm;

    // True while a walk/train run (or the CP-apply it hands off, or a CP-spend
    // confirm prompt) is in flight.
    public bool IsBusy => _phase != Phase.Idle || _autoTrain.IsBusy || _confirming;

    // True when Train Now would do something: a CP plan is applicable, or we can level.
    public bool CanTrainNow => _autoTrain.CanTrainNow || CanLevelNow();

    // Manual trigger (CP Allocation tab "Train Now"). Assesses the banked-exp /
    // buffer situation before acting:
    //   * banked levels above the AutoTrainerSettings.LevelsToKeep reserve → walk +
    //     loop train down to the reserve, applying the CP plan per the
    //     Auto-train-stats toggle;
    //   * no bankable level but the current level has an affordable, unapplied CP
    //     plan raise → prompt the user, and on yes walk + allocate CP without
    //     levelling (the stuck-at-level-N reconcile);
    //   * otherwise nothing to do — log why.
    public void TrainNow()
    {
        if (IsBusy || !_wire.IsBound) return;
        _ = TrainNowAsync();
    }

    private async Task TrainNowAsync()
    {
        AutoTrainerSettings s = ReadSettings();
        int keep = Math.Max(0, s.LevelsToKeep);
        int toTrain = TrainBudgetCalculator.LevelsToTrain(
            _stats.Exp, _stats.Level, Chart(), _gameData.ActiveRealm, keep, MaxTrainLoopSteps);

        // Banked levels past the reserve → train them (loop), applying CP if the
        // Auto-train-stats toggle is on. The loop stops once only `keep` remain.
        if (toTrain > 0)
        {
            Begin(loop: true, applyCp: s.AutoTrainStats, reply: null, keepLevels: keep);
            return;
        }

        // No banked level to train. If the current level still has an affordable,
        // unapplied CP plan raise, that's the stuck-at-level-N state (a train fired
        // but the allocation didn't) — offer to spend CP without a fresh level-up.
        if (!_autoTrain.CanTrainNow)
        {
            int banked = CountBankableAbove(_stats.Level);   // only the reserve message needs it
            _log?.Info("AutoTrain", banked > 0
                ? $"Nothing to train — keeping {banked} banked level{(banked == 1 ? "" : "s")} in reserve."
                : "Nothing to train — no banked levels and CP already allocated.");
            return;
        }

        bool proceed = false;
        if (_confirmCpSpend is { } confirm)
        {
            _confirming = true;
            StateChanged?.Invoke();
            try { proceed = await confirm(); }
            catch { proceed = false; }   // dialog failed → treat as "no"
            finally { _confirming = false; StateChanged?.Invoke(); }
        }

        if (proceed && !IsBusy) BeginCpReconcile();
    }

    // Remote @train trigger — train where we are (no walk; assumes we're already at
    // a trainer). Behaviour follows the Auto-Trainer toggles:
    //   * neither set → a single train;
    //   * Auto-train set → loop train across every banked level until the trainer
    //     rejects us (progressed-too-far / no-money) or the banked exp runs out,
    //     then report;
    //   * both set → as above, plus apply the CP plan through the final level.
    // reply (when supplied) receives a one-line status report once the run settles.
    // No engine detour — a remote train is used while parked.
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

    private void Begin(bool loop, bool applyCp, Action<string>? reply, int keepLevels = 0)
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
        _keepLevels = keepLevels;
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

    // CP reconcile: walk to a trainer and apply the current level's CP plan without
    // training a new level. Sends no `train` — on arrival it refreshes `stat`
    // (re-anchoring stored CP) and hands straight to the train-stats screen.
    private void BeginCpReconcile()
    {
        if (IsBusy || !_wire.IsBound) return;
        if (_tracker.State.CurrentRoom is not { } cur)
        {
            _log?.Info("AutoTrain", "Can't allocate CP — current room unknown (walk a step to locate).");
            return;
        }

        TrainerShop? target = SelectNearest(cur.Key);
        if (target is not { } t)
        {
            _log?.Info("AutoTrain",
                $"No reachable allowed trainer serves level {_stats.Level} to allocate CP at.");
            return;
        }

        ResetRunState(loop: false, applyCp: true, reply: null);
        _cpOnlyRun = true;
        _target = t;
        _startLevel = _stats.Level;
        _resume = SnapshotEngine();
        StopEngine();

        var room = new RoomKey(t.Map, t.Room);
        if (cur.Key == room)
        {
            SendStatRefresh();
        }
        else if (_walker.WalkTo(room))
        {
            _phase = Phase.Walking;
            _log?.Info("AutoTrain", $"Walking to {t.Name} ({t.Map}/{t.Room}) to allocate CP.");
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
        _cpOnlyRun = false;
        _keepLevels = 0;
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
            {
                // CP-only reconcile skips `train` and goes straight to a stat refresh
                // → train-stats screen; every other run trains on arrival.
                if (_cpOnlyRun) SendStatRefresh();
                else SendTrain();
            }
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

    // Stock: "...you receive training to attain level N." — level-up confirmation
    // carrying the attained level in group 1.
    private void OnTrained(MatchResult m)
    {
        if (_phase != Phase.Training) return;
        if (m.Groups.Count == 0 || !int.TryParse(m.Groups[0], out int level))
        {
            Finish("Couldn't parse the trained level.");
            return;
        }
        RegisterTrainSuccess(level);
    }

    // Paradigm/ParaMud: "You hand over N copper farthings to train to the next
    // level!" — a successful train with no level number, so infer current+1.
    private void OnTrainedNextLevel(MatchResult m)
    {
        if (_phase != Phase.Training) return;
        RegisterTrainSuccess(explicitLevel: null);
    }

    // Shared level-up bookkeeping for both train-success wordings. A level-less
    // success (Paradigm) infers the new level as one past the last confirmed
    // level (the prior attained level mid-loop, else the run's start level) —
    // each success is exactly one level, so the running count stays accurate.
    private void RegisterTrainSuccess(int? explicitLevel)
    {
        int prior = _attainedLevel > 0 ? _attainedLevel : _startLevel;
        _attainedLevel = explicitLevel ?? prior + 1;
        _levelsTrained++;
        _log?.Info("AutoTrain", $"Trained to level {_attainedLevel} ({_levelsTrained} this run).");

        // Looping mode keeps training while banked exp can reach a level past the
        // reserve (and the safety cap holds). We never refresh stats between loop
        // steps — PlayerStats.Level lags, so the bankable check rides _stats.Exp
        // (fixed mid-loop) against the just-attained level.
        if (_loopTrain && _trainSteps < MaxTrainLoopSteps && CountBankableAbove(_attainedLevel) > _keepLevels)
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
        SendStatRefresh();
    }

    // Poll `stat` and wait for the parsed screen — re-anchors Level/Exp/CP and
    // feeds the CP-apply. Shared by the post-train settle and the CP-only reconcile.
    private void SendStatRefresh()
    {
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
        if (_phase != Phase.RefreshingStats) return;
        // A post-train settle must match the level we just attained (filters stray
        // stat screens mid-run); the CP-only reconcile has no attained level and
        // trusts whatever current level the refreshed screen reports.
        if (!_cpOnlyRun && snapshot.Level != _attainedLevel) return;

        int targetLevel = _cpOnlyRun ? snapshot.Level : _attainedLevel;
        bool wantCp = _cpOnlyRun || _applyCp;

        // Apply the CP plan when the run wants it (Train Now, @train both-toggles,
        // or the CP reconcile) and a row exists for the target level. CpPlanEntry
        // rows are cumulative targets, so applying that level's row against the
        // accumulated CP budget covers every level we trained through in one pass.
        bool hasPlan = _profile.Current?.CharacterPlan?.Any(e => e.Level == targetLevel) ?? false;
        if (wantCp && hasPlan)
        {
            _log?.Info("AutoTrain", $"Applying CP plan through level {targetLevel}.");
            _cpTargetLevel = targetLevel;
            _autoTrain.TrainNow();
            if (_autoTrain.IsBusy)
            {
                _phase = Phase.ApplyingCp;
                StateChanged?.Invoke();
                return;
            }
            // Nothing affordable to apply — leave the plan rows in place.
        }

        // Levelled up (or reconciled) but applied no CP — keep the plan rows (per
        // spec); the refreshed level still re-anchors the projection.
        FinishWithReport();
    }

    // The CP keystroke replay has committed (raises + SAVE on the wire). Clear the
    // fulfilled plan rows now instead of letting the grid linger until the menu-exit
    // prompt round-trip (or AutoTrainManager's exit-grace fallback) releases the run.
    // The run itself still finishes on the idle transition in OnAutoTrainStateChanged.
    private void OnCpPlanCommitted()
    {
        if (_phase != Phase.ApplyingCp || _cpApplied) return;
        _cpApplied = true;
        RemoveFulfilledPlanRows(_cpTargetLevel);
    }

    private void OnAutoTrainStateChanged()
    {
        if (_phase == Phase.ApplyingCp && !_autoTrain.IsBusy)
        {
            // Rows are cleared only by OnCpPlanCommitted, which fires when the
            // keystroke replay actually committed the raises + SAVE. Reaching idle
            // without that signal means the replay never ran (trainer screen never
            // opened / aborted) — keep the plan rows so a later attempt can retry,
            // and leave _cpApplied false so the report reflects "CP not applied".
            if (!_cpApplied)
                _log?.Info("AutoTrain", "CP plan not applied (trainer screen didn't open) — plan rows kept.");
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
        AutoTrainerSettings s = ReadSettings();
        int keep = Math.Max(0, s.LevelsToKeep);
        if (!IsBusy && EngineActive && s.AutoTrain && CountBankableAbove(_stats.Level) > keep)
            // Armed auto-train: detour + loop-train down to the reserve, applying
            // CP per the Auto-train-stats toggle.
            Begin(loop: true, applyCp: s.AutoTrainStats, reply: null, keepLevels: keep);
        else
            StateChanged?.Invoke();
    }

    // Build the status report while run state is intact, then tear the run down and
    // resume the engine. A bound @train reply sink receives it; otherwise (the local
    // Train Now / armed path) it goes to the log so the user still sees the outcome.
    private void FinishWithReport()
    {
        string report = BuildReport(_levelsTrained, _stopReason, _cpApplied, _cpTargetLevel,
                                    _attainedLevel > 0 ? _attainedLevel : _startLevel);
        Action<string>? reply = _reply;
        Finish(null);
        if (reply is not null) reply(report);
        else _log?.Info("AutoTrain", report);
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
        _cpOnlyRun = false;
        _keepLevels = 0;
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

    // Compose the one-line status line (the @train reply, or the Train Now / armed
    // log). A single train gets a terse outcome; a looping run reports the level
    // count, why we stopped, how many banked levels remain, and — when CP was
    // applied — the level we allocated through.
    private string BuildReport(int trained, StopReason stop, bool cpApplied, int cpLevel, int finalLevel)
    {
        // The CP reconcile trained nothing — it only allocated (or found no) CP.
        if (_cpOnlyRun)
            return cpApplied ? $"Allocated CP at level {cpLevel}." : "No CP to allocate.";

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

    // True when banked exp can reach a level past the reserve — i.e. Train Now would
    // actually train something. Honours the LevelsToKeep buffer so a character sitting
    // exactly on its reserve doesn't light the button up.
    private bool CanLevelNow()
    {
        if (_stats.Level <= 0) return false;
        int keep = Math.Max(0, ReadSettings().LevelsToKeep);
        return CountBankableAbove(_stats.Level) > keep;
    }

    // How many further levels above level the banked exp can still reach — the
    // count of Level-Projection rows past level whose "Exp to
    // level" is already 0. Delegates to the pure budgeter so the boundary logic
    // (exact-threshold counts, monotonic stop, cap) is unit-tested in one place.
    private int CountBankableAbove(int level) =>
        TrainBudgetCalculator.BankableLevels(_stats.Exp, level, Chart(), _gameData.ActiveRealm, MaxTrainLoopSteps);

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
        _nextLevelSub.Dispose();
        _tooFarSub.Dispose();
        _noMoneySub.Dispose();
        _stats.PropertyChanged -= OnStatsPropertyChanged;
        _statParser.ScreenParsed -= OnStatScreenParsed;
        _autoTrain.StateChanged -= OnAutoTrainStateChanged;
        _autoTrain.PlanCommitted -= OnCpPlanCommitted;
    }
}
