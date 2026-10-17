using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Cash;

// Carries a stash room's coin to a bank: walk to the stash, search, take what the
// cash settings let us carry, walk to the bank, deposit it, and go back for more
// until the search shows nothing left. It ends in the bank after the last deposit.
//
// A stash is a belief until it is searched (StashLedger): anyone can have drawn on
// it, and a pile is bigger than one trip can carry. So every trip searches again
// and reads the pile before taking anything, and the ledger is set to what the
// search showed less what was taken (GAME_MECHANICS "Hiding coin in a room
// (stashing)": the untaken part of a searched pile stays hidden).
//
// Each deposit is everything carried above the Settings → Cash keep-on-hand
// amount, the same rule every other deposit follows: the stash's coin, whatever
// was picked up off the ground between the two rooms, and what was already in the
// purse (report paradigm-20261002-111650: keeping the purse as it stood at the
// start left earlier pickups unbanked trip after trip).
//
// A purse that is already loaded goes to the bank first: started with more coin
// than there is room left for (a transfer cut off on its way to the bank and
// started again, report paradigm-20261002-114620), or found unable to take
// anything at the stash while it still holds coin to bank.
//
// A party leader can have the members carry too (Settings → Cash): once the
// leader has taken its own load, each member is telepathed `@get-stash` — search
// and take coin up to your own weight limits (GetStashHandler) — and
// `@deposit-all` at the bank. A search shows hidden coin only to the searcher, so
// the members can't be handed what the leader saw. Each member's reply says it is
// done, and once all have answered (or the wait runs out) the run moves on; what
// they took isn't read from the reply — a second search counts what is left.
//
// Walker, wire and collect engine are reached through delegates, like
// TrainFundingRouter.
public sealed class StashTransferRunner
{
    public const string LogCategory = "StashTransfer";

    // How long a `sea` gets to answer before the pile is read.
    public TimeSpan SurveyWindow { get; set; } = TimeSpan.FromSeconds(1.5);

    // How long the `get`s get to land before the trip counts what it took.
    public TimeSpan CollectWindow { get; set; } = TimeSpan.FromSeconds(3);

    // How long a `dep` gets to echo.
    public TimeSpan DepositWindow { get; set; } = TimeSpan.FromSeconds(4);

    // The longest the members get to answer a telepathed order. Normally every reply
    // arrives well inside it and the run moves on at the last one; a member who never
    // answers (an older client, failure replies switched off) costs the full wait.
    public TimeSpan PartyWindow { get; set; } = TimeSpan.FromSeconds(12);

    private enum Phase
    {
        Idle, WalkingToStash, Surveying, Collecting,
        // Party share: the members are taking theirs, then the pile is searched
        // again to count what is left.
        PartyTaking, Recounting,
        WalkingToBank, Depositing,
        // The members are depositing.
        PartyDepositing,
    }

    private readonly Func<RoomKey?> _currentRoom;
    private readonly Func<long> _onHandCopper;
    private readonly Func<RoomKey, bool> _walkTo;
    private readonly Action<string> _send;
    private readonly Action<TimeSpan, Action> _armTimer;
    private readonly Action<long?> _limitCollection;
    private readonly Func<long> _surveyedCopper;
    private readonly Action<long> _collectSurveyed;
    private readonly Action<bool> _forceAutoGetCash;
    private readonly Action<RoomKey, long> _reconcileStash;
    private readonly Action<string> _notice;
    private readonly Func<IReadOnlyList<string>>? _partyMembers;
    private readonly Func<long>? _keepOnHandCopper;
    private readonly Func<(long Held, long Room)?>? _coinLoad;
    private readonly LogService? _log;

    // Reads behind Progress (the Navigation chip's tooltip). All optional: without
    // them the tooltip gives the copper figures and leaves out what it can't know.
    private readonly Func<IReadOnlyDictionary<CoinDenomination, long>>? _surveyedCoins;
    private readonly Func<CurrencyHoldings>? _purse;
    private readonly Func<RoomKey, RoomKey, TimeSpan?>? _walkTime;
    private readonly Func<RoomKey, long>? _believedCopper;
    private readonly Func<DateTimeOffset> _now;

    // Told, while we stand in the stash room, what a search showed gone from the
    // pile and what it showed left, by coin — the stash's row in the transaction
    // history is kept to it. Only called when the coin was read by type.
    private readonly Action<IReadOnlyDictionary<CoinDenomination, long>, IReadOnlyDictionary<CoinDenomination, long>>? _noteStashSeen;

