using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Inventory;

// Looks for what a penalised hang-up dropped, on the way back into the game. Some
// boards drop a few of a character's items on the floor of the room when the link
// goes down in a fight (GAME_MECHANICS "Hang-up / lost carrier"), and nothing
// printed on the next entry says which. So what is held is written to the profile
// while the character plays (StampForSave), and the next entry compares that list
// with the login's own inventory read: what is short and lying in the room is
// taken back.
//
// The pickup runs only on a realm whose settings say items are dropped (Settings →
// BBS, HangupPenaltyNotice.MaxItemsDropped), once per connection, takes back no
// more copies than those settings say were dropped, and has no part in deciding a
// hang-up.
//
// The order on the way in:
//   connect       — a list is stored for this realm and the realm drops items (or
//                   a death is in question, as described at the end): movement
//                   is held, so a loop that restarts on the first prompt can't
//                   walk out of the room before the check has run.
//   game entered  — wait for an inventory read. The login sends `i` itself; an
//                   entry made by hand gets one asked for after InventoryWaitTicks.
//   inventory in  — compare. Nothing short ends it. Otherwise the room is needed:
//                   the login's own display, or one bare Enter when none was read.
//   room known    — another room than the one the link dropped in ends it. Else a
//                   `get` for each short item the floor list shows, once any
//                   fight in the room is over.
//   gets answered — the gear set that was on is applied again when a piece of it
//                   came back, and the hold on movement is dropped.
//
// The hold never outlasts its reason. An entry or an inventory read that doesn't
// come in time gives it up, and so does Reset States; the list is kept, and the
// comparison still runs when the read arrives, without the hold, picking up only
// where the map is sure the character stands in the room it left the game in.
//
// Nothing is retried: one pass per connection, and what isn't on the floor then
// is logged as still missing and forgotten. Only a pass cut short by the link
// dropping again carries its unfound items over to the next connection.
//
// The same pass answers a second question first: did the hang-up kill the
// character? A board that penalises a hang-up kills a character that was dropped,
// or low enough for the HP it takes to drop it under the death threshold, after
// the link is gone, so no death line is ever seen (Recovery.HangupDeath has the
// rule). When the list says the character was that low and the realm penalises,
// the pass runs for that alone, item counts or not: at the inventory read it
// asks for a `stat` if none has given the lives, then judges. A life lost says
// "died", and the room the character comes back in must agree: the first room
// the map is sure of on this connection, before a move has gone out, is where
// the dead of the room it hung up in arrive. A death it finds is recorded as any
// other and ends the pass: what is missing then is a deathpile, Death Recovery's
// to fetch, and nothing here picks it up or calls it missing. When it can't
// tell, it says so once and records nothing.
//
// A hang-up while dropped in an arena room is looked into on any realm, penalty
// or none: the death it gets there takes no life and nothing else, so it is told
// by the room it comes back in alone and recorded as an arena death, with no
// pile (Recovery.HangupDeath.JudgeArena).
//
// The question outlives the pass when the lives can't be had (Auto-All off, or
// the hold already given up): it stays open, without a hold and with nothing
// sent, and any `stat` read on the connection answers it. It is judged on what
// the entry showed (HP at the first prompt, the first inventory read), never on
// what the character has become by the time the answer arrives.
public sealed class HangupItemRecheck
{
    public const string LogCategory = "HangupItems";
    public const string AsserterName = nameof(HangupItemRecheck);

    // Counted in heartbeats (TickEngine.HeartbeatInterval, 1 s).
    // The login's own `i` goes out under a second after the first room display;
    // this is how long it is given before one is asked for, and how long the
    // answer to that one is given.
    private const int InventoryWaitTicks = 4;
    // A room redisplay answers within the second; a dark room never does.
    private const int RoomWaitTicks = 2;
    // Quiet after the last `You took` before the gets count as answered. A get
    // the game refuses prints a line this doesn't read, and must not hold movement.
    private const int GetSettleTicks = 3;
    // From the connect to the game: a login takes seconds, and a login that stops
    // at the board's menu (after a hang-up the user enters by hand) can take as
    // long as the user likes. Past this the hold is given up.
    private const int EntryWaitTicks = 180;
    // A `stat` answers within the second.
    private const int LivesWaitTicks = 3;
    // How long after the lives changed unread a `stat` goes out for them: long
    // enough for the command that changed them to have been answered, and for a
    // `stat` someone else sends for the same reason to have made this one needless.
    private const int LivesRereadTicks = 5;

    // The line a death record shows where a witnessed death has the game's own.
    // It claims no more than is known: the room and time are where this client
    // last had the character, which is where it died unless it was played from
    // another client in between.
    public const string DeathMessage =
        "Killed by the hang-up penalty (not seen: worked out on entering the game). The room and time are where this "
        + "client last had the character in the game.";

    // The same for a hang-up while dropped in an arena room.
    public const string ArenaDeathMessage =
        "Died in an arena room after the link went down (not seen: worked out on entering the game). An arena death "
        + "takes no life, item or coin. The room and time are where this client last had the character in the game.";

    private enum Phase { Idle, AwaitingEntry, AwaitingInventory, AwaitingLives, AwaitingRoom, AwaitingFightEnd, PickingUp }

    private readonly MovementCoordinator _coordinator;
    private readonly Func<CharacterProfile?> _profile;
    private readonly Action _saveProfile;
    private readonly Func<string?> _realmKey;
    private readonly Func<int> _maxItemsDropped;
    private readonly Func<InventorySnapshot> _inventory;
    private readonly Func<RoomKey?> _confirmedRoom;
    private readonly Func<IReadOnlyList<string>> _floor;
    private readonly Func<string, string>? _itemKey;
    private readonly Func<bool> _fighting;
    private readonly Func<bool> _hostilePresent;
    private readonly Func<bool> _isAutoEnabled;
    private readonly Func<bool> _roomRedisplayFree;
    private readonly Action<string, int> _collect;
    private readonly Func<IReadOnlyCollection<string>, string?> _reapplyGearSet;
    private readonly Action<string>? _notice;
    private readonly Action<Action> _post;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();

    // What the death question reads. All unset: the
    // list then carries no HP, and a list without one raises no suspicion.
    private readonly Func<(int Hp, int? MaxHp)?>? _vitals;
    private readonly Func<int?>? _lives;
    private readonly Func<bool>? _pvpFight;
    private readonly Func<bool>? _monsterFight;
    private readonly Func<bool, bool, int?>? _hpShareTop;
    private readonly Func<bool>? _stockRealm;
    private readonly Func<string, bool>? _staysOnDeath;
    private readonly Func<RoomKey, RoomKey, Recovery.HangupEntryRoomReading>? _entryRoomSays;
    private readonly Func<RoomKey, string>? _roomLabel;
    private readonly Func<string?>? _roomsGameData;
    private readonly Func<Recovery.UnwitnessedDeath, DeathRecord?>? _recordDeath;

    private Phase _phase;
    private HeldAtDisconnect? _before;
    private List<(string Name, int Count)> _missing = new();
    private List<(string Name, int Count)> _plan = new();
    private readonly Dictionary<string, int> _taken = new(StringComparer.OrdinalIgnoreCase);
    private int _ticks;
    private int _entryTicks;
    private bool _askedInventory;
    private bool _askedLives;
    private bool _holding;

    // Per connection.
    private bool _linkUp;
    private bool _inGame;
    private bool _enteredThisLink;
    private bool _inventoryReadThisLink;
    // The character died and no inventory has been read since: the record in
    // memory still lists what went into the deathpile, so nothing it says is held.
    private bool _heldUnknown;
    // These fall with a room change: a display read before it shows another
    // room's floor.
    private bool _roomShownHere;
    private bool _floorShownHere;
    // A move, typed or an engine's, went out after the game was entered.
    private bool _moveSentSinceEntry;
    // Lives off a `stat` read on this connection. The count the client carries
    // over a reconnect is the one from before, which is the other side of the
    // comparison.
    private int? _livesReadThisLink;
    // The count the client holds was given by the game on this connection (a
    // `stat`, or the readout of a death that was seen). Only then does it go on
    // the list: a count carried over from an earlier session would hide a death
    // nobody saw in between, and the next comparison would pin it on the wrong
    // hang-up.
    private bool _livesKnownThisLink;
    // Heartbeats until a `stat` is sent to read the lives again, after something
    // changed them with no screen saying so. 0: none owed.
    private int _livesRereadTicks;
    // They changed that way on this connection and no `stat` has given them since.
    private bool _livesChangedUnread;
    // A room display was read on this connection: the client knows what is in the
    // room, and so whether a fight is on.
    private bool _roomReadThisLink;
    // The list on file was marked as left by the game's own exit on this connection.
    private bool _cleanExitMarked;
    // The character's name off a `stat` read on this connection.
    private string? _nameReadThisLink;
    // A game prompt was read on this connection, so the HP the client holds is
    // this connection's and not the one it left the game with.
    private bool _promptThisLink;

    // The death question, open from the connect until it is answered or the link
    // drops: the list it is asked of. What it is judged on is taken when the game
    // is entered, not when the answer comes, since the answer can come much later
    // (the lives need a `stat`) and by then the character has played on.
    private HeldAtDisconnect? _deathBefore;
    // HP at the first prompt of this connection.
    private bool _hpAtEntryTaken;
    private int? _hpAtEntry;
    // The first room the map confirmed on this connection, before any move went
    // out: where the hang-up left the character, or where its dead arrive.
    private RoomKey? _entryRoom;
    // The first inventory read of this connection.
    private InventorySnapshot? _firstInventory;
    // The board's two hang-up lines were printed on this connection.
    private bool _loginLines;
    // The terminal has been told the question is waiting for a `stat`.
    private bool _saidWaiting;

