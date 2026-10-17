using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;

namespace MudPlay.Game.Map;

// Roomba Mode: an automated gang-house (GH) item sorter. Builds an in-memory
// Loop out of the user's labeled GH rooms and hands it to LoopRunner — the
// same circuit engine every saved loop runs on — instead of rolling a
// bespoke navigation state machine the way Auto-Lair did (the anti-pattern
// this feature was explicitly designed not to repeat). GhSweepManager never
// calls AutoWalkManager or emits movement itself: LoopRunner owns the initial
// recon circuit and every shortest-work shuttle used during sorting.
//
// The two phases share the same LoopRunner, but Sorting can replace its route:
//   - Reconning (one lap): pure observation. On
//     arrival at every room in the expanded circuit this manager holds GhSortGate and
//     sends `sea` directly SearchesPerRoom times (settling between each),
//     the same way Sorting sends get/drop directly rather than delegating to
//     another engine — the loop can't step out mid-search. Recorded via
//     GroundItemTracker's room-aligned surveys as two records per room: what
//     its display shows, and the hidden stacks its searches find.
//   - Sorting: visible items dispatch immediately; only an item recon marked
//     hidden repeats the configured searches before pickup. After each verified
//     transaction the active Loop is replaced by a two-way shortest-work shuttle:
//     nearest carried destination first, otherwise nearest remaining source.
//     GhSortGate stays held until the replacement route is ready.
//   - Final recon (one lap): each room's display is read again, the visible
//     record rebuilt from it, and the room held once against what the sort
//     should have left there.
//
// The sort queue is built ONLY from items this manager itself observed in a
// GH room during its own recon lap (_visibleByRoom and _hiddenByRoom, sourced
// solely from GroundItemTracker) — never from InventoryManager's carried
// snapshot. That's a hard invariant, not an incidental side effect: a sweep
// must never treat something the player was already carrying before it
// started as sortable.
//
// What a room holds (_observedByRoom, the two records added) also feeds
// GhItemLocationStore, a BBS-tier "last seen here" log independent of this
// manager's own per-sweep state — see RoombaQueryHandler for the @roomba remote
// command it backs. A room is written once per visit, when the reads of it are
// done, and again from the final lap.
public sealed class GhSweepManager : IDisposable
{
    public const string LogCategory = "GhSweep";

    // Settle after a recon `sea` before the next one goes out: the reply is just
    // command→reply latency (~150ms, no server-side delay), so this only needs to
    // outlast that round-trip plus a little parse margin.
    internal static readonly TimeSpan ReconSearchSettle = TimeSpan.FromMilliseconds(350);

    // Backstop for a dispatched get/drop that never confirms — e.g. a get for
    // an item recon saw but that's genuinely no longer there ("You don't see
    // X here."), which this manager doesn't parse as a distinct failure line
    // (mirrors AcquisitionGate's own documented reasoning: a failure line we
    // don't parse would otherwise strand the hold forever). Resets on every
    // confirmation that DOES land, so a legitimately slow multi-item room
    // isn't cut short — only fires once confirmations stop arriving entirely.
    internal static readonly TimeSpan DispatchSettleTimeout = TimeSpan.FromSeconds(2);

    // Fallback pace when a prompt doesn't arrive to release the next queued
    // command. Comfortably slower than the game's limit, so a batch that loses
    // its prompts still drains — just at the safe rate instead of the live one.
    internal static readonly TimeSpan PromptWaitTimeout = TimeSpan.FromMilliseconds(800);

    // Hard floor between sends, prompt or no prompt. A prompt alone is NOT a safe
    // release token: every rate-limit line the game emits carries one, so
    // prompt-gating on its own speeds up under exactly the condition it should be
    // slowing down for — nudge arrives with a prompt, prompt releases another
    // command, which earns another nudge. Observed live as bursts of five nudges
    // inside 200ms, repeating every backoff.
    //
    // 800ms is the one interval known to be safe on this realm (GAME_MECHANICS
    // "Command rate limit (typing/sending too fast)": the paced @roomba sync uses
    // it), so it's the floor rather than a guess. The
    // prompt still gates — it just can't release EARLY.
    internal static readonly TimeSpan MinCommandInterval = TimeSpan.FromMilliseconds(800);

    // Pause after the game complains about our command rate before resuming the
    // queue. Matches PacedReplySender's clobber backoff, for the same reason:
    // give the limiter time to forgive before pushing again.
    internal static readonly TimeSpan RateLimitBackoff = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan InventoryVerificationTimeout = TimeSpan.FromSeconds(3);

