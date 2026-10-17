using System.Collections.Generic;
using System.Linq;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Inventory;

// Auto-discard items engine. When the pack holds an item flagged
// ItemOverlay.AutoDiscard above its keep floor, offloads the excess with
// drop <item name> — or hide <item name> when HideMode is set (OtherSettings
// "hide when discarding") — one counted command on Paradigm, one command per
// copy on Stock. The keep floor is MinToKeep when MustHaveMinimum is set; otherwise
// zero, so an unbanded flagged item is discarded entirely (the confirmed
// "no band → discard all" rule). A LoyalItem is never discarded even if also
// flagged AutoDiscard — loyalty (never-drop) is the safety flag and wins the
// contradiction.
//
// Exists to clean up chest dumps — open chest pours a set of random items
// straight into inventory that the player can't refuse — and unwanted
// auto-collected loot.
//
// Trigger: InventoryManager.Changed (wired in AppServices to OnInventoryChanged).
// Every inventory change re-evaluates the carried list. Offloads the engine has
// sent but not yet seen confirmed are held in _inFlight (drops) or _hidesInFlight
// (hides) and subtracted from the live count, so the Changed events its own
// confirmations raise don't re-send. Own "You dropped X." lines clear the drop
// count via the PlayerDrops subscription; "You hid X." clears the hide count via
// UserHides. Other players' drops (same PlayerDrops pattern, alternate branch)
// are ignored.
//
// Each hide is also registered in _suppressLog so the transaction-history
// forwarder can tell a discard's hide from a genuine stash — it is a discard, not
// a "stashed" item, so TryConsumeSuppressedHide lets the forwarder drop that
// ledger row while manual / stash-room hides (never registered here) still record.
//
// The hide-or-drop choice is one decision for every discard, not only the
// engine's own: a surface that discards by hand (Chest Offload's Drop buttons)
// sends through EmitDiscard, so the setting covers it and its hides stay out of
// the stash ledger the same way.
//
// A hide the room has no room for (Stock only: its rooms cap their hidden items)
// keeps the item and is sent again in each new room the character enters until
// it lands (user, 2026-10-09). It is never turned into a drop. The hold is for a
// hide still wanted: selling the item, dropping it, hiding it by hand, or taking
// it off the Chest Offload list calls the hold off (ReleaseHeld), since by then
// any copy left in the pack may be one the player means to keep.
//
// A hide for a copy that isn't in the pack is worse than wasted: Stock's engine
// doesn't answer it, and the line then falls through to speech. So no hide goes
// out for more copies than are carried and not already spoken for, and whatever
// is still unanswered is forgotten on a death, a disconnect, a profile swap, and
// once two inventory reads pass with no discard traffic between them. After one
// of the first three nothing goes out until the pack has been read again.
//
// Master switch: Auto Get Items (AutoActionDefaults.AutoGetItems, the Settings
// and Action-menu toggle the item engines share). Runs UI-thread only
// (MessageRouter + Inventory.Changed both marshal upstream), so the dictionaries
// need no lock.
public sealed class AutoDiscardManager : IDisposable
{
    // LogService category — [AutoDiscard] rows per dropped item.
    public const string LogCategory = "AutoDiscard";

    // One resolved carried entry: the canonical item Number, the name to send to
    // the game, whether the user flagged it for discard, and how many copies to
    // keep (0 = discard all).
    public sealed record ResolvedDiscard(int Number, string Name, bool Discard, int KeepCount);

    // Copies of one item by who sent their hide. The engine's own answer to its
    // master switch; a by-hand surface's never did.
    private readonly record struct Copies(int ByHand, int Engine)
    {
        public int Total => ByHand + Engine;

        public static Copies Of(int count, bool byHand) => byHand ? new(count, 0) : new(0, count);

        public Copies Plus(Copies other) => new(ByHand + other.ByHand, Engine + other.Engine);

        // Split off up to `count` copies, the by-hand ones first: which copy a
        // line is about can't be told, and by-hand is the one the player is
        // watching.
        public (Copies Taken, Copies Left) Take(int count)
        {
            int hand = Math.Clamp(count, 0, ByHand);
            int engine = Math.Clamp(count - hand, 0, Engine);
            return (new(hand, engine), new(ByHand - hand, Engine - engine));
        }
    }

    // One item's hides that are waiting for somewhere they can land.
    private sealed class HeldHide
    {
        // The name to send.
        public required string Name { get; init; }
        // Refused, or never sent for want of an answer: waiting for a room.
        public Copies Waiting;
        // Sent again and not yet answered. These are in _hidesInFlight as well.
        public int Out;
        // The room that last refused it, or the one its last retry went out in.
        // Null when no room has refused it.
        public RoomKey? TriedIn;
        // Refused while the room wasn't known: the first room confirmed is that one.
        public bool RefusedBeforeRoomKnown;
        // Copies in the pack that no discard was out for when the hide was last
        // refused: the player's own. A retry never reaches into these, however
        // the copies that were to be hidden have left since.
        public int Kept;
        public int Count => Waiting.Total + Out;
    }

    // A held hide as the bug report shows it.
    public sealed record HeldHideInfo(string Name, int ByHand, int Engine, int Out, string Why)
    {
        public int Count => ByHand + Engine + Out;
    }

    private readonly Func<IReadOnlyList<string>> _carried;
    // The names of what is worn: copies the character holds that are not in the
    // pack. They count toward an item's keep amount (user, 2026-10-09) and are
    // never what a discard is sent for.
    private readonly Func<IEnumerable<string>> _worn;
    // The lit light's name, as the last full read listed it. The listing sets it
    // apart, but to the game it is a pack copy like any other and is counted as
    // one: a flagged light is discarded lit or not (user, 2026-10-10), and which
    // copy a drop takes is the game's choice.
    private readonly Func<string?> _lit;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<string, ResolvedDiscard?> _resolve;
    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _isParadigm;
    private readonly LogService? _log;
    private readonly IDisposable _dropSub;
    private readonly IDisposable _hideSub;
    private readonly IDisposable _hideRefusedSub;
    private readonly IDisposable _dropRefusedSub;

    // item Number → drops sent (the engine's or a by-hand surface's) but not yet
    // confirmed by a self "You dropped X.".
    private readonly Dictionary<int, int> _inFlight = new();

    // item Number → the name its discards went out under, to find them again in
    // the pacer's queue.
    private readonly Dictionary<int, string> _sentAs = new();

    // item Number → the room whose floor had no room for its drop. It is not
    // dropped there again; the next room entered clears it.
    private readonly Dictionary<int, RoomKey?> _dropRefusedIn = new();