    //   profile          — the named character that is loaded, whose HeldAtDisconnect
    //                      is read and written; null with none (the default profile
    //                      is the template new characters are made from).
    //   realmKey         — the BBS and realm being played, as HeldAtDisconnect.Realm.
    //   maxItemsDropped  — HangupPenaltyNotice.MaxItemsDropped for that realm.
    //   confirmedRoom    — the room the map is sure of, else null.
    //   floor            — the room's latest floor list (GroundItemTracker.Items).
    //   fighting         — a monster the client is set to fight is in the room: the
    //                      pickup waits for the fight to end, as collect-after-combat does.
    //   hostilePresent   — any hostile monster is in the room, fought or not.
    //   isAutoEnabled    — Auto-All; off, nothing is sent.
    //   roomRedisplayFree — RoomRedisplayCoordinator.ShouldSend.
    //   collect          — AutoGetItemsManager.CollectNamed.
    //   reapplyGearSet   — AutoEquipCoordinator.ReapplySetAfterItemsReturned: given
    //                      the names picked up, the set it applied again, or null.
    //   notice           — a line for the terminal.
    //   itemKey          — see HangupItemPlan.Pickup; null for its default.
    //   post             — run after the handlers of the line now being read; the room
    //                      display parser reports a display before the map has taken it in.
    //   vitals           — HP off the statline, null until a prompt has given it, and
    //                      max HP, null until one is known (the statline carries none).
    //   lives            — the lives the client knows of, null when it knows none.
    //   pvpFight         — a fight with a player is on, or one has just attacked.
    //   monsterFight     — in combat with a monster, or a hostile one in the room.
    //   hpShareTop       — HangupPenaltyNotice.HpShareTop for the realm, given whether
    //                      the character was in a fight with a player, and with a monster.
    //   stockRealm       — the realm runs the Stock engine, where a death is known to
    //                      unequip everything and the board prints its hang-up lines.
    //   recordDeath      — RoomTracker.NoteUnwitnessedDeath: the record it made.
    //   staysOnDeath     — an item of this name stays with the character through a
    //                      death (loyal, or cursed so); false for a name the game
    //                      data doesn't know, which is then taken for one a death takes.
    //   entryRoomSays    — HangupDeathArrivalRooms.Read for the active game data,
    //                      given the room the character hung up in and the first
    //                      room confirmed on the way back in.
    //   roomLabel        — a room's name with its number, for the wording.
    public HangupItemRecheck(
        MovementCoordinator coordinator,
        Func<CharacterProfile?> profile,
        Action saveProfile,
        Func<string?> realmKey,
        Func<int> maxItemsDropped,
        Func<InventorySnapshot> inventory,
        Func<RoomKey?> confirmedRoom,
        Func<IReadOnlyList<string>> floor,
        Func<bool> fighting,
        Func<bool> hostilePresent,
        Func<bool> isAutoEnabled,
        Func<bool> roomRedisplayFree,
        Action<string, int> collect,
        Func<IReadOnlyCollection<string>, string?> reapplyGearSet,
        Action<string>? notice = null,
        Func<string, string>? itemKey = null,
        Action<Action>? post = null,
        Func<DateTimeOffset>? now = null,
        LogService? log = null,
        Func<(int Hp, int? MaxHp)?>? vitals = null,
        Func<int?>? lives = null,
        Func<bool>? pvpFight = null,
        Func<bool>? monsterFight = null,
        Func<bool, bool, int?>? hpShareTop = null,
        Func<bool>? stockRealm = null,
        Func<Recovery.UnwitnessedDeath, DeathRecord?>? recordDeath = null,
        Func<string, bool>? staysOnDeath = null,
        Func<RoomKey, RoomKey, Recovery.HangupEntryRoomReading>? entryRoomSays = null,
        Func<RoomKey, string>? roomLabel = null,
        Func<string?>? roomsGameData = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(saveProfile);
        ArgumentNullException.ThrowIfNull(realmKey);
        ArgumentNullException.ThrowIfNull(maxItemsDropped);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(confirmedRoom);
        ArgumentNullException.ThrowIfNull(floor);
        ArgumentNullException.ThrowIfNull(fighting);
        ArgumentNullException.ThrowIfNull(hostilePresent);
        ArgumentNullException.ThrowIfNull(isAutoEnabled);
        ArgumentNullException.ThrowIfNull(roomRedisplayFree);
        ArgumentNullException.ThrowIfNull(collect);
        ArgumentNullException.ThrowIfNull(reapplyGearSet);
        _coordinator = coordinator;
        _profile = profile;
        _saveProfile = saveProfile;
        _realmKey = realmKey;
        _maxItemsDropped = maxItemsDropped;
        _inventory = inventory;
        _confirmedRoom = confirmedRoom;
        _floor = floor;
        _fighting = fighting;
        _hostilePresent = hostilePresent;
        _isAutoEnabled = isAutoEnabled;
        _roomRedisplayFree = roomRedisplayFree;
        _collect = collect;
        _reapplyGearSet = reapplyGearSet;
        _notice = notice;
        _itemKey = itemKey;
        _post = post ?? (static run => run());
        _now = now ?? (static () => DateTimeOffset.Now);
        _log = log;
        _vitals = vitals;
        _lives = lives;
        _pvpFight = pvpFight;
        _monsterFight = monsterFight;
        _hpShareTop = hpShareTop;
        _stockRealm = stockRealm;
        _recordDeath = recordDeath;
        _staysOnDeath = staysOnDeath;
        _entryRoomSays = entryRoomSays;
        _roomLabel = roomLabel;
        _roomsGameData = roomsGameData;
    }

    // ----- for the bug report -------------------------------------------

    private const string NoneYet = "none for this character this session";

    // Where the check stands, in words.
    public string Status => _phase switch
    {
        Phase.AwaitingEntry => "waiting for the game to be entered",
        Phase.AwaitingInventory => "waiting for an inventory read",
        Phase.AwaitingLives => "waiting for a `stat` read",
        Phase.AwaitingRoom => "waiting for a room display",
        Phase.AwaitingFightEnd => "waiting for the fight in the room to end",
        Phase.PickingUp => "picking up",
        _ => "idle",
    } + (_phase != Phase.Idle && !_holding ? " (movement no longer held)" : "")
      + (_phase == Phase.Idle && _deathBefore is not null ? "; a death by the hang-up is still to be judged, on the next `stat` read" : "");

    // How the last check ended, with its time.
    public string LastOutcome { get; private set; } = NoneYet;

    // What the last comparison found short, and what of it was still short when
    // the check ended.
    public IReadOnlyList<(string Name, int Count)> LastMissing { get; private set; } = [];
    public IReadOnlyList<(string Name, int Count)> LastStillMissing { get; private set; } = [];

    // A pass ran to an answer: nothing more will be picked up by it. True when a
    // death by the hang-up is still to be judged on the next `stat` read.
    public event Action<bool>? CheckFinished;

    // The last time a hang-up that could have killed was looked into: what was
    // seen, and what it was taken to mean.
    public string LastDeathCheck { get; private set; } = NoneYet;

    // The learned pair ("died in → arrived in") the last such look went by, when
    // the room the character came back in said yes on the strength of one; null
    // when it didn't.
    public string? LastLearnedArrivalUsed { get; private set; }

    // Copies of an item this check has asked the game for and not yet seen arrive:
    // those are coming back, not being acquired. Only while it is picking up. The
    // lists above are a record of how the last check went and say nothing about now.
    public int BeingPickedUp(string name)
    {
        if (_phase != Phase.PickingUp) return 0;
        string key = Key(name);
        foreach ((string planned, int wanted) in _plan)
            if (string.Equals(Key(planned), key, StringComparison.OrdinalIgnoreCase))
                return Math.Max(0, wanted - _taken.GetValueOrDefault(planned));
        return 0;
    }

    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // ----- the list on disk ---------------------------------------------

    // ProfileService.ProfileSaving: write what is held now. Only for the character
    // the check reads from, in the game, with an inventory read on this connection:
    // before that the record in memory is the last session's, and the list on disk
    // is the one still to be compared. After a death there is nothing to write.
    public void StampForSave(CharacterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!ReferenceEquals(profile, _profile()) || !_inGame) return;
        if (_heldUnknown)
        {
            profile.HeldAtDisconnect = null;
            return;
        }
        // While the lives are waited for the inventory has been read and the list
        // on disk not yet compared with it.
        if (_phase == Phase.AwaitingLives) return;
        // With no inventory read the older list stands, except when the link is
        // dropping with the character low enough to be killed for it (LeaveGame).
        bool itemsKnown = _inventoryReadThisLink;
        if (!itemsKnown && _leaving != Leaving.DropThatCouldKill) return;