    // The pile by coin as the last search showed it, and the purse just before the
    // take, so what is left can be told by coin too.
    private IReadOnlyDictionary<CoinDenomination, long> _shownCoins = NoCoins;
    private CurrencyHoldings _purseCoinsBefore;
    private IReadOnlyDictionary<CoinDenomination, long> _leftCoins = NoCoins;
    private static readonly IReadOnlyDictionary<CoinDenomination, long> NoCoins =
        new Dictionary<CoinDenomination, long>();
    private int? _tripsToGo;
    // When the last load was taken, and how long the trip before it ran from its own
    // take to that one: a full stash-bank-stash round.
    private DateTimeOffset? _lastTakeAt;
    private TimeSpan? _lastRound;
    private bool _legTimed;
    private TimeSpan? _legTime;

    // Every change of stage is a change in Progress.
    private Phase _stage = Phase.Idle;
    private Phase Stage
    {
        get => _stage;
        set
        {
            if (_stage == value) return;
            _stage = value;
            ProgressChanged?.Invoke();
        }
    }
    private int _session;
    private long _purseBefore;
    private long _shown;
    private bool _forcedAutoGetCash;
    // The stash has been searched at least once this transfer, so LeftCopper is real.
    private bool _pileRead;

    // The members sent to the pile on this trip; the same ones are told to deposit.
    private IReadOnlyList<string> _partyOnTrip = Array.Empty<string>();
    // The members whose reply to the current order is still owed, and the wait they
    // belong to (a wait ended early leaves its timer behind).
    private readonly HashSet<string> _awaitingReply = new(StringComparer.OrdinalIgnoreCase);
    private int _partyWait;

    // Set while this runner is itself starting a walk: WalkTo raises Stopped when it
    // supersedes one in flight, and that is ours, not a user stop.
    private bool _drivingWalker;

    public RoomKey Stash { get; private set; }
    public RoomKey Bank { get; private set; }
    public string BankName { get; private set; } = string.Empty;
    public int Trips { get; private set; }
    public long MovedCopper { get; private set; }

    // What the last search showed still hidden once this trip's coin was taken.
    public long LeftCopper { get; private set; }

    public bool IsBusy => Stage != Phase.Idle;

    // Raised when a transfer starts or ends.
    public event Action? StateChanged;

    // Raised once a started transfer is over, after StateChanged.
    public event Action<StashTransferOutcome>? Ended;

    // Raised when a figure in Progress changes mid-transfer (a search read, a load
    // taken, a deposit made).
    public event Action? ProgressChanged;

    // Where the transfer stands; null when none is running.
    public StashTransferProgress? Progress
    {
        get
        {
            if (!IsBusy) return null;
            long left = _pileRead ? LeftCopper : Math.Max(0, _believedCopper?.Invoke(Stash) ?? 0);
            return new StashTransferProgress(Stash, BankName, Doing(), _pileRead, left, _leftCoins,
                Trips, MovedCopper, _tripsToGo, Eta());
        }
    }

    private string Doing() => Stage switch
    {
        Phase.WalkingToStash => "walking to the stash",
        Phase.Surveying or Phase.Recounting => "searching the stash",
        Phase.Collecting => "taking coin",
        Phase.PartyTaking => "the party is taking coin",
        Phase.WalkingToBank => $"walking to {BankName}",
        Phase.Depositing => "depositing",
        Phase.PartyDepositing => "the party is depositing",
        _ => "starting",
    };

    // The trips still to make at the pace of a full round, plus the leg left to
    // walk with the load in hand. Null until both a trip count and a pace are known.
    private TimeSpan? Eta()
    {
        if (_tripsToGo is not { } trips) return null;
        // Worked out once per transfer: it is a route search.
        if (!_legTimed)
        {
            _legTimed = true;
            _legTime = _walkTime?.Invoke(Stash, Bank);
        }
        TimeSpan? leg = _legTime;
        TimeSpan? round = _lastRound
            ?? (leg is { } l ? l + l + SurveyWindow + CollectWindow + DepositWindow : null);
        if (round is not { } r) return null;
        bool loadInHand = Stage is Phase.Collecting or Phase.PartyTaking or Phase.Recounting or Phase.WalkingToBank;
        return trips * r + (loadInHand ? leg ?? r / 2 : TimeSpan.Zero);
    }