    // item Number → retries called off while they were out. They stay counted as
    // in flight until answered, so nothing else is sent for those copies; a
    // refusal of one holds nothing.
    private readonly Dictionary<int, int> _recalled = new();

    // item Number → hides sent (the engine's, a by-hand surface's, a retry's) but
    // not yet confirmed by a self "You hid X.".
    private readonly Dictionary<int, Copies> _hidesInFlight = new();

    // item Number → discard hides not yet claimed by the transaction-log
    // forwarder, so a hide that is a discard is kept out of the stash ledger.
    private readonly Dictionary<int, int> _suppressLog = new();

    // item Number → the copies whose hide a room refused.
    private readonly Dictionary<int, HeldHide> _held = new();

    // The room the character stands in, as last confirmed. Null while unknown.
    private RoomKey? _room;

    // When a discard was last sent or answered, and whether a full inventory read
    // has landed since with the answers given time to arrive. A listing asked for
    // before a pile went out can land ahead of its first answer, so a read only
    // says something about the pile once that long has passed.
    private DateTimeOffset _lastTrafficAt = DateTimeOffset.MinValue;
    private bool _quietReadSeen;

    // How long the game is given to answer before a read counts as quiet.
    public static readonly TimeSpan AnswerSettle = TimeSpan.FromSeconds(10);

    // item Number → copies of the engine's own commands still waiting in the pacer,
    // piles and retries alike. What is counted as out less these is on the wire.
    private readonly Dictionary<int, int> _queued = new();

    // The tags the engine's commands wait in the pacer under: a pile from the
    // evaluation, and a held hide's retry. Each is taken back on its own terms.
    private readonly object _pileOwner = new();
    private readonly object _retryOwner = new();

    // The pack hasn't been read since a reset (a death, a disconnect, a profile
    // swap): the carried list is the one from before, and a hide for a copy that
    // went meanwhile would not be refused but said aloud. Nothing is sent until a
    // full `i` lands; a coin picked up in between must not set the engine off.
    private bool _awaitingRead;

    // True from a reset until the next full inventory read: no discard goes out.
    public bool AwaitingInventoryRead => _awaitingRead;

    // The last reason every held hide was kept back, and the room it was in, so
    // the log says it once and not on every inventory change.
    private (RoomKey? Room, string Reason)? _keptBack;

    private bool _hideMode;

    // When true, offload with hide <item> (conceal on the ground) instead of
    // drop <item>. Live-mirrored from OtherSettings via AppServices ApplyToServices.
    // Turning it off gives up the hides waiting for a room: the player no longer
    // wants items hidden, and the engine's own are picked up again as drops.
    public bool HideMode
    {
        get => _hideMode;
        set
        {
            if (_hideMode == value) return;
            _hideMode = value;
            if (!value) EndAllHeld("hide when discarding turned off");
        }
    }

    // The verb a discard goes out with right now.
    public string DiscardVerb => HideMode ? "hide" : "drop";

    // A discard's hide was refused for want of room ("There is no room to hide X
    // here.") and is held for the next room, with the item the game named. A hide
    // this engine never sent (typed, a stash room's, Hide All's) doesn't raise it.
    public event Action<string>? HideRefused;

    // Nothing of this item is held any more: its hide landed, or the hold was
    // called off, given up or forgotten.
    public event Action<string>? HeldHideEnded;

    // True while a Roomba sweep is underway. Auto-discard is held off for the whole
    // sweep so it can't bin an item Roomba is in the middle of relocating — Roomba
    // sorts auto-discard-flagged items into their labeled rooms instead. Normal
    // auto-discard resumes the moment the sweep ends, clearing anything that
    // accumulated. Wired by AppServices to GhSweepManager.IsActive; defaults to
    // never-suppress.
    public Func<bool> SuppressDuringSweep { get; set; } = static () => false;

    // False while the engine send gate is up: a command sent then is dropped
    // unsent and would stay counted as on its way. Nothing is sent, drop or hide.
    public Func<bool> SendGateOpen { get; set; } = static () => true;

    // False while a hide would get no answer of its own for want of sight: on
    // Stock the engine answers a hide in the dark or blind with the can't-see line
    // and hides nothing (Paradigm isn't known to). Held hides wait, the engine's
    // own hides are not sent, and a by-hand one is held instead of sent. A drop
    // needs no sight.
    public Func<bool> CanSeeToHide { get; set; } = static () => true;

    private bool HideCanBeAnswered() => SendGateOpen() && CanSeeToHide();

    // Drops whatever the pacer still has queued. After a death or a disconnect a
    // queued discard would go out against a pack that is no longer the one it was
    // counted from.
    public Action? CancelQueuedSends { get; set; }

    // True while the pacer still has discards queued that were counted when they
    // were handed to it. They are not unanswered yet, only unsent.
    public Func<bool> SendsQueued { get; set; } = static () => false;

    // Sends a batch a few at a time. A pile is one command per copy on Stock, and a
    // room's worth of held hides goes out right after a move, when Stock queues
    // commands behind the move's delay and drops them past a dozen. The batch goes
    // in under a tag, to be taken back while it waits (RecallQueued), and with a
    // check the pacer makes just before each command goes: a command can wait
    // seconds, and what made it right to queue may not hold by then. Unbound,
    // each goes straight out.
    public Action<IReadOnlyList<string>, object, Func<string, bool>>? PacedSender { get; set; }

    // Takes back commands still waiting in the pacer under a tag: handed a test
    // for each waiting command, returns the ones it took.
    public Func<object, Func<string, bool>, IReadOnlyList<string>>? RecallQueued { get; set; }

    private Action<byte[]>? _wireSender;
    private bool _disposed;

    public AutoDiscardManager(
        MessageRouter router,
        Func<IReadOnlyList<string>> carriedItems,
        Func<string, ResolvedDiscard?> resolve,
        Func<bool> isEnabled,
        LogService? log = null,
        Func<bool>? isParadigm = null,
        Func<IEnumerable<string>>? wornItems = null,
        Func<string?>? litLight = null,
        Func<DateTimeOffset>? now = null,
        Func<int, int>? carriedForJourney = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(carriedItems);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(isEnabled);
        _carried = carriedItems;
        // Unbound: only the pack counts.
        _worn = wornItems ?? (static () => Array.Empty<string>());
        _lit = litLight ?? (static () => null);
        _carriedForJourney = carriedForJourney ?? (static _ => 0);
        _now = now ?? (static () => DateTimeOffset.UtcNow);
        _resolve = resolve;
        _isEnabled = isEnabled;
        // Unbound → Stock behaviour: one drop/hide per copy, never batched.
        _isParadigm = isParadigm ?? (static () => false);
        _log = log;

        _dropSub = router.Subscribe(KnownPatterns.PlayerDrops, OnDropLine);
        _hideSub = router.Subscribe(KnownPatterns.UserHides, OnHideLine);
        _hideRefusedSub = router.Subscribe(KnownPatterns.RoomHideRefused, OnHideRefusedLine);
        _dropRefusedSub = router.Subscribe(KnownPatterns.RoomDropRefused, OnDropRefusedLine);
    }

