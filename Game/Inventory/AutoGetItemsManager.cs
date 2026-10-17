using System.Text;
using Avalonia.Threading;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Inventory;

// Auto-get items engine. Parses the room "You notice <list> here." survey
// line, resolves each entry against game data, and sends get <item name> for
// any item flagged ItemOverlay.AutoCollect.
//
// There is no "get all" verb — each item is collected by name (Paradigm takes
// a counted `get N <item>` for a pile; Stock one `get` per copy). Every entry
// the survey line names is run through the injected resolver, which maps the
// loose room wording back to an item Number and reads its per-character
// AutoCollect override. Non-items (cash entries, scenery) and items not
// flagged for collection are skipped.
//
// Collect-after-combat: when CashSettings.CollectAfterCombatFinished is set
// (the shared Cash + Items timing toggle) and the room still holds engageable
// hostiles, the gets are queued and flushed on OnRoomObserved once combat
// clears (no engageable hostiles remain). When the toggle is off, or no
// hostiles are present, the gets fire immediately. A room change (OnRoomChanged)
// discards any un-flushed queue — those items belong to a room we've left.
//
// Movement gate: collecting (or deferring) items asserts the shared
// AcquisitionGate so the walker holds until get-clear — deferred items hold the
// gate before CombatStateTracker clears the Combat gate, defeating the
// synchronous walker-resume race; immediate gets hold it until the `You took`
// confirmations land (settle window as fallback). Bound via SetAcquisitionGate
// (optional — unbound, the engine doesn't gate movement).
//
// Master switch: AutoActionDefaults.AutoGetItems (shared with the Settings →
// General toggle and the toolbar Toggle command).
public sealed class AutoGetItemsManager : IDisposable
{
    // LogService category — [AutoGet] rows per collected / deferred item.
    public const string LogCategory = "AutoGet";

    // One resolved room entry: the item's canonical Number (for the held-count
    // lookup), the name to send to the game, whether the user flagged it for
    // auto-collection, whether it is marked CannotBeTaken (a hard never-pick-up
    // flag that wins over AutoCollect — the engine never sends get for it), the
    // MaxToGet carry cap (int.MaxValue = unbounded), the item's carry Weight
    // (MDB Encum; 0 when unknown — such items skip the encumbrance gate), and
    // whether Auto-sell would sell it (coin may be dropped to fit one of those in).
    public sealed record ResolvedItem(int Number, string Name, bool AutoCollect,
                                      bool CannotBeTaken, int MaxToGet, int Weight,
                                      bool AutoSell = false);

    private readonly Func<string, ResolvedItem?> _resolve;
    private readonly Func<int, int> _heldCount;
    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _collectAfterCombatFinished;
    private readonly Func<bool> _hasEngageableHostiles;
    private readonly Func<bool> _isParadigm;
    private readonly Func<bool> _isPeekSuppressed;
    private readonly Func<EncumbranceReading> _encumbrance;
    private readonly Func<(bool Light, bool Medium, bool Heavy, bool Past90)> _itemEncGates;
    private readonly Func<long, string, bool> _dropCoinForWeight;
    private readonly LogService? _log;
    private readonly IDisposable _noticeSub;
    private readonly IDisposable _gotSub;

    // Cooldown so an area kill whose several corpses each drop a flagged item
    // re-surveys the room once, not once per corpse.
    private const int ReLookCooldownMs = 750;
    private DateTime _lastReLookAt = DateTime.MinValue;

    // True from a post-kill re-look until the room display it asked for arrives.
    // The walker is held meanwhile; the timer gives up on a display that never
    // comes (a dark room prints no exits line).
    private bool _reLookHeld;
    public bool IsReLookHeld => _reLookHeld;
    private DispatcherTimer? _reLookRelease;
    private const int ReLookReleaseMs = 1500;

    private Terminal.LineExtractor? _lines;
    private string? _noticeBuffer;            // multi-line continuation

