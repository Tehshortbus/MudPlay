using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MudPlay.Game.Calculators;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Game.Train;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game;

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
    // PartyTrip: a party auto-train trip owns the engine detour (the loop / auto-lair
    // is stopped and snapshotted here) while PartyTrainCoordinator drives the walk
    // and the members' trains; the leader's own train runs inside it and returns to
    // PartyTrip rather than resuming the engine.
    // Spells: the levels are trained and the run has gone on to the shops for the
    // scrolls they unlocked (ShopSpellErrand drives the walker).
    private enum Phase { Idle, Funding, Walking, Training, RefreshingStats, ApplyingCp, Spells, PartyTrip }
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
    private readonly Game.Light.ErrandLightStop? _lightStop;
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
    private Action<string>? _refuse;  // @train: where a report of nothing trained goes (null: _reply)
    private TrainerShop? _target;
    private ResumeTarget _resume;
    private bool _walkRun;            // the run walked to its trainer (Train Now / armed), not @train in place
    private RoomKey _runFrom;         // where a walk run started — the shop leg plans its way back here
    private bool _spellTripTried;     // the shop leg has had its one go this run
    private bool _spellsOnly;         // a shop leg started by hand: no training, no train report
    private bool _fundingWithSpells;  // the funding errand in flight is collecting scroll money too

    // Party-train run shaping. _ceilingCap tightens DoNotTrainAbove for this run only
    // (the level-11 party rule, or the leader's target for a member); _noChain stops a
    // run walking on to the next trainer — in a party that would leave everyone behind.
    private int _ceilingCap;
    private bool _noChain;
    private Action<int, string>? _partyDone;   // (levels trained, report) for a party train
    private bool _partyTrip;                   // a party trip holds the engine detour
    private bool _partyTripTrained;

    // UI prompt for the CP reconcile (Train Now path only); abstract delegate so
    // the Game layer stays UI-free. Null until MainWindowViewModel wires it.
    private Func<Task<bool>>? _confirmCpSpend;
    private bool _confirming;         // a CP-spend confirm dialog is open (gates IsBusy)

    // How long to wait for the "attain level" line after sending train.
    public TimeSpan TrainConfirmTimeout { get; } = TimeSpan.FromSeconds(8);
    // How long to wait for the post-train stat refresh before giving up on CP.
    public TimeSpan StatRefreshTimeout { get; } = TimeSpan.FromSeconds(8);
    // How often a run parked at the trainer reads `stat` again while a stat is
    // altered. How long it keeps at it is AutoTrainerSettings.AlteredStatsWaitSeconds.
    public TimeSpan AlteredStatsRereadDelay { get; set; } = TimeSpan.FromSeconds(20);
    private readonly Game.Train.AlteredStatsWait _alteredStats = new();
    // The user's setting for how long that wait may last (0 = don't wait).
    public int AlteredStatsWaitSeconds =>
        Math.Clamp(ReadSettings().AlteredStatsWaitSeconds, 0, AutoTrainerSettings.MaxAlteredStatsWaitSeconds);

    // Raised when IsBusy / CanTrainNow may have changed.
    public event Action? StateChanged;

    // Raised after a level's CP plan row is applied + removed — the CP tab reloads.
    public event Action? PlanApplied;

    public TrainerWalkManager(PlayerStats stats, StatParser statParser, GameDataCache gameData,
                              ProfileService profile, RoomTracker tracker, BfsMapper bfs,
                              AutoWalkManager walker, LoopRunner loopRunner, AutoLairManager autoLair,
                              AutoTrainManager autoTrain, MessageRouter router, LogService? log = null,
                              // Auto-Light's stop for a walk to a trainer that runs dark: the
                              // walk is this run's, so the stop is too, since the walk-to light
                              // router stands down while an errand has the walker. Unwired,
                              // the walks go as they are.
                              Game.Light.ErrandLightStop? lightStop = null)
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
        _lightStop = lightStop;

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

    // Collects the money before a run commits to a trainer. Optional: with none
    // wired the manager behaves exactly as it always did — walk, train, and learn
    // it can't pay from the server's rejection. Set from AppServices.
    private Game.Train.TrainFundingRouter? _funding;

    public void SetFundingRouter(Game.Train.TrainFundingRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        _funding = router;
        _funding.Finished += OnFundingFinished;
    }

    // The shop leg after the training: buys and reads the scrolls for spells the
    // character can now learn. Optional, like the funding errand. Set from AppServices.
    private Game.Train.ShopSpellErrand? _spellErrand;

    public void SetSpellErrand(Game.Train.ShopSpellErrand errand)
    {
        ArgumentNullException.ThrowIfNull(errand);
        _spellErrand = errand;
        _spellErrand.Finished += OnSpellErrandFinished;
    }

    // What the shop leg would buy for a character at a level, walking from a room and
    // then on to a second — unbudgeted, so funding can price the scrolls into the trip.
    public Func<RoomKey, RoomKey, int, Game.Train.ShopSpellPlan>? PlanShopSpells { get; set; }

    // Renders "you're short N, here's when that stops being true" from the live
    // session earn rate. A delegate because the rate and the lap time live in the
    // session/loop trackers, which this manager has no business reaching into.
    public Func<long, string>? DescribeShortfall { get; set; }

    // How long until earnings are projected to close a shortfall, or null when the
    // rate is too low to say. Drives the retry hold below.
    public Func<long, TimeSpan?>? EstimateWaitToAfford { get; set; }

    // Nothing re-checks funding until this passes. Without it the armed trigger
    // re-prices the entire run on EVERY exp gain while broke — a full itinerary BFS
    // plus a funding BFS per source, per kill, forever, each one logging the same
    // shortfall. Grinding is exactly the state a broke character is in, so that's
    // the common case, not an edge one.
    private DateTimeOffset _fundingRetryAt = DateTimeOffset.MinValue;

    private void HoldFundingRetry(long shortfall) =>
        _fundingRetryAt = DateTimeOffset.Now
            + Game.Train.TrainFundingForecast.RetryDelay(EstimateWaitToAfford?.Invoke(shortfall));

    // Invoked once a run that actually trained something has finished and the loop /
    // auto-lair is running again. Bound to the auto-deposit check, so a purse left
    // heavy by a withdraw-and-train trip gets banked on the way back rather than
    // carried around the circuit.
    public Action? AfterTrainRun { get; set; }

    // True when the SOLO armed run may start. Training is a realm excursion that
    // disbands a party server-side, so the solo run never fires in a group — party
    // training goes through PartyTrainCoordinator instead. Null = no restriction.
    public Func<bool>? CanStartRun { get; set; }

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

    // The shop leg can be started by hand: it is wired and nothing is in flight.
    public bool CanBuySpellsNow => _spellErrand is not null && !IsBusy && _wire.IsBound;

    // A shop leg started by hand is under way (fetching the coin, or at the shops).
    public bool IsBuyingSpells => _spellsOnly && _phase != Phase.Idle;

    // Raised when a shop leg started by hand is over, with what it learned.
    public event Action<string>? SpellRunFinished;

    // Manual trigger (CP Allocation tab "Buy spells"): the shop leg of a train trip
    // on its own — walk to the shops selling scrolls for spells the character can
    // learn at its level, buy and read them — whatever Auto-obtain spells from shops
    // is set to. That box decides whether a train trip goes on to the shops by
    // itself; the per-spell checklist under it still decides which spells are wanted.
    // The scrolls are paid for the way a train trip pays: from the purse, with the
    // funding errand fetching the rest first. A running loop or Auto-Lair is stopped
    // for the trip and resumed after, as Train Now does.
    //
    // Null when the run is under way; otherwise why it didn't start.
    public string? BuySpellsNow()
    {
        if (IsBusy) return "A train or spell run is already under way.";
        if (_spellErrand is null || !_wire.IsBound) return "Not connected.";
        if (_tracker.State.CurrentRoom is not { } cur) return "Current room unknown — walk a step to locate.";

        int level = _stats.Level;
        Game.Train.ShopSpellPlan? wanted = PlanShopSpells?.Invoke(cur.Key, cur.Key, level);
        if (wanted is not { Stops.Count: > 0 } && !_spellErrand.HasCarriedScrolls(level))
        {
            string why = wanted is { Unreachable.Count: > 0 }
                ? $"No spell scrolls to buy — can't reach a shop for {string.Join(", ", wanted.Unreachable)}."
                : $"No spell scrolls to buy at level {level}.";
            _log?.Info("AutoTrain", why);
            return why;
        }

        ResetRunState(loop: false, applyCp: false, reply: null);
        _spellsOnly = true;
        _startLevel = level;
        _runFrom = cur.Key;
        _resume = SnapshotEngine();
        StopEngine();
        _log?.Info("AutoTrain", $"Buy spells: checking the spell shops for level {level}.");

        if (_funding is not null && wanted is { CostCopper: > 0, Stops.Count: > 0 })
        {
            switch (_funding.Begin(wanted.CostCopper, wanted.Stops[0].Room,
                        TripTolls(Array.Empty<Game.Train.TrainSegment>(), cur.Key, wanted)))
            {
                case Game.Train.TrainFundingStart.Collecting:
                    _phase = Phase.Funding;
                    StateChanged?.Invoke();
                    return null;
                case Game.Train.TrainFundingStart.Short:
                    _log?.Info("AutoTrain", "Buy spells: can't cover every scroll — buying what the purse stretches to.");
                    break;
            }
        }
        BeginSpellsOnlyErrand();
        return null;
    }

    // The coin is settled (or as settled as it gets): go to the shops. A purse that
    // stretches to nothing ends the run here.
    private void BeginSpellsOnlyErrand()
    {
        if (_spellErrand is null || !_spellErrand.Begin(_stats.Level, _runFrom))
        {
            const string none = "Nothing affordable to buy.";
            Finish($"Buy spells: {none}");
            SpellRunFinished?.Invoke(none);
            return;
        }
        _phase = Phase.Spells;
        StateChanged?.Invoke();
    }

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
        // An explicit click overrides any funding back-off the armed path is sitting
        // on — the user asking to train now has better information than our
        // projection of when they'd be able to afford it.
        _fundingRetryAt = DateTimeOffset.MinValue;
        _ = TrainNowAsync();
    }

    private async Task TrainNowAsync()
    {
        AutoTrainerSettings s = ReadSettings();
        int keep = Math.Max(0, s.LevelsToKeep);
        int ceiling = Math.Max(0, s.DoNotTrainAbove);
        int toTrain = TrainBudgetCalculator.LevelsToTrain(
            _stats.Exp, _stats.Level, Chart(), _gameData.ActiveRealm, keep, MaxTrainLoopSteps, ceiling);

        // Banked levels past the reserve (and under the ceiling) → train them (loop),
        // applying CP if the Auto-train-stats toggle is on. The loop stops once only
        // `keep` remain or the ceiling is reached.
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
            _log?.Info("AutoTrain",
                banked > 0 && !TrainBudgetCalculator.WithinCeiling(_stats.Level, ceiling)
                    ? $"Nothing to train — at the level ceiling ({ceiling}); {banked} banked level{(banked == 1 ? "" : "s")} held."
                : banked > 0
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
    // refuse (when supplied) receives it in reply's place when the run trained no
    // level and allocated no CP: that report is a failure answer, which the remote
    // command sends under the Warn setting.
    // No engine detour — a remote train is used while parked.
    public void TrainInPlace(Action<string>? reply = null, Action<string>? refuse = null)
    {
        if (IsBusy || !_wire.IsBound) return;
        AutoTrainerSettings s = ReadSettings();
        // Decoupled: AutoTrain drives the level-loop, AutoTrainStats drives the CP
        // apply — each independent (either, neither, or both).
        ResetRunState(loop: s.AutoTrain, applyCp: s.AutoTrainStats, reply);
        _refuse = refuse;
        _target = null;       // no walk target — we're assumed to be at the trainer
        _resume = default;    // and not detouring a running engine
        _startLevel = _stats.Level;
        SendTrain();
    }

    // Party-train in place: loop-train down to the reserve under a party ceiling, and
    // never chain to another trainer. Used by a member when the leader says "train"
    // at a stop, and by the leader at the trip's final stop (inside BeginPartyTrip).
    // done receives (levels trained, report) once the run settles — always, even when
    // there was nothing to train, so the coordinator never waits on silence.
    public void TrainForParty(int ceilingCap, Action<int, string> done)
    {
        ArgumentNullException.ThrowIfNull(done);
        bool inTrip = _phase == Phase.PartyTrip;
        if ((!inTrip && IsBusy) || !_wire.IsBound)
        {
            done(0, "busy");
            return;
        }

        AutoTrainerSettings s = ReadSettings();
        int keep = Math.Max(0, s.LevelsToKeep);
        int ceiling = MinCeiling(s.DoNotTrainAbove, ceilingCap);
        if (CountBankableAbove(_stats.Level) <= keep || !TrainBudgetCalculator.WithinCeiling(_stats.Level, ceiling))
        {
            done(0, "nothing to train");
            return;
        }

        ResetRunState(loop: true, applyCp: s.AutoTrainStats, reply: null);
        _keepLevels = keep;
        _ceilingCap = ceilingCap;
        _noChain = true;
        _partyDone = done;
        _target = null;
        if (!inTrip) _resume = default;   // a member trains where it stands; no engine detour
        _startLevel = _stats.Level;
        _log?.Info("AutoTrain", $"Party train at level {_startLevel} (ceiling {(ceiling > 0 ? ceiling : "none")}).");
        SendTrain();
    }

    // Start a party trip: snapshot + stop the running loop / auto-lair so the trip's
    // walk owns the wire, and hold IsBusy (so the solo armed run and Train Now stay
    // out) until EndPartyTrip. False when something else is already running.
    public bool BeginPartyTrip()
    {
        if (IsBusy || !_wire.IsBound) return false;
        _resume = SnapshotEngine();
        StopEngine();
        _partyTrip = true;
        _partyTripTrained = false;
        _phase = Phase.PartyTrip;
        _log?.Info("AutoTrain", "Party train trip started — engine paused for the detour.");
        StateChanged?.Invoke();
        return true;
    }

    // End the party trip and put the engine back. The movement gate the party
    // re-form arms holds the resumed loop until the members are back.
    public void EndPartyTrip(string reason)
    {
        if (!_partyTrip) return;
        _partyTrip = false;
        bool trained = _partyTripTrained;
        _partyTripTrained = false;
        ReserveForTraining?.Invoke(0);
        _phase = Phase.Idle;
        ResumeTarget resume = _resume;
        _resume = default;
        _log?.Info("AutoTrain", $"Party train trip ended — {reason}");
        StateChanged?.Invoke();
        ResumeEngine(resume);
        if (trained) AfterTrainRun?.Invoke();
    }

    public bool PartyTripActive => _partyTrip;

    // A run of our own that took the engine's place is under way: the walk to a
    // trainer (Train Now, the armed run) or to the spell shops, from the moment
    // the engine is stopped for it until the run is finished. A party in tow
    // follows it, and a leader goes back for nobody in the middle of it
    // (PartyComebackManager.TrainTripRunning).
    public bool OwnRunActive => !_partyTrip && _phase != Phase.Idle && (_walkRun || _spellsOnly);

    // Told when such a run is over, after the engine it paused is back. False: it
    // was taken out of our hands (cancelled, its walk stopped under it, the spell
    // errand's walker taken over), and nothing was resumed.
    public Action<bool>? OwnRunOver { get; set; }

    private bool _runTakenOver;

    // This character's party-train picture, from its own Auto-Trainer thresholds:
    // Ready once the solo trigger would fire, Waiting (with a projected time to Ready
    // at expPerHour, -1 when unknown) before that, Blocked when the party ceiling
    // leaves nothing to train on the way — the level-11 rule, DoNotTrainAbove — or the
    // class / exp chart can't be resolved.
    public PartyTrainSelfAssessment AssessForParty(double expPerHour)
    {
        AutoTrainerSettings s = ReadSettings();
        int level = _stats.Level;
        int classNumber = ResolveClassNumber();
        int chart = Chart();
        int ceiling = PartyCeiling(s, level);
        long exp = _stats.Exp;
        long nextExp = level > 0 && chart > 0
            ? ExperienceTableCalculator.CalcExpNeeded(level + CountBankableAbove(level) + 1, chart, _gameData.ActiveRealm)
            : 0;
        if (level <= 0 || chart <= 0 || !TrainBudgetCalculator.WithinCeiling(level, ceiling))
            return new(level, classNumber, 0, 0, PartyTrainReadiness.Blocked, -1, exp, nextExp);

        int keep = Math.Max(0, s.LevelsToKeep);
        int toTrain = TrainBudgetCalculator.LevelsToTrain(
            _stats.Exp, level, chart, _gameData.ActiveRealm, keep, MaxTrainLoopSteps, ceiling);
        bool ready = toTrain > 0
            && TrainBudgetCalculator.ShouldFire(CountBankableAbove(level), keep, s.FireAtBankedLevels);
        if (ready)
            return new(level, classNumber, toTrain, CostToTrain(level, toTrain, classNumber),
                PartyTrainReadiness.Ready, 0, exp, nextExp);

        // Ready arrives when the banked count reaches the fire threshold (floored at
        // keep + 1, as ShouldFire does) — time to reach that level's exp at the rate.
        int need = Math.Max(keep + 1, Math.Max(0, s.FireAtBankedLevels));
        TimeSpan? eta = ExperienceTableCalculator.CalcTimeToLevel(
            ExperienceTableCalculator.CalcExpNeeded(level + need, chart, _gameData.ActiveRealm),
            _stats.Exp, (long)expPerHour);
        return new(level, classNumber, 0, 0, PartyTrainReadiness.Waiting,
            eta is { } t ? (int)Math.Min(int.MaxValue, t.TotalSeconds) : -1, exp, nextExp);
    }

    // The ceiling a party train runs under: DoNotTrainAbove, tightened to 10 below
    // level 11 when the level-11 step is left to a solo trip.
    public static int PartyCeiling(AutoTrainerSettings s, int level)
    {
        ArgumentNullException.ThrowIfNull(s);
        return MinCeiling(s.DoNotTrainAbove, s.PartySkipLevel11 && level < 11 ? 10 : 0);
    }

    // Two "0 = none" ceilings combined: the tighter one that's set.
    private static int MinCeiling(int a, int b)
    {
        a = Math.Max(0, a);
        b = Math.Max(0, b);
        if (a == 0) return b;
        if (b == 0) return a;
        return Math.Min(a, b);
    }

    private int EffectiveCeiling() => MinCeiling(ReadSettings().DoNotTrainAbove, _ceilingCap);

    // What training these levels costs across however many trainers they span, priced
    // from where we stand. 0 when we can't place ourselves (the trip's funding plan
    // then just doesn't count this member's fee).
    private long CostToTrain(int level, int levels, int classNumber)
    {
        if (_tracker.State.CurrentRoom is not { } cur) return 0;
        IReadOnlyList<Game.Train.TrainSegment> itinerary = Game.Train.TrainItineraryPlanner.Build(
            TrainerCatalog.Enumerate(_gameData), level, levels, classNumber,
            ReadDisabledTrainers(), cur.Key, _bfs.DistanceMemo());
        return Game.Train.TrainItineraryPlanner.TotalCost(itinerary);
    }

    public AutoTrainerSettings CurrentSettings => ReadSettings();

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
        _walkRun = true;
        _runFrom = cur.Key;
        _resume = SnapshotEngine();
        StopEngine();   // free the wire for the detour (no-op when nothing's running)

        // Price the whole run and make sure we can pay BEFORE walking. Without this
        // the run walks to the trainer and discovers it's broke from the server's
        // rejection line, having spent the trip to find out.
        if (BeginFunding(cur.Key, t)) return;

        WalkToTrainer(t, cur.Key);
    }

    // Walk to (or train at) the selected trainer. Split out of Begin so the funding
    // errand can re-enter it once the purse is settled.
    private void WalkToTrainer(TrainerShop t, RoomKey from)
    {
        var room = new RoomKey(t.Map, t.Room);
        if (from == room)
        {
            SendTrain();
        }
        else if (WalkToTrainerRoom(room))
        {
            _phase = Phase.Walking;
            _log?.Info("AutoTrain", $"Walking to {t.Name} ({t.Map}/{t.Room}) to train.");
            StateChanged?.Invoke();
        }
        else
        {
            Finish(_walker.AvoidBlockingRouteTo(room) is { } blocked
                ? $"Route to trainer {t.Name} ({t.Map}/{t.Room}) blocked by your avoid in room {blocked.Map}/{blocked.Room} — skipping."
                : $"No path to trainer {t.Name} ({t.Map}/{t.Room}).");
        }
    }

    // planThroughAcquirableGates: the trainer may sit behind (or the walk home may
    // re-enter) a gated area — key-doors, hidden exits, summon-drop keys,
    // multi-action gates — that a plain walk can't route through (report
    // paradigm-20260913-022254, same class of strand as the bank run). A walk that
    // runs dark goes by a light shop first (ErrandLightStop), once per run.
    private bool WalkToTrainerRoom(RoomKey room) =>
        _lightStop?.Walk(room, WalkThroughGates, LightStopTakenOver) ?? WalkThroughGates(room);

    private bool WalkThroughGates(RoomKey room) => _walker.WalkTo(room, planThroughAcquirableGates: true);

    // The walk to the trainer was stopped, or a walk started, while the light stop
    // had it: someone has taken over, so the run ends and the engine stays down.
    private void LightStopTakenOver(string detail)
    {
        if (_phase != Phase.Walking) return;
        _resume = default;
        _runTakenOver = true;
        Finish($"Light stop taken over: {detail} — run cancelled; the loop stays stopped.");
    }

    // Ask the funding errand whether this run can be paid for. Returns true when the
    // caller has nothing left to do — either an errand is collecting (we resume on
    // its Finished) or a run that hadn't set out yet has been ended as unaffordable.
    // False leaves the run with the caller: funded, or short part-way through a run,
    // which the caller's own give-up path settles.
    //
    // The bill is the WHOLE itinerary, not the first trainer's fee: trainers serve a
    // contiguous level band, so a banked run can span several, each charging its own
    // markup. Budgeting only the first leg would fund a trip that strands halfway.
    // The tolls and fares on a route (as the walk takes it once it can pay), and
    // whether a route round every toll exists. Funding budgets the trip's tolls on top
    // of the fees, so a toll on the way doesn't leave the purse short at the trainer
    // (report paradigm-20260930-204041), or routes round them when the money for both
    // isn't there. Null when routes can't be priced.
    public Func<RoomKey, RoomKey, long>? RouteTolls { get; set; }
    public Func<RoomKey, RoomKey, bool>? HasTollFreeRoute { get; set; }

    // Sets aside the trip's fees from the wallet the walk judges tolls against, so it
    // takes a toll only when it can pay it on top of the training (0 releases it).
    public Action<long>? ReserveForTraining { get; set; }

    private Game.Train.TrainTripTolls? TripTolls(
        IReadOnlyList<Game.Train.TrainSegment> itinerary, RoomKey runFrom, Game.Train.ShopSpellPlan? spells)
    {
        if (RouteTolls is not { } tolls || HasTollFreeRoute is not { } tollFree) return null;
        List<RoomKey> stops = itinerary.Select(s => new RoomKey(s.Trainer.Map, s.Trainer.Room)).ToList();
        if (spells is not null) stops.AddRange(spells.Stops.Select(s => s.Room));
        stops.Add(runFrom);
        return new Game.Train.TrainTripTolls(
            here =>
            {
                long copper = 0;
                RoomKey at = here;
                foreach (RoomKey stop in stops) { copper += tolls(at, stop); at = stop; }
                return copper;
            },
            here =>
            {
                RoomKey at = here;
                foreach (RoomKey stop in stops) { if (!tollFree(at, stop)) return false; at = stop; }
                return true;
            });
    }

    private bool BeginFunding(RoomKey from, TrainerShop first) =>
        PriceRun(from, first, withSpells: true)
            is Game.Train.TrainFundingHandoff.AwaitErrand or Game.Train.TrainFundingHandoff.Abandon;

    // BeginFunding's answer in full, for the caller that has to tell a funded run
    // from one that is short part-way through.
    //
    // withSpells adds the scrolls the shop leg would buy (and the tolls out to those
    // shops) to the bill. That is the first ask only: when it can't be met the run is
    // priced again for the training alone, so a scroll never holds a level back.
    private Game.Train.TrainFundingHandoff PriceRun(RoomKey from, TrainerShop first, bool withSpells)
    {
        if (_funding is null || _cpOnlyRun) return Game.Train.TrainFundingHandoff.Proceed;

        int levels = LevelsThisRun();
        if (levels <= 0) return Game.Train.TrainFundingHandoff.Proceed;

        IReadOnlyList<Game.Train.TrainSegment> itinerary = Game.Train.TrainItineraryPlanner.Build(
            TrainerCatalog.Enumerate(_gameData), RunLevel, levels, ResolveClassNumber(),
            ReadDisabledTrainers(), from, _bfs.DistanceMemo());
        long cost = Game.Train.TrainItineraryPlanner.TotalCost(itinerary);
        if (cost <= 0) return Game.Train.TrainFundingHandoff.Proceed;
        ReserveForTraining?.Invoke(cost);

        Game.Train.ShopSpellPlan? spells = withSpells ? ShopSpellsFor(itinerary, levels) : null;
        long scrolls = spells?.CostCopper ?? 0;
        _fundingWithSpells = scrolls > 0;
        if (scrolls <= 0) spells = null;

        var trainerRoom = new RoomKey(first.Map, first.Room);
        Game.Train.TrainFundingHandoff handoff = Game.Train.TrainFundingHandoffRule.For(
            _funding.Begin(cost + scrolls, trainerRoom, TripTolls(itinerary, from, spells)),
            _refusal.RecoveryClaimed);

        // The scrolls come off the bill before either short answer is taken as final.
        if (_fundingWithSpells
            && handoff is Game.Train.TrainFundingHandoff.StopShort or Game.Train.TrainFundingHandoff.Abandon)
            return FundTrainingAlone(from, first);

        switch (handoff)
        {
            case Game.Train.TrainFundingHandoff.Proceed:
                _fundingRetryAt = DateTimeOffset.MinValue;
                return handoff;                     // purse covers it — carry on

            case Game.Train.TrainFundingHandoff.StopShort:
                // The trainer refused and nothing reachable covers it either. Not
                // ours to end: the caller stops the train loop, so levels trained
                // before the refusal still get their stat refresh and report.
                _log?.Info("AutoTrain",
                    DescribeShortfall?.Invoke(_lastFundingShortfall)
                    ?? $"Can't afford training — short {_lastFundingShortfall:N0} copper.");
                return handoff;

            case Game.Train.TrainFundingHandoff.AwaitErrand:
                _phase = Phase.Funding;
                _log?.Info("AutoTrain",
                    $"Training {levels} level(s) across {itinerary.Count} trainer(s) costs {cost:N0} copper"
                    + (scrolls > 0 ? $", plus {scrolls:N0} for spell scrolls — " : " — ")
                    + (_funding.IsCheckingFunds
                        ? "checking the purse and bank before deciding."
                        : "collecting the difference first."));
                StateChanged?.Invoke();
                return handoff;

            default:
                // Nothing reachable covers it. Stay armed and say when that changes,
                // rather than walking somewhere pointless or disarming. The router
                // reports the exact gap on its Finished event, which has already
                // fired synchronously inside Begin by the time we get here.
                HoldFundingRetry(_lastFundingShortfall);
                _log?.Info("AutoTrain",
                    DescribeShortfall?.Invoke(_lastFundingShortfall)
                    ?? $"Can't afford training — short {_lastFundingShortfall:N0} copper.");
                Finish("Not enough money to train — staying armed.");
                return Game.Train.TrainFundingHandoff.Abandon;
        }
    }

    // The scrolls the shop leg would buy once this run's levels are trained, walking
    // on from the last trainer. Null when the shop leg won't run.
    private Game.Train.ShopSpellPlan? ShopSpellsFor(IReadOnlyList<Game.Train.TrainSegment> itinerary, int levels)
    {
        if (!_walkRun || itinerary.Count == 0 || PlanShopSpells is not { } plan) return null;
        if (!ReadSettings().AutoObtainShopSpells) return null;
        TrainerShop last = itinerary[^1].Trainer;
        return plan(new RoomKey(last.Map, last.Room), _runFrom, RunLevel + levels);
    }

    // The scrolls couldn't be paid for on top of the training: price the run again
    // without them. The shop leg still goes, and buys what the purse then stretches to.
    private Game.Train.TrainFundingHandoff FundTrainingAlone(RoomKey from, TrainerShop first)
    {
        _fundingWithSpells = false;
        _log?.Info("AutoTrain", "Can't cover the spell scrolls as well — funding the training alone.");
        return PriceRun(from, first, withSpells: false);
    }

    // Recorded on every result, acted on only while we're waiting. Begin's Short
    // branch fires this synchronously before _phase is Funding, so the gap has to be
    // captured unconditionally or that branch has nothing to report.
    private long _lastFundingShortfall;

    private void OnFundingFinished(Game.Train.TrainFundingResult result)
    {
        _lastFundingShortfall = result.ShortfallCopper;
        if (_phase != Phase.Funding) return;

        if (_spellsOnly)
        {
            if (!result.Funded)
                _log?.Info("AutoTrain", $"Buy spells: couldn't gather the coin for every scroll ({result.Detail}) — buying what the purse stretches to.");
            BeginSpellsOnlyErrand();
            return;
        }

        if (!result.Funded && _fundingWithSpells && _target is { } unfunded
            && _tracker.State.CurrentRoom is { } at)
        {
            // Idle first: pricing again can settle as short on the spot, and that
            // answer comes back through this handler before BeginFunding returns.
            _phase = Phase.Idle;
            switch (FundTrainingAlone(at.Key, unfunded))
            {
                case Game.Train.TrainFundingHandoff.Proceed:
                    _target = SelectNearest(at.Key) ?? unfunded;
                    WalkToTrainer(_target.Value, at.Key);
                    break;

                // Short even without the scrolls, on a run the trainer has already
                // refused: settle the levels it trained rather than walking back to
                // a trainer it can't pay.
                case Game.Train.TrainFundingHandoff.StopShort:
                    HoldFundingRetry(_lastFundingShortfall);
                    StopAfterShortRecovery(result.Detail);
                    break;

                // AwaitErrand: the training-only errand is collecting.
                // Abandon: the run has been ended.
            }
            return;
        }

        if (!result.Funded)
        {
            HoldFundingRetry(result.ShortfallCopper);
            _log?.Info("AutoTrain",
                DescribeShortfall?.Invoke(result.ShortfallCopper)
                ?? $"Funding errand ended short — {result.Detail}.");
            if (_refusal.RecoveryClaimed)
            {
                StopAfterShortRecovery(result.Detail);
                return;
            }
            Finish($"Couldn't fund the train ({result.Detail}) — staying armed.");
            return;
        }

        if (_target is not { } t || _tracker.State.CurrentRoom is not { } cur)
        {
            Finish("Funded, but position is unknown — aborting.");
            return;
        }

        _fundingRetryAt = DateTimeOffset.MinValue;
        // Re-select from where the errand left us: the bank we withdrew at may sit
        // nearer a different branch of the same trainer.
        _target = SelectNearest(cur.Key) ?? t;
        WalkToTrainer(_target.Value, cur.Key);
    }

    // The errand that came up short began at the trainer, after a refusal. The run
    // may already have trained levels, so it ends the way a refusal does — through
    // the stat refresh and report — rather than being dropped where it stands.
    private void StopAfterShortRecovery(string detail)
    {
        _log?.Info("AutoTrain", $"Couldn't fund the rest of the train ({detail}) — staying armed.");

        // The errand may have walked off to a bank or stash before giving up, and
        // the CP plan can only be applied at a trainer.
        bool atTrainer = _target is { } t && _tracker.State.CurrentRoom is { } here
                         && here.Key == new RoomKey(t.Map, t.Room);
        if (_applyCp && !atTrainer)
        {
            _applyCp = false;
            _log?.Info("AutoTrain", "Away from the trainer — CP plan left for the next visit.");
        }

        _phase = Phase.Training;
        StopLoop(StopReason.NoMoney);
    }

    // Banked levels this run will actually train, after the reserve and the ceiling.
    private int LevelsThisRun()
    {
        AutoTrainerSettings s = ReadSettings();
        int levels = Math.Max(0, CountBankableAbove(RunLevel) - Math.Max(0, _keepLevels));
        int ceiling = Math.Max(0, s.DoNotTrainAbove);
        if (ceiling > 0) levels = Math.Min(levels, Math.Max(0, ceiling - RunLevel));
        return levels;
    }

    // The level the run stands at: the last one attained this run, else the stat
    // screen's. PlayerStats.Level isn't re-polled between trains, so pricing a
    // mid-run refusal off it would bill the levels already bought again.
    private int RunLevel => _attainedLevel > 0 ? _attainedLevel : _stats.Level;

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

        // CP allocation is `train stats` — not level-band gated — so pick by CLASS only.
        // A band-filtered pick would walk you across the map (or abort) when your current
        // level's band isn't served where you stand, even though stats apply right here.
        TrainerShop? target = SelectNearestForStats(cur.Key);
        if (target is not { } t)
        {
            _log?.Info("AutoTrain",
                "No reachable allowed class trainer to allocate CP at.");
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
        else if (WalkToTrainerRoom(room))
        {
            _phase = Phase.Walking;
            _log?.Info("AutoTrain", $"Walking to {t.Name} ({t.Map}/{t.Room}) to allocate CP.");
            StateChanged?.Invoke();
        }
        else
        {
            Finish(_walker.AvoidBlockingRouteTo(room) is { } blocked
                ? $"Route to trainer {t.Name} ({t.Map}/{t.Room}) blocked by your avoid in room {blocked.Map}/{blocked.Room} — skipping."
                : $"No path to trainer {t.Name} ({t.Map}/{t.Room}).");
        }
    }

    private void ResetRunState(bool loop, bool applyCp, Action<string>? reply)
    {
        _ceilingCap = 0;
        _noChain = false;
        _partyDone = null;
        _loopTrain = loop;
        _cpOnlyRun = false;
        _refusal.Reset();
        _keepLevels = 0;
        _applyCp = applyCp;
        _reply = reply;
        _levelsTrained = 0;
        _trainSteps = 0;
        _attainedLevel = 0;
        _cpTargetLevel = 0;
        _cpApplied = false;
        _stopReason = StopReason.None;
        _walkRun = false;
        _spellTripTried = false;
        _spellsOnly = false;
        _fundingWithSpells = false;
        _alteredStats.Reset();
        _lightStop?.BeginRun();
    }

    private void OnWalkEvent(WalkEvent e)
    {
        if (_lightStop?.OnWalkEvent(e) == true) return;
        if (_phase != Phase.Walking) return;
        RoomKey? trainerRoom = _target is { } t ? new RoomKey(t.Map, t.Room) : null;
        // Events for some other walk (a user walk-to that replaced ours, a detour
        // leg) aren't this run's. Treating that walk's arrival as ours aborted the
        // run from wherever the user went and restarted the loop, dragging them
        // back out (report paradigm-20260927-032602).
        bool ours = e.Destination is null || e.Destination == trainerRoom;
        if (e.Kind == WalkEventKind.Finished)
        {
            if (trainerRoom is { } room && _tracker.State.CurrentRoom?.Key == room)
            {
                // CP-only reconcile skips `train` and goes straight to a stat refresh
                // → train-stats screen; every other run trains on arrival.
                if (_cpOnlyRun) SendStatRefresh();
                else SendTrain();
            }
            else if (ours)
                Finish("Walk finished away from the trainer — aborting.");
        }
        else if (e.Kind == WalkEventKind.Failed && ours)
        {
            Finish("Couldn't reach the trainer — aborting.");
        }
        else if (e.Kind == WalkEventKind.Stopped && ours)
        {
            // Someone stopped the trainer walk: a Stop, a user walk-to replacing it,
            // or a death. They've taken over, so end the run and leave the engine
            // stopped instead of resuming the loop under them.
            _resume = default;
            _runTakenOver = true;
            Finish($"Walk to the trainer stopped ({e.Detail}) — run cancelled; the loop stays stopped.");
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
        int ceiling = EffectiveCeiling();
        if (_loopTrain && _trainSteps < MaxTrainLoopSteps
            && CountBankableAbove(_attainedLevel) > _keepLevels
            && TrainBudgetCalculator.WithinCeiling(_attainedLevel, ceiling))
        {
            SendTrain();
            return;
        }
        if (_loopTrain && ceiling > 0 && !TrainBudgetCalculator.WithinCeiling(_attainedLevel, ceiling))
            _log?.Info("AutoTrain", $"Reached the level ceiling ({ceiling}) — stopping auto-train.");

        // Single train, banked exp exhausted, or cap hit → settle the run.
        BeginStatRefresh();
    }

    // A trainer rejected the train: out-levelled here, or can't pay. Stop the loop
    // and settle with the appropriate reason (drives the @train reply).
    // "Progressed too far" means we've out-levelled THIS trainer's band, not that the
    // run is over. Trainers serve one contiguous band, so a character at the top of a
    // 1-10 trainer with levels still banked just needs the 11+ trainer — walking on
    // is the fix for the strand where the run stopped here with exp to spare.
    private void OnProgressedTooFar(MatchResult m)
    {
        if (TryChainToNextTrainer()) return;
        StopLoop(StopReason.ProgressedTooFar);
    }

    private bool TryChainToNextTrainer()
    {
        if (_phase != Phase.Training || !_loopTrain || _cpOnlyRun || _noChain) return false;
        if (_trainSteps >= MaxTrainLoopSteps) return false;

        if (_tracker.State.CurrentRoom is not { } cur) return false;

        // Mid-run, PlayerStats.Level lags (we don't re-poll between loop steps), so
        // the band question rides the level we last confirmed attaining. The whole
        // decision — reserve, ceiling, candidate, and the "not the trainer that just
        // refused" guard — lives in NextTrainerInChain so it can be tested.
        int level = _attainedLevel > 0 ? _attainedLevel : _stats.Level;
        TrainerShop? pick = Game.Train.TrainItineraryPlanner.NextTrainerInChain(
            TrainerCatalog.Enumerate(_gameData), level, CountBankableAbove(level), _keepLevels,
            EffectiveCeiling(), ResolveClassNumber(), ReadDisabledTrainers(),
            cur.Key, _bfs.DistanceMemo());

        if (pick is not { } next) return false;
        var room = new RoomKey(next.Map, next.Room);
        if (!WalkToTrainerRoom(room)) return false;

        _target = next;
        _phase = Phase.Walking;
        _log?.Info("AutoTrain",
            $"Out-levelled this trainer at {level} — walking to {next.Name} ({next.Map}/{next.Room}) to continue.");
        StateChanged?.Invoke();
        return true;
    }

    // The server is the authority on the purse, and it has just said no. The
    // client only got here because its own figure said yes, so the figure was
    // wrong: re-anchor it and price again, which is also what finds the bank trip
    // that would settle the bill. Once per run — a second refusal after a funding
    // errand has already run means the money is not there, and repeating would be
    // the walk-back-and-forth this exists to stop.
    //
    // Without the hold on the give-up path the armed trigger re-fires on the next
    // kill and walks to the trainer again, refused again, forever.
    private void OnNoMoney(MatchResult m)
    {
        if (_phase != Phase.Training) return;

        bool canRecover = _funding is not null && _target is not null
                          && _tracker.State.CurrentRoom is not null;
        if (_refusal.TryClaimRecovery(canRecover)
            && _target is { } t && _tracker.State.CurrentRoom is { } here)
        {
            _log?.Info("AutoTrain",
                "Trainer refused for money the purse said we had — re-reading the "
                + "purse and looking for funds.");
            // True only when an errand is collecting, and BeginFunding has already
            // put the run in its funding phase. Never set that phase here: a short
            // answer leaves the router idle, and a run parked waiting on it stays
            // busy with nothing coming to release it.
            if (BeginFunding(here.Key, t)) return;
        }

        HoldFundingRetry(_lastFundingShortfall);
        StopLoop(StopReason.NoMoney);
    }

    // One funding recovery per run, reset with the run.
    private readonly Game.Train.TrainRefusalGate _refusal = new();

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
        // While waiting out an altered stat only a `stat` screen counts. An `exp`
        // screen closing carries the marks of the `stat` before it, and would
        // spend one of the wait's re-reads without anything having been read.
        if (_alteredStats.Waiting && !_statParser.LastCaptureReadHits) return;
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
            if (_autoTrain.HoldReason is { } hold)
            {
                if (WaitOutAlteredStats(hold)) return;
            }
            else
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
        }

        // Levelled up (or reconciled) but applied no CP — keep the plan rows (per
        // spec); the refreshed level still re-anchors the projection.
        FinishWithReport();
    }

    // The run is at the trainer and its fresh `stat` still marks a stat as altered,
    // which Stock won't train through: wait there and read `stat` again, for as
    // long as the user's setting allows. False once that is used up (or set to no
    // wait at all); the plan row stays for a later pass.
    private bool WaitOutAlteredStats(string hold)
    {
        int waitSeconds = AlteredStatsWaitSeconds;
        int rereads = Game.Train.AlteredStatsWait.Rereads(waitSeconds, AlteredStatsRereadDelay);
        if (!_alteredStats.TryClaimReread(rereads))
        {
            _log?.Info("AutoTrain", waitSeconds == 0
                ? $"{hold} Not waiting (the wait for altered stats is set to 0) — plan rows kept for later."
                : $"Stats are still altered after {waitSeconds}s at the trainer — giving the CP plan up for this run; plan rows kept.");
            return false;
        }
        if (_alteredStats.JustBegan)
            _log?.Info("AutoTrain",
                $"{hold} Waiting at the trainer for up to {waitSeconds}s, reading `stat` again every "
                + $"{AlteredStatsRereadDelay.TotalSeconds:0}s; after that the plan row is left for later.");
        int session = ++_sessionId;   // retires the read timeout of the screen just parsed
        _ = RereadStatAfterAsync(session);
        return true;
    }

    private async Task RereadStatAfterAsync(int session)
    {
        await Task.Delay(AlteredStatsRereadDelay);
        if (_sessionId == session && _phase == Phase.RefreshingStats) SendStatRefresh();
    }

    // The CP pass was checked as applied (AutoTrainManager.PlanCommitted). Clear the
    // fulfilled plan rows now instead of letting the grid linger until the menu-exit
    // prompt round-trip (or AutoTrainManager's exit-grace fallback) releases the run.
    // The run itself still finishes on the idle transition in OnAutoTrainStateChanged.
    private void OnCpPlanCommitted()
    {
        // The user's own `train stats` applied the plan (no run of ours in flight):
        // that level's row is just as fulfilled as after Train Now.
        if (_phase == Phase.Idle && _autoTrain.ManualApplyLevel is { } manualLevel)
        {
            RemoveFulfilledPlanRows(manualLevel);
            return;
        }
        if (_phase != Phase.ApplyingCp || _cpApplied) return;
        _cpApplied = true;
        RemoveFulfilledPlanRows(_cpTargetLevel);
    }

    private void OnAutoTrainStateChanged()
    {
        if (_phase == Phase.ApplyingCp && !_autoTrain.IsBusy)
        {
            // Rows are cleared only by OnCpPlanCommitted, which fires when the pass
            // was checked as applied. Reaching idle without that signal means it
            // wasn't (the screen never opened, the game refused, or no CP was
            // spent) — keep the plan rows so a later attempt can retry, and leave
            // _cpApplied false so the report reflects "CP not applied".
            if (!_cpApplied)
                _log?.Info("AutoTrain",
                    $"CP plan not applied ({_autoTrain.LastApplyNote ?? "trainer screen didn't open"}) — plan rows kept.");
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

    // The master switch (true = off).
    public Func<bool>? MasterSwitchOff { get; set; }

    // PlayerStats.Exp is the live experience total — StatParser re-anchors it on
    // every stat/exp poll and accrues each "You gain N experience." line. We react
    // to its change so the armed run fires the instant a kill crosses the next-level
    // threshold (no exp poll), and so CanTrainNow refreshes for the UI.
    private void OnStatsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlayerStats.Exp)) return;
        AutoTrainerSettings s = ReadSettings();
        int keep = Math.Max(0, s.LevelsToKeep);
        // Auto-train does not fire with the master switch off, "because no other
        // engines will fire" (user, 2026-10-09). This handler runs on every exp
        // change, so an armed run fires at the first gain after it is back on.
        if (!IsBusy && EngineActive && s.AutoTrain
            && MasterSwitchOff?.Invoke() != true
            && DateTimeOffset.Now >= _fundingRetryAt
            && TrainBudgetCalculator.ShouldFire(CountBankableAbove(_stats.Level), keep, s.FireAtBankedLevels)
            && TrainBudgetCalculator.WithinCeiling(_stats.Level, Math.Max(0, s.DoNotTrainAbove))
            // Last: in a party the gate also notes who to re-invite, so only ask it
            // once a run is otherwise going.
            && CanStartRun?.Invoke() != false)
            // Armed auto-train: detour + loop-train down to the reserve, applying
            // CP per the Auto-train-stats toggle. Suppressed once the level ceiling
            // is reached.
            Begin(loop: true, applyCp: s.AutoTrainStats, reply: null, keepLevels: keep);
        else
            StateChanged?.Invoke();
    }

    // Build the status report while run state is intact, then tear the run down and
    // resume the engine. A bound @train reply sink receives it; otherwise (the local
    // Train Now / armed path) it goes to the log so the user still sees the outcome.
    private void FinishWithReport()
    {
        if (TryBeginSpellTrip()) return;

        string report = BuildReport(_levelsTrained, _stopReason, _cpApplied, _cpTargetLevel,
                                    _attainedLevel > 0 ? _attainedLevel : _startLevel);
        Action<string>? reply = _reply;
        Action<int, string>? partyDone = _partyDone;
        int levels = _levelsTrained;
        if (levels == 0 && !_cpApplied && _refuse is { } refuse) reply = refuse;
        _partyDone = null;   // delivered here with the report, not by Finish
        Finish(null);
        if (partyDone is not null)
        {
            _log?.Info("AutoTrain", report);
            partyDone(levels, report);
        }
        else if (reply is not null) reply(report);
        else _log?.Info("AutoTrain", report);
    }

    // With the levels trained, go on to the shops for the scrolls they unlocked. Only
    // a solo run that walked to its trainer: a party would be left standing, and an
    // @train in place is a parked character someone else is steering.
    private bool TryBeginSpellTrip()
    {
        if (_spellErrand is null || _spellTripTried || !_walkRun || _levelsTrained == 0) return false;
        if (_partyTrip || _partyDone is not null) return false;
        _spellTripTried = true;
        if (!ReadSettings().AutoObtainShopSpells) return false;

        int level = _attainedLevel > 0 ? _attainedLevel : _stats.Level;
        if (!_spellErrand.Begin(level, _runFrom)) return false;

        _phase = Phase.Spells;
        _log?.Info("AutoTrain", $"Trained to level {level} — checking the spell shops before heading back.");
        StateChanged?.Invoke();
        return true;
    }

    private void OnSpellErrandFinished(Game.Train.ShopSpellResult result)
    {
        if (_phase != Phase.Spells) return;
        // Someone took the walker over mid-trip: leave the engine stopped under
        // them, as a stopped trainer walk does.
        if (result.Aborted)
        {
            _resume = default;
            _runTakenOver = true;
        }
        if (_spellsOnly)
        {
            // The errand has logged the trip and the spells by name; a train report
            // would say "no levels". The tab's notice only has room for a count.
            int bought = result.Learned.Count;
            string summary = $"Spell buying is complete, bought {(bought == 0 ? "no" : bought.ToString())} spell{(bought == 1 ? "" : "s")}.";
            Finish(null);
            SpellRunFinished?.Invoke(summary);
            return;
        }
        FinishWithReport();
    }

    // Reset States: end any run or party trip where it stands — nothing resumed,
    // no report and no post-train deposit offer.
    public void Cancel(string reason)
    {
        if (_phase == Phase.Idle && !_partyTrip) return;
        _spellErrand?.Cancel(reason);
        _partyTrip = false;
        _partyTripTrained = false;
        _resume = default;
        _runTakenOver = true;
        _partyDone = null;
        _levelsTrained = 0;
        Finish(reason);
    }

    private void Finish(string? reason)
    {
        if (reason is not null) _log?.Info("AutoTrain", reason);
        bool trained = _levelsTrained > 0;
        int levels = _levelsTrained;
        Action<int, string>? partyDone = _partyDone;
        ResumeTarget resume = _resume;
        bool ownRun = OwnRunActive;
        bool takenOver = _runTakenOver;
        _runTakenOver = false;
        ReserveForTraining?.Invoke(0);
        _lightStop?.Cancel();
        _phase = Phase.Idle;
        _target = null;
        _resume = default;
        _ceilingCap = 0;
        _noChain = false;
        _partyDone = null;
        _applyCp = false;
        _loopTrain = false;
        _cpOnlyRun = false;
        _keepLevels = 0;
        _cpApplied = false;
        _reply = null;
        _refuse = null;
        _levelsTrained = 0;
        _trainSteps = 0;
        _startLevel = 0;
        _attainedLevel = 0;
        _cpTargetLevel = 0;
        _stopReason = StopReason.None;
        _walkRun = false;
        _spellTripTried = false;
        _spellsOnly = false;
        _fundingWithSpells = false;

        // Inside a party trip the engine stays down: the trip goes on (more stops,
        // the re-form) and EndPartyTrip resumes it.
        if (_partyTrip)
        {
            _partyTripTrained |= trained;
            _phase = Phase.PartyTrip;
            _resume = resume;
            StateChanged?.Invoke();
            partyDone?.Invoke(levels, reason ?? "");
            return;
        }

        StateChanged?.Invoke();
        ResumeEngine(resume);
        if (partyDone is not null)
        {
            // A member's party train: stand where the leader stopped us — no bank
            // detour of our own while the party waits.
            partyDone(levels, reason ?? "");
            return;
        }

        // Offer the purse to auto-deposit AFTER the engine is back up, not before:
        // the deposit reroute snapshots whatever engine is running so it can restore
        // it, and firing while ours was still stopped would have it capture nothing
        // and strand the loop at the bank. Running it second means the bank visit
        // happens on the way back into the circuit, exactly as a mid-loop one does.
        if (trained) AfterTrainRun?.Invoke();
        // Last, so a party the train disbanded is being re-formed and the engine is
        // back before anyone the run left on the way is gone back for.
        if (ownRun) OwnRunOver?.Invoke(!takenOver);
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
                // Plain hyphens: this line is also the @train answer, which rides
                // the wire to another player (WireText).
                StopReason.ProgressedTooFar => "Can't train - you've progressed too far for this trainer.",
                StopReason.NoMoney => "Can't train - not enough money for training.",
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

    public bool EngineActive => _loopRunner.State != LoopState.Idle || _autoLair.IsActive;

    private ResumeTarget SnapshotEngine()
    {
        if (_autoLair.IsActive) return new ResumeTarget(ResumeKind.Lair, null);
        if (_loopRunner.State != LoopState.Idle && _loopRunner.CurrentLoop is { } loop)
            return new ResumeTarget(ResumeKind.Loop, loop);
        return new ResumeTarget(ResumeKind.None, null);
    }

    private void StopEngine()
    {
        if (_autoLair.IsActive) _autoLair.Stop("trainer detour", willResume: true);
        if (_loopRunner.State != LoopState.Idle) _loopRunner.Stop("trainer detour", willResume: true);
    }

    private void ResumeEngine(ResumeTarget resume)
    {
        switch (resume.Kind)
        {
            case ResumeKind.Lair:
                _autoLair.Start();
                break;
            case ResumeKind.Loop:
                // ResumeAfterDetour (not Start): the train detour is a continuation of
                // the same grind session, so it must not re-fire the first-waypoint
                // reset (session stats + party @reset). throughGates lets the loop
                // re-approach back INTO a gated grind area the detour walked out of.
                if (resume.Loop is { } loop) _loopRunner.ResumeAfterDetour(loop, throughGates: true);
                break;
        }
    }

    // ----- resolution / detection ----------------------------------------

    private TrainerShop? SelectNearest(RoomKey from) => SelectNearest(from, _stats.Level);

    // CP allocation (`train stats`) selector — class-only, no level band (stats aren't
    // band-gated, only `train` is). Standing in a class-valid trainer gives distance 0, so
    // CP applies in place with no walk.
    private TrainerShop? SelectNearestForStats(RoomKey from) =>
        TrainerCatalog.SelectNearestForStats(
            TrainerCatalog.Enumerate(_gameData), ResolveClassNumber(), ReadDisabledTrainers(),
            DistancesFrom(from));

    // Level is explicit for the chain re-target: mid-run PlayerStats.Level lags the
    // level we've actually attained, and picking the next trainer against a stale
    // level would re-select the one that just refused us.
    private TrainerShop? SelectNearest(RoomKey from, int level)
    {
        IReadOnlyList<TrainerCandidate> ranked = RankTrainers(from, level);
        TrainerShop? pick = TrainerCatalog.FirstUsable(ranked);
        // Every candidate, not just the winner: a walk to a far trainer is only
        // diagnosable when the log shows what the nearer ones measured or why they
        // were ruled out (report paradigm-20260927-030929).
        _log?.Info("AutoTrain",
            $"Trainer choice for level {level} from {from.Map}/{from.Room}: {TrainerCatalog.DescribeCandidates(ranked)}.");
        return pick;
    }

    private IReadOnlyList<TrainerCandidate> RankTrainers(RoomKey from, int level) =>
        TrainerCatalog.RankCandidates(
            TrainerCatalog.Enumerate(_gameData), level, ResolveClassNumber(), ReadDisabledTrainers(),
            DistancesFrom(from));

    // Distance from one room to each trainer, mapped once rather than searched per
    // trainer (BfsMapper.DistanceMemo).
    private Func<TrainerShop, int?> DistancesFrom(RoomKey from)
    {
        Func<RoomKey, RoomKey, int?> distance = _bfs.DistanceMemo();
        return t => distance(from, new RoomKey(t.Map, t.Room));
    }

    // The ranking a level-up run would use if it started right now, for the bug
    // report — the capture lands after the walk began, so this shows whether the
    // choice still stands from where the character is.
    public string DescribeTrainerChoiceFromHere()
    {
        if (_tracker.State.CurrentRoom is not { } cur) return "(current room unknown)";
        if (_stats.Level <= 0) return "(level unknown)";
        return $"level {_stats.Level} from {cur.Key.Map}/{cur.Key.Room}: "
            + TrainerCatalog.DescribeCandidates(RankTrainers(cur.Key, _stats.Level));
    }

    // True when banked exp can reach a level past the reserve — i.e. Train Now would
    // actually train something. Honours the LevelsToKeep buffer so a character sitting
    // exactly on its reserve doesn't light the button up.
    private bool CanLevelNow()
    {
        if (_stats.Level <= 0) return false;
        AutoTrainerSettings s = ReadSettings();
        int keep = Math.Max(0, s.LevelsToKeep);
        return CountBankableAbove(_stats.Level) > keep
            && TrainBudgetCalculator.WithinCeiling(_stats.Level, Math.Max(0, s.DoNotTrainAbove));
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

    private static int GetInt(GameRow? rowOpt, string prop)
    {
        if (rowOpt is not GameRow row) return 0;
        if (!row.TryGetProperty(prop, out GameValue v)) return 0;
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
        if (_funding is not null) _funding.Finished -= OnFundingFinished;
        if (_spellErrand is not null) _spellErrand.Finished -= OnSpellErrandFinished;
    }

    // Believed shortfall from the last funding attempt, and when the armed run will
    // look again. Read by the bug report — "auto-train just doesn't go" is the report
    // this feature will generate, and it's unanswerable without these.
    public long LastFundingShortfall => _lastFundingShortfall;
    public DateTimeOffset? FundingRetryAt =>
        _fundingRetryAt == DateTimeOffset.MinValue ? null : _fundingRetryAt;
}
