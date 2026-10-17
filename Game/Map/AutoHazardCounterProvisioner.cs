using MudPlay.Services;

namespace MudPlay.Game.Map;

// Keeps a checkspell hazard buff up while the character is in hazard country.
//
// Some room hazards (the Scorching Desert's heat) are survived not by HOLDING a
// counter but by an active buff the player raises with `use <item>`: the desert
// waterskin `use`s to cast buff 711, safe only while that buff is up. Carrying the
// source item is enough to let the route pass the hazard gate
// (RoomHazardIndex.RoomHazard.IsSatisfiedBy checks carrying), but the buff must
// actually be RAISED, or the route walks straight into the damage — the "it required
// a waterskin but never used it" report.
//
// The buff is tracked quietly (user, 2026-10-10): when it went on, how long it
// lasts by the game data, and so when it runs out. It is raised again shortly before
// that and not sooner, since every `use` spends a charge, and only while it matters:
// standing in a room the buff counters, or with one a few planned steps ahead.
//
//  • On since — a `use` this engine sends stamps it at the send, and the buff
//    spell's own line (the swig) stamps it again when it shows, for a `use` typed by
//    hand as much as for ours. The waterskin buff has no wear-off line, so the end
//    is worked out from the duration. A death, a room whose spell strips it, negate
//    magic, a new profile and a new game-data set forget it: unknown counts as off
//    when a hazard room is next. A dropped link does not: "buffs survive
//    disconnects, relogs", and "as soon as we've disconnected all timers need to be
//    paused until we're back in the game, then they all resume" (user, 2026-10-10),
//    so the clock stops when the game is left and runs on from the first prompt back
//    in it with the time it had left (Pause / Resume). What it had left is kept in
//    the character's profile meanwhile and put back by a client started later
//    (SnapshotForSave / Restore, driven with the cast pass's timers by
//    Game.Spells.BuffTimerKeeper).
//  • The window — with Auto-Sneak on, inside RefreshWindowSeconds of the end, and
//    with a countered room here or a few planned steps ahead, the refresh is offered
//    to the between-round cast scheduler (CastingDirector.SetClientUseSource), which
//    gives it the round's one cast and holds it for sneak keeping like any buff: it
//    waits for a room with no NPCs, goes out there, and the re-sneak that follows is
//    answered before the next step. The early window and the look ahead exist to buy
//    that search and nothing else, so with Auto-Sneak off there is neither: "not too
//    soon before it wears off or we'll be using a lot more waterskins than we should
//    be" (user, 2026-10-10). The refresh then keeps to the last call.
//  • The last call — inside ForcedLeadSeconds of the end (or with the buff off)
//    it can't wait any longer. The step into a countered room sends it where the
//    character stands (OnApproachingRoom, asked ahead of the sneak check), a
//    follower's arrival does (OnArrivedInRoom), and the scheduler is told it must
//    go now. A sneak ended that way is reported (SneakSpentHere) so the reactions
//    to a lost sneak apply as for any other.
//  • Reactive (message) — the timer only estimates the lapse. When the buff drops
//    early the room casts its lapse-damage spell and the game prints that spell's
//    message ("you suffer in the desert heat... you need water, soon!"). Seeing it,
//    the buff is counted as off whatever the clock said, and on a walk we fire
//    exactly ONE `use` to re-raise. The trigger is the game-data message record
//    linked to the hazard's lapse spell (RoomHazardIndex.BuffCounter.LapseSpell),
//    not hardcoded realm text; where the data doesn't tie that spell to the room
//    (Paradigm's desert) RoomSpellDamageClassifier.ConfirmedFollowOn does. If a SECOND lapse
//    prompt arrives before the swig, the `use` drew nothing (out of charges /
//    waterskins) and we HALT the walk rather than march deeper into a hazard we can
//    no longer counter.
//
// A party FOLLOWER runs no walk of its own — the leader's route carries it — so it
// raises the buff on arriving in a hazard room, and the window and the lapse prompt
// work for it as on a walk.
//
// No toggle of its own: surviving a hazard room the route already commits to
// walking is not opt-in (mirrors auto-light's "leave it off if you don't want it" —
// here the equivalent is simply not routing through the hazard). The master switch
// is the one thing that stops it, as a survival item too: "with the master switch
// off, it shouldnt automatically swap gear" (user, 2026-10-10). Off, no `use`, no
// re-raise and no "out of" say goes out, and no latch or timer is stamped, so
// when the switch comes back on the next arrival or lapse acts afresh. It only
// ever acts when a checkspell hazard and a carried source item coincide: on a live
// walk (ours, or the leader's we're following), and for a character standing in a
// countered room with nothing moving it, where the cast pass keeps the buff up by
// the same clock ("if auto master toggle is on, yes, if off, no", user,
// 2026-10-10). The "out of" say and the halt stay a walk's.
public sealed class AutoHazardCounterProvisioner
{
    // True (and counted) while the master switch is off; asked right before
    // anything is sent. Null = never off.
    public Func<bool>? MasterSwitchOff { get; set; }

    // LogService category — [HazardCounter] rows per decision.
    public const string LogCategory = "HazardCounter";

    // With Auto-Sneak on the refresh is on offer this long before the buff runs out.
    // One charge buys 1800 s of waterskin, so a refresh at the very start of the
    // window gives up a thirtieth of one; twelve 5 s combat rounds is room to finish
    // a fight, find a room with no NPCs on a sneaked walk and get the round's one
    // cast. With Auto-Sneak off there is no search to buy, and no window.
    public const int RefreshWindowSeconds = 60;

    // Inside this of the end the `use` goes out wherever the character stands: three
    // rounds, one for a cast already made this round, one for the `use`, one for a
    // retry the game turned away. A step into a countered room never lands in the gap
    // between the lapse and the refresh.
    public const int ForcedLeadSeconds = 15;

    // With Auto-Sneak on, how many planned steps ahead a countered room makes the buff
    // matter. Far enough to find a room with no NPCs before the edge, near enough
    // that a route passing by spends nothing. With Auto-Sneak off nothing is looked
    // ahead for: the first drink goes out at the edge, on the step's last call.
    public const int LookaheadSteps = 5;

    // Fallback refresh interval when the buff's duration isn't in the data (Dur 0):
    // still re-use periodically rather than once-and-never so an un-timed counter
    // doesn't silently lapse.
    private const int UnknownDurationRefreshSeconds = 60;

    // A refusal line is taken as the answer to our `use` only this soon after it.
    private static readonly TimeSpan AnswerWindow = TimeSpan.FromSeconds(5);

