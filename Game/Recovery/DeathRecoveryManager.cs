using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Combat;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Recovery;

// Death observation aggregator. Composes DeathLineWatcher.PlayerDied and the
// per-character CharacterProfile.DeathHistory into a live observable shape that
// the Workshop DEATH section binds to.
//
// Surfaces the loaded profile's Records (the deathpile grid), the persisted
// AutoRecover / AutoEquip toggles, and the recovery actions. The recovery state
// machine drives each record's Active → Partial → Recovered transition off
// observed room re-entry and the recovery lines ("You took <item>." on Stock,
// "You have recovered the corpse of <name>." on Paradigm). Current lives are
// surfaced separately on the Character Info tab (from PlayerStats.Lives), not
// here.
//
// The @comeback remote command is a separate party-pickup flow (stranded-
// follower → leader) owned by PartyComebackManager, not this aggregator — it has
// nothing to do with death recovery.
public sealed partial class DeathRecoveryManager : ObservableObject, IDisposable
{
    // LogService category — the [DeathRecovery] rows.
    public const string LogCategory = "DeathRecovery";

    private readonly DeathLineWatcher _deathWatcher;
    private readonly ProfileService _profile;
    private readonly RoomTracker _roomTracker;
    private readonly LogService? _log;
    private AutoWalkManager? _walker;
    private Action<byte[]>? _wireSender;
    private LineExtractor? _lines;
    private Func<InventorySnapshot>? _inventorySnapshot;
    private Func<IReadOnlyList<TranscriptSnapshot.Line>>? _transcriptTail;
    private bool _disposed;

    // In-progress recovery context — one deathpile at a time (you stand in one
    // room). _activeRecovery is the record we're recovering. Paradigm's deathpile
    // is a single "corpse of <given-name>" object recovered by ONE `recover corpse
    // <name>` command (not a per-item get), so there's no per-item remaining set:
    // the corpse either shows in the room's "You notice" survey (recover it) or it
    // doesn't (mark Missing). Stock's is loose items, got one at a time.
    // _grabOnSurvey arms the grab for the next survey to be read in the room. A
    // room prints its floor BEFORE the exits line that confirms the move, so on a
    // walk-in the arrival's own survey has gone by when the room change fires: it
    // is read from the floor list there and then, and otherwise the grab waits for
    // the next display of the room (a look, a search's reveal, the display after
    // logging back in). _armedAt is when it was armed: a survey is this room's only
    // if no move has gone out since.
    // _pendingRecoverNow is a record the user pressed "Recover Now" on while away —
    // it forces a grab on arrival even when Auto-Recover is off, until that walk is
    // stopped, fails or is called off.
    private DeathRecord? _activeRecovery;
    private DeathRecord? _pendingRecoverNow;
    private bool _recoverNowWalking;
    private bool _grabOnSurvey;
    private DateTimeOffset _armedAt;
    private GroundItemTracker? _groundItems;

    // Combat-aware re-equip interleaving. Recovering the corpse itself
    // (`recover corpse`) doesn't break combat, but each re-equip (wear/eq) DOES —
    // exactly like a between-round cast (see OutboundCastObserver). So when the
    // corpse comes back in a room with a live hostile, the wear/eq burst is paced
    // a few pieces per combat round instead of firing all at once, letting the
    // weapon attack keep landing between bursts; the remainder is flushed the
    // moment the room clears. _pendingEquip holds the ordered pieces still to go
    // on; _equipRoom pins the room they belong to (abandon if we leave it). All
    // delegates are wired by AppServices after the combat engine exists; unbound
    // (no hostile), ReequipAllWorn sends everything at once as before.
    private readonly Queue<DeathItem> _pendingEquip = new();

    // The master switch (the Auto-All kill switch): with automation silenced, a
    // corpse the user recovers by hand must not set off a burst of wear / eq
    // commands — they are playing the character themselves, often with something
    // hostile in the room (report paradigm-20261004-201808). The pieces wait here
    // and go on when Auto-All is switched back on. Unbound = always enabled.
    private Func<bool>? _isAutoEnabled;
    private readonly List<DeathItem> _heldEquip = new();
    private DateTimeOffset _heldEquipSince;

    // Deathpile gear handed back by another player — a party member who recovered
    // our corpse and gave the items over (a follower never walks back to its own
    // pile; the leader does). Each received item is struck off the open pile as it
    // lands; once the hand-off burst goes quiet the pile is finalised and the worn
    // half re-equipped, exactly like recovering it ourselves (report
    // paradigm-20260926-102406: gear given back sat unworn in the pack).
    private DeathRecord? _handedBack;
    private string _handedBackBy = "";
    private int _handedBackSettleTicks;
    private const int HandBackSettleTicks = 3;
    private (int Map, int Room)? _equipRoom;
    private bool _recoveryGateHeld;
    private Func<bool>? _hostilesPresent;
    private Action? _armCombatResume;
    private Action? _assertRecoveryGate;
    private Action? _clearRecoveryGate;
    private Func<string, int>? _armourClass;

    // Pieces re-equipped per combat round while a hostile is up. A wear/eq breaks
    // the round, so ~4 fits the 5 s round window and still leaves time for the
    // re-attack to land before the next round (user-tuned "3-4 pieces / ~3 s").
    private const int EquipBurstPerRound = 4;

    // Stock ground recovery. On Stock, death scatters items LOOSE on the floor
    // (Paradigm packs them in a corpse — the path is chosen by _isParadigm). We
    // `get <name>` each pile item that's actually in the survey (never an absent
    // one — that was the old get-spam), confirmed one at a time by "You took
    // <name>."; when the whole pile is back the record finalises and worn gear
    // re-equips (paced by the combat interleave). Items not on this floor stay
    // unrecovered — the pile holds at Partial, retried on re-entry (and, on a
    // deliberate recovery, swept from the rooms it spilled into). _stockRecovering
    // scopes the "You took" tracking to our own in-progress grab.
    private Func<bool>? _isParadigm;
    // Live in-game self-name (PartyManager.LocalCharacterName). Preferred over the
    // possibly-stale CharacterProfile.Name when matching our own corpse. See
    // AttachLiveSelfName.
    private Func<string?>? _liveSelfName;
    private bool _stockRecovering;
    // Heartbeats of quiet before the death-room `get` burst counts as settled (so
    // the spillover sweep can start on the leftovers). Reset by each "You took".
    private int _stockSettleTicks;
    // A deliberate recovery (Recover Now, or a walk that ENDED in the death room)
    // earns the spill sweep; a pass-through walk does not.
    private bool _deliberateRecovery;
    // That walk was another engine's (a loop's approach or flee return, an Auto-Lair
    // hop, an errand), not one the user asked for: the sweep it earns only peeks and
    // walks to a neighbour the peek saw our items in. Whose walk it was is asked at
    // the heartbeat after the arrival (_classifyOwed), not inside the room change:
    // there an errand that has just reached its goal still reads as driving, and a
    // loop whose approach walk has just ended doesn't yet.
    private bool _engineArrival;
    private bool _classifyOwed;
    // Heartbeats a deferred sweep has left to become startable; the sweep's own
    // time budget, counted from when it was put off.
    private int _deferBudget;
    // Set when a deliberate Stock recovery wanted to sweep but a hostile was still
    // in the death room — the heartbeat starts the sweep once the room clears.
    private bool _stockSweepPending;
    // Pass-through spillover grab (Stock only): auto-recover walking through a room
    // ADJACENT to an unrecovered deathpile grabs our overflow there in-stride, no
    // detour. Kept separate from the death-room grab so both can run on one walk.
    private DeathRecord? _spilloverPile;
    private bool _spilloverGrabOnSurvey;
    private bool _spilloverRecovering;
    // Which survey an armed pass-through grab took (by when it was read).
    private DateTimeOffset _spilloverTookSurveyAt;
    private readonly DeathGroundSweep _sweep;

    // Spillover COLLECT: after the LOOK sweep, we walk the rooms the engine would
    // have spilled into (GAME_MECHANICS "Deathpile — where the items go"): first the
    // neighbours the looks showed holding our items, then the rest of the engine's
    // order (DeathSpillOrder), and when those run out with items still missing, the
    // trail walked before the death, where the engine hides what found no floor.
    // Each stop is a WalkTo through the normal walker (it opens the door, searches
    // for the hidden exit and disarms the trap on the way, or reports it can't),
    // a grab on CONFIRMED arrival, and for a trail room a `sea` for the hidden side.
    // It stops as soon as nothing is missing, then walks back to the death room.
    // Keyed off room arrivals, not the walker's Finished. _sweepStops is the whole
    // plan and _sweepNext the next stop to try, kept for the bug report;
    // _collectRecord/_collectHome pin the pile + the death room to return to.
    private enum CollectPhase { None, WalkingOut, Grabbing, Searching, WalkingBack }
    private enum StopKind { Spill, Trail }
    private readonly record struct SweepStop(RoomKey Room, StopKind Kind);
    private CollectPhase _collectPhase;
    private readonly List<SweepStop> _sweepStops = new();
    private int _sweepNext;
    private SweepStop _collectStop;
    private RoomKey _collectHome;
    private DeathRecord? _collectRecord;
    private int _collectSettle;
    private int _collectTimeout;
    private bool _sweepNeighboursOnly;
    private int _sweepWalked;
    private int _sweepUnseenStops;
    private int _sweepBudget;
    private int _returnBudget;
    private int _legRooms;
    private bool _trailQueued;
    private int _searchesLeft;
    private string _sweepEnded = "";
    private DateTimeOffset _peekStartedAt;
    // The floor as it read on arriving at a stop, and whether its gets are still to
    // be sent: they wait out a hold on sends, and the live floor list is emptied by
    // the room change itself.
    private List<string>? _arrivalFloor;
    private bool _grabOwed;
    // The combat tracker said a move carried us out of a room with a hostile engaged.
    private bool _abandonedCombat;
    // `get`s already sent at the stop we're standing in, by normalized name. The
    // floor is read on arrival and again when a search reveals more; this keeps the
    // second read from asking twice for a unit the first already asked for.
    private readonly Dictionary<string, int> _stopAsked = new(StringComparer.OrdinalIgnoreCase);
    // The walker's own word on the leg in flight, acted on at the next heartbeat:
    // its events are raised mid-teardown, where starting another walk would be
    // wiped by the reset that follows.
    private string? _legFailed;
    private string? _legStopped;
    private bool _issuingLeg;
    // The walk back ended one room short of a death room marked "stop before
    // entering": as near as the walker goes, and where the sweep finishes.
    private bool _issuingReturn;
    private bool _homeReachedShort;
    private Func<RoomKey, Room?>? _roomLookup;
    private Func<bool>? _movementHeld;
    private Func<RoomKey, bool>? _isStashRoom;
    private Func<bool>? _otherEngineDrives;
    private Func<RoomKey, bool>? _engineWalkEndedAt;
    private Func<bool>? _restHeld;
    private Func<bool>? _userPaused;
    private Func<bool>? _autoSearchesRooms;
    private Action<RoomKey>? _noteRoomSearched;
    private Func<string, bool>? _staysOnDeath;
    // `Your <item> has returned to its rightful place.` lines seen and not yet pinned
    // on a death: they print before the lives readout that makes the record.
    private readonly List<(string Item, DateTimeOffset At)> _returnedLines = new();
    // The death-room grab has nothing (more) to wait for, and what comes next is
    // decided at the next heartbeat, never inside the room change or survey that
    // found it out: a walk-in whose floor printed nothing or nothing of ours, and a
    // Recover Now from inside the room once its `look` has shown its exits line, by
    // which the floor has printed if there is one. No timer stands in for a reply: a
    // slow one would be read as an empty floor, and its late survey as the first
    // neighbour's.
    private bool _settleOwed;
    // The `look` a Recover Now sent from inside the death room has not been answered
    // by a display of that room yet (DeathRoomShownAgain).
    private bool _lookExitsOwed;
    private DateTimeOffset _lastSurveyAt;
    // A floor line has been read since the last exits line: it belongs to the room
    // display now printing.
    private bool _floorSinceExits;
    // The pile marked Missing on this visit to its room: a floor that shows the
    // corpse after all, before we leave, takes the verdict back.
    private DeathRecord? _missingThisVisit;
    private Func<string, bool>? _isKeyItem;

    // Heartbeats (1 s) of quiet after the death-room `get` burst before it counts
    // as settled and the sweep can start on the leftovers.
    private const int StockSettleTicks = 2;
    // Heartbeats to let a stop's grab confirm before moving on.
    private const int CollectSettleTicks = 2;
    // Heartbeats without reaching a new room before a leg is given up — a trap we
    // can't disarm (or any stall) halts the walker, so we skip and move on. Not
    // counted while a movement gate holds the walker (a fight, a rest, a pause).
    private const int CollectWalkTimeoutTicks = 20;
    // Rooms one sweep walks to. A floor holds 17 objects, so twelve is over 200
    // slots, more than one character drops; items still missing past that were
    // taken by someone, and the walk would only be a tour of empty rooms.
    private const int MaxSweepStops = 12;
    // Rooms in the walker's route to one stop. A spill room is at most five exits
    // from the death room, so ten crosses from one side of it to the other and
    // twelve allows a short way round; longer than that is the walker going a long
    // way round an exit it can't pass.
    private const int MaxLegRooms = 12;
    // Heartbeats a sweep may take up to its walk back, fights and rests on the way
    // included. It sends a character who has just died walking with part of its gear
    // missing.
    private const int SweepBudgetTicks = 600;
    // Heartbeats the walk back may take on top, held or not. Twelve rooms is under a
    // minute's walk; two leave room for a door or a fight, and a walk back held longer
    // than that is dropped rather than left to set off whenever the hold lifts.
    private const int ReturnBudgetTicks = 120;
    private static readonly string OutOfTime = $"out of time ({SweepBudgetTicks} s)";
    // Searches in one trail room. Each hidden item is found on its own Perception
    // roll (GAME_MECHANICS "Hiding items in a room (stashing)"), so one can miss.
    private const int TrailSearchTries = 2;
    // The lines of one death arrive together; the window only keeps a line from
    // some other moment off this death's record.
    private static readonly TimeSpan ReturnedLineWindow = TimeSpan.FromSeconds(30);

    private readonly Func<DateTimeOffset> _now;

    public DeathRecoveryManager(
        DeathLineWatcher deathWatcher,
        ProfileService profile,
        RoomTracker roomTracker,
        LogService? log = null,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(deathWatcher);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(roomTracker);
        _deathWatcher = deathWatcher;
        _profile = profile;
        _roomTracker = roomTracker;
        _log = log;
        _now = now ?? (static () => DateTimeOffset.UtcNow);
        // The spillover LOOK sweep just peeks exits via our Send; the walk-collect is
        // driven here off confirmed room arrivals (OnRoomChanged), not by the sweep.
        _sweep = new DeathGroundSweep(Send, log);

        _deathWatcher.PlayerDied += OnPlayerDied;
        // Re-entering a room that holds one of our deathpiles drives the
        // Active → Partial → Recovered transitions (and the auto-grab).
        _roomTracker.StateChanged += OnRoomChanged;
        // A death record was just appended — snapshot the backscroll tail now,
        // before the graveyard room display floods scrollback and pushes the
        // fatal scene out of the "How did I Die?" window.
        _roomTracker.PlayerDeathObserved += OnDeathObserved;
        _roomTracker.PlayerDeathInferred += OnDeathInferred;
        _roomTracker.UnwitnessedDeathRecorded += OnUnwitnessedDeathRecorded;
        _roomTracker.MoveBlocked += OnMoveBlocked;
        _profile.ProfileLoaded += OnProfileLoaded;
        _profile.ProfileClosed += OnProfileChanged;
    }

    private void OnProfileLoaded(CharacterProfile _) => OnProfileChanged();

    // The game refused the last move: it has been answered and we are where we were.
    // An armed grab counts a survey as the death room's only if no move has gone out
    // since it was armed, so the arming is brought up to now. Without this a move
    // that bounced would shut the grab to every later display of the room. (When the
    // tracker was Confirmed the room change does the same; Suspect, it raises none.)
    private void OnMoveBlocked()
    {
        if (_grabOnSurvey && _activeRecovery is not null) _armedAt = _now();
    }

    private void OnPlayerDied(PlayerDiedEvent evt)
    {
        _log?.Info(LogCategory, $"player slain by={evt.Killer}");
        // The death record is written separately by
        // DeathDetector → RoomTracker.NoteDeath → profile.DeathHistory.
        // Nudge the grid so the new record surfaces once that write lands.
        OnPropertyChanged(nameof(Records));
    }

    // Wire the walker used by WalkToDeathRoom / RecoverNow and the spillover
    // collect legs. Set post-construction because the AutoWalkManager is built after
    // this manager in AppServices. The collect takes an ARRIVAL from a confirmed room
    // change (OnRoomChanged), never from the walker's Finished, so a `look` peek that
    // momentarily desyncs the position can't fire a premature "arrived" that grabs in
    // the wrong room. The walker's Failed and Stopped are read (OnWalkerEvent): a
    // route that can't be walked is skipped at once, and a walk someone stopped ends
    // the sweep instead of being started again over their stop.
    public void AttachWalker(AutoWalkManager walker)
    {
        ArgumentNullException.ThrowIfNull(walker);
        if (ReferenceEquals(_walker, walker)) return;
        if (_walker is not null) _walker.Event -= OnWalkerEvent;
        _walker = walker;
        _walker.Event += OnWalkerEvent;
    }