    // A load has left the pile (ours, and the party's when it shared): what is left
    // by coin, how many more loads like it the rest takes, and how long a round ran.
    private void NoteLoadTaken(IReadOnlyDictionary<CoinDenomination, long> leftCoins)
    {
        _leftCoins = leftCoins;
        long shown = _shownCoins.Count > 0 && leftCoins.Count > 0 ? _shownCoins.Values.Sum() : _shown;
        long left = _shownCoins.Count > 0 && leftCoins.Count > 0 ? leftCoins.Values.Sum() : LeftCopper;
        long removed = shown - left;
        _tripsToGo = LeftCopper <= 0 ? 0
            : removed > 0 ? (int)Math.Ceiling(left / (double)removed)
            : null;
        // Known by coin: the pile was read by type, and what is left was too (an
        // emptied pile leaves nothing to read).
        if (_shownCoins.Count > 0 && (leftCoins.Count > 0 || LeftCopper <= 0))
        {
            Dictionary<CoinDenomination, long> took = new();
            foreach ((CoinDenomination coin, long shownOf) in _shownCoins)
                if (shownOf - leftCoins.GetValueOrDefault(coin) is > 0 and var gone) took[coin] = gone;
            _noteStashSeen?.Invoke(took, leftCoins);
        }
        ProgressChanged?.Invoke();
    }

    public StashTransferRunner(
        Func<RoomKey?> currentRoom,
        Func<long> onHandCopper,
        Func<RoomKey, bool> walkTo,
        Action<string> send,
        Action<TimeSpan, Action> armTimer,
        // The collect engine's errand hooks: hold pickup at a copper ceiling (zero
        // takes nothing, null lifts it), read the value the room surveys have shown
        // since, and take up to a value of that surveyed coin.
        Action<long?> limitCollection,
        Func<long> surveyedCopper,
        Action<long> collectSurveyed,
        // Coin pickup has to be on for a survey to be read at all; borrowed for the
        // stash stop only and put back.
        Action<bool> forceAutoGetCash,
        Action<RoomKey, long> reconcileStash,
        Action<string> notice,
        LogService? log = null,
        // The party members to share the carrying with (given names): empty unless
        // the option is on and we lead a party.
        Func<IReadOnlyList<string>>? partyMembers = null,
        // The keep-on-hand floor in copper (Settings → Cash); a deposit leaves this
        // much in the purse. Unwired, everything is deposited.
        Func<long>? keepOnHandCopper = null,
        // Coins carried and the room the weight limits leave for more
        // (CashManager.CoinLoad); null when the capacity isn't known.
        Func<(long Held, long Room)?>? coinLoad = null,
        // For the progress tooltip: the pile by coin as the surveys showed it, the
        // purse by coin, the walk time between two rooms, the ledger's belief about
        // a stash, and the clock.
        Func<IReadOnlyDictionary<CoinDenomination, long>>? surveyedCoins = null,
        Func<CurrencyHoldings>? purse = null,
        Func<RoomKey, RoomKey, TimeSpan?>? walkTime = null,
        Func<RoomKey, long>? believedCopper = null,
        Func<DateTimeOffset>? now = null,
        Action<IReadOnlyDictionary<CoinDenomination, long>, IReadOnlyDictionary<CoinDenomination, long>>? noteStashSeen = null)
    {
        _currentRoom = currentRoom ?? throw new ArgumentNullException(nameof(currentRoom));
        _onHandCopper = onHandCopper ?? throw new ArgumentNullException(nameof(onHandCopper));
        _walkTo = walkTo ?? throw new ArgumentNullException(nameof(walkTo));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _armTimer = armTimer ?? throw new ArgumentNullException(nameof(armTimer));
        _limitCollection = limitCollection ?? throw new ArgumentNullException(nameof(limitCollection));
        _surveyedCopper = surveyedCopper ?? throw new ArgumentNullException(nameof(surveyedCopper));
        _collectSurveyed = collectSurveyed ?? throw new ArgumentNullException(nameof(collectSurveyed));
        _forceAutoGetCash = forceAutoGetCash ?? throw new ArgumentNullException(nameof(forceAutoGetCash));
        _reconcileStash = reconcileStash ?? throw new ArgumentNullException(nameof(reconcileStash));
        _notice = notice ?? throw new ArgumentNullException(nameof(notice));
        _log = log;
        _partyMembers = partyMembers;
        _keepOnHandCopper = keepOnHandCopper;
        _coinLoad = coinLoad;
        _surveyedCoins = surveyedCoins;
        _purse = purse;
        _walkTime = walkTime;
        _believedCopper = believedCopper;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _noteStashSeen = noteStashSeen;
    }