    // Bind the wire sender — the gate-wrapped engine pipeline from
    // MainWindowViewModel.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Re-evaluate the carried list on any inventory change and drop each flagged
    // item down to its keep floor.
    public void OnInventoryChanged() => LookAgain(rulesChanged: false);

    // The rules a pile was counted under have changed (the engine's switch, a
    // Roomba sweep, an item's flag or keep amount): what waits in the pacer is
    // gone through against them, then the pack is evaluated afresh. Not for use
    // from inside a game line's handling: the keep arithmetic wants the pack and
    // the counts to agree, and mid-line one may have moved before the other.
    public void OnRulesChanged() => LookAgain(rulesChanged: true);

    private void LookAgain(bool rulesChanged)
    {
        // Whatever kept a held hide back on arriving here (a sweep, the engine's
        // switch, the send gate, the dark) may have cleared since.
        RecheckHeldHides();
        ReviewQueued(withKeepAmounts: rulesChanged);
        Evaluate();
    }

    private void Evaluate()
    {
        if (_awaitingRead || !_isEnabled() || _wireSender is null) return;
        // A Roomba sweep is sorting the house — don't bin an item it may be
        // relocating; Roomba sorts auto-discard-flagged items itself.
        if (SuppressDuringSweep()) return;
        // Sent now it would be lost and still counted as on its way.
        if (!SendGateOpen() || (HideMode && !CanSeeToHide())) return;

        // Copies per item Number, not entries: the pack lists a pile as one entry
        // with its count in front ("3 torch").
        Dictionary<int, (ResolvedDiscard Item, int Count)> groups = new();
        foreach (string entry in PackEntries())
        {
            if (_resolve(entry) is not { Discard: true } item) continue;
            int copies = CountedCommand.SplitLeadingCount(entry.Trim()).Count;
            groups[item.Number] = (item, groups.GetValueOrDefault(item.Number).Count + copies);
        }

        List<string> commands = new();
        foreach ((int number, (ResolvedDiscard item, int count)) in groups)
        {
            // This room's floor has refused the item already.
            if (!HideMode && _dropRefusedIn.TryGetValue(number, out RoomKey? full) && Nullable.Equals(full, _room))
                continue;
            // What may still go once the outstanding offloads land. A copy held
            // for the next room is spoken for too: this room refused it.
            int toDrop = count - SpokenFor(number) - PackFloor(item);
            if (toDrop <= 0) continue;

            _log?.Info(LogCategory,
                $"discard {toDrop}x item={item.Name} via {DiscardVerb} (pack {count}, worn {WornCopies(number)}, keep {item.KeepCount})");
            // The count-prefixed confirmation clears in-flight by N below.
            Offload(commands.Add, number, item.Name, Copies.Of(toDrop, byHand: false));
        }
        SendAll(commands, _pileOwner, PileMayGo);
    }

    // The pack as the game holds it: the listed entries, and the lit light, which
    // the listing sets apart but which is one more copy in a pack slot.
    private IEnumerable<string> PackEntries()
        => _lit() is { Length: > 0 } lit ? _carried().Append(lit) : _carried();

    // Copies of an item worn. A worn piece is not in the pack.
    private int WornCopies(int number)
    {
        int copies = 0;
        foreach (string name in _worn())
            if (_resolve(name) is { } item && item.Number == number) copies++;
        return copies;
    }

    // How many copies the engine must leave in the pack: the keep amount less what
    // is worn. Worn copies count toward keeping, so a piece just taken off is not
    // an extra; and only pack copies are ever asked for, since a discard for more
    // than the pack holds would take the worn one off.
    // Copies a walk under way has fetched for the party to cross its gates with (its
    // own and those it hands to members) stay too: dropped, the copy bought for a
    // member was bought again every trip (report paradigm-20261016-031735; user,
    // 2026-10-16).
    private int PackFloor(ResolvedDiscard item) =>
        Math.Max(Math.Max(0, item.KeepCount - WornCopies(item.Number)), _carriedForJourney(item.Number));

    private readonly Func<int, int> _carriedForJourney;

    // A pile is one command on Paradigm and one per copy on Stock, which is why
    // the engine's commands share the pacer with the by-hand sweeps.
    private void SendAll(IReadOnlyList<string> commands, object owner, Func<string, bool> mayGo)
    {
        if (commands.Count == 0) return;
        if (PacedSender is not { } paced)
        {
            foreach (string command in commands) Send(command);
            return;
        }
        foreach (string command in commands)
            if (Queued(command) is { } q) _queued[q.Item.Number] = _queued.GetValueOrDefault(q.Item.Number) + q.Count;
        paced(commands, owner, mayGo);
    }

    // One of the engine's waiting commands, read back: the item, the verb, the copies.
    private (ResolvedDiscard Item, bool Hide, int Count)? Queued(string command)
        => DiscardOf(command) is { } d && _resolve(d.Name) is { } item ? (item, d.Hide, d.Count) : null;

    // Copies of an item whose discard has gone to the game and not been answered:
    // what is counted as out, less the engine's commands still waiting to be sent.
    private int OnTheWire(int number)
        => Math.Max(0, _inFlight.GetValueOrDefault(number) + _hidesInFlight.GetValueOrDefault(number).Total
                       - _queued.GetValueOrDefault(number));

    // Asked by the pacer just before one of a pile's commands goes. The same things
    // are checked as when the pile was counted, against the pack as it is now; a
    // command that no longer holds is not sent, and it and the rest of its pile
    // come off the count, so nothing goes out blind and nothing stays counted.
    private bool PileMayGo(string command)
    {
        if (Queued(command) is not { } q) return true;
        int number = q.Item.Number;
        int onTheWire = OnTheWire(number);
        Reduce(_queued, number, q.Count);

        string? stop = WhyAPileStops(q.Item, q.Hide);
        if (stop is null)
        {
            int held = _held.TryGetValue(number, out HeldHide? h) ? h.Waiting.Total : 0;
            if (CarriedCopies(number) - PackFloor(q.Item) - held - onTheWire < q.Count)
                stop = $"the pack has {CarriedCopies(number)} and the keep amount is {q.Item.KeepCount}";
        }
        if (stop is null)
        {
            NoteTraffic();
            return true;
        }

        Uncount(number, q.Hide, q.Count);
        int rest = RecallPile(number, q.Item.Name, int.MaxValue, stop, quiet: true);
        _log?.Info(LogCategory, $"{q.Count + rest} queued discard(s) of {q.Item.Name} not sent: {stop}");
        // Paradigm's pile is one command and came back whole: what the rules still
        // ask for goes out again at once.
        Evaluate();
        return false;
    }