    // What the Stock spill sweep reads besides the walker:
    //   - roomLookup: the room graph, to work out where the engine spills;
    //   - movementHeld: a movement gate is holding the walker (a held leg isn't a
    //     stalled one);
    //   - isStashRoom: the character's stash rooms, never searched (a search there
    //     would dig up what was hidden on purpose);
    //   - otherEngineDrives: a loop, Auto-Lair, a solver or an errand has the
    //     character, or it is following a party leader. A sweep the user asked for
    //     doesn't start under one and ends where it stands when one begins;
    //   - engineWalkEndedAt: an engine's own walk has just ended in this room and the
    //     engine with it (a PvP flee, the walk back from one), so the arrival was not
    //     a walk the user made though nothing is driving any more;
    //   - restHeld: a rest is holding helper actions. Like the Auto-All switch, the
    //     sweep sends nothing while one is up;
    //   - userPaused: the user's own pause. Nothing is sent, and its clocks stop too,
    //     so a paused sweep waits as a paused loop does and never ends itself with
    //     the user's gate still up and nothing running under it;
    //   - autoSearchesRooms / noteRoomSearched: auto-search searches each room on
    //     entry, which counts as one of a trail stop's searches, and is told of ours
    //     so it doesn't search the same room again.
    // Unbound, a deliberate Stock recovery still grabs the death room and holds the
    // rest at Partial.
    public void AttachSpillSweep(
        Func<RoomKey, Room?> roomLookup,
        Func<bool> movementHeld,
        Func<RoomKey, bool> isStashRoom,
        Func<bool> otherEngineDrives,
        Func<RoomKey, bool> engineWalkEndedAt,
        Func<bool> restHeld,
        Func<bool> userPaused,
        Func<bool> autoSearchesRooms,
        Action<RoomKey> noteRoomSearched)
    {
        ArgumentNullException.ThrowIfNull(roomLookup);
        ArgumentNullException.ThrowIfNull(movementHeld);
        ArgumentNullException.ThrowIfNull(isStashRoom);
        ArgumentNullException.ThrowIfNull(otherEngineDrives);
        ArgumentNullException.ThrowIfNull(engineWalkEndedAt);
        ArgumentNullException.ThrowIfNull(restHeld);
        ArgumentNullException.ThrowIfNull(userPaused);
        ArgumentNullException.ThrowIfNull(autoSearchesRooms);
        ArgumentNullException.ThrowIfNull(noteRoomSearched);
        _roomLookup = roomLookup;
        _movementHeld = movementHeld;
        _isStashRoom = isStashRoom;
        _otherEngineDrives = otherEngineDrives;
        _engineWalkEndedAt = engineWalkEndedAt;
        _restHeld = restHeld;
        _userPaused = userPaused;
        _autoSearchesRooms = autoSearchesRooms;
        _noteRoomSearched = noteRoomSearched;
    }

    // Whether the named item stays on the character through a death
    // (DeathPileRules.StaysWithCharacter, read from the item's game data).
    //   isKeyItem — the named item is a key by its game data. The game keeps a key
    //               on the key ring, which the stays rule never reaches, wherever
    //               the client happened to file it.
    public void SetStaysOnDeathProbe(Func<string, bool> staysOnDeath, Func<string, bool>? isKeyItem = null)
    {
        ArgumentNullException.ThrowIfNull(staysOnDeath);
        _staysOnDeath = staysOnDeath;
        _isKeyItem = isKeyItem;
    }

    // Bind the gate-wrapped wire sender so auto-recover can send get / wear /
    // hold commands. Bound by MainWindowViewModel on connect; unbound, the grab +
    // re-equip are no-ops (status transitions still follow observed pickups).
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Bind the per-session LineExtractor so the manager reads the recovery lines
    // ("You took …", "You have recovered the corpse of …") that drive the
    // Partial → Recovered transition (and the per-item re-equip). Bound by
    // MainWindowViewModel on connect.
    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (ReferenceEquals(_lines, lines)) return;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    // Bind the room's floor-survey tracker. Auto-recover reads its parsed "You
    // notice" list to confirm the corpse is actually in the room before sending
    // `recover corpse`, and acts off its SurveyUpdated event: an armed grab takes the
    // next survey read in the room (the arrival's own has printed before the room
    // change fires; see _grabOnSurvey). Wired by AppServices once GroundItems exists
    // (it's built after this manager).
    public void AttachGroundItems(GroundItemTracker ground)
    {
        ArgumentNullException.ThrowIfNull(ground);
        if (ReferenceEquals(_groundItems, ground)) return;
        if (_groundItems is not null) _groundItems.SurveyUpdated -= OnSurveyUpdated;
        _groundItems = ground;
        _groundItems.SurveyUpdated += OnSurveyUpdated;
    }

    // Wire the combat-interleaving probes so an in-combat corpse recovery paces
    // its re-equip across rounds instead of dumping the whole wear/eq burst (which
    // would repeatedly break the round). hostilesPresent reports a live engageable
    // hostile in the room; armCombatResume nudges the combat engine to re-attack
    // on the *Combat Off* a burst produces (same signal a between-round cast arms);
    // assert/clearRecoveryGate hold the walker on the CorpseRecovery gate while
    // pieces are still going on; armourClass returns an item's game-data ArmourClass
    // for the highest-AC-first ordering. Bound by AppServices once the combat
    // engine + tick exist; the tick drives OnRecoveryCombatRound / OnRecoveryHeartbeat.
    // Unbound, ReequipAllWorn falls back to the immediate all-at-once burst.
    public void AttachCombatInterleave(
        Func<bool> hostilesPresent,
        Action armCombatResume,
        Action assertRecoveryGate,
        Action clearRecoveryGate,
        Func<string, int> armourClass)
    {
        ArgumentNullException.ThrowIfNull(hostilesPresent);
        ArgumentNullException.ThrowIfNull(armCombatResume);
        ArgumentNullException.ThrowIfNull(assertRecoveryGate);
        ArgumentNullException.ThrowIfNull(clearRecoveryGate);
        ArgumentNullException.ThrowIfNull(armourClass);
        _hostilesPresent = hostilesPresent;
        _armCombatResume = armCombatResume;
        _assertRecoveryGate = assertRecoveryGate;
        _clearRecoveryGate = clearRecoveryGate;
        _armourClass = armourClass;
    }

    public void SetAutoEnabledProbe(Func<bool> isAutoEnabled)
    {
        ArgumentNullException.ThrowIfNull(isAutoEnabled);
        _isAutoEnabled = isAutoEnabled;
    }

    // Bind the realm probe that chooses the recovery mechanic: Paradigm packs the
    // pile into a `corpse of <name>` (one `recover corpse`), Stock scatters it
    // loose on the floor (per-item `get`). Wired by AppServices from the active
    // game-data set's realm. Unbound, recovery defaults to the corpse path.
    public void SetRealmProbe(Func<bool> isParadigm)
    {
        ArgumentNullException.ThrowIfNull(isParadigm);
        _isParadigm = isParadigm;
    }

    // Bind the LIVE self-name (PartyManager.LocalCharacterName, tracked off the
    // `stat`/`par` screens) so corpse matching identifies "self" by the in-game name
    // rather than CharacterProfile.Name, which a copied profile can leave stale. The
    // live name wins; Current.Name is the fallback only when it is blank. Optional —
    // unbound, it reads Current.Name.
    public void AttachLiveSelfName(Func<string?> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _liveSelfName = provider;
    }

    // Bind the live inventory snapshot provider so SimulateDeath captures a
    // realistic deathpile. Real deaths capture via RoomTracker.NoteDeath; this is
    // only for the test button.
    public void AttachInventorySnapshot(Func<InventorySnapshot> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _inventorySnapshot = provider;
    }

    // Bind the backscroll-tail provider — the live terminal transcript's last
    // ~200 lines, oldest → newest. Bound by MainWindowViewModel, where the
    // Emulator lives. Unbound (headless / test paths), death logs simply aren't
    // captured: the record still exists, just without a "How did I Die?" replay.
    public void AttachTranscriptTail(Func<IReadOnlyList<TranscriptSnapshot.Line>> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _transcriptTail = provider;
    }

    // The loaded profile's death history (oldest → newest). Empty when no profile
    // is loaded or the lucky character has never died. The DEATH grid sorts this
    // newest-first for display.
    public IReadOnlyList<DeathRecord> Records =>
        _profile.Current?.DeathHistory is { } list ? list : Array.Empty<DeathRecord>();

    // Worn pieces still queued for the combat-paced re-equip (0 when idle) — a
    // bug report taken mid-recovery shows how far the in-combat burst has drained.
    public int PendingReequipCount => _pendingEquip.Count;

    // Worn pieces recovered while Auto-All was off, waiting for it to come back on.
    public int HeldReequipCount => _heldEquip.Count;

    // The Stock spill sweep is peeking exits or walking its plan.
    private bool SweepRunning => _sweep.Active || _collectPhase != CollectPhase.None;

    // The sweep sends nothing (no look, get, search or move) while the Auto-All switch
    // is off, a rest holds helper actions, or the user has paused. It neither starts
    // nor goes on until all three are clear.
    private bool SweepOnHold =>
        _isAutoEnabled?.Invoke() == false || _restHeld?.Invoke() == true || UserPaused;
    private bool UserPaused => _userPaused?.Invoke() == true;

    // For the movement controller, which lists the sweep with the puzzle solvers: the
    // walker is idle while it peeks, gets and searches, and without this the toolbar
    // reads those stretches as nothing running, so Stop and Pause can't reach them.
    public bool SpillSweepActive => SweepRunning;
    public bool SpillSweepHeld => SweepOnHold || _movementHeld?.Invoke() == true;
    public event Action? SpillSweepStateChanged;

    // Where the Stock spill sweep stands, for a bug report: what it is doing now, or
    // how the last one ended.
    public string SpillSweepState
    {
        get
        {
            string held = SweepOnHold ? "; sends held (Auto-All off, a rest, or paused)" : "";
            if (_sweep.Active) return "peeking the death room's exits" + held;
            if (_collectPhase == CollectPhase.None)
                return _stockSweepPending ? "waiting to start (hostile in the death room, a rest, a pause, or Auto-All off)"
                    : _sweepEnded.Length > 0 ? $"idle; the last one ended: {_sweepEnded}"
                    : "idle";
            string stop = $"{_collectStop.Room.Map}/{_collectStop.Room.Room}";
            string doing = _collectPhase switch
            {
                CollectPhase.WalkingOut => $"walking to {stop}",
                CollectPhase.Grabbing => $"getting items at {stop}",
                CollectPhase.Searching => $"searching {stop}",
                _ => $"walking back to the death room {_collectHome.Map}/{_collectHome.Room}",
            };
            return $"{doing}; {(_sweepNeighboursOnly ? "neighbours the looks showed only, " : "")}"
                + $"stop {_sweepNext} of {_sweepStops.Count}, walked to {_sweepWalked} of at most "
                + $"{MaxSweepStops}, {(_collectPhase == CollectPhase.WalkingBack ? _returnBudget : _sweepBudget)} s left"
                + (_sweepUnseenStops > 0 ? $", {_sweepUnseenStops} stop(s) dark" : "")
                + (_movementHeld?.Invoke() == true ? ", movement held by a gate" : "") + held;
        }
    }

    // Where the death-room pickup stands, for a bug report: a "Recover Now did
    // nothing" or "marked Missing with the corpse there" report turns on whether a
    // grab was armed and waiting for a display, and whether a Missing could still be
    // taken back or reopened.
    public string DeathRoomGrabState
    {
        get
        {
            var parts = new List<string>();
            if (_activeRecovery is { } active)
                parts.Add($"recovering the pile at {active.RoomKeyText}: "
                    + (_grabOnSurvey
                        ? "grab armed, waiting for a display of the room"
                            + (_lookExitsOwed ? " (Recover Now's look not answered yet)" : "")
                        : "grab spent"));
            else parts.Add("no pile being recovered");
            if (_pendingRecoverNow is { } asked)
                parts.Add($"Recover Now pending for {asked.RoomKeyText}{(_recoverNowWalking ? " (walking)" : "")}");
            if (_missingThisVisit is { } written)
                parts.Add($"marked {written.RoomKeyText} Missing on this visit (a corpse shown here takes it back)");
            return string.Join("; ", parts);
        }
    }

    // What would keep a sweep the user asked for from starting or going on right now,
    // for a bug report: a "Recover Now only grabbed the room" report turns on these.
    public string SpillSweepBlockers
    {
        get
        {
            List<string> blockers = new();
            if (_otherEngineDrives?.Invoke() == true)
                blockers.Add("another engine is driving the character, or it is following a party leader (it gives way)");
            if (_isAutoEnabled?.Invoke() == false) blockers.Add("Auto-All is off (it waits)");
            if (_restHeld?.Invoke() == true) blockers.Add("a rest is on (it waits)");
            if (UserPaused) blockers.Add("the user has paused (it waits, clocks stopped)");
            if (_roomTracker.IsInDarkRoom) blockers.Add("the room is dark (nothing can be read)");
            if (_roomTracker.EnteredBlind) blockers.Add("the room was entered blind (nothing was read)");
            if (_hostilesPresent?.Invoke() == true) blockers.Add("a hostile is in the room (it waits to start, and to search)");
            return blockers.Count == 0 ? "(nothing)" : string.Join("; ", blockers);
        }
    }

    // The rooms of the last (or running) sweep's plan in the order it tries them,
    // trail rooms marked. Empty when no sweep has run this session.
    public string SpillSweepPlan => _sweepStops.Count == 0 ? "" : DescribeStops(0);

    // The automatic grab on walking into the death room, or past a pile: the
    // setting, and the master switch being on. Recover Now forces it and is not
    // read through here.
    private bool AutoGrabAllowed => AutoRecover && _isAutoEnabled?.Invoke() != false;

    // Auto-grab a deathpile's lost items (ignoring per-item auto-get policy) when
    // re-entering the death room. Persisted per-character.
    public bool AutoRecover
    {
        get => _profile.Current?.DeathAutoRecover ?? false;
        set
        {
            if (_profile.Current is not { } p || p.DeathAutoRecover == value) return;
            p.DeathAutoRecover = value;
            _profile.Save();
            OnPropertyChanged();
        }
    }

    // Re-equip items that were worn at death after recovering them. Persisted
    // per-character.
    public bool AutoEquip
    {
        get => _profile.Current?.DeathAutoEquip ?? false;
        set
        {
            if (_profile.Current is not { } p || p.DeathAutoEquip == value) return;
            p.DeathAutoEquip = value;
            _profile.Save();
            OnPropertyChanged();
        }
    }