    // Items deferred until the room's combat finishes. Cleared on flush
    // and on room change. Holds canonical names (already resolved).
    private readonly List<string> _deferred = new();

    // In-flight gets for the current room's floor, keyed by item name — gets we
    // sent but the server hasn't confirmed with `You took <item>.` yet. A burst of
    // "You notice" re-renders (the Cash engine re-displaying to grab ground coin,
    // our own post-kill re-look, the combat-clear redisplay) all re-list the same
    // still-unprocessed floor items; without this the engine re-sent a get for each,
    // so four ground orc-heads became eight-plus gets. Reconciled against the
    // survey's current count so a genuinely larger pile (a fresh drop) still tops
    // up. Decremented on the `You took` confirmation (the copy left the floor, so a
    // later re-listing is a new drop and collects again); cleared on room change;
    // and swept after the window below as a backstop for a get that never confirms
    // (a failure line we don't yet parse).
    private readonly Dictionary<string, int> _floorInFlight =
        new(StringComparer.OrdinalIgnoreCase);
    private DateTime _floorInFlightAt = DateTime.MinValue;

    // Backstop only: how long an unconfirmed get keeps blocking re-collection
    // before it's presumed failed and swept. Confirmations normally clear entries
    // well before this; kept short so a stuck entry can't strand collection.
    private const int FloorLedgerWindowMs = 1500;

    private Action<byte[]>? _wireSender;
    private AcquisitionGate? _gate;
    private RoomRedisplayCoordinator? _redisplay;
    private bool _disposed;

    public AutoGetItemsManager(
        MessageRouter router,
        Func<string, ResolvedItem?> resolve,
        Func<bool> isEnabled,
        Func<bool> collectAfterCombatFinished,
        Func<bool> hasEngageableHostiles,
        Func<bool>? isPeekSuppressed = null,
        Func<int, int>? heldCount = null,
        Func<EncumbranceReading>? encumbrance = null,
        Func<(bool Light, bool Medium, bool Heavy, bool Past90)>? itemEncGates = null,
        LogService? log = null,
        Func<bool>? isParadigm = null,
        Func<long, string, bool>? dropCoinForWeight = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(collectAfterCombatFinished);
        ArgumentNullException.ThrowIfNull(hasEngageableHostiles);
        _resolve = resolve;
        // Null when unbound → nothing is ever held,
        // so a MaxToGet cap can still fire off the first pickup. Live wiring
        // supplies the carried+worn+key-ring count.
        _heldCount = heldCount ?? (static _ => 0);
        _isEnabled = isEnabled;
        _collectAfterCombatFinished = collectAfterCombatFinished;
        _hasEngageableHostiles = hasEngageableHostiles;
        // Unbound → Stock behaviour: one `get` per unit, never batched.
        _isParadigm = isParadigm ?? (static () => false);
        // Null when unbound → never a peek. A `look <dir>` peek renders a
        // full "You notice" survey for the adjacent room; gate the get path on it
        // so we don't send get commands (and trigger the get→inventory→equip
        // chain) against a room the player never entered.
        _isPeekSuppressed = isPeekSuppressed ?? (static () => false);
        // Null when unbound → Empty reading (MaxWeight 0) disables the
        // encumbrance gate, so the engine collects ungated. Live wiring supplies
        // the parsed InventorySnapshot.Encumbrance and the per-character item
        // bracket gates (the "Cash + Items" tab checkboxes).
        _encumbrance = encumbrance ?? (static () => EncumbranceReading.Empty);
        _itemEncGates = itemEncGates ?? (static () => (false, false, false, false));
        // (weight to free, item name) → whether coin was dropped to free it. Unbound
        // → never.
        _dropCoinForWeight = dropCoinForWeight ?? (static (_, _) => false);
        _log = log;

        _noticeSub = router.Subscribe(KnownPatterns.YouNoticeRoom, OnYouNoticeRoom);
        // `You took <item>.` (own pickup) confirms one of our gets landed. The
        // pattern is shared with `<player> picks up <item>.`; the player group is
        // empty on our own line, which is how OnPlayerGets tells them apart.
        _gotSub = router.Subscribe(KnownPatterns.PlayerGets, OnPlayerGets);
    }