    private readonly Func<RoomKey, Room?> _resolveRoom;
    private readonly Func<int, RoomHazardIndex.RoomHazard?> _hazardForSpell;
    private readonly Func<int, int> _carriedCount;
    private readonly Func<int, string?> _itemName;
    // Resolve a spell number to a predicate that recognises that spell's own
    // game-data message on a server line (lapse prompt for LapseSpell, swig
    // confirmation for BuffSpell). Null → the reactive path stays inert and only
    // the timer keeps the buff up.
    private readonly Func<int, Func<string, bool>?>? _messageMatcherForSpell;
    private readonly Func<bool> _walkActive;
    private readonly Func<bool> _followingLeader;
    private readonly Action<string>? _haltWalk;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();

    // The room we stand in, then the rooms the walk or loop plans to enter next (at
    // most LookaheadSteps of them). Null → the window is never offered and only the
    // step into a countered room, an arrival and the lapse prompt act.
    private readonly Func<IReadOnlyList<RoomKey>>? _roomsAhead;
    // Every buff counter the active game-data set knows, so the buff's line is read
    // wherever it shows. Null → only the counter last approached is watched.
    private readonly Func<IEnumerable<RoomHazardIndex.BuffCounter>>? _allCounters;
    // The character is sneaking or hidden, and that is being kept (SneakGuard.Holds):
    // a `use` now spends it. Both halves: the guard also holds for any move in flight
    // with Auto-Sneak on, sneaking or not, and a sneak that had already failed is not
    // one this `use` lost.
    private readonly Func<bool>? _sneakKept;
    // Auto-Sneak is on: the refresh gets its window and its look ahead, to find a
    // room with no NPCs in. Null → off.
    private readonly Func<bool>? _autoSneakOn;
    // RoomBuffStripIndex.StripsBuff: a room's cast-on-enter spell (first) takes the
    // buff (second) off. Null → no room is known to.
    private readonly Func<int, int, bool>? _roomStripsBuff;
    // RoomBuffStripIndex.CommandStripsBuff: in a room with this command textblock, the
    // command's line casts a spell that takes the buff off. Null → none is known to.
    private readonly Func<int, string, int, bool>? _commandStripsBuff;
    // Charges left in the carried item, null when the client doesn't know.
    private readonly Func<int, int?>? _chargesLeft;
    // The engine send gate is up (a password prompt, mortally wounded): a send now
    // would be dropped, and must not be stamped as made.
    private readonly Func<bool>? _sendBlocked;

    // Per buff spell: when it went on, and whether its own line said so or we only
    // know the `use` went out.
    private readonly Dictionary<int, (DateTimeOffset OnSince, bool Confirmed)> _on = new();

    // Our last `use`, until its answer: what the buff's tracking was before it, to
    // put back if the game turns the `use` away.
    private SentUse? _pending;

    //   Before       — the buff's tracking before the `use`, put back if it is refused.
    //   CouldNotWait — it went out at the last call, not inside a window it could
    //                  have waited in.
    //   StepInto     — it went out ahead of a step into this room, held for its answer.
    private readonly record struct SentUse(
        int BuffSpell, int Item, DateTimeOffset SentAt, (DateTimeOffset OnSince, bool Confirmed)? Before,
        bool CouldNotWait, Room? StepInto);

    // A `use` that could not wait drew `You have already cast a spell this round!`.
    // The game takes one such cast a round, so asking again before the round turns
    // only draws the line again (and with Auto-Sneak on, a fresh `sn` each time).
    // Until the cast pass has the next round's cast for it (FireDue), or the round
    // has passed, no other road sends it.
    private (RoomHazardIndex.BuffCounter Counter, Room? StepInto, DateTimeOffset Until)? _roundWait;

    // Longer than the cast pass keeps its slot spent (5 s) by its 1 s heartbeat.
    private static readonly TimeSpan RoundWait = TimeSpan.FromSeconds(6);

    // How long a step waits for the answer to the `use` sent ahead of it: the wait a
    // step gives a `sn`.
    private static readonly TimeSpan StepAnswerCap = TimeSpan.FromSeconds(3);

    // The step into a countered room waits on MovementCoordinator.HazardBuffGate for
    // the answer to its `use`. Unbound, nothing is held.
    private MovementCoordinator? _coordinator;
    private Action<TimeSpan, Action>? _schedule;
    private bool _holdingStep;
    private int _holdSeq;

    // Per source item: the game refused a `use` of it for a reason that holds (not
    // carried, no uses left, not usable), with the count carried at the time. Nothing
    // more is sent for the item until that count changes or the buff's line shows.
    private readonly Dictionary<int, int> _refusedAtCount = new();

    // When the link dropped, null while it is up and we are back in the game. The
    // buffs' clocks stand still from then, and nothing is sent.
    private DateTimeOffset? _pausedAt;
    // What stopped them, for the bug report.
    private string _pausedWhy = string.Empty;

    // The buffs carried over time out of the game (across a pause, or from a saved
    // profile), each with the start its clock had when it was last carried. A `use`
    // or the buff's own line sets a new start, which is how a carried buff is told
    // from one raised since.
    private readonly Dictionary<int, DateTimeOffset> _carried = new();

    // Buff-line watchers for every known counter, built on first use and dropped by
    // Forget so a new game-data set rebuilds them.
    private List<(int BuffSpell, Func<string, bool> Match)>? _buffLines;

    // The hazard counter we last approached, with its compiled lapse / swig line
    // predicates — armed so a lapse prompt fired mid-room (not just on a step) is
    // still caught. Null when we're not in/around a checkspell hazard.
    private RoomHazardIndex.BuffCounter? _activeCounter;
    private Func<string, bool>? _lapseMatch;
    private Func<string, bool>? _swigMatch;

    // A `use` was sent and its swig confirmation hasn't been seen yet. A lapse
    // prompt arriving while this is set means the `use` produced nothing — out of
    // charges — so the walk halts instead of re-firing into the void.
    private bool _awaitingSwig;

    // We've already told the room we're out of the buff's source item. Cleared once a
    // `use` goes out again (a fresh one picked up / handed over), so a later run-out
    // is announced anew — but a hazard stretch doesn't repeat it every lapse tick.
    private bool _announcedOut;

    // Said once per refresh: that it is due, and that it is waiting on the sneak.
    private enum DueSaid { Nothing, Due, CannotWait }
    private DueSaid _dueSaid;
    private bool _waitLogged;

    // A `use` went out, by whichever road. The cast scheduler takes it for the
    // round's between-round cast, so the fight resumes and the sneak is taken again.
    public event Action? UseSent;

    // A `use` went out although a sneak was being kept, because it could wait no
    // longer. The sneak is gone in this room, for whatever reacts to that.
    public event Action? SneakSpentHere;