    // Why none of an item's pile may go right now, or null. The pack arithmetic is
    // the caller's: it is only sound when the pack and the counts agree.
    private string? WhyAPileStops(ResolvedDiscard item, bool hide)
    {
        if (_awaitingRead) return "the pack hasn't been read again yet";
        if (!_isEnabled()) return "auto-discard is off";
        if (SuppressDuringSweep()) return "a Roomba sweep is running";
        if (!item.Discard) return "it is no longer flagged for auto-discard";
        if (!SendGateOpen()) return "the send gate is up";
        if (hide && !CanSeeToHide()) return "the character can't see";
        return null;
    }

    // Go through the engine's commands still waiting in the pacer and take back
    // what should not go. A command already sent is out of reach, and by-hand
    // discards are not the engine's to take back. The keep amounts are only gone
    // into when asked: from inside a line's handling the pack may have changed
    // before the count has, and a pile would look one too many.
    private void ReviewQueued(bool withKeepAmounts)
    {
        if (RecallQueued is null || _queued.Count == 0) return;
        // A retry answers to what keeps held hides back, not to the engine's switch
        // or the item's flag: a by-hand discard never did.
        string? retriesStop = KeptBackBy();
        foreach (int number in _queued.Keys.ToList())
        {
            if (NameOf(number) is not { } name || _resolve(name) is not { } item) continue;
            if (retriesStop is not null) RecallRetries(number, name, int.MaxValue, retriesStop);
            if (WhyAPileStops(item, hide: _hidesInFlight.ContainsKey(number)) is { } stop)
            {
                RecallPile(number, name, int.MaxValue, stop);
                continue;
            }
            if (!withKeepAmounts) continue;
            int held = _held.TryGetValue(number, out HeldHide? h) ? h.Waiting.Total : 0;
            int allowed = Math.Max(0, CarriedCopies(number) - PackFloor(item) - held);
            int excess = _inFlight.GetValueOrDefault(number) + _hidesInFlight.GetValueOrDefault(number).Total - allowed;
            if (excess > 0) RecallPile(number, name, excess, $"the keep amount is {item.KeepCount}");
        }
    }

    // Take up to `copies` of an item's waiting pile back from the pacer and off the
    // count. Returns how many copies came back.
    private int RecallPile(int number, string name, int copies, string reason, bool quiet = false)
    {
        int back = 0;
        foreach ((bool hide, int count) in TakeFromQueue(_pileOwner, number, name, copies))
        {
            back += count;
            Uncount(number, hide, count);
        }
        if (back > 0 && !quiet) _log?.Info(LogCategory, $"{back} queued discard(s) of {name} taken back: {reason}");
        return back;
    }

    // The same for a held hide's retry still waiting: its copies go back to being
    // held. No room is recorded against them, since none has refused them since.
    private int RecallRetries(int number, string name, int copies, string reason)
    {
        int back = 0;
        foreach ((_, int count) in TakeFromQueue(_retryOwner, number, name, copies)) back += count;
        if (back > 0) ReholdRetry(number, back, reason);
        return back;
    }

    private void ReholdRetry(int number, int copies, string reason)
    {
        Copies taken = TakeFlight(number, copies);
        Reduce(_suppressLog, number, copies);
        if (!_held.TryGetValue(number, out HeldHide? held)) return;
        held.Out -= Math.Min(held.Out, copies);
        held.Waiting = held.Waiting.Plus(taken);
        held.TriedIn = null;
        _log?.Info(LogCategory, $"retry of {copies}x {held.Name} not sent, held again: {reason}");
    }

    // Take an item's commands waiting under one tag out of the pacer, up to
    // `copies` of them. Returns each one's verb and count.
    private List<(bool Hide, int Count)> TakeFromQueue(object owner, int number, string name, int copies)
    {
        List<(bool Hide, int Count)> taken = new();
        if (RecallQueued is not { } recall) return taken;
        int budget = copies;
        foreach (string command in recall(owner, command =>
                 {
                     if (budget <= 0 || DiscardOf(command) is not { } queued) return false;
                     if (!string.Equals(queued.Name, name, StringComparison.OrdinalIgnoreCase)) return false;
                     budget -= queued.Count;
                     return true;
                 }))
        {
            (bool hide, int count, _) = DiscardOf(command)!.Value;
            Reduce(_queued, number, count);
            taken.Add((hide, count));
        }
        return taken;
    }

    // Take a pile's copies off the count: they were never sent.
    private void Uncount(int number, bool hide, int count)
    {
        if (!hide)
        {
            Reduce(_inFlight, number, count);
            return;
        }
        TakeFlightEngineFirst(number, count);
        Reduce(_suppressLog, number, count);
    }

    // Asked by the pacer just before a held hide's retry goes. A retry that could
    // get no answer now, or for a copy that has left the pack, is not sent: it and
    // the rest of that item's retries go back to being held.
    private bool RetryMayGo(string command)
    {
        if (Queued(command) is not { } q) return true;
        int number = q.Item.Number;
        int onTheWire = OnTheWire(number);
        Reduce(_queued, number, q.Count);

        string? stop = !HideMode ? "hide when discarding is off" : KeptBackBy();
        if (stop is null && CarriedCopies(number) - onTheWire < q.Count) stop = "it is no longer in the pack";
        if (stop is null)
        {
            NoteTraffic();
            return true;
        }
        ReholdRetry(number, q.Count, stop);
        RecallRetries(number, q.Item.Name, int.MaxValue, stop);
        return false;
    }

    // A discard command as this engine words it: the verb, the copies and the item.
    private static (bool Hide, int Count, string Name)? DiscardOf(string command)
    {
        bool hide = command.StartsWith("hide ", StringComparison.Ordinal);
        if (!hide && !command.StartsWith("drop ", StringComparison.Ordinal)) return null;
        (int count, string name) = CountedCommand.SplitLeadingCount(command[5..]);
        return (hide, count, name);
    }