    // Bind the wire sender — the gate-wrapped engine pipeline from
    // MainWindowViewModel.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Bind the shared AcquisitionGate so collecting (or deferring) items holds
    // the walker until get-clear. Optional — when unbound the engine doesn't
    // gate movement.
    public void SetAcquisitionGate(AcquisitionGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _gate = gate;
    }

    // Bind the shared room-redisplay coordinator so the post-kill re-look
    // coalesces with Cash's combat-clear re-display (one Enter, not two). Optional
    // — unbound, the engine sends its own Enter.
    public void SetRoomRedisplay(RoomRedisplayCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _redisplay = coordinator;
    }

    // Bind the per-session LineExtractor so the manager can stitch a wrapped
    // "You notice" survey back together.
    public void AttachLineExtractor(Terminal.LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (ReferenceEquals(_lines, lines)) return;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    // Called on each room-entity observation (wired after the combat tracker so
    // the hostile check is current). Flushes the deferred queue once no
    // engageable hostiles remain — the "combat finished for this room" signal.
    public void OnRoomObserved()
    {
        if (_deferred.Count == 0) return;
        if (_hasEngageableHostiles()) return;   // still fighting — keep waiting
        FlushDeferred();
    }

    // Called on actual room change. Discards any un-flushed deferred gets and the
    // dedup ledger — both belonged to the room we just left.
    public void OnRoomChanged()
    {
        ReleaseReLookHold();
        _floorInFlight.Clear();
        CancelDeferredCollect("room changed");
    }

    // Drop any deferred gets and release the acquisition hold — the items belonged
    // to a room/session we're no longer collecting for. Shared by the room-change
    // path and the reconnect resume: a disconnect mid-defer strands the hold (no
    // self-heal), leaving the walker paused until it's dropped.
    // True when gets were in fact deferred.
    public bool CancelDeferredCollect(string reason)
    {
        if (_deferred.Count == 0) return false;
        _log?.Debug(LogCategory, $"{reason} — dropping {_deferred.Count} deferred get(s)");
        _deferred.Clear();
        _gate?.NoteDeferredCleared();
        return true;
    }

    // Re-survey the room after a kill whose monster could drop an
    // AutoCollect-flagged item. A drop lands loose on the ground as the item but
    // isn't announced on the kill line, so a re-render of the "You notice … here."
    // survey the get path already parses is needed. A bare Enter (carriage return)
    // re-renders that survey exactly like `look` would but without look's room
    // description / flavor text, so we send an empty line — Send appends the \r,
    // making an empty payload a lone Enter. No-op while the master toggle is off;
    // cooldown-guarded so an area kill re-looks once (the single survey collects
    // every ground drop at once).
    public void RequestDropReLook()
    {
        if (_disposed || !_isEnabled()) return;
        DateTime now = DateTime.UtcNow;
        if ((now - _lastReLookAt).TotalMilliseconds < ReLookCooldownMs) return;
        _lastReLookAt = now;
        // The kill clears the Combat gate in this same pass, and a loop's next move
        // would leave before the survey lands. Held even when Cash sends the Enter.
        HoldForReLook();
        // On the last kill, Cash's combat-clear re-display fires the same instant —
        // one bare Enter re-renders the room for both engines (they read the same
        // "You notice" survey). Defer to the shared coordinator so the room isn't
        // displayed twice; if it already rendered, our collect still runs off that
        // survey.
        if (_redisplay is not null && !_redisplay.ShouldSend()) return;
        _log?.Info(LogCategory, "re-look after kill (monster drops a flagged item)");
        Send("");
    }

    // A room display finished (its exits line). The "You notice" line comes before
    // the exits line, so any gets the re-look led to have already gone out and
    // carry the hold from here.
    public void NoteRoomDisplayed() => ReleaseReLookHold();

    private void HoldForReLook()
    {
        if (_gate is null) return;
        _reLookHeld = true;
        _gate.NoteReLookPending();
        if (_reLookRelease is null)
        {
            _reLookRelease = new DispatcherTimer();
            _reLookRelease.Tick += OnReLookReleaseElapsed;
        }
        _reLookRelease.Interval = TimeSpan.FromMilliseconds(ReLookReleaseMs);
        _reLookRelease.Stop();
        _reLookRelease.Start();
    }

    private void OnReLookReleaseElapsed(object? sender, EventArgs e) => ReleaseReLookHold();

    private void ReleaseReLookHold()
    {
        if (!_reLookHeld) return;
        _reLookHeld = false;
        _reLookRelease?.Stop();
        _gate?.NoteReLookDone();
    }

    // ----- notice parsing ----------------------------------------------

    // `You took <item>.` / `<player> picks up <item>.` — the shared PlayerGets
    // pattern. Only our OWN pickup ("You took", empty player group) confirms one
    // of our gets: drain the item's in-flight count (that copy left the floor, so
    // a later re-listing is a fresh drop) and tell the acquisition gate so the
    // walker can resume without waiting on the settle fallback. Someone else's
    // pickup is ignored here.
    private void OnPlayerGets(MatchResult m)
    {
        // Groups: [0]=player (empty for our "You took"), [1]=item.
        if (m.Groups.Count < 2 || m.Groups[0].Length != 0) return;
        string taken = m.Groups[1].Trim();
        if (taken.Length == 0) return;

        // Paradigm confirms a batched get in one counted line ("You took 5
        // orc-head."); strip the count and drain that many. Resolve to the same
        // canonical name the get was keyed under so the decrement lands on the
        // right entry (survey and confirmation both route through the resolver).
        (int count, string name) = CountedCommand.SplitLeadingCount(taken);
        if (_resolve(name) is { } item)
        {
            int have = _floorInFlight.GetValueOrDefault(item.Name);
            int cleared = Math.Min(have, count);
            if (have <= cleared) _floorInFlight.Remove(item.Name);
            else _floorInFlight[item.Name] = have - cleared;
        }
        _gate?.NoteGetConfirmed();
    }

    // Single-line "You notice <list> here." — the pattern subscription path.
    // Multi-line wraps stitch through OnLine and feed the same dispatch.
    private void OnYouNoticeRoom(MatchResult m)
    {
        if (m.Groups.Count == 0) return;
        DispatchList(m.Groups[0]);
    }

    // Multi-line stitch mirrors CashManager.OnLine — a wrapped survey
    // ("You notice ...\n... here.") arrives as two emitted lines, so we
    // buffer from the "You notice " row until a row ends with '.'.
    // Duplicated rather than shared because the two engines own
    // independent buffers and CashManager is already shipped; factoring
    // a common stitcher would mean touching that engine for no behaviour
    // change. Single-line surveys are skipped here (the pattern
    // subscription handles them) to avoid double-processing.
    private void OnLine(Terminal.LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine) return;
        string text = line.Text.TrimEnd();
        if (text.Length == 0) return;

        if (_noticeBuffer is not null)
        {
            _noticeBuffer = _noticeBuffer + " " + text;
            if (text.EndsWith('.'))
            {
                string complete = _noticeBuffer;
                _noticeBuffer = null;
                ProcessMultiLine(complete);
            }
            return;
        }

        if (FloorListLine.OpensWrappedList(text))
            _noticeBuffer = text;
    }