        List<(string Name, int Count)> outstanding = itemsKnown ? Outstanding() : new List<(string Name, int Count)>();
        InventorySnapshot? held = itemsKnown ? _inventory() : null;
        bool couldKill = _leaving == Leaving.DropThatCouldKill;
        HeldAtDisconnect? carry = couldKill ? _carryFrom : null;
        HeldAtDisconnect? carriedItems = _carryItems ? carry : null;
        profile.HeldAtDisconnect = new HeldAtDisconnect
        {
            At = _now(),
            Realm = _realmKey(),
            Character = _nameReadThisLink ?? carry?.Character,
            Room = _confirmedRoom() is { } room ? new RoomRef(room.Map, room.Room) : null,
            GameData = _roomsGameData?.Invoke(),
            // What an unfinished check hasn't found was dropped before this
            // connection; a drop of this one comes on top of it.
            PenaltiesSpanned = outstanding.Count > 0 ? Spanned(_before) + 1 : 1,
            Items = held is { } pack ? HangupItemPlan.WithOutstanding(HangupItemPlan.Held(pack), outstanding)
                : carriedItems is not null ? carriedItems.Items.Select(i => new HeldItem(i.Name, i.Count)).ToList()
                : new List<HeldItem>(),
            ItemsUnknown = held is null && carriedItems is null,
            // A dropped character can't leave by the exit command: the game refuses it.
            CleanExit = _leaving == Leaving.CleanExit && VitalsNow() is not { Hp: <= 0 },
            Hp = VitalsNow()?.Hp,
            MaxHp = VitalsNow()?.MaxHp,
            Lives = _livesKnownThisLink ? _lives?.Invoke() : _livesChangedUnread ? null : carry?.Lives,
            PvpFight = couldKill ? _dropPvp : _pvpFight?.Invoke() ?? false,
            InCombat = couldKill ? _dropFight : _monsterFight?.Invoke() ?? false,
            Worn = held is { } worn ? DeathLootCapture.FromSnapshot(worn).Equipped : carriedItems?.Worn,
            Carried = held is { } carried ? DeathLootCapture.LostOf(carried) : carriedItems?.Carried,
            Coins = held?.Currency ?? carriedItems?.Coins,
        };
        if (profile.HeldAtDisconnect.CleanExit) _cleanExitMarked = true;
    }

    // Set by LeaveGame for the list a drop that could kill writes with no inventory
    // read: the fight it is judged by, and the older list when that one's hang-up
    // is known not to have killed, whose lives and name then still hold (and its
    // items, when nothing can have changed them).
    private bool _dropPvp;
    private bool _dropFight;
    private HeldAtDisconnect? _carryFrom;
    private bool _carryItems;

    // How the game is being left, for the list written as it is.
    private enum Leaving
    {
        No,
        // The link went down.
        Drop,
        // The link went down with no inventory read on it and the character low
        // enough for the penalty to kill: the list is written anyway, without what
        // was held, so a death that follows is not laid at the older list's room.
        DropThatCouldKill,
        // By the game's own exit, the link still up.
        CleanExit,
    }

    private Leaving _leaving;

    // Short items of a check that hasn't ended, which a list saved now must keep.
    private List<(string Name, int Count)> Outstanding() =>
        _phase is Phase.AwaitingRoom or Phase.AwaitingFightEnd or Phase.PickingUp
            ? HangupItemPlan.Outstanding(_missing, _taken)
            : new List<(string Name, int Count)>();

    private static int Spanned(HeldAtDisconnect? list) => Math.Max(1, list?.PenaltiesSpanned ?? 1);

    // ----- connection and game entry ------------------------------------

    // A new link is up. The check is decided here, not on the first game prompt,
    // because the movement engines act on that prompt too and the hold has to be
    // in place before any of them.
    public void NoteConnected()
    {
        Abandon("a new connection began");
        _linkUp = true;
        _inGame = false;
        _enteredThisLink = false;
        _inventoryReadThisLink = false;
        _heldUnknown = false;
        _roomShownHere = false;
        _floorShownHere = false;
        _moveSentSinceEntry = false;
        _livesReadThisLink = null;
        _livesKnownThisLink = false;
        _livesRereadTicks = 0;
        _livesChangedUnread = false;
        _roomReadThisLink = false;
        _cleanExitMarked = false;
        _nameReadThisLink = null;
        _promptThisLink = false;
        _askedLives = false;
        _deathBefore = null;
        _hpAtEntryTaken = false;
        _hpAtEntry = null;
        _entryRoom = null;
        _firstInventory = null;
        _loginLines = false;
        _saidWaiting = false;

        if (_profile()?.HeldAtDisconnect is not { } before) return;
        bool items = before.Items.Count > 0 && _maxItemsDropped() > 0;
        bool arena = ArenaDeathPossible(before);
        bool death = DeathSuspected(before) || arena;
        if (!items && !death) return;
        string? realm = _realmKey();
        if (realm is null || !string.Equals(before.Realm, realm, StringComparison.OrdinalIgnoreCase))
        {
            _log?.Debug(LogCategory,
                $"the stored list is for '{before.Realm ?? "(no realm)"}', not '{realm ?? "(no realm)"}': left alone");
            return;
        }

        _before = before;
        _phase = Phase.AwaitingEntry;
        _entryTicks = 0;
        string left = $"{before.At.ToLocalTime():yyyy-MM-dd HH:mm:ss}, {RoomText(before.Room)}"
            + (before.GameData is { } set ? $" of game data '{set}'" : "");
        if (arena)
        {
            _deathBefore = before;
            Hold("whether the character died in the arena after the hang-up is checked on entering the game");
            _log?.Info(LogCategory,
                $"The character was {HpText(before)} in an arena room when it was last in the game ({left}). A hang-up "
                + "there while dropped is the arena's death, which takes no life and no item and moves the character to "
                + "where that room's dead arrive: checked on entering the game, from the room it comes back in"
                + (DeathSuspected(before) ? ", and from its lives should it prove an ordinary death." : "."));
            return;
        }
        if (death)
        {
            _deathBefore = before;
            Hold("whether the hang-up penalty killed the character is checked on entering the game");
            _log?.Info(LogCategory,
                $"The character was {HpText(before)} when it was last in the game ({left}), {FightText(before)}, and this "
                + "realm's settings penalise that hang-up. A board that does kills a character that is dropped, or that "
                + "the HP it takes drops under the death threshold: checked on entering the game, from its lives "
                + $"({Shown(before.Lives)} then) against a `stat` read on this connection.");
            return;
        }
        Hold("items a hang-up may have dropped are checked for on entering the game");
        _log?.Info(LogCategory,
            $"This realm drops items for a hang-up. {before.Items.Count} kind(s) of item were held when the character "
            + $"was last in the game ({left}); they are checked against the inventory on entering the game.");
    }

    // The link is going down, told ahead of everything the drop tears down (the
    // fight with a player, combat, the room's occupants): the list written here
    // says what fight the character was in, which decides the penalty.
    public void NoteLinkDropping() => LeaveGame(linkDropped: true);

    // The link is gone. InGameCapture has normally reported leaving the game
    // already; an entry it never saw (taken from a room display) is left here.
    public void NoteDisconnected()
    {
        LeaveGame(linkDropped: true);
        _linkUp = false;
        // A question the pass left open, waiting for a `stat` that never came, ends
        // with the link: by now the list on disk is this connection's own.
        if (_deathBefore is not null && _phase == Phase.Idle)
        {
            LastDeathCheck = $"{_now():HH:mm:ss} not checked: the link dropped before a `stat` was read";
            _log?.Info(LogCategory,
                "Whether the hang-up penalty killed the character was never checked: the link dropped before a `stat` was read.");
        }
        _deathBefore = null;
        Abandon("the link dropped");
    }

    // InGameCapture.InGameChanged: on, a game prompt was read. Off while the link
    // is up, the game was left by its own exit (the character saved, the board's
    // menu showing): a link going down reaches NoteLinkDropping first.
    public void OnInGameChanged(bool inGame)
    {
        if (!inGame)
        {
            LeaveGame(linkDropped: false);
            return;
        }
        _promptThisLink = true;
        // A game prompt after "the game was left": it wasn't. A line that only
        // looked like the exit's (chat can) had the list marked as a clean exit,
        // and a hang-up after it must not go unlooked at.
        if (_cleanExitMarked && _profile()?.HeldAtDisconnect is { CleanExit: true } stillIn)
        {
            stillIn.CleanExit = false;
            _saveProfile();
        }
        _cleanExitMarked = false;
        // The statline's own handler runs ahead of this one (PromptParser is built
        // first), so the HP read here is that first prompt's.
        if (!_hpAtEntryTaken)
        {
            _hpAtEntryTaken = true;
            _hpAtEntry = _vitals?.Invoke()?.Hp;
        }
        EnterGame();
    }

    // The game's lives count changed, or is about to, with no screen telling the
    // client the new one: a life asked back after a death (Sysop god lives), or a
    // level trained, which gives lives. The count the client holds is then one
    // off, and a list carrying it would read the next death by a hang-up as "no
    // life lost" and say nothing. So it is not known again until a `stat` gives
    // it, and one is sent for it shortly, under Auto-All.
    public void NoteLivesChangedUnread(string why)
    {
        if (!_linkUp) return;
        _livesKnownThisLink = false;
        _livesChangedUnread = true;
        _livesRereadTicks = LivesRereadTicks;
        _log?.Info(LogCategory, $"The lives count is no longer known ({why}): a `stat` will read it again.");
    }

    // Not sent with Auto-All off or out of the game: the count then stays unknown,
    // and the next list says so, until a `stat` is read some other way.
    private void RereadLivesWhenDue()
    {
        if (_livesRereadTicks <= 0 || --_livesRereadTicks > 0) return;
        if (_livesKnownThisLink || !_inGame || !_isAutoEnabled()) return;
        _log?.Info(LogCategory, "Sending `stat` to read the lives again.");
        _wire.Send("stat");
    }

    // The board's `Last time you were on, you disconnected while playing.`
    public void NoteHangupLoginLine()
    {
        if (!_linkUp) return;
        // It is only asked for on Stock. Seen anywhere else it is worth a line in
        // the log, since nobody has yet said another board prints it.
        if (!_loginLines && !(_stockRealm?.Invoke() ?? false))
            _log?.Info(LogCategory, "The board printed the Stock hang-up line on a realm that isn't set as Stock.");
        _loginLines = true;
    }

    // StatParser.ScreenParsed, when that `stat` gave the lives; the name is the
    // one on the same screen.
    public void NoteLivesRead(int lives, string? name = null)
    {
        if (!_linkUp) return;
        _livesReadThisLink = lives;
        _livesKnownThisLink = true;
        _livesRereadTicks = 0;
        _livesChangedUnread = false;
        if (!string.IsNullOrWhiteSpace(name)) _nameReadThisLink = name.Trim();
        if (_before is { } before && _phase == Phase.AwaitingLives)
            CompareAndSave(before);
        // Left open by a pass that couldn't ask for the lives itself: any `stat`
        // read on this connection answers it, the user's own included.
        else if (_deathBefore is { } open && _firstInventory is not null
                 && _phase is not (Phase.AwaitingEntry or Phase.AwaitingInventory))
            AnswerOpenQuestion(open);
    }

    private void EnterGame()
    {
        _inGame = true;
        if (_enteredThisLink) return;
        _enteredThisLink = true;
        if (_phase != Phase.AwaitingEntry) return;
        _phase = Phase.AwaitingInventory;
        _ticks = InventoryWaitTicks;
        _askedInventory = false;
    }

    // Out of the game, by a dropped link or an exit to the board's menu: what is
    // held now is what the next entry is checked against. Saved while the check
    // still counts as under way, so what it hasn't found goes onto the list.
    //
    // With no inventory read on this connection there is nothing to write about
    // what is held, and the older list stands, still to be compared. Two things
    // are done about that:
    //   - the link dropping with the character low enough for the penalty to
    //     kill writes a list anyway, without the items: where and when that
    //     hang-up was must not be the older list's, or a death that follows is
    //     recorded in the wrong room;
    //   - otherwise the older list is kept, with what this connection saw of the
    //     entry after its hang-up (the board's lines, HP at the first prompt),
    //     which the connection that does judge it won't see again.
    private void LeaveGame(bool linkDropped)
    {
        if (!_inGame) return;
        HeldAtDisconnect? older = OlderList();
        bool pvp = _pvpFight?.Invoke() ?? false;
        bool fight = _monsterFight?.Invoke() ?? false;
        // With no room display read on this connection the client can't say what
        // is in the room: a character that comes back dropped where it left, and
        // is hung up again at the first prompt, is in the fight it left.
        if (!_roomReadThisLink && older is not null)
        {
            pvp |= older.PvpFight;
            fight |= older.InCombat;
        }
        bool couldKill = linkDropped && !_inventoryReadThisLink && !_heldUnknown && _phase != Phase.AwaitingLives
            && VitalsNow() is { } vitals
            && Recovery.HangupDeath.Suspected(vitals.Hp, vitals.MaxHp, _hpShareTop?.Invoke(pvp, fight));
        if (couldKill)
        {
            _dropPvp = pvp;
            _dropFight = fight;
            SettleOlderList(older);
        }
        _leaving = !linkDropped ? Leaving.CleanExit : couldKill ? Leaving.DropThatCouldKill : Leaving.Drop;
        // Nothing is written over the list while it is still to be compared.
        bool listStands = !_inventoryReadThisLink || _phase == Phase.AwaitingLives;
        if (!listStands || couldKill)
            _saveProfile();
        else if (_deathBefore is { } open && ReferenceEquals(_profile()?.HeldAtDisconnect, open))
        {
            open.LoginLinesSeenSince |= _loginLines;
            open.HpAtFirstEntrySince ??= _hpAtEntry;
            if (_entryRoom is { } entered) open.EntryRoomSince ??= new RoomRef(entered.Map, entered.Room);
            // It moved on before the map knew where it had come back: the room a
            // later connection starts in is not that room.
            else if (_moveSentSinceEntry && open.EntryRoomSince is null) open.MovedBeforeEntryRoomKnownSince = true;
            _saveProfile();
        }
        _leaving = Leaving.No;
        _carryFrom = null;
        _carryItems = false;
        _inGame = false;
        switch (_phase)
        {
            case Phase.AwaitingInventory or Phase.AwaitingLives:
                // Not compared yet, and nothing was stamped over the list: it waits
                // for the game to be entered again, on this link or the next. An
                // inventory read while the lives were waited for doesn't count on
                // that entry: a save made there would write over the list.
                _phase = Phase.AwaitingEntry;
                _enteredThisLink = false;
                _inventoryReadThisLink = false;
                break;
            case Phase.AwaitingRoom or Phase.AwaitingFightEnd or Phase.PickingUp:
                Abandon("the game was left before the check ended; what it hadn't found is kept on the list");
                break;
        }
    }

    // The list on file, when it is this realm's and this character's.
    private HeldAtDisconnect? OlderList()
    {
        if (_profile()?.HeldAtDisconnect is not { } list) return null;
        string? realm = _realmKey();
        if (realm is null || !string.Equals(list.Realm, realm, StringComparison.OrdinalIgnoreCase)) return null;
        return OtherCharacter(list) is null ? list : null;
    }

    // The link is dropping with the character low enough to be killed and no
    // inventory read on this connection, so a list is about to be written for this
    // hang-up over the older one. The older one is settled first, with what this
    // connection knows:
    //   - its hang-up is known not to have killed (it couldn't, or HP at the first
    //     prompt after it says so: the character came back alive, if down; never
    //     when it came back with more HP than it left with). Then
    //     the lives and the name it carries are still current and go onto the new
    //     list, which a connection too short for a `stat` could not give. This is
    //     the commonest way the penalty kills: a hang-up that leaves the character
    //     dropped, a re-entry with the monster still there, and a second hang-up at
    //     the first prompt. What it held goes on too when nothing can have changed
    //     it: no move was sent, and the realm's penalty takes no items.
    //   - its hang-up could have killed and was never judged. It is judged now
    //     rather than dropped: on Stock, a `stat` on this connection showing one
    //     life fewer is a death by it, recorded here from the older list. Off Stock
    //     that is told, not recorded. When it can't be told, that is said, since
    //     nothing will ask again.
    //   - a death seen since the older list was written has its own record, so the
    //     older list answers nothing and carries nothing.
    private void SettleOlderList(HeldAtDisconnect? older)
    {
        _carryFrom = null;
        _carryItems = false;
        if (older is null) return;
        // A death seen since the list was written has its own record and owns the
        // life it cost: judging the list again would record that life twice, and the
        // count it carries is out of date.
        if (DeathSeenSince(older))
        {
            _deathBefore = null;
            _log?.Info(LogCategory, "A death was recorded after the older list was written: the hang-up before this connection is let go unjudged.");
            return;
        }
        int? hpAtEntry = older.HpAtFirstEntrySince ?? _hpAtEntry;
        int? share = ShareTop(older);
        // HP cannot rise while out of the game except by a death (it sets HP to its
        // maximum), so a character back with more HP than it left with may have been
        // killed by a hang-up the client took to be harmless (no fight it knew of):
        // its count is then one too high to carry.
        bool cameBackHigher = older.Hp is { } leftHp && hpAtEntry is { } backHp && backHp > leftHp;
        if (older.CleanExit || (!cameBackHigher && Recovery.HangupDeath.RuledOut(older.Hp, older.MaxHp, share, hpAtEntry)))
        {
            _carryFrom = older;
            _carryItems = !older.ItemsUnknown && !_moveSentSinceEntry && _maxItemsDropped() <= 0;
            _log?.Info(LogCategory,
                "The link dropped with the character low enough for the penalty to kill and no inventory read on this "
                + "connection. The hang-up before it didn't kill (the character came back "
                + $"{(hpAtEntry is { } hp ? $"at {hp} HP" : "as it left")}), so the lives and name of the older list still hold "
                + $"and go onto the list written for this hang-up{(_carryItems ? ", with what was held" : "; what is held isn't known")}.");
            return;
        }
        if (older.Hp is null) return;

        bool stock = _stockRealm?.Invoke() ?? false;
        bool? loginLines = stock ? _loginLines || older.LoginLinesSeenSince : null;
        (bool? entryRoom, string? entryRoomText, LastLearnedArrivalUsed) = EntryRoom(older);
        (Recovery.HangupDeathVerdict verdict, string why, bool arena) =
            Verdict(older, hpAtEntry, worn: null, loginLines, takenStillHeld: null, entryRoom, entryRoomText,
                entryRoomLearned: LastLearnedArrivalUsed is not null);
        // Without an inventory read the worn and still-held checks that tell a death
        // by that hang-up from a life lost on another client (recovered, everything
        // worn again) can't be made. On Stock the board's login lines still can; off
        // Stock only the room it came back in does (where the dead of the room it
        // hung up in arrive), and without that it is told instead.
        if (verdict == Recovery.HangupDeathVerdict.Died && !stock && entryRoom != true)
        {
            verdict = Recovery.HangupDeathVerdict.Unsure;
            why += ", but no inventory was read on this connection and this realm prints no login lines, so nothing separates "
                + "a death by that hang-up from a life lost on another client";
        }
        string seen = $"judged as the link dropped again with no inventory read: left the game {older.At.ToLocalTime():yyyy-MM-dd HH:mm:ss} "
            + $"at {RoomText(older.Room)} with HP {Shown(older.Hp)}, lives {Shown(older.Lives)}; since then HP {Shown(hpAtEntry)} at "
            + $"the first prompt, lives {Shown(_livesReadThisLink)} from a `stat`"
            + (entryRoomText is null ? "" : $", came back in {entryRoomText}");
        switch (verdict)
        {
            case Recovery.HangupDeathVerdict.Died when _livesReadThisLink is { } livesNow:
                if (arena) RecordArenaDeath(older, atEntry: false, livesNow, why, seen, entryRoomText);
                else RecordDeath(older, held: null, livesNow, why, seen, entryRoom == true ? entryRoomText : null);
                break;
            case Recovery.HangupDeathVerdict.NeedsLives or Recovery.HangupDeathVerdict.Unsure:
                _deathBefore = null;
                LastDeathCheck = $"{_now():HH:mm:ss} can't tell, nothing recorded: {why}. Seen: {seen}";
                _log?.Info(LogCategory, $"The hang-up before this connection is let go unjudged: {why}.");
                _notice?.Invoke($"[Hang-up check: you were {HpText(older)} when you left the game before this connection, and "
                                + $"whether that hang-up killed can no longer be told: {why}, and the link has dropped again "
                                + "with the character low enough to be killed. No death was recorded for it]");
                break;
            default:
                _deathBefore = null;
                _log?.Info(LogCategory, $"The hang-up before this connection didn't kill the character: {why}.");
                break;
        }
    }

    // Another character was loaded: the check, and what it last found, belonged to
    // the one before.
    public void OnProfileLoaded()
    {
        Abandon("another profile was loaded");
        _inventoryReadThisLink = false;
        _heldUnknown = false;
        _deathBefore = null;
        _livesReadThisLink = null;
        _livesKnownThisLink = false;
        _nameReadThisLink = null;
        _firstInventory = null;
        LastOutcome = NoneYet;
        LastMissing = [];
        LastStillMissing = [];
        LastDeathCheck = NoneYet;
        LastLearnedArrivalUsed = null;
    }

    // RoomTracker.PlayerDeathObserved. The inventory record goes on naming what
    // went into the deathpile until it is read again (PostDeathInventoryRefresh
    // asks for that at the graveyard, and the link can drop first). A list written
    // from it would name the whole pile as held, and the next entry would read all
    // of it as dropped by a hang-up. The list is void until that read arrives.
    public void OnPlayerDied()
    {
        // The death's own readout gave the lives (unless a life has just been
        // asked back, which that readout doesn't count), and a death seen on this
        // connection has its own record: an open question about the hang-up before
        // it could no longer be told apart from it.
        _livesKnownThisLink = _linkUp && _livesRereadTicks == 0;
        _deathBefore = null;
        if (!_inGame) return;
        _inventoryReadThisLink = false;
        _heldUnknown = true;
        if (_phase is Phase.Idle or Phase.AwaitingEntry) _saveProfile();
        // No word on the terminal: what is missing now is in the deathpile.
        else Finish("the character died, so what was held before no longer says what is missing", quiet: true);
    }

    // Reset States. Before the comparison only the hold goes: the list is still to
    // be compared, and is when an inventory is read. After it the check stops.
    public void Cancel(string reason)
    {
        switch (_phase)
        {
            case Phase.AwaitingEntry or Phase.AwaitingInventory or Phase.AwaitingLives:
                ReleaseHold($"{reason}: movement is no longer held; the list is kept and compared when an inventory is read");
                break;
            case Phase.AwaitingRoom or Phase.AwaitingFightEnd or Phase.PickingUp:
                Finish($"stopped ({reason})");
                break;
        }
    }

    // ----- the pass -----------------------------------------------------

    // InventoryManager.FullInventoryParsed.
    public void OnInventoryRead()
    {
        _inventoryReadThisLink = true;
        _heldUnknown = false;
        if (_linkUp) _firstInventory ??= _inventory();
        if (_phase != Phase.AwaitingInventory || _before is null) return;
        CompareAndSave(_before);
    }

    private void CompareAndSave(HeldAtDisconnect before)
    {
        Compare(before);
        // Still under way: put what is held now on disk in place of the list just
        // read, with the short items kept on it (StampForSave).
        if (_phase is not (Phase.Idle or Phase.AwaitingLives)) _saveProfile();
    }

    private void Compare(HeldAtDisconnect before)
    {
        _taken.Clear();
        _missing = new List<(string Name, int Count)>();
        LastMissing = [];
        LastStillMissing = [];
        _log?.Debug(LogCategory, $"held before: {Names(before.Items.Select(i => (i.Name, i.Count)))}");

        // A profile can be loaded over another character. Its list answers nothing
        // about this one: not what a hang-up took, and not whether it died.
        if (OtherCharacter(before) is { } other)
        {
            _deathBefore = null;
            LastDeathCheck = $"{_now():HH:mm:ss} not asked: {other}";
            Finish(other, quiet: true);
            return;
        }
        // A death seen since the list was written took what the list names into
        // its own pile, and has its own record.
        if (DeathSeenSince(before))
        {
            _deathBefore = null;
            Finish("a death was recorded after the list was written, so it no longer says what a hang-up took", quiet: true);
            return;
        }
        // What the death question came to, for a pass that ends without an item check.
        string death = "";
        if (ReferenceEquals(_deathBefore, before))
        {
            switch (AskDeath(before, mayAsk: true))
            {
                case DeathAnswer.Asked or DeathAnswer.Recorded:
                    return;
                case DeathAnswer.Open or DeathAnswer.CantTell when !SameRoomAsLeft(before):
                    // A death isn't ruled out and the character isn't known to be
                    // where it left the game: what is missing may be a deathpile,
                    // and is neither picked up nor reported as dropped.
                    Finish("a death by the hang-up isn't ruled out, and the character isn't known to be in the room "
                           + "it left the game in", quiet: true);
                    return;
                case DeathAnswer.No:
                    death = "the hang-up didn't kill the character; ";
                    break;
                default:
                    death = "whether the hang-up killed the character couldn't be told; ";
                    break;
            }
        }
        if (before.ItemsUnknown)
        {
            Finish(death + "what was held when the game was left isn't known, so nothing can be missing from it");
            return;
        }
        if (before.Items.Count == 0 || _maxItemsDropped() <= 0)
        {
            Finish(death + "there are no dropped items to look for (the realm's settings don't say a hang-up drops any)");
            return;
        }

        _missing = HangupItemPlan.Missing(before.Items, _inventory());
        LastMissing = _missing;
        LastStillMissing = _missing;
        if (_missing.Count == 0)
        {
            Finish("nothing held before is missing");
            return;
        }
        _log?.Info(LogCategory, $"Held before and missing now: {Names(_missing)}.");

        if (!_isAutoEnabled())
        {
            Finish("Auto-All is off");
            return;
        }
        if (CameInElsewhere(before) is { } elsewhere)
        {
            Finish(elsewhere);
            return;
        }
        // Once the hold is given up, or a move has gone out, nothing has kept the
        // character where it came in. Then only the map can say this is still the
        // room the items fell in, and a map that isn't sure of both rooms can't: it
        // keeps its old room while unsure, so walking doesn't show as a room change.
        if ((!_holding || _moveSentSinceEntry) && !SameRoomAsLeft(before))
        {
            Finish(_holding
                ? "a move was sent since the game was entered, and the map can't say this is the room the character left the game in"
                : "the inventory read came after the check had stopped holding movement, and the map can't say this "
                  + "is the room the character left the game in");
            return;
        }
        if (_roomShownHere)
        {
            CheckRoom(before);
            return;
        }
        // No hold, so no time to draw the room in: the floor stays unread.
        if (!_holding)
        {
            Finish(FloorUnseen);
            return;
        }

        _phase = Phase.AwaitingRoom;
        _ticks = RoomWaitTicks;
        if (_roomRedisplayFree())
        {
            _log?.Info(LogCategory, "No room display has been read for this room: asking for one.");
            _wire.Send("");   // a bare Enter redraws the room, floor list included
        }
    }

    // ----- a hang-up that could have killed ------------------------------

    // The list says the character was dropped, or low enough for the penalty to
    // drop it, and the realm's settings penalise a hang-up in the fight it was in.
    // Only then is a death looked for.
    // Never after the game's own exit: that is not a hang-up, and the board takes
    // nothing for it.
    private bool DeathSuspected(HeldAtDisconnect before) =>
        !before.CleanExit && Recovery.HangupDeath.Suspected(before.Hp, before.MaxHp, ShareTop(before));

    // The list says the character was dropped in an arena room. A hang-up there is
    // the arena's death on the Stock engine whatever the board's penalty, so this
    // is looked into on any realm, and never after the game's own exit.
    private static bool ArenaDeathPossible(HeldAtDisconnect before) =>
        !before.CleanExit && before.Hp is <= 0 && InArenaRoom(before);

    private static bool InArenaRoom(HeldAtDisconnect before) =>
        before.Room is { } room && Recovery.ArenaDeathRooms.Contains(new RoomKey(room.Map, room.Room));

    // What a list comes to: the arena's death first, where it could have been one,
    // and the ordinary rule when it wasn't.
    private (Recovery.HangupDeathVerdict Verdict, string Why, bool Arena) Verdict(
        HeldAtDisconnect before, int? hpAtEntry, bool? worn, bool? loginLines, bool? takenStillHeld,
        bool? entryRoom, string? entryRoomText, bool entryRoomLearned)
    {
        string? notArena = null;
        if (ArenaDeathPossible(before))
        {
            (Recovery.HangupDeathVerdict arena, string arenaWhy) = Recovery.HangupDeath.JudgeArena(
                before.Hp, hpAtEntry, before.Lives, _livesReadThisLink, takenStillHeld, entryRoom, entryRoomText,
                ordinaryDeathPossible: ShareTop(before) is not null, entryRoomLearned);
            if (arena != Recovery.HangupDeathVerdict.NotSuspected) return (arena, arenaWhy, true);
            notArena = arenaWhy;
        }
        (Recovery.HangupDeathVerdict verdict, string why) = Recovery.HangupDeath.Judge(
            before.Hp, before.MaxHp, ShareTop(before), hpAtEntry, before.Lives, _livesReadThisLink,
            worn, loginLines, takenStillHeld, entryRoom, entryRoomText, OwnedNothing(before), entryRoomLearned);
        // A realm that doesn't penalise the hang-up was looked at for the arena
        // alone: the reason it wasn't that is the one worth reading.
        if (verdict == Recovery.HangupDeathVerdict.NotSuspected && notArena is not null)
            why = $"no arena death ({notArena}), and {why}";
        return (verdict, why, false);
    }

    private int? ShareTop(HeldAtDisconnect before) => _hpShareTop?.Invoke(before.PvpFight, before.InCombat);

    private (int Hp, int? MaxHp)? VitalsNow() => _promptThisLink ? _vitals?.Invoke() : null;

    // A death seen since the list was written took what the list names into its
    // own pile, has its own record, and accounts for a life.
    private bool DeathSeenSince(HeldAtDisconnect before) =>
        _profile()?.DeathHistory is { } deaths && deaths.Exists(d => d.At > before.At);

    // Says so when the list names one character and the `stat` read on this
    // connection another. Null while either name isn't known.
    private string? OtherCharacter(HeldAtDisconnect before) =>
        before.Character is { Length: > 0 } then && _nameReadThisLink is { Length: > 0 } now
        && !string.Equals(then, now, StringComparison.OrdinalIgnoreCase)
            ? $"the list is {then}'s, and the character in the game is {now}"
            : null;

    private enum DeathAnswer
    {
        No,         // no death: the question is closed
        Recorded,   // a death was recorded, and the pass ended with it
        Asked,      // a `stat` went out; the pass waits for it
        Open,       // the lives aren't known and can't be asked for; a `stat` read later answers
        CantTell,   // nothing will tell; said once, nothing recorded
    }

    // Asks the open question with what is known now.
    //   mayAsk — the pass is at its inventory read and may send a `stat` for the lives.
    private DeathAnswer AskDeath(HeldAtDisconnect before, bool mayAsk)
    {
        bool stock = _stockRealm?.Invoke() ?? false;
        InventorySnapshot held = _firstInventory ?? _inventory();
        int? share = ShareTop(before);
        // The first entry after the hang-up is the one that shows what it did. That
        // is this connection, unless an earlier one came and went without judging
        // the list and left what it saw on it.
        int? hpAtEntry = before.HpAtFirstEntrySince ?? _hpAtEntry;
        // A death unequips everything. Stock's engine does so for a cursed piece too;
        // Paradigm isn't known to, and is treated the same way by the user's ruling.
        bool? worn = _firstInventory is null ? null : held.EquippedItems.Count > 0;
        // Stock only: no other board is known to print these lines.
        bool? loginLines = stock ? _loginLines || before.LoginLinesSeenSince : null;
        List<(string Name, int Count)> gone = HangupItemPlan.Missing(before.Items, held);
        bool? takenStillHeld = _firstInventory is null ? null : TakenStillHeld(before, held);
        (bool? entryRoom, string? entryRoomText, LastLearnedArrivalUsed) = EntryRoom(before);
        (Recovery.HangupDeathVerdict verdict, string why, bool arena) =
            Verdict(before, hpAtEntry, worn, loginLines, takenStillHeld, entryRoom, entryRoomText,
                entryRoomLearned: LastLearnedArrivalUsed is not null);

        RoomKey? here = _confirmedRoom();
        string evidence =
            $"left the game {before.At.ToLocalTime():yyyy-MM-dd HH:mm:ss} at {RoomText(before.Room)}"
            + $"{(InArenaRoom(before) ? " (an arena room)" : "")} with HP "
            + $"{Shown(before.Hp)} of {Shown(before.MaxHp)}, lives {Shown(before.Lives)}, {FightText(before)}; the realm's "
            + $"settings say that hang-up costs up to {Shown(share)}% of max HP; since then: HP {Shown(hpAtEntry)} "
            + $"at the first prompt, lives {Shown(_livesReadThisLink)} from a `stat`, "
            + (worn is { } w ? $"{(w ? "something" : "nothing")} worn at the first inventory read, " : "")
            + (loginLines is { } l ? $"the board's hang-up lines {(l ? "printed" : "not printed")}, " : "")
            + (entryRoomText is null ? "the room it came back in not confirmed before it moved, "
                : $"came back in {entryRoomText} ({(entryRoom is { } says ? says ? "a death by this hang-up puts it there" : "a death by this hang-up does not put it there" : "which says nothing either way")}), ")
            + $"{(here is { } at ? $"room {at}" : "room not known")} now; held then and not at the first inventory read: "
            + (before.ItemsUnknown ? "not known (no inventory was read on the connection it left on)"
                : gone.Count > 0 ? Names(gone) : "nothing");
        // A realm that doesn't penalise the hang-up is looked at for the arena alone.
        _log?.Info(LogCategory, (DeathSuspected(before)
            ? "A hang-up the penalty could have killed for: "
            : "A hang-up while dropped in an arena room: ") + $"{evidence}.");

        switch (verdict)
        {
            // Died is only ever said on a count read on this connection, so the
            // record never carries a made-up one.
            case Recovery.HangupDeathVerdict.Died when _livesReadThisLink is { } livesNow:
                if (arena) RecordArenaDeath(before, atEntry: _holding, livesNow, why, evidence, entryRoomText);
                else RecordDeath(before, held, livesNow, why, evidence, entryRoom == true ? entryRoomText : null);
                return DeathAnswer.Recorded;

            case Recovery.HangupDeathVerdict.NeedsLives:
                // The login sends a `stat` ahead of its `i`; an entry made by hand
                // doesn't. One is sent only while the pass still holds movement:
                // once it has let go, nothing more goes out for it.
                if (mayAsk && !_askedLives && _holding && _inGame && _isAutoEnabled())
                {
                    _askedLives = true;
                    _phase = Phase.AwaitingLives;
                    _ticks = LivesWaitTicks;
                    _log?.Info(LogCategory, "No `stat` has been read since entering the game: sending one, for the lives.");
                    _wire.Send("stat");
                    return DeathAnswer.Asked;
                }
                LastDeathCheck = $"{_now():HH:mm:ss} waiting for a `stat` on this connection: {why}. Seen: {evidence}";
                SayWaiting(before, !_isAutoEnabled() ? "Auto-All is off, so no `stat` was sent"
                    : _askedLives ? "the `stat` sent for it wasn't answered"
                    : "the check had stopped holding movement, so no `stat` was sent");
                return DeathAnswer.Open;

            case Recovery.HangupDeathVerdict.Unsure:
                _deathBefore = null;
                LastDeathCheck = $"{_now():HH:mm:ss} can't tell, nothing recorded: {why}. Seen: {evidence}";
                _log?.Info(LogCategory, $"No death is recorded for the hang-up: {why}.");
                _notice?.Invoke($"[Hang-up check: {Looked(before)}. No death was recorded: {why}"
                                + (gone.Count > 0 ? $". Held then and not now: {Names(gone)}" : "") + "]");
                return DeathAnswer.CantTell;

            default:
                _deathBefore = null;
                LastDeathCheck = $"{_now():HH:mm:ss} no death: {why}. Seen: {evidence}";
                _log?.Info(LogCategory, $"The hang-up didn't kill the character: {why}.");
                return DeathAnswer.No;
        }
    }

    private bool StaysOnDeath(string name) => _staysOnDeath?.Invoke(name) ?? false;

    // What the room the character came back in says of a death by the hang-up, and
    // the words for it. The room is the first one the map confirmed after the
    // hang-up with no move sent: this connection's, or the one an earlier
    // connection saw and left on the list. Nothing when the room it hung up in or
    // the room it came back in isn't known, and an earlier connection that moved
    // on before the map knew the room leaves it unknown for good: this
    // connection's first room is then somewhere the character walked to.
    //   LearnedPair — "died in → arrived in" when the yes rests on a death once
    //                 seen to end in that room, for the bug report.
    private (bool? Says, string? Text, string? LearnedPair) EntryRoom(HeldAtDisconnect before)
    {
        RoomKey? entered = before.EntryRoomSince is { } since ? new RoomKey(since.Map, since.Room)
            : before.MovedBeforeEntryRoomKnownSince ? null
            : _entryRoom;
        if (before.Room is not { } left || entered is not { } here) return (null, null, null);
        RoomKey hungUpIn = new(left.Map, left.Room);
        Recovery.HangupEntryRoomReading reading = _entryRoomSays?.Invoke(hungUpIn, here) ?? default;
        string text = reading.Says != true
            ? here == hungUpIn ? $"{Label(here)}, the room it left the game in" : Label(here)
            : (reading.TakenFor is { } arrival
                ? $"{Label(here)}, which the map can't tell from {Label(arrival)}, where the dead of {Label(hungUpIn)} arrive"
                : $"{Label(here)}, where the dead of {Label(hungUpIn)} arrive")
              // Paradigm: no list names this room's arrival room; it is in Albion.
              + (reading.Albion ? " (Albion: reachable from that room without a boat)" : "")
              // No list names it either: a death seen ended there, in that room or
              // in another of its land.
              + (reading.LearnedFrom is not { } seenIn ? ""
                  : seenIn == hungUpIn ? " (learned from a death seen there)"
                  : $" (learned from a death seen in {Label(seenIn)}, a room of the same land)");
        return (reading.Says, text, reading is { Says: true, LearnedFrom: { } from }
            ? $"{from} → {reading.TakenFor ?? here}" + (from == hungUpIn ? "" : $" (standing for {hungUpIn}, a room of its land)")
            : null);
    }

    private string Label(RoomKey room) => _roomLabel?.Invoke(room) ?? $"room {room}";

    // Both realms: a death takes every item but those that stay with the character,
    // and every coin. So one such item of the list, or any of its coins, still
    // held at the first inventory read says the character didn't die since the
    // list was written, or died and got its pile back where this client didn't
    // see. "Any", not "all": one torch burnt elsewhere must not hide it. Null when
    // the list has nothing a death takes, or doesn't know what was held.
    private bool? TakenStillHeld(HeldAtDisconnect before, InventorySnapshot held)
    {
        if (before.ItemsUnknown || OwnedNothing(before)) return null;
        List<HeldItem> taken = before.Items.Where(i => !StaysOnDeath(i.Name)).ToList();
        long coinsThen = before.Coins?.TotalCoinCount ?? 0;
        HashSet<string> heldNow = new(HangupItemPlan.Held(held).Select(h => h.Name), StringComparer.OrdinalIgnoreCase);
        return taken.Exists(i => heldNow.Contains(i.Name)) || (coinsThen > 0 && held.Currency.TotalCoinCount > 0);
    }

    // The list knows what was held, and none of it is taken by a death: no item but
    // those that stay with the character, and no coin. Such a character comes back
    // from a death holding what it left with, so its pack can neither bear a
    // death out nor speak against one.
    private bool OwnedNothing(HeldAtDisconnect before) =>
        !before.ItemsUnknown && before.Items.TrueForAll(i => StaysOnDeath(i.Name))
        && (before.Coins?.TotalCoinCount ?? 0) == 0;

    // A `stat` was read while the question stood open after its pass.
    private void AnswerOpenQuestion(HeldAtDisconnect before)
    {
        if (OtherCharacter(before) is { } other)
        {
            _deathBefore = null;
            LastDeathCheck = $"{_now():HH:mm:ss} not asked: {other}";
            _log?.Info(LogCategory, $"The hang-up before this connection is not judged: {other}.");
            return;
        }
        if (DeathSeenSince(before))
        {
            _deathBefore = null;
            _log?.Info(LogCategory, "A death was recorded after the list was written: the hang-up before it is not judged.");
            return;
        }
        AskDeath(before, mayAsk: false);
    }

    // The question can't be answered yet and nothing more will be sent for it: say
    // so once, with what will answer it. Not when HP at the first prompt already
    // says there was no death.
    private void SayWaiting(HeldAtDisconnect before, string why)
    {
        if (_saidWaiting) return;
        // Only when a read is what is missing (a `stat`, or the inventory): not
        // when HP at the first prompt or the room it came back in already answers,
        // and not when nothing that could be read would tell.
        //
        // What the pack says can decide whether a `stat` is worth waiting for: with
        // no count from before, only a character back stripped where its dead
        // arrive is still in question, and in an arena only one that isn't. So the
        // first inventory read is asked when there is one, and with none read yet
        // the question is whether either answer would leave it open.
        int? hpAtEntry = before.HpAtFirstEntrySince ?? _hpAtEntry;
        (bool? entryRoom, _, string? learnedPair) = EntryRoom(before);
        bool StillOpen(bool? takenStillHeld) =>
            Verdict(before, hpAtEntry, worn: null, loginLines: null, takenStillHeld, entryRoom, entryRoomText: null,
                    entryRoomLearned: learnedPair is not null).Verdict
                is Recovery.HangupDeathVerdict.NeedsLives or Recovery.HangupDeathVerdict.Died;
        bool open = _firstInventory is { } first
            ? StillOpen(TakenStillHeld(before, first))
            : StillOpen(false) || StillOpen(true);
        if (!open) return;
        _saidWaiting = true;
        _log?.Info(LogCategory,
            $"Whether the hang-up killed the character hasn't been checked: {why}. A `stat` and an inventory read on "
            + "this connection will answer it.");
        _notice?.Invoke($"[Hang-up check: {Looked(before)}. Whether it did hasn't been checked: {why}. Type `stat` and `i` and it will be]");
    }

    // Why the hang-up is being looked into, for the terminal. A realm that doesn't
    // penalise it is here for the arena alone.
    private string Looked(HeldAtDisconnect before) => DeathSuspected(before)
        ? $"you were {HpText(before)} when you left the game and this realm penalises that hang-up, which can kill"
        : $"you were {HpText(before)} in an arena room when you left the game, and a hang-up there while dropped "
          + "can kill, as the arena does (nothing lost)";

    // The record a death line would have made, from the list: where and when the
    // character was last in the game, and as its pile what it held then and
    // didn't at the first inventory read. Nothing is picked up here and nothing
    // reported as dropped; Death Recovery has the record from here on.
    //   held — the first inventory read since the hang-up; null when none was read
    //          (the list is being let go as the link drops again): the pile is
    //          then everything on the list that a death takes.
    //   cameBackIn — the room the character came back in and whose dead arrive
    //          there, when that is what the room says; the record and the notice
    //          then name it.
    private void RecordDeath(HeldAtDisconnect before, InventorySnapshot? held, int lives, string why, string evidence,
        string? cameBackIn = null)
    {
        _deathBefore = null;
        // Still holding movement: nothing has run since the connect, and the death
        // gets all a death is owed. Found later (the hold given up, a `stat` read
        // long after the pass, or the link dropping again), the character has
        // played on since: the record is made, and the engines and buff timers
        // are left as they are.
        bool atEntry = _holding && held is not null;
        (List<DeathItem>? equipped, List<DeathItem>? lost) = Recovery.HangupDeath.Pile(before, held ?? InventorySnapshot.Empty);
        if (held is null)
        {
            equipped?.RemoveAll(i => StaysOnDeath(i.Name));
            lost?.RemoveAll(i => StaysOnDeath(CountedCommand.SplitLeadingCount(i.Name.Trim()).Name));
        }
        string message = DeathMessage
            + (cameBackIn is null ? "" : $" It came back in {cameBackIn}.")
            + (before.ItemsUnknown ? " What it held then isn't known." : "");
        DeathRecord? record = _recordDeath?.Invoke(new Recovery.UnwitnessedDeath(
            before.Room, before.At, lives, message, equipped, lost, before.ItemsUnknown ? null : before.Coins, atEntry,
            GameData: before.GameData));

        (string where, string number) = Recorded(record, before);
        string pile = equipped is null || lost is null
            ? "not known (no inventory was read on the connection it left on)"
            : $"{equipped.Count} worn, {lost.Count} carried"
              + (before.Coins is { TotalCoinCount: > 0 } coins ? $", {coins.TotalCoinCount} coin(s)" : "");
        LastDeathCheck = $"{_now():HH:mm:ss} died, death{number} recorded{(atEntry ? "" : " (found late)")}: {why}. Seen: {evidence}";
        _log?.Info(LogCategory,
            $"Death{number} recorded for the hang-up at {where}: {why}. Pile: {pile}. "
            + "The item check stands down: what is missing is the pile."
            + (atEntry ? "" : " Found after the login, so the movement engines and the buff timers are left as they are."));
        _notice?.Invoke(
            $"[Hang-up check: the character died to the hang-up penalty and has {lives} {(lives == 1 ? "life" : "lives")} "
            + $"left{(cameBackIn is null ? "" : $": it came back in {cameBackIn}")}. "
            + $"This client last had it in the game at {where}, {HpText(before)}, when the link went down "
            + $"({before.At.ToLocalTime():yyyy-MM-dd HH:mm:ss}): death{number} is recorded there, and Recover Now in Death "
            + "Recovery goes for the pile. If it was played from another client since, the pile is where it died then"
            + (before.ItemsUnknown ? ". What it held then isn't known, so the record lists no pile" : "") + "]");
        // What an item pass called missing was the pile.
        LastMissing = [];
        _missing = new List<(string Name, int Count)>();
        Finish("the hang-up killed the character; the death is recorded and the pile is Death Recovery's", quiet: true);
    }

    // Where a record was made and its number, for the log and the terminal.
    private static (string Where, string Number) Recorded(DeathRecord? record, HeldAtDisconnect before) =>
        (record?.RoomName is { Length: > 0 } name ? $"{name} ({record.RoomKeyText})" : RoomText(before.Room),
         record is null ? "" : $" #{record.RecordNumber}");

    // The record a witnessed arena death gets (RoomTracker.NoteDeath's
    // tookNothing): in the history with nothing lost, so there is no pile and
    // nothing for Death Recovery to do. The pass ends with it: the character
    // stands where the arena's dead arrive, not where it left the game, and
    // nothing it held was taken.
    //   atEntry — movement is still held at the login, so nothing has run since
    //             the connect (UnwitnessedDeath.AtEntry).
    private void RecordArenaDeath(HeldAtDisconnect before, bool atEntry, int lives, string why, string evidence,
        string? cameBackIn)
    {
        _deathBefore = null;
        string message = ArenaDeathMessage + (cameBackIn is null ? "" : $" It came back in {cameBackIn}.");
        DeathRecord? record = _recordDeath?.Invoke(new Recovery.UnwitnessedDeath(
            before.Room, before.At, lives, message, Equipped: null, Lost: null, Coins: null, atEntry, NothingLost: true,
            GameData: before.GameData));

        (string where, string number) = Recorded(record, before);
        LastDeathCheck = $"{_now():HH:mm:ss} died in the arena, death{number} recorded with nothing lost"
            + $"{(atEntry ? "" : " (found late)")}: {why}. Seen: {evidence}";
        _log?.Info(LogCategory,
            $"Arena death{number} recorded for the hang-up at {where}: {why}. Nothing was lost, so there is no pile and "
            + "nothing to recover."
            + (atEntry ? "" : " Found after the login, so the movement engines and the buff timers are left as they are."));
        _notice?.Invoke(
            $"[Hang-up check: the character died in the arena after the link went down"
            + $"{(cameBackIn is null ? "" : $", and came back in {cameBackIn}")}. An arena death takes no life and no item: it "
            + $"has {lives} {(lives == 1 ? "life" : "lives")}, and there is no pile to recover. This client "
            + $"last had it in the game at {where}, {HpText(before)}, when the link went down "
            + $"({before.At.ToLocalTime():yyyy-MM-dd HH:mm:ss}): death{number} is recorded there]");
        Finish("the character died in the arena after the hang-up; nothing was lost, so nothing is looked for", quiet: true);
    }

    private static string HpText(HeldAtDisconnect before) =>
        before.Hp is <= 0 ? $"dropped (HP {before.Hp})" : $"at {Shown(before.Hp)} of {Shown(before.MaxHp)} HP";

    private static string FightText(HeldAtDisconnect before) =>
        before.PvpFight ? "in a fight with a player"
        : before.InCombat ? "in a fight with a monster"
        : "in no fight the client knew of";

    private static string Shown(int? value) =>
        value is { } known ? known.ToString(System.Globalization.CultureInfo.InvariantCulture) : "not known";

    private void CheckRoom(HeldAtDisconnect before)
    {
        if (CameInElsewhere(before) is { } elsewhere)
        {
            Finish(elsewhere);
            return;
        }
        RoomKey? here = _confirmedRoom();
        if (before.Room is null || here is null)
            _log?.Info(LogCategory,
                "The room can't be compared with the one the character left the game in "
                + $"(then {RoomText(before.Room)}, now {(here is { } k ? $"room {k}" : "room not known")}): "
                + "checking this room's floor.");
        TryPickUp();
    }

    // How the check ends when the map is sure of both rooms and they differ: what
    // was dropped lies where the character left, not here. Null otherwise.
    private string? CameInElsewhere(HeldAtDisconnect before) =>
        before.Room is { } left && _confirmedRoom() is { } here && (here.Map != left.Map || here.Room != left.Room)
            ? $"the character is at {here}, not at {left.Map}/{left.Room} where it left the game"
            : null;

    private bool SameRoomAsLeft(HeldAtDisconnect before) =>
        before.Room is { } left && _confirmedRoom() is { } here && here.Map == left.Map && here.Room == left.Room;

    // A dark room, or a blinded character, prints no room display and no floor
    // list. That is not the items being gone.
    private const string FloorUnseen = "the floor here couldn't be seen (no room display could be read: dark, or blinded)";

    private void TryPickUp()
    {
        List<(string Name, int Count)> outstanding = HangupItemPlan.Outstanding(_missing, _taken);
        // It can have been switched off while a fight was waited out.
        if (!_isAutoEnabled())
        {
            Finish("Auto-All is off");
            return;
        }
        if (!_roomShownHere && !_floorShownHere)
        {
            Finish(FloorUnseen);
            return;
        }

        IReadOnlyList<string> floor = _floorShownHere ? _floor() : Array.Empty<string>();
        List<(string Name, int Count)> lying = HangupItemPlan.Pickup(outstanding, floor, _itemKey);
        if (lying.Count == 0)
        {
            Finish("none of them is on the floor here");
            return;
        }

        // No more than the board can have dropped: its count for one hang-up, for
        // each drop of the link the list covers.
        int most = Math.Max(0, _maxItemsDropped()) * Spanned(_before);
        _plan = HangupItemPlan.Capped(lying, most);
        int beyond = lying.Sum(p => p.Count) - _plan.Sum(p => p.Count);
        if (beyond > 0)
            _log?.Info(LogCategory,
                $"On the floor here: {Names(lying)}. The realm's settings say a hang-up drops at most {most} item(s), "
                + $"so {beyond} of them are beyond what the penalty takes and are left.");
        if (_plan.Count == 0)
        {
            Finish("the realm's settings no longer say a hang-up drops items");
            return;
        }

        if (_fighting())
        {
            if (_phase != Phase.AwaitingFightEnd)
                _log?.Info(LogCategory, $"On the floor here: {Names(_plan)}. A fight is on in the room: picking up when it ends.");
            _phase = Phase.AwaitingFightEnd;
            return;
        }
        if (_hostilePresent())
        {
            Finish("a hostile monster the client isn't fighting is in the room");
            return;
        }

        _log?.Info(LogCategory, $"On the floor here: {Names(_plan)}. Picking up.");
        _phase = Phase.PickingUp;
        _ticks = GetSettleTicks;
        foreach ((string name, int count) in _plan) _collect(name, count);
    }

    // InventoryManager.ItemTaken: a `You took`, ours or typed.
    public void OnItemTaken(string name, int count)
    {
        if (_phase != Phase.PickingUp) return;
        string key = Key(name);
        int at = _plan.FindIndex(p => string.Equals(Key(p.Name), key, StringComparison.OrdinalIgnoreCase));
        if (at < 0) return;
        (string planned, int wanted) = _plan[at];
        _taken[planned] = Math.Min(wanted, _taken.GetValueOrDefault(planned) + count);
        _ticks = GetSettleTicks;
        if (_plan.TrueForAll(p => _taken.GetValueOrDefault(p.Name) >= p.Count)) Complete();
    }

    private void Complete()
    {
        List<(string Name, int Count)> got = _plan
            .Select(p => (p.Name, Count: _taken.GetValueOrDefault(p.Name)))
            .Where(p => p.Count > 0).ToList();
        if (got.Count == 0)
        {
            Finish("the game didn't confirm any of the gets");
            return;
        }

        _log?.Info(LogCategory, $"Picked up: {Names(got)}.");
        _notice?.Invoke($"[Hang-up item check: picked up {Names(got)} from the floor]");
        // Ahead of the hold being dropped, so the wear commands go out before a
        // waiting loop's next step. Which pieces go on, and whether any does, is the
        // equipment manager's to say.
        string? set = _reapplyGearSet(got.Select(g => g.Name).ToList());
        _log?.Info(LogCategory, set is not null
            ? $"Gear set '{set}' was applied again, for the pieces of it that came back."
            : "No gear set was applied again: none of what came back is in the set that is on, or the equipment "
              + "manager held the apply (its own [Equipment] line says which).");
        Finish($"picked up {Names(got)}",
            notPickedUp: "not on the floor here, or more than the realm's settings say a hang-up drops");
    }

    // ----- what moves the pass along ------------------------------------

    // TickEngine.HeartbeatElapsed.
    public void OnHeartbeat()
    {
        RereadLivesWhenDue();
        switch (_phase)
        {
            case Phase.AwaitingEntry:
                if (_holding && ++_entryTicks >= EntryWaitTicks)
                    ReleaseHold($"the game wasn't entered within {EntryWaitTicks / 60} minutes of connecting: movement is no "
                                + "longer held; the list is kept and compared if the game is entered");
                break;
            case Phase.AwaitingInventory:
                // Given up: the read is waited for without asking or holding.
                if (!_holding || --_ticks > 0) return;
                if (!_askedInventory && _isAutoEnabled())
                {
                    _askedInventory = true;
                    _ticks = InventoryWaitTicks;
                    _log?.Info(LogCategory, "No inventory read has come since entering the game: sending `i`.");
                    _wire.Send("i");
                    return;
                }
                ReleaseHold("no inventory read came after entering the game: movement is no longer held; the list is "
                            + "kept and compared when one is read");
                break;
            case Phase.AwaitingLives:
                if (--_ticks > 0 || _before is null) return;
                _log?.Info(LogCategory, "No `stat` read came back: the item check goes on without it.");
                CompareAndSave(_before);
                break;
            case Phase.AwaitingRoom:
                if (--_ticks > 0 || _before is null) return;
                _log?.Info(LogCategory, "No room display came back: going by what has been read.");
                CheckRoom(_before);
                break;
            case Phase.AwaitingFightEnd:
                OnRoomObserved();
                break;
            case Phase.PickingUp:
                if (--_ticks <= 0) Complete();
                break;
        }
    }

    // RoomDisplayParser.RoomParsed: a room display was read to its exits line.
    public void NoteRoomDisplayed()
    {
        // Only the game draws a room. The game prompt is what normally says the
        // game was entered, and a statline it can't read would leave the hold up
        // on a character that is plainly in the game.
        if (_linkUp && !_inGame)
        {
            _log?.Debug(LogCategory, "a room display was read before any game prompt: taken as the game being entered");
            EnterGame();
        }
        _roomShownHere = true;
        if (_linkUp) _roomReadThisLink = true;
        // The room the map makes of this display, once it has taken it in: the
        // first one it is sure of, with no move sent yet, is where the character
        // came back in. A room it only still holds from before the link dropped
        // doesn't count, so nothing is taken before a display has been read.
        if (_linkUp && _entryRoom is null && !_moveSentSinceEntry)
            _post(() =>
            {
                if (_entryRoom is null && !_moveSentSinceEntry) _entryRoom = _confirmedRoom();
            });
        if (_phase != Phase.AwaitingRoom) return;
        _post(() =>
        {
            if (_phase == Phase.AwaitingRoom && _before is { } before) CheckRoom(before);
        });
    }

    // GroundItemTracker.SurveyUpdated. An empty floor prints no list, so a list
    // the tracker still holds from before the link dropped says nothing about now.
    public void NoteFloorSurveyed() => _floorShownHere = true;

    // The room's occupants were read again, or combat was force-cleared.
    public void OnRoomObserved()
    {
        if (_phase == Phase.AwaitingFightEnd && !_fighting()) TryPickUp();
    }

    // The map moved the character to another room (or lost it, on a death).
    public void OnRoomChanged()
    {
        _roomShownHere = false;
        _floorShownHere = false;
        if (_phase == Phase.AwaitingFightEnd)
            Finish("the character left the room before the fight in it ended");
        else if (_phase == Phase.PickingUp)
            Complete();
    }

    // OutboundMovementObserver.MoveSent.
    public void NoteMoveSent()
    {
        if (_enteredThisLink) _moveSentSinceEntry = true;
    }

    // ----- ending -------------------------------------------------------

    // The pass ran to an answer. What it didn't find is not looked for again, so
    // the terminal says what that is and why, however the pass ended.
    //   notPickedUp — the reason for the terminal, where the outcome isn't one.
    //   quiet       — say nothing on the terminal.
    private void Finish(string outcome, string? notPickedUp = null, bool quiet = false)
    {
        LastStillMissing = HangupItemPlan.Outstanding(_missing, _taken);
        if (LastStillMissing.Count > 0)
        {
            _log?.Info(LogCategory, $"Still missing: {Names(LastStillMissing)}.");
            if (!quiet)
                _notice?.Invoke($"[Hang-up item check: {Names(LastStillMissing)} missing since you were last in the game. "
                                + $"Not picked up: {notPickedUp ?? outcome}]");
        }
        LastOutcome = $"{_now():HH:mm:ss} {outcome}";
        _log?.Info(LogCategory, $"Check ended: {outcome}.");
        End(outcome);
        // The list just compared is spent: what is held now replaces it.
        _saveProfile();
        CheckFinished?.Invoke(_deathBefore is not null);
    }

    // The pass was cut short by the link or by another character being loaded.
    // The list on disk is what the next connection reads: the one still to be
    // compared, since nothing is written before an inventory read, or the one
    // LeaveGame saved with the unfound items on it.
    private void Abandon(string why)
    {
        if (_phase == Phase.Idle) return;
        LastOutcome = $"{_now():HH:mm:ss} not finished: {why}";
        _log?.Info(LogCategory, $"Check not finished: {why}.");
        End(why);
    }

    private void End(string why)
    {
        _phase = Phase.Idle;
        _before = null;
        _missing = new List<(string Name, int Count)>();
        _plan = new List<(string Name, int Count)>();
        _taken.Clear();
        if (!_holding) return;
        _holding = false;
        _coordinator.ClearGate(MovementCoordinator.HangupItemCheckGate, AsserterName, why);
    }

    private void Hold(string reason)
    {
        if (_holding) return;
        _holding = true;
        _coordinator.AssertGate(MovementCoordinator.HangupItemCheckGate, AsserterName, reason);
    }

    // Stop holding movement and keep waiting: the list is still to be compared.
    private void ReleaseHold(string why)
    {
        if (!_holding) return;
        _holding = false;
        _log?.Info(LogCategory, $"Hold given up: {why}.");
        LastOutcome = $"{_now():HH:mm:ss} still waiting, without the hold: {why}";
        _coordinator.ClearGate(MovementCoordinator.HangupItemCheckGate, AsserterName, why);
        // From here nothing more is sent for a death in question either.
        if (_deathBefore is { } open)
            SayWaiting(open, "the check stopped holding movement before it could run");
    }

    private string Key(string name) => (_itemKey ?? ItemNameStore.Normalize)(name);

    private static string RoomText(RoomRef? room) => room is null ? "room not known" : $"room {room.Map}/{room.Room}";

    private static string Names(IEnumerable<(string Name, int Count)> items) =>
        string.Join(", ", items.Select(i => i.Count > 1 ? $"{i.Count} {i.Name}" : i.Name));
}
