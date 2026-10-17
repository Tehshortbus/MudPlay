using MudPlay.Game.Spells;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Holds a loop one step short of a lair until the round's between-round cast is free,
// so the combat profile's pre-attack debuff goes out on entry instead of behind the
// attack (report paradigm-20261007-120006: a buff cast a second before the fight spent
// the cast, and the room spell went first).
//
// The loop's move-ready check asks ReadyToEnter for each step into a lair. While the
// answer is no, the walker is held on MovementCoordinator.LairDebuffGate and a timer
// keeps asking; when it turns yes the gate clears, which re-drives the step. From then
// buffs are held back (BlockingBuffs), so nothing takes the cast ahead of the debuff.
//
// With the cast free, the loop still stays (on LairDebuffManaGate) while mana is under
// the debuff's own minimum: see ShortOfManaForTheDebuff.
//
// When buffs are let through again, and held again (user, 2026-10-10: "ordinary buffs
// should be let through after the debuff spell has gone out in the lair room and held
// again once the user ends up next to the next lair room"):
//   - The hold put up at the go-ahead lifts the moment the debuff is sent
//     (NoteDebuffSent). With none sent it lifts when the combat engine opens the
//     fight in the lair with no debuff to cast, when that fight is two rounds old,
//     when it ends, when the loop asks for its next step, and, with no fight started,
//     when EntryWindow runs out. See Advance.
//   - It goes up again at the next doorstep ask, which is the loop about to step into
//     the next lair. Being next to that lair while still fighting in this one holds
//     nothing: with lairs side by side the character is next to the next one the
//     whole fight, and the buffs would never get out.
//   - Buffs this hold kept back that have had no cast by that next ask (the kill came
//     before the cast scheduler's next pass, or they only cast out of combat) get one
//     cast there before they are held again. See GivingTheOwedCast.
//   - A fight where the loop stands waiting holds nothing: the debuff is for the lair,
//     and the character fights at the doorstep with every buff it has.
//   - A monster swinging at the character where the loop stands, in a room the loop
//     is set not to fight in, is no such fight while the wait is for the cast: see
//     InAFightOfOurs. Once the wait is for mana the room is fought in, and it is.
//
// What is asked of the hold (BlockingBuffs and the rest of ILairEntryBuffHold's
// questions, Describe) only reads. Its state moves at three places: the loop's ask,
// its own timer while a wait stands, and Advance, which the cast scheduler calls as a
// pass starts.
public sealed class LairEntryDebuffHold : ILairEntryBuffHold
{
    // Rounds a WaitForSpells hold lets other casts have before it stops letting them.
    public const int MaxRoundsWaited = 3;