    private void ProcessMultiLine(string completeLine)
    {
        if (FloorListLine.TryReadList(completeLine, out string body))
            DispatchList(body);
    }

    // Split the survey list into entries, resolve each against game data, and
    // collect (or defer) those flagged for auto-collection. The deferred queue
    // is rebuilt per survey — a fresh "You notice" supersedes the prior room
    // snapshot.
    // True while standing in a marked stash room mid-loop AND an auto-search reveal
    // is in flight — the `sea` round-trip that re-exposes items the pass-through
    // stash just hid (report paradigm-20260819-121516). Gated on the reveal window
    // (not merely "in a stash room") so plainly-visible items on entry are still
    // collected. Wired by AppServices to AutoDeposit + AutoSearch; defaults to
    // never-suppress.
    internal Func<bool> SuppressCollectInStashRoom { get; set; } = static () => false;

    // True while a Roomba sweep is underway. Auto-collect is held off for the whole
    // sweep: every flagged item it grabbed would eat the carry headroom Roomba
    // budgets for its moves, shrinking it until pre-planned sorts no longer fit and
    // the sweep can never finish. Roomba sorts flagged items itself; normal
    // auto-collect resumes the moment the sweep ends. Wired by AppServices to
    // GhSweepManager.IsActive; defaults to never-suppress.
    public Func<bool> SuppressDuringSweep { get; set; } = static () => false;