    // The name an item's discards went out under. The pack may no longer list it.
    private string? NameOf(int number) => _sentAs.GetValueOrDefault(number);

    // A discard went out or was answered: a read from now on says nothing about
    // what is still counted until the answers have had time to come.
    private void NoteTraffic()
    {
        _lastTrafficAt = _now();
        _quietReadSeen = false;
    }

    // Send a discard of up to `count` copies of `name` for a surface that discards
    // by hand, in the engine's own wording: hide or drop by HideMode, counted the
    // way the realm takes it. Only copies that are carried and not already spoken
    // for go out, so a second press before the first is answered sends nothing.
    // A hide that could get no answer here is held for when it can instead of
    // sent.
    public DiscardResult EmitDiscard(Action<string> send, string name, int count)
    {
        ArgumentNullException.ThrowIfNull(send);
        string verb = DiscardVerb;
        // A blank name would leave a bare `hide`, which hides the character.
        if (string.IsNullOrWhiteSpace(name) || _resolve(name) is not { } item) return new(verb, 0, 0);
        if (_awaitingRead) return new(verb, 0, 0, "your inventory hasn't been read yet (type i)");

        int held = TakeOverEngineHold(item, count);
        int free = Math.Min(count, CarriedCopies(item.Number)) - SpokenFor(item.Number);
        if (free > 0 && HideMode && !HideCanBeAnswered())
        {
            HeldHide hold = HoldFor(item);
            hold.Waiting = hold.Waiting.Plus(Copies.Of(free, byHand: true));
            hold.Kept = UnspokenCopies(item.Number);
            _log?.Info(LogCategory, $"hide of {free}x {item.Name} not sent, it could not be answered here: held");
            return new(verb, 0, held + free);
        }
        if (free > 0 && !SendGateOpen())
            return new(verb, 0, held, "the game is at a menu or prompt and would not take the command");
        if (free > 0) Offload(send, item.Number, name, Copies.Of(free, byHand: true));
        // Copies taken over may be free to go already: this room needn't be the
        // one that refused them.
        if (held > 0) RecheckHeldHides();
        return new(verb, Math.Max(free, 0), held);
    }

    // What a by-hand discard came to: the verb, the copies sent, the copies held
    // for when a hide can land, and, when nothing could go at all, why, in words
    // for the player.
    public readonly record struct DiscardResult(string Verb, int Sent, int Held, string? Blocked = null);

    // The engine's held copies wait for its switch. With the switch off they
    // would wait for good and still count as spoken for, so a by-hand discard of
    // the same item takes them over: the player has now asked for them himself.
    private int TakeOverEngineHold(ResolvedDiscard item, int count)
    {
        if (_isEnabled() || !_held.TryGetValue(item.Number, out HeldHide? held)) return 0;
        int taken = Math.Min(count, held.Waiting.Engine);
        if (taken <= 0) return 0;
        held.Waiting = new Copies(held.Waiting.ByHand + taken, held.Waiting.Engine - taken);
        _log?.Info(LogCategory, $"held hide of {taken}x {item.Name} taken over by hand: auto-discard is off");
        return taken;
    }

    // Copies of `name` whose hide is waiting for a room or has not landed since.
    public int HeldFor(string name)
        => _resolve(name) is { } item && _held.TryGetValue(item.Number, out HeldHide? held) ? held.Count : 0;

    // Every held hide with who sent it and what it is waiting on, and how many
    // hides are out with no answer yet, for the bug report.
    public IReadOnlyList<HeldHideInfo> HeldHides
        => _held.Values.Select(h => new HeldHideInfo(h.Name, h.Waiting.ByHand, h.Waiting.Engine, h.Out, WhyWaiting(h))).ToList();
    public int UnansweredHides => _hidesInFlight.Values.Sum(c => c.Total);
    public int UnansweredDrops => _inFlight.Values.Sum();
    // Of those, the engine's copies still waiting their turn in the pacer.
    public int QueuedCopies => _queued.Values.Sum();

    private string WhyWaiting(HeldHide held)
    {
        if (held.Waiting.Total == 0) return $"sent again in {RoomText(held.TriedIn)}, no answer yet";
        string refused = held.RefusedBeforeRoomKnown ? "refused before the room was known"
            : held.TriedIn is null ? "not sent yet"
            : $"last tried in {RoomText(held.TriedIn)}";
        if (KeptBackBy() is { } reason) return $"{refused}; waiting: {reason}";
        if (_room is { } here && here.Equals(held.TriedIn)) return $"{refused}; waiting for another room";
        if (held.Waiting.ByHand == 0 && !_isEnabled()) return $"{refused}; waiting: auto-discard is off";
        return $"{refused}; goes out on the next look";
    }

    private static string RoomText(RoomKey? room) => room is { } key ? key.ToString() : "an unknown room";

    private HeldHide HoldFor(ResolvedDiscard item)
    {
        if (!_held.TryGetValue(item.Number, out HeldHide? held))
            _held[item.Number] = held = new HeldHide { Name = item.Name };
        return held;
    }

    // Copies of an item in the pack that no discard is out for or waiting on.
    private int UnspokenCopies(int number) => Math.Max(0, CarriedCopies(number) - SpokenFor(number));

    // Call off held hides of an item: it was sold, dropped, hidden by hand, or
    // taken off the list it was being discarded from. The copy the hold was for
    // is gone or wanted again, and a retry would hide some other copy. A by-hand
    // surface calling off its own asks for byHandOnly, leaving the engine's.
    public void ReleaseHeld(string name, int count, bool byHandOnly = false)
    {
        if (count <= 0 || _resolve(name) is not { } item) return;
        if (!_held.TryGetValue(item.Number, out HeldHide? held)) return;
        Copies from = byHandOnly ? held.Waiting with { Engine = 0 } : held.Waiting;
        Copies released = from.Take(count).Taken;
        held.Waiting = new Copies(held.Waiting.ByHand - released.ByHand, held.Waiting.Engine - released.Engine);

        // A retry already out is called off too, or its refusal would put the copy
        // back to waiting. It stays counted as in flight until it is answered (the
        // hide is on the wire, and another for the same copy must not follow it),
        // but marked: a refusal of it holds nothing.
        Copies flight = _hidesInFlight.GetValueOrDefault(item.Number);
        int recalled = Math.Min(Math.Min(count - released.Total, held.Out), byHandOnly ? flight.ByHand : flight.Total);
        if (recalled > 0)
        {
            held.Out -= recalled;
            _recalled[item.Number] = _recalled.GetValueOrDefault(item.Number) + recalled;
        }

        int calledOff = released.Total + recalled;
        if (calledOff == 0) return;
        if (held.Count == 0)
            EndHeld(item.Number, $"held hide of {held.Name} called off ({calledOff})");
        else
            _log?.Info(LogCategory, $"held hide of {held.Name} called off ({calledOff}), {held.Count} still held");
    }