    // Starts the transfer. Null when it is under way; otherwise why it isn't.
    public string? Start(RoomKey stash, RoomKey bank, string bankName)
    {
        if (IsBusy) return "a stash transfer is already running";
        if (_currentRoom() is null) return "your current room is unknown";

        Stash = stash;
        Bank = bank;
        BankName = bankName;
        Trips = 0;
        MovedCopper = 0;
        LeftCopper = 0;
        _pileRead = false;
        _shownCoins = NoCoins;
        _leftCoins = NoCoins;
        _tripsToGo = null;
        _lastTakeAt = null;
        _lastRound = null;
        _legTimed = false;
        _legTime = null;
        _partyOnTrip = Array.Empty<string>();
        _session++;

        _log?.Info(LogCategory, $"transfer started: stash {stash} → {bankName} ({bank})");
        string? refused = LoadedAlready() ? BankWhatWeCarryFirst() : GoToStash();
        if (refused is not null)
        {
            _log?.Info(LogCategory, $"transfer not started — {refused}");
            return refused;
        }
        _notice($"[Stash Transfer Started: {stash.Map}/{stash.Room} -> {bankName}]");
        StateChanged?.Invoke();
        return null;
    }

    // Stopped from outside: the user's Stop, Reset States, a disconnect.
    public void Cancel(string reason)
    {
        if (!IsBusy) return;
        End($"stopped ({reason})", StashTransferOutcome.Stopped);
    }

    // Wired to the walker's event stream by the owner.
    public void OnWalkEvent(WalkEventKind kind)
    {
        if (Stage is not (Phase.WalkingToStash or Phase.WalkingToBank)) return;

        switch (kind)
        {
            case WalkEventKind.Stopped:
                if (_drivingWalker) return;
                End("stopped (movement stopped)", StashTransferOutcome.Stopped);
                break;
            case WalkEventKind.Failed:
                End(Stage == Phase.WalkingToStash
                    ? "the walk to the stash failed"
                    : $"the walk to {BankName} failed");
                break;
            case WalkEventKind.Finished:
                if (Stage == Phase.WalkingToStash) BeginSurvey();
                else BeginDeposit();
                break;
        }
    }

    // More coin carried than there is room left for, and some of it above the
    // keep-on-hand amount: walking to the stash would bring back little or nothing.
    private bool LoadedAlready() =>
        _onHandCopper() - KeepOnHand() > 0
        && _coinLoad?.Invoke() is { } load && load.Held > load.Room;

    // Null when the walk to the bank is under way (or the deposit, when we stand in
    // it). Counts as a trip: it ends in a deposit like any other.
    private string? BankWhatWeCarryFirst()
    {
        _log?.Info(LogCategory, "the purse is already loaded — banking it before going to the stash");
        Trips++;
        if (_currentRoom() is { } here && here.Equals(Bank))
        {
            BeginDeposit();
            return null;
        }
        if (!Walk(Bank)) return $"no route to {BankName}";
        Stage = Phase.WalkingToBank;
        return null;
    }

    // Null when the leg is under way (or the survey, when we already stand there).
    // Coin on the ground along the way is picked up as usual (report
    // paradigm-20261002-101410: it was held off, so an emptied purse walked past
    // every drop); the next deposit banks it with the stash's.
    private string? GoToStash()
    {
        if (_currentRoom() is { } here && here.Equals(Stash))
        {
            BeginSurvey();
            return null;
        }
        if (!Walk(Stash)) return $"no route to the stash room {Stash.Map}/{Stash.Room}";
        Stage = Phase.WalkingToStash;
        return null;
    }

    private bool Walk(RoomKey target)
    {
        _drivingWalker = true;
        try { return _walkTo(target); }
        finally { _drivingWalker = false; }
    }