    private void DispatchList(string list)
    {
        if (!_isEnabled()) return;
        // A Roomba sweep is sorting the house — don't let auto-collect grab extra
        // weight and shrink the headroom Roomba needs for its moves.
        if (SuppressDuringSweep())
        {
            _log?.Debug(LogCategory, "skipped you-notice survey (Roomba sweep active)");
            return;
        }
        // A search in a stash room just re-exposed the pile the pass-through stash
        // hid — don't sweep our own stash back up. Only the reveal survey is gated;
        // items visible on plain entry collect normally.
        if (SuppressCollectInStashRoom())
        {
            _log?.Info(LogCategory, "stash-room search reveal — not re-grabbing stashed items");
            return;
        }
        // A look-direction peek renders a full "You notice" survey for the
        // adjacent room. Getting items from a room we never entered wastes
        // commands and (via the resulting inventory change) can fire auto-equip;
        // skip while the peek window is armed.
        if (_isPeekSuppressed())
        {
            _log?.Debug(LogCategory, "skipped you-notice survey (look-direction peek)");
            return;
        }

        ExpireFloorLedgerIfStale();

        bool deferMode = _collectAfterCombatFinished() && _hasEngageableHostiles();
        if (deferMode) _deferred.Clear();   // rebuild for this survey

        // Copies decided in this survey, by item Number — so a MaxToGet cap
        // holds even when a single "You notice" lists the same item twice
        // (the held-count snapshot doesn't move until the get echoes back).
        Dictionary<int, int>? decidedThisPass = null;

        // Floor-dedup bookkeeping for this survey (immediate path). _floorInFlight
        // is read (not mutated) during the pass so the dedup skip is measured
        // against the pre-survey count — two separate "long sword" entries in ONE
        // survey are two floor copies and both collect, while the SAME sword
        // re-listed by a later re-render is skipped. seenThisSurvey tracks copies
        // encountered so far (to know our position past the already-sent count);
        // sentThisSurvey is merged into _floorInFlight once, after the pass.
        Dictionary<string, int>? seenThisSurvey = null;
        Dictionary<string, int>? sentThisSurvey = null;

        // Encumbrance gate. With a known carry weight, cap what we grab this
        // survey so a pickup can't push past the character's capacity (the
        // always-on hard cap = MaxWeight) or past a user-flagged bracket. The
        // running projection charges each accepted item's weight so a survey
        // that lists several heavy items stops at the first that would overflow.
        EncumbranceReading enc = _encumbrance();
        bool encKnown = enc.MaxWeight > 0;
        long capWeight = long.MaxValue;
        if (encKnown)
        {
            (bool gL, bool gM, bool gH, bool g90) = _itemEncGates();
            capWeight = EncumbranceGate.ComputeCapWeight(gL, gM, gH, enc, g90);
        }
        long projectedWeight = enc.CurrentWeight;

        foreach (string entry in SplitEntries(list))
        {
            ResolvedItem? item = _resolve(entry);
            if (item is null) continue;          // not an item / not in game data
            if (item.CannotBeTaken)              // hard never-pick-up flag wins over AutoCollect
            {
                _log?.Debug(LogCategory, $"skipped item={item.Name} (cannot be taken)");
                continue;
            }
            if (!item.AutoCollect) continue;     // user didn't flag it

            // A stacked ground pile is surveyed with a leading count ("5 piece of
            // amber"): every counted unit is weighed on its own, then sent as one
            // `get N <item>` on Paradigm or one get per unit on Stock — otherwise only
            // one is taken and the rest linger until the next room re-display. Each
            // unit re-checks the MaxToGet cap and the encumbrance projection on its
            // own, so a cap or weight ceiling stops the pile partway rather than after
            // the whole stack.
            int units = ParseLeadingCount(entry);
            // Command-side dedup (immediate path only — the deferred build is
            // reconciled at flush). Skip the copies a re-render of this same floor
            // already sent a get for; anything the survey lists beyond that count
            // is a genuinely larger pile (a fresh drop) and still gets collected.
            // Measured against the pre-survey count minus copies of this item
            // already seen earlier in THIS survey, so duplicate entries in one
            // survey collect while a re-render's re-listing is skipped.
            int skip = 0;
            if (!deferMode)
            {
                int alreadySent = _floorInFlight.GetValueOrDefault(item.Name);
                int seenBefore = seenThisSurvey?.GetValueOrDefault(item.Name) ?? 0;
                skip = Math.Min(units, Math.Max(0, alreadySent - seenBefore));
                (seenThisSurvey ??= new(StringComparer.OrdinalIgnoreCase))[item.Name] =
                    seenBefore + units;
                if (skip > 0)
                    _log?.Debug(LogCategory,
                        $"re-render: skipping {skip} already-collected {item.Name} this room");
            }
            // Immediate-mode units of this entry that clear both gates — emitted as
            // one `get N <item>` on Paradigm (see after the loop), or N single gets
            // on Stock.
            int allowedThisEntry = 0;
            for (int u = skip; u < units; u++)
            {

                // MaxToGet carry cap. Count what we already hold (carried + worn +
                // key ring — key-type items land in the ring, not the pack) plus
                // anything already decided in this same survey; stop at the cap.
                // Decided-count and projected-weight are committed only after BOTH
                // gates pass, so a weight refusal doesn't burn a cap slot.
                int decidedSoFar = decidedThisPass?.GetValueOrDefault(item.Number) ?? 0;
                if (item.MaxToGet != int.MaxValue)
                {
                    int have = _heldCount(item.Number) + decidedSoFar;
                    if (have >= item.MaxToGet)
                    {
                        _log?.Debug(LogCategory,
                            $"skipped item={item.Name} (have {have} >= max {item.MaxToGet})");
                        break;   // cap reached — leave the rest of the pile
                    }
                }

                // Weight gate — skip a pickup that would breach the cap. Weight 0
                // (unknown OR genuinely weightless) means "don't gate": a weightless
                // item fits even at capacity, and an unknown-weight get is
                // self-correcting (the server picks up a weightless item and rejects
                // an over-cap one), so there's no reliable signal to suppress on.
                if (encKnown && item.Weight > 0 && projectedWeight + item.Weight > capWeight)
                {
                    // An item we'd sell may be worth the coin in its way: drop just
                    // enough to fit it. Not while its get waits on a fight, though —
                    // the coin would be down long before the item came up.
                    long over = projectedWeight + item.Weight - capWeight;
                    if (deferMode || !item.AutoSell || !_dropCoinForWeight(over, item.Name))
                    {
                        _log?.Info(LogCategory,
                            $"skipped item={item.Name} (encumbrance: {projectedWeight}+{item.Weight} > cap {capWeight})");
                        break;   // over the weight cap — leave the rest of the pile
                    }
                    projectedWeight -= over;
                }

                if (item.MaxToGet != int.MaxValue)
                {
                    decidedThisPass ??= new Dictionary<int, int>();
                    decidedThisPass[item.Number] = decidedSoFar + 1;
                }
                if (encKnown && item.Weight > 0) projectedWeight += item.Weight;

                if (deferMode)
                {
                    _deferred.Add(item.Name);
                    _log?.Info(LogCategory, $"deferred (combat) item={item.Name}");
                }
                else
                {
                    allowedThisEntry++;
                }
            }

            if (!deferMode && allowedThisEntry > 0)
            {
                _log?.Info(LogCategory, $"collect {allowedThisEntry}x item={item.Name}");
                // Paradigm grabs the pile in one `get N <item>`; Stock sends one per
                // unit. NoteGetSent fires per emitted line so the gate balances the
                // count-prefixed `You took N <item>` confirmation either way.
                CountedCommand.Emit(cmd => { _gate?.NoteGetSent(); Send(cmd); },
                    "get", allowedThisEntry, item.Name, _isParadigm());
                (sentThisSurvey ??= new(StringComparer.OrdinalIgnoreCase))[item.Name] =
                    sentThisSurvey.GetValueOrDefault(item.Name) + allowedThisEntry;
            }
        }

        // Commit this survey's immediate gets to the floor ledger in one shot so a
        // following re-render of the same floor reconciles against them.
        if (sentThisSurvey is not null)
        {
            foreach ((string name, int n) in sentThisSurvey)
                _floorInFlight[name] = _floorInFlight.GetValueOrDefault(name) + n;
            _floorInFlightAt = DateTime.UtcNow;
        }

        // Hold the walker while the queued gets wait for combat to finish.
        // Asserted now, before CombatStateTracker clears the Combat gate on
        // the same EntitiesObserved pass, so the walker can't slip out
        // between fight-clear and the loot flush.
        if (deferMode) _gate?.NoteDeferredPending(_deferred.Count);
    }