    // Times one priority buff may go out while a WaitForPriorityBuffs hold stands
    // outside one lair. A buff that takes starts its recast timer as it is sent and
    // isn't due again, so one sent over and over is being refused, or is a roll spell
    // rerolling without end, and the wait would never see "none left due": that buff
    // is given up on there and held back, and the wait goes on for the others.
    public const int MaxSendsOfOnePriorityBuff = 3;

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);
    // Mana comes back by the tick, and a wait for it can last minutes: it is looked
    // at less often, and said in the log no more than once a minute.
    private static readonly TimeSpan ManaPoll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ManaLogEvery = TimeSpan.FromMinutes(1);
    // Something due that never casts must not park the loop. A WaitForSpells hold that
    // has run this long, or a WaitForPriorityBuffs hold that has gone this long with no
    // priority buff going out, enters as soon as the cast is free, whatever is still due.
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(18);
    // How long buffs stay held back after the go-ahead when no fight starts (the step
    // never landed, the lair was empty, or nothing in it is fought).
    private static readonly TimeSpan EntryWindow = TimeSpan.FromSeconds(5);
    // How far into the lair's fight, counted from the engage, the hold after the
    // go-ahead may reach with no debuff sent. Only a backstop: when no debuff is coming the combat engine says so
    // as it opens the fight (NoteNoDebuffThisFight), and the hold is gone at once. What
    // is left is a debuff put off to a later round (behind a backstab, or a cast spent
    // as the fight began), and it must get its cast ahead of the buffs: a backstab on a
    // late first round, and the pass that waits for HP to settle after it, can pass six
    // seconds. Two rounds and that pass is as long as any buff the character fights
    // with (a shield cast "during combat") is kept waiting.
    private static readonly TimeSpan HeldIntoAFight = TimeSpan.FromSeconds(11);
    // How long a doorstep gives the one cast owed to buffs an earlier lair's hold
    // kept back. A round is five seconds, so a cast spent as the loop arrives is free
    // again within that; the rest is for the cast scheduler's next pass. One window a
    // doorstep, never restarted.
    private static readonly TimeSpan OwedCastWindow = TimeSpan.FromSeconds(8);

    private const string Asserter = "LairEntryDebuff";

    private enum WaitLimit { None, Rounds, Time, NothingWentOut, RanOutBefore }

    private readonly MovementCoordinator _coordinator;
    private readonly Func<bool> _slotUsed;
    private readonly Func<bool> _castDue;
    private readonly Action<TimeSpan, Action> _scheduleAfter;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private readonly Func<bool, IReadOnlyList<string>>? _buffsDue;
    private readonly Func<bool>? _stillWanted;
    private readonly Func<string?>? _healOrCureDue;
    private readonly Func<(string Spell, int Needed, int Mana, int MaxMana, bool IsSpellCost)?>? _debuffMana;
    private readonly Func<bool>? _inCombat;
    private readonly Func<bool>? _manaIsFull;
    private readonly Func<RoomKey?>? _fightRoom;
    private readonly Func<bool>? _fightAnswered;

    private LairEntryDebuffMode _mode;
    private bool _waiting;
    private bool _deciding;
    private bool _slotWasUsed;
    private int _roundsWaited;
    private DateTimeOffset _waitStarted;
    private bool _waitLineSaid;
    private bool _doorFightSaid;
    private bool _unansweredSaid;
    private bool _blockWhileWaiting;
    private bool _blockOrdinaryWhileWaiting;
    private WaitLimit _limitReached;

    // The hold put up at the go-ahead: standing, when its window ends if no fight
    // starts, when a fight was first seen under it, and how it last came down.
    private bool _entryHold;
    private DateTimeOffset _blockUntil = DateTimeOffset.MinValue;
    private DateTimeOffset? _fightSeenAt;
    private string? _lastRelease;

    // Buffs this hold has kept back (they were due while it held them) with none of
    // their kind cast since: the ones not ticked priority, and the priority ones.
    // They outlive the doorstep: an ordinary room between two lairs is passed in a
    // moment, and what the first lair held is still owed its cast at the second.
    private bool _ordinaryHeldUnpaid;
    private bool _priorityHeldUnpaid;
    // This doorstep gives the owed cast first: decided once when the loop arrives.
    // While it is being given: until when, and whether a buff has gone out in it.
    private bool _castOwedHere;
    private DateTimeOffset? _owedCastUntil;
    private bool _givingOwedCast;
    private bool _owedCastWentOut;
    // The due held buffs an owed cast's window ran out on without one going out. The
    // same set is owed nothing at the next doorstep: it can't be cast, and standing
    // eight seconds for it at every lair buys nothing.
    private string? _owedRanOutOn;

    // A WaitForPriorityBuffs wait: when a priority buff last went out (the wait's start
    // until one does), and whether its time without one ran out.
    private DateTimeOffset _lastPriorityCast;
    private bool _ranOutThisWait;
    // What the waits have learned at this doorstep, kept across asks for the same lair:
    // how often each buff they let through went out (the priority buffs of a
    // WaitForPriorityBuffs wait, the mana-bringing ones of a wait for mana), the ones
    // given up on, and that the time ran out once (it is not waited out a second time).
    private RoomKey? _doorstep;
    private readonly Dictionary<string, int> _prioritySends = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _givenUpOn = new(StringComparer.OrdinalIgnoreCase);
    private bool _ranOutHere;
    private bool _manaNotWaitedForSaid;
    // The wait is past its casts and stands for mana: what the debuff needs against
    // what there is, as last read; when that was last said in the log; and, once mana
    // is back, the read that ended it.
    private (string Spell, int Needed, int Mana, int MaxMana, bool IsSpellCost)? _forMana;
    private DateTimeOffset _manaSaidAt;
    private bool _stoodForMana;
    private (string Spell, int Needed, int Mana, int MaxMana, bool IsSpellCost)? _manaCameBack;
    private string? _lastOutcome;
    private int _generation;

    // slotUsed: this round's between-round cast is spent. castDue: a heal, cure or
    // buff that isn't being held back is due and would take it. buffsDue: the names of
    // the buffs due, those ticked "Priority buff" (true) or the rest (false), held
    // back or not, and healOrCureDue the heal or cure that is. stillWanted: the loop
    // is still about to step into a lair it waits outside (asked while a wait stands,
    // since a loop that was stopped or switched off never asks ReadyToEnter again).
    // debuffMana: the debuff the wait is for, the mana it needs (its "min mana per
    // cast", or the spell's cost where that is the higher: IsSpellCost), and current
    // and maximum mana; null when no debuff needs mana waited for.
    // inCombat: the cast scheduler's own "in combat". manaIsFull: mana is as high as
    // the game will take it, whatever maximum the client believes in. fightRoom: the
    // room a fight now is in, as the combat engine judges it (the room a loop step
    // in flight is entering, else the one the character stands in); without it the
    // hold can't tell a fight at the door from one in the lair. fightAnswered: the
    // client fights in that room. False where the loop is set not to attack (the
    // room's "do not attack", or "only attack in lair rooms" outside a lair) and
    // nothing else has the combat engine fighting there.
    public LairEntryDebuffHold(MovementCoordinator coordinator, Func<bool> slotUsed, Func<bool> castDue,
        Action<TimeSpan, Action> scheduleAfter, Func<DateTimeOffset>? now = null, LogService? log = null,
        Func<bool, IReadOnlyList<string>>? buffsDue = null, Func<bool>? stillWanted = null,
        Func<string?>? healOrCureDue = null,
        Func<(string Spell, int Needed, int Mana, int MaxMana, bool IsSpellCost)?>? debuffMana = null,
        Func<bool>? inCombat = null, Func<bool>? manaIsFull = null, Func<RoomKey?>? fightRoom = null,
        Func<bool>? fightAnswered = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(slotUsed);
        ArgumentNullException.ThrowIfNull(castDue);
        ArgumentNullException.ThrowIfNull(scheduleAfter);
        _coordinator = coordinator;
        _slotUsed = slotUsed;
        _castDue = castDue;
        _scheduleAfter = scheduleAfter;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _log = log;
        _buffsDue = buffsDue;
        _stillWanted = stillWanted;
        _healOrCureDue = healOrCureDue;
        _debuffMana = debuffMana;
        _inCombat = inCombat;
        _manaIsFull = manaIsFull;
        _fightRoom = fightRoom;
        _fightAnswered = fightAnswered;
    }

    // ----- What is asked ----------------------------------------------

    // Every buff waits: the loop is at a lair's doorstep with no more casts allowed
    // ahead of the debuff, or has the go-ahead and the debuff hasn't gone out yet.
    // Not during a fight where the loop stands waiting.
    public bool BlockingBuffs => (_blockWhileWaiting && !FightingWhereItWaits) || EntryHoldStands;

    // The buffs not ticked "Priority buff" wait: as above, or the loop is at a lair's
    // doorstep letting only the priority buffs go first.
    public bool BlockingOrdinaryBuffs =>
        (_blockOrdinaryWhileWaiting && !FightingWhereItWaits) || BlockingBuffs;

    public bool HoldingForMana => _waiting && _forMana is not null && !FightingNow;

    // The loop stands at the door until there is mana for the debuff, a fight there
    // or not. While it does, the room is fought in whatever the loop's attack
    // settings say (LoopCombatSuppression.LoopIsDriving).
    public bool WaitingForMana => _waiting && _forMana is not null;

    // A wait has come to stand for mana: the engage gates are to be asked again.
    public event Action? ManaWaitStarted;

    public bool SkipsUnpayableBuffs => _waiting || _deciding;

    // A buff this doorstep's wait has given up on (it went out as often as one may),
    // while the loop stands there or asks for the step again.
    public bool HasGivenUpOn(string buff) =>
        (_waiting || _deciding) && !FightingNow && _givenUpOn.Contains(buff);

    // A fight is on: the cast scheduler reads it so, or the combat gate is up.
    private bool FightingNow =>
        InAFightOfOurs || _coordinator.IsGateAsserted(MovementCoordinator.CombatGate);

    // "In combat" is latched by a monster's swings alone. Where the loop is set not
    // to fight, nothing answers them and nothing ends them: a wait that stood until
    // such a fight was over stood until the loop was stopped by hand (report
    // paradigm-20261012-080902: seven minutes at a doorstep under "only attack in
    // lair rooms", a monster that had followed the loop swinging all the while). So
    // it is no fight to this hold. The loop steps in as it would with nothing there,
    // and what followed it is fought in the lair. A wait that comes to stand for
    // mana has the room fought in (WaitingForMana), which makes it a fight again.
    private bool InAFightOfOurs => _inCombat?.Invoke() == true && _fightAnswered?.Invoke() != false;

    private bool AttackedUnanswered =>
        _inCombat?.Invoke() == true && _fightAnswered?.Invoke() == false
        && !_coordinator.IsGateAsserted(MovementCoordinator.CombatGate);

    private bool FightingWhereItWaits => _waiting && FightingNow;

    // The combat engine has engaged: "in combat" where that is known, since the combat
    // gate goes up at the room's roster, a moment before.
    private bool Engaged => _inCombat is null ? FightingNow : InAFightOfOurs;

    // Where a fight now is, against the lair this hold is for. Neither is true when
    // either room isn't known.
    private bool InTheLair => _doorstep is { } lair && _fightRoom?.Invoke() is { } here && here == lair;

    private bool StillAtTheDoor => _doorstep is { } lair && _fightRoom?.Invoke() is { } here && here != lair;

    // The hold put up at the go-ahead still stands: by the clock and what is read off
    // the game alone, so asking changes nothing. Advance takes it down for the things
    // that happen. A fight where the loop still stands, before the step in, sets it
    // aside as a fight sets a wait aside.
    private bool EntryHoldStands =>
        _entryHold && !(StillAtTheDoor && FightingNow)
        && (_fightSeenAt is { } since ? _now() - since < HeldIntoAFight : _now() < _blockUntil);

    // What the hold is doing and how its last wait ended, for the bug report.
    public string Describe()
    {
        string last = (AttackedUnanswered
                ? "; a monster is attacking where the loop stands, in a room it doesn't fight in: not waited out"
                : string.Empty)
            + (_lastRelease is null ? string.Empty : $"; buffs last released: {_lastRelease}")
            + (_lastOutcome is null ? string.Empty : $"; last wait: {_lastOutcome}");
        DateTimeOffset now = _now();
        if (!_waiting)
            return (EntryHoldStands
                ? "has the go-ahead, buffs held until the debuff is sent"
                    + (_fightSeenAt is { } since
                        ? $", the fight ends, or it is {HeldIntoAFight.TotalSeconds:0}s old "
                            + $"({(now - since).TotalSeconds:0}s now)"
                        : $", or {EntryWindow.TotalSeconds:0}s pass with no fight")
                : "not waiting, no buff held") + last;
        if (FightingNow)
            return $"waiting ({LairEntryDebuffModes.Label(_mode)}; a fight where it stands: nothing held, "
                + "nothing decided until it is over)" + last;
        if (_owedCastUntil is { } until)
            return $"waiting ({LairEntryDebuffModes.Label(_mode)}; giving the one cast owed to buffs an earlier "
                + $"lair held, {Math.Max(0, (until - now).TotalSeconds):0}s left of "
                + $"{OwedCastWindow.TotalSeconds:0}s; nothing held; due: {JoinOrNone(DueHeldUnpaid())})" + last;
        if (_forMana is { } need)
            return $"waiting for mana ({LairEntryDebuffModes.Label(_mode)}; {(now - _waitStarted).TotalSeconds:0}s; "
                + $"mana {need.Mana} of the {need.Needed} the debuff {need.Spell} needs, {Basis(need.IsSpellCost)}; "
                + "every buff held bar the ones that bring mana back; a monster in this room is fought)" + last;
        string held = _blockWhileWaiting ? "every buff held"
            : _blockOrdinaryWhileWaiting ? "buffs not ticked priority held"
            : "nothing held";
        string limits = _mode == LairEntryDebuffMode.WaitForPriorityBuffs
            ? $"{(now - _waitStarted).TotalSeconds:0}s, {(now - _lastPriorityCast).TotalSeconds:0}/"
                + $"{MaxWait.TotalSeconds:0}s since a priority buff went out; sent: {PrioritySends()}; "
                + $"given up on: {JoinOrNone(_givenUpOn)}"
            : $"{_roundsWaited}/{MaxRoundsWaited} rounds, {(now - _waitStarted).TotalSeconds:0}/{MaxWait.TotalSeconds:0}s";
        return $"waiting ({LairEntryDebuffModes.Label(_mode)}; {limits}; {held}; "
            + $"priority buffs due: {JoinOrNone(WaitedFor())}; other buffs due: {JoinOrNone(HeldBack())})" + last;
    }

    // For a bug report's list of pending work: that the hold is up, and no more,
    // or null. What it is waiting on is Describe's to say, which the report
    // prints once, with the loop.
    public string? DescribePending() =>
        _waiting ? "waiting outside a lair (see Movement engine: Lair-entry debuff wait)"
        : EntryHoldStands ? "has the go-ahead, buffs held (see Movement engine: Lair-entry debuff wait)"
        : null;

    // ----- The loop's ask ----------------------------------------------

    // May the loop take its step into a lair now? False holds the walker; the hold
    // lifts by itself when the answer changes.
    //
    // lair: the room the step enters. What a WaitForPriorityBuffs wait learned at one
    // doorstep (how often each buff went out, which were given up on, that its time
    // ran out) is kept while the asks are for the same lair: a step that doesn't land
    // is asked for again, and that must not start the count afresh.
    public bool ReadyToEnter(LairEntryDebuffMode mode, RoomKey? lair = null)
    {
        if (mode == LairEntryDebuffMode.Off)
        {
            // The next step is no lair. What this hold kept back and never saw cast
            // is still owed its cast at the next lair's door: the room between may be
            // one quick step, with no pass of the cast scheduler in it.
            End("no longer entering a lair", forgetWhatIsOwed: false);
            return true;
        }
        // From here the loop is at a doorstep: what is asked of the cast scheduler is
        // asked as the wait asks it (Decide lowers this again).
        _deciding = true;
        if (!Nullable.Equals(lair, _doorstep))
        {
            // Asked for the next lair with the last one's hold still up: no debuff went
            // out there and no fight was seen to end. The loop is leaving that room.
            if (_entryHold) ReleaseBuffs("the loop is at the next lair's doorstep");
            ForgetDoorstep();
            _castOwedHere = OwedACast();
        }
        _doorstep = lair;
        _mode = mode;
        if (Decide())
        {
            GoIn();
            return true;
        }
        if (!_waiting)
        {
            _waiting = true;
            SchedulePoll();
        }
        SayWhatTheWaitIsFor();
        HoldTheWalker();
        return false;
    }

    // The loop stopped, failed, was paused by hand or switched off: nothing is held
    // and nothing is owed any more.
    public void Reset(string why = "no longer entering a lair") => End(why, forgetWhatIsOwed: true);

    private void End(string why, bool forgetWhatIsOwed)
    {
        if (_entryHold) ReleaseBuffs(why);
        ForgetDoorstep();
        _generation++;
        _blockWhileWaiting = false;
        _blockOrdinaryWhileWaiting = false;
        _blockUntil = DateTimeOffset.MinValue;
        _fightSeenAt = null;
        _castOwedHere = false;
        _owedCastUntil = null;
        _givingOwedCast = false;
        if (forgetWhatIsOwed)
        {
            _ordinaryHeldUnpaid = false;
            _priorityHeldUnpaid = false;
            _owedRanOutOn = null;
        }
        _roundsWaited = 0;
        _slotWasUsed = false;
        _limitReached = WaitLimit.None;
        _forMana = null;
        _manaCameBack = null;
        if (!_waiting) return;
        _waiting = false;
        _lastOutcome = $"{LocalTime()} {why}";
        _log?.Info("Loop", $"the wait outside the lair is over, buffs released — {why}");
        ReleaseTheWalker(why);
    }

    // What the waits outside one lair have learned about it.
    private void ForgetDoorstep()
    {
        _doorstep = null;
        _prioritySends.Clear();
        _givenUpOn.Clear();
        _ranOutHere = false;
        _manaNotWaitedForSaid = false;
    }

    // One gate while the wait is for the cast, another while it is for mana, so the
    // Navigation window can say which. The new one goes up before the old one comes
    // down: the walker never sees a moment with neither.
    private void HoldTheWalker()
    {
        (string gate, string other, string reason) = _forMana is null
            ? (MovementCoordinator.LairDebuffGate, MovementCoordinator.LairDebuffManaGate,
                "waiting for the between-round cast")
            : (MovementCoordinator.LairDebuffManaGate, MovementCoordinator.LairDebuffGate,
                "waiting for mana for the debuff");
        _coordinator.AssertGate(gate, Asserter, reason);
        _coordinator.ClearGate(other, Asserter, reason);
    }

    private void ReleaseTheWalker(string reason)
    {
        _coordinator.ClearGate(MovementCoordinator.LairDebuffGate, Asserter, reason);
        _coordinator.ClearGate(MovementCoordinator.LairDebuffManaGate, Asserter, reason);
    }

    // ----- What it is told ---------------------------------------------

    // A pass of the cast scheduler is starting (it runs on every change of "in
    // combat", among much else). The hold put up at the go-ahead comes down here for
    // what has happened since the last look; NoteDebuffSent and NoteNoDebuffThisFight
    // take it down on the spot.
    public void Advance()
    {
        if (_waiting && FightingNow) SayAFightIsAtTheDoor();
        if (!_entryHold) return;
        DateTimeOffset now = _now();
        bool fighting = FightingNow;
        if (_fightSeenAt is { } since)
        {
            if (!fighting) ReleaseBuffs("the fight ended with no debuff sent");
            else if (now - since >= HeldIntoAFight)
                ReleaseBuffs("the fight is two rounds old with no debuff sent");
        }
        // The go-ahead was given and a monster came to where the loop still stands:
        // that fight is not the lair's, and holds nothing. The loop asks again after it.
        else if (fighting && StillAtTheDoor)
            ReleaseBuffs("a fight at the door before the step in");
        else if (now >= _blockUntil)
            ReleaseBuffs($"no fight started within {EntryWindow.TotalSeconds:0} seconds of the go-ahead");
        // The backstop's clock runs from the engage.
        else if (Engaged) _fightSeenAt = now;
        if (_entryHold) NoteWhatIsHeld();
    }

    // The combat profile's pre-attack debuff went out. That is what the buffs were
    // held for: from here the cast scheduler's ordinary rules decide (a buff's own
    // "during combat" setting says whether it can go out mid-fight).
    public void NoteDebuffSent(string spell)
    {
        if (!_entryHold) return;
        _entryHold = false;
        _lastRelease = $"{LocalTime()} debuff {spell} sent";
        _log?.Info("Loop", $"debuff {spell} sent: buffs released");
    }

    // The combat engine opened a fight with no debuff to cast first (none allowed on
    // this target, its mana or its cap not met): there is nothing to keep the cast for.
    // Only for a fight in the lair this hold is for. The engine says the same of a
    // monster it engages where the loop still stands, and letting the buffs go on that
    // would hand one of them the round's cast as the loop steps in: the lair's debuff
    // put off a round, the very thing the hold is for.
    public void NoteNoDebuffThisFight()
    {
        if (_entryHold && InTheLair) ReleaseBuffs("the attack went out with no debuff to cast first");
    }

    private void ReleaseBuffs(string why)
    {
        _entryHold = false;
        _lastRelease = $"{LocalTime()} {why}";
        _log?.Info("Loop", $"buffs released: {why}");
    }

    // A buff not ticked priority went out (named as BuffsDue names it): the ones this
    // hold kept back have had their cast. During a wait for mana only the buffs that
    // bring mana back go out, and each of them counts toward its limit of sends.
    public void NoteOrdinaryBuffCast(string buff)
    {
        _ordinaryHeldUnpaid = false;
        _owedRanOutOn = null;
        if (_givingOwedCast) _owedCastWentOut = true;
        if (_waiting && _forMana is not null) CountASend(buff, "buff");
    }

    // The cast scheduler sent a buff ticked "Priority buff" (named as BuffsDue names
    // it). While the loop stands outside a lair for those, each one going out is the
    // wait getting somewhere, and restarts the time it may go without one.
    public void NotePriorityBuffCast(string buff)
    {
        if (_priorityHeldUnpaid)
        {
            _priorityHeldUnpaid = false;
            _owedRanOutOn = null;
        }
        // The owed cast is one cast: a priority buff taking it ends it as any other does.
        if (_givingOwedCast) _owedCastWentOut = true;
        if (!_waiting) return;
        if (_mode == LairEntryDebuffMode.WaitForPriorityBuffs) _lastPriorityCast = _now();
        else if (_forMana is null) return;
        CountASend(buff, "priority buff");
    }

    // One more send of a buff the loop stands here letting through: a priority buff in
    // a WaitForPriorityBuffs wait, a buff that brings mana back in a wait for mana. One
    // that has gone out as often as one may is given up on at this doorstep: it is held
    // back with the other buffs, and the wait goes on for the rest.
    private void CountASend(string buff, string what)
    {
        int sends = _prioritySends[buff] = _prioritySends.GetValueOrDefault(buff) + 1;
        if (sends != MaxSendsOfOnePriorityBuff)
        {
            _log?.Info("Loop", sends == 1
                ? $"{what} {buff} cast while waiting outside the lair"
                : $"{what} {buff} cast again while waiting outside the lair (sent {sends} times)");
            return;
        }
        _givenUpOn.Add(buff);
        _log?.Info("Loop",
            $"{what} {buff} went out {sends} times outside this lair, the most one may — it is held back "
            + "with the other buffs now, and the wait goes on for the rest");
    }

    // A fight where the loop stands waiting: said once a fight.
    private void SayAFightIsAtTheDoor()
    {
        if (_doorFightSaid) return;
        _doorFightSaid = true;
        _log?.Info("Loop", "a fight at the door: buffs are free, the wait resumes after");
    }

    // A monster is swinging at the character where the loop stands and the loop
    // doesn't fight there: said once while it lasts, since the step that follows
    // walks into a lair with it in tow.
    private void SayAnAttackGoesUnanswered()
    {
        if (!AttackedUnanswered)
        {
            _unansweredSaid = false;
            return;
        }
        if (_unansweredSaid) return;
        _unansweredSaid = true;
        _log?.Info("Loop",
            "a monster is attacking at the door and the loop doesn't fight in this room — not waited out: "
            + "the loop enters the lair as it would without it");
    }

    // ----- Deciding ------------------------------------------------------

    // True when the step may go. Told apart from the poll's own look by _deciding:
    // the cast scheduler's reads made from in here are the wait's own.
    private bool Decide()
    {
        _deciding = true;
        try { return DecideNow(); }
        finally { _deciding = false; }
    }

    private bool DecideNow()
    {
        DateTimeOffset now = _now();
        if (!_waiting && !_slotWasUsed && _roundsWaited == 0)
        {
            _waitStarted = now;
            _lastPriorityCast = now;
            _ranOutThisWait = false;
            _waitLineSaid = false;
            _forMana = null;
            _manaCameBack = null;
            _stoodForMana = false;
        }

        bool used = _slotUsed();
        if (used && !_slotWasUsed) _roundsWaited++;     // a cast took a round while we stood here
        _slotWasUsed = used;

        // A fight where the loop stands (a monster walked out, or one followed it):
        // the debuff is for the lair, so nothing is decided until the fight is over.
        // What the wait holds is left as it is; BlockingBuffs sets it aside for the
        // fight, and it is in force again the moment the fight ends.
        if (FightingNow)
        {
            SayAFightIsAtTheDoor();
            return false;
        }
        _doorFightSaid = false;
        SayAnAttackGoesUnanswered();

        if (GivingTheOwedCast(now, used)) return false;

        // WaitForSpells is bounded by rounds and time. WaitForPriorityBuffs waits for
        // every priority buff however many are due, so it is bounded by time without
        // one going out; once that has run out at a doorstep it isn't waited again there.
        // A wait that has come to stand for mana is past its casts: its limits stay as
        // they were when it got there.
        if (_forMana is null)
        {
            _limitReached = _mode switch
            {
                LairEntryDebuffMode.WaitForSpells =>
                    _roundsWaited >= MaxRoundsWaited ? WaitLimit.Rounds
                    : now - _waitStarted >= MaxWait ? WaitLimit.Time
                    : WaitLimit.None,
                LairEntryDebuffMode.WaitForPriorityBuffs =>
                    _ranOutThisWait ? WaitLimit.NothingWentOut
                    : _ranOutHere ? WaitLimit.RanOutBefore
                    : now - _lastPriorityCast >= MaxWait ? WaitLimit.NothingWentOut
                    : WaitLimit.None,
                _ => WaitLimit.None,
            };
            if (_limitReached == WaitLimit.NothingWentOut) _ranOutThisWait = _ranOutHere = true;
        }
        bool waitsForCasts = _mode is LairEntryDebuffMode.WaitForSpells or LairEntryDebuffMode.WaitForPriorityBuffs;
        // While it stands for mana every buff waits: one cast now would spend what the
        // wait is for.
        bool letSpellsCast = waitsForCasts && _limitReached == WaitLimit.None && _forMana is null;
        _blockWhileWaiting = !letSpellsCast;
        // Set ahead of the castDue read below: a buff this holds back is not counted
        // as due, so the wait is only for the priority buffs (and heals and cures).
        _blockOrdinaryWhileWaiting = _mode == LairEntryDebuffMode.WaitForPriorityBuffs;
        NoteWhatIsHeld();

        if (used) return false;
        // The cast is free. In a mode that waits a due spell is about to take it; let it.
        if (letSpellsCast && _castDue()) return false;
        return !ShortOfManaForTheDebuff(now);
    }

    // A buff is due and this hold is keeping it back: remembered, so that if none of
    // its kind has gone out by the next lair's door, it gets its cast there.
    private void NoteWhatIsHeld()
    {
        if (!_ordinaryHeldUnpaid && BlockingOrdinaryBuffs && DueBuffs(priority: false).Count > 0)
            _ordinaryHeldUnpaid = true;
        if (!_priorityHeldUnpaid && BlockingBuffs && DueBuffs(priority: true).Count > 0)
            _priorityHeldUnpaid = true;
    }

    // The due buffs this hold kept back and has seen no cast of since.
    private List<string> DueHeldUnpaid()
    {
        List<string> due = [];
        if (_ordinaryHeldUnpaid) due.AddRange(DueBuffs(priority: false));
        if (_priorityHeldUnpaid) due.AddRange(DueBuffs(priority: true));
        return due;
    }

    // Asked once as the loop comes to a lair's door: is a cast owed here? Yes when a
    // buff this hold kept back is due and none of its kind has gone out since, unless
    // the last owed cast ran out on those very buffs.
    private bool OwedACast()
    {
        List<string> due = DueHeldUnpaid();
        return due.Count > 0 && string.Join("|", due) != _owedRanOutOn;
    }

    // An earlier lair's hold kept buffs back, and none has been cast since: the fight
    // ended and the loop was at this doorstep before the cast scheduler had a pass
    // (its pass at the kill waits for HP to settle), or the buff only casts out of
    // combat and the kill took one round. Holding them again here, lair after lair in
    // a row, they would never go out at all. So this doorstep first lets one cast go.
    // One window, never restarted; it is over at the first buff of any kind that goes
    // out in it, when the cast is free with nothing due, or when its time is up. The
    // buffs a WaitForPriorityBuffs wait gave up on here stay held through it. Wait
    // for spells holds nothing at a doorstep and needs none of this.
    private bool GivingTheOwedCast(DateTimeOffset now, bool used)
    {
        if (!_castOwedHere) return false;
        if (_mode is not (LairEntryDebuffMode.BlockSpells or LairEntryDebuffMode.WaitForPriorityBuffs))
        {
            _castOwedHere = false;
            return false;
        }
        if (_owedCastUntil is null)
        {
            _owedCastUntil = now + OwedCastWindow;
            _owedCastWentOut = false;
            _log?.Info("Loop",
                "buffs an earlier lair held have had no cast since — one goes out here before they are held "
                + $"again (due: {JoinOrNone(DueHeldUnpaid())})");
        }
        _givingOwedCast = true;
        _blockWhileWaiting = false;
        _blockOrdinaryWhileWaiting = false;
        if (!_owedCastWentOut && now < _owedCastUntil && (used || _castDue())) return true;

        // Time up with none gone out: these can't be cast. Not owed again as they are.
        if (!_owedCastWentOut && now >= _owedCastUntil) _owedRanOutOn = string.Join("|", DueHeldUnpaid());
        _castOwedHere = false;
        _owedCastUntil = null;
        _givingOwedCast = false;
        // The wait proper starts now: its own time without a priority buff going out
        // is not spent by the cast it began with.
        _lastPriorityCast = now;
        return false;
    }

    // The cast is free and nothing more is waited for. The debuff the wait is for needs
    // mana of its own (the slot's "min mana per cast", and never less than the spell
    // costs; with two debuffs set, the higher need of the two), and under it the
    // combat engine won't cast it: walking in now would be the very entry without a
    // debuff this hold exists to prevent. So the loop stays until mana is back (user,
    // 2026-10-10: "we should also hold to get mana back and make a program log entry
    // saying why we're waiting"). No time limit: resting, if the player's rest settings
    // call for it here, or standing regen, bring the mana back, and Stop, a pause or
    // the master switch end the wait. A minimum mana can't reach is not waited for:
    // one over maximum mana, or one over what the game calls full when the maximum
    // the client believes in overshoots it.
    private bool ShortOfManaForTheDebuff(DateTimeOffset now)
    {
        (string Spell, int Needed, int Mana, int MaxMana, bool IsSpellCost)? read = _debuffMana?.Invoke();
        if (read is not { } need || need.Mana >= need.Needed)
        {
            if (_forMana is not null) _manaCameBack = read;
            _forMana = null;
            return false;
        }
        string? outOfReach =
            need.Needed > need.MaxMana
                ? $"maximum mana is {need.MaxMana}: that can never be met"
            : _manaIsFull?.Invoke() == true
                ? $"mana is full at {need.Mana}: it will come back no higher"
            : null;
        if (outOfReach is not null)
        {
            if (!_manaNotWaitedForSaid)
            {
                _manaNotWaitedForSaid = true;
                _log?.Info("Loop",
                    $"the debuff {need.Spell} asks for {need.Needed} mana ({Basis(need.IsSpellCost)}) and {outOfReach}, "
                    + "so the lair is entered without waiting for it");
            }
            _forMana = null;
            return false;
        }

        bool saidBefore = _forMana is not null;
        _forMana = need;
        _stoodForMana = true;
        _blockWhileWaiting = true;
        if (!saidBefore || now - _manaSaidAt >= ManaLogEvery)
        {
            _manaSaidAt = now;
            _log?.Info("Loop",
                $"waiting outside the lair for mana: {need.Mana} of the {need.Needed} the debuff {need.Spell} needs "
                + $"({Basis(need.IsSpellCost)}) — buffs held back meanwhile, bar the ones that bring mana back; "
                + "a monster in this room is fought");
        }
        if (!saidBefore) ManaWaitStarted?.Invoke();
        return true;
    }

    // Which of a debuff's two figures set what it needs: with two debuffs set, the one
    // named is the one that asks for more.
    private static string Basis(bool isSpellCost) => isSpellCost ? "its mana cost" : "its min mana per cast";

    private void GoIn()
    {
        string reason = EntryReason();
        _generation++;
        _blockWhileWaiting = false;
        _blockOrdinaryWhileWaiting = false;
        _entryHold = true;
        _fightSeenAt = null;
        _blockUntil = _now() + EntryWindow;
        NoteWhatIsHeld();
        _roundsWaited = 0;
        _slotWasUsed = false;
        _limitReached = WaitLimit.None;
        _forMana = null;
        _manaCameBack = null;
        if (!_waiting) return;
        _waiting = false;
        _lastOutcome = $"{LocalTime()} {reason}";
        _log?.Info("Loop", reason);
        ReleaseTheWalker(reason);
    }

    // ----- What it says --------------------------------------------------

    // Said once a wait, when the wait proper begins: not while the owed cast is being
    // given (nothing is held then), nor during a fight where the loop stands, nor for
    // a wait that is for mana alone (that has its own line).
    private void SayWhatTheWaitIsFor()
    {
        if (_waitLineSaid || _givingOwedCast || _forMana is not null || FightingNow) return;
        _waitLineSaid = true;
        _log?.Info("Loop", _mode switch
        {
            LairEntryDebuffMode.BlockSpells =>
                "waiting outside the lair until the debuff can be cast — buffs held back",
            LairEntryDebuffMode.WaitForPriorityBuffs =>
                "waiting outside the lair until the debuff can be cast — casting priority buffs first "
                + $"(due: {JoinOrNone(WaitedFor())}); other buffs held back (due: {JoinOrNone(HeldBack())})",
            _ => "waiting outside the lair until the debuff can be cast — casting what is due first",
        });
    }

    // Why the step is going now. A wait that ran into a limit says which, and names
    // what it was waiting for that is still due: that could not be cast in time.
    private string EntryReason()
    {
        const string Entering = "holding the buffs back and entering the lair on a free between-round cast";
        if (_stoodForMana)
            return _manaCameBack is { } back
                ? $"mana is back at {back.Mana}, and the debuff {back.Spell} needs {back.Needed} — entering the lair"
                : "mana for the debuff is no longer waited for (the debuff was taken off, or mana is as full as "
                    + "it gets) — entering the lair on a free between-round cast";
        switch (_limitReached)
        {
            case WaitLimit.NothingWentOut:
                return $"no priority buff went out for {MaxWait.TotalSeconds:0} seconds "
                    + $"({StillDue(WaitedFor(), "priority buffs") ?? "nothing still due"}) — {Entering}";
            case WaitLimit.RanOutBefore:
                return $"the wait outside this lair already ran out its {MaxWait.TotalSeconds:0} seconds "
                    + $"({StillDue(WaitedFor(), "priority buffs") ?? "nothing still due"}) — {Entering}";
            case WaitLimit.Rounds or WaitLimit.Time:
                if (StillDue(DueBuffs(priority: true).Concat(DueBuffs(priority: false)), "buffs") is not { } stillDue)
                    break;
                string limit = _limitReached == WaitLimit.Rounds
                    ? $"{MaxRoundsWaited} rounds of casts"
                    : $"{MaxWait.TotalSeconds:0} seconds";
                return $"waited the limit of {limit} ({stillDue}) — {Entering}";
        }
        if (_mode != LairEntryDebuffMode.WaitForPriorityBuffs)
            return "the between-round cast is free — entering the lair";
        string givenUpOn = _givenUpOn.Count == 0 ? string.Empty
            : $" (held back after {MaxSendsOfOnePriorityBuff} sends: {JoinOrNone(_givenUpOn)})";
        return $"no priority buff is due{givenUpOn} and the between-round cast is free — entering the lair";
    }

    // What a wait that ran out was still waiting for: the buffs named, and the heal
    // or cure that was due (a wait can run out on one of those alone). Null for nothing.
    private string? StillDue(IEnumerable<string> buffs, string what)
    {
        List<string> due = [];
        if (string.Join(", ", buffs) is { Length: > 0 } names) due.Add($"{what} still due: {names}");
        if (_healOrCureDue?.Invoke() is { Length: > 0 } heal) due.Add($"a heal or cure still due: {heal}");
        return due.Count == 0 ? null : string.Join("; ", due);
    }

    // The bug report prints this beside the program log, whose times are local.
    private string LocalTime() => $"{_now().ToLocalTime():HH:mm:ss}";

    private string PrioritySends() => _prioritySends.Count == 0
        ? "none"
        : string.Join(", ", _prioritySends.Select(s => s.Value == 1 ? s.Key : $"{s.Key} x{s.Value}"));

    private IReadOnlyList<string> DueBuffs(bool priority) => _buffsDue?.Invoke(priority) ?? [];

    // The due priority buffs the wait is for, and the due buffs it keeps back: the
    // ones not ticked priority, and the priority ones given up on here.
    private IEnumerable<string> WaitedFor() => DueBuffs(priority: true).Where(b => !_givenUpOn.Contains(b));

    private IEnumerable<string> HeldBack() =>
        DueBuffs(priority: false).Concat(DueBuffs(priority: true).Where(_givenUpOn.Contains));

    private static string JoinOrNone(IEnumerable<string> names) =>
        string.Join(", ", names) is { Length: > 0 } joined ? joined : "none";

    // ----- The wait's own timer -----------------------------------------

    private void SchedulePoll()
    {
        int generation = _generation;
        _scheduleAfter(_forMana is null ? Poll : ManaPoll, () =>
        {
            if (generation != _generation || !_waiting) return;
            if (WhyNoLongerWanted() is { } why) Reset(why);
            else if (Decide()) GoIn();
            else
            {
                SayWhatTheWaitIsFor();
                HoldTheWalker();
                SchedulePoll();
            }
        });
    }

    // A wait stands until the loop asks again, and a loop that was stopped, paused by
    // hand or switched off doesn't: left alone, the wait would hold its gate and the
    // buffs until its own limits ran out, over whatever the player did next.
    private string? WhyNoLongerWanted() =>
        _coordinator.IsGateAsserted(MovementCoordinator.UserGate) ? "paused by hand"
        : _coordinator.IsGateAsserted(MovementCoordinator.AutoAllGate) ? "the master switch is off"
        : _stillWanted?.Invoke() == false ? "the loop is no longer about to enter a lair it waits outside"
        : null;
}