    private void BeginSurvey()
    {
        // Search surfaces our hidden coin, and is the only way to learn what the pile
        // holds now. Held at zero, the collect engine reads the survey and takes
        // nothing; setting the ceiling also forgets coin seen on the way here.
        ForceAutoGetCash();
        Search(Phase.Surveying, OnSurveyed);
    }

    private void Search(Phase phase, Action<int> then)
    {
        _limitCollection(0);
        Stage = phase;
        int session = _session;
        _send("sea");
        _armTimer(SurveyWindow, () => then(session));
    }

    private void OnSurveyed(int session)
    {
        if (session != _session || Stage != Phase.Surveying) return;

        _shown = Math.Max(0, _surveyedCopper());
        _shownCoins = _surveyedCoins?.Invoke() is { Count: > 0 } pile
            ? new Dictionary<CoinDenomination, long>(pile) : NoCoins;
        _pileRead = true;
        if (_shown <= 0)
        {
            _reconcileStash(Stash, 0);
            _noteStashSeen?.Invoke(NoCoins, NoCoins);
            LeftCopper = 0;
            _leftCoins = NoCoins;
            // Nothing here, but coin in the purse: the last load of a transfer that
            // was cut off, or what was picked up on the way. Bank it and end there
            // rather than stop at the stash holding it (report
            // paradigm-20261004-224242).
            if (_onHandCopper() - KeepOnHand() > 0)
            {
                _log?.Info(LogCategory, "the stash is empty — banking what is carried and ending at the bank");
                Trips++;
                _tripsToGo = 0;
                GoToBank();
                return;
            }
            End(Trips == 0 ? "the stash is empty, nothing to transfer" : "the stash is empty", StashTransferOutcome.Done);
            return;
        }

        _purseBefore = _onHandCopper();
        _purseCoinsBefore = _purse?.Invoke() ?? default;
        // Until the take is counted the pile stands as searched.
        LeftCopper = _shown;
        _leftCoins = _shownCoins;
        Stage = Phase.Collecting;
        ProgressChanged?.Invoke();
        // The weight limits in Settings → Cash decide how much of the pile this trip
        // carries; the collect engine applies them.
        _collectSurveyed(_shown);
        _armTimer(CollectWindow, () => OnCollected(session));
    }

    private void OnCollected(int session)
    {
        if (session != _session || Stage != Phase.Collecting) return;

        long taken = Math.Max(0, _onHandCopper() - _purseBefore);
        LeftCopper = Math.Max(0, _shown - taken);
        _reconcileStash(Stash, LeftCopper);

        if (taken <= 0)
        {
            if (_onHandCopper() - KeepOnHand() > 0)
            {
                // No room, but coin to bank: empty the purse and come back. A second
                // empty-handed stop, with nothing left to bank, ends the transfer.
                _log?.Info(LogCategory, "nothing could be picked up with coin still carried — banking it first");
                Trips++;
                GoToBank();
                return;
            }
            End("nothing could be picked up - check the coin weight limits in Settings, Cash");
            return;
        }

        Trips++;
        _log?.Info(LogCategory, $"trip {Trips}: took {taken:N0} copper, {LeftCopper:N0} left hidden");
        DateTimeOffset tookAt = _now();
        if (_lastTakeAt is { } before) _lastRound = tookAt - before;
        _lastTakeAt = tookAt;
        NoteLoadTaken(LeftAfterOurTake());

        _partyOnTrip = Array.Empty<string>();
        if (LeftCopper > 0 && SendPartyToThePile())
        {
            AwaitParty(Phase.PartyTaking);
            return;
        }
        GoToBank();
    }

    // The searched pile less what our purse gained, coin by coin. A coin the purse
    // lost (dropped to make room for a dearer one) isn't added back: it lies on the
    // floor in the open, not in the hidden pile. Empty when the coin wasn't read.
    private IReadOnlyDictionary<CoinDenomination, long> LeftAfterOurTake()
    {
        if (_shownCoins.Count == 0 || _purse?.Invoke() is not { } now) return NoCoins;
        CurrencyHoldings was = _purseCoinsBefore;
        Dictionary<CoinDenomination, long> left = new();
        void Leave(CoinDenomination coin, int had, int has)
        {
            if (!_shownCoins.TryGetValue(coin, out long shown)) return;
            long rest = shown - Math.Max(0, has - had);
            if (rest > 0) left[coin] = rest;
        }
        Leave(CoinDenomination.Copper, was.Copper, now.Copper);
        Leave(CoinDenomination.Silver, was.Silver, now.Silver);
        Leave(CoinDenomination.Gold, was.Gold, now.Gold);
        Leave(CoinDenomination.Platinum, was.Platinum, now.Platinum);
        Leave(CoinDenomination.Runic, was.Runic, now.Runic);
        return left;
    }