    public AutoHazardCounterProvisioner(
        Func<RoomKey, Room?> resolveRoom,
        Func<int, RoomHazardIndex.RoomHazard?> hazardForSpell,
        Func<int, int> carriedCount,
        Func<int, string?> itemName,
        Func<int, Func<string, bool>?>? messageMatcherForSpell = null,
        Func<bool>? walkActive = null,
        Action<string>? haltWalk = null,
        Func<DateTimeOffset>? now = null,
        LogService? log = null,
        Func<bool>? followingLeader = null,
        Func<IReadOnlyList<RoomKey>>? roomsAhead = null,
        Func<IEnumerable<RoomHazardIndex.BuffCounter>>? allCounters = null,
        Func<bool>? sneakKept = null,
        Func<int, int?>? chargesLeft = null,
        Func<bool>? sendBlocked = null,
        Func<bool>? autoSneakOn = null,
        Func<int, int, bool>? roomStripsBuff = null,
        Func<int, string, int, bool>? commandStripsBuff = null)
    {
        ArgumentNullException.ThrowIfNull(resolveRoom);
        ArgumentNullException.ThrowIfNull(hazardForSpell);
        ArgumentNullException.ThrowIfNull(carriedCount);
        ArgumentNullException.ThrowIfNull(itemName);
        _resolveRoom = resolveRoom;
        _hazardForSpell = hazardForSpell;
        _carriedCount = carriedCount;
        _itemName = itemName;
        _messageMatcherForSpell = messageMatcherForSpell;
        _walkActive = walkActive ?? (() => true);
        _followingLeader = followingLeader ?? (() => false);
        _haltWalk = haltWalk;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _log = log;
        _roomsAhead = roomsAhead;
        _allCounters = allCounters;
        _sneakKept = sneakKept;
        _chargesLeft = chargesLeft;
        _sendBlocked = sendBlocked;
        _autoSneakOn = autoSneakOn;
        _roomStripsBuff = roomStripsBuff;
        _commandStripsBuff = commandStripsBuff;
    }