    private void FlushDeferred()
    {
        ExpireFloorLedgerIfStale();

        // Count the deferred gets per name (first-seen order) so we can honour any
        // copies a same-window re-render already grabbed via the immediate path
        // (normally none — the deferred flush is the room's first collection), then
        // grab each name's remainder in one `get N <item>` on Paradigm.
        Dictionary<string, int> deferredByName = new(StringComparer.OrdinalIgnoreCase);
        List<string> flushOrder = new();
        foreach (string name in _deferred)
        {
            if (!deferredByName.ContainsKey(name)) flushOrder.Add(name);
            deferredByName[name] = deferredByName.GetValueOrDefault(name) + 1;
        }

        bool sentAny = false;
        foreach (string name in flushOrder)
        {
            int have = _floorInFlight.GetValueOrDefault(name);
            int toSend = deferredByName[name] - have;   // ledger already covers `have`
            if (toSend <= 0) continue;
            _log?.Info(LogCategory, $"collect (post-combat) {toSend}x item={name}");
            CountedCommand.Emit(cmd => { _gate?.NoteGetSent(); Send(cmd); },
                "get", toSend, name, _isParadigm());
            _floorInFlight[name] = have + toSend;
            sentAny = true;
        }
        if (sentAny) _floorInFlightAt = DateTime.UtcNow;

        _deferred.Clear();
        _gate?.NoteDeferredCleared();
    }