    // Tells each member to search and load up. False when there is no one to tell.
    private bool SendPartyToThePile()
    {
        IReadOnlyList<string> members = _partyMembers?.Invoke() ?? Array.Empty<string>();
        if (members.Count == 0) return false;

        foreach (string member in members) _send($"/{member} @get-stash");
        _partyOnTrip = members;
        _log?.Info(LogCategory, $"trip {Trips}: sent @get-stash to {string.Join(", ", members)}");
        return true;
    }

    // Holds in a party phase until every member told has replied, or PartyWindow.
    private void AwaitParty(Phase phase)
    {
        Stage = phase;
        _awaitingReply.Clear();
        foreach (string member in _partyOnTrip) _awaitingReply.Add(member);
        int wait = ++_partyWait;
        int session = _session;
        _armTimer(PartyWindow, () =>
        {
            if (session != _session || wait != _partyWait) return;
            if (_awaitingReply.Count > 0)
                _log?.Info(LogCategory, $"no reply from {string.Join(", ", _awaitingReply)} — moving on");
            PartyAnswered();
        });
    }

    // The answers GetStashHandler and InventoryActionHandler.DepositAll give. Other
    // replies from the same member (a @health probe's, say) don't end the wait.
    private static readonly string[] TakingReplies = { "ok", "busy" };
    private static readonly string[] DepositReplies =
        { "depositing", "withdrawing", "already at keep-on-hand", "wealth unknown", "not in a bank" };

    // A {reply} came in by telepath. From a member we are waiting on, the answer to
    // the order means that member has finished it (taken its load, or sent its
    // deposit).
    public void NoteMemberReply(string sender, string reply)
    {
        string[] answers;
        if (Stage == Phase.PartyTaking) answers = TakingReplies;
        else if (Stage == Phase.PartyDepositing) answers = DepositReplies;
        else return;

        string text = reply.Trim().TrimStart('{').TrimStart();
        if (!answers.Any(a => text.StartsWith(a, StringComparison.OrdinalIgnoreCase))) return;

        string trimmed = sender.Trim();
        int space = trimmed.IndexOf(' ');
        if (!_awaitingReply.Remove(space >= 0 ? trimmed[..space] : trimmed)) return;
        if (_awaitingReply.Count == 0) PartyAnswered();
    }

    private void PartyAnswered()
    {
        _partyWait++;
        _awaitingReply.Clear();
        // What the members managed isn't taken from their replies: the pile is
        // searched again and counted.
        if (Stage == Phase.PartyTaking) Search(Phase.Recounting, OnRecounted);
        else if (Stage == Phase.PartyDepositing) NextTripOrDone();
    }

    private void OnRecounted(int session)
    {
        if (session != _session || Stage != Phase.Recounting) return;

        LeftCopper = Math.Max(0, _surveyedCopper());
        _reconcileStash(Stash, LeftCopper);
        _log?.Info(LogCategory, $"trip {Trips}: {LeftCopper:N0} copper left hidden after the party's share");
        NoteLoadTaken(_surveyedCoins?.Invoke() is { } pile
            ? new Dictionary<CoinDenomination, long>(pile) : NoCoins);
        GoToBank();
    }

    private void GoToBank()
    {
        ReleaseCollection();
        if (_currentRoom() is { } here && here.Equals(Bank))
        {
            BeginDeposit();
            return;
        }
        if (!Walk(Bank))
        {
            End($"no route to {BankName}");
            return;
        }
        Stage = Phase.WalkingToBank;
    }

    private void BeginDeposit()
    {
        _purseBefore = _onHandCopper();
        long spare = _purseBefore - KeepOnHand();
        Stage = Phase.Depositing;
        int session = _session;
        if (spare <= 0)
        {
            // The purse was under the keep-on-hand amount and this trip's coin only
            // topped it up; nothing to bank.
            _log?.Info(LogCategory, $"trip {Trips}: nothing above the keep-on-hand amount to deposit");
            AfterOwnDeposit();
            return;
        }
        _send($"dep {spare}");
        _armTimer(DepositWindow, () => OnDeposited(session));
    }