    // Manually flag a record as fully recovered (user pressed "Mark Recovered").
    // Sets Recovered, persists, and notifies binders.
    public void MarkRecovered(DeathRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Status == DeathRecoveryStatus.Recovered) return;
        record.Status = DeathRecoveryStatus.Recovered;
        record.RecoveryMessage = "Marked recovered by user.";
        record.HandedBack = false;
        _profile.Save();
        OnPropertyChanged(nameof(Records));
    }

    // Remove a single record from the history (and its death-log file, if any).
    public void ClearSelected(DeathRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_profile.Current?.DeathHistory is not { } list || !list.Remove(record)) return;
        DeleteDeathLog(record);
        _profile.Save();
        OnPropertyChanged(nameof(Records));
    }

    // Remove every record whose status is Recovered (and their death-log files).
    public void ClearAllRecovered()
    {
        if (_profile.Current?.DeathHistory is not { } list) return;
        List<DeathRecord> removed = list.Where(r => r.Status == DeathRecoveryStatus.Recovered).ToList();
        if (removed.Count == 0) return;
        foreach (DeathRecord r in removed)
        {
            DeleteDeathLog(r);
            list.Remove(r);
        }
        _profile.Save();
        OnPropertyChanged(nameof(Records));
    }

    // Starts a walk the user asked for, on the route cards (the UI wires it to the
    // route picker). True when a walk, or its cards, went out.
    private Func<RoomKey, bool>? _demandedWalk;
    public void SetDemandedWalk(Func<RoomKey, bool> walk)
    {
        ArgumentNullException.ThrowIfNull(walk);
        _demandedWalk = walk;
    }

    // Walk to the room a death occurred in. Returns false when no walker is
    // attached or the record has no recorded room.
    public bool WalkToDeathRoom(DeathRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Room is not { } r) return false;
        RoomKey room = new(r.Map, r.Room);
        // Only Recover Now reaches here: the user asked for this walk, so it goes
        // through the route cards like any walk-to. Without them it takes
        // the shortest route.
        if (_demandedWalk is { } ask) return ask(room);
        return _walker is not null && _walker.WalkTo(room, preferTeleportFree: false);
    }

    // Why this death's pile can't be gone for right now, or null: it was recorded on
    // another game-data set than the one whose rooms the map holds, and the same
    // room number there is another room. Nothing walks to it, arms a grab in it or
    // sweeps from it until that set is back. A record from before the set was kept
    // has no stamp and is taken as the active set's, as it always was. So is one
    // whose set is no longer installed (deleted, or renamed in Manage Sets), which
    // could never be switched back to (user, 2026-10-17). The record itself is left
    // as it is.
    public string? OtherGameDataRefusal(DeathRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.GameData is not { } set) return null;
        string? refusal = new OpenedOnGameData(set).Refusal("death", _roomTracker.RoomsGameData, "recover it");
        return refusal is not null && GameDataInstalled(set) ? refusal : null;
    }

    // Whether the named game-data set is still installed. Unbound, every set is.
    public bool GameDataInstalled(string set) => _gameDataInstalled?.Invoke(set) ?? true;

    private Func<string, bool>? _gameDataInstalled;

    // Bind the probe that says whether a game-data set is still installed, for a
    // death stamped with one that has since gone. Wired by AppServices.
    public void SetGameDataInstalledProbe(Func<string, bool> installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        _gameDataInstalled = installed;
    }

    private bool OnActiveGameData(DeathRecord record) => OtherGameDataRefusal(record) is null;

    private void NoteOtherGameData(DeathRecord record, string what) =>
        _log?.Info(LogCategory, $"{what} for death #{record.RecordNumber} at {record.RoomKeyText}: "
            + new OpenedOnGameData(record.GameData).LogReason(_roomTracker.RoomsGameData));

    // Demand signal to recover a deathpile. If we're already standing in the
    // death room, grab every recorded pile item in place (and re-equip the worn
    // ones when Auto-Equip is on); otherwise start walking there and grab on
    // arrival. The grab is forced regardless of the Auto-Recover toggle — the
    // user asked for it explicitly.
    public bool RecoverNow(DeathRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Room is not { } target) return false;
        if (OtherGameDataRefusal(record) is not null)
        {
            NoteOtherGameData(record, "Recover Now refused");
            return false;
        }
        if (SweepRunning)
        {
            _log?.Info(LogCategory, "recover-now: a spill sweep is already under way — left to finish");
            return true;
        }

        Room? here = _roomTracker.State.CurrentRoom;
        bool inRoom = here is not null && here.Key.Map == target.Map && here.Key.Room == target.Room;
        string where = record.RoomName ?? record.RoomKeyText;

        if (inRoom)
        {
            _log?.Info(LogCategory, $"recover-now: in death room {where} — surveying for the corpse");
            BeginRecovery(record, autoGrab: true, deliberate: true, arrivedByWalk: false);
            // Already standing here, so no room-change survey is coming — re-look
            // to re-render the "You notice" list, which fires SurveyUpdated and
            // drives the grab. An empty floor prints no survey at all: that is
            // settled when the tracker confirms the look's display as this room's
            // (DeathRoomShownAgain), however long the reply takes.
            _lookExitsOwed = true;
            if (_roomTracker.IsInDarkRoom || _roomTracker.EnteredBlind)
                _log?.Info(LogCategory,
                    "recover-now: the room is dark or was entered blind — the look will show nothing, and nothing is "
                    + "concluded until the room has been seen");
            Send("look");
            return true;
        }

        // Walking back: arm a one-shot force-grab so arrival recovers even
        // when Auto-Recover is off.
        // Pressed again for the same room while the first press's walk is under way
        // (walking, or stopped by a detour that resumes it): that walk is still this
        // request's, whatever becomes of the second press's route cards.
        bool underWay = _recoverNowWalking && _pendingRecoverNow is { Room: { } was }
            && was.Map == target.Map && was.Room == target.Room;
        _pendingRecoverNow = record;
        _recoverNowWalking = underWay;   // otherwise set by the walker's Started for this room
        bool walking = WalkToDeathRoom(record);
        if (walking)
            _log?.Info(LogCategory, $"recover-now: walking to {where} then recovering"
                + (underWay ? " (asked again; the walk already under way stands)" : ""));
        else if (!underWay)
            _pendingRecoverNow = null;
        return walking || underWay;
    }

    // ----- recovery state machine -------------------------------------

    // Fires on every room transition. On entering (Confirmed) a room that holds
    // an un-recovered deathpile, begin recovery; on leaving the pile we were
    // recovering, drop the in-progress tracker (the record stays Partial until we
    // return and finish).
    private void OnRoomChanged(RoomTransition t)
    {
        // The LOOK sweep is peeking exits (no real movement) — ignore transitions so a
        // peek that momentarily desyncs the tracker can't drop _activeRecovery or
        // re-arm a different pile.
        if (_sweep.Active) return;

        // The COLLECT walk owns every transition while it runs (out to a spill room,
        // back to the death room) and advances off these confirmed arrivals.
        if (_collectPhase != CollectPhase.None) { HandleCollectArrival(t); return; }

        // A pass-through grab left armed in the room before takes the first survey to
        // come, and walking on that is this room's, read before it confirmed. If so,
        // its gets have gone out for this very floor.
        DeathRecord? floorAlreadyAskedFor =
            _spilloverRecovering && _spilloverTookSurveyAt == _lastSurveyAt ? _spilloverPile : null;

        // A grab left armed is done with once anything changes: armed, it would take
        // the next survey to come, and with a move just sent that is the next room's.
        // The pickups of a grab already made are only finished with on a real change
        // of room. A walk that goes straight on sends its next move in the same
        // breath as the arrival (the room staying the same, now waiting on that
        // move), and the "You took" lines for this floor come back after it.
        bool sameRoom = t.PreviousRoom is not null && t.NewRoom is not null
            && t.PreviousRoom.Key.Equals(t.NewRoom.Key);
        _spilloverGrabOnSurvey = false;
        if (!sameRoom)
        {
            _spilloverRecovering = false;
            _spilloverPile = null;
        }

        Room? room = t.NewRoom;

        if (_activeRecovery is { Room: { } ar }
            && (room is null || ar.Map != room.Key.Map || ar.Room != room.Key.Room))
        {
            // Stock's grab is left armed on purpose after an empty floor has settled.
            if (_grabOnSurvey && !IsStock)
                _log?.Info(LogCategory, "recovery: left the death room with the grab still armed — no display of it was read, so nothing was concluded");
            _activeRecovery = null;
            _grabOnSurvey = false;
            _stockRecovering = false;
            _stockSweepPending = false;
            _settleOwed = false;
            _classifyOwed = false;
            _lookExitsOwed = false;
        }
        if (_missingThisVisit is { Room: { } gone }
            && (room is null || gone.Map != room.Key.Map || gone.Room != room.Key.Room))
            _missingThisVisit = null;

        // Left the room our paced re-equip pieces belong to (rare — the
        // CorpseRecovery gate holds the walker while pieces are pending, so this is
        // really only a manual move): stop equipping and release the gate rather
        // than putting the rest on in the wrong room.
        if (_pendingEquip.Count > 0 && _equipRoom is { } er
            && (room is null || er.Map != room.Key.Map || er.Room != room.Key.Room))
            AbandonPendingEquip();

        if (room is null || t.NewConfidence != RoomConfidence.Confirmed) return;

        DeathRecord? rec = FindRecoverableAt(room.Key);
        // A pile marked Missing is not come back to on its own, but Recover Now on
        // one is the user asking for the room to be looked at again: its arrival is
        // judged like any other, off the room's own display.
        if (rec is null && _pendingRecoverNow is { Status: DeathRecoveryStatus.Missing, Room: { } asked } again
            && asked.Map == room.Key.Map && asked.Room == room.Key.Room && OnActiveGameData(again))
            rec = again;
        if (rec is null && !sameRoom && FindRecoverableAt(room.Key, otherGameData: true) is { } elsewhere)
            NoteOtherGameData(elsewhere, "recovery not started here");
        if (rec is not null && !ReferenceEquals(_activeRecovery, rec))
        {
            bool force = ReferenceEquals(_pendingRecoverNow, rec);
            if (force)
            {
                _pendingRecoverNow = null;
                _recoverNowWalking = false;
            }
            // A walk that ended here earns the sweep. One the user asked for (Recover
            // Now's, or their own walk-to with nothing else driving) earns all of it;
            // one another engine made earns the peek and its neighbours only.
            bool walkedHere = WalkedToDeathRoom(rec);
            BeginRecovery(rec, autoGrab: AutoGrabAllowed || force, deliberate: force || walkedHere,
                arrivedByWalk: !force && walkedHere);
            // A room prints its floor before the exits line that confirms the move
            // (GAME_MECHANICS "Hiding coin in a room (stashing)", Client use), so on
            // a walk-in the arrival's survey has already been read, or the floor is
            // empty and none was printed. That is only known when the room was
            // displayed on a real arrival: not in the dark or blind (confirmed with
            // no display at all), not when the tracker settled on the room some other
            // way (a manual locate, a saved room at load), and not when the room
            // merely confirmed in place (a typed-ahead move that bounced: the floor
            // list was emptied when the step landed). Then nothing is concluded and
            // the grab stays armed, as it always did, for the next display of the
            // room.
            bool arrivedSeeing = (t.PreviousRoom is null || !t.PreviousRoom.Key.Equals(room.Key)) && t.Displayed;
            if (_grabOnSurvey && ReferenceEquals(_activeRecovery, rec) && !arrivedSeeing)
                _log?.Info(LogCategory,
                    "recovery: in the death room without a display of it to read — armed for its next display");
            if (IsStock && _grabOnSurvey && ReferenceEquals(_activeRecovery, rec) && arrivedSeeing)
            {
                if (FloorWasJustRead() && ReferenceEquals(floorAlreadyAskedFor, rec))
                {
                    // Asked for once already: don't ask again, just count the replies.
                    _grabOnSurvey = false;
                    _stockRecovering = true;
                    _stockSettleTicks = StockSettleTicks;
                    _log?.Info(LogCategory, "stock-recover: this floor's gets went out with the pass-through grab — counting them here");
                }
                else if (FloorWasJustRead()) TryGroundRecover(rec);
                // Displayed, with no floor line: empty. What follows is settled at
                // the next heartbeat, and the grab stays armed all the same: a search
                // may yet show the room's hidden side.
                else _settleOwed = true;
            }
            else if (!IsStock && _grabOnSurvey && ReferenceEquals(_activeRecovery, rec) && arrivedSeeing)
                CorpseOnArrival(rec);
            return;
        }

        // Confirmed again in the death room with a grab still armed (a move sent
        // since it was armed has bounced): whatever went out has been answered, so a
        // survey from here on is this room's.
        if (rec is not null && _grabOnSurvey) _armedAt = _now();

        // And when it was a display of the room that confirmed it, that display has
        // just ended with the grab still armed: it showed nothing to take.
        if (rec is not null && ReferenceEquals(_activeRecovery, rec) && _grabOnSurvey && t.Displayed)
            DeathRoomShownAgain(rec);

        // This room holds no deathpile of ours — but if it's adjacent to one, an
        // auto-recover pass-through grabs our overflow here in-stride (Stock only),
        // covering the rooms right before and after a death room on the route.
        TryArmSpillover(room, arrived: !sameRoom, floorAlreadyAskedFor);
    }

    // Arm a pass-through spillover grab: when auto-recover is on and the room we
    // just walked into borders an un-recovered Stock deathpile, get our overflow
    // off this floor on its next survey — no detour. Paradigm never spills (the
    // corpse holds everything), so it's Stock-only.
    //
    // The room's floor prints before the exits line that confirms the arrival, so
    // when it was read on the way in the grab is made from it here and now: armed
    // instead, it would wait for a survey that has already gone by, and take the
    // floor of whichever room came next. Armed only when this floor wasn't read (an
    // empty one prints nothing), for a later display of the room.
    //
    // The floor is read once, on arriving. A confirm in place (a move that bounced, a
    // look) reads nothing again: with pickups still coming back it leaves them be,
    // and otherwise arms for the room's next display. And a floor an armed grab from
    // the room before already asked for (its survey, read before this room
    // confirmed) is not asked for a second time, only counted.
    private void TryArmSpillover(Room room, bool arrived, DeathRecord? floorAlreadyAskedFor)
    {
        if (!AutoGrabAllowed || _isParadigm?.Invoke() == true) return;
        if (!arrived && _spilloverRecovering) return;
        if (FindPileAdjacentTo(room) is not { } dp) return;
        _spilloverPile = dp;
        string where = $"{room.Key.Map}/{room.Key.Room} borders deathpile at {dp.RoomKeyText} "
            + $"({dp.UnrecoveredItems?.Count ?? 0} still out)";
        if (arrived && ReferenceEquals(floorAlreadyAskedFor, dp))
        {
            _spilloverRecovering = true;
            _log?.Info(LogCategory, $"pass-through: {where} — its floor was asked for on the way in; counting the replies");
            return;
        }
        if (arrived && FloorWasJustRead())
        {
            int sent = GetOurItemsHere(dp);
            _spilloverRecovering = sent > 0;
            _log?.Info(LogCategory, $"pass-through: {where} — {sent} of ours on its floor{(sent > 0 ? ", get" : "")}");
            return;
        }
        _spilloverGrabOnSurvey = true;
        _log?.Info(LogCategory, $"pass-through: {where} — arming an in-stride grab");
    }

    // Newest un-recovered pile whose death room borders `room` (one of its exits
    // leads there) — the spillover target for a pass-through grab.
    private DeathRecord? FindPileAdjacentTo(Room room)
    {
        if (_profile.Current?.DeathHistory is not { } list) return null;
        DeathRecord? best = null;
        foreach (DeathRecord r in list)
        {
            if (r.Status is DeathRecoveryStatus.Recovered or DeathRecoveryStatus.Missing) continue;
            // Nothing item-worthy left (empty or only coins) → nothing to grab in passing.
            if (FullyRecovered(r) || r.Room is not { } rr || !OnActiveGameData(r)) continue;
            bool borders = room.Exits.Values.Any(e => e.Target.Map == rr.Map && e.Target.Room == rr.Room);
            if (borders && (best is null || r.RecordNumber > best.RecordNumber)) best = r;
        }
        return best;
    }

    // A recovery is "deliberate" (earns the spill sweep) ONLY when a
    // directed walk-to targeted this death room — the walker's destination is this
    // room. MANUAL movement into a death room (walker idle, destination null) is NOT
    // deliberate: grab the floor, but don't fire the look-sweep. A walk whose
    // destination lies beyond this room is a pass-through (also not deliberate).
    // Recover Now is handled separately (the `force` path), so its walk-back arrival
    // is deliberate regardless of what the walker reports.
    private bool WalkedToDeathRoom(DeathRecord rec)
    {
        return _walker is { Destination: { } dest }
            && rec.Room is { } r && dest.Map == r.Map && dest.Room == r.Room;
    }

    // Newest un-recovered record whose death room matches key, of those recorded on
    // the active game data (or, with otherGameData, of those recorded on another).
    private DeathRecord? FindRecoverableAt(RoomKey key, bool otherGameData = false)
    {
        if (_profile.Current?.DeathHistory is not { } list) return null;
        DeathRecord? best = null;
        foreach (DeathRecord r in list)
        {
            // Recovered + Missing are terminal — a Missing pile (corpse wasn't in
            // the room) must not re-arm on re-entry, or it would spam-retry again.
            // The user re-tries a Missing pile explicitly via Recover Now.
            if (r.Status is DeathRecoveryStatus.Recovered or DeathRecoveryStatus.Missing) continue;
            if (r.Room is not { } room || room.Map != key.Map || room.Room != key.Room) continue;
            if (OnActiveGameData(r) == otherGameData) continue;
            if (best is null || r.RecordNumber > best.RecordNumber) best = r;
        }
        return best;
    }

    // Start (or restart) recovering a deathpile. Mark the record Partial and,
    // when autoGrab, ARM the grab for the next floor survey read in the room (see
    // _grabOnSurvey, OnSurveyUpdated, RecoverCorpse). A known-empty pile (nothing
    // was lost) jumps straight to Recovered.
    private void BeginRecovery(DeathRecord record, bool autoGrab, bool deliberate, bool arrivedByWalk)
    {
        _activeRecovery = record;
        _missingThisVisit = null;
        _grabOnSurvey = false;
        _stockRecovering = false;
        _settleOwed = false;
        _lookExitsOwed = false;
        _deliberateRecovery = deliberate;
        _engineArrival = false;
        _classifyOwed = false;

        List<string> pile = PileNames(record);
        // A Stock pile comes back an item at a time, over more than one visit, so a
        // list already counted down is kept: starting it over would send the sweep
        // out after gear that is back in the pack.
        // Only what stays on the character is taken off it again (asking twice
        // changes nothing); the returned items came off when it was first built, and
        // taking them off a second time would strike units that are still out.
        // A list a hand-back has counted down is kept on either realm: rebuilt from
        // the full loot, it would list what is already back.
        if (record.UnrecoveredItems is { Count: > 0 } counted
            && ((IsStock && record.Status == DeathRecoveryStatus.Partial) || record.HandedBack))
        {
            if (IsStock) DropWhatStays(counted, record);
            pile = counted;
        }
        record.UnrecoveredItems = pile.Count > 0 ? pile : null;   // corpse contents, for the detail panel

        _log?.Info(LogCategory,
            $"begin recovery: {record.RoomKeyText} realm={(_isParadigm?.Invoke() == true ? "Paradigm" : "Stock")} "
            + $"deliberate={deliberate} autoGrab={autoGrab} pile={pile.Count} item(s)");

        bool known = record.EquippedAtDeath is not null || record.LostItems is not null;
        if (known && pile.Count == 0)
        {
            _log?.Info(LogCategory, "recovery: nothing was lost at death — done");
            // A Stock pile can be empty because everything worn stayed on the
            // character, and a death takes it all off (GAME_MECHANICS "Death
            // threshold & consequences"): that gear is in the pack, to be worn again.
            if (IsStock) ReequipAllWorn(record);
            FinalizeRecovered(record, "Nothing was lost at death.");
            return;
        }

        // Active/Missing → Partial: we're back in the room. Missing flips back
        // when the user Recover-Nows a pile whose corpse has reappeared.
        if (record.Status is DeathRecoveryStatus.Active or DeathRecoveryStatus.Missing)
            SetStatus(record, DeathRecoveryStatus.Partial, "Returned to the death room — recovering.");

        _grabOnSurvey = autoGrab;
        _armedAt = _now();
        // Whose walk this was only matters where a sweep can follow: on Stock, with
        // a grab to settle.
        _classifyOwed = arrivedByWalk && autoGrab && IsStock;
        if (!autoGrab)
            _log?.Info(LogCategory, "recovery: auto-recover off — armed nothing (manual Recover Now only)");
    }

    // A survey read while a grab is armed is the death room's when we are in it
    // with no move on the wire: the tracker says that room and isn't waiting on a
    // move, and none has gone out since the grab was armed. The next room's entry
    // survey prints before that room confirms, with the move still pending. The
    // tracker need not be Confirmed: Suspect in the room (a display it couldn't
    // place) is still the room, and a look from there is answered with its floor.
    // A `look <dir>` still to be answered shows the room next door, floor and all.
    private bool ArmedSurveyIsTheDeathRooms(DeathRecord record) =>
        _roomTracker.State.CurrentRoom?.Key is { } at && record.Room is { } died
        && at.Map == died.Map && at.Room == died.Room
        && _roomTracker.State.Confidence != RoomConfidence.Pending
        && !(_roomTracker.LastMoveSentAt is { } moved && moved > _armedAt)
        && !_roomTracker.IsPeekSuppressed();

    // The realm probe says Stock. Unbound it says neither, and the Stock-only parts
    // of recovery stay off.
    private bool IsStock => _isParadigm?.Invoke() == false;

    // The room's floor survey ("You notice … here.") was just reparsed. If we're
    // armed to auto-recover a pile here, act on it now — via the realm's mechanic:
    // Paradigm recovers the `corpse of <name>`, Stock `get`s the loose items.
    private void OnSurveyUpdated()
    {
        _lastSurveyAt = _now();
        _floorSinceExits = true;
        // Spillover sweep LOOK phase: each `look <dir>` re-parses the PEEKED room's
        // floor into GroundItemTracker (it doesn't skip look-direction peeks), and
        // its Items are already multi-line-stitched — so hand those to the sweep for
        // the exit we're currently peeking. Reusing the tracker's parser is what
        // makes a crowded, line-wrapped "You notice" survey match (a naive single-line
        // parse missed it, so the sweep never walked — report stock-20260825-101612).
        if (_sweep.Active && _groundItems is { } ground)
        {
            _sweep.OnPeekedNotice(ground.Items);
            return;
        }

        // Standing at a sweep stop: a survey here is the floor re-read, or what a
        // search just revealed. Get whatever of ours it shows that isn't asked for yet.
        if (_collectPhase is CollectPhase.Grabbing or CollectPhase.Searching && _collectRecord is { } atStop)
        {
            // Only a survey of the stop itself: one read while a move is on the wire,
            // or after the character was moved off, is some other room's floor.
            if (ConfirmedRoom() is not { } standing || !standing.Equals(_collectStop.Room)) return;
            if (SweepYields()) return;
            // Sends are held: keep what it showed, and get it when the hold lifts.
            if (SweepOnHold || _grabOwed)
            {
                _arrivalFloor = _groundItems?.Items.ToList();
                _grabOwed = true;
                return;
            }
            int more = GetOurItemsHere(atStop, _stopAsked);
            if (more > 0)
            {
                _collectSettle = CollectSettleTicks;
                _log?.Info(LogCategory,
                    $"stock-sweep: {(_collectPhase == CollectPhase.Searching ? "the search revealed" : "the floor shows")} "
                    + $"{more} more of our item(s) — get");
            }
            return;
        }
        if (_collectPhase != CollectPhase.None) return;   // walking a leg: nothing to read

        // Pass-through: grab our overflow off an adjacent-death-room's neighbour we
        // just walked into (Stock only; armed by TryArmSpillover).
        if (_spilloverGrabOnSurvey && _spilloverPile is { } sp)
        {
            _spilloverGrabOnSurvey = false;
            if (GetOurItemsHere(sp) > 0)
            {
                _spilloverRecovering = true;
                _spilloverTookSurveyAt = _lastSurveyAt;
                _log?.Info(LogCategory, "stock-recover: grabbing spillover in a room next to the death room");
            }
        }

        // A corpse seen after all, on the visit that said it was gone: the display
        // that verdict came off was not this room's (a re-display of the room just
        // left can be taken for the arrival where the two rooms read alike), or the
        // corpse has been put back. The pile is reopened and recovered.
        if (_activeRecovery is null && _missingThisVisit is { } written
            && ArmedSurveyIsTheDeathRooms(written) && FindOurCorpse() is { } there)
        {
            _missingThisVisit = null;
            _log?.Info(LogCategory, "auto-recover: the corpse is on this floor after all — the pile is reopened");
            _activeRecovery = written;
            _armedAt = _now();
            // Auto-All went off since the verdict: the record is put right, but
            // the corpse is not asked for. It stands as an arrival made with
            // Auto-All off does, open and unarmed, for Recover Now or the user's
            // own `recover corpse`.
            if (_isAutoEnabled?.Invoke() == false)
            {
                _log?.Info(LogCategory, "auto-recover: Auto-All is off — the corpse is not asked for (Recover Now does it)");
                SetStatus(written, DeathRecoveryStatus.Partial,
                    "The corpse was here after all. Auto-All is off, so it was not recovered: press Recover Now.");
                return;
            }
            SetStatus(written, DeathRecoveryStatus.Partial, "The corpse was here after all — recovering.");
            RecoverCorpse(there);
            return;
        }

        if (_activeRecovery is not { } record || !_grabOnSurvey) return;
        // Only the death room's own floor. Some other room's, taken for it, sent
        // Stock gets for items that weren't there and, on Paradigm, wrote the pile
        // off as Missing because the next room along showed no corpse.
        if (!ArmedSurveyIsTheDeathRooms(record))
        {
            _log?.Info(LogCategory,
                $"armed grab: a floor was read that can't be taken for the death room's ({WhyNotTheDeathRooms(record)}) — left armed");
            return;
        }
        if (IsStock)
        {
            TryGroundRecover(record);
            return;
        }
        // Paradigm. The corpse on the floor just read is recovered at once: the
        // command names a corpse the game has just shown here, whichever room this
        // turns out to be. A floor without it says nothing yet. The line prints
        // before the exits line that tells which room the display was of, and the
        // character may have been moved with no move sent (a teleport, fear, the
        // room it stands in at login): the verdict waits for that line
        // (DeathRoomShownAgain), or is never given if the room is another.
        if (FindOurCorpse() is { } corpse) RecoverCorpse(corpse);
        else
            _log?.Info(LogCategory,
                "armed grab: a floor without the corpse was read — waiting for its exits line to say which room it was");
    }

    // Which part of ArmedSurveyIsTheDeathRooms failed, for the log.
    private string WhyNotTheDeathRooms(DeathRecord record)
    {
        if (_roomTracker.State.CurrentRoom?.Key is not { } at || record.Room is not { } died
            || at.Map != died.Map || at.Room != died.Room)
            return "the map has us in another room";
        if (_roomTracker.State.Confidence == RoomConfidence.Pending) return "a move is on the wire";
        if (_roomTracker.LastMoveSentAt is { } moved && moved > _armedAt) return "a move went out since the grab was armed";
        return "a look through an exit is waiting for its answer";
    }

    // One `recover corpse <name>` for the corpse the floor just showed (our own
    // needs no password, and naming it disambiguates when several share the room).
    private void RecoverCorpse(string corpse)
    {
        _grabOnSurvey = false;   // one shot per arming — never loop
        _lookExitsOwed = false;
        _log?.Info(LogCategory, $"auto-recover: recover corpse {corpse}");
        _corpseAskedFor = _activeRecovery is { } asking ? (asking, corpse) : null;
        Send($"recover corpse {corpse}");
    }

    // The pile is gone (looted, decayed): marked Missing so we neither retry nor
    // spam. Only ever said off a display the tracker has confirmed as the death
    // room's. The record is remembered for the rest of the visit: a floor that
    // shows the corpse after all takes the verdict back (OnSurveyUpdated).
    private void MarkCorpseMissing(DeathRecord record, string why)
    {
        _grabOnSurvey = false;
        _lookExitsOwed = false;
        _log?.Info(LogCategory, $"auto-recover: no corpse in the death room ({why}) — marking Missing.");
        // The note says which display the verdict came off: a report of a pile
        // wrongly written off turns on that.
        SetStatus(record, DeathRecoveryStatus.Missing, $"Corpse was not in the room ({why}) — pile appears lost.");
        _missingThisVisit = record;
        _activeRecovery = null;
    }

    // A Paradigm walk-in the room was displayed for. The arrival's own display has
    // just been read: the floor line prints before the exits line that confirms the
    // room (GAME_MECHANICS "Room display parsing — the room title is positional, not
    // just bright cyan"), and an empty floor prints none. So the corpse is
    // recovered, or found gone, off this display, with no later one to wait for.
    // Arming for "the next survey" instead took the floor of whatever room was
    // walked into next for this one.
    private void CorpseOnArrival(DeathRecord record)
    {
        if (FloorWasJustRead() && FindOurCorpse() is { } corpse) RecoverCorpse(corpse);
        else
            MarkCorpseMissing(record, FloorWasJustRead()
                ? "it is not on the floor shown on arriving" : "the room was shown on arriving with nothing on its floor");
    }

    // A display of the death room has ended (its exits line confirmed the room we
    // already stood in) with the grab still armed. It is the first display read
    // here since the grab was armed: after a dark or blind arrival, a manual locate,
    // a saved room at load, or the `look` a Recover Now sent from inside the room.
    //
    // Paradigm: a corpse on its floor would have been asked for at the floor line,
    // so there is none, and the pile is gone. This is the only place an armed grab
    // says so: the raw exits line can be some other display's (a `look <dir>` the
    // tracker has already set aside by the time recovery reads the line), and the
    // floor line comes before the room is known.
    // Stock: only the look a Recover Now sent settles here (nothing of ours on the
    // floor), and the grab stays armed for a search's reveal.
    private void DeathRoomShownAgain(DeathRecord record)
    {
        bool lookOwed = _lookExitsOwed;
        _lookExitsOwed = false;
        if (IsStock)
        {
            if (!lookOwed) return;
            _log?.Info(LogCategory, "stock-recover: the look showed no floor of ours — nothing is on it");
            _settleOwed = true;
            return;
        }
        // Passed over at its floor line (not provably this room's then), and now it is.
        if (_floorSinceExits && FindOurCorpse() is { } corpse)
        {
            RecoverCorpse(corpse);
            return;
        }
        // The corpse is known to be gone: a party member has it and is handing the
        // gear back. Bare floors say nothing new, so the pile stays as counted and
        // the grab stays armed for a corpse that is put down after all.
        if (record.HandedBack)
        {
            _log?.Info(LogCategory,
                $"auto-recover: no corpse in the death room, but {record.UnrecoveredItems?.Count ?? 0} item(s) have been handed back — pile left as it is");
            return;
        }
        MarkCorpseMissing(record, _floorSinceExits
            ? "it is not on the floor the room showed" : "the room was shown with nothing on its floor");
    }

    // Stock deathpile = loose items on the floor. `get <name>` each pile item
    // that's actually in this room's survey (article/count-insensitive match) —
    // never an absent one, which is what made the old flow spam "You don't see X
    // here.". Each grab confirms with a "You took <name>." line (see OnStockItemTaken)
    // that decrements the pile; the record finalises when everything's back. Items
    // NOT on this floor stay unrecovered — the pile holds at Partial (retried on
    // re-entry, or swept from the rooms it spilled into on a deliberate recovery).
    private void TryGroundRecover(DeathRecord record)
    {
        _grabOnSurvey = false;   // one shot per arming — never loop
        _lookExitsOwed = false;
        int sent = GetOurItemsHere(record);
        if (sent > 0)
        {
            _settleOwed = false;   // an "empty floor" verdict still waiting is overtaken
            // And so is a sweep put off until a hostile left: started now, its looks
            // would drop these gets' "You took" lines, and it would walk out after
            // items already in the pack. The settle that follows decides afresh.
            _stockSweepPending = false;
            _stockRecovering = true;
            _stockSettleTicks = StockSettleTicks;   // heartbeat settles the burst → sweep the leftovers
            _log?.Info(LogCategory, $"stock-recover: get {sent} pile item(s) from the floor");
        }
        else
        {
            // Nothing of ours on this floor — no "You took" is coming, so the
            // death-room phase is settled (everything spilled / gone).
            _log?.Info(LogCategory, "stock-recover: none of our pile items on this floor");
            _settleOwed = true;
        }
    }

    // `get` each of record's still-missing items that's on the CURRENT room's floor
    // (article/count-insensitive; never an absent one — that avoids the get-spam).
    // Returns how many gets were sent. Shared by the death-room grab, the spillover
    // sweep's stops, and the pass-through grab. A sweep stop reads its floor more than
    // once and passes `asked`, the gets it has already sent there: a unit asked for
    // is passed over, and each new get is added. It also passes the floor it copied
    // on arriving, the live list having been emptied by the room change since.
    private int GetOurItemsHere(DeathRecord record, Dictionary<string, int>? asked = null,
        IReadOnlyList<string>? floorNames = null)
    {
        if (record.UnrecoveredItems is not { Count: > 0 } remaining) return 0;
        if ((floorNames ?? _groundItems?.Items) is not { } onFloor) return 0;

        HashSet<string> floor = onFloor
            .Select(ItemNameStore.Normalize)
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, int>? owed = asked is null ? null : new(asked, asked.Comparer);
        int sent = 0;
        foreach (string pileName in remaining)
        {
            string norm = ItemNameStore.Normalize(pileName);
            if (!floor.Contains(norm)) continue;
            if (owed is not null && owed.TryGetValue(norm, out int pending) && pending > 0)
            {
                owed[norm] = pending - 1;
                continue;
            }
            Send($"get {pileName}");
            if (asked is not null) asked[norm] = asked.GetValueOrDefault(norm) + 1;
            sent++;
        }
        return sent;
    }

    // The death-room `get` burst has settled with items still missing. Re-equip
    // whatever worn gear we did get back (combat-paced), then — on a deliberate
    // recovery in a cleared room — sweep the rooms the engine spills into; otherwise
    // hold the pile at Partial (retried on re-entry / manually).
    private void OnStockDeathRoomSettled(DeathRecord record)
    {
        _stockRecovering = false;
        _stockSettleTicks = 0;
        if (record.UnrecoveredItems is not { Count: > 0 } left) return;   // already fully recovered

        ReequipAllWorn(record);   // wear the recovered half now (paced if a hostile's up)

        // Only coins left on the floor — coins recover as cash, not as `get`-ed items,
        // so the pile is effectively done. Finalise rather than sweeping other rooms
        // for pocket change.
        if (FullyRecovered(record))
        {
            int total = PileNames(record).Count;
            _log?.Info(LogCategory, "stock-recover: only currency left in the pile — treating as recovered");
            FinalizeRecovered(record, $"Recovered the deathpile ({total} item(s)).");
            return;
        }

        bool hostile = _hostilesPresent?.Invoke() ?? false;
        _log?.Info(LogCategory,
            $"stock-recover: death-room grab settled, {left.Count} item(s) still missing "
            + $"(deliberate={_deliberateRecovery}, engineArrival={_engineArrival}, hostile={hostile}, onHold={SweepOnHold})");

        bool sweeps = _deliberateRecovery && _isParadigm?.Invoke() != true;
        if (sweeps && SweepGivesWayAtStart(record)) return;
        if (sweeps && !hostile && !SweepOnHold && SweepCanStartHere(record) && StartStockSweep(record))
            return;

        // Can't sweep now (pass-through, Paradigm, hostile still here, sends held, a
        // move or a walk under way, or nothing to plan from) — a hostile, a hold or a
        // move just defers it: the heartbeat retries the sweep once the room is clear,
        // the hold is off and we stand confirmed in it. A move that lands elsewhere
        // drops it with the rest of this room's recovery.
        _stockSweepPending = sweeps;
        _deferBudget = SweepBudgetTicks;
        _log?.Info(LogCategory, _stockSweepPending
            ? "stock-recover: sweep deferred (hostile in the death room, a rest, a pause, Auto-All off, or a move under way) — retrying when clear"
            : "stock-recover: no sweep (not a deliberate recovery) — holding Partial");
        SetStatus(record, DeathRecoveryStatus.Partial,
            $"Recovered what was here — {left.Count} item(s) not in this room.");
    }

    // A sweep starts only from a death room we stand confirmed in, seen (not dark,
    // not entered blind), with the walker idle. A move on the wire leaves the tracker
    // unconfirmed: started then, the looks would go out from a room the character is
    // leaving, the landing would be ignored for their length, and the tracker
    // anchored back where it isn't.
    private bool SweepCanStartHere(DeathRecord record) =>
        InConfirmedDeathRoom(record)
        && !_roomTracker.IsInDarkRoom && !_roomTracker.EnteredBlind
        && _walker is not { State: not WalkState.Idle };

    private bool InConfirmedDeathRoom(DeathRecord record) =>
        ConfirmedRoom() is { } at && record.Room is { } died && at.Map == died.Map && at.Room == died.Room;

    // A sweep the user asked for doesn't start while another engine drives the
    // character or it is following a party leader; it isn't left waiting either, since
    // that engine may run for hours. A neighbours-only sweep is the one such an
    // engine's arrival earns, and starts under it.
    private bool SweepGivesWayAtStart(DeathRecord record)
    {
        if (_engineArrival || _otherEngineDrives?.Invoke() != true) return false;
        _stockSweepPending = false;
        // And this recovery earns no sweep any more: the grab stays armed, and a later
        // survey in the room would otherwise start one nobody asked for by then.
        _deliberateRecovery = false;
        _log?.Info(LogCategory,
            "stock-recover: no spill sweep — another engine is driving the character, or it is following a party leader");
        NotePartial(record,
            $"Recovered what was here — {record.UnrecoveredItems?.Count ?? 0} item(s) not in this room. No spill sweep: "
            + "another engine is driving, or the character is following. Recover Now runs it.");
        return true;
    }

    // True when the floor list holds this room's own survey: one read since the move
    // that brought us here went out. A survey older than that move is the room we
    // left, re-read there, and this floor printed none.
    private bool FloorWasJustRead() =>
        _groundItems is { Items.Count: > 0 }
        && (_roomTracker.LastMoveSentAt is not { } moved || _lastSurveyAt > moved);

    // Drop one entry matching a "You took <item>." from pile's unrecovered set
    // (article/count-insensitive). Returns true when an entry was removed. Shared by
    // the death-room grab and the pass-through grab.
    private bool RemoveRecoveredItem(DeathRecord pile, string rawItem)
    {
        if (pile.UnrecoveredItems is not { } remaining) return false;
        string norm = ItemNameStore.Normalize(rawItem);
        if (norm.Length == 0) return false;
        int idx = remaining.FindIndex(n =>
            string.Equals(ItemNameStore.Normalize(n), norm, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return false;
        remaining.RemoveAt(idx);
        OnPropertyChanged(nameof(Records));
        return true;
    }

    // A "You took <item>." landed while grabbing the death-room stock pile. Drop it
    // from the unrecovered set; once the whole pile is back, finalise Recovered and
    // re-equip the worn half (paced by the combat interleave, same as the corpse path).
    private void OnStockItemTaken(string rawItem)
    {
        if (_activeRecovery is not { } record) return;
        if (!RemoveRecoveredItem(record, rawItem))
        {
            // We `get`-ed a floor item but it matched no unrecovered pile entry —
            // shouldn't happen (we only `get` pile items), so trace the mismatch for
            // diagnosis rather than silently leaving the pile count wrong.
            _log?.Debug(LogCategory,
                $"stock-recover: 'You took {rawItem}' matched no unrecovered pile item "
                + $"(remaining: {(record.UnrecoveredItems is { } r ? string.Join(", ", r) : "none")})");
            return;
        }
        _stockSettleTicks = StockSettleTicks;   // more may still be arriving — keep waiting
        _log?.Info(LogCategory, $"stock-recover: got {rawItem} ({record.UnrecoveredItems?.Count ?? 0} left)");

        // Mid-collect (grabbing at a sweep stop): just decrement — CompleteCollect
        // finalises once the sweep is over, so we don't finish early and strand the
        // walk-back. The stop waits on while its gets are still confirming.
        if (_collectPhase != CollectPhase.None)
        {
            _collectSettle = CollectSettleTicks;
            string norm = ItemNameStore.Normalize(rawItem);
            if (_stopAsked.TryGetValue(norm, out int asked) && asked > 0) _stopAsked[norm] = asked - 1;
            return;
        }

        if (FullyRecovered(record))   // empty, or only coins left → done
        {
            _stockRecovering = false;
            int total = PileNames(record).Count;
            ReequipAllWorn(record);
            FinalizeRecovered(record, $"Recovered the deathpile ({total} item(s)).");
        }
    }

    // Start the spill sweep: work out where the engine would have put what's still
    // missing, then peek each death-room exit for it (the LOOK sweep, which costs no
    // movement). On completion (OnSweepLookComplete) we walk the plan. Returns false
    // (caller holds Partial) when it can't run — no current room, no room graph, or
    // nothing left to find.
    //
    // Two kinds. A recovery the user asked for walks the engine's whole order and
    // then the trail. An arrival another engine made (a loop's approach or flee
    // return, an Auto-Lair hop, an errand's walk) only peeks, and walks no further
    // than a neighbour the peek saw our items in: that engine is about to move the
    // character on, and a twelve-room walk under it would fight it for the wire.
    private bool StartStockSweep(DeathRecord record)
    {
        if (record.UnrecoveredItems is not { Count: > 0 } remaining) return false;
        // Only sweep for real items — coins stay in the death room as coin counts and
        // recover as cash (GAME_MECHANICS "Coins in the deathpile"), so a pile down to
        // pocket change has nothing to chase into another room.
        var want = remaining.Where(n => _groundItems is not { } g || !g.IsCashEntry(n)).ToList();
        if (want.Count == 0) return false;
        if (!SweepCanStartHere(record) || _roomTracker.State.CurrentRoom is not { } here) return false;
        if (_roomLookup is not { } rooms)
        {
            _log?.Info(LogCategory, "stock-sweep: no room graph to plan from — not sweeping");
            return false;
        }
        var exits = DeathSpillOrder.SpillExits(here).ToList();
        if (_engineArrival && exits.Count == 0) return false;   // nothing to peek, and it walks nowhere unseen

        _collectHome = here.Key;
        _collectRecord = record;
        _sweepNeighboursOnly = _engineArrival;
        _sweepStops.Clear();
        if (!_sweepNeighboursOnly)
            foreach (RoomKey room in DeathSpillOrder.Candidates(here.Key, rooms))
                _sweepStops.Add(new SweepStop(room, StopKind.Spill));
        _sweepNext = 0;
        _sweepWalked = 0;
        _sweepUnseenStops = 0;
        _sweepBudget = SweepBudgetTicks;
        _trailQueued = false;
        _sweepEnded = "";
        _legFailed = null;
        _legStopped = null;
        _abandonedCombat = false;
        _homeReachedShort = false;
        _peekStartedAt = _now();
        _log?.Info(LogCategory, _sweepNeighboursOnly
            ? $"stock-sweep: {want.Count} item(s) missing; another engine brought us to {here.Key.Map}/{here.Key.Room}, "
              + "so only its exits are looked through, and a neighbour seen holding our items is walked to"
            : $"stock-sweep: {want.Count} item(s) missing; the engine's spill order from {here.Key.Map}/{here.Key.Room} "
              + $"is {_sweepStops.Count} room(s): {DescribeStops(0)}");

        if (exits.Count > 0 && _sweep.Begin(exits, want, OnSweepLookComplete))
        {
            SpillSweepStateChanged?.Invoke();
            return true;
        }

        // No exit an item could have left by: nothing to peek, and the plan goes
        // straight to the trail, from the next heartbeat like every other step of it.
        _collectStop = new SweepStop(here.Key, StopKind.Spill);
        _collectSettle = 1;
        _searchesLeft = 0;
        SetPhase(CollectPhase.Grabbing);
        return true;
    }

    // The LOOK sweep is done. The neighbours it saw holding our items go to the front
    // of the plan (they're certain), the rest keeps the engine's order, and the walk
    // starts — driven off confirmed arrivals (HandleCollectArrival) and the heartbeat
    // (OnCollectHeartbeat), routing through the normal walker.
    private void OnSweepLookComplete(IReadOnlyList<RoomKey> hits)
    {
        if (SweepYields()) return;

        // Room changes are ignored while the looks run, so whether the character
        // really moved meanwhile is read from what could have moved it: a move
        // command sent since they began, or a walk under way. Then nothing may be
        // planned from the death room, and the tracker must not be put back in it.
        if (MovedDuringLooks())
        {
            EndSweepHere(MovedDuringLooksReason);
            return;
        }
        // Nothing moved us, so a tracker that no longer says the death room was thrown
        // by a peek: a peek's render gets mistaken for a real move (especially when a
        // post-login stat/i refresh interleaves into the sweep, and worse when the room
        // name is ambiguous). Anchor it back so WalkTo routes from where we actually
        // are (report stock-20260825-112233).
        if (ConfirmedRoom() is not { } at || !at.Equals(_collectHome))
            _roomTracker.SetLocated(_collectHome);

        if (_sweepNeighboursOnly)
        {
            foreach (RoomKey hit in hits) _sweepStops.Add(new SweepStop(hit, StopKind.Spill));
        }
        else if (hits.Count > 0)
        {
            List<SweepStop> rest = _sweepStops.Where(s => !hits.Contains(s.Room)).ToList();
            _sweepStops.Clear();
            foreach (RoomKey hit in hits) _sweepStops.Add(new SweepStop(hit, StopKind.Spill));
            _sweepStops.AddRange(rest);
        }
        StartNextCollectLeg();
    }

    private const string MovedDuringLooksReason = "the character may have moved while the exits were looked through";

    private bool MovedDuringLooks() =>
        (_roomTracker.LastMoveSentAt is { } sent && sent > _peekStartedAt)
        || _walker is { State: not WalkState.Idle };

    private RoomKey? ConfirmedRoom() =>
        _roomTracker.State.Confidence == RoomConfidence.Confirmed ? _roomTracker.State.CurrentRoom?.Key : null;

    // A sweep the user asked for gives way the moment another engine drives the
    // character (a loop, Auto-Lair, a solver, an errand's walk) or it starts following
    // a party leader: it ends where it stands and sends nothing more. True when it did.
    // A neighbours-only sweep was started under such an engine and isn't judged by it.
    private bool SweepYields()
    {
        if (_sweepNeighboursOnly || _otherEngineDrives?.Invoke() != true) return false;
        AbandonLeg();
        EndSweepHere("another engine is driving the character, or it is following a party leader");
        return true;
    }

    // Move on to the next stop of the plan that can be walked to, or end the sweep.
    // A stop the walker has no route to, or only a long way round to, is passed over
    // with a log line: the engine drops items through exits a character can't always
    // follow them through.
    private void StartNextCollectLeg()
    {
        AbandonLeg();
        _stockRecovering = false;
        _stopAsked.Clear();
        _legFailed = null;
        _legStopped = null;
        _grabOwed = false;
        _arrivalFloor = null;
        if (_collectRecord is not { } cr) { CompleteCollect(wear: false); return; }
        if (SweepYields()) return;

        while (true)
        {
            if (FullyRecovered(cr)) { ReturnHome("nothing is missing any more"); return; }
            if (_sweepBudget <= 0) { EndOutOfTime(); return; }
            if (ConfirmedRoom() is not { } here) { EndSweepHere("position lost"); return; }
            if (_sweepNext >= _sweepStops.Count)
            {
                if (!_sweepNeighboursOnly && !_trailQueued && QueueTrailStops(cr)) continue;
                ReturnHome(_sweepNeighboursOnly
                    ? "every neighbour seen holding our items was tried"
                    : "every room in the plan was tried");
                return;
            }
            if (_sweepWalked >= MaxSweepStops) { ReturnHome($"walked to {MaxSweepStops} rooms, the limit"); return; }

            SweepStop stop = _sweepStops[_sweepNext++];
            string where = $"{stop.Room.Map}/{stop.Room.Room}";
            if (stop.Room.Equals(here)) { ArriveAtStop(stop, walkedIn: false); return; }

            if (LegRooms(here, stop.Room) is not { } rooms)
            {
                _log?.Info(LogCategory, $"stock-sweep: skipping {where} — the walker has no route there on foot");
                continue;
            }
            if (rooms > MaxLegRooms)
            {
                _log?.Info(LogCategory,
                    $"stock-sweep: skipping {where} — the walker's route is {rooms} rooms, over the {MaxLegRooms} allowed");
                continue;
            }

            _collectStop = stop;
            if (!WalkLeg(stop.Room) || _legFailed is not null)
            {
                _log?.Info(LogCategory,
                    $"stock-sweep: skipping {where} — the walker wouldn't go ({_legFailed ?? _walker?.LastEvent?.Detail})");
                _legFailed = null;
                continue;
            }
            _legRooms = 0;
            _collectTimeout = CollectWalkTimeoutTicks;
            _sweepWalked++;
            SetPhase(CollectPhase.WalkingOut);
            _log?.Info(LogCategory,
                $"stock-sweep: walking to {where} ({(stop.Kind == StopKind.Trail ? "trail room" : "spill room")}, "
                + $"stop {_sweepNext} of {_sweepStops.Count}, {rooms} room(s) away)");
            return;
        }
    }

    // Rooms in the route a leg would walk, planned as WalkLeg walks it (on foot), or
    // null when there is none.
    private int? LegRooms(RoomKey from, RoomKey to) =>
        _walker?.TryComputeRouteKeys(from, to, avoidTeleports: true) is { } route ? route.Count - 1 : null;

    // One leg through the normal walker, on foot: a spill room is a few steps off, and
    // a teleport's charge or a shopping detour for a gate item isn't worth a floor that
    // may be empty. _issuingLeg marks the events this very call raises (the Stopped of
    // the leg it replaces, its own Started) as ours. It is an owned leg: whatever
    // takes the walker ends the sweep, so nothing may save this walk to resume later
    // (a PvP fight, a party comeback, a light-shop detour, an event run all would).
    private bool WalkLeg(RoomKey room)
    {
        if (_walker is null) return false;
        _issuingLeg = true;
        try { return _walker.WalkTo(room, armItemAcquisition: false, avoidTeleports: true, ownedLeg: true); }
        finally { _issuingLeg = false; }
    }

    // Where the leg in flight is headed, or null when none is.
    private RoomKey? LegTarget => _collectPhase switch
    {
        CollectPhase.WalkingOut => _collectStop.Room,
        CollectPhase.WalkingBack => _collectHome,
        _ => null,
    };

    // The walker's word while a sweep runs.
    //   - A walk that isn't ours starting, in any phase: the walker has been taken
    //     (the user's walk-to, another engine's). The sweep ends here and now, so its
    //     next heartbeat can't start a leg over that walk.
    //   - Our own leg failing or being stopped is only noted; the heartbeat acts, since
    //     these are raised mid-teardown, where a new walk would be wiped by the reset
    //     that follows.
    //   - A leg to a boss room marked "stop before entering" finishes one room short
    //     and never arrives. Out to a stop, that is a failed leg, known at once. Back
    //     to a death room so marked, it is the arrival: one short is as near as any
    //     walk of the client's goes.
    private void OnWalkerEvent(WalkEvent evt)
    {
        // A Recover Now's walk to the death room is under way once a walk begins on a
        // journey that ends there, whichever room its first leg goes to (a side trip
        // for an item, a death room the walker stops one short of). From then a Stop
        // calls it off, and so does that walk failing.
        //
        // A walker Stopped does NOT: a detour stops the walk and resumes its journey
        // afterwards (a sell trip, a PvP flee, a party comeback, the light shop a
        // character with no lights left is sent to on the way back), and the arrival
        // is still the Recover Now's. The user's own stops reach here by name: the
        // toolbar's through DropDeferredSweep, the Navigation window's through
        // StopSpillSweep, Reset States through CancelTrip, closed route cards
        // through RecoverNowCardsClosed. An engine ending a sweep (EndSpillSweep) is
        // not among them.
        if (_pendingRecoverNow is { Room: { } bound })
        {
            RoomKey deathRoom = new(bound.Map, bound.Room);
            if (evt.Kind == WalkEventKind.Started && _walker?.Journey is { } trip && trip.Destination.Equals(deathRoom))
                _recoverNowWalking = true;
            else if (evt.Kind == WalkEventKind.Failed && _recoverNowWalking
                && evt.Destination is { } failedAt && failedAt.Equals(deathRoom))
                ForgetRecoverNowWalk($"the walk failed: {evt.Detail}");
        }

        // Raised from inside WalkLeg when the room is the very next one, so it is read
        // ahead of the guards below.
        if (evt.Kind == WalkEventKind.Finished && evt.Requested is { } asked)
        {
            bool returning = _issuingReturn || _collectPhase == CollectPhase.WalkingBack;
            if (returning && asked.Equals(_collectHome))
            {
                _homeReachedShort = true;
                return;
            }
            if (!returning && (_issuingLeg || _collectPhase == CollectPhase.WalkingOut)
                && asked.Equals(_collectStop.Room))
            {
                _legFailed = "it stops one room short of a boss room";
                return;
            }
        }
        if (_issuingLeg || !SweepRunning) return;
        bool ourLeg = LegTarget is { } target && evt.Destination is { } dest && dest.Equals(target);
        switch (evt.Kind)
        {
            case WalkEventKind.Started when !ourLeg:
                EndSweepHere(evt.Destination is { } to
                    ? $"another walk started (to {to.Map}/{to.Room})"
                    : "another walk started");
                break;
            case WalkEventKind.Failed when LegTarget is not null:
                _legFailed = evt.Detail;
                break;
            case WalkEventKind.Stopped when LegTarget is not null:
                _legStopped = evt.Detail;
                break;
        }
    }

    // Standing in a stop. A room walked into had its survey read as it printed, before
    // the exits line that confirmed the arrival, and the floor list is emptied right
    // after a room change, so it is copied now. The gets wait if sends are held.
    private void ArriveAtStop(SweepStop stop, bool walkedIn)
    {
        if (_collectRecord is null) { CompleteCollect(wear: false); return; }
        _collectStop = stop;
        _collectSettle = CollectSettleTicks;
        _stockRecovering = true;   // route this room's "You took" to the decrement
        // With auto-search on, a room walked into has just been searched by it; that
        // is one of this stop's tries, and its reveal reaches us as a survey here.
        bool searchedOnEntry = walkedIn && _autoSearchesRooms?.Invoke() == true;
        _searchesLeft = stop.Kind == StopKind.Trail ? TrailSearchTries - (searchedOnEntry ? 1 : 0) : 0;
        // Walked in, the floor list is this room's if it was read since the step went
        // out (FloorWasJustRead). Standing here already, it is whatever was read last
        // (a neighbour's floor, after the looks), and this room's floor is only known
        // from the search to come.
        _arrivalFloor = walkedIn && FloorWasJustRead() && _groundItems is { } ground
            ? ground.Items.ToList() : new List<string>();
        _grabOwed = true;
        SetPhase(CollectPhase.Grabbing);
        if (!SweepOnHold) GrabAtStop();
    }

    private void GrabAtStop()
    {
        _grabOwed = false;
        IReadOnlyList<string>? floor = _arrivalFloor;
        _arrivalFloor = null;
        if (_collectRecord is not { } rec) return;
        int got = GetOurItemsHere(rec, _stopAsked, floor);
        _collectSettle = CollectSettleTicks;
        // A character back from a death has no light. A dark stop (or one entered
        // blind) shows no floor, so nothing was read there: counted, for the note.
        bool unseen = _roomTracker.IsInDarkRoom || _roomTracker.EnteredBlind;
        if (unseen) _sweepUnseenStops++;
        _log?.Info(LogCategory,
            $"stock-sweep: at {_collectStop.Room.Map}/{_collectStop.Room.Room} — "
            + (unseen ? "dark or entered blind, its floor couldn't be read"
                : $"{got} of our item(s) on the floor" + (got > 0 ? ", get" : ""))
            + (_searchesLeft > 0 ? "; its hidden side is searched next" : ""));
    }

    // The trail: when every spill room is tried and items are still out, the engine's
    // next places are the rooms walked before the death, newest first and starting
    // with the death room, each on its hidden side (GAME_MECHANICS "Deathpile — where
    // the items go"). Only a `search` shows those. Returns false when there's no
    // trail to add.
    private bool QueueTrailStops(DeathRecord record)
    {
        _trailQueued = true;
        if (record.Trail is not { Count: > 0 } trail)
        {
            _log?.Info(LogCategory, "stock-sweep: no walked trail was kept for this death — nothing hidden to search for");
            return false;
        }
        int before = _sweepStops.Count;
        foreach (RoomRef r in trail)
        {
            SweepStop stop = new(new RoomKey(r.Map, r.Room), StopKind.Trail);
            if (!_sweepStops.Contains(stop)) _sweepStops.Add(stop);
        }
        _log?.Info(LogCategory,
            $"stock-sweep: spill rooms done with items still missing — searching the {_sweepStops.Count - before} "
            + $"room(s) walked before the death: {DescribeStops(before)}");
        return _sweepStops.Count > before;
    }

    // The sweep is over: walk back to the death room, where recovery started. The way
    // back is bounded like any leg; with no short way it stays where it is.
    private void ReturnHome(string reason)
    {
        NoteSweepEnded(reason);
        AbandonLeg();
        if (ConfirmedRoom() is not { } here)
        {
            CompleteCollect(wear: false);
            return;
        }
        if (here.Equals(_collectHome))
        {
            CompleteCollect(wear: true);
            return;
        }
        int? rooms = LegRooms(here, _collectHome);
        _homeReachedShort = false;
        _issuingReturn = true;
        bool walking = rooms is not null && rooms <= MaxLegRooms && WalkLeg(_collectHome);
        _issuingReturn = false;
        if (walking && _homeReachedShort)
        {
            _log?.Info(LogCategory, "stock-sweep: the death room is one the walker stops before — back as near as it goes");
            CompleteCollect(wear: true);
            return;
        }
        if (!walking)
        {
            _log?.Info(LogCategory, rooms is null
                ? "stock-sweep: no way back to the death room on foot — staying here"
                : rooms > MaxLegRooms
                    ? $"stock-sweep: the way back to the death room is {rooms} rooms, over the {MaxLegRooms} allowed — staying here"
                    : $"stock-sweep: the walker wouldn't start back to the death room ({_walker?.LastEvent?.Detail}) — staying here");
            CompleteCollect(wear: true);
            return;
        }
        _legRooms = 0;
        _returnBudget = ReturnBudgetTicks;
        _collectTimeout = CollectWalkTimeoutTicks;
        SetPhase(CollectPhase.WalkingBack);
    }

    // Stop a leg of ours the walker still holds: one given up on while it was stalled
    // or held by a gate must not set off later for a room nobody is waiting at.
    private void AbandonLeg()
    {
        if (LegTarget is not { } target
            || _walker is not { State: not WalkState.Idle, Destination: { } bound }
            || !bound.Equals(target))
            return;
        _issuingLeg = true;
        try { _walker.Stop("spill sweep moved on"); }
        finally { _issuingLeg = false; }
    }

    // The sweep ends where the character stands and nothing more is sent for it: no
    // look, get, search or move, and no walk back. The walk was stopped or taken,
    // another engine is driving, a held sweep ran out of time, or the position is
    // unknown.
    private void EndSweepHere(string reason)
    {
        _sweep.Cancel();
        NoteSweepEnded(reason);
        CompleteCollect(wear: false);
    }

    // Time is up. A sweep that can still move walks back; one a gate is holding (a
    // fight, a rest, Auto-All) ends where it stands, because a walk queued behind
    // that hold would set off whenever it lifted. The user's own pause never gets
    // here: the clock doesn't run under it.
    private void EndOutOfTime()
    {
        if (SweepOnHold || _movementHeld?.Invoke() == true)
        {
            AbandonLeg();
            EndSweepHere(OutOfTime + ", with movement held");
            return;
        }
        _sweep.Cancel();
        ReturnHome(OutOfTime);
    }

    private void NoteSweepEnded(string reason)
    {
        _sweepEnded = reason;
        int left = _collectRecord?.UnrecoveredItems?.Count ?? 0;
        _log?.Info(LogCategory,
            $"stock-sweep: ended — {reason}; walked to {_sweepWalked} room(s), {left} item(s) still missing"
            + (left > 0 ? $": {string.Join(", ", _collectRecord!.UnrecoveredItems!)}" : "")
            + (_sweepUnseenStops > 0 ? $"; {_sweepUnseenStops} stop(s) dark, floor not read" : ""));
    }

    private void SetPhase(CollectPhase phase)
    {
        if (_collectPhase == phase) return;
        _collectPhase = phase;
        SpillSweepStateChanged?.Invoke();
    }

    // The plan from index `from` on, as map/room pairs, trail rooms marked.
    private string DescribeStops(int from) =>
        _sweepStops.Count <= from
            ? "(none)"
            : string.Join(", ", _sweepStops.Skip(from).Select(s =>
                $"{s.Room.Map}/{s.Room.Room}{(s.Kind == StopKind.Trail ? " (trail)" : "")}"));

    // Nothing left worth recovering as an ITEM — the unrecovered set is empty, or
    // everything still in it is currency. Coins are recovered as cash (they're
    // dropped from the ground survey, never `get`-ed like items), so a pile holding
    // only coins counts as fully recovered rather than lingering at Partial.
    private bool FullyRecovered(DeathRecord record)
    {
        if (record.UnrecoveredItems is not { Count: > 0 } remaining) return true;
        return _groundItems is { } g && remaining.All(g.IsCashEntry);
    }

    // A confirmed room transition arrived while collecting. Arriving at the stop we're
    // walking to starts its grab; arriving back at the death room ends the sweep. Any
    // room reached on the way is progress, so the leg's stall count starts over, but a
    // leg that has entered more rooms than a leg may is given up: a re-plan or a
    // detour for a lever can make the walk longer than the route it was let go on.
    private void HandleCollectArrival(RoomTransition t)
    {
        if (t.NewRoom is not { } room || t.NewConfidence != RoomConfidence.Confirmed) return;
        if (LegTarget is not { } target) return;
        _collectTimeout = CollectWalkTimeoutTicks;
        if (t.PreviousRoom is null || !t.PreviousRoom.Key.Equals(room.Key)) _legRooms++;

        if (!room.Key.Equals(target))
        {
            if (_legRooms > MaxLegRooms)
                _legFailed = $"it walked {_legRooms} rooms without arriving";
            return;
        }
        if (_collectPhase == CollectPhase.WalkingOut) ArriveAtStop(_collectStop, walkedIn: true);
        else CompleteCollect(wear: true);
    }

    // The sweep's heartbeat, in this order:
    //   1. what takes it away (its walk stopped, another engine driving) ends it;
    //   2. under the user's own pause it does nothing more: no clock runs and it does
    //      not end itself, as a paused loop waits. A sweep that ended itself there
    //      would leave the user's gate up with nothing running, and the next walk the
    //      client started would begin held;
    //   3. what its own state ends it for (a room left with a hostile engaged, a move
    //      sent or a walk begun during the looks);
    //   4. its clocks;
    //   5. and only if nothing holds its sends does it do anything.
    private void OnSweepHeartbeat()
    {
        if (_legStopped is { } stopped) { EndSweepHere($"the walk was stopped ({stopped})"); return; }
        if (SweepYields()) return;
        if (UserPaused) return;

        // Leaving a room with an engaged hostile is a halt, never a return: the sweep
        // doesn't walk on and doesn't walk back through it.
        if (_abandonedCombat)
        {
            AbandonLeg();
            EndSweepHere("it left a room with a hostile still engaged");
            return;
        }
        // Room changes are ignored while the looks run, so they stop the moment
        // something could have moved the character, not when the last one is done.
        if (_sweep.Active && MovedDuringLooks())
        {
            EndSweepHere(MovedDuringLooksReason);
            return;
        }

        if (_collectPhase == CollectPhase.WalkingBack)
        {
            if (--_returnBudget <= 0)
            {
                AbandonLeg();
                EndSweepHere($"the walk back took over {ReturnBudgetTicks} s");
                return;
            }
        }
        else
        {
            if (_sweepBudget > 0) _sweepBudget--;
            if (_sweepBudget <= 0) { EndOutOfTime(); return; }
        }

        if (SweepOnHold) return;
        if (_sweep.Active) _sweep.OnHeartbeat();
        else OnCollectHeartbeat();
    }

    // Heartbeat pump for the collect: settle a stop's grab, search a trail room's
    // hidden side, move on, and give up on a leg that the walker failed or that makes
    // no progress (a trap we can't disarm, or any stall).
    private void OnCollectHeartbeat()
    {
        switch (_collectPhase)
        {
            case CollectPhase.Grabbing:
            case CollectPhase.Searching:
                // Standing at a stop is the premise of everything done there. A typed
                // move, a flee or a teleport can have taken the character elsewhere
                // (a stash room, say) with no leg of ours to notice it.
                if (ConfirmedRoom() is not { } at) { EndSweepHere("position lost"); return; }
                if (!at.Equals(_collectStop.Room))
                {
                    EndSweepHere($"the character was moved off the stop, to {at.Map}/{at.Room}");
                    return;
                }
                if (_grabOwed) { GrabAtStop(); return; }
                if (--_collectSettle > 0) return;
                if (_collectRecord is { } rec && !FullyRecovered(rec) && _searchesLeft > 0 && TrySearchStop())
                    return;
                StartNextCollectLeg();
                break;
            case CollectPhase.WalkingOut:
                if (_legFailed is { } failed)
                {
                    _log?.Info(LogCategory,
                        $"stock-sweep: skipping {_collectStop.Room.Map}/{_collectStop.Room.Room} — the walk there failed ({failed})");
                    StartNextCollectLeg();
                    return;
                }
                if (_movementHeld?.Invoke() == true || --_collectTimeout > 0) return;
                _log?.Info(LogCategory,
                    $"stock-sweep: skipping {_collectStop.Room.Map}/{_collectStop.Room.Room} — no new room reached in "
                    + $"{CollectWalkTimeoutTicks} s (trapped / blocked?)");
                StartNextCollectLeg();
                break;
            case CollectPhase.WalkingBack:
                if (_homeReachedShort)
                {
                    _log?.Info(LogCategory, "stock-sweep: the death room is one the walker stops before — back as near as it goes");
                    CompleteCollect(wear: true);
                    return;
                }
                if (_legFailed is { } lost)
                {
                    AbandonLeg();
                    EndSweepHere($"the walk back to the death room failed ({lost})");
                    return;
                }
                if (_movementHeld?.Invoke() == true || --_collectTimeout > 0) return;
                AbandonLeg();
                EndSweepHere($"the walk back reached no new room in {CollectWalkTimeoutTicks} s");
                break;
        }
    }

    // Search the trail room we're standing in for its hidden side. True when the
    // stop goes on waiting: a `sea` went out, or it is held back by a fight (the game
    // refuses a search while attacking — GAME_MECHANICS "Room-wide search during and
    // after combat"). False when this room isn't to be searched.
    //
    // A search ends a sneak (GAME_MECHANICS "Sneaking — commands, equip order, and the
    // sneak state machine"). Nothing is done about that here: the send is seen as a
    // sneak-breaking command like any other, and the next leg is the walker's, whose
    // pre-move hook sneaks again before its first step.
    private bool TrySearchStop()
    {
        string where = $"{_collectStop.Room.Map}/{_collectStop.Room.Room}";
        if (_isStashRoom?.Invoke(_collectStop.Room) == true)
        {
            _searchesLeft = 0;
            _log?.Info(LogCategory, $"stock-sweep: {where} is one of your stash rooms — not searched");
            return false;
        }
        if (_hostilesPresent?.Invoke() == true)
        {
            _collectSettle = 1;   // look again next heartbeat; the time budget bounds the wait
            return true;
        }
        _searchesLeft--;
        _collectSettle = CollectSettleTicks;
        SetPhase(CollectPhase.Searching);
        _log?.Info(LogCategory, $"stock-sweep: searching {where} for hidden items ({_searchesLeft} more to go)");
        Send("sea");
        // Auto-search would otherwise search this room once more as the next leg
        // starts from it.
        _noteRoomSearched?.Invoke(_collectStop.Room);
        return true;
    }

    // The collect is done. A sweep that finished (the plan ran out or a bound was hit,
    // and it is back in the death room or has no way back) wears the recovered worn
    // half. One that was ended where it stood sends nothing, the re-wear included,
    // unless the pile is complete: then this is the recovery finishing, and it
    // finishes as it does anywhere. Recovered if the pile's empty, else Partial with
    // what stopped it.
    private void CompleteCollect(bool wear)
    {
        _sweep.Cancel();
        _stockRecovering = false;
        _stockSweepPending = false;
        _stopAsked.Clear();
        _legFailed = null;
        _legStopped = null;
        _abandonedCombat = false;
        _grabOwed = false;
        _arrivalFloor = null;
        DeathRecord? record = _collectRecord;
        _collectRecord = null;
        _collectPhase = CollectPhase.None;
        if (record is null)
        {
            SpillSweepStateChanged?.Invoke();
            return;
        }

        bool complete = FullyRecovered(record);   // pile empty, or only coins left
        // Paced, if something hostile is here, in the room we ended in.
        if (wear || complete) ReequipAllWorn(record, _roomTracker.State.CurrentRoom?.Key);
        if (complete)
        {
            _log?.Info(LogCategory, "stock-sweep: recovered everything via the spill sweep");
            FinalizeRecovered(record, "Recovered the deathpile (spill sweep).");
        }
        else
        {
            int left = record.UnrecoveredItems?.Count ?? 0;
            _log?.Info(LogCategory, $"stock-sweep: done — {left} item(s) still missing, holding Partial"
                + (wear ? "" : "; nothing more sent, recovered gear not put back on"));
            // Without a light the sweep walks dark rooms blind; the note says so, or
            // "still missing" reads as "looked and not there".
            NotePartial(record, $"Spill sweep done — {left} item(s) still missing ({_sweepEnded})."
                + (_sweepUnseenStops > 0
                    ? $" {_sweepUnseenStops} of the rooms it stopped in {(_sweepUnseenStops == 1 ? "was" : "were")} dark,"
                        + " so nothing could be read there; try again with a light."
                    : "")
                + (wear ? "" : " Recovered gear was not put back on; Recover Now does that."));
            _activeRecovery = null;
        }
        SpillSweepStateChanged?.Invoke();
    }

    // The given name of OUR corpse as it appears in the floor survey ("corpse of
    // Ermias" → "Ermias"), or null when no matching corpse is on the floor. The
    // survey shows the GIVEN name only, so we compare against the first token of
    // our character name. With several corpses we require the name match so we
    // never recover another player's corpse; a single corpse in our own death
    // room is taken when we have no name to match against.
    private string? FindOurCorpse()
    {
        if (_groundItems is not { } ground) return null;
        string ourGiven = OurGivenName();
        string? loneCorpse = null;
        int corpseCount = 0;
        foreach (string item in ground.Items)
        {
            Match cm = CorpseRegex().Match(item);
            if (!cm.Success) continue;
            corpseCount++;
            string given = cm.Groups["name"].Value.Trim();
            if (ourGiven.Length > 0 && string.Equals(given, ourGiven, StringComparison.OrdinalIgnoreCase))
                return given;
            loneCorpse ??= given;
        }
        return ourGiven.Length == 0 && corpseCount == 1 ? loneCorpse : null;
    }

    // The corpse our own `recover corpse` named, and the live in-game name, both
    // say the corpse is ours. The profile's stored name is asked only when neither
    // is known: a copied profile can leave it stale (report stock-20260828-104653),
    // and a stale name taken alone would leave our recovered pile open.
    private bool IsOurCorpse(string whose)
    {
        string given = CorpseRecoveryLines.GivenName(whose);
        string asked = _corpseAskedFor is { } a && ReferenceEquals(a.Record, _activeRecovery)
            ? CorpseRecoveryLines.GivenName(a.Name)
            : string.Empty;
        string live = CorpseRecoveryLines.GivenName(_liveSelfName?.Invoke());
        if (asked.Length == 0 && live.Length == 0)
        {
            string stored = CorpseRecoveryLines.GivenName(_profile.Current?.Name);
            return stored.Length == 0 || string.Equals(given, stored, StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(given, asked, StringComparison.OrdinalIgnoreCase)
            || string.Equals(given, live, StringComparison.OrdinalIgnoreCase);
    }

    // The corpse name the last `recover corpse` we sent asked for, and the recovery
    // it was sent for: it speaks for that recovery only, so a finished or replaced
    // one leaves it unread.
    private (DeathRecord Record, string Name)? _corpseAskedFor;

    // Prefer the live in-game name over the profile's stored name, which a copied
    // profile can leave stale (report stock-20260828-104653) — matching another
    // player's corpse would be a serious mis-recovery.
    private string OurGivenName()
    {
        string? live = _liveSelfName?.Invoke();
        return CorpseRecoveryLines.GivenName(!string.IsNullOrWhiteSpace(live) ? live : _profile.Current?.Name);
    }

    private List<string> PileNames(DeathRecord record)
    {
        var names = new List<string>();
        AddPileNames(names, record.EquippedAtDeath);
        AddPileNames(names, record.LostItems);
        if (!IsStock) return names;
        // What no floor will ever show comes off a Stock pile list as it is built.
        // Left on, either kind keeps the pile at Partial for good and sends the spill
        // sweep out after nothing.
        DropWhatStays(names, record);
        if (record.ReturnedItems is { } returned)
        {
            // `has returned to its rightful place`: gone for good, a unit per line.
            foreach (string gone in returned)
            {
                string norm = ItemNameStore.Normalize(gone);
                int idx = names.FindIndex(n =>
                    string.Equals(ItemNameStore.Normalize(n), norm, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) names.RemoveAt(idx);
            }
        }
        return names;
    }

    // An item that stayed on the character through the death (DeathPileRules) comes
    // off the list. A key on the ring never stays, whatever abilities its item
    // carries: the Stock engine makes the loyal / cursed test on the pack only
    // (GAME_MECHANICS "Death threshold & consequences"). So of a name that stays,
    // as many units are kept as the ring held copies of it. A key is on the game's
    // ring whether the client listed it there or, not having been told it was a
    // key, in the pack: an unworn entry that is a key by its item data counts too.
    private void DropWhatStays(List<string> names, DeathRecord record)
    {
        if (_staysOnDeath is not { } stays) return;
        var onRing = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (record.LostItems is { } lost)
            foreach (DeathItem key in lost)
            {
                if (string.IsNullOrWhiteSpace(key.Name)) continue;
                (int count, string bare) = CountedCommand.SplitLeadingCount(key.Name.Trim());
                if (!key.OnKeyRing && _isKeyItem?.Invoke(bare) != true) continue;
                string norm = ItemNameStore.Normalize(bare);
                onRing[norm] = onRing.GetValueOrDefault(norm) + Math.Max(1, count);
            }
        names.RemoveAll(n =>
        {
            if (!stays(n)) return false;
            string norm = ItemNameStore.Normalize(n);
            if (onRing.GetValueOrDefault(norm) <= 0) return true;
            onRing[norm]--;
            return false;
        });
    }

    // Expand a captured stack ("15 torch") into per-unit bare names ("torch" ×15).
    // Stock has no batched `get N <item>` — it'd send a malformed `get 15 torch` —
    // so each unit needs its own `get torch` and matches its own "You took torch.".
    // Worn gear is singular and passes through unchanged. Currency ("1500 gold") is
    // kept as ONE verbatim entry, never expanded per-coin: coins recover as cash (not
    // `get`-ed), so the count must survive for IsCashEntry to still read it as cash.
    private void AddPileNames(List<string> into, List<DeathItem>? items)
    {
        if (items is null) return;
        foreach (DeathItem item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Name)) continue;
            string name = item.Name.Trim();
            if (_groundItems?.IsCashEntry(name) == true) { into.Add(name); continue; }
            (int count, string bare) = CountedCommand.SplitLeadingCount(name);
            for (int i = 0; i < Math.Max(1, count); i++) into.Add(bare);
        }
    }

    // Recovery confirmations. Paradigm: the whole pile returns on one "You have
    // recovered the corpse of <name>." line — finalise and (Auto-Equip) re-wear
    // everything worn at death (works for a manual `recover corpse` too, since we
    // key off the confirmation, not who sent it). Stock: items come back one "You
    // took <item>." at a time — decrement the pile per confirmation.
    private void OnLine(LineExtractor.EmittedLine line)
    {
        // Do NOT skip IsPromptLine here: a `get` confirmation ("You took X.") is
        // drawn by OVERWRITING the echoed command on the prompt row (the game sends
        // ^[[K to clear it), so it's flagged as a prompt line — gating on that
        // silently dropped every stock grab's decrement (reports stock-20260825-101612
        // / -104351). The content regexes below never match a bare prompt, so it's
        // safe to run them regardless.

        // Printed as the death happens, before there is a record or a recovery, so it
        // is read ahead of every guard below and kept until the death is recorded.
        if (ReturnedRegex().Match(line.Text) is { Success: true } gone)
        {
            _returnedLines.Add((gone.Groups["item"].Value, line.Timestamp));
            return;
        }

        // An exits line ends a room display, whichever room it was of (a `look <dir>`
        // included): a floor line read before it belongs to a display that is over.
        // Nothing is concluded from this line itself. What a display of the death
        // room showed is settled when the tracker confirms it was the death room's
        // (DeathRoomShownAgain), which it does on this same line before recovery
        // reads it: the room parser is subscribed first.
        if (line.Text.StartsWith("Obvious exits:", StringComparison.Ordinal))
        {
            _floorSinceExits = false;
            return;
        }

        // While the LOOK sweep runs we're only peeking exits — its floors arrive via
        // GroundItemTracker → OnSurveyUpdated (multi-line-safe), and no `get` fires, so
        // there's nothing to do with lines here. (The COLLECT walk isn't sweep-active;
        // its stops' "You took" flows through the normal stock path below, since
        // _stockRecovering is set while grabbing.)
        if (_sweep.Active) return;

        // Pass-through spillover "You took" is independent of any death-room
        // recovery (we're standing in a NEIGHBOUR of the death room, not it), so it
        // runs before the _activeRecovery guard below.
        if (_spilloverRecovering && CorpseRecoveryLines.Took(line.Text) is { } spill)
        {
            OnSpilloverItemTaken(spill);
            return;
        }

        if (_activeRecovery is null) return;

        if (!line.IsPromptLine && CorpseRecoveryLines.Completed(line.Text) is { } whose)
        {
            // Another player's corpse prints the same line, and it is theirs, not
            // ours (CorpseHandoffManager keeps it to hand back).
            if (!IsOurCorpse(whose))
            {
                _log?.Debug(LogCategory, $"paradigm: recovered {whose}'s corpse, not ours — our pile stays open");
                return;
            }
            DeathRecord record = _activeRecovery;
            int total = PileNames(record).Count;
            _log?.Info(LogCategory, $"paradigm: corpse recovered ({total} item(s)) — re-equipping worn gear");
            record.UnrecoveredItems = null;   // corpse = the whole pile is back at once
            ReequipAllWorn(record);
            FinalizeRecovered(record,
                total > 0 ? $"Recovered the corpse ({total} item(s))." : "Recovered the corpse.");
            return;
        }

        if (_stockRecovering && CorpseRecoveryLines.Took(line.Text) is { } took)
            OnStockItemTaken(took);
    }

    // A "You took <item>." landed while grabbing spillover in a room next to a death
    // room. Decrement that pile; if this passing grab happened to complete it, wear
    // the recovered gear and mark it Recovered.
    private void OnSpilloverItemTaken(string rawItem)
    {
        if (_spilloverPile is not { } pile || !RemoveRecoveredItem(pile, rawItem)) return;
        _log?.Info(LogCategory, $"stock-recover: grabbed spillover {rawItem} ({pile.UnrecoveredItems?.Count ?? 0} left)");
        if (!FullyRecovered(pile)) return;   // real items still out — stay Partial

        _spilloverRecovering = false;
        _spilloverPile = null;
        _log?.Info(LogCategory, $"pass-through: overflow grab completed the pile at {pile.RoomKeyText} — re-equipping");
        ReequipAllWorn(pile);
        pile.UnrecoveredItems = null;
        SetStatus(pile, DeathRecoveryStatus.Recovered, "Recovered — overflow grabbed in passing.");
    }

    // The worn pieces that have a copy back to put on, counted by name. The pile can
    // hold more copies of a name than were worn (a worn longsword and a spare in the
    // pack): the copies back are the pile's copies less those still out and those
    // gone for good, and a worn piece goes back on as soon as one of them is. With
    // the worn longsword and its spare both lost and one got back, that one is worn;
    // with both back, one is worn and the other stays in the pack.
    private List<DeathItem> WornPiecesBack(DeathRecord record, List<DeathItem> worn)
    {
        var back = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Tally(IEnumerable<string>? names, int each)
        {
            if (names is null) return;
            foreach (string name in names)
            {
                string norm = ItemNameStore.Normalize(name);
                back[norm] = back.GetValueOrDefault(norm) + each;
            }
        }
        var held = new List<string>();
        AddPileNames(held, record.EquippedAtDeath);
        AddPileNames(held, record.LostItems);
        Tally(held, +1);
        Tally(record.UnrecoveredItems, -1);
        Tally(record.ReturnedItems, -1);

        var pieces = new List<DeathItem>();
        foreach (DeathItem piece in worn)
        {
            string norm = ItemNameStore.Normalize(piece.Name);
            if (back.GetValueOrDefault(norm) <= 0) continue;
            back[norm]--;
            pieces.Add(piece);
        }
        return pieces;
    }

    // Re-equip the worn half we've RECOVERED (Auto-Equip). Paradigm gets the whole
    // pile back at once; a Stock recovery may leave some pieces on a neighbour's
    // floor, so we only re-wear worn items with a copy back (WornPiecesBack; a caller
    // nulls UnrecoveredItems when the whole pile is confirmed back — see the corpse
    // path).
    // Ordering is weapon(s) first then armour highest-AC-first (see OrderForReequip).
    // When a hostile is in the room the wear/eq burst would repeatedly break the
    // combat round, so we don't fire it all at once — enqueue it and pace it across
    // rounds (OnRecoveryCombatRound), holding the CorpseRecovery gate meanwhile. No
    // hostile (or interleaving unbound) → put everything on at once, as before.
    // pacingRoom: the room a combat-paced re-equip belongs to — the death room by
    // default (we recovered it there), or wherever we stood when the gear was handed
    // back to us.
    private void ReequipAllWorn(DeathRecord record, RoomKey? pacingRoom = null)
    {
        if (!AutoEquip || record.EquippedAtDeath is not { } worn || worn.Count == 0) return;

        List<DeathItem> recovered = WornPiecesBack(record, worn);
        if (recovered.Count == 0) return;

        List<DeathItem> ordered = OrderForReequip(recovered, _armourClass);

        if (_isAutoEnabled?.Invoke() == false)
        {
            if (_heldEquip.Count == 0) _heldEquipSince = _now();
            foreach (DeathItem item in ordered)
                if (!_heldEquip.Exists(h => NamesMatch(h.Name, item.Name))) _heldEquip.Add(item);
            _log?.Info(LogCategory,
                $"auto-equip: Auto-All is off — holding {_heldEquip.Count} piece(s) until it is back on");
            return;
        }

        (int Map, int Room)? paceIn = pacingRoom is { } pr ? (pr.Map, pr.Room)
            : record.Room is { } dr ? (dr.Map, dr.Room) : null;
        EquipOrPace(ordered, paceIn);
    }

    // Auto-All came back on: put on what was held while it was off. Anything the user
    // has since worn by hand is left out — judged only by an inventory read newer
    // than the hold, since an older one still lists the gear as it was worn at death.
    // The rest goes on the same way a fresh recovery would, paced when something
    // hostile is here.
    public void OnAutoAllRestored()
    {
        if (_heldEquip.Count == 0) return;
        List<DeathItem> held = new(_heldEquip);
        _heldEquip.Clear();
        if (!AutoEquip)
        {
            _log?.Info(LogCategory, $"auto-equip: Auto-All back on, but Auto-equip is off — {held.Count} held piece(s) dropped");
            return;
        }
        if (_inventorySnapshot?.Invoke() is { } inv && inv.LastUpdated >= _heldEquipSince)
            held.RemoveAll(i => inv.EquippedItems.Any(w => NamesMatch(w.Name, i.Name)));
        if (held.Count == 0)
        {
            _log?.Info(LogCategory, "auto-equip: Auto-All back on — the held pieces are already worn");
            return;
        }
        _log?.Info(LogCategory, $"auto-equip: Auto-All back on — putting on {held.Count} held piece(s)");
        RoomKey? here = _roomTracker.State.CurrentRoom?.Key;
        EquipOrPace(held, here is { } k ? (k.Map, k.Room) : null);
    }

    private static bool NamesMatch(string a, string b) =>
        string.Equals(ItemNameStore.Normalize(a), ItemNameStore.Normalize(b), StringComparison.OrdinalIgnoreCase);

    private void EquipOrPace(List<DeathItem> ordered, (int Map, int Room)? paceIn)
    {
        if (_hostilesPresent?.Invoke() == true && paceIn is { } room)
        {
            _pendingEquip.Clear();
            foreach (DeathItem item in ordered) _pendingEquip.Enqueue(item);
            _equipRoom = room;
            AssertRecoveryGate();
            _log?.Info(LogCategory,
                $"auto-equip: hostile present — pacing {_pendingEquip.Count} piece(s) across combat rounds");
            return;
        }

        foreach (DeathItem item in ordered) SendEquip(item);
    }

    // Order the worn set for re-equip: weapon(s) first — so our swings do real
    // damage sooner while the fight's still on — then armour highest-AC-first
    // (weakest last). Weapon Hand precedes Off-Hand; armour AC is the item's
    // game-data ArmourClass (0 when the lookup is unbound or the item's unknown,
    // which sorts it last). Stable ordering keeps same-AC pieces in captured order.
    internal static List<DeathItem> OrderForReequip(
        IReadOnlyList<DeathItem> worn, Func<string, int>? armourClass)
    {
        List<DeathItem> named = worn.Where(i => !string.IsNullOrWhiteSpace(i.Name)).ToList();
        List<DeathItem> ordered = named
            .Where(i => i.IsHeld)
            .OrderBy(i => i.Slot == "Off-Hand" ? 1 : 0)
            .ToList();
        ordered.AddRange(named
            .Where(i => !i.IsHeld)
            .OrderByDescending(i => armourClass?.Invoke(i.Name) ?? 0));
        return ordered;
    }

    // Send one piece's slot-correct equip verb. A held item (weapon / off-hand) is
    // wielded with `eq` — matching EquipmentManager's wield path — NOT `hold`,
    // which only carries it in hand rather than wielding it (report: corpse
    // recovery sent "hold platinum mace" for the weapon). Body armour uses `wear`.
    // Lights never reach here: a readied light is tracked separately (not in the
    // worn slot set) and has its own `use` verb (see AutoLightProvisioner).
    private void SendEquip(DeathItem item)
    {
        string verb = item.IsHeld ? "eq" : "wear";
        _log?.Info(LogCategory, $"auto-equip: {verb} {item.Name}");
        Send($"{verb} {item.Name}");
    }

    // Combat-round pulse (TickEngine.CombatTickElapsed, ~5 s, driven by damage
    // lines — NOT by the *Combat Off* our own equips emit, so it can't re-enter
    // mid-burst). Put the next handful of pieces on, then nudge the engine to
    // re-attack on the *Combat Off* they cause. Once the room is clear (the mob
    // died) or the queue empties, the remainder goes on at once. No-op when nothing
    // is pending.
    public void OnRecoveryCombatRound()
    {
        if (_pendingEquip.Count == 0) return;
        if (_hostilesPresent?.Invoke() != true) { FlushEquipQueue(); return; }

        for (int i = 0; i < EquipBurstPerRound && _pendingEquip.Count > 0; i++)
            SendEquip(_pendingEquip.Dequeue());
        _armCombatResume?.Invoke();

        if (_pendingEquip.Count == 0) ReleaseRecoveryGate();
    }

    // 1 s heartbeat. Flushes the paced re-equip the instant the room clears (a kill
    // between combat rounds), settles the Stock death-room `get` burst so its
    // leftovers can be swept, paces the sweep's own looks/collects, and starts a
    // sweep that was deferred while a hostile held the death room.
    public void OnRecoveryHeartbeat()
    {
        if (_pendingEquip.Count > 0 && _hostilesPresent?.Invoke() != true)
            FlushEquipQueue();

        if (_handedBack is { } returned && _handedBackSettleTicks > 0 && --_handedBackSettleTicks == 0)
            OnHandBackSettled(returned);

        if (SweepRunning) { OnSweepHeartbeat(); return; }   // paces the looks, the stops and the legs

        // Whose walk brought us here, asked now that every engine has seen the arrival.
        if (_classifyOwed)
        {
            _classifyOwed = false;
            // An engine still driving, or one whose own walk it was and which is done
            // now that it has arrived (a PvP flee, its come-back).
            _engineArrival = _otherEngineDrives?.Invoke() == true
                || (ConfirmedRoom() is { } here && _engineWalkEndedAt?.Invoke(here) == true);
            if (_engineArrival)
                _log?.Info(LogCategory, "stock-recover: another engine's walk brought us here — its sweep is the looks and their neighbours only");
        }

        // The death-room grab found nothing to wait for (see _settleOwed).
        if (_settleOwed)
        {
            _settleOwed = false;
            if (_activeRecovery is { } settled) OnStockDeathRoomSettled(settled);
        }

        // Death-room grab quieted down with items still out → decide sweep vs Partial.
        if (_stockRecovering && _stockSettleTicks > 0 && --_stockSettleTicks == 0
            && _activeRecovery is { } settling)
            OnStockDeathRoomSettled(settling);

        // A sweep can't wait for ever on a hostile that never leaves or a hold that
        // never lifts, to set off long after anyone remembers asking for it.
        if (_stockSweepPending && --_deferBudget <= 0)
        {
            _stockSweepPending = false;
            _log?.Info(LogCategory,
                $"stock-recover: the deferred spill sweep waited {SweepBudgetTicks} s without being able to start — dropped; Recover Now runs it");
        }

        // A deferred sweep (hostile cleared, no hold on sends) can run now, unless
        // another engine has taken the character meanwhile.
        if (_stockSweepPending && _activeRecovery is { } pending && !SweepGivesWayAtStart(pending)
            && !(_hostilesPresent?.Invoke() ?? false) && !SweepOnHold && SweepCanStartHere(pending))
        {
            _stockSweepPending = false;
            if (FullyRecovered(pending))   // only currency left → done, no sweep
            {
                _log?.Info(LogCategory, "stock-recover: death room clear — only currency left, treating as recovered");
                FinalizeRecovered(pending, "Recovered the deathpile.");
            }
            else
            {
                _log?.Info(LogCategory, "stock-recover: death room clear — starting the deferred spillover sweep");
                if (!StartStockSweep(pending))
                    SetStatus(pending, DeathRecoveryStatus.Partial,
                        pending.UnrecoveredItems is { Count: > 0 } l
                            ? $"Recovered what was here — {l.Count} item(s) not in this room."
                            : "Recovered the deathpile.");
            }
        }
    }

    private void FlushEquipQueue()
    {
        while (_pendingEquip.Count > 0) SendEquip(_pendingEquip.Dequeue());
        _equipRoom = null;
        ReleaseRecoveryGate();
    }

    // Give up on the remaining pieces (we left the recovery room) — drop them and
    // release the gate. The items are back in the pack either way; the user can
    // re-equip manually.
    private void AbandonPendingEquip()
    {
        if (_pendingEquip.Count > 0)
            _log?.Info(LogCategory,
                $"auto-equip: left the room with {_pendingEquip.Count} piece(s) unequipped — abandoning");
        _pendingEquip.Clear();
        _equipRoom = null;
        ReleaseRecoveryGate();
    }

    private void AssertRecoveryGate()
    {
        if (_recoveryGateHeld) return;
        _recoveryGateHeld = true;
        _assertRecoveryGate?.Invoke();
    }

    // Reset States: drop the recovery trip in progress (spill-over collect, stock
    // sweep, paced re-equip) and release its movement hold. The deathpile records
    // stay, so a recovery can be started again.
    public void CancelTrip()
    {
        DropSweep("cancelled (Reset States)");
        ForgetRecoverNowWalk("Reset States");
        _grabOnSurvey = false;
        _settleOwed = false;
        _classifyOwed = false;
        _lookExitsOwed = false;
        _stockRecovering = false;
        _stockSweepPending = false;
        _spilloverGrabOnSurvey = false;
        _spilloverRecovering = false;
        _deliberateRecovery = false;
        AbandonPendingEquip();
    }

    // The user's Stop, from the movement controller (which lists the sweep as a
    // solver, so its Stop and Pause reach it in every phase) and from the Navigation
    // window's own Stop buttons. The walker's Stopped only reaches a sweep with a leg
    // in flight; one that stands at a stop getting or searching, or is still peeking,
    // would otherwise start its next leg after the Stop. Ends it where we stand and
    // sends nothing more.
    // And a Stop calls off a Recover Now still walking to the death room.
    public void StopSpillSweep(string reason)
    {
        ForgetRecoverNowWalkUnderWay(reason);
        EndSpillSweep(reason);
    }

    // An engine taking the character over (a fight with a player): the sweep ends
    // where it stands, as for Stop, and that is all. A Recover Now still walking is
    // left alone. The engine has stopped that walk and resumes its journey when it
    // is done, and the arrival is still the Recover Now's; forgetting it here is the
    // user's Stop reached by something that isn't the user.
    public void EndSpillSweep(string reason)
    {
        _stockSweepPending = false;   // nor does one waiting on a hostile start later
        if (!SweepRunning) return;
        AbandonLeg();
        EndSweepHere(reason);
    }

    // A move carried us out of a room with a hostile still engaged (the walker's
    // abandoned-combat halt). The sweep ends at its next heartbeat and never walks
    // back through that room.
    public void NoteEngagedTargetAbandoned()
    {
        if (SweepRunning) _abandonedCombat = true;
    }

    // The connection dropped. A sweep left running would spend its stops timing out
    // against a dead wire and write that up as a result, or set off again on
    // reconnect.
    //
    // The pickup itself is left as it stands. A grab armed and waiting for the room's
    // next display (the usual state after a Paradigm walk-in, whose arrival survey
    // has gone by, and a Stock one in the dark) must still be armed for the display
    // that follows logging back in: the room isn't re-entered then, so nothing would
    // arm it again. What is taken away is the sweep after it: the recovery is no
    // longer a deliberate one, so whatever settles holds the pile at Partial.
    public void NotifyDisconnected()
    {
        DropSweep("disconnected");
        _stockSweepPending = false;
        _deliberateRecovery = false;
        _classifyOwed = false;
    }

    // Stop, with no sweep running yet: one waiting on a hostile, a rest or a move
    // doesn't start afterwards, and a Recover Now still walking to the death room is
    // called off with its walk. (A running sweep is ended through the controller's
    // solver list.)
    public void DropDeferredSweep()
    {
        ForgetRecoverNowWalkUnderWay("Stop");
        if (!_stockSweepPending) return;
        _stockSweepPending = false;
        _log?.Info(LogCategory, "stock-recover: the deferred spill sweep was called off (Stop)");
    }

    // The walk a Recover Now started is not going to arrive: stopped, failed, or its
    // route cards closed without a pick. Left standing, the next time the character
    // came into that death room by any means would count as the Recover Now's
    // arrival and set the whole sweep off.
    private void ForgetRecoverNowWalk(string why)
    {
        if (_pendingRecoverNow is null) return;
        _pendingRecoverNow = null;
        _recoverNowWalking = false;
        _log?.Info(LogCategory, $"recover-now: the walk to the death room was called off ({why})");
    }

    // A Stop only calls off a Recover Now whose walk is under way. Before that (its
    // route cards still up, or the engines being stopped to make way for it) the Stop
    // is not about this walk.
    private void ForgetRecoverNowWalkUnderWay(string why)
    {
        if (_recoverNowWalking) ForgetRecoverNowWalk(why);
    }

    // Route cards for a Recover Now's walk were closed without a pick. That forgets
    // it, unless a walk for it is under way all the same: pressed again while
    // walking, the second press put up cards of its own, and closing those says
    // nothing about the walk the first press started.
    public void RecoverNowCardsClosed()
    {
        if (_recoverNowWalking) return;
        ForgetRecoverNowWalk("the route cards were closed");
    }

    // Another character was loaded, or this one closed: what is in progress belongs
    // to a death record that is no longer the loaded profile's.
    private void OnProfileChanged()
    {
        DropSweep("the character was changed");
        _activeRecovery = null;
        _corpseAskedFor = null;
        _pendingRecoverNow = null;
        _recoverNowWalking = false;
        _grabOnSurvey = false;
        _settleOwed = false;
        _lookExitsOwed = false;
        _stockRecovering = false;
        _stockSweepPending = false;
        _returnedLines.Clear();
        _missingThisVisit = null;
    }

    // Abandon a running spill sweep where it stands: no walk is started, nothing more
    // is sent, and no note is written. The pile keeps what it has counted down and
    // can be recovered again.
    private void DropSweep(string reason)
    {
        if (!SweepRunning) return;
        NoteSweepEnded(reason);
        AbandonLeg();
        _sweep.Cancel();
        _collectPhase = CollectPhase.None;
        _collectRecord = null;
        _stockRecovering = false;
        _stopAsked.Clear();
        _legFailed = null;
        _legStopped = null;
        _abandonedCombat = false;
        _grabOwed = false;
        _arrivalFloor = null;
        SpillSweepStateChanged?.Invoke();
    }

    private void ReleaseRecoveryGate()
    {
        if (!_recoveryGateHeld) return;
        _recoveryGateHeld = false;
        _clearRecoveryGate?.Invoke();
    }

    // Another player gave us an item. It is struck off the most recent deathpile
    // that still lists it as not returned, newest first; the heartbeat settles the
    // pile once the burst of "X just gave you …" lines goes quiet. A later death
    // that dropped nothing lists nothing, so it doesn't stand between a hand-back
    // and the pile it belongs to.
    //
    // Any pile takes the item from anyone, party or not, at any time: an Active,
    // Partial or Missing one alike (user, 2026-10-10). A corpse gone from
    // the death room is what it looks like when someone has picked it up to hand the
    // gear back, and a pile marked Missing is not closed to that.
    public void OnItemReceived(string itemName, string giver)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return;
        string got = ItemNameStore.Normalize(itemName);
        foreach (DeathRecord rec in Records.Reverse())   // newest pile first
        {
            if (rec.Status is DeathRecoveryStatus.Recovered) continue;
            List<string> listed = rec.UnrecoveredItems ?? PileNames(rec);
            int idx = listed.FindIndex(n =>
                string.Equals(ItemNameStore.Normalize(n), got, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) continue;
            rec.UnrecoveredItems = listed;
            rec.UnrecoveredItems.RemoveAt(idx);
            rec.HandedBack = true;
            _handedBack = rec;
            _handedBackBy = giver;
            _handedBackSettleTicks = HandBackSettleTicks;
            _log?.Info(LogCategory,
                $"hand-back: {giver} gave us '{itemName}' from the {rec.RoomKeyText} deathpile "
                + $"({rec.UnrecoveredItems.Count} item(s) still out)");
            return;
        }
    }

    // The hand-back burst went quiet: finalise the pile (all back, or only coins left)
    // or hold it at Partial, then re-equip whatever worn gear came back — paced if a
    // hostile's up, in the room we're in now rather than the death room.
    private void OnHandBackSettled(DeathRecord record)
    {
        _handedBack = null;
        string by = _handedBackBy;
        if (FullyRecovered(record))
            FinalizeRecovered(record, $"Handed back by {by}.");
        else
            SetStatus(record, DeathRecoveryStatus.Partial,
                $"Handed back by {by} — {record.UnrecoveredItems?.Count ?? 0} item(s) still out.");
        _profile.Save();
        ReequipAllWorn(record, pacingRoom: _roomTracker.State.CurrentRoom?.Key);
    }

    private void FinalizeRecovered(DeathRecord record, string message)
    {
        _log?.Info(LogCategory, $"recovery complete: {record.RoomKeyText} — {message}");
        _activeRecovery = null;
        _corpseAskedFor = null;
        _grabOnSurvey = false;
        _stockRecovering = false;
        _stockSweepPending = false;
        record.UnrecoveredItems = null;   // everything accounted for
        SetStatus(record, DeathRecoveryStatus.Recovered, message);
    }

    // SetStatus leaves the note of a pile that is already Partial as it was. How a
    // sweep ended, or why none ran, is the newer and more useful thing to read there.
    private void NotePartial(DeathRecord record, string message)
    {
        record.Status = DeathRecoveryStatus.Partial;
        record.RecoveryMessage = message;
        _profile.Save();
        OnPropertyChanged(nameof(Records));
    }

    private void SetStatus(DeathRecord record, DeathRecoveryStatus status, string message)
    {
        if (record.Status == status) return;
        record.Status = status;
        record.RecoveryMessage = message;
        if (status == DeathRecoveryStatus.Recovered) record.HandedBack = false;
        _profile.Save();
        OnPropertyChanged(nameof(Records));
    }

    private void Send(string text)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(text + "\r"));
    }

    // Append a synthetic death record (the "Simulate Death" button) so the DEATH
    // grid + recovery flow can be exercised without dying in game. Decrements the
    // displayed lives by one, floored at zero.
    public void SimulateDeath()
    {
        if (_profile.Current is not { } p) return;
        p.DeathHistory ??= new List<DeathRecord>();
        Room? here = _roomTracker.State.CurrentRoom;
        // Continue the declining-lives series from the most recent record.
        int prevLives = p.DeathHistory.Count > 0 ? p.DeathHistory[^1].LivesRemaining : 0;
        var record = new DeathRecord(
            _now(),
            here is null ? null : new RoomRef(here.Key.Map, here.Key.Room),
            Math.Max(0, prevLives - 1),
            "Simulated death (test).")
        {
            RecordNumber = p.DeathHistory.Count + 1,
            RoomName = here?.Name,
            GameData = _roomTracker.RoomsGameData,
            Status = DeathRecoveryStatus.Active,
        };
        if (_inventorySnapshot is { } provider)
        {
            InventorySnapshot snapshot = provider();
            (List<DeathItem> equipped, List<DeathItem> lost) =
                DeathLootCapture.FromSnapshot(snapshot);
            record.EquippedAtDeath = equipped;
            record.LostItems = lost;
            record.CoinsAtDeath = snapshot.Currency;
        }
        p.DeathHistory.Add(record);
        _profile.Save();
        // Real deaths capture via the PlayerDeathObserved hook; the test button
        // bypasses RoomTracker.NoteDeath, so snapshot the trail and the tail here too.
        CaptureSpillFacts(record);
        CaptureDeathLog(record);
        OnPropertyChanged(nameof(Records));
    }

    // ----- death-log capture ("How did I Die?") -----------------------

    // PlayerDeathObserved fires synchronously from RoomTracker.NoteDeath right
    // after the record is appended, so the just-added record is the history tail.
    // Snapshot its backscroll before the graveyard display floods scrollback.
    private void OnDeathObserved()
    {
        // An arena death took nothing (RoomTracker.NoteDeath): the pieces held to
        // go back on are still in the pack, and there are no spill facts to keep.
        bool tookNothing = _roomTracker.LastDeathTookNothing;
        if (!tookNothing) _heldEquip.Clear();   // whatever was waiting to go back on is on the new pile now
        // A sweep that was out walking died with us. Every movement engine is stopped
        // on a death and nothing may walk back toward the room we died in.
        DropSweep("died during the sweep");
        // And whatever recovery was in hand is over with it. Room changes aren't
        // followed while a sweep runs, so the one that took us out of the room (the
        // death) didn't clear this.
        _activeRecovery = null;
        _corpseAskedFor = null;
        _grabOnSurvey = false;
        _stockRecovering = false;
        _stockSweepPending = false;
        _settleOwed = false;
        _classifyOwed = false;
        _lookExitsOwed = false;
        if (_profile.Current?.DeathHistory is not { Count: > 0 } list)
        {
            _returnedLines.Clear();
            return;
        }
        DeathRecord last = list[^1];
        if (tookNothing) _returnedLines.Clear();
        else CaptureSpillFacts(last);
        if (last.DeathLogFile is not null) return;   // already captured
        CaptureDeathLog(last);
    }

    // RoomTracker.PlayerDeathInferred: a death worked out at the login. The pieces
    // held to go back on are on that pile now, as above, unless it was an arena
    // death, which took nothing.
    private void OnDeathInferred()
    {
        if (!_roomTracker.LastDeathTookNothing) _heldEquip.Clear();
    }

    // RoomTracker.UnwitnessedDeathRecorded: a death recorded after the fact, at
    // the login or later. The grid gets its row. No backscroll is kept for it,
    // and none of what only the moment of death can tell (CaptureSpillFacts): by
    // now the terminal shows a login, and the rooms last walked are this
    // connection's, not the ones that led to where the character died.
    private void OnUnwitnessedDeathRecorded() => OnPropertyChanged(nameof(Records));

    // What only the moment of death can tell a Stock recovery (GAME_MECHANICS
    // "Deathpile — where the items go"): which items the game said were gone for
    // good, and the rooms walked on the way here, which the engine falls back to,
    // hidden side, when no floor nearby has room. The tracker's history is newest
    // first and still ends in the death room: the respawn room isn't confirmed yet.
    private void CaptureSpillFacts(DeathRecord record)
    {
        List<string> returned = _returnedLines
            .Where(r => (record.At - r.At).Duration() <= ReturnedLineWindow)
            .Select(r => r.Item)
            .ToList();
        _returnedLines.Clear();
        if (!IsStock) return;

        if (returned.Count > 0)
        {
            record.ReturnedItems = returned;
            _log?.Info(LogCategory,
                $"death: {returned.Count} item(s) returned to their rightful place, gone for good: {string.Join(", ", returned)}");
        }

        List<RoomRef> trail = new();
        foreach (RoomKey key in _roomTracker.GetHistory())
        {
            if (trail.Count > 0 && trail[^1].Map == key.Map && trail[^1].Room == key.Room) continue;
            trail.Add(new RoomRef(key.Map, key.Room));
            if (trail.Count == DeathSpillOrder.TrailRooms) break;
        }
        // A history that doesn't end where we died is some other walk's.
        if (record.Room is { } died && trail.Count > 0 && trail[0].Map == died.Map && trail[0].Room == died.Room)
        {
            record.Trail = trail;
            _log?.Info(LogCategory,
                $"death: kept the {trail.Count} room(s) walked up to it: {string.Join(", ", trail.Select(r => $"{r.Map}/{r.Room}"))}");
        }
        if (record.ReturnedItems is not null || record.Trail is not null) _profile.Save();
    }

    // Snapshot the transcript tail to a per-character death-log file and pin its
    // name on the record. No-op without a bound transcript provider or a named
    // profile (a draft has nowhere to write). Best-effort: a capture
    // failure must never break the death-record write it rides on.
    private void CaptureDeathLog(DeathRecord record)
    {
        if (_transcriptTail is not { } provider) return;
        if (record.DeathLogFile is not null) return;
        if (_profile.CurrentBbsName is not { } bbs || _profile.CurrentProfileName is not { } chr) return;

        IReadOnlyList<TranscriptSnapshot.Line> lines;
        try { lines = provider(); }
        catch { return; }
        if (lines.Count == 0) return;

        string fileName = $"death-{record.At.LocalDateTime:yyyyMMdd-HHmmss}-{record.RecordNumber}.log";
        try
        {
            Directory.CreateDirectory(AppPaths.DeathLogsFolder(bbs, chr));
            File.WriteAllText(AppPaths.DeathLogFile(bbs, chr, fileName),
                DeathLogFormat.Render(record, lines, chr));
            record.DeathLogFile = fileName;
            _profile.Save();
            _log?.Info(LogCategory, $"death log captured: {fileName} ({lines.Count} lines)");
        }
        catch (Exception ex)
        {
            _log?.Warn(LogCategory, $"death log capture failed: {ex.Message}");
        }
    }

    // Read a record's captured death log, or null when it has none / the file is
    // gone / unreadable. The "How did I Die?" viewer treats null as "nothing to
    // show".
    public string? ReadDeathLog(DeathRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (ResolveDeathLogPath(record) is not { } path) return null;
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    // Resolve the on-disk path for a record's death log, or null when the record
    // has no log or no profile is loaded to scope it.
    private string? ResolveDeathLogPath(DeathRecord record)
    {
        if (record.DeathLogFile is not { Length: > 0 } fileName) return null;
        if (_profile.CurrentBbsName is not { } bbs || _profile.CurrentProfileName is not { } chr) return null;
        return AppPaths.DeathLogFile(bbs, chr, fileName);
    }

    private void DeleteDeathLog(DeathRecord record)
    {
        if (ResolveDeathLogPath(record) is not { } path) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort; an orphaned log file is harmless clutter */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _deathWatcher.PlayerDied -= OnPlayerDied;
        _roomTracker.StateChanged -= OnRoomChanged;
        _roomTracker.PlayerDeathObserved -= OnDeathObserved;
        _roomTracker.PlayerDeathInferred -= OnDeathInferred;
        _roomTracker.UnwitnessedDeathRecorded -= OnUnwitnessedDeathRecorded;
        _roomTracker.MoveBlocked -= OnMoveBlocked;
        _profile.ProfileLoaded -= OnProfileLoaded;
        _profile.ProfileClosed -= OnProfileChanged;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = null;
        if (_groundItems is not null) _groundItems.SurveyUpdated -= OnSurveyUpdated;
        _groundItems = null;
        if (_walker is not null) _walker.Event -= OnWalkerEvent;
        _walker = null;
        _sweep.Cancel();
        _collectPhase = CollectPhase.None;
        _collectRecord = null;
        AbandonPendingEquip();   // never strand the CorpseRecovery gate asserted
    }

    // A floor-survey entry naming our deathpile corpse: "corpse of <given-name>"
    // (the survey renders the given name only, no article). The captured name is
    // the exact token `recover corpse <name>` takes. Paradigm only — Stock scatters
    // the pile loose instead of packing it in a corpse.
    [GeneratedRegex(@"^corpse of (?<name>.+?)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CorpseRegex();

    // "Your <item> has returned to its rightful place." — printed to the dying
    // player for an item the engine found no room for anywhere (GAME_MECHANICS
    // "Deathpile — where the items go"). That item is gone: nothing to recover.
    [GeneratedRegex(@"^Your (?<item>.+?) has returned to its rightful place\.$", RegexOptions.CultureInvariant)]
    private static partial Regex ReturnedRegex();
}