    // Get a named item the caller knows to be the character's own and lying on this
    // floor (what a penalised hang-up dropped, HangupItemRecheck). None of the
    // auto-collect choices apply to it: not the master toggle, the per-item flag,
    // the carry cap or the encumbrance brackets, since it is being taken back, not
    // looted. It still goes out the one way a get does here: counted per realm,
    // held on the acquisition gate until `You took` answers it, and reconciled with
    // the floor ledger both ways. A get this engine already has out for the item
    // counts towards the copies asked for (the collect-after-combat flush runs on
    // the observation that ends a fight, just ahead of a caller waiting on the same
    // one, and a room redisplay the caller asked for collects a flagged item
    // first); and what is sent here is entered there, so a re-render of the same
    // floor doesn't collect it a second time.
    public void CollectNamed(string name, int count, string why)
    {
        if (_disposed || count <= 0 || string.IsNullOrWhiteSpace(name)) return;
        ExpireFloorLedgerIfStale();
        // Under the name the confirmation drains it by (OnPlayerGets).
        string ledgerName = _resolve(name)?.Name ?? name;
        int have = _floorInFlight.GetValueOrDefault(ledgerName);
        int toSend = count - have;
        if (toSend <= 0)
        {
            _log?.Info(LogCategory, $"collect {count}x item={name} ({why}): {have} get(s) already out for it, none added");
            return;
        }
        _log?.Info(LogCategory, $"collect {toSend}x item={name} ({why})"
            + (have > 0 ? $"; {have} more already out" : ""));
        CountedCommand.Emit(cmd => { _gate?.NoteGetSent(); Send(cmd); },
            "get", toSend, name, _isParadigm());
        _floorInFlight[ledgerName] = have + toSend;
        _floorInFlightAt = DateTime.UtcNow;
    }