    // Copies of an item a discard is already out for, or waiting on a room for.
    private int SpokenFor(int number)
        => _inFlight.GetValueOrDefault(number) + _hidesInFlight.GetValueOrDefault(number).Total
           + (_held.TryGetValue(number, out HeldHide? held) ? held.Waiting.Total : 0);

    // Copies of an item in the pack: an entry is a stack with its count in front
    // ("3 moonstone"). AppServices.CountInPack does the same sum off the snapshot;
    // this one goes through the resolver the class is handed.
    private int CarriedCopies(int number)
    {
        int copies = 0;
        foreach (string entry in PackEntries())
            if (_resolve(entry) is { } item && item.Number == number)
                copies += CountedCommand.SplitLeadingCount(entry.Trim()).Count;
        return copies;
    }

    // Paradigm offloads the pile in one `{verb} N <item>`; Stock sends one per
    // copy. A hide is counted until its "You hid X." and registered so the
    // transaction log can tell it from a stash; a drop is counted until its "You
    // dropped X.", a by-hand one too, or the engine would take the copies a
    // by-hand drop is already out for as still to be discarded.
    private void Offload(Action<string> send, int number, string name, Copies copies)
    {
        if (copies.Total <= 0) return;
        NoteTraffic();
        _sentAs[number] = name;
        if (HideMode)
        {
            _hidesInFlight[number] = _hidesInFlight.GetValueOrDefault(number).Plus(copies);
            _suppressLog[number] = _suppressLog.GetValueOrDefault(number) + copies.Total;
        }
        else
        {
            _inFlight[number] = _inFlight.GetValueOrDefault(number) + copies.Total;
        }
        CountedCommand.Emit(send, DiscardVerb, copies.Total, name, _isParadigm());
    }

    // Clear a pending drop when our own "You dropped X." confirmation arrives.
    // PlayerDrops is a combined pattern (Groups[0] = the other-player name for
    // "<name> drops X.", empty for the self "You dropped X." branch); only the
    // self branch confirms a command we sent. A drop of an item whose hide is held
    // calls the hold off.
    private void OnDropLine(MatchResult m)
    {
        if (m.Groups.Count < 2) return;
        if (!string.IsNullOrEmpty(m.Groups[0])) return;   // another player's drop
        (int count, string name) = CountedCommand.SplitLeadingCount(m.Groups[1]);
        if (_resolve(name) is not { } item) return;
        if (Reduce(_inFlight, item.Number, count)) NoteTraffic();
        ReleaseHeld(item.Name, count);
    }

    // Clear a pending hide when our own "You hid X." confirmation arrives.
    // UserHides is a self-only single-group pattern (Groups[0] = the hidden item),
    // so unlike PlayerDrops there's no other-player branch to filter out. Coin
    // hides ("You hid 10 gold.") don't resolve to an item and fall through.
    private void OnHideLine(MatchResult m)
    {
        if (m.Groups.Count < 1) return;
        (int count, string name) = CountedCommand.SplitLeadingCount(m.Groups[0]);
        if (_resolve(name) is not { } item) return;
        int ours = TakeFlight(item.Number, count).Total;
        if (ours > 0) NoteTraffic();
        _held.TryGetValue(item.Number, out HeldHide? held);

        // A landing settles a retry still wanted first; what is left over may be
        // one that was called off while it was out.
        int retried = Math.Min(held?.Out ?? 0, ours);
        if (held is not null) held.Out -= retried;
        Reduce(_recalled, item.Number, ours - retried);
        if (held is null) return;

        // A hide nobody here sent (typed, Hide All) put a held copy away itself.
        if (count > ours) ReleaseHeld(item.Name, count - ours);
        if (_held.ContainsKey(item.Number) && held.Count == 0)
            EndHeld(item.Number, $"held hide of {held.Name} landed in {_room}");
    }

    // The room's floor is full. No "You dropped X." will come, so the drop comes
    // off the count at once, and the item is not dropped here again: this room
    // would only refuse it. Only a drop counted here is acted on; Roomba's and a
    // typed one are their sender's.
    private void OnDropRefusedLine(MatchResult m)
    {
        if (m.Groups.Count < 1) return;
        (int count, string name) = CountedCommand.SplitLeadingCount(m.Groups[0]);
        if (_resolve(name) is not { } item || !Reduce(_inFlight, item.Number, count)) return;
        NoteTraffic();
        _dropRefusedIn[item.Number] = _room;
        _log?.Info(LogCategory, $"drop of {item.Name} refused, no room here: not dropped again in {RoomText(_room)}");
        // The rest of its pile still waiting would only be refused one by one.
        RecallPile(item.Number, item.Name, int.MaxValue, "the room's floor is full");
    }

    // The room has no room left for hidden items. The copy stays in the pack and
    // waits for the next room; this one would only refuse it again. Only a hide
    // registered as a discard is carried on with: a typed hide, a stash room's or
    // Hide All's belongs to whoever sent it. Its ledger registration goes with the
    // refusal (no "You hid X." will claim it) and the retry takes out a fresh one.
    private void OnHideRefusedLine(MatchResult m)
    {
        if (m.Groups.Count < 1) return;
        string token = m.Groups[0];
        if (!TryConsumeSuppressedHide(token)) return;
        (int count, string name) = CountedCommand.SplitLeadingCount(token);
        if (_resolve(name) is not { } item) return;
        Copies refused = TakeFlight(item.Number, count);
        // A retry called off while it was out is not held again.
        int calledOff = Math.Min(refused.Total, _recalled.GetValueOrDefault(item.Number));
        if (calledOff > 0)
        {
            Reduce(_recalled, item.Number, calledOff);
            refused = refused.Take(calledOff).Left;
            _log?.Info(LogCategory, $"hide of {item.Name} refused, no room here: it had been called off, not held");
        }
        if (refused.Total == 0) return;
        if (!HideMode)
        {
            _log?.Info(LogCategory, $"hide of {item.Name} refused, no room here: not held, hide when discarding is off");
            return;
        }
        HeldHide held = HoldFor(item);
        held.Out -= Math.Min(held.Out, refused.Total);
        held.Waiting = held.Waiting.Plus(refused);
        // The rest of the engine's pile still waiting would only be refused one by
        // one: it comes back and is held with the copy that was.
        int unsent = RecallPile(item.Number, item.Name, int.MaxValue, "the room has no room for hidden items");
        held.Waiting = held.Waiting.Plus(Copies.Of(unsent, byHand: false));
        RecallRetries(item.Number, item.Name, int.MaxValue, "the room has no room for hidden items");
        held.TriedIn = _room;
        held.RefusedBeforeRoomKnown = _room is null;
        held.Kept = UnspokenCopies(item.Number);
        _log?.Info(LogCategory, $"hide of {item.Name} refused, no room here: {held.Waiting.Total} held for the next room");
        HideRefused?.Invoke(item.Name);
    }