    private void OnDeposited(int session)
    {
        if (session != _session || Stage != Phase.Depositing) return;

        long deposited = Math.Max(0, _purseBefore - _onHandCopper());
        if (deposited <= 0)
        {
            // Going back for more would only pile up coin the bank isn't taking.
            End($"{BankName} took no deposit");
            return;
        }
        MovedCopper += deposited;
        _log?.Info(LogCategory, $"trip {Trips}: deposited {deposited:N0} copper at {BankName}");
        ProgressChanged?.Invoke();
        AfterOwnDeposit();
    }

    private void AfterOwnDeposit()
    {
        if (_partyOnTrip.Count == 0)
        {
            NextTripOrDone();
            return;
        }
        foreach (string member in _partyOnTrip) _send($"/{member} @deposit-all");
        AwaitParty(Phase.PartyDepositing);
    }

    private void NextTripOrDone()
    {
        // A deposit made before the pile was ever read says nothing about the stash.
        if (_pileRead && LeftCopper <= 0)
        {
            End(null);
            return;
        }
        Stage = Phase.Idle;
        if (GoToStash() is { } refused) End(refused);
    }

    // why: null when the stash was emptied into the bank. The notice goes to the
    // terminal, whose CP437 font has no arrows or long dashes, and follows the
    // client's bracketed-notice form.
    private void End(string? why, StashTransferOutcome outcome = StashTransferOutcome.Failed)
    {
        if (why is null) outcome = StashTransferOutcome.Done;
        Stage = Phase.Idle;
        _session++;
        _awaitingReply.Clear();
        ReleaseCollection();

        long carrying = Math.Max(0, _onHandCopper() - KeepOnHand());
        string moved = MovedCopper <= 0
            ? "nothing moved"
            : $"{CurrencyFormat.Full(MovedCopper)} moved to {BankName} in {Trips} trip{(Trips == 1 ? "" : "s")}";
        string text = why is null
            ? $"[Stash Transfer Done: {moved}]"
            : $"[Stash Transfer Ended: {why}; {moved}"
              + (carrying > 0 ? $"; carrying {CurrencyFormat.Full(carrying)} not yet deposited" : "")
              + (LeftCopper > 0 ? $"; {CurrencyFormat.Full(LeftCopper)} still stashed" : "")
              + "]";
        _log?.Info(LogCategory, text);
        _notice(text);
        StateChanged?.Invoke();
        Ended?.Invoke(outcome);
    }

    private long KeepOnHand() => Math.Max(0, _keepOnHandCopper?.Invoke() ?? 0);

    private void ForceAutoGetCash()
    {
        if (_forcedAutoGetCash) return;
        _forcedAutoGetCash = true;
        _forceAutoGetCash(true);
    }

    // Runs on every exit from the stash stop and from the transfer, so the pickup
    // ceiling and the borrowed toggle never outlive it.
    private void ReleaseCollection()
    {
        _limitCollection(null);
        if (!_forcedAutoGetCash) return;
        _forcedAutoGetCash = false;
        _forceAutoGetCash(false);
    }

    // One line for the bug report.
    public string Describe() => IsBusy
        ? $"{Stage} — stash {Stash} → {BankName} ({Bank}), trip {Trips}, moved {MovedCopper:N0} copper, "
          + $"{LeftCopper:N0} believed left, keeping {KeepOnHand():N0} on hand"
          + (_partyOnTrip.Count > 0 ? $", party on this trip: {string.Join(", ", _partyOnTrip)}" : "")
          + (_awaitingReply.Count > 0 ? $", awaiting a reply from: {string.Join(", ", _awaitingReply)}" : "")
          + (_tripsToGo is { } togo ? $", {togo} trip(s) to go" : ", trips to go unknown")
          + (Eta() is { } eta ? $", ETA {RouteEtaEstimator.FormatCompact(eta)}" : "")
          + (_lastRound is { } round ? $", last round {RouteEtaEstimator.FormatCompact(round)}" : "")
          + (_leftCoins.Count > 0
              ? $", left by coin: {string.Join(" ", _leftCoins.OrderByDescending(c => c.Key).Select(c => $"{c.Value:N0} {c.Key}"))}"
              : "")
        : "idle";
}