    // Drop the dedup ledger once its window has elapsed: by then the sent gets are
    // reflected on the floor, so a fresh survey is authoritative and a later kill's
    // drop must be collectable again.
    private void ExpireFloorLedgerIfStale()
    {
        if (_floorInFlight.Count == 0) return;
        if ((DateTime.UtcNow - _floorInFlightAt).TotalMilliseconds > FloorLedgerWindowMs)
            _floorInFlight.Clear();
    }

    // The survey prefixes a stacked ground pile with its count ("5 piece of
    // amber", "three torches"). Return that count so the collect loop can send
    // one get per unit; a missing or unrecognized leading token is the article
    // form ("a rusty dagger") — a single item. Mirrors the leading-count strip
    // in ItemNameStore.Normalize so the count parsed here is exactly the token
    // the resolver drops when matching the item name. Shared with
    // HangupItemPlan, which counts the same entries.
    internal static int ParseLeadingCount(string entry)
    {
        int sp = entry.IndexOf(' ');
        if (sp <= 0) return 1;
        string first = entry[..sp];
        if (int.TryParse(first, out int n)) return n > 0 ? n : 1;
        return first.ToLowerInvariant() switch
        {
            "two" => 2,
            "three" => 3,
            "four" => 4,
            "five" => 5,
            "six" => 6,
            "seven" => 7,
            "eight" => 8,
            "nine" => 9,
            "ten" => 10,
            _ => 1,   // "one", an article, or a plain noun → single item
        };
    }

    // Split "a, b and c" survey wording into individual entries — commas
    // separate all but the final pair, which uses " and ".
    private static IEnumerable<string> SplitEntries(string list)
    {
        foreach (string comma in list.Split(',', StringSplitOptions.TrimEntries
                                              | StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string piece in comma.Split(" and ",
                         StringSplitOptions.TrimEntries
                         | StringSplitOptions.RemoveEmptyEntries))
            {
                if (piece.Length > 0) yield return piece;
            }
        }
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
        _noticeSub.Dispose();
        _gotSub.Dispose();
        if (_reLookRelease is not null)
        {
            _reLookRelease.Stop();
            _reLookRelease.Tick -= OnReLookReleaseElapsed;
            _reLookRelease = null;
        }
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = null;
    }
}