    // The character stands in a room, by the room tracker's confirmed reading. A
    // redisplay of the same room is not an arrival.
    public void OnRoomEntered(RoomKey room)
    {
        if (_room is { } here && here.Equals(room)) return;
        bool wasUnknown = _room is null;
        _room = room;
        // A hide refused before any room was confirmed was refused by this one.
        foreach (HeldHide held in _held.Values)
        {
            if (!held.RefusedBeforeRoomKnown) continue;
            held.RefusedBeforeRoomKnown = false;
            held.TriedIn = room;
        }
        // The same for a drop; any other refused drop was refused somewhere else,
        // and this room may have the floor for it.
        foreach (int number in _dropRefusedIn.Keys.ToList())
        {
            if (wasUnknown && _dropRefusedIn[number] is null) _dropRefusedIn[number] = room;
            else _dropRefusedIn.Remove(number);
        }
        // A new room is also where the engine's own discards get another look: one
        // kept back for the dark, or refused next door, may go here.
        OnInventoryChanged();
    }

    // The master switch (true = off). The engine's own piles follow it through
    // isEnabled; this is for the held hides, which answer to neither.
    public Func<bool>? MasterSwitchOff { get; set; }

    // What keeps every held hide back right now, or null when they may go.
    private string? KeptBackBy()
    {
        // A retry is the client's own doing even when the hide it retries was
        // asked for by hand, so the master switch stops it. The engine's switch
        // does not (see ReviewQueued), which is why this is asked here.
        if (MasterSwitchOff?.Invoke() == true) return "the master switch is off";
        if (_awaitingRead) return "the pack hasn't been read again yet";
        if (_room is null) return "the room isn't known";
        if (_wireSender is null) return "not connected";
        if (SuppressDuringSweep()) return "a Roomba sweep is running";
        if (!SendGateOpen()) return "the send gate is up";
        if (!CanSeeToHide()) return "the character can't see";
        return null;
    }

    // Send each held hide once in the room the character stands in, unless that
    // room is the one that refused it. A refusal here holds it again for the room
    // after. Safe to call whenever something that kept the hides back may have
    // cleared.
    public void RecheckHeldHides()
    {
        if (_held.Count == 0) return;
        if (KeptBackBy() is { } reason)
        {
            if (_keptBack == (_room, reason)) return;
            _keptBack = (_room, reason);
            _log?.Info(LogCategory, $"held hides kept back in {RoomText(_room)}: {reason}");
            return;
        }
        if (_room is not { } here) return;
        _keptBack = null;

        List<string> commands = new();
        foreach ((int number, HeldHide held) in _held.ToList())
        {
            // The room is kept per item, not per sender: engine copies left waiting
            // for the switch when the by-hand ones went out here skip this room too.
            if (held.Waiting.Total == 0 || here.Equals(held.TriedIn)) continue;

            ResolvedDiscard? item = _resolve(held.Name);
            // The engine's copies are its own only while the item is still flagged.
            if (held.Waiting.Engine > 0 && item is not { Discard: true })
            {
                _log?.Info(LogCategory, $"held hide of {held.Name}: no longer flagged for auto-discard, {held.Waiting.Engine} let go");
                held.Waiting = held.Waiting with { Engine = 0 };
            }
            // Not for a copy that has left the pack some other way, and never into
            // the copies that weren't to be discarded when the hide was refused.
            int carried = CarriedCopies(number) - _inFlight.GetValueOrDefault(number)
                          - _hidesInFlight.GetValueOrDefault(number).Total;
            int free = carried - held.Kept;
            if (held.Waiting.Total > free)
            {
                _log?.Info(LogCategory, $"held hide of {held.Name}: {held.Waiting.Total - Math.Max(free, 0)} no longer in the pack to hide, forgotten");
                held.Waiting = held.Waiting.Take(free).Taken;
            }
            // The engine also leaves the item's keep amount, as it is set now.
            if (held.Waiting.Engine > 0 && item is { } flagged)
            {
                int engineFree = Math.Max(0, carried - PackFloor(flagged) - held.Waiting.ByHand);
                if (held.Waiting.Engine > engineFree)
                {
                    _log?.Info(LogCategory, $"held hide of {held.Name}: {held.Waiting.Engine - engineFree} let go, within the keep amount of {flagged.KeepCount}");
                    held.Waiting = held.Waiting with { Engine = engineFree };
                }
            }
            if (held.Count == 0) { EndHeld(number, $"held hide of {held.Name} given up"); continue; }

            // The engine's copies wait for its switch; a by-hand discard never did.
            Copies due = _isEnabled() ? held.Waiting : held.Waiting with { Engine = 0 };
            if (due.Total == 0) continue;

            held.Waiting = new Copies(held.Waiting.ByHand - due.ByHand, held.Waiting.Engine - due.Engine);
            held.Out += due.Total;
            held.TriedIn = here;
            _log?.Info(LogCategory, $"retrying held hide of {due.Total}x {held.Name} in {here}");
            Offload(commands.Add, number, held.Name, due);
        }
        SendAll(commands, _retryOwner, RetryMayGo);
    }