    // A dispatched `get` can come back as a failure this manager must act on
    // rather than retry forever. Four shapes (GAME_MECHANICS "`get` failure responses"):
    //   "You don't see <echo> here."  — the item is genuinely gone (recon's snapshot
    //       went stale, or another player took it); <echo> is whatever followed `get`.
    //   "Syntax: GET {Amount} {Currency}" — the game misparsed the item name as a
    //       currency get; retrying the same name can't help.
    //   "You cannot carry that much!" — a capacity refusal; the item is still there,
    //       our tracked working-weight drifted low, so resync (not strand).
    //   "A strange force stops you from getting this item." — the game won't let
    //       this character take it; permanent for the item, so it is left alone.
    private static readonly Regex GetNotHereRegex = new(
        @"^\s*You don't see (?<echo>.+?) here\.\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // Both bracket and brace forms; the live realm prints braces (GAME_MECHANICS
    // "`get` failure responses"). A miss here isn't harmless: an item the game
    // won't take by that name is never stranded and gets retried every lap.
    private static readonly Regex GetCurrencySyntaxRegex = new(
        @"^\s*Syntax:\s*GET\s*[\[{]Amount[\]}]\s*[\[{]Currency[\]}]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GetCannotCarryRegex = new(
        @"^\s*You cannot carry that much!\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GetStrangeForceRegex = new(
        @"^\s*A strange force stops you from getting this item\.\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // The DROP counterpart of the currency misparse — note the BRACES, where the
    // get form uses brackets. Names no item, exactly like the get form, but with
    // dispatch paced one command per prompt there is only ever one drop in flight,
    // so the failure attributes unambiguously to the command just sent.
    //
    // It means the game didn't recognise the name as something we're holding, so
    // the usual cause is that we aren't holding it — the ledger believes a pickup
    // landed that actually didn't. Confirmed live 2026-09-02.
    private static readonly Regex DropCurrencySyntaxRegex = new(
        @"^\s*Syntax:\s*DROP\s*[\[{]Amount[\]}]\s*[\[{]Currency[\]}]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // The other face of the same problem, and the more dangerous one. The game
    // partial-matches a drop's argument against what you're holding, so a `drop
    // bloodstone` for bloodstones we no longer have can bind to a DIFFERENT held
    // item — a "bloodstone orb" — and this line is the game refusing because that
    // item happens to be undroppable. Had the collision landed on something
    // droppable we'd have thrown away the wrong item with no complaint at all.
    // Treated exactly like the syntax refusal: verify against a real `i` rather
    // than retry. Confirmed live 2026-09-02.
    private static readonly Regex DropNotAllowedRegex = new(
        @"^\s*You may not drop that item!\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const string SweepLoopName = "Roomba sweep";

    public enum SweepPhase { Idle, Reconning, Sorting, FinalRecon }

    // Sort (the original, default behavior) walks the circuit, then moves
    // misplaced items into their labeled rooms. InventoryOnly walks the exact
    // same circuit — observing, and searching per SearchForHidden/
    // SearchesPerRoom exactly like Sort's recon does — but never builds a sort
    // queue or dispatches a single get/drop; it finishes the moment its one
    // recon lap completes. For a player who wants @roomba's item-location log
    // kept fresh without Roomba touching (and potentially undoing) their own
    // manual organization.
    public enum SweepMode { Sort, InventoryOnly }

    private sealed class PendingSortMove
    {
        public required RoomKey From { get; init; }
        // Settable, unlike From: a destination that refuses the drop as full is
        // re-targeted onto a backup room mid-sweep. The trip planner reads
        // destinations straight off this ledger, so rewriting To is all it takes
        // to route the carried item somewhere else.
        public required RoomKey To { get; set; }
        public required string ItemName { get; init; }
        // Settable: a pickup that came up short is cut down to the copies still to
        // take, the ones already taken going on as a carried move of their own.
        public required int Count { get; set; }
        public required bool RequiresSearch { get; init; }
        public bool IsCarried { get; set; }
        public bool Delivered { get; set; }

        // Set once the room has been read again for this move after the game
        // refused its counted `get`. A second refusal leaves it where it is.
        public bool Recounted { get; set; }

        // Units the game has actually confirmed for the CURRENT leg. Stock has no
        // bulk verb, so a Count of 3 is sent as three separate commands and answers
        // with three lines — completing the move on the first one orphans the rest,
        // and an orphaned confirmation is then misread as somebody else's drop.
        // Reset when the leg changes (collected -> delivering).
        public int ConfirmedUnits { get; set; }
    }

    private readonly GhRoomLabelStore _labels;
    private readonly LoopRunner _loopRunner;
    private readonly RoomTracker _tracker;
    private readonly BfsMapper _bfs;
    private readonly GroundItemTracker _groundItems;
    private readonly ItemNameStore _itemNames;
    private readonly MessageRouter _router;
    private readonly MovementCoordinator _coordinator;
    private readonly Func<bool> _isOtherEngineBusy;
    private readonly Func<bool> _isParadigm;
    private readonly Func<RoomKey, bool> _isRoomActivelyManaged;
    private readonly InventoryManager? _inventory;
    private readonly GhItemLocationStore? _itemLocations;
    private readonly LogService? _log;
    private readonly IDisposable _getSub;
    private readonly IDisposable _dropSub;
    private readonly IDisposable _dropRefusedSub;
    private readonly IDisposable _commandIgnoredSub;
    private readonly IDisposable _slowDownSub;
    private readonly WirePromptScanner? _promptScanner;
    private readonly GhSuspendedSweepStore? _suspendedStore;

    // The unfinished sort queue from a sweep that stopped early, kept in memory so
    // Resume can carry on from it. Survives ResetToIdle deliberately — it's the
    // one piece of a dead sweep still worth something. Cleared by a fresh Start,
    // since that re-scans anyway.
    private List<PendingSortMove>? _suspended;
    // The game-data set _suspended's rooms are numbers of.
    private string? _suspendedGameData;
    // The active game-data set, for stamping the unfinished queue. Unset → none.
    private readonly Func<string?> _activeGameData;

    private IDisposable? _reconSearchSettle;
    private IDisposable? _dispatchSettle;
    private IDisposable? _inventorySettle;
    private IDisposable? _promptWait;
    private IDisposable? _rateLimitBackoff;
    // Arms one tick after the delay; disposing what it returns cancels it.
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    // The clock the command pacing reads.
    private readonly Func<DateTimeOffset> _now;

    private Action<byte[]>? _wireSender;
    private bool _disposed;
    private bool _gateAsserted;
    private bool _reroutingSortLoop;

    // The room a Sorting-phase dispatch is currently outstanding for, and
    // exactly which PendingSortMoves it dispatched there — DispatchSettleTimeout
    // releases the hold while leaving unconfirmed work queued for the next lap.
    private RoomKey? _dispatchRoom;
    private readonly List<PendingSortMove> _outstandingDispatch = new();

    // The dispatch batch, released one command per wire prompt rather than in a
    // burst. See DispatchAtRoomAfterSearch for why the burst was fatal.
    private readonly Queue<(string Verb, PendingSortMove Move)> _commandQueue = new();
    private (string Verb, PendingSortMove Move)? _lastQueuedCommand;
    private DateTimeOffset _lastCommandSentAt = DateTimeOffset.MinValue;

    // Rooms the game refused a drop in ("There is no room to drop X here.") this
    // sweep. Two opposite effects from the one set: excluded as a DESTINATION, so
    // pending moves re-resolve onto a backup room; and preferred as a SOURCE, so
    // the foreign items sitting in them — the only things whose removal frees the
    // capacity — get pulled out first.
    //
    // Per-sweep, cleared on start: a full room is only full until someone loots it,
    // and carrying the mark across sweeps would permanently skip a room that has
    // since emptied. Deliberately NOT cleared when a get frees a slot mid-sweep —
    // every pending drop for the room has already been re-targeted by then, so
    // restoring it as a destination would just churn them back and risk a
    // mark/clear/refuse loop.
    private readonly HashSet<RoomKey> _fullRooms = new();

    // Full-ledger verification: we trust "You took X" / "You dropped X" as ground
    // truth and track the working carry weight ourselves (each confirmed get adds
    // its weight, each drop subtracts), so the happy path never sends a
    // per-transaction `i`. A fresh `i` is requested ONLY to resync the baseline
    // after a capacity refusal ("You cannot carry that much!") — the one case where
    // the ledger has provably drifted (we thought an item fit and it didn't).
    private bool _awaitingInventoryResync;
    private bool _resyncPending;

    // Set when the resync was triggered by a drop the game didn't recognise: the
    // fresh `i` is being read to find moves we only THINK we're carrying, not just
    // to correct the weight baseline.
    private bool _verifyCarriedAfterResync;

    // True while emptying a near-full pack before collecting anything more. Owned
    // here rather than in the pure planner because the whole point is that it
    // persists between decisions — that's what makes it a run instead of a flap.
    private bool _unloading;

    // Latches the "pack too full to sort" warning so a tight pack reports once
    // rather than on every reroute.
    private bool _reportedTightPack;

    // Baseline captured at sort start (and corrected on a resync): base = the
    // player's gear+pack weight carrying zero Roomba pickups; max = MaxWeight.
    // int.MaxValue max means "no weight data" → fill everything, strand nothing.
    private int _baseCarryWeight;
    private int _maxCarryWeight = int.MaxValue;

    // Lowest base seen this sweep — the pack at its emptiest, which is the only
    // honest yardstick for a permanent "can this ever be carried" call. Tracked
    // separately from _baseCarryWeight, which moves with the live pack.
    private int _minBaseCarryWeight = int.MaxValue;

    // Recon's own direct-search dispatch (mirrors _outstandingDispatch's role
    // for Sorting's get/drop dispatch): which circuit room we're currently
    // holding for, and how many of SearchesPerRoom have gone out so far.
    private RoomKey? _reconSearchRoom;
    private int _reconSearchesSent;
    private RoomKey? _sortSearchRoom;
    private int _sortSearchesSent;

    // Room descriptions print their floor list before the closing exits line
    // confirms the move. While RoomTracker is Pending, CurrentRoom therefore
    // still names the room being left. Stage that list and attach it to the
    // transition's NewRoom in OnRoomChanged instead of shifting it backward.
    private List<string>? _pendingArrivalSurvey;
    private TimeSpan _pendingArrivalReadTime;

    // A floor of this many stacks is logged as it is read even when recon isn't
    // searching. A Paradigm room has no item cap, and what a list that long cost
    // to read is the first thing a report of a slow sweep needs.
    private const int HugeFloorStacks = 100;

    // How many hidden items a room's summary line names. A vault's full hidden
    // list ran to 1,500 characters and was logged after each of ten searches.
    private const int HiddenNamesLogged = 20;

    // The floor reads taken since entering the room recon is standing in, and what
    // writing it to the item-location log cost, kept for the one summary line the
    // visit gets.
    private int _surveysHere;
    private GhSurveyTiming? _slowestSurveyHere;
    private TimeSpan? _itemLogWriteHere;

    // The clock floor reads and log writes are timed on.
    private readonly Func<long> _timestamp;

    // Latches the warning that lists can't be told apart, so a sweep says it once.
    private bool _reportedUnattributedLists;

    // What the commands sent in the room being worked did, for the one line the
    // room gets when they are all answered.
    private readonly List<string> _pickedUpHere = new();
    private readonly List<string> _deliveredHere = new();
    private readonly List<string> _failedHere = new();

    // The floor read and the item-location log write that took longest this sweep
    // (or the last one, until the next starts). Surfaced for the bug report.
    public GhSurveyTiming? SlowestSurvey { get; private set; }
    public (RoomKey Room, TimeSpan Took)? SlowestItemLogWrite { get; private set; }

    // Every graph room traversed by the expanded sweep circuit. Labels choose
    // destinations; they are not the list of source rooms worth inspecting.
    private readonly HashSet<RoomKey> _sweepRooms = new();

    // Per-room floors captured during recon, as the game keeps them: the stacks a
    // room display shows and the stacks only a search finds are separate, and an
    // item can have copies in both. Each ledger holds the highest count any one
    // display or search showed for an item. The sort queue is built from the two
    // apart, so a visible stack is never searched for and a hidden one always is.
    // _observedByRoom is their sum, what the room holds: the item-location log and
    // the room's shown inventory read it. The visible record is the latest display
    // (a display never rolls, so the latest is the floor); the hidden record is the
    // highest count across the searches, which do roll.
    private readonly Dictionary<RoomKey, List<string>> _observedByRoom = new();
    private readonly Dictionary<RoomKey, List<string>> _visibleByRoom = new();
    private readonly Dictionary<RoomKey, List<string>> _hiddenByRoom = new();

    // Rooms whose display recon read, an empty floor included. Only these have a
    // floor the final lap can be held against: the final circuit is plotted afresh
    // from where the sort ended and can pass through a room recon never walked.
    private readonly HashSet<RoomKey> _readOnRecon = new();

    // The final lap. What each room should have in plain sight once the sort is
    // done: recon's visible record, less every copy the game confirmed taken from
    // it, plus every copy confirmed dropped in it, kept as the sort goes. Each
    // room's display on that lap is held against it once. Null on a resumed sweep,
    // which ran no recon to start it from. _hiddenTaken is the same for the copies
    // taken out of hidden stacks, and for the ones a second search showed were no
    // longer in them.
    private GhFloorCounts? _expectedAfterSort;
    private GhFloorCounts? _hiddenTaken;
    private readonly HashSet<RoomKey> _readOnFinalLap = new();
    private readonly List<RoomKey> _changedAfterSort = new();

    // Rooms the final lap found holding something other than what the sort left
    // (someone took or dropped an item meanwhile). Surfaced for the bug report.
    public IReadOnlyList<RoomKey> RoomsChangedAfterSort => _changedAfterSort;
    private readonly List<PendingSortMove> _pending = new();
    private readonly List<GhSweepItemFound> _leftInPlace = new();
    private readonly List<GhSweepMove> _movedSoFar = new();
    private readonly List<GhSweepStranded> _stranded = new();

    // Sorting-phase lap-progress tracking is diagnostic only. Queued work is
    // never discarded because a lap made no progress or because an arbitrary
    // lap count was reached: a normal sweep ends only after every PendingSortMove
    // has a verified delivery. The user can still stop it manually, and a
    // genuine LoopRunner failure remains an abnormal terminal condition.
    private int _sortLapCount;
    private int _progressSnapshotMoved;
    private int _progressSnapshotCarried;
    // Consecutive Sorting laps that moved/carried nothing. One stall is the
    // "everything we had headroom for is done" signal; a second confirms no
    // queued move can currently complete, so we stop instead of walking the
    // circuit forever (see OnSortingLapCompleted).
    private int _noProgressSortLaps;
    // Stall this many fruitless Sorting laps in a row and the sweep finishes: the
    // first lap says "nothing left I can move", the verification lap confirms it.
    private const int StalledSortLapLimit = 2;

    public SweepPhase Phase { get; private set; } = SweepPhase.Idle;

    // A sweep is underway in any non-idle phase (recon, sorting, or the final
    // verification lap). The auto-get and auto-discard engines read this to hold
    // off while Roomba is sorting — an auto-get would eat the headroom Roomba
    // needs to carry moves, and an auto-discard would bin an item Roomba is in
    // the middle of relocating.
    public bool IsActive => Phase != SweepPhase.Idle;
    public int CompletedReconLaps { get; private set; }

    // Which mode the current (or most recently finished) run is/was — set
    // fresh on every Start(). Idle default is Sort so a bug report captured
    // before any run ever started still prints something meaningful.
    public SweepMode Mode { get; private set; } = SweepMode.Sort;

    // Wall-clock time the current (or most recently finished) run started —
    // set fresh on every Start(). Feeds the start/completion gangpath
    // announcements' timestamps.
    public DateTimeOffset? StartedAt { get; private set; }

    // Why the last Start() attempt refused (null when the last Start succeeded or
    // none has run). The GH Management tab shows this so a refused Start isn't a
    // silent no-op — the common one being fewer than 2 labeled rooms on first run.
    public string? LastStartError { get; private set; }

    public IReadOnlyList<GhSweepMove> MovedSoFar => _movedSoFar;
    public IReadOnlyList<GhSweepItemFound> LeftInPlace => _leftInPlace;

    // Rooms that refused a drop as full this sweep. Surfaced for the bug report:
    // "Roomba kept carrying everything" reads identically to a routing bug unless
    // you can see the destinations were simply out of space.
    public IReadOnlyCollection<RoomKey> FullRooms => _fullRooms;

    // Room groups that ran out of space this sweep, worst first. Surfaced so the
    // Roomba log can tell the user which category to label another room for
    // instead of leaving that only in the program log.
    public IReadOnlyList<GhSaturatedGroup> SaturatedGroups
        => _saturated.Values
            .OrderByDescending(e => e.Items.Count)
            .Select(e => new GhSaturatedGroup(e.Rooms.ToList(), e.Items.ToList()))
            .ToList();
    public IReadOnlyList<GhSweepStranded> Stranded => _stranded;
    public int CircuitRoomCount => _sweepRooms.Count;
    public int PendingMoveCount => _pending.Count(p => !p.Delivered);
    // The moves still to make, in the order they were queued.
    public IReadOnlyList<GhSweepPendingMove> PendingMoves => _pending
        .Where(p => !p.Delivered)
        .Select(p => new GhSweepPendingMove(p.From, p.To, p.ItemName, p.Count, p.RequiresSearch, p.IsCarried))
        .ToList();
    public int CarriedPendingCount => _pending.Count(p => p.IsCarried && !p.Delivered);
    public int HiddenPendingCount => _pending.Count(p => p.RequiresSearch && !p.Delivered);
    public int CompletedSortLaps => _sortLapCount;

    // Read-only weight-ledger snapshot for the bug report: the working budget
    // (max - base), what the tracked ledger says is carried right now, and the
    // resulting live headroom. int.MaxValue budget means "no weight data".
    public int WorkingWeightBudget => WorkingBudget();
    public int LedgerCarriedWeightNow => LedgerCarriedWeight();
    public int CarryHeadroomNow => CurrentHeadroom();

    // A room's most-recently-observed floor inventory (refreshed by the final recon
    // pass after sorting). Empty when the room wasn't observed. Fed to the GH tab's
    // double-click "what's in this room now" view.
    public IReadOnlyList<string> ObservedItemsAt(RoomKey room) =>
        _observedByRoom.TryGetValue(room, out List<string>? items)
            ? items.ToList()
            : Array.Empty<string>();

    // True while `room` still has a queued item on its floor waiting to be picked up
    // (the tab's "Cleaning" status). A carried or delivered move no longer keeps the
    // room dirty, and a stranded item is dropped from the queue, so this reads false
    // once the room's movable clutter is gone.
    public bool HasPendingPickupAt(RoomKey room) =>
        _pending.Any(m => !m.Delivered && !m.IsCarried && m.From.Equals(room));

    // Fires once, when a sweep finishes (queue exhausted or stopped early).
    public event Action<GhSweepReport>? SweepCompleted;

    // Fires on every phase/lap-count change — the GH Management tab's live
    // status readout reads Phase / CompletedReconLaps / MovedSoFar / LeftInPlace
    // off this.
    public event Action? PhaseChanged;

    public GhSweepManager(
        GhRoomLabelStore labels,
        LoopRunner loopRunner,
        RoomTracker tracker,
        BfsMapper bfs,
        GroundItemTracker groundItems,
        ItemNameStore itemNames,
        MessageRouter router,
        MovementCoordinator coordinator,
        Func<bool> isOtherEngineBusy,
        LogService? log = null,
        Func<bool>? isParadigm = null,
        InventoryManager? inventory = null,
        GhItemLocationStore? itemLocations = null,
        Func<RoomKey, bool>? isRoomActivelyManaged = null,
        WirePromptScanner? promptScanner = null,
        GhSuspendedSweepStore? suspendedStore = null,
        Func<string?>? activeGameData = null,
        Func<long>? timestamp = null,
        Func<DateTimeOffset>? now = null,
        Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        _schedule = schedule ?? DispatcherTicks.Once;
        _now = now ?? (static () => DateTimeOffset.UtcNow);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(loopRunner);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(groundItems);
        ArgumentNullException.ThrowIfNull(itemNames);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(isOtherEngineBusy);
        _labels = labels;
        _loopRunner = loopRunner;
        _tracker = tracker;
        _bfs = bfs;
        _groundItems = groundItems;
        _itemNames = itemNames;
        _router = router;
        _coordinator = coordinator;
        _isOtherEngineBusy = isOtherEngineBusy;
        _isParadigm = isParadigm ?? (static () => false);
        _inventory = inventory;
        _itemLocations = itemLocations;
        // Which labeled rooms THIS character sweeps (per-character; labels are shared
        // per-BBS). Defaults to "all managed" when unwired.
        _isRoomActivelyManaged = isRoomActivelyManaged ?? (static _ => true);
        _promptScanner = promptScanner;
        _suspendedStore = suspendedStore;
        _activeGameData = activeGameData ?? (static () => null);
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _log = log;

        _getSub = _router.Subscribe(KnownPatterns.PlayerGets, OnGetLine);
        _dropSub = _router.Subscribe(KnownPatterns.PlayerDrops, OnDropLine);
        _dropRefusedSub = _router.Subscribe(KnownPatterns.RoomDropRefused, OnDropRefusedLine);
        // The game's own rate-limit signals. Both patterns already existed and
        // nothing had ever subscribed to either, which is why a flooded dispatch
        // looked like silence rather than a refusal.
        _commandIgnoredSub = _router.Subscribe(KnownPatterns.CommandIgnored, _ => OnRateLimited(commandDropped: true));
        _slowDownSub = _router.Subscribe(KnownPatterns.SlowDown, _ => OnRateLimited(commandDropped: false));
        if (_promptScanner is not null) _promptScanner.PromptObserved += OnPromptObservedFromWire;
        // Raw-line hook for the get-FAILURE shapes (no KnownPattern for them);
        // gated hard on an outstanding get so a manual `get` failure isn't ours.
        _router.LineDispatched += OnLineForGetFailure;
        _loopRunner.Event += OnLoopEvent;
        if (_inventory is not null)
            _inventory.FullInventoryParsed += OnFullInventoryParsed;
        // NOT _tracker.StateChanged += OnRoomChanged here — LoopRunner also
        // subscribes to RoomTracker.StateChanged, and multicast delegates fire in
        // registration order. GhSweepManager is constructed AFTER LoopRunner (it
        // depends on the instance), so a direct subscription here would always run
        // second: LoopRunner's own arrival-confirm-and-advance would already have
        // sent the next move before this manager got a chance to assert GhSortGate
        // — the sweep arrives at a room and leaves again before picking anything
        // up. AppServices wires OnRoomChanged externally instead, from the same
        // early wrapper lambda AutoSearchManager/AutoGetItemsManager/GroundItemTracker
        // /CashManager already use specifically because it's registered before
        // LoopRunner exists.
        _groundItems.SurveyUpdated += OnSurveyUpdated;
    }

    // Bind the wire sender — the gate-wrapped engine pipeline from
    // MainWindowViewModel.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Start a sweep over the current label set. mode defaults to Sort (the
    // original behavior); pass InventoryOnly to walk the exact same circuit
    // and feed the item-location log without ever dispatching a get/drop —
    // see SweepMode. Refuses when fewer than two rooms are labeled
    // (LoopRunner's own ≥2-waypoint cycle requirement), another sweep is
    // already running, or another movement engine (walk / loop / auto-lair)
    // is active.
    // Shared Start / Resume preconditions. Both put the same engine on the wire,
    // so both refuse for the same reasons and report them the same way.
    private bool TryClaimSweepStart(out int manageable)
    {
        manageable = 0;
        if (Phase != SweepPhase.Idle)
        {
            LastStartError = "A sweep is already running.";
            _log?.Warn(LogCategory, "start refused: a sweep is already running");
            return false;
        }
        manageable = _labels.Labels.Count(l => _isRoomActivelyManaged(new RoomKey(l.Map, l.Room)));
        if (manageable < 2)
        {
            LastStartError = manageable == 0
                ? "No rooms are set to Actively Manage — check at least 2 on the Roomba tab."
                : "Set at least 2 rooms to Actively Manage on the Roomba tab to start a sweep.";
            _log?.Warn(LogCategory,
                $"start refused: {manageable} actively-managed room(s); need at least 2");
            return false;
        }
        if (_isOtherEngineBusy())
        {
            LastStartError = "Another movement engine (a walk, loop, or auto-lair) is running — stop it first.";
            _log?.Warn(LogCategory, "start refused: another movement engine is active");
            return false;
        }
        return true;
    }

    // Everything a run owns, wiped back to its pre-sweep state. Shared by Start
    // and Resume; Resume then re-seeds the queue it kept.
    private void ResetSweepState()
    {
        _observedByRoom.Clear();
        _visibleByRoom.Clear();
        _hiddenByRoom.Clear();
        _readOnRecon.Clear();
        _expectedAfterSort = null;
        _hiddenTaken = null;
        ForgetRefusedPickups();
        _pickedUpHere.Clear();
        _deliveredHere.Clear();
        _failedHere.Clear();
        _readOnFinalLap.Clear();
        _changedAfterSort.Clear();
        _pendingArrivalSurvey = null;
        _surveysHere = 0;
        _slowestSurveyHere = null;
        _itemLogWriteHere = null;
        _reportedUnattributedLists = false;
        SlowestSurvey = null;
        SlowestItemLogWrite = null;
        _sweepRooms.Clear();
        _pending.Clear();
        _movedSoFar.Clear();
        _leftInPlace.Clear();
        _stranded.Clear();
        _fullRooms.Clear();
        _saturated.Clear();
        _commandQueue.Clear();
        _lastQueuedCommand = null;
        Cancel(ref _promptWait);
        Cancel(ref _rateLimitBackoff);
        CompletedReconLaps = 0;
        _sortLapCount = 0;
        _progressSnapshotMoved = 0;
        _progressSnapshotCarried = 0;
        _noProgressSortLaps = 0;
        Cancel(ref _reconSearchSettle);
        _reconSearchRoom = null;
        _reconSearchesSent = 0;
        _sortSearchRoom = null;
        _sortSearchesSent = 0;
        Cancel(ref _dispatchSettle);
        _dispatchRoom = null;
        _outstandingDispatch.Clear();
        Cancel(ref _inventorySettle);
        _awaitingInventoryResync = false;
        _resyncPending = false;
        _verifyCarriedAfterResync = false;
        _unloading = false;
        _reportedTightPack = false;
        _baseCarryWeight = 0;
        _maxCarryWeight = int.MaxValue;
        _minBaseCarryWeight = int.MaxValue;
    }

    // Work left over from a sweep that stopped early — what Resume would pick up.
    // Falls back to the persisted record when there's nothing in memory, which is
    // the case that matters most: a client restart after a bail is exactly when
    // re-walking a 120-room circuit hurts, and it's the one an in-memory-only
    // queue can't survive.
    public int ResumableMoveCount
        => _suspended?.Count ?? _suspendedStore?.Load().Count ?? 0;

    public bool CanResume => Phase == SweepPhase.Idle && ResumableMoveCount > 0;

    // The game-data set that work's rooms are numbers of (null: none recorded).
    public string? ResumableGameData => _suspended is not null ? _suspendedGameData : _suspendedStore?.GameData;

    // This manager is app-scoped, but a resumable sweep belongs to ONE character:
    // the manifest is persisted per profile (GhSuspendedSweepStore keys on the active
    // character), yet _suspended is a shared in-memory copy. A character switch must
    // drop it, or Resume would offer — and deliver — the previous character's load to
    // the new one (its own items dropped by name against a pack that doesn't hold
    // them). Cleared here so CanResume / ResumableMoveCount fall back to the
    // newly-loaded character's own store; the UI re-reads on the PhaseChanged.
    public void OnProfileLoaded()
    {
        if (_suspended is null) return;
        _suspended = null;
        PhaseChanged?.Invoke();
    }

    // Carry on from where a stopped sweep left off, skipping recon entirely. The
    // survey a sweep dies holding is still good, and re-walking a 120-room circuit
    // to rediscover what we already knew is most of the cost of a sweep.
    //
    // Deliberately keeps the old queue rather than re-deriving it: items already
    // delivered are gone from it, so a re-derived plan would send us back to
    // collect things we'd already moved. Anything that HAS changed underneath us
    // (someone else took an item) surfaces the same way it always does — the get
    // fails and that move is dropped.
    public bool Resume()
    {
        LastStartError = null;
        string? savedOn = ResumableGameData;
        if (OtherGameDataRefusal(savedOn) is { } refusal)
        {
            LastStartError = refusal;
            _log?.Info(LogCategory, $"resume refused: {refusal}");
            return false;
        }
        // In-memory first (same session), else rehydrate the persisted record —
        // that's the path a client restart takes.
        List<PendingSortMove>? resumable = _suspended ?? RehydrateSuspended();
        if (resumable is not { Count: > 0 })
        {
            LastStartError = "There's no unfinished sweep to resume.";
            return false;
        }
        if (!TryClaimSweepStart(out int _)) return false;

        Mode = SweepMode.Sort;
        StartedAt = DateTimeOffset.Now;
        List<PendingSortMove> restored = resumable;
        _suspended = null;
        ResetSweepState();
        _pending.AddRange(restored);

        if (!PlotAndStartCircuit())
        {
            // Put it back — a route we can't plot right now (mid-combat, position
            // unknown) is worth retrying once the player sorts that out.
            _suspended = restored;
            _suspendedGameData = savedOn;
            _pending.Clear();
            LastStartError = "Couldn't plot a walkable route through the labeled rooms.";
            _log?.Warn(LogCategory, "resume refused: LoopRunner declined the sweep circuit");
            return false;
        }

        Phase = SweepPhase.Sorting;
        CaptureCarryBaseline();
        SplitOversizedMoves();
        StrandUnmovableItems();
        // The live queue now owns this work; the persisted copy would otherwise let
        // a later Start adopt the carried half a second time. It's rewritten at the
        // next sweep end either way. One kept for another set is not this queue.
        if (!StoredRecordIsOtherSets) _suspendedStore?.Clear();

        _log?.Info(LogCategory,
            $"sweep resumed: {_pending.Count} move(s) carried over, recon skipped");
        PhaseChanged?.Invoke();
        if (_pending.Count == 0) FinishSweep();
        return true;
    }

    public bool Start(SweepMode mode = SweepMode.Sort)
    {
        LastStartError = null;
        if (!TryClaimSweepStart(out int manageable)) return false;

        Mode = mode;
        StartedAt = DateTimeOffset.Now;
        _suspended = null;   // a fresh start re-scans, so the old queue is moot
        ResetSweepState();
        if (!PlotAndStartCircuit())
        {
            LastStartError = "Couldn't plot a walkable route through the labeled rooms.";
            _log?.Warn(LogCategory, "start refused: LoopRunner declined the sweep circuit");
            return false;
        }

        Phase = SweepPhase.Reconning;
        _log?.Info(LogCategory,
            $"sweep started ({Mode}): {manageable} actively-managed destination(s), "
            + $"{_sweepRooms.Count} circuit room(s), recon phase begins");
        // PlotAndStartCircuit already dispatched the circuit's first move (via
        // LoopRunner.Start) as part of confirming the route is walkable, so
        // this announce lands right after it rather than strictly before —
        // only fires once a start is genuinely confirmed, never on a refusal.
        DateTimeOffset startedAt = StartedAt ?? DateTimeOffset.Now;
        string startStamp = $"{startedAt:yyyy-MM-dd HH:mm} {TimeZoneAbbreviation.For(startedAt)}";
        Send(Mode == SweepMode.InventoryOnly
            ? $"bg Roomba inventory mode starting - {startStamp}."
            : $"bg Roomba sorting starting - {startStamp}.");
        PhaseChanged?.Invoke();
        return true;
    }

    // Plot a nearest-neighbour walking route through the labeled rooms and hand it
    // to LoopRunner, then record the full circuit's room set. Shared by the initial
    // recon start and the post-sort final recon pass. Ordering the rooms (rather
    // than raw label-insertion order, whatever order the user right-clicked them in)
    // keeps the sweep from zigzagging across the house. Anchors from the player's
    // current room when known.
    private bool PlotAndStartCircuit()
    {
        // Only rooms THIS character actively manages — never the whole label set.
        // Labels are per-BBS and can be adopted from another player's @roomba sync,
        // so the full set may span separate gang houses; sweeping all of them would
        // route Roomba house-to-house (or into one it can't enter).
        IReadOnlyList<RoomKey> allRooms = _labels.Labels
            .Select(l => new RoomKey(l.Map, l.Room))
            .Where(_isRoomActivelyManaged)
            .ToList();
        if (allRooms.Count == 0) return false;
        RoomKey startRoom = _tracker.State.CurrentRoom?.Key ?? allRooms[0];
        IReadOnlyList<RoomKey> orderedRooms =
            GhRouteOrderer.OrderNearestNeighbor(startRoom, allRooms, _bfs);

        if (!_loopRunner.Start(new Loop(SweepLoopName, orderedRooms))) return false;

        _sweepRooms.UnionWith(_loopRunner.ResolveLoopRoomKeys());
        _sweepRooms.UnionWith(allRooms);
        return true;
    }

    // Stop a running sweep early. No-op when idle. A manual stop mid-Sorting
    // can leave items picked up but never dropped, same as an external loop
    // failure — report those as Stranded rather than silently discarding
    // _pending.
    public void Stop(string reason)
    {
        if (Phase == SweepPhase.Idle) return;
        _log?.Info(LogCategory, $"sweep stopped: {reason}");
        GhSweepReport report = BuildFinalReport(stoppedBecause: reason);
        ResetToIdle(reason);
        SweepCompleted?.Invoke(report);
    }

    private void OnLoopEvent(LoopEvent e)
    {
        if (Phase == SweepPhase.Idle) return;

        switch (e.Kind)
        {
            case LoopEventKind.RepeatStarted:
                if (Phase == SweepPhase.Reconning)
                {
                    // One recon lap is enough — walk the circuit once, observe every
                    // room, then sort. (No configurable lap count: a second lap of the
                    // same rooms tells us nothing new — Sort mode never touches anything
                    // between laps that recon itself didn't already see.)
                    CompletedReconLaps = _loopRunner.CompletedLaps;
                    _log?.Info(LogCategory, $"recon lap {CompletedReconLaps} complete");
                    PhaseChanged?.Invoke();
                    if (Mode == SweepMode.InventoryOnly)
                    {
                        // Nothing to sort, nothing to re-verify — the recon lap just
                        // taken IS the freshest state, so finish immediately rather
                        // than walking a redundant final-recon lap.
                        _log?.Info(LogCategory, "inventory-only recon complete; finishing (nothing sorted)");
                        FinishSweep();
                        return;
                    }
                    BeginSortPhase();
                    return;
                }
                if (Phase == SweepPhase.FinalRecon)
                {
                    WriteRoomsTheFinalLapMissed();
                    _log?.Info(LogCategory, "final recon lap complete; sweep done");
                    FinishSweep();
                    return;
                }
                if (Phase == SweepPhase.Sorting) OnSortingLapCompleted();
                return;

            case LoopEventKind.Stopped:
            case LoopEventKind.Failed:
                if (_reroutingSortLoop && e.Kind == LoopEventKind.Stopped)
                    return; // expected supersede while replacing the sort shuttle
                // The loop ended — either our own Stop()/FinishSweep() (which
                // already reset Phase to Idle before touching LoopRunner, so
                // the guard above already returned) or something external
                // (toolbar Stop, a superseding loop, tier-3 recovery giving
                // up). Either way the run is over; reset without recursing
                // back into LoopRunner.Stop(). An external ending mid-Sorting
                // can leave items picked up but never dropped — report those
                // as Stranded rather than silently discarding what the player
                // is now carrying.
                _log?.Warn(LogCategory, $"sweep ended by loop: {e.Kind} — {e.Detail}");
                ReportAbnormalEnd(e.Kind == LoopEventKind.Failed
                    ? $"its loop failed ({e.Detail})"
                    : $"its loop was stopped ({e.Detail})");
                return;
        }
    }

    // A lap of the Sorting circuit completed. Tracks progress and decides when the
    // sort is exhausted: a lap that moved and carried nothing means every queued
    // move we had headroom (and a free destination) for is done. One such lap isn't
    // proof — a transient miss, a briefly-full room, or a re-survey catching up can
    // clear on the next pass — so we take one more verification lap; a SECOND
    // fruitless lap confirms nothing queued can complete this sweep and we finish
    // (rather than circling the house forever, which once stranded a sweep across
    // 70 laps). A lap that DID progress resets the stall counter.
    private void OnSortingLapCompleted()
    {
        _sortLapCount++;

        // Forget which rooms were full. "Full" is a reading taken at one moment,
        // and a lap later it's often stale — Roomba has been emptying rooms all
        // lap, and the player may have cleared space themselves (one run refused
        // the catch-all early and then treated it as full for another two hours
        // while it visibly had room). Re-learning costs one refused drop per room
        // that really is still full, and buys back every room that isn't.
        if (_fullRooms.Count > 0)
        {
            _log?.Info(LogCategory,
                $"lap {_sortLapCount}: forgetting {_fullRooms.Count} full room(s) so any that have "
                + "since freed up get used again");
            _fullRooms.Clear();
            // The map's amber "out of space" rings read FullRooms on PhaseChanged;
            // without this they'd linger a full lap on rooms we just forgot.
            PhaseChanged?.Invoke();
        }

        int movedNow = _movedSoFar.Count;
        int carriedNow = _pending.Count(p => p.IsCarried && !p.Delivered);
        bool progressed = movedNow != _progressSnapshotMoved || carriedNow != _progressSnapshotCarried;
        int remaining = _pending.Count(p => !p.Delivered);
        _progressSnapshotMoved = movedNow;
        _progressSnapshotCarried = carriedNow;

        if (progressed)
        {
            _noProgressSortLaps = 0;
            _log?.Info(LogCategory,
                $"sort lap {_sortLapCount} complete: moved={movedNow} carrying={carriedNow} remaining={remaining}");
            return;
        }

        _noProgressSortLaps++;
        // remaining == 0 is handled by the normal all-delivered completion path
        // (MaybeFinish → BeginFinalRecon); only a stall with work still queued
        // needs the stop-instead-of-loop decision.
        if (remaining == 0 || _noProgressSortLaps < StalledSortLapLimit)
        {
            _log?.Warn(LogCategory,
                $"sort lap {_sortLapCount}: no progress (moved={movedNow} carrying={carriedNow}); "
                + $"{remaining} queued move(s) remain — one verification lap to confirm nothing else can move");
            return;
        }

        _log?.Warn(LogCategory,
            $"sort lap {_sortLapCount}: still no progress after a verification lap; "
            + $"{remaining} queued move(s) can't be completed this sweep — finishing");
        StrandStalledAndFinish();
    }

    // Two Sorting laps running moved nothing: every remaining queued move is blocked
    // (its destination stayed full, a hidden item never surfaced, or what's left is
    // too heavy for the budget the player's own load leaves). Surface each still-
    // undelivered, not-carried move as left-in-place so the Roomba Log names exactly
    // what couldn't be sorted, then finish through the normal final-recon pass.
    // Carried-but-undelivered moves become Stranded on the finish path
    // (BuildFinalReport).
    private void StrandStalledAndFinish()
    {
        List<PendingSortMove> stalled = _pending.Where(p => !p.Delivered && !p.IsCarried).ToList();
        foreach (PendingSortMove move in stalled)
        {
            _pending.Remove(move);
            _leftInPlace.Add(new GhSweepItemFound(move.From, move.ItemName, GhLeftReason.CouldNotComplete));
        }
        if (stalled.Count > 0)
            _log?.Info(LogCategory,
                $"left {stalled.Count} move(s) unsorted — no reachable destination or headroom this sweep");
        BeginFinalRecon();
    }

    // LoopRunner ended outside our own Stop()/FinishSweep() call (toolbar
    // Stop, a superseding loop, tier-3 recovery giving up). Anything still
    // carried and undelivered is now sitting in the player's pack with
    // nowhere logged for it to go — surface that explicitly rather than
    // silently discarding _pending on ResetToIdle.
    private void ReportAbnormalEnd(string why)
    {
        GhSweepReport report = BuildFinalReport(stoppedBecause: why);
        ResetToIdle();
        SweepCompleted?.Invoke(report);
    }

    // Moves any still-carried, undelivered PendingSortMove into _stranded (a
    // manual drop is needed for these — the sweep is ending with them in the
    // player's pack) and builds the outgoing report. Shared by every sweep
    // exit path (Stop, an external LoopRunner ending, FinishSweep's normal
    // completion) so none of them can silently drop what's currently carried.
    private GhSweepReport BuildFinalReport(string? stoppedBecause = null)
    {
        // Hold the unfinished queue so Resume can pick it up without re-walking
        // the whole circuit. Recon is by far the most expensive part of a sweep —
        // a 120-room house is minutes of walking — and an abort throws away a
        // survey that is still perfectly good.
        _suspended = _pending.Where(p => !p.Delivered).ToList();
        if (_suspended.Count == 0) _suspended = null;
        // The rooms are the active set's numbers: under another set they are other
        // rooms, and Resume refuses the queue there (OtherGameDataRefusal).
        _suspendedGameData = _activeGameData();

        List<PendingSortMove> stillCarried = _pending.Where(p => p.IsCarried && !p.Delivered).ToList();
        foreach (PendingSortMove move in stillCarried)
            _stranded.Add(new GhSweepStranded(move.From, move.To, move.ItemName));

        // Hand the whole unfinished queue forward, not just the carried half.
        // Whatever ended this sweep, the items in the pack are still there and only
        // this queue knows where each was going; and the planned-but-uncollected
        // moves are a full lap of the circuit to rediscover. Persisting both is
        // what lets Resume skip the scan even after the client restarts. Always
        // written, including empty, so a clean finish clears the last record
        // rather than leaving it to be re-delivered forever.
        if (StoredRecordIsOtherSets)
            _log?.Info(LogCategory,
                $"the unfinished sweep saved on game data '{_suspendedStore!.GameData}' is kept for when it is active again; "
                + "this sweep's leftovers are not saved over it");
        else
            _suspendedStore?.Save((_suspended ?? new List<PendingSortMove>()).Select(m =>
                new GhSuspendedMove($"{m.From.Map}/{m.From.Room}", $"{m.To.Map}/{m.To.Room}",
                    m.ItemName, m.Count, m.IsCarried, m.RequiresSearch)), _suspendedGameData);

        if (stillCarried.Count > 0)
        {
            _log?.Warn(LogCategory,
                $"sweep ending with {stillCarried.Count} item(s) still carried, undelivered: "
                + string.Join("; ", stillCarried.Select(m => $"{m.ItemName} (from {m.From}) -> {m.To}")));
        }

        ReportSaturatedCategories();

        // Name the rooms that hit capacity — otherwise a sweep that quietly
        // rerouted half its load reads the same as one that had nowhere to go.
        if (_fullRooms.Count > 0)
        {
            _log?.Warn(LogCategory,
                $"{_fullRooms.Count} room(s) were full this sweep: "
                + string.Join(", ", _fullRooms.OrderBy(r => r.Map).ThenBy(r => r.Room))
                + ". Label another room for the same category to give them a backup.");
        }

        return new GhSweepReport(_movedSoFar.ToList(), _leftInPlace.ToList(), _stranded.ToList(), stoppedBecause);
    }

    private void BeginSortPhase()
    {
        Phase = SweepPhase.Sorting;
        CaptureCarryBaseline();
        BuildSortQueue();
        SplitOversizedMoves();
        StrandUnmovableItems();
        // The floors the sort starts from, to be kept in step with every copy the
        // game confirms taken or dropped.
        _expectedAfterSort = new GhFloorCounts(_itemNames, _visibleByRoom);
        _hiddenTaken = new GhFloorCounts(_itemNames);
        _log?.Info(LogCategory,
            $"recon complete; sort queue: {_pending.Count} move(s), {_leftInPlace.Count} left in place");
        PhaseChanged?.Invoke();
        if (_pending.Count == 0) FinishSweep();
    }

    // The carry weight one queued move adds to the pack (whole stack). 0 when the
    // item's weight isn't in game data — never block a pickup on missing data.
    private int MoveWeight(PendingSortMove move)
        => (_itemNames.WeightOf(move.ItemName) ?? 0) * Math.Max(1, move.Count);

    // Capture the working-weight baseline as the sort phase opens: recon picks
    // nothing up, so the live CurrentWeight is exactly the gear+pack weight we carry
    // with zero sort-items (our base), and MaxWeight is the ceiling. No encumbrance
    // reading yet → no cap (fill everything, strand nothing), as the pre-planner
    // engine did.
    private void CaptureCarryBaseline()
    {
        if (_inventory?.Snapshot.Encumbrance is { MaxWeight: > 0 } enc)
        {
            _maxCarryWeight = enc.MaxWeight;
            // Discount anything the sort ledger already owns. A fresh sort opens
            // with an empty load so this changes nothing there — but Resume enters
            // Sorting ALREADY carrying the items it re-adopted, and counting those
            // as the player's gear inflates base by exactly their weight. That
            // collapses the budget, trips the too-tight-to-collect guard, and the
            // resumed sweep delivers its load and immediately stops.
            _baseCarryWeight = Math.Max(0, enc.CurrentWeight - LedgerCarriedWeight());
        }
        else
        {
            _maxCarryWeight = int.MaxValue;
            _baseCarryWeight = 0;
        }
        // A later resync can still beat this reading if the player sheds gear, so
        // track the minimum rather than pinning the opening one.
        _minBaseCarryWeight = _baseCarryWeight;
    }

    // Correct the baseline from a fresh `i` after a capacity refusal proved the
    // ledger drifted. Fold the discrepancy into base so that base + ledger-carried
    // equals the real current weight and future headroom matches reality.
    private void ResyncCarryBaseline()
    {
        if (_inventory?.Snapshot.Encumbrance is not { MaxWeight: > 0 } enc) return;
        _maxCarryWeight = enc.MaxWeight;
        _baseCarryWeight = Math.Max(0, enc.CurrentWeight - LedgerCarriedWeight());
        _minBaseCarryWeight = Math.Min(_minBaseCarryWeight, _baseCarryWeight);
        _log?.Info(LogCategory,
            $"resynced carry weight: max={_maxCarryWeight} current={enc.CurrentWeight} "
            + $"base={_baseCarryWeight} (lowest {_minBaseCarryWeight}) ledgerCarried={LedgerCarriedWeight()}"
            + (_baseCarryWeight > _minBaseCarryWeight
                ? $"; pack holds {_baseCarryWeight - _minBaseCarryWeight} of weight Roomba didn't collect, "
                  + "so live headroom is down but nothing is written off for it"
                : string.Empty));
    }

    // Total weight of everything Roomba is currently carrying (picked up, not yet
    // delivered) — the ledger we track in lieu of a per-transaction `i`.
    private int LedgerCarriedWeight()
        => _pending.Where(m => m.IsCarried && !m.Delivered).Sum(MoveWeight);

    // The most sort-item weight we could hold at once RIGHT NOW: MaxWeight minus
    // whatever the pack is currently carrying that isn't ours. Live on purpose —
    // headroom has to track reality or we over-fill and earn a capacity refusal.
    // int.MaxValue when there's no weight data to judge by.
    private int WorkingBudget()
        => _maxCarryWeight == int.MaxValue
            ? int.MaxValue
            : Math.Max(0, _maxCarryWeight - _baseCarryWeight);

    // The budget at our emptiest this sweep. "Base" is everything in the pack the
    // sort ledger doesn't own, and it is NOT stable: auto-get loot, a quest item,
    // anything picked up mid-sweep inflates it, and it only falls again once that
    // weight leaves. So the live budget is the wrong yardstick for any permanent
    // decision — measured against a dip, an item looks unmovable when it would fit
    // fine minutes later. Use this for "could we EVER carry this", and the live
    // budget for "does it fit at this moment".
    private int BestCaseBudget()
        => _maxCarryWeight == int.MaxValue
            ? int.MaxValue
            : Math.Max(0, _maxCarryWeight - _minBaseCarryWeight);

    // How much more can be carried right now, from the tracked ledger (no `i`):
    // working budget minus what's already in the pack. No cap without weight data.
    private int CurrentHeadroom()
        => _maxCarryWeight == int.MaxValue
            ? int.MaxValue
            : Math.Max(0, WorkingBudget() - LedgerCarriedWeight());

    // Split a queued stack heavier than the whole working budget into budget-sized
    // sub-moves, so a big pile (e.g. 140 torches) is carried across several trips
    // instead of stranded wholesale. Splits only when a SINGLE unit still fits the
    // budget — a unit too heavy to carry at all is left to StrandUnmovableItems.
    // No-op without weight data. Runs at sort start and after each resync (a shrunk
    // budget can require finer splitting; already-carried moves are left alone).
    private void SplitOversizedMoves()
    {
        int workingBudget = WorkingBudget();
        if (workingBudget == int.MaxValue) return;

        List<PendingSortMove> split = new();
        int trips = 0;
        foreach (PendingSortMove move in _pending.ToList())
        {
            if (move.IsCarried || move.Delivered) continue;
            int unitWeight = _itemNames.WeightOf(move.ItemName) ?? 0;
            if (unitWeight <= 0 || unitWeight > workingBudget) continue; // no data, or a single unit can't be carried
            int perTrip = workingBudget / unitWeight;                    // >= 1 units that fit an empty pack
            if (move.Count <= perTrip) continue;                         // already a single trip

            IReadOnlyList<int> loads = GhSortPlanner.SplitIntoTrips(move.Count, perTrip);
            int index = _pending.IndexOf(move);
            _pending.RemoveAt(index);                                    // replace the stack with its trip-sized chunks
            for (int i = 0; i < loads.Count; i++)
            {
                _pending.Insert(index + i, new PendingSortMove
                {
                    From = move.From,
                    To = move.To,
                    ItemName = move.ItemName,
                    Count = loads[i],
                    RequiresSearch = move.RequiresSearch,
                });
            }
            split.Add(move);
            trips += loads.Count;
            _log?.Debug(LogCategory,
                $"split {move.Count}x {move.ItemName} ({move.Count * unitWeight} > budget {workingBudget}) "
                + $"into {loads.Count} trips of up to {perTrip}: {move.From} -> {move.To}");
        }
        if (split.Count > 0)
            _log?.Info(LogCategory,
                $"split {split.Count} stack(s) too heavy for one trip (budget {workingBudget}) "
                + $"into {trips} trips: {NameStacks(split)}");
    }

    // Strand any not-yet-carried queued item whose SINGLE unit is too heavy to ever
    // carry — its own weight exceeds the whole working budget, so no delivery could
    // free enough room and no split could help. (Oversized stacks of a movable unit
    // are handled by SplitOversizedMoves, which runs first.) Without this the planner
    // would never route to it and the sweep could never finish. Surfaced under
    // LeftInPlace so the user sees what was skipped. No-op without weight data.
    private void StrandUnmovableItems()
    {
        // Judged against the BEST budget we've seen this sweep, not the current
        // one. This decision is permanent — the move is removed and recorded — so
        // "too heavy" has to mean "too heavy at our emptiest", not "too heavy
        // right now". The live budget dips whenever the pack holds anything Roomba
        // didn't put there (auto-get loot, a quest item), and writing items off
        // against a dip permanently discards armour that fits perfectly well once
        // the pack clears.
        int bestBudget = BestCaseBudget();
        if (bestBudget == int.MaxValue) return;
        List<PendingSortMove> tooHeavy = _pending
            .Where(m => !m.Delivered && !m.IsCarried
                        && (_itemNames.WeightOf(m.ItemName) ?? 0) > bestBudget).ToList();
        foreach (PendingSortMove move in tooHeavy)
        {
            _pending.Remove(move);
            _leftInPlace.Add(new GhSweepItemFound(move.From, move.ItemName, GhLeftReason.TooHeavy));
            _log?.Debug(LogCategory,
                $"too heavy to carry (unit weight {_itemNames.WeightOf(move.ItemName)} > best-case budget "
                + $"{bestBudget}): leaving {move.Count}x {move.ItemName} at {move.From}");
        }
        if (tooHeavy.Count > 0)
            _log?.Info(LogCategory,
                $"too heavy to carry at any time (best-case budget {bestBudget}): leaving "
                + $"{tooHeavy.Count} stack(s) where they are: {NameStacks(tooHeavy)}");
    }

    // Build the sort queue from what recon observed. Delegates the actual
    // "what should move where" decision to GhSortQueueBuilder (a pure
    // function testable without this engine's LoopRunner/RoomTracker/
    // MessageRouter wiring) so the decision logic and the dispatch state
    // machine stay separately verifiable. Only ever reads recon's own floor
    // ledgers (GroundItemTracker-sourced) — never InventoryManager's carried
    // snapshot, so a pre-existing carried item can never become a
    // PendingSortMove.
    private void BuildSortQueue()
    {
        _pending.Clear();
        _leftInPlace.Clear();

        // A room's visible stacks and its hidden ones are queued apart, the visible
        // first: an item with copies in both is two moves. The game takes from the
        // visible stack before a found hidden one, so the first `get` is answered
        // whatever the search before it finds, and a hidden stack that search
        // misses fails on its own (`You don't see … here.`) without holding up the
        // copies in plain sight.
        foreach (RoomKey room in _observedByRoom.Keys)
        {
            int first = _pending.Count, leftBefore = _leftInPlace.Count;
            QueueFrom(room, _visibleByRoom, requiresSearch: false);
            QueueFrom(room, _hiddenByRoom, requiresSearch: true);
            LogQueuedFrom(room, _pending.Skip(first).ToList(), _leftInPlace.Count - leftBefore);
        }
        RestoreCarriedManifest();
    }

    private void QueueFrom(RoomKey room, Dictionary<RoomKey, List<string>> ledger, bool requiresSearch)
    {
        if (!ledger.TryGetValue(room, out List<string>? stacks) || stacks.Count == 0) return;

        (IReadOnlyList<GhPendingMove> moves, IReadOnlyList<GhSweepItemFound> leftInPlace) =
            GhSortQueueBuilder.Build(
                new Dictionary<RoomKey, IReadOnlyList<string>> { [room] = stacks },
                _labels.Labels, _itemNames);

        foreach (GhPendingMove move in moves)
        {
            _pending.Add(new PendingSortMove
            {
                From = move.From,
                To = move.To,
                ItemName = move.ItemName,
                Count = move.Count,
                RequiresSearch = requiresSearch,
            });
            _log?.Debug(LogCategory,
                $"queued {move.Count}x {move.ItemName}: {move.From} -> {move.To}"
                + (requiresSearch ? " (hidden)" : string.Empty));
        }
        _leftInPlace.AddRange(leftInPlace);
    }

    // How many of a pass's stacks an Info line names before it only counts them. A
    // vault queues hundreds of stacks; a line each pushed everything else out of
    // the program log, and the Debug lines still carry every one.
    private const int StacksNamedPerLogLine = 5;
    private const int DestinationsNamedPerLogLine = 6;

    // One line for what a room contributes to the sort queue: how much leaves it and
    // for which rooms.
    private void LogQueuedFrom(RoomKey room, List<PendingSortMove> queued, int leftInPlace)
    {
        if (_log is null || (queued.Count == 0 && leftInPlace == 0)) return;

        StringBuilder line = new($"queued from {room}: {queued.Count} stack(s), {queued.Sum(m => m.Count)} item(s)");
        int hidden = queued.Count(m => m.RequiresSearch);
        if (hidden > 0) line.Append($", {hidden} of the stacks hidden");
        if (queued.Count > 0)
        {
            var destinations = queued.GroupBy(m => m.To).OrderByDescending(g => g.Count()).ToList();
            line.Append(" -> " + string.Join(", ", destinations
                .Take(DestinationsNamedPerLogLine).Select(g => $"{g.Key} ({g.Count()})")));
            if (destinations.Count > DestinationsNamedPerLogLine)
                line.Append($" and {destinations.Count - DestinationsNamedPerLogLine} more room(s)");
        }
        if (leftInPlace > 0) line.Append($"; {leftInPlace} stack(s) left in place");
        _log.Info(LogCategory, line.ToString());
    }

    // "3x torch, mace" for the first few of a pass's stacks, then how many more.
    private static string NameStacks(IReadOnlyList<PendingSortMove> stacks) =>
        NameFew(stacks.Take(StacksNamedPerLogLine + 1).Select(Stack).ToList(), stacks.Count);

    // The record on disk is another set's: what the pack holds for that set, and its
    // rooms, are kept for when it is active again, not saved or cleared over.
    private bool StoredRecordIsOtherSets
        => _suspendedStore is { Any: true } store && OtherGameDataRefusal(store.GameData) is not null;

    // An unfinished queue saved on another game-data set than the active one: its
    // rooms are that set's numbers, other rooms here. Null when it may be used (the
    // same set, or a record saved before queues were stamped).
    private string? OtherGameDataRefusal(string? savedOn)
    {
        string? now = _activeGameData();
        if (savedOn is null || string.Equals(savedOn, now, StringComparison.OrdinalIgnoreCase)) return null;
        return $"The unfinished sweep was saved on game data '{savedOn}', and '{now ?? "(none)"}' is active now: "
            + "its rooms are that game data's.";
    }

    // Re-adopt what a previous sweep left in the pack. These enter already
    // IsCarried, so the planner treats them as deliveries owed — with the unload
    // hysteresis that means a pack still full from last time is emptied before
    // anything new is collected, which is the behaviour we want anyway.
    //
    // The manifest is a record, not a fact: the player may have dropped, sold or
    // worn any of it in between. So a restored move is queued only when a fresh
    // inventory read still shows the item. Without live inventory data we adopt
    // nothing rather than guess — a phantom carried move sends a drop the game
    // partial-matches onto some other item we really are holding.
    private void RestoreCarriedManifest()
    {
        if (_suspendedStore is null) return;
        IReadOnlyList<GhSuspendedMove> manifest = _suspendedStore.LoadCarried();
        if (manifest.Count == 0) return;
        if (OtherGameDataRefusal(_suspendedStore.GameData) is { } refusal)
        {
            _log?.Info(LogCategory, $"{manifest.Count} item(s) remembered from the last sweep not adopted: {refusal}");
            return;
        }

        if (_inventory is null || !_inventory.IsLoaded)
        {
            _log?.Warn(LogCategory,
                $"{manifest.Count} item(s) remembered from the last sweep, but inventory hasn't been read "
                + "— not adopting them rather than risk delivering something we aren't holding");
            return;
        }

        IReadOnlyList<string> carried = _inventory.Snapshot.CarriedItems;
        int adopted = 0, gone = 0;
        foreach (GhSuspendedMove entry in manifest)
        {
            if (entry.Item is not { Length: > 0 } name
                || ParseRoom(entry.From) is not { } from
                || ParseRoom(entry.To) is not { } to) continue;

            int held = HeldCount(name, carried);
            if (held == 0) { gone++; continue; }

            _pending.Add(new PendingSortMove
            {
                From = from,
                To = to,
                ItemName = name,
                // Clamp to what the pack ACTUALLY holds. The manifest's count is last
                // session's record; a stack sold or dropped down since would otherwise
                // queue a drop for units that aren't there — and the game partial-
                // matches that surplus drop onto a DIFFERENT item we still hold.
                Count = Math.Min(Math.Max(1, entry.Count), held),
                RequiresSearch = false,
                IsCarried = true,
            });
            adopted++;
        }

        _log?.Info(LogCategory,
            $"resuming {adopted} item(s) still carried from the last sweep"
            + (gone > 0 ? $"; {gone} no longer in the pack and dropped from the manifest" : string.Empty));
    }

    // Rebuild the suspended queue from the persisted record, for a Resume in a
    // session that never ran the sweep that made it. Carried entries honour the same
    // invariant a fresh Start does — never queue a delivery for an item the pack no
    // longer holds: when a live inventory read is available it's verified here (gone
    // entries dropped, partially-sold stacks clamped) rather than issued and unwound
    // by delivery-time refusal. Only when inventory hasn't been read do we adopt a
    // carried entry unverified and lean on that refusal path. Uncollected entries are
    // planned work, re-verified by their own get. Unparseable rows are skipped.
    private List<PendingSortMove>? RehydrateSuspended()
    {
        if (_suspendedStore is null) return null;
        IReadOnlyList<GhSuspendedMove> saved = _suspendedStore.Load();
        if (saved.Count == 0) return null;

        IReadOnlyList<string>? carried =
            _inventory is { IsLoaded: true } ? _inventory.Snapshot.CarriedItems : null;

        List<PendingSortMove> restored = new();
        int droppedFromManifest = 0;
        foreach (GhSuspendedMove entry in saved)
        {
            if (entry.Item is not { Length: > 0 } name
                || ParseRoom(entry.From) is not { } from
                || ParseRoom(entry.To) is not { } to) continue;

            int count = Math.Max(1, entry.Count);
            if (entry.Carried && carried is not null)
            {
                int held = HeldCount(name, carried);
                if (held == 0) { droppedFromManifest++; continue; }
                count = Math.Min(count, held);
            }

            restored.Add(new PendingSortMove
            {
                From = from,
                To = to,
                ItemName = name,
                Count = count,
                RequiresSearch = entry.Hidden,
                IsCarried = entry.Carried,
            });
        }

        _log?.Info(LogCategory,
            $"rehydrated {restored.Count} unfinished move(s) from the last session's sweep"
            + (droppedFromManifest > 0
                ? $"; {droppedFromManifest} carried item(s) no longer in the pack and dropped from the manifest"
                : string.Empty));
        return restored.Count > 0 ? restored : null;
    }

    private static RoomKey? ParseRoom(string? coordinate)
    {
        (int? map, int? room) = RoomSearchService.TryParseCoordinate(coordinate ?? string.Empty);
        return map is int m && room is int r ? new RoomKey(m, r) : null;
    }

    // Recon capture. An arrival description is staged while RoomTracker still
    // says Pending, then committed to NewRoom as the room's visible floor. A list
    // read while standing in the room goes to the ledger of whatever printed it: a
    // search's reply is hidden stacks, a redisplay is the visible floor again.
    private void OnSurveyUpdated()
    {
        var snapshot = new List<string>(_groundItems.Items);
        FloorSurveySource source = _groundItems.LastSurveySource;
        if (Phase == SweepPhase.Sorting)
        {
            RecountFrom(snapshot, source);
            return;
        }
        if (Phase != SweepPhase.Reconning && Phase != SweepPhase.FinalRecon) return;

        if (_tracker.State.Confidence == RoomConfidence.Pending
            || _tracker.State.Confidence == RoomConfidence.PendingRespawn)
        {
            // A search's reply is of the room being left, never the one arrived in.
            if (source == FloorSurveySource.SearchReply) return;
            _pendingArrivalSurvey = snapshot;
            _pendingArrivalReadTime = _groundItems.LastSurveyReadTime;
            return;
        }

        if (_tracker.State.Confidence != RoomConfidence.Confirmed
            || _tracker.State.CurrentRoom is not { } current
            || !_sweepRooms.Contains(current.Key)) return;

        RoomKey room = current.Key;
        bool searchingHere = _reconSearchRoom is { } searchRoom && searchRoom.Equals(room);
        long started = _timestamp();
        if (source == FloorSurveySource.SearchReply)
        {
            // With searching off nothing hidden is recorded or sorted, whoever it
            // was that searched.
            if (!_labels.SearchForHidden) return;
            GhSurveyMerger.Merge(_hiddenByRoom, room, snapshot, _itemNames);
        }
        else if (source == FloorSurveySource.Unknown && searchingHere && _reconSearchesSent > 0)
        {
            // Our own search is out and this list is not shown to be a room
            // display: most likely its reply, with the echo knocked away from it.
            // It goes by the older arithmetic, never straight onto the visible
            // record, where a hidden stack would be queued with no search and then
            // written off as gone.
            if (!_reportedUnattributedLists)
            {
                _reportedUnattributedLists = true;
                _log?.Warn(LogCategory,
                    $"a list read at {room} during a search follows neither the search's echo nor a room "
                    + "name, so it can't be told from a redisplay of the room: hidden copies of an item "
                    + "that is also in plain sight are not added to it");
            }
            GhSurveyMerger.MergeUnattributed(_visibleByRoom, _hiddenByRoom, room, snapshot, _itemNames);
        }
        else if (source == FloorSurveySource.RoomDisplay)
        {
            GhSurveyMerger.Replace(_visibleByRoom, room, snapshot, _itemNames);
            if (Phase == SweepPhase.Reconning) _readOnRecon.Add(room);
        }
        else
        {
            // Not shown to be a display, so it may not be the whole floor (someone
            // else's search reply with its echo knocked away, say): it can add to
            // the visible record and raise a count, never take anything off it.
            GhSurveyMerger.Merge(_visibleByRoom, room, snapshot, _itemNames);
            if (Phase == SweepPhase.Reconning) _readOnRecon.Add(room);
        }
        _observedByRoom[room] = GhSurveyMerger.Total(_visibleByRoom, _hiddenByRoom, room, _itemNames);
        NoteSurvey(room, _groundItems.LastSurveyReadTime, Stopwatch.GetElapsedTime(started, _timestamp()));

        // While its searches run, the room is written and logged once, when they end.
        if (searchingHere) return;
        if (Phase == SweepPhase.FinalRecon)
        {
            NoteFinalLapRead(room);
            return;
        }
        RecordItemLocations(room);
        LogRoomSurveyIfHuge(room);
    }

    private void LogRoomSurveyIfHuge(RoomKey room)
    {
        if (_observedByRoom.TryGetValue(room, out List<string>? floor) && floor.Count >= HugeFloorStacks)
            LogRoomSurvey(room);
    }

    // Writes what the room holds to the item-location log. Once per visit, when the
    // reads of it are done: every search rolls again and may name fewer stacks or
    // smaller ones, so only the highest counts of the whole visit are the room.
    private void RecordItemLocations(RoomKey room)
    {
        if (_itemLocations is null || !_observedByRoom.TryGetValue(room, out List<string>? floor)) return;
        long started = _timestamp();
        _itemLocations.RecordRoom(room, floor);
        TimeSpan took = Stopwatch.GetElapsedTime(started, _timestamp());
        _itemLogWriteHere = took;
        if (SlowestItemLogWrite is not { } slowest || took > slowest.Took) SlowestItemLogWrite = (room, took);
    }

    private void NoteSurvey(RoomKey room, TimeSpan read, TimeSpan merge)
    {
        _surveysHere++;

        List<string> floor = _observedByRoom[room];
        GhSurveyTiming timing = new(room, floor.Count, UnitCount(floor), read, merge);
        if (_slowestSurveyHere is null || timing.Total > _slowestSurveyHere.Total) _slowestSurveyHere = timing;
        if (SlowestSurvey is null || timing.Total > SlowestSurvey.Total) SlowestSurvey = timing;
    }

    // What the two ledgers hold for a room, for the bug report: stacks and items
    // on display, then stacks and items only searches found.
    public (int VisibleStacks, int VisibleItems, int HiddenStacks, int HiddenItems) FloorLedgerAt(RoomKey room)
    {
        List<string> visible = _visibleByRoom.GetValueOrDefault(room) ?? new List<string>();
        List<string> hidden = _hiddenByRoom.GetValueOrDefault(room) ?? new List<string>();
        return (visible.Count, UnitCount(visible), hidden.Count, UnitCount(hidden));
    }

    private static int UnitCount(IEnumerable<string> floor) =>
        floor.Sum(item => CountedCommand.SplitLeadingCount(item).Count);

    // One line for the room recon is leaving: what it holds, how much of that is on
    // display and how much only a search found, and what the reads cost. The hidden
    // stacks are named up to HiddenNamesLogged; the item-location log has them all.
    private void LogRoomSurvey(RoomKey room, int searches = 0)
    {
        if (_log is null) return;
        List<string> floor = _observedByRoom.GetValueOrDefault(room) ?? new List<string>();

        (int visibleStacks, int visibleItems, int hiddenStacks, int hiddenItems) = FloorLedgerAt(room);
        StringBuilder line = new(floor.Count == 0
            ? $"recon at {room}: nothing in the room"
            : $"recon at {room}: {UnitCount(floor)} item(s) of {floor.Count} kind(s) in the room; "
              + $"on display {visibleItems} in {visibleStacks} stack(s)");
        if (searches > 0) line.Append($"; searched {searches} time(s)");
        if (hiddenStacks > 0)
        {
            List<string> hidden = _hiddenByRoom[room];
            line.Append($"; hidden {hiddenItems} in {hiddenStacks} stack(s): "
                + string.Join(", ", hidden.Take(HiddenNamesLogged)));
            if (hiddenStacks > HiddenNamesLogged) line.Append($" (+{hiddenStacks - HiddenNamesLogged} more)");
        }
        if (_slowestSurveyHere is { } slowest)
            line.Append($"; {_surveysHere} floor read(s), slowest: {slowest.StagesText}");
        if (_itemLogWriteHere is { } wrote)
            line.Append($"; item log written in {wrote.TotalMilliseconds:F1} ms");
        _log.Info(LogCategory, line.ToString());
    }

    // Called from AppServices' early RoomTracker.StateChanged wrapper — see the
    // constructor comment on why this manager doesn't subscribe itself. The
    // filtering below (genuine room change, room is labeled) stays here rather
    // than trusting the wrapper's own filter, since a public method shouldn't
    // assume a specific caller's pre-filtering.
    public void OnRoomChanged(RoomTransition t)
    {
        if (Phase == SweepPhase.Idle) return;

        // Diagnostic visibility for a reported-but-unreproduced failure mode
        // (a circuit room's arrival silently produces zero dispatch — no
        // search, no get/drop — the loop just continues past it). Every
        // filter below was a bare `return` with no trace; if this recurs, a
        // log capture through this method now shows EXACTLY which filter
        // declined the transition, rather than requiring another blind
        // investigation. Debug level — this fires on every room-adjacent
        // transition, most of them legitimately filtered (BFS-filled rooms,
        // in-place confidence bumps), so Info would drown the log.
        if (t.NewRoom is null)
        {
            _log?.Debug(LogCategory,
                $"OnRoomChanged declined: NewRoom is null (confidence {t.PreviousConfidence} -> {t.NewConfidence})");
            return;
        }
        if (t.PreviousRoom is { } prev && prev.Key.Equals(t.NewRoom.Key))
        {
            _log?.Debug(LogCategory,
                $"OnRoomChanged declined: not a genuine room change ({t.PreviousRoom.Key} -> {t.NewRoom.Key}, "
                + $"confidence {t.PreviousConfidence} -> {t.NewConfidence})");
            return;
        }

        RoomKey here = t.NewRoom.Key;
        _surveysHere = 0;
        _slowestSurveyHere = null;
        _itemLogWriteHere = null;
        bool reading = Phase is SweepPhase.Reconning or SweepPhase.FinalRecon;
        List<string>? display = _pendingArrivalSurvey;
        TimeSpan readTime = display is null ? TimeSpan.Zero : _pendingArrivalReadTime;
        _pendingArrivalSurvey = null;
        // A display with no floor list is a floor with nothing in plain sight,
        // which is how a room that lost its last stack looks, on any pass through
        // it. A room too dark to show, or entered blind, displayed nothing and
        // says nothing.
        bool lit = !_tracker.IsInDarkRoom && !_tracker.EnteredBlind;
        if (display is null && lit) display = new List<string>();
        if (Phase == SweepPhase.Reconning && lit && _sweepRooms.Contains(here)) _readOnRecon.Add(here);
        if (reading && display is not null && _sweepRooms.Contains(here))
        {
            long started = _timestamp();
            GhSurveyMerger.Replace(_visibleByRoom, here, display, _itemNames);
            _observedByRoom[here] = GhSurveyMerger.Total(_visibleByRoom, _hiddenByRoom, here, _itemNames);
            NoteSurvey(here, readTime, Stopwatch.GetElapsedTime(started, _timestamp()));
            // A room about to be searched is written once, when its searches end.
            if (Phase == SweepPhase.FinalRecon) NoteFinalLapRead(here);
            else if (!_labels.SearchForHidden) RecordItemLocations(here);
        }

        if (!_sweepRooms.Contains(here))
        {
            _log?.Debug(LogCategory, $"OnRoomChanged declined: {here} is not on the sweep circuit");
            return;
        }

        if (Phase == SweepPhase.Reconning)
        {
            // Only hold the room to send `sea` when the user opted into hidden-item
            // search. With it off (the default), recon just observes the room's
            // visible floor — the room display already surfaced it via the survey —
            // and lets the loop walk on, so nothing hidden is ever tagged or sorted.
            if (_labels.SearchForHidden) BeginRoomSearches(here);
            else LogRoomSurveyIfHuge(here);
            return;
        }
        // Final recon just refreshes each room's floor (the arrival survey above
        // already merged it) — observe-only, no search and no get/drop.
        if (Phase == SweepPhase.FinalRecon) return;
        DispatchAtRoom(here);
    }

    // Recon's own direct dispatch — holds GhSortGate itself and sends `sea`
    // SearchesPerRoom times, settling between each, exactly the way Sorting's
    // DispatchAtRoom holds the room for get/drop. Without this hold the loop's
    // own arrival-confirm-and-advance would send the next move before even the
    // FIRST search went out (the "leaves the room before picking up" bug this
    // whole gate-before-LoopRunner-registration fix addresses applies equally
    // to recon, since recon reacts to the same room-arrival wrapper).
    private void BeginRoomSearches(RoomKey room)
    {
        _reconSearchRoom = room;
        _reconSearchesSent = 0;
        AssertGate($"reconning at {room} ({_labels.SearchesPerRoom} search(es))");
        DispatchNextReconSearch();
    }

    private void DispatchNextReconSearch()
    {
        if (_reconSearchRoom is not { } room) return;
        _reconSearchesSent++;
        Send("sea");
        _log?.Debug(LogCategory,
            $"recon search {_reconSearchesSent}/{_labels.SearchesPerRoom} at {room}");
        Arm(ref _reconSearchSettle, ReconSearchSettle, OnReconSearchSettleElapsed);
    }

    private void OnReconSearchSettleElapsed()
    {
        Cancel(ref _reconSearchSettle);
        if (Phase == SweepPhase.Sorting && _sortSearchRoom is { } sortRoom)
        {
            if (_sortSearchesSent < Math.Max(1, _labels.SearchesPerRoom))
            {
                DispatchNextSortSearch();
                return;
            }

            _sortSearchRoom = null;
            DispatchAtRoomAfterSearch(sortRoom);
            return;
        }

        if (Phase != SweepPhase.Reconning || _reconSearchRoom is not { } room) return;

        if (_reconSearchesSent < _labels.SearchesPerRoom)
        {
            DispatchNextReconSearch();
            return;
        }

        _reconSearchRoom = null;
        RecordItemLocations(room);
        LogRoomSurvey(room, _reconSearchesSent);
        ReleaseGate("recon searches complete");
    }

    private void DispatchAtRoom(RoomKey room)
    {
        // Do whatever this room holds: drop carried items destined here, and pick
        // up pending sources here that still fit the live carry headroom. The trip
        // planner (GhSortPlanner) chooses WHICH room to walk to next — fill the
        // pack at the nearest fitting source before delivering — so on arrival we
        // don't second-guess the route, we just act on what's here.
        //
        // A pure transit room (nothing to drop, no pending pickup that fits the
        // current load) falls through untouched, so the old "don't interrupt a
        // delivery route with a mid-route hidden-item search" guarantee still
        // holds: in deliver mode nothing fits headroom anywhere, so no transit
        // room ever has a fitting get to search for.
        bool hasDrop = _pending.Any(m => m.IsCarried && !m.Delivered && m.To.Equals(room));
        List<PendingSortMove> fittingGets = FittingGetsAt(room);
        if (!hasDrop && fittingGets.Count == 0)
        {
            _log?.Debug(LogCategory,
                $"passing through {room}: nothing to drop and no pending pickup fits the current load");
            return;
        }

        // Visible items remain directly gettable after recon and must not pay
        // another SearchesPerRoom delay. Only a queue entry explicitly learned
        // from a recon search needs to be revealed again before its pickup.
        if (!fittingGets.Any(m => m.RequiresSearch))
        {
            DispatchAtRoomAfterSearch(room);
            return;
        }

        _sortSearchRoom = room;
        _sortSearchesSent = 0;
        AssertGate($"searching before pickup at {room}");
        DispatchNextSortSearch();
    }

    // The pending pickups at `room` that still fit the live carry headroom, in
    // discovery order (greedy per item — a heavy one that overflows is skipped
    // while a lighter later one may still fit). The planner only routes to a
    // source whose whole batch fits, so at a deliberate pickup target this keeps
    // everything; at a room we're only passing through (or dropping in with a full
    // pack) it correctly leaves the too-heavy pickups queued for a later pass.
    //
    // The copies in plain sight go first, and a hidden stack of an item is picked
    // up only once every visible copy of it here is in the pack or in this same
    // batch. The game serves a `get` from the visible stack before a found hidden
    // one, so a hidden pickup sent while visible copies lie there takes those: the
    // room would then show fewer than recorded, the hidden stack would be marked
    // taken while it is still hidden, and the later pickup of the visible stack
    // would come up short.
    private List<PendingSortMove> FittingGetsAt(RoomKey room)
    {
        int headroom = CurrentHeadroom();
        List<PendingSortMove> fitting = new();
        HashSet<string> visibleLeftBehind = new(StringComparer.OrdinalIgnoreCase);
        int used = 0;
        List<PendingSortMove> here = _pending
            .Where(m => !m.Delivered && !m.IsCarried && m.From.Equals(room)).ToList();
        foreach (PendingSortMove move in here.Where(m => !m.RequiresSearch))
        {
            int weight = MoveWeight(move);
            if (used + weight > headroom)
            {
                visibleLeftBehind.Add(GhSurveyMerger.Canonical(move.ItemName, _itemNames));
                continue;
            }
            used += weight;
            fitting.Add(move);
        }
        foreach (PendingSortMove move in here.Where(m => m.RequiresSearch))
        {
            if (visibleLeftBehind.Contains(GhSurveyMerger.Canonical(move.ItemName, _itemNames))) continue;
            int weight = MoveWeight(move);
            if (used + weight > headroom) continue;
            used += weight;
            fitting.Add(move);
        }
        return fitting;
    }

    // The weight of the lightest pickup each room still offers, for the planner to
    // choose a source by. A hidden stack waiting on visible copies of the same item
    // (FittingGetsAt) isn't on offer: routing to a room for it alone would arrive,
    // pick up nothing and walk on.
    private List<GhSortPlanner.PickupRoom> PickupsOnOffer()
    {
        List<PendingSortMove> uncollected = _pending.Where(p => !p.IsCarried && !p.Delivered).ToList();
        HashSet<(RoomKey, string)> visibleStill = new();
        foreach (PendingSortMove move in uncollected.Where(m => !m.RequiresSearch))
            visibleStill.Add((move.From, GhSurveyMerger.Canonical(move.ItemName, _itemNames).ToLowerInvariant()));

        return uncollected
            .Where(m => !m.RequiresSearch || !visibleStill.Contains(
                (m.From, GhSurveyMerger.Canonical(m.ItemName, _itemNames).ToLowerInvariant())))
            .GroupBy(p => p.From)
            .Select(g => new GhSortPlanner.PickupRoom(g.Key, g.Min(MoveWeight)))
            .ToList();
    }

    private void DispatchNextSortSearch()
    {
        if (_sortSearchRoom is not { } room) return;
        _sortSearchesSent++;
        Send("sea");
        _log?.Debug(LogCategory,
            $"sort search {_sortSearchesSent}/{Math.Max(1, _labels.SearchesPerRoom)} at {room}");
        Arm(ref _reconSearchSettle, ReconSearchSettle, OnReconSearchSettleElapsed);
    }

    private void DispatchAtRoomAfterSearch(RoomKey room)
    {
        // Hard invariant: never dispatch a get/drop for a room we're not
        // verifiably standing in right now. `room` comes from the
        // RoomTransition that triggered this call, which should already
        // match the tracker's live current room by construction — but this
        // gang house's rooms all display identically ("Bronze House Room"
        // repeated across the whole map), a known room-identity ambiguity,
        // and a mis-fired dispatch here sends a whole room's worth
        // of commands that all fail against wherever the player actually is.
        // Refuse rather than trust the parameter blindly.
        if (_tracker.State.CurrentRoom?.Key.Equals(room) != true)
        {
            _log?.Warn(LogCategory,
                $"dispatch refused: asked to dispatch at {room} but tracker's current room is "
                + $"{_tracker.State.CurrentRoom?.Key.ToString() ?? "(unknown)"} — room-identity mismatch");
            ReleaseGate("dispatch room identity mismatch");
            return;
        }

        List<PendingSortMove> drops = _pending
            .Where(m => !m.Delivered && m.IsCarried && m.To.Equals(room)).ToList();
        // Only pick up what still fits the live carry headroom (same filter the
        // arrival dispatch and the trip planner use). Items too heavy to add right
        // now stay queued and are retried on a later pass once a delivery frees
        // space — the fill-to-capacity, trip-minimizing contract.
        List<PendingSortMove> gets = FittingGetsAt(room);

        if (drops.Count == 0 && gets.Count == 0)
        {
            ReleaseGate("nothing dispatchable after room search");
            MaybeFinish();
            return;
        }

        AssertGate($"dispatching at {room}");
        _dispatchRoom = room;
        _outstandingDispatch.Clear();
        _outstandingDispatch.AddRange(drops);
        _outstandingDispatch.AddRange(gets);

        // Queue the batch and release it one command per prompt. Dumping a whole
        // room's worth at once trips the game's command-rate limit (stock nudges
        // with "Why don't you slow down for a few seconds?" then drops commands
        // outright with "You are typing too quickly"), and once that happens NONE
        // of the batch lands — nor does the loop's next move, which leaves the
        // tracker Pending on a move the server never processed. Letting the
        // game's own prompt meter the send makes that unreachable by
        // construction, and needs no guess at the rate.
        _commandQueue.Clear();
        ForgetRefusedPickups();
        foreach (PendingSortMove move in drops) _commandQueue.Enqueue(("drop", move));
        foreach (PendingSortMove move in gets) _commandQueue.Enqueue(("get", move));
        // The floor paces commands WITHIN a batch. Reaching a new room means we
        // just walked here, which is seconds of wall clock on its own, so the
        // first command of a batch shouldn't wait on the previous room's last one.
        _lastCommandSentAt = DateTimeOffset.MinValue;
        SendNextQueuedCommand();
    }

    // Release one queued get/drop. Re-arms both the settle timer (so it measures
    // "confirmations stopped arriving", not "the batch is long") and the
    // prompt-timeout backstop.
    private void SendNextQueuedCommand()
    {
        Cancel(ref _promptWait);
        if (_commandQueue.Count == 0) return;

        // Never send early, whatever woke us. A prompt arriving sooner than the
        // floor means the game is talking to us for some other reason — very often
        // a rate-limit line, whose prompt would otherwise release the next command
        // and earn another one. Re-arm and wait out the remainder instead.
        TimeSpan since = _now() - _lastCommandSentAt;
        if (since < MinCommandInterval)
        {
            Arm(ref _promptWait, MinCommandInterval - since, SendNextQueuedCommand);
            return;
        }

        (string verb, PendingSortMove move) = _commandQueue.Dequeue();
        _lastCommandSentAt = _now();
        _lastQueuedCommand = (verb, move);
        Arm(ref _dispatchSettle, DispatchSettleTimeout, OnDispatchSettleElapsed);
        if (verb == LookAgainVerb)
        {
            _awaitingLook = true;
            Send("l");
        }
        else if (verb == SearchAgainVerb)
        {
            _awaitingSearch = true;
            Send("sea");
        }
        else
        {
            CountedCommand.Emit(Send, verb, move.Count, move.ItemName, _isParadigm());
        }

        // A prompt normally releases the next one. Arm a bounded fallback so a
        // prompt we never see (or one swallowed by an interleaved burst) can't
        // strand the rest of the batch.
        if (_commandQueue.Count > 0) Arm(ref _promptWait, PromptWaitTimeout, SendNextQueuedCommand);
    }

    // The wire prompt came back, so the game has processed the last command.
    private void OnPromptObservedFromWire(Services.PromptObservation _) => OnPromptObserved();

    private void OnPromptObserved()
    {
        if (Phase != SweepPhase.Sorting) return;
        // Held off while the game is telling us to slow down; the backoff timer
        // owns the restart from there.
        if (_rateLimitBackoff is not null) return;
        SendNextQueuedCommand();
    }

    // Which groups of rooms ran out of space, and what couldn't be placed because
    // of it. Keyed by the set of rooms an item could legitimately have gone to, so
    // the end-of-sweep summary can say "these rooms are all full — label another
    // one for that category" instead of a flat list of items that went nowhere.
    private readonly Dictionary<string, (List<RoomKey> Rooms, List<string> Items)> _saturated = new();

    private void NoteSaturatedCategory(string itemName, GhItemClass? cls)
    {
        if (cls is not { } item) return;
        IReadOnlyList<RoomKey> candidates = GhDestinationResolver.CandidateRooms(item, _labels.Labels);
        if (candidates.Count == 0) return;   // nothing labelled for it — a different problem

        List<RoomKey> ordered = candidates.OrderBy(r => r.Map).ThenBy(r => r.Room).ToList();
        string key = string.Join(",", ordered);
        if (!_saturated.TryGetValue(key, out (List<RoomKey> Rooms, List<string> Items) entry))
        {
            entry = (ordered, new List<string>());
            _saturated[key] = entry;
        }
        if (!entry.Items.Contains(itemName)) entry.Items.Add(itemName);
    }

    // Spell out what to do about it. The per-item warnings say an item went
    // nowhere; this says WHICH rooms are saturated, which is the thing the user
    // can act on — label another room for that category.
    private void ReportSaturatedCategories()
    {
        if (_saturated.Count == 0) return;
        foreach ((List<RoomKey> rooms, List<string> items) in _saturated.Values
                     .OrderByDescending(e => e.Items.Count))
        {
            _log?.Warn(LogCategory,
                $"out of space for {items.Count} item type(s) — every room that takes them is full: "
                + string.Join(", ", rooms)
                + $". Examples: {string.Join(", ", items.Take(4))}"
                + (items.Count > 4 ? $" (+{items.Count - 4} more)" : string.Empty)
                + ". Label another room for this category to give them somewhere to go.");
        }
    }

    // Abandon whatever is left of the batch. Anything unsent stays on _pending
    // and is retried on a later visit, exactly like an unconfirmed command.
    private void ClearCommandQueue()
    {
        _commandQueue.Clear();
        _lastQueuedCommand = null;
        Cancel(ref _promptWait);
        Cancel(ref _rateLimitBackoff);
    }

    private void OnRateLimitBackoffElapsed()
    {
        _rateLimitBackoff = null;
        SendNextQueuedCommand();
    }

    private void Arm(ref IDisposable? timer, TimeSpan delay, Action tick)
    {
        timer?.Dispose();
        timer = _schedule(delay, tick);
    }

    private static void Cancel(ref IDisposable? timer)
    {
        timer?.Dispose();
        timer = null;
    }

    // The game reported a rate-limit clobber. On the hard form the last command
    // was DROPPED, so re-queue it at the front; on the soft nudge it probably
    // landed, so just leave a gap. Either way stop pushing for a beat.
    private void OnRateLimited(bool commandDropped)
    {
        if (Phase != SweepPhase.Sorting) return;
        if (_commandQueue.Count == 0 && _lastQueuedCommand is null) return;

        // The game answers one over-fast batch with a run of these lines, and
        // they arrive together. Treat the run as one event: already backing off
        // and not told a command was lost means there's nothing new to do, and
        // logging each line turns one incident into a wall of warnings.
        if (_rateLimitBackoff is not null && !commandDropped) return;

        if (commandDropped && _lastQueuedCommand is { } last)
        {
            // Put it back at the head, ahead of everything still waiting.
            Queue<(string Verb, PendingSortMove Move)> restored = new();
            restored.Enqueue(last);
            foreach ((string, PendingSortMove) queued in _commandQueue) restored.Enqueue(queued);
            _commandQueue.Clear();
            foreach ((string, PendingSortMove) queued in restored) _commandQueue.Enqueue(queued);
            _lastQueuedCommand = null;
        }

        _log?.Warn(LogCategory,
            $"rate-limited mid-dispatch ({(commandDropped ? "command dropped" : "slow-down nudge")}); "
            + $"backing off with {_commandQueue.Count} command(s) still queued");
        Cancel(ref _promptWait);
        Arm(ref _rateLimitBackoff, RateLimitBackoff, OnRateLimitBackoffElapsed);
    }

    // Backstop for a dispatched get/drop that fails without a confirmation line
    // this manager recognizes — a DROP that doesn't land, or a get-failure shape
    // other than the "You don't see X here." / currency-syntax ones
    // OnLineForGetFailure strands explicitly. Without this, _outstandingDispatch
    // never reaches zero and GhSortGate holds forever. Resets on every real
    // confirmation (ResolveConfirm), so it only fires once confirmations genuinely
    // stop arriving. Note: unlike a parsed get-failure, this LEAVES the moves
    // queued for a later-lap retry (a transient block, not a proven-gone item).
    private void OnDispatchSettleElapsed()
    {
        Cancel(ref _dispatchSettle);
        if (Phase != SweepPhase.Sorting || _outstandingDispatch.Count == 0) return;

        // The room was read again for refused pickups and no list came: a floor
        // with nothing in plain sight prints none, and a search that finds nothing
        // answers in other words.
        if ((_awaitingLook || _awaitingSearch) && _rateLimitBackoff is null)
        {
            SettleRefused(hidden: !_awaitingLook, Array.Empty<string>());
            return;
        }

        // Still feeding the batch out (or sitting in a rate-limit backoff, which
        // is deliberately longer than this window). "Confirmations stopped" isn't
        // a meaningful reading until every command has actually been sent, so
        // wait rather than abandoning a batch that's simply being paced.
        if (_commandQueue.Count > 0 || _rateLimitBackoff is not null)
        {
            Arm(ref _dispatchSettle, DispatchSettleTimeout, OnDispatchSettleElapsed);
            return;
        }

        _log?.Warn(LogCategory,
            $"dispatch settle timeout at {_dispatchRoom}: {_outstandingDispatch.Count} unconfirmed "
            + $"command(s) — leaving them queued for a later-lap retry: "
            + string.Join("; ", _outstandingDispatch.Select(m => $"{m.ItemName} ({(m.IsCarried ? "drop" : "get")})")));

        // A pickup the game answered in part stays queued for the rest only.
        foreach (PendingSortMove get in _outstandingDispatch.Where(m => !m.IsCarried).ToList())
            KeepWhatWasTaken(get);
        _outstandingDispatch.Clear();
        EndDispatch();
        ClearCommandQueue();
        // Trust the ledger — a lost command just means that move stays queued; no
        // `i` needed. (A real capacity refusal arrives as its own line and resyncs.)
        ContinueAfterTransaction("dispatch settle timeout");
    }

    // Self-drop confirmation ("You dropped X." / Paradigm's counted form) —
    // the same PlayerDrops pattern AutoDiscardManager confirms against.
    private void OnDropLine(MatchResult m)
    {
        if (Phase != SweepPhase.Sorting) return;
        if (m.Groups.Count < 2 || !string.IsNullOrEmpty(m.Groups[0])) return;   // another player's drop
        ResolveConfirm(m.Groups[1], isDrop: true);
    }

    // Self-get confirmation ("You took X.") — the same PlayerGets pattern
    // AcquisitionGate confirms gets against.
    private void OnGetLine(MatchResult m)
    {
        if (Phase != SweepPhase.Sorting) return;
        if (m.Groups.Count < 2 || !string.IsNullOrEmpty(m.Groups[0])) return;   // another player's pickup
        ResolveConfirm(m.Groups[1], isDrop: false);
    }

    // The room is at item capacity ("There is no room to drop X here."). One line
    // reroutes the whole batch: a full room refuses every drop we just sent, so
    // the first refusal marks the room and re-targets everything bound for it, and
    // the remaining refusal lines then match nothing and fall through harmlessly.
    private void OnDropRefusedLine(MatchResult m)
    {
        if (Phase != SweepPhase.Sorting) return;
        if (m.Groups.Count < 1) return;
        if (_tracker.State.CurrentRoom is not { } current) return;
        if (_dispatchRoom is not { } dispatchRoom || !dispatchRoom.Equals(current.Key)) return;

        (_, string name) = CountedCommand.SplitLeadingCount(m.Groups[0]);
        PendingSortMove? refused = _outstandingDispatch.FirstOrDefault(
            p => !p.Delivered && p.IsCarried && SameItem(p.ItemName, name));
        if (refused is null) return;   // a manual drop, or already handled

        if (_fullRooms.Add(dispatchRoom))
            _log?.Warn(LogCategory,
                $"{dispatchRoom} is full (refused {name}) — re-targeting everything bound for it "
                + "and prioritising it as a pickup source to free space");

        // Every outstanding drop here will be refused too; stop waiting on them.
        foreach (PendingSortMove queued in _outstandingDispatch
                     .Where(p => !p.Delivered && p.IsCarried && p.To.Equals(dispatchRoom)).ToList())
            _outstandingDispatch.Remove(queued);

        RetargetAwayFromFullRooms();

        PhaseChanged?.Invoke();
        if (_outstandingDispatch.Count == 0)
        {
            EndDispatch();
            ContinueAfterTransaction("destination full");
        }
    }

    // The room's commands are all answered or given up on: one line for what was
    // done here, then the room is let go.
    private void EndDispatch()
    {
        Cancel(ref _dispatchSettle);
        if (_dispatchRoom is { } room && _log is not null
            && (_pickedUpHere.Count + _deliveredHere.Count + _failedHere.Count > 0))
        {
            StringBuilder line = new($"at {room}:");
            if (_sortSearchesSent > 0 && _pickedUpHere.Concat(_failedHere).Any())
                line.Append($" searched {_sortSearchesSent} time(s);");
            if (_deliveredHere.Count > 0) line.Append($" delivered {NameFew(_deliveredHere)};");
            if (_pickedUpHere.Count > 0) line.Append($" picked up {NameFew(_pickedUpHere)};");
            if (_failedHere.Count > 0) line.Append($" not picked up: {NameFew(_failedHere)};");
            _log.Info(LogCategory, line.ToString().TrimEnd(';'));
        }
        _pickedUpHere.Clear();
        _deliveredHere.Clear();
        _failedHere.Clear();
        ForgetRefusedPickups();
        _sortSearchesSent = 0;
        _dispatchRoom = null;
    }

    // Re-resolve every pending move whose destination has gone full — carried and
    // not-yet-collected alike — onto the next room that admits it (another labeled
    // room for the same category, then the catch-all). Anything with nowhere left
    // is recorded and dropped from the queue rather than retried into the same
    // wall every lap, which is exactly what used to run forever.
    private void RetargetAwayFromFullRooms()
    {
        List<PendingSortMove> retargeted = new(), keptInPack = new(), leftBehind = new();
        foreach (PendingSortMove move in _pending
                     .Where(p => !p.Delivered && _fullRooms.Contains(p.To)).ToList())
        {
            GhItemClass? cls = GhItemClassifier.Classify(_itemNames, move.ItemName);
            RoomKey? dest = cls is { } c
                ? GhDestinationResolver.Resolve(c, _labels.Labels, _fullRooms)
                : null;

            if (dest is not { } target)
            {
                // Never forget an item we are actually holding. Dropping the move
                // while the item stays in the pack loses it three ways at once:
                // the player is left holding it with nothing tracking it, its
                // weight is misread as their gear at the next resync (which
                // collapses the carry budget), and the sweep re-collects the same
                // item on a later lap and abandons it again — one run left six
                // copies of the same gloves in the pack that way.
                //
                // Keep it queued instead. It has nowhere to go right now, so the
                // planner won't route to it (see the full-room filter on carried
                // destinations), but the ledger stays honest and the
                // suspended-sweep record hands it to the next sweep, by which time
                // a room may have space.
                if (move.IsCarried)
                {
                    NoteSaturatedCategory(move.ItemName, cls);
                    keptInPack.Add(move);
                    _log?.Debug(LogCategory, $"nowhere left for {move.ItemName} and it's in the pack");
                    continue;
                }

                NoteSaturatedCategory(move.ItemName, cls);
                leftBehind.Add(move);
                _log?.Debug(LogCategory, $"nowhere left for {move.ItemName} — leaving it at {move.From}");
                _leftInPlace.Add(new GhSweepItemFound(move.From, move.ItemName, GhLeftReason.AllDestinationsFull));
                _pending.Remove(move);
                continue;
            }

            retargeted.Add(move);
            _log?.Debug(LogCategory,
                $"re-targeting {move.Count}x {move.ItemName}: {move.To} (full) -> {target}");
            move.To = target;
        }

        // One line per outcome: a full room turns away every stack bound for it.
        if (retargeted.Count > 0)
            _log?.Info(LogCategory,
                $"re-targeted {retargeted.Count} stack(s) away from full rooms: {NameStacks(retargeted)}");
        if (keptInPack.Count > 0)
            _log?.Warn(LogCategory,
                $"nowhere left for {keptInPack.Count} stack(s) in the pack — still carrying them; the next "
                + $"sweep delivers them once a room has space: {NameStacks(keptInPack)}");
        if (leftBehind.Count > 0)
            _log?.Warn(LogCategory,
                $"nowhere left for {leftBehind.Count} stack(s): every matching room and the catch-all are "
                + $"full — left where they are: {NameStacks(leftBehind)}");
    }

    private void ResolveConfirm(string token, bool isDrop)
    {
        (int unitsThisLine, string name) = CountedCommand.SplitLeadingCount(token);
        if (_tracker.State.CurrentRoom is not { } current) return;
        if (_dispatchRoom is not { } dispatchRoom || !dispatchRoom.Equals(current.Key))
        {
            // Not our dispatch — but if something ELSE just dropped an item we
            // believe we're carrying (auto-discard binning loot we picked up is
            // the live case), our ledger is now wrong. Left stale, the next
            // delivery sends a drop for an item we no longer hold, and the game
            // partial-matches that name onto whatever else we're carrying.
            if (isDrop)
            {
                _expectedAfterSort?.Add(current.Key, name, unitsThisLine);
                ReconcileForeignDrop(name);
            }
            return;
        }

        // A pickup is credited to the copies in plain sight before a hidden stack of
        // the same item: that is the order the game takes them in.
        PendingSortMove? match = isDrop
            ? _outstandingDispatch.FirstOrDefault(p => !p.Delivered && p.IsCarried
                                         && SameItem(p.ItemName, name))
            : _outstandingDispatch.FirstOrDefault(p => !p.Delivered && !p.IsCarried && !p.RequiresSearch
                                         && SameItem(p.ItemName, name))
              ?? _outstandingDispatch.FirstOrDefault(p => !p.Delivered && !p.IsCarried
                                         && SameItem(p.ItemName, name));
        if (match is null)
        {
            // Same reasoning as above: a drop we didn't dispatch, while we happen
            // to be mid-dispatch in this room.
            if (isDrop)
            {
                _expectedAfterSort?.Add(current.Key, name, unitsThisLine);
                ReconcileForeignDrop(name);
            }
            return;
        }

        // What the game confirmed is what changed the floor, a copy at a time: the
        // final lap holds each room against it.
        if (isDrop) _expectedAfterSort?.Add(current.Key, match.ItemName, unitsThisLine);
        else if (match.RequiresSearch) _hiddenTaken?.Add(current.Key, match.ItemName, unitsThisLine);
        else _expectedAfterSort?.Add(current.Key, match.ItemName, -unitsThisLine);

        // One confirmation line reports however many UNITS that command moved, not
        // one move. Stock sends a 3x move as three separate commands answered by three
        // count-less lines (1 unit each); Paradigm sends it as one bulk `drop 3 orc-head`
        // answered by a single count-prefixed line (3 units at once). Add the line's own
        // count: a raw ++ left a Paradigm bulk move stuck at 1/3 forever, and on Stock
        // completing on the first line left two orphans that get misread as someone
        // else's drop — deleting an unrelated carried move and losing a real item.
        match.ConfirmedUnits += unitsThisLine;
        if (match.ConfirmedUnits < match.Count)
        {
            _log?.Debug(LogCategory,
                $"{(isDrop ? "dropped" : "took")} {match.ConfirmedUnits}/{match.Count} "
                + $"{match.ItemName} — awaiting the rest of the stack");
            Arm(ref _dispatchSettle, DispatchSettleTimeout, OnDispatchSettleElapsed);
            return;
        }

        if (isDrop)
        {
            match.Delivered = true;
            _movedSoFar.Add(new GhSweepMove(match.From, match.To, match.ItemName, match.Count));
            _deliveredHere.Add(Stack(match));
            _log?.Debug(LogCategory, $"delivered {match.Count}x {match.ItemName} -> {match.To}");
        }
        else
        {
            match.IsCarried = true;
            // The next leg is the delivery, which confirms its own units.
            match.ConfirmedUnits = 0;
            _pickedUpHere.Add(Stack(match));
            _log?.Debug(LogCategory,
                $"picked up {match.Count}x {match.ItemName} at {match.From}, carrying to {match.To}");
        }

        _outstandingDispatch.Remove(match);
        PhaseChanged?.Invoke();
        if (_outstandingDispatch.Count == 0)
        {
            EndDispatch();
            AdvanceAfterDispatch("room dispatch confirmed");
        }
        else
        {
            // A real confirmation landed — a genuine failure (no confirmation
            // line at all) is what OnDispatchSettleElapsed exists to catch, so
            // keep pushing the deadline out while commands are still resolving.
            Arm(ref _dispatchSettle, DispatchSettleTimeout, OnDispatchSettleElapsed);
        }
    }

    // A dispatch just fully resolved. Normally re-plan straight off the ledger; but
    // if a capacity refusal flagged the ledger as drifted, resync from a fresh `i`
    // first (which then re-plans). The one place `i` re-enters the happy path.
    private void AdvanceAfterDispatch(string reason)
    {
        if (_resyncPending)
        {
            _resyncPending = false;
            BeginInventoryResync(reason);
            return;
        }
        ContinueAfterTransaction(reason);
    }

    // Detect a get FAILURE for one of our outstanding pickups and drop that item
    // from the queue instead of retrying it forever — the "a vanished item pins an
    // otherwise-finished sweep in an endless get loop" case. Gated on an
    // outstanding get so a manual/other `get` failure is never misattributed.
    private void OnLineForGetFailure(LineExtractor.EmittedLine line)
    {
        if (Phase != SweepPhase.Sorting) return;

        if (DropCurrencySyntaxRegex.IsMatch(line.Text) || DropNotAllowedRegex.IsMatch(line.Text))
        {
            HandleDropSyntaxRefusal();
            return;
        }

        List<PendingSortMove> gets = _outstandingDispatch
            .Where(m => !m.IsCarried && !m.Delivered).ToList();
        if (gets.Count == 0) return;

        // A capacity refusal is NOT a gone item — the pickup is gettable, our
        // tracked working-weight just drifted low. Leave the refused gets queued and
        // resync the baseline from a fresh `i`, then re-plan (deliver to free room,
        // or strand what the corrected budget can't ever hold).
        if (GetCannotCarryRegex.IsMatch(line.Text))
        {
            HandleCapacityRefusal(gets);
            return;
        }

        Match notHere = GetNotHereRegex.Match(line.Text);
        if (notHere.Success)
        {
            // Match the echoed word to an outstanding get; fall back to the sole
            // outstanding get when the game reshaped or truncated the echo. Paradigm
            // echoes a counted get's count ahead of the name (`You don't see 10
            // blackwood long here.`), which the name lookup reads past. An echo that
            // names some other known item is not the sole get's: Stock sends a
            // stack as one `get` a copy, and the ones still on their way after the
            // stack ran out each answer again for a move already settled.
            string echo = notHere.Groups["echo"].Value;
            PendingSortMove? move = gets.FirstOrDefault(m => SameItem(m.ItemName, echo))
                ?? (gets.Count == 1 && _itemNames.FindByName(echo) is null ? gets[0] : null);
            if (move is not null) OnGetRefusedAsNotHere(move, line.Text.Trim());
            return;
        }
        // The currency-syntax misparse names no item. Dispatch is paced one
        // command per prompt, so the command just sent is the one that failed —
        // attribute to it rather than requiring a single outstanding get, which
        // was rarely true in a batch and left the failure unattributed. Retrying
        // the same name can't help, so it's stranded either way.
        if (GetCurrencySyntaxRegex.IsMatch(line.Text))
        {
            PendingSortMove? failed =
                _lastQueuedCommand is { Verb: "get", Move: { } sent } && gets.Contains(sent)
                    ? sent
                    : gets.Count == 1 ? gets[0] : null;
            if (failed is not null)
                StrandFailedGet(failed, "game misparsed the get as a currency command");
            return;
        }
        // The game's own refusal names no item either, and is attributed the same
        // way. It is permanent for the item (user, 2026-10-09): asking again every
        // lap would only be refused again.
        if (GetStrangeForceRegex.IsMatch(line.Text))
        {
            PendingSortMove? refused =
                _lastQueuedCommand is { Verb: "get", Move: { } sent } && gets.Contains(sent)
                    ? sent
                    : gets.Count == 1 ? gets[0] : null;
            if (refused is not null)
                StrandFailedGet(refused, "the game won't let it be picked up (a strange force)",
                    GhLeftReason.RefusedByGame);
        }
    }

    // The game didn't recognise our drop's item name, which almost always means we
    // aren't holding it — a pickup the ledger recorded that never actually landed
    // (a flooded `get`, say). Don't take that on trust: ask for a real `i` and let
    // OnFullInventoryParsed drop only the moves the inventory genuinely doesn't
    // show. Retrying is pointless either way, so the command stops here.
    private void HandleDropSyntaxRefusal()
    {
        List<PendingSortMove> drops = _outstandingDispatch
            .Where(m => m.IsCarried && !m.Delivered).ToList();
        if (drops.Count == 0) return;

        foreach (PendingSortMove drop in drops) _outstandingDispatch.Remove(drop);
        _verifyCarriedAfterResync = true;
        _resyncPending = true;
        _log?.Warn(LogCategory,
            $"game misparsed a drop as a currency command at {_dispatchRoom} "
            + $"({drops.Count} outstanding) — verifying against a fresh inventory");
        PhaseChanged?.Invoke();
        if (_outstandingDispatch.Count == 0)
        {
            EndDispatch();
            ClearCommandQueue();
            AdvanceAfterDispatch("drop syntax refusal");
        }
    }

    // An item left our pack without us dropping it. Whatever took it (auto-discard
    // is the one that actually bit), we are no longer carrying it, so the move
    // can't be delivered — and keeping it queued is worse than useless: the drop
    // we'd eventually send names an item we don't hold, and the game resolves that
    // name against something we DO hold. Forget it rather than re-collect it,
    // since whatever binned it will just bin it again next lap.
    private void ReconcileForeignDrop(string name)
    {
        if (Phase != SweepPhase.Sorting) return;

        // Only a drop we had no part in. A drop of an item we are dispatching
        // right now is ours — an extra unit of a stack, or a reply arriving just
        // after its move completed — and claiming it here deletes a DIFFERENT
        // carried move of the same item, losing a real item and corrupting the
        // weight ledger with it.
        if (_outstandingDispatch.Any(p => SameItem(p.ItemName, name))) return;
        if (_lastQueuedCommand is { Verb: "drop", Move: { } justSent }
            && SameItem(justSent.ItemName, name)) return;

        PendingSortMove? carried = _pending.FirstOrDefault(
            p => p.IsCarried && !p.Delivered && SameItem(p.ItemName, name));
        if (carried is null) return;

        _log?.Warn(LogCategory,
            $"{carried.ItemName} left the pack without us dropping it (auto-discard, or a manual drop) "
            + $"— abandoning its move to {carried.To} rather than sending a drop we can't honour");
        _leftInPlace.Add(new GhSweepItemFound(carried.From, carried.ItemName, GhLeftReason.NotActuallyCarried));
        _pending.Remove(carried);
        PhaseChanged?.Invoke();
    }

    // Post-`i` reconciliation for the above: anything we believe we're carrying
    // that the real inventory doesn't list was never picked up, so the move is a
    // phantom. Remove it outright — leaving it queued means dispatching a drop
    // that can only ever fail again.
    private void DropPhantomCarriedMoves()
    {
        if (_inventory is null) return;
        IReadOnlyList<string> carried = _inventory.Snapshot.CarriedItems;

        foreach (PendingSortMove move in _pending
                     .Where(p => p.IsCarried && !p.Delivered).ToList())
        {
            if (carried.Any(entry => SameItem(move.ItemName, entry))) continue;

            _log?.Warn(LogCategory,
                $"{move.ItemName} isn't in inventory despite a recorded pickup from {move.From} "
                + "— dropping the phantom move rather than retrying a drop that can't work");
            _leftInPlace.Add(new GhSweepItemFound(move.From, move.ItemName, GhLeftReason.NotActuallyCarried));
            _pending.Remove(move);
        }
    }

    // "You cannot carry that much!" — abort the outstanding pickups (they stay
    // queued, they're not gone) and mark the ledger for a resync. The resync + the
    // re-plan run once the dispatch fully unwinds (any drops in the same batch
    // resolve first), routing AdvanceAfterDispatch through BeginInventoryResync.
    private void HandleCapacityRefusal(List<PendingSortMove> outstandingGets)
    {
        foreach (PendingSortMove get in outstandingGets)
        {
            KeepWhatWasTaken(get);
            _outstandingDispatch.Remove(get);
        }
        ForgetRefusedPickups();
        // The pickups not yet sent, and a second read queued behind them, are given
        // up with the rest: sent now they would only be refused too, or land after
        // the re-plan has walked on, in another room.
        ClearCommandQueue();
        _resyncPending = true;
        _log?.Info(LogCategory,
            $"capacity refused {outstandingGets.Count} pending pickup(s) at {_dispatchRoom}; "
            + "will resync carry weight and re-plan");
        PhaseChanged?.Invoke();
        if (_outstandingDispatch.Count == 0)
        {
            EndDispatch();
            AdvanceAfterDispatch("capacity refusal");
        }
        else
        {
            Arm(ref _dispatchSettle, DispatchSettleTimeout, OnDispatchSettleElapsed);
        }
    }

    // The copies of a stack the game confirmed taken are in the pack, whatever
    // became of the rest: they go on as a carried move of their own, to be
    // delivered, reported and kept in the resume record, and the move is cut down
    // to what is still to take. Stock takes a stack one `get` a copy, so a stack
    // that ran out part-way, or a pack that filled, leaves one of these.
    private void KeepWhatWasTaken(PendingSortMove move)
    {
        if (move.IsCarried || move.ConfirmedUnits <= 0 || move.ConfirmedUnits >= move.Count) return;

        int taken = move.ConfirmedUnits;
        PendingSortMove inPack = new()
        {
            From = move.From,
            To = move.To,
            ItemName = move.ItemName,
            Count = taken,
            RequiresSearch = move.RequiresSearch,
            IsCarried = true,
        };
        _pending.Insert(_pending.IndexOf(move), inPack);
        move.Count -= taken;
        move.ConfirmedUnits = 0;
        _pickedUpHere.Add(Stack(inPack));
        _log?.Debug(LogCategory,
            $"picked up {taken} of {taken + move.Count} {move.ItemName} at {move.From}, carrying to {move.To}");
    }

    // The room read again for the pickups the game refused in it: one look for every
    // refused stack in plain sight, one search for every refused hidden one.
    private const string LookAgainVerb = "look again";
    private const string SearchAgainVerb = "search again";

    // The refused pickups of the room being worked, waiting on that second read,
    // by which read they wait on; and whether that read is queued, and sent.
    private readonly List<PendingSortMove> _refusedVisible = new();
    private readonly List<PendingSortMove> _refusedHidden = new();
    private bool _lookAgainQueued, _searchAgainQueued;
    private bool _awaitingLook, _awaitingSearch;

    // Hidden pickups taken out of the batch because the visible stack of the same
    // item was refused. The game serves a `get` from the copies in plain sight
    // first, so sent before that stack is settled it would take those, and be
    // counted as part of the refused stack.
    private readonly List<PendingSortMove> _heldForVisibleTwin = new();

    private void ForgetRefusedPickups()
    {
        // A pickup still waiting here never had its second read (a full pack or a
        // timeout ended the room first), so it is owed one on the next visit.
        foreach (PendingSortMove unread in _refusedVisible.Concat(_refusedHidden)) unread.Recounted = false;
        _refusedVisible.Clear();
        _refusedHidden.Clear();
        _heldForVisibleTwin.Clear();
        _lookAgainQueued = _searchAgainQueued = false;
        _awaitingLook = _awaitingSearch = false;
    }

    // `You don't see … here.` for one of our pickups. Paradigm refuses a counted
    // `get` for more than is there outright and takes nothing (GAME_MECHANICS "`get`
    // failure responses"), so the stack may only be smaller than recorded: the room
    // is read once more, after the rest of the batch (its display, or a search for
    // hidden stacks; one read serves every pickup refused here), and each is asked
    // again for what is there. A hidden stack of one gets the same second search.
    // Anything else, or a second refusal, is left where it is, under the reason
    // that fits: a hidden stack the search didn't find is not gone.
    private void OnGetRefusedAsNotHere(PendingSortMove move, string line)
    {
        // Copies a late answer already credited to this stack are in the pack.
        KeepWhatWasTaken(move);
        if (!_isParadigm() || move.Recounted || (move.Count <= 1 && !move.RequiresSearch))
        {
            StrandFailedGet(move, $"game reports it isn't there (\"{line}\")", NotHereReason(move));
            return;
        }

        move.Recounted = true;
        _log?.Debug(LogCategory,
            $"get refused for {move.Count}x {move.ItemName} at {move.From} (\"{line}\") — reading the room again");
        if (move.RequiresSearch)
        {
            _refusedHidden.Add(move);
            if (!_searchAgainQueued) _commandQueue.Enqueue((SearchAgainVerb, move));
            _searchAgainQueued = true;
        }
        else
        {
            HoldHiddenTwinsOf(move);
            _refusedVisible.Add(move);
            if (!_lookAgainQueued) _commandQueue.Enqueue((LookAgainVerb, move));
            _lookAgainQueued = true;
        }
        SendNextQueuedCommand();
    }

    private void HoldHiddenTwinsOf(PendingSortMove visible)
    {
        List<(string Verb, PendingSortMove Move)> queued = _commandQueue.ToList();
        _commandQueue.Clear();
        foreach ((string verb, PendingSortMove move) in queued)
        {
            if (verb == "get" && move.RequiresSearch && SameItem(move.ItemName, visible.ItemName))
                _heldForVisibleTwin.Add(move);
            else
                _commandQueue.Enqueue((verb, move));
        }
    }

    private static GhLeftReason NotHereReason(PendingSortMove move) =>
        move.RequiresSearch ? GhLeftReason.NotFoundBySearch : GhLeftReason.GoneBySortTime;

    // A list read while sorting. Only the one asked for after a refused pickup
    // matters here: the display for the stacks in plain sight, a search's reply for
    // the hidden ones.
    private void RecountFrom(IReadOnlyList<string> list, FloorSurveySource source)
    {
        if (_tracker.State.Confidence != RoomConfidence.Confirmed
            || _dispatchRoom is not { } room
            || _tracker.State.CurrentRoom?.Key.Equals(room) != true) return;

        if (_awaitingLook && source != FloorSurveySource.SearchReply) SettleRefused(hidden: false, list);
        else if (_awaitingSearch && source != FloorSurveySource.RoomDisplay) SettleRefused(hidden: true, list);
    }

    // The second read is in (or the wait for it ran out, with nothing listed: a
    // floor with nothing in plain sight prints no list, and a search that finds
    // nothing answers in other words). Each refused pickup is asked again for what
    // the list shows of it, or left if it shows none; then the hidden pickups held
    // back for a visible stack go out behind it.
    private void SettleRefused(bool hidden, IReadOnlyList<string> list)
    {
        List<PendingSortMove> refused = hidden ? _refusedHidden : _refusedVisible;
        if (hidden) _awaitingSearch = _searchAgainQueued = false;
        else _awaitingLook = _lookAgainQueued = false;

        foreach (PendingSortMove move in refused.ToList())
        {
            // Copies credited to the stack since it was refused are in the pack
            // and off the floor this list is of: only the rest is still to take.
            KeepWhatWasTaken(move);
            int there = list.Where(entry => SameItem(entry, move.ItemName))
                .Sum(entry => CountedCommand.SplitLeadingCount(entry).Count);
            if (there <= 0)
            {
                Strand(move, "a second read of the room doesn't list it", NotHereReason(move));
            }
            else
            {
                if (there < move.Count)
                {
                    _log?.Info(LogCategory,
                        $"{move.From} holds {there} {move.ItemName}, not {move.Count}: picking up {there}");
                    // The copies that aren't there were never the sort's to account
                    // for, and come off the room's record with the ones it takes.
                    if (hidden) _hiddenTaken?.Add(move.From, move.ItemName, move.Count - there);
                    else _expectedAfterSort?.Add(move.From, move.ItemName, there - move.Count);
                    move.Count = there;
                }
                _commandQueue.Enqueue(("get", move));
            }

            if (hidden) continue;
            foreach (PendingSortMove twin in _heldForVisibleTwin
                         .Where(t => SameItem(t.ItemName, move.ItemName)).ToList())
            {
                _heldForVisibleTwin.Remove(twin);
                _commandQueue.Enqueue(("get", twin));
            }
        }
        refused.Clear();
        PhaseChanged?.Invoke();

        if (_commandQueue.Count > 0)
        {
            SendNextQueuedCommand();
        }
        else if (_outstandingDispatch.Count == 0)
        {
            EndDispatch();
            ContinueAfterTransaction("refused pickups settled");
        }
        else
        {
            Arm(ref _dispatchSettle, DispatchSettleTimeout, OnDispatchSettleElapsed);
        }
    }

    // Take a failed get out of the queue (so it stops being retried and no longer
    // blocks completion) and record it under LeftInPlace. Copies of it already
    // taken stay queued as a carried move (KeepWhatWasTaken); only the rest is left.
    private void Strand(PendingSortMove move, string reason, GhLeftReason why)
    {
        KeepWhatWasTaken(move);
        _refusedVisible.Remove(move);
        _refusedHidden.Remove(move);
        _pending.Remove(move);
        _outstandingDispatch.Remove(move);
        _leftInPlace.Add(new GhSweepItemFound(move.From, move.ItemName, why));
        _failedHere.Add($"{Stack(move)} ({reason})");
        _log?.Debug(LogCategory,
            $"get failed for {move.ItemName} at {move.From} — {reason}; not retrying");
    }

    // Strand a failed get and advance the dispatch exactly as a real confirmation would.
    private void StrandFailedGet(PendingSortMove move, string reason,
        GhLeftReason why = GhLeftReason.GoneBySortTime)
    {
        Strand(move, reason, why);
        PhaseChanged?.Invoke();
        if (_outstandingDispatch.Count == 0)
        {
            EndDispatch();
            ContinueAfterTransaction("get failure resolved dispatch");
        }
        else
        {
            Arm(ref _dispatchSettle, DispatchSettleTimeout, OnDispatchSettleElapsed);
        }
    }

    private static string Stack(PendingSortMove move) =>
        move.Count > 1 ? $"{move.Count}x {move.ItemName}" : move.ItemName;

    private void BeginInventoryResync(string reason)
    {
        if (_inventory is null)
        {
            ContinueAfterTransaction(reason);
            return;
        }
        _awaitingInventoryResync = true;
        Arm(ref _inventorySettle, InventoryVerificationTimeout, OnInventoryVerificationTimeout);
        Send("i");
        _log?.Info(LogCategory, $"requesting inventory resync after {reason}");
    }

    private void OnFullInventoryParsed()
    {
        if (!_awaitingInventoryResync || Phase != SweepPhase.Sorting) return;
        Cancel(ref _inventorySettle);
        _awaitingInventoryResync = false;
        if (_verifyCarriedAfterResync)
        {
            _verifyCarriedAfterResync = false;
            DropPhantomCarriedMoves();
        }
        ResyncCarryBaseline();
        // A corrected (smaller) budget can require finer stack splitting and can
        // reveal a single unit is now unmovable.
        SplitOversizedMoves();
        StrandUnmovableItems();
        PhaseChanged?.Invoke();
        ContinueAfterTransaction("inventory resync complete");
    }

    private void OnInventoryVerificationTimeout()
    {
        Cancel(ref _inventorySettle);
        if (!_awaitingInventoryResync) return;
        _awaitingInventoryResync = false;
        _log?.Warn(LogCategory,
            "inventory resync timed out; continuing on the tracked ledger");
        ContinueAfterTransaction("inventory resync timeout");
    }

    // A room transaction just resolved off the tracked ledger (or a resync). Do not
    // blindly release the original one-way circuit — let the trip planner choose the
    // next waypoint: fill the pack at the nearest fitting source, then deliver to the
    // nearest carried destination. LoopRunner still owns every movement command and
    // all its normal gating/recovery behavior; GhSweep only chooses the next room.
    private void ContinueAfterTransaction(string reason)
    {
        if (_pending.All(p => p.Delivered))
        {
            BeginFinalRecon();
            return;
        }

        if (TryRerouteToNextWork())
        {
            ReleaseGate($"{reason}; shortest-work route ready");
            return;
        }

        ReleaseGate(reason);
        MaybeFinish();
    }

    // Sorting is done — every queued move delivered (or stranded). Before finishing,
    // walk the circuit one more time, observe-only, so each room's shown inventory
    // reflects the post-sweep floor rather than the pre-sort recon snapshot. On the
    // lap's completion (RepeatStarted) FinishSweep runs. Guarded to Sorting so it
    // fires exactly once at the sort/finish boundary.
    private void BeginFinalRecon()
    {
        if (Phase != SweepPhase.Sorting) return;
        Phase = SweepPhase.FinalRecon;
        CarryRecordsIntoFinalLap();
        _pendingArrivalSurvey = null;
        _log?.Info(LogCategory, "sort complete; final recon pass to refresh room inventories");
        PhaseChanged?.Invoke();

        bool started;
        _reroutingSortLoop = true; // suppress the supersede Stopped event as we swap loops
        try { started = PlotAndStartCircuit(); }
        finally { _reroutingSortLoop = false; }
        // Only once the circuit has replaced the sort's shuttle, as a reroute does:
        // released first, the shuttle shipped one more step of its own and the lap
        // set off a room out of step with where it thought it was.
        ReleaseGate("final recon begins");
        if (!started)
        {
            _log?.Warn(LogCategory, "final recon couldn't plot a circuit; finishing");
            FinishSweep();
        }
    }

    // The final lap reads each room's display again and does not search. So the
    // visible record starts empty and is rebuilt from what the lap sees, while the
    // hidden record is kept, less the copies the sort took out of hidden stacks: no
    // display will show whether the rest are still there, and recon's count is the
    // last word on them.
    private void CarryRecordsIntoFinalLap()
    {
        if (_hiddenTaken is { } taken)
        {
            foreach (RoomKey room in _hiddenByRoom.Keys.ToList())
            {
                _hiddenByRoom[room] = _hiddenByRoom[room]
                    .Select(entry => (Name: GhSurveyMerger.Canonical(entry, _itemNames),
                        Left: CountedCommand.SplitLeadingCount(entry).Count - taken.Of(room, entry)))
                    .Where(stack => stack.Left > 0)
                    .Select(stack => stack.Left > 1 ? $"{stack.Left} {stack.Name}" : stack.Name)
                    .ToList();
            }
        }
        // A resumed sweep ran no recon, so it has no floor to expect.
        if (CompletedReconLaps == 0) _expectedAfterSort = null;

        _observedByRoom.Clear();
        _visibleByRoom.Clear();
        _readOnFinalLap.Clear();
        _changedAfterSort.Clear();
    }

    // A room read on the final lap: write it to the item-location log, and say
    // once if it isn't as the sort left it. Nothing is re-sorted: the sort is over,
    // and whoever took or dropped an item since may still be at it. A resumed sweep
    // ran no recon, so it has no floor to expect, and where searching is on it has no
    // hidden record either: writing the room from its display alone would drop the
    // hidden stacks an earlier sweep logged there. A room recon never read (the
    // final circuit is plotted afresh and can pass through one) is written as seen
    // and held against nothing.
    private void NoteFinalLapRead(RoomKey room)
    {
        if (CompletedReconLaps > 0 || !_labels.SearchForHidden) RecordItemLocations(room);
        if (!_readOnFinalLap.Add(room) || _expectedAfterSort is null || !_readOnRecon.Contains(room)) return;

        List<(string Item, int Difference)> differences = GhSurveyMerger.Difference(
            _expectedAfterSort.EntriesAt(room),
            _visibleByRoom.GetValueOrDefault(room) ?? new List<string>(),
            _itemNames);
        if (differences.Count == 0) return;

        _changedAfterSort.Add(room);
        List<string> gained = differences.Where(d => d.Difference > 0)
            .Select(d => d.Difference > 1 ? $"{d.Difference} {d.Item}" : d.Item).ToList();
        List<string> lost = differences.Where(d => d.Difference < 0)
            .Select(d => d.Difference < -1 ? $"{-d.Difference} {d.Item}" : d.Item).ToList();
        _log?.Info(LogCategory,
            $"final recon at {room}: not as the sort left it"
            + (gained.Count > 0 ? $"; {gained.Count} more than expected: {NameFew(gained)}" : string.Empty)
            + (lost.Count > 0 ? $"; {lost.Count} missing: {NameFew(lost)}" : string.Empty)
            + (_itemLocations is not null ? "; item log brought up to date" : string.Empty));
    }

    // A room recon read that the final lap never showed: its circuit is plotted
    // afresh from where the sort ended and need not pass through every room recon
    // walked, and a room too dark to show is not read either. Its item-location
    // entry would otherwise stay as recon wrote it, with the items since sorted
    // out of it still listed there. It is written from the sort's own account:
    // what recon saw, less what was taken, plus what was delivered.
    private void WriteRoomsTheFinalLapMissed()
    {
        if (_expectedAfterSort is not { } expected) return;

        List<RoomKey> missed = _readOnRecon.Where(room => !_readOnFinalLap.Contains(room))
            .OrderBy(room => room.Map).ThenBy(room => room.Room).ToList();
        foreach (RoomKey room in missed)
        {
            _visibleByRoom[room] = expected.EntriesAt(room);
            _observedByRoom[room] = GhSurveyMerger.Total(_visibleByRoom, _hiddenByRoom, room, _itemNames);
        }
        // One write for all of them: the file holds every room of the house.
        _itemLocations?.RecordRooms(missed
            .Select(room => (room, (IReadOnlyList<string>)_observedByRoom[room])).ToList());
        if (missed.Count > 0)
            _log?.Info(LogCategory,
                $"final recon: {missed.Count} room(s) not read again; their item-log entries are the "
                + $"sort's own account of them: {NameFew(missed.Select(room => room.ToString()).ToList())}");
    }

    // The first few of a list of stacks, then how many more there are. total is the
    // length of the whole list when only its head was handed over.
    private static string NameFew(IReadOnlyList<string> stacks, int? total = null)
    {
        int count = total ?? stacks.Count;
        return count > StacksNamedPerLogLine
            ? $"{string.Join(", ", stacks.Take(StacksNamedPerLogLine))} (+{count - StacksNamedPerLogLine} more)"
            : string.Join(", ", stacks);
    }

    private bool TryRerouteToNextWork()
    {
        if (Phase != SweepPhase.Sorting || _tracker.State.CurrentRoom is not { } current)
            return false;

        RoomKey here = current.Key;

        // Weight-aware, trip-minimizing pick: keep filling the pack (nearest source
        // whose batch fits the live headroom) before delivering (nearest carried
        // destination). See GhSortPlanner.
        // Carried items whose destination is full aren't routable — walking there
        // only earns another refusal. They stay in the pack (and in the ledger, so
        // the weight model stays honest) and ride out to the suspended-sweep
        // record, which the next sweep re-adopts once a room has space.
        List<GhSortPlanner.CarriedLoad> carried = _pending
            .Where(p => p.IsCarried && !p.Delivered && !_fullRooms.Contains(p.To))
            .GroupBy(p => p.To)
            .Select(g => new GhSortPlanner.CarriedLoad(g.Key, g.Sum(MoveWeight)))
            .ToList();
        // A room is a candidate source as long as its LIGHTEST pending move fits the
        // headroom (not its whole sum) — so a room holding a big split stack, or a
        // mix of heavy and light items, is still visited to grab what does fit.
        // FittingGetsAt then picks up every move at the room that fits on arrival.
        List<GhSortPlanner.PickupRoom> pickups = PickupsOnOffer();

        // Above 80% of the working budget we stop collecting and empty the pack
        // down past 40% before filling again, heaviest stop first. Without the
        // hysteresis a saturated pack alternates deliver-one / collect-one and
        // walks the same long leg twice per item.
        bool wasUnloading = _unloading;
        _unloading = GhSortPlanner.ShouldUnload(_unloading, LedgerCarriedWeight(), WorkingBudget());
        if (_unloading != wasUnloading)
            _log?.Info(LogCategory, _unloading
                ? $"pack at {LedgerCarriedWeight()}/{WorkingBudget()} — delivering until it's back under "
                  + $"{GhSortPlanner.UnloadExitLoad:P0} before collecting again"
                : $"pack down to {LedgerCarriedWeight()}/{WorkingBudget()} — collecting again");

        // A pack with room for barely one item can't sort — it collects one thing,
        // immediately crosses the unload threshold, walks the whole way to deliver
        // it, and walks back for the next. Every item costs a full round trip and
        // the sweep looks hung. That happens when the pack is mostly full of
        // weight Roomba didn't collect (auto-get loot), which no amount of
        // delivering on our side will free. Stop collecting, deliver what we're
        // holding, and end with the reason — the queue is kept, so Resume carries
        // on once there's room.
        int lightestPickup = pickups.Count == 0 ? 0 : pickups.Min(p => p.Weight);
        bool tooTightToCollect = pickups.Count > 0
            && WorkingBudget() != int.MaxValue
            && WorkingBudget() < Math.Max(1, lightestPickup) * 2;
        if (tooTightToCollect)
        {
            if (!_reportedTightPack)
            {
                _reportedTightPack = true;
                _log?.Warn(LogCategory,
                    $"pack has only {WorkingBudget()} of {_maxCarryWeight} usable — the rest is weight Roomba "
                    + $"didn't collect. The lightest thing left to move is {lightestPickup}, so every item would "
                    + "cost its own delivery trip. Delivering what's carried, then stopping; free some space and "
                    + "Resume.");
            }
            pickups = new List<GhSortPlanner.PickupRoom>();
        }

        RoomKey? target = GhSortPlanner.NextTarget(
            carried, pickups, CurrentHeadroom(), _bfs.ComputeDistancesFrom(here), _fullRooms, _unloading);
        if (target is not { } destination) return false;

        var shuttle = new Loop(SweepLoopName, new[] { here, destination });
        bool started;
        _reroutingSortLoop = true;
        try
        {
            started = _loopRunner.Start(shuttle);
        }
        finally
        {
            _reroutingSortLoop = false;
        }

        if (!started)
        {
            _log?.Warn(LogCategory,
                $"shortest-work reroute failed: {here} -> {destination}; retaining current route");
            return Phase != SweepPhase.Sorting; // a synchronous Failed event already ended the sweep
        }

        string targetKind = carried.Any(c => c.Room.Equals(destination)) ? "drop" : "pickup";
        _log?.Info(LogCategory,
            $"shortest-work reroute: {here} -> {destination} ({targetKind}, "
            + $"{_bfs.DistanceBetween(here, destination)} hop(s))");
        return true;
    }

    private bool SameItem(string left, string right)
    {
        int? leftNumber = _itemNames.FindByName(left);
        int? rightNumber = _itemNames.FindByName(right);
        if (leftNumber is not null && rightNumber is not null)
            return leftNumber.Value == rightNumber.Value;
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    // How many of `name` a live 'i' read still shows carried. Carried items are stacked
    // into one "N name" entry per item, so sum the stack count of every matching entry
    // (a lone item reads as a bare name = 1) — 0 means gone, a value below the manifest's
    // count means the stack was reduced. Used to clamp a re-adopted carried move to what
    // the pack actually holds.
    private int HeldCount(string name, IReadOnlyList<string> carried)
    {
        int total = 0;
        foreach (string held in carried)
        {
            (int n, string heldName) = CountedCommand.SplitLeadingCount(held);
            if (SameItem(name, heldName)) total += n;
        }
        return total;
    }

    private void MaybeFinish()
    {
        if (_pending.All(p => p.Delivered)) BeginFinalRecon();
    }

    private void FinishSweep()
    {
        // Normal Sort-mode completion reaches this only after every
        // PendingSortMove is Delivered; InventoryOnly reaches it straight off
        // its one recon lap, having never queued a move. Stop() and an
        // external LoopRunner failure build their own abnormal report,
        // including anything still carried as Stranded.
        GhSweepReport report = BuildFinalReport();
        _log?.Info(LogCategory,
            $"{Mode} complete: moved={_movedSoFar.Count} left-in-place={_leftInPlace.Count}"
            + (_stranded.Count > 0 ? $" stranded={_stranded.Count}" : string.Empty));
        DateTimeOffset started = StartedAt ?? DateTimeOffset.Now;
        DateTimeOffset finished = DateTimeOffset.Now;
        string timespan = $"started {started:yyyy-MM-dd HH:mm} {TimeZoneAbbreviation.For(started)}, "
            + $"finished {finished:yyyy-MM-dd HH:mm} {TimeZoneAbbreviation.For(finished)}";
        // _observedByRoom's raw floor tokens can carry a leading stack count
        // ("35 orc-head") the same way a `get`/`drop` command line does, so the
        // unit total needs the same split — a raw list length would undercount
        // every stacked item to 1. Sort mode's recon + final-recon already
        // populate _observedByRoom the same way InventoryOnly's single recon
        // does (see OnSurveyUpdated), so a Sort run is inherently also a full
        // inventory pass — this reports that instead of only the moved count.
        int inventoried = UnitCount(_observedByRoom.Values.SelectMany(items => items));
        if (Mode == SweepMode.InventoryOnly)
        {
            Send($"bg Roomba inventory complete - inventoried {inventoried} item(s). {timespan}.");
        }
        else
        {
            // report.Moved is one entry per relocation, not per unit —
            // SplitOversizedMoves can split one big stack across several
            // trips, so the item total is the sum of each move's Count.
            int sorted = report.Moved.Sum(m => m.Count);
            Send($"bg Roomba sorting complete - sorted {sorted} item(s), inventoried {inventoried} item(s). {timespan}.");
        }
        ResetToIdle(Mode == SweepMode.InventoryOnly ? "roomba inventory scan complete" : "roomba sweep complete");
        SweepCompleted?.Invoke(report);
    }

    private void ResetToIdle(string? stopSweepLoopReason = null)
    {
        Cancel(ref _reconSearchSettle);
        // A sweep stopped part-way through a room's searches still writes what it
        // read of that room.
        if (_reconSearchRoom is { } searching) RecordItemLocations(searching);
        _reconSearchRoom = null;
        _sortSearchRoom = null;
        Cancel(ref _dispatchSettle);
        _dispatchRoom = null;
        _outstandingDispatch.Clear();
        Cancel(ref _inventorySettle);
        _awaitingInventoryResync = false;
        _resyncPending = false;
        _pendingArrivalSurvey = null;
        // Mark idle before stopping our loop so its synchronous Stopped event
        // cannot be mistaken for an external failure. Stop while GhSortGate is
        // still asserted; clearing the gate first would let a paused LoopRunner
        // ship one extra step before Stop reached it.
        Phase = SweepPhase.Idle;
        if (stopSweepLoopReason is not null
            && _loopRunner.CurrentLoop?.Name == SweepLoopName)
            _loopRunner.Stop(stopSweepLoopReason);
        ReleaseGate("phase reset");
        PhaseChanged?.Invoke();
    }

    private void AssertGate(string reason)
    {
        if (_gateAsserted) return;
        _gateAsserted = true;
        _coordinator.AssertGate(MovementCoordinator.GhSortGate, LogCategory, reason);
    }

    private void ReleaseGate(string reason)
    {
        if (!_gateAsserted) return;
        _gateAsserted = false;
        _coordinator.ClearGate(MovementCoordinator.GhSortGate, LogCategory, reason);
    }

    private void Send(string text)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(text + "\r"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel(ref _reconSearchSettle);
        Cancel(ref _dispatchSettle);
        Cancel(ref _inventorySettle);
        _getSub.Dispose();
        _dropSub.Dispose();
        _dropRefusedSub.Dispose();
        _commandIgnoredSub.Dispose();
        _slowDownSub.Dispose();
        if (_promptScanner is not null) _promptScanner.PromptObserved -= OnPromptObservedFromWire;
        Cancel(ref _promptWait);
        Cancel(ref _rateLimitBackoff);
        _router.LineDispatched -= OnLineForGetFailure;
        _loopRunner.Event -= OnLoopEvent;
        _groundItems.SurveyUpdated -= OnSurveyUpdated;
        if (_inventory is not null)
            _inventory.FullInventoryParsed -= OnFullInventoryParsed;
        ReleaseGate("disposed");
    }
}