    // Bind the wire-sender — the gate-wrapped engine pipeline from
    // MainWindowViewModel. Until bound, a `use` goes nowhere.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // Bind the gate the step is held on, and the one-shot timer its caps run on.
    public void SetStepHold(MovementCoordinator coordinator, Action<TimeSpan, Action> schedule)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(schedule);
        _coordinator = coordinator;
        _schedule = schedule;
    }

    // The step ahead waits for a `use`'s answer, or for the round's cast. Asked by
    // both engines' ready checks right after the step's last call.
    public bool HoldingStep => _holdingStep;

    private void HoldStep(TimeSpan cap, string why)
    {
        if (_coordinator is null) return;
        if (!_holdingStep)
        {
            _holdingStep = true;
            _coordinator.AssertGate(MovementCoordinator.HazardBuffGate, nameof(AutoHazardCounterProvisioner), why);
        }
        int seq = ++_holdSeq;
        _schedule?.Invoke(cap, () =>
        {
            if (seq != _holdSeq) return;
            _roundWait = null;
            ReleaseStep("no answer in time — moving on");
        });
    }

    private void ReleaseStep(string why)
    {
        if (!_holdingStep) return;
        _holdingStep = false;
        _holdSeq++;
        _coordinator?.ClearGate(MovementCoordinator.HazardBuffGate, nameof(AutoHazardCounterProvisioner), why);
    }

    // True while a round refusal is being waited out.
    private bool InRoundWait()
    {
        if (_roundWait is not { } wait) return false;
        if (_now() < wait.Until) return true;
        _roundWait = null;
        return false;
    }

    // ----- the buff's clock ------------------------------------------------

    // How long the buff lasts: the spell's Dur in spell rounds at 3 s each, taken at
    // the spell's own level (RoomHazardIndex.ReadSpellDurations), so the shortest it
    // can be. A buff with no duration in the data is given the fallback interval.
    private static TimeSpan DurationOf(in RoomHazardIndex.BuffCounter counter) =>
        TimeSpan.FromSeconds(counter.DurationSeconds > 0
            ? counter.DurationSeconds
            : UnknownDurationRefreshSeconds + ForcedLeadSeconds);

    // Time left on the buff, null when it isn't known to be on.
    private TimeSpan? RemainingOf(in RoomHazardIndex.BuffCounter counter, DateTimeOffset now) =>
        _on.TryGetValue(counter.BuffSpell, out (DateTimeOffset OnSince, bool Confirmed) on)
            ? on.OnSince + DurationOf(counter) - now
            : null;

    // Off, unknown or about to run out: the `use` can't wait.
    private bool MustUseNow(in RoomHazardIndex.BuffCounter counter, DateTimeOffset now) =>
        RemainingOf(counter, now) is not { } left || left <= TimeSpan.FromSeconds(ForcedLeadSeconds);

    // Inside the refresh window. A buff of unknown length has no window before its
    // last call: there is no end to count back from.
    private bool InWindow(in RoomHazardIndex.BuffCounter counter, DateTimeOffset now) =>
        RemainingOf(counter, now) is not { } left
        || left <= TimeSpan.FromSeconds(counter.DurationSeconds > 0 ? RefreshWindowSeconds : ForcedLeadSeconds);

    // The moment the buffs' clocks are read at: the drop, while the link is down.
    private DateTimeOffset Clock() => _pausedAt ?? _now();

    // The character is out of the game: the link dropped, or it left for the board's
    // menu. Its buffs stay on it and stop running down until it is back, so the
    // clocks stop here. The answer to a `use` still in flight went with it.
    public void Pause(string why = "the link dropped")
    {
        if (_pausedAt is not null) return;
        _pausedAt = _now();
        _pausedWhy = why;
        foreach ((int buffSpell, (DateTimeOffset onSince, bool _)) in _on) _carried[buffSpell] = onSince;
        _pending = null;
        _awaitingSwig = false;
        _roundWait = null;
        ReleaseStep(why);
        if (_on.Count > 0)
            _log?.Info(LogCategory, $"buff clock paused ({why}) — {TimeLeftText()}, held until back in the game");
    }

    // What each tracked buff has left, for the profile (Game.Spells.BuffTimerKeeper).
    // One whose length the game data doesn't give has no time left to tell.
    public List<Models.Profile.SavedHazardBuff> SnapshotForSave()
    {
        DateTimeOffset now = Clock();
        List<Models.Profile.SavedHazardBuff> saved = new();
        foreach ((int buffSpell, (DateTimeOffset onSince, bool confirmed)) in _on)
        {
            if (CounterOf(buffSpell) is not { DurationSeconds: > 0 } counter) continue;
            int left = (int)Math.Round((onSince + DurationOf(counter) - now).TotalSeconds);
            if (left <= 0) continue;
            saved.Add(new Models.Profile.SavedHazardBuff { BuffSpell = buffSpell, RemainingSec = left, Confirmed = confirmed });
        }
        return saved;
    }

    // Put back the buffs a profile was saved with, their clocks standing still until
    // Resume. Called on a profile load, after Forget and with that profile's game
    // data already the active set: a buff the set doesn't know as a hazard counter
    // is left out. Returns how many were put back.
    public int Restore(IReadOnlyList<Models.Profile.SavedHazardBuff> saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        DateTimeOffset now = _now();
        int restored = 0;
        foreach (Models.Profile.SavedHazardBuff buff in saved)
        {
            if (buff.RemainingSec <= 0 || CounterOf(buff.BuffSpell) is not { DurationSeconds: > 0 } counter) continue;
            DateTimeOffset onSince = now - (DurationOf(counter) - TimeSpan.FromSeconds(buff.RemainingSec));
            _on[buff.BuffSpell] = (onSince, buff.Confirmed);
            _carried[buff.BuffSpell] = onSince;
            restored++;
        }
        // From now, not from an earlier drop: the time left is as of this moment.
        if (restored > 0)
        {
            _pausedAt = now;
            _pausedWhy = "put back from the saved profile";
        }
        return restored;
    }

    // Forget the buffs that were carried over time out of the game and have not been
    // raised again since (no `use` of ours, no line of theirs). For a death the client
    // did not see and learns of after it is back in the game: it wiped what was on
    // then, and a buff raised since is not its business.
    // Returns how many went.
    public int ForgetCarried(string why)
    {
        List<int> gone = new();
        foreach ((int buffSpell, DateTimeOffset onSince) in _carried)
            if (_on.TryGetValue(buffSpell, out (DateTimeOffset OnSince, bool Confirmed) on) && on.OnSince == onSince)
                gone.Add(buffSpell);
        _carried.Clear();
        if (gone.Count == 0) return 0;
        DropTracked(gone);
        _log?.Info(LogCategory,
            $"buff {string.Join(", ", gone)} dropped ({why}): carried over time out of the game, and not raised since");
        return gone.Count;
    }

    // The first prompt back in the game: the clocks run on with what they had left.
    public void Resume()
    {
        if (_pausedAt is not { } since) return;
        _pausedAt = null;
        TimeSpan away = _now() - since;
        // Only what was running when the link dropped stood still. A buff whose line
        // showed on the way back in, ahead of the first prompt, started then.
        foreach (int buffSpell in _on.Keys.ToList())
        {
            DateTimeOffset onSince = _on[buffSpell].OnSince;
            if (onSince > since) continue;
            _on[buffSpell] = (onSince + away, _on[buffSpell].Confirmed);
            // The mark of a carried buff moves with it.
            if (_carried.TryGetValue(buffSpell, out DateTimeOffset carried) && carried == onSince)
                _carried[buffSpell] = onSince + away;
        }
        if (_on.Count > 0)
            _log?.Info(LogCategory, $"buff clock resumed after {(int)away.TotalSeconds} s away — {TimeLeftText()}");
    }

    private string TimeLeftText()
    {
        DateTimeOffset now = Clock();
        return string.Join(", ", _on.Select(on => CounterOf(on.Key) is { } c
            ? $"buff {on.Key} has {(int)(on.Value.OnSince + DurationOf(c) - now).TotalSeconds} s left"
            : $"buff {on.Key} is on"));
    }

    // Forget what is believed about every buff: a death wipes them, and a new
    // profile or a new game-data set is another character or other data. Unknown
    // counts as off when a hazard room is next.
    public void Forget(string why)
    {
        bool known = _on.Count > 0;
        _on.Clear();
        _carried.Clear();
        _pending = null;
        _roundWait = null;
        ReleaseStep(why);
        _refusedAtCount.Clear();
        _buffLines = null;
        _dueSaid = DueSaid.Nothing;
        _waitLogged = false;
        Disarm();
        if (known) _log?.Info(LogCategory, $"buff tracking reset ({why}) — treated as off until used again");
    }

    // What ends the buff early: "dying or walking through a room that purges buffs,
    // negate magic" (user, 2026-10-10). It has no wear-off line to say so, which is
    // how the cast scheduler learns of its own buffs going, so the room's spell is
    // asked instead: the same index that keeps buffs from being cast in such a room.
    private void DropBuffsStrippedBy(Room room)
    {
        if (_roomStripsBuff is null || _on.Count == 0) return;
        List<int>? gone = null;
        foreach (int buffSpell in _on.Keys)
            if (_roomStripsBuff(room.Spell, buffSpell)) (gone ??= new()).Add(buffSpell);
        if (gone is null) return;
        DropTracked(gone);
        _log?.Info(LogCategory,
            $"buff {string.Join(", ", gone)} taken off by {Describe(room)}, whose spell strips it — treated as off until used again");
    }

    // A command went out in a room whose command textblock answers it with a dispel
    // (`enter tapestry`, `enter portal`, `go courtyard`: negate magic, then a
    // teleport). Judged by the command sent, since no line of the game's is read for
    // it: a cast written ahead of the line's conditions lands whether or not the rest
    // goes through, and one behind a condition that failed did not, in which case
    // the buff is only forgotten early.
    public void OnRoomCommandSent(Room room, string command)
    {
        if (_commandStripsBuff is null || _on.Count == 0 || room.Cmd <= 0) return;
        List<int>? gone = null;
        foreach (int buffSpell in _on.Keys)
            if (_commandStripsBuff(room.Cmd, command, buffSpell)) (gone ??= new()).Add(buffSpell);
        if (gone is null) return;
        DropTracked(gone);
        _log?.Info(LogCategory,
            $"buff {string.Join(", ", gone)} taken off by `{command.Trim()}` in {Describe(room)}, a command that casts a dispel — treated as off until used again");
    }

    // Negate magic was cast on the character by something other than a room's own
    // spell (a transport token casts it before it teleports): every buff is gone.
    public void NoteNegateMagic(string from)
    {
        if (_on.Count == 0) return;
        DropTracked(_on.Keys.ToList());
        _log?.Info(LogCategory, $"buff tracking reset (negate magic: {from}) — treated as off until used again");
    }

    private void DropTracked(List<int> buffSpells)
    {
        foreach (int buffSpell in buffSpells)
        {
            _on.Remove(buffSpell);
            if (_pending is { } sent && sent.BuffSpell == buffSpell) _pending = null;
        }
        _dueSaid = DueSaid.Nothing;
        _waitLogged = false;
    }

    // ----- when a `use` goes out --------------------------------------------

    // Predictive last call. The walker / loop-runner ask this as they commit to a
    // step, with the room about to be entered, BEFORE the sneak check and the move
    // bytes. If that room's cast-on-enter spell is a checkspell hazard whose buff is
    // off or about to run out, and we carry its source, we `use` it now — so the buff
    // is up when the step lands. A no-op for a seeable / benign / passive-counter
    // room, and while the buff still has time on it.
    public void OnApproachingRoom(RoomKey target)
    {
        if (_resolveRoom(target) is not { } room) return;
        if (room.Spell <= 0) return;
        if (_hazardForSpell(room.Spell) is not { } hazard) return;
        foreach (RoomHazardIndex.BuffCounter counter in hazard.BuffCounters)
        {
            Arm(counter);
            TryRaiseBuff(counter, room, aheadOfStep: true);
        }
    }

    // The character arrived in a room, however it got there. A room whose own spell
    // strips the buff has just taken it off. Then, for a follower (the leader's move
    // carried it), raise the room's hazard buff the same way the approach hook does
    // for our own walk; our own walk, if one is running, already covered this room on
    // approach.
    public void OnArrivedInRoom(RoomKey room)
    {
        if (_resolveRoom(room) is not { } r || r.Spell <= 0) return;
        DropBuffsStrippedBy(r);
        if (!_followingLeader() || _walkActive()) return;
        if (_hazardForSpell(r.Spell) is not { } hazard) return;
        foreach (RoomHazardIndex.BuffCounter counter in hazard.BuffCounters)
        {
            Arm(counter);
            TryRaiseBuff(counter, r, aheadOfStep: false);
        }
    }

    private void TryRaiseBuff(RoomHazardIndex.BuffCounter counter, Room room, bool aheadOfStep)
    {
        // The round's cast is spent, by the game's own word: the cast pass sends this
        // when it has the next one.
        if (InRoundWait()) return;
        if (SourceToUse(counter) is not int pick) return;

        DateTimeOffset now = _now();
        if (!MustUseNow(counter, now)) return;   // buff still up — don't spend a charge

        // Asked after the timing check, before SendUse stamps anything, so a skip
        // leaves no timer behind that would read as a buff still covering us.
        if (MasterSwitchOff?.Invoke() == true) return;
        if (OutOfCharges(counter, pick)) return;

        // Read before the send: the `use` itself ends the sneak being kept.
        bool overSneak = _sneakKept?.Invoke() == true;
        if (SendUse(counter, pick, now, couldNotWait: true, stepInto: aheadOfStep ? room : null) is not { } name) return;
        _log?.Info(LogCategory, overSneak
            ? $"forced: `use {name}` for buff {counter.BuffSpell} before {Describe(room)} — it could not wait for a room with no NPCs, and the sneak is spent here"
            : $"used `use {name}` for buff {counter.BuffSpell} before {Describe(room)} ({Lasts(counter)})");
        if (overSneak) SneakSpentHere?.Invoke();
    }

    // The source item to `use` for this counter, or null when nothing is to be sent:
    // a guard makes the hazard a no-op, nothing is carried to raise the buff, the item
    // is out of charges or was refused, or the send gate is up.
    private int? SourceToUse(in RoomHazardIndex.BuffCounter counter)
    {
        // Not back in the game yet: whatever is on the screen is not the game's.
        if (_pausedAt is not null) return null;
        // Ahead of the returns below: a pack seen empty has changed.
        SweepSetAside();

        // A passive immunity guard (the desert sunstone wristband) makes the whole
        // hazard a no-op just by being POSSESSED — carried or worn, no `use` needed
        // (user-confirmed: the player only has to have it). Spending a waterskin
        // charge to raise the buff is pointless while one is held, so skip the `use`
        // entirely (report -112011: a swig fired in the Scorching Desert though the
        // sunstone already granted immunity). `_carriedCount` counts pack + worn.
        if (FirstCarried(counter.ImmunityItems) is int guard and > 0)
        {
            _log?.Debug(LogCategory,
                $"buff spell {counter.BuffSpell}: immune via {_itemName(guard) ?? guard.ToString()} — no `use` needed");
            return null;
        }

        int pick = FirstCarried(counter.SourceItems);
        if (pick == 0)
        {
            // Nothing carried to raise this buff. The route's own gating / obtain
            // detour owns getting one; here we can only note the exposure.
            _log?.Debug(LogCategory,
                $"buff spell {counter.BuffSpell}: no source item carried — can't raise");
            return null;
        }

        if (StillRefused(pick)) return null;   // said when it was refused
        if (_sendBlocked?.Invoke() == true) return null;
        return pick;
    }

    // The game turned a `use` of this item away for a reason that holds, and the pack
    // has not been seen to change since.
    private bool StillRefused(int item)
    {
        SweepSetAside();
        return _refusedAtCount.ContainsKey(item);
    }

    // An item set aside comes back into use once the count carried is seen to differ
    // from what it was: more, fewer or none. It has to be caught at fewer or none,
    // which is why this is asked on every look and not only when a `use` is wanted:
    // the spent one sold and a fresh one bought brings the count back to where it
    // was, and the fresh one would stay set aside.
    private void SweepSetAside()
    {
        if (_refusedAtCount.Count == 0) return;
        List<int>? changed = null;
        foreach ((int item, int countThen) in _refusedAtCount)
            if (_carriedCount(item) != countThen) (changed ??= new()).Add(item);
        if (changed is null) return;
        foreach (int item in changed) _refusedAtCount.Remove(item);
    }

    // True when the client's charge count for the item reads none left: nothing is
    // sent, and the item is set aside until the pack changes. Asked only when a `use`
    // is about to go out, since the count is read from the item table.
    private bool OutOfCharges(in RoomHazardIndex.BuffCounter counter, int pick)
    {
        if (_chargesLeft?.Invoke(pick) != 0) return false;
        _refusedAtCount[pick] = _carriedCount(pick);
        _log?.Info(LogCategory,
            $"refused: {_itemName(pick) ?? pick.ToString()} has no charges left for buff {counter.BuffSpell} — not sent until the pack changes");
        return true;
    }

    // What the cast scheduler is offered: the command to send, and whether it can
    // wait for a better moment. Null when nothing is due — the buff has time on it,
    // no countered room is here or a few steps ahead, or nothing can raise it.
    public (string What, bool MustGoNow)? DueNow()
    {
        if (DueUse(out _, out _, out bool mustGoNow, out TimeSpan? left, out Room? where) is not { } name)
        {
            _dueSaid = DueSaid.Nothing;
            _waitLogged = false;
            return null;
        }
        string what = $"use {name}";
        DueSaid saying = mustGoNow ? DueSaid.CannotWait : DueSaid.Due;
        if (_dueSaid != saying)
        {
            _dueSaid = saying;
            _log?.Info(LogCategory, $"due: `{what}` — the buff "
                + (left is { } l ? $"runs out in {Math.Max(0, (int)l.TotalSeconds)} s" : "is off or not known to be on")
                + (where is null ? "" : $", and {Describe(where)} needs it")
                + (mustGoNow ? "; it can't wait" : ""));
        }
        return (what, mustGoNow);
    }

    // The scheduler held the due `use` to keep a sneak: it waits for a room with no
    // NPCs, or for the last call.
    public void NoteHeldForSneak()
    {
        if (_waitLogged) return;
        _waitLogged = true;
        _log?.Info(LogCategory, "waiting for a room with no NPCs before the `use`, so the sneak isn't spent beside one");
    }

    // The scheduler has the round's cast for us: send the due `use`. False when
    // nothing was sent.
    public bool FireDue()
    {
        if (DueUse(out RoomHazardIndex.BuffCounter counter, out int pick, out bool mustGoNow, out _, out Room? where) is null)
            return false;
        if (MasterSwitchOff?.Invoke() == true) return false;
        if (OutOfCharges(counter, pick)) return false;
        Arm(counter);
        // A step still waiting on this `use` waits on for its answer.
        Room? stepInto = _holdingStep ? _roundWait?.StepInto : null;
        // Read before the send: the `use` itself ends the sneak being kept.
        bool overSneak = _sneakKept?.Invoke() == true;
        if (SendUse(counter, pick, _now(), couldNotWait: mustGoNow, stepInto) is not { } name) return false;
        string place = where is null ? "" : $" {Describe(where)}";
        _log?.Info(LogCategory, overSneak
            ? $"forced: `use {name}` for buff {counter.BuffSpell}{place} — no room with no NPCs turned up in time, and the sneak is spent here"
            : $"used `use {name}` for buff {counter.BuffSpell}{place}{(mustGoNow ? ", at the last call" : "")} ({Lasts(counter)})");
        if (overSneak) SneakSpentHere?.Invoke();
        return true;
    }

    // The due `use`, if any: the item's name, with the counter, the item, whether it
    // can wait, the time left on the buff and the countered room that makes it matter.
    private string? DueUse(
        out RoomHazardIndex.BuffCounter counter, out int pick, out bool mustGoNow, out TimeSpan? left, out Room? where)
    {
        counter = default;
        pick = 0;
        mustGoNow = false;
        left = null;
        where = null;
        if (_pausedAt is not null) return null;
        // Asked once a second by the cast pass, wherever the character is: the look
        // that catches a set-aside item leaving the pack.
        SweepSetAside();

        // Owed from a round refusal: due wherever the character stands, and it can't
        // wait, since it couldn't when it was first sent.
        if (InRoundWait() && _roundWait is { } owed)
        {
            if (SourceToUse(owed.Counter) is int owedItem && _itemName(owedItem) is { Length: > 0 } owedName)
            {
                counter = owed.Counter;
                pick = owedItem;
                mustGoNow = true;
                left = RemainingOf(owed.Counter, _now());
                where = owed.StepInto;
                return owedName;
            }
            _roundWait = null;
            ReleaseStep("nothing left to use");
        }

        // No walk has to be running: asked whether the refresh should also run for a
        // character standing in a countered room by hand, "if auto master toggle is
        // on, yes, if off, no" (user, 2026-10-10). The switch is asked by whoever
        // offers and sends this. With nothing planned the list is the room we stand in.
        if (_roomsAhead?.Invoke() is not { Count: > 0 } rooms) return null;

        // The window and the look ahead buy the search for a room with no NPCs. With
        // Auto-Sneak off there is nothing to search for: only the room we stand in
        // counts, and only at the last call.
        bool searching = _autoSneakOn?.Invoke() == true;
        int reach = searching ? rooms.Count : 1;
        DateTimeOffset now = _now();
        for (int i = 0; i < reach; i++)
        {
            if (_resolveRoom(rooms[i]) is not { Spell: > 0 } room) continue;
            if (_hazardForSpell(room.Spell) is not { } hazard) continue;
            foreach (RoomHazardIndex.BuffCounter c in hazard.BuffCounters)
            {
                if (!(searching ? InWindow(c, now) : MustUseNow(c, now))) continue;
                if (SourceToUse(c) is not int item) continue;
                if (_itemName(item) is not { Length: > 0 } name) continue;
                counter = c;
                pick = item;
                left = RemainingOf(c, now);
                where = room;
                // Standing in the countered room there is nothing to wait for. One
                // ahead is the step's own last call (OnApproachingRoom).
                mustGoNow = i == 0 && MustUseNow(c, now);
                return name;
            }
        }
        return null;
    }

    // Arm the reactive layer for this hazard's buff: compile the lapse-prompt and
    // swig-confirmation line predicates so a lapse fired mid-room (not on a step)
    // is still caught. Re-arming the SAME buff is a no-op so an in-flight
    // _awaitingSwig latch survives a re-approach of the room we're already in.
    private void Arm(RoomHazardIndex.BuffCounter counter)
    {
        if (_activeCounter is { } cur && cur.BuffSpell == counter.BuffSpell) return;
        _activeCounter = counter;
        _lapseMatch = counter.LapseSpell > 0
            ? _messageMatcherForSpell?.Invoke(counter.LapseSpell)
            : null;
        _swigMatch = _messageMatcherForSpell?.Invoke(counter.BuffSpell);
        _awaitingSwig = false;
    }

    // ----- what the game says ------------------------------------------------

    // Fed every server line. Reads a buff's own line wherever it shows (it is on,
    // from now), the answer to a `use` of ours the game turned away, and the armed
    // hazard's lapse prompt (re-raises once, or halts when out of charges).
    public void OnServerLine(string line)
    {
        if (BuffLineOf(line) is int buffSpell)
        {
            _on[buffSpell] = (_now(), true);
            _pending = null;
            _roundWait = null;
            _refusedAtCount.Clear();
            if (_activeCounter is { } armed && armed.BuffSpell == buffSpell) _awaitingSwig = false;
            ReleaseStep("the buff is on");
            return;
        }
        if (_pending is { } sent && TakeRefusal(line, sent)) return;
        if (_activeCounter is { } counter && _lapseMatch?.Invoke(line) == true) HandleThirst(counter);
    }

    // The buff whose own line this is, or null.
    private int? BuffLineOf(string line)
    {
        if (_activeCounter is { } armed && _swigMatch?.Invoke(line) == true) return armed.BuffSpell;
        if (_allCounters is null || _messageMatcherForSpell is null) return null;
        if (_buffLines is null)
        {
            _buffLines = new();
            foreach (RoomHazardIndex.BuffCounter c in _allCounters())
                if (_buffLines.All(b => b.BuffSpell != c.BuffSpell) && _messageMatcherForSpell(c.BuffSpell) is { } match)
                    _buffLines.Add((c.BuffSpell, match));
        }
        foreach ((int spell, Func<string, bool> match) in _buffLines)
            if (match(line)) return spell;
        return null;
    }

    // The Stock engine's answers to a `use` it won't carry out (GAME_MECHANICS
    // "Equip → use → restore swap for a readied buff item"), read only right behind
    // a `use` of ours. The round refusal is tried again a round later, by whichever
    // road sent it; the others hold until the pack changes, since asking again would
    // only draw the same line.
    private bool TakeRefusal(string line, SentUse sent)
    {
        if (_now() - sent.SentAt > AnswerWindow)
        {
            _pending = null;
            return false;
        }
        // The two lines that name the item are ours only when they name this one:
        // "You don't have ..." answers other commands as well.
        string named = (_itemName(sent.Item) ?? string.Empty).Split(' ')[0];
        bool namesIt = named.Length > 0 && line.Contains(named, StringComparison.OrdinalIgnoreCase);
        string? why =
            line.StartsWith("You have already cast a spell this round!", StringComparison.Ordinal) ? "already cast this round — trying again next round"
            : namesIt && line.StartsWith("You don't have ", StringComparison.Ordinal) ? "the game says it isn't carried"
            : namesIt && line.StartsWith("There are no more uses in ", StringComparison.Ordinal) ? "no uses left in it"
            : line.StartsWith("You may not use that item!", StringComparison.Ordinal) ? "the character may not use it"
            : null;
        if (why is null) return false;

        _pending = null;
        _awaitingSwig = false;
        if (sent.Before is { } before) _on[sent.BuffSpell] = before;
        else _on.Remove(sent.BuffSpell);
        bool again = line.StartsWith("You have already cast", StringComparison.Ordinal);
        if (!again) _refusedAtCount[sent.Item] = _carriedCount(sent.Item);
        _log?.Info(LogCategory,
            $"refused: `use {_itemName(sent.Item) ?? sent.Item.ToString()}` for buff {sent.BuffSpell} — {why}"
            + (again ? "" : "; not sent again until the pack changes"));

        // One that could wait is offered to the cast pass again as before, and the
        // pass keeps to a cast a round. One that couldn't is owed to that pass now,
        // and a step waiting on it waits out the round with it.
        if (again && sent.CouldNotWait && CounterOf(sent.BuffSpell) is { } counter)
        {
            _roundWait = (counter, sent.StepInto, _now() + RoundWait);
            if (sent.StepInto is not null) HoldStep(RoundWait, "waiting for the next round's cast to send the `use` again");
            else ReleaseStep("the `use` was refused");
        }
        else
        {
            ReleaseStep("the `use` was refused");
        }
        return true;
    }

    private void HandleThirst(RoomHazardIndex.BuffCounter counter)
    {
        // The room has just cast its buff-absent spell, so the buff is off whatever
        // the clock said. Taken first and for anyone standing here: when nothing
        // below sends a `use`, the refresh is due again under its usual rules.
        NoteBuffAbsent(counter);
        // On the way back in, ahead of the first in-game prompt: the line is the
        // game's and the buff is off, but nothing is sent until we are back.
        if (_pausedAt is not null) return;
        SweepSetAside();

        // The `use` sent from here is for a live walk — ours, or the leader's we're
        // following: the "out of" say and the halt below are a route's business.
        if (!_walkActive() && !_followingLeader()) return;

        // Immune via a passive guard — the lapse prompt can't actually harm us, so
        // neither re-`use` (pointless) nor halt (we're safe). Just walk on.
        if (FirstCarried(counter.ImmunityItems) is int guard and > 0)
        {
            _log?.Debug(LogCategory,
                $"buff spell {counter.BuffSpell}: lapse prompt ignored — immune via {_itemName(guard) ?? guard.ToString()}");
            return;
        }

        // Before the out-of-charges branches too: they say "I'm out of …" and halt
        // the walk, and neither may happen (nor latch _announcedOut) while off.
        if (MasterSwitchOff?.Invoke() == true) return;

        // A prior `use` never produced its swig: the item drew nothing (out of
        // charges / waterskins). Re-firing would march deeper into a hazard we can
        // no longer counter, so halt instead of firing into the void.
        if (_awaitingSwig)
        {
            AnnounceOut(counter);
            Halt($"buff {counter.BuffSpell}: lapse prompt with no swig — out of charges");
            return;
        }

        int pick = FirstCarried(counter.SourceItems);
        if (pick == 0)
        {
            AnnounceOut(counter);
            Halt($"buff {counter.BuffSpell}: lapsed with no source item carried");
            return;
        }

        // The game already refused this item, or the client counts it empty: asking
        // again at every lapse would only draw the same line.
        if (StillRefused(pick) || OutOfCharges(counter, pick))
        {
            AnnounceOut(counter);
            Halt($"buff {counter.BuffSpell}: lapsed with nothing left to use in {_itemName(pick) ?? pick.ToString()}");
            return;
        }
        if (_sendBlocked?.Invoke() == true) return;
        // The game has just said the round's cast is spent: the cast pass sends the
        // `use` with the next one.
        if (InRoundWait()) return;

        // Read before the send: the `use` itself ends the sneak being kept.
        bool overSneak = _sneakKept?.Invoke() == true;
        if (SendUse(counter, pick, _now(), couldNotWait: true, stepInto: null) is not { } name) return;
        _log?.Info(LogCategory,
            $"re-raised buff {counter.BuffSpell} with `use {name}` on lapse prompt — it ran out sooner than its timer said"
            + (overSneak ? "; the sneak is spent here" : ""));
        if (overSneak) SneakSpentHere?.Invoke();
    }

    // The game's own word that the buff is absent: drop the clock, and the stamp a
    // pending `use` would put back if it is turned away.
    private void NoteBuffAbsent(in RoomHazardIndex.BuffCounter counter)
    {
        TimeSpan? left = RemainingOf(counter, Clock());
        _on.Remove(counter.BuffSpell);
        if (_pending is { } sent && sent.BuffSpell == counter.BuffSpell) _pending = sent with { Before = null };
        // Not while a `use` of ours is unanswered: that one is told as out of charges.
        if (left is { } l && l > TimeSpan.Zero && !_awaitingSwig)
            _log?.Info(LogCategory,
                $"buff {counter.BuffSpell} is off: the room's lapse line showed with {(int)l.TotalSeconds} s still on its clock");
    }

    // `use` the carried source item: stamp the buff as on from now, remember what it
    // was before in case the game turns the `use` away, and latch _awaitingSwig for
    // the swig confirmation. Returns the item name used, or null when the item
    // resolves to no name (nothing sent). couldNotWait: it went out at the last call.
    // stepInto: it went out ahead of a step into that room, which then waits on the
    // answer, so it goes in with the buff on or not until the game has said why not.
    private string? SendUse(
        in RoomHazardIndex.BuffCounter counter, int itemId, DateTimeOffset now, bool couldNotWait, Room? stepInto)
    {
        string? name = _itemName(itemId);
        if (string.IsNullOrWhiteSpace(name))
        {
            _log?.Debug(LogCategory, $"buff spell {counter.BuffSpell}: item {itemId} has no name — can't `use`");
            return null;
        }
        _wire.Send($"use {name}");
        _pending = new SentUse(counter.BuffSpell, itemId, now,
            _on.TryGetValue(counter.BuffSpell, out (DateTimeOffset OnSince, bool Confirmed) before) ? before : null,
            couldNotWait, stepInto);
        _on[counter.BuffSpell] = (now, false);
        _awaitingSwig = true;
        _announcedOut = false;
        _dueSaid = DueSaid.Nothing;
        _waitLogged = false;
        _roundWait = null;
        if (stepInto is not null) HoldStep(StepAnswerCap, $"waiting for the answer to `use {name}` before the step");
        else ReleaseStep("the `use` went out");
        UseSent?.Invoke();
        return name;
    }

    // Tell the room we can no longer counter the hazard — out of the buff's source
    // item — so the party knows why we're taking the damage (a follower can't stop
    // the leader's route itself). Said once per run-out; the `.` precursor makes it
    // a plain say whatever the line starts with.
    private void AnnounceOut(RoomHazardIndex.BuffCounter counter)
    {
        if (_announcedOut) return;
        _announcedOut = true;
        string item = counter.SourceItems.Count > 0 && _itemName(counter.SourceItems[0]) is { Length: > 0 } n
            ? n : "water";
        string plural = item.EndsWith('s') ? item : item + "s";
        _wire.Send($".I'm out of {plural}!");
        _log?.Info(LogCategory, $"out of {plural} — told the room");
    }

    // Out of ways to raise the buff. Our own walk backs out rather than marching
    // deeper; a follower has no walk to halt — the leader owns the route — so it only
    // records it.
    private void Halt(string why)
    {
        if (_walkActive())
        {
            _log?.Info(LogCategory, $"halting walk — {why}");
            _haltWalk?.Invoke(why);
        }
        else
        {
            _log?.Info(LogCategory, $"following through the hazard unprotected — {why}");
        }
        Disarm();
    }

    private void Disarm()
    {
        _activeCounter = null;
        _lapseMatch = null;
        _swigMatch = null;
        _awaitingSwig = false;
    }

    private int FirstCarried(IReadOnlyList<int> items)
    {
        foreach (int id in items)
            if (_carriedCount(id) > 0) return id;
        return 0;
    }

    // ----- for the log and the bug report ------------------------------------

    private static string Describe(Room room) => $"{room.Key} ({room.Name})";

    private string Lasts(in RoomHazardIndex.BuffCounter counter) => counter.DurationSeconds > 0
        ? $"lasts {counter.DurationSeconds} s; {Refreshed()}"
        : $"length not in the game data; used again after {UnknownDurationRefreshSeconds} s";

    // When the refresh comes, as things stand.
    private string Refreshed() => _autoSneakOn?.Invoke() == true
        ? $"refresh window {RefreshWindowSeconds} s, last call {ForcedLeadSeconds} s"
        : $"refreshed in its last {ForcedLeadSeconds} s (Auto-Sneak off: no window, no look ahead)";

    // The tracked buffs, one line each, for the bug report. Empty when none is known
    // to be on.
    public IReadOnlyList<string> DescribeTracking()
    {
        DateTimeOffset now = Clock();
        List<string> lines = new();
        if (_pausedAt is { } since && _on.Count > 0)
            lines.Add($"clocks paused since {since.ToLocalTime():HH:mm:ss} ({_pausedWhy}); they run on from the first prompt back in the game");
        foreach ((int buffSpell, (DateTimeOffset onSince, bool confirmed)) in _on)
        {
            RoomHazardIndex.BuffCounter? counter = CounterOf(buffSpell);
            // On since is the start its clock now counts from: after a drop, moved on
            // by the time away.
            string ends = counter is not { } c ? "length unknown"
                : _pausedAt is not null ? $"{(int)(onSince + DurationOf(c) - now).TotalSeconds} s left when it paused"
                : $"runs out {(onSince + DurationOf(c)).ToLocalTime():HH:mm:ss} (in {(int)(onSince + DurationOf(c) - now).TotalSeconds} s)";
            lines.Add($"buff {buffSpell}: on since {onSince.ToLocalTime():HH:mm:ss} "
                + (confirmed ? "(its line was seen)" : "(a `use` went out; its line not seen)")
                + $", {ends}; {Refreshed()}"
                + (_pending is { } p && p.BuffSpell == buffSpell ? "; answer to the `use` still awaited" : ""));
        }
        foreach ((int item, int count) in _refusedAtCount)
            lines.Add($"{_itemName(item) ?? $"item {item}"}: a `use` was refused or it has no charges; nothing sent while {count} are carried");
        if (InRoundWait())
            lines.Add("a `use` was refused for the round's cast already made; the cast pass sends it with the next one"
                + (_holdingStep ? ", and the step waits for it" : ""));
        else if (_holdingStep)
            lines.Add("the step waits for the answer to a `use`");
        if (_dueSaid != DueSaid.Nothing)
            lines.Add(_dueSaid == DueSaid.CannotWait ? "a `use` is due and can't wait"
                : _waitLogged ? "a `use` is due, waiting for a room with no NPCs"
                : "a `use` is due, waiting for the round's cast");
        return lines;
    }

    private RoomHazardIndex.BuffCounter? CounterOf(int buffSpell)
    {
        if (_activeCounter is { } armed && armed.BuffSpell == buffSpell) return armed;
        if (_allCounters is null) return null;
        foreach (RoomHazardIndex.BuffCounter c in _allCounters())
            if (c.BuffSpell == buffSpell) return c;
        return null;
    }
}