    // A full `i` listing landed. A discard for a copy the pack no longer holds
    // can't be answered, so nothing stays counted past what is carried. And once a
    // whole read-to-read stretch passes with no discard sent or answered, whatever
    // is still counted never will be (a refusal with no line of its own, a counted
    // command answered for fewer, a command the game dropped for coming too fast):
    // forget it, or it would block every later discard of that item and swallow a
    // later stash's ledger row. A retry lost that way goes back to waiting for a
    // room.
    //
    // Only a read that lands with nothing left waiting in the pacer, and long
    // enough after the last send or answer for the game to have answered, opens or
    // closes such a stretch. When a listing was asked for can't be known, and one
    // asked for before a pile went out lands ahead of the pile's first answer:
    // taken for quiet, it had the pile forgotten and sent a second time.
    public void OnFullInventoryRead()
    {
        bool wasAwaitingRead = _awaitingRead;
        _awaitingRead = false;
        foreach (int number in _inFlight.Keys.Concat(_hidesInFlight.Keys).Concat(_suppressLog.Keys)
                     .Concat(_held.Keys).Distinct().ToList())
        {
            int carried = CarriedCopies(number);
            Cap(_inFlight, number, carried);
            CapFlight(number, carried);
            Cap(_suppressLog, number, carried);
            Cap(_recalled, number, _hidesInFlight.GetValueOrDefault(number).Total);
            if (!_held.TryGetValue(number, out HeldHide? held)) continue;
            held.Out = Math.Min(held.Out, _hidesInFlight.GetValueOrDefault(number).Total);
            held.Waiting = held.Waiting.Take(carried - held.Kept - held.Out).Taken;
            if (held.Count == 0) EndHeld(number, $"held hide of {held.Name} forgotten: no longer carried");
        }

        // Discards still queued in the pacer were counted when handed over. They
        // are unsent, not unanswered, and forgetting them would let a second press
        // send for copies the queue is about to take.
        bool quiet = !SendsQueued() && _now() - _lastTrafficAt >= AnswerSettle;
        if (quiet && _quietReadSeen
            && (_inFlight.Count > 0 || _hidesInFlight.Count > 0 || _suppressLog.Count > 0))
        {
            foreach ((int number, HeldHide held) in _held.ToList())
            {
                if (held.Out == 0) continue;
                Copies back = TakeFlight(number, held.Out).Plus(held.Waiting)
                    .Take(CarriedCopies(number) - held.Kept).Taken;
                held.Out = 0;
                held.Waiting = back;
                if (held.Count == 0) EndHeld(number, $"held hide of {held.Name} forgotten: no longer carried");
                else _log?.Info(LogCategory, $"retry of {held.Name} never answered: {back.Total} held again for the next room");
            }
            if (_inFlight.Count + _hidesInFlight.Count > 0)
                _log?.Info(LogCategory,
                    $"{UnansweredDrops} drop(s) and {UnansweredHides} hide(s) never answered: no longer counted as on their way");
            _inFlight.Clear();
            _hidesInFlight.Clear();
            _suppressLog.Clear();
            _recalled.Clear();
            _queued.Clear();
        }
        _quietReadSeen = quiet;
        // This read is what everything was waiting on. Its own Changed came before
        // it and was passed over, so the pack is looked at here.
        if (wasAwaitingRead) OnInventoryChanged();
    }

    // Nothing sent before this point will be answered (a death empties the pack,
    // a dropped connection loses the replies, another character has its own pack),
    // and the carried list is stale until it is read again, so nothing more goes
    // out before that. A dropped connection leaves the pack as it was, so with
    // keepHeld the refused copies go on waiting, each still tied to the room that
    // refused it: the character comes back into that same room.
    public void Reset(string reason, bool keepHeld = false)
    {
        CancelQueuedSends?.Invoke();
        _keptBack = null;
        _awaitingRead = true;
        _recalled.Clear();
        _dropRefusedIn.Clear();
        _sentAs.Clear();
        int unanswered = UnansweredDrops + UnansweredHides;
        if (unanswered > 0) _log?.Info(LogCategory, $"{reason}: {unanswered} unanswered discard(s) forgotten");
        if (keepHeld)
        {
            foreach ((int number, HeldHide held) in _held)
            {
                held.Waiting = held.Waiting.Plus(TakeFlight(number, held.Out));
                held.Out = 0;
            }
        }
        else
        {
            EndAllHeld(reason);
        }
        _inFlight.Clear();
        _hidesInFlight.Clear();
        _suppressLog.Clear();
        _queued.Clear();
        _room = null;
        _quietReadSeen = false;
    }

    // One item's hold is over: say why, and tell whoever is showing it.
    private void EndHeld(int number, string message)
    {
        if (!_held.Remove(number, out HeldHide? held)) return;
        _log?.Info(LogCategory, message);
        HeldHideEnded?.Invoke(held.Name);
    }

    private void EndAllHeld(string reason)
    {
        foreach ((int number, HeldHide held) in _held.ToList())
            EndHeld(number, $"{reason}: held hide of {held.Count} {held.Name} given up");
    }

    // Take up to `count` hides of an item off the in-flight count; what came off.
    private Copies TakeFlight(int number, int count)
    {
        if (!_hidesInFlight.TryGetValue(number, out Copies flight)) return default;
        (Copies taken, Copies left) = flight.Take(count);
        if (left.Total == 0) _hidesInFlight.Remove(number);
        else _hidesInFlight[number] = left;
        return taken;
    }

    // The same for a pile of the engine's own taken back from the pacer: its copies
    // come off before any by-hand ones.
    private void TakeFlightEngineFirst(int number, int count)
    {
        if (!_hidesInFlight.TryGetValue(number, out Copies flight)) return;
        int engine = Math.Min(flight.Engine, count);
        Copies left = new(Math.Max(0, flight.ByHand - (count - engine)), flight.Engine - engine);
        if (left.Total == 0) _hidesInFlight.Remove(number);
        else _hidesInFlight[number] = left;
    }

    private void CapFlight(int number, int max)
    {
        if (_hidesInFlight.TryGetValue(number, out Copies flight) && flight.Total > max)
            TakeFlight(number, flight.Total - max);
    }

    // Lower a count by up to `by`, dropping the entry at zero. False when there
    // was nothing to lower.
    private static bool Reduce(Dictionary<int, int> counts, int number, int by)
    {
        if (!counts.TryGetValue(number, out int n) || n <= 0) return false;
        if (n <= by) counts.Remove(number);
        else counts[number] = n - by;
        return true;
    }

    private static void Cap(Dictionary<int, int> counts, int number, int max)
    {
        if (!counts.TryGetValue(number, out int n) || n <= max) return;
        if (max <= 0) counts.Remove(number);
        else counts[number] = max;
    }

    // The transaction-history forwarder calls this on every "You hid X." to ask
    // whether the hide was a discard (skip the stash ledger) or a genuine manual /
    // stash-room hide (record it). A discard's hide was registered in _suppressLog
    // at send time; claim it here (returns true). A hide we never initiated isn't
    // registered (returns false), so it still records.
    public bool TryConsumeSuppressedHide(string itemName)
    {
        (int count, string name) = CountedCommand.SplitLeadingCount(itemName);
        if (_resolve(name) is not { } item) return false;
        if (!Reduce(_suppressLog, item.Number, count)) return false;
        NoteTraffic();
        return true;
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
        _dropSub.Dispose();
        _hideSub.Dispose();
        _hideRefusedSub.Dispose();
        _dropRefusedSub.Dispose();
    }
}
