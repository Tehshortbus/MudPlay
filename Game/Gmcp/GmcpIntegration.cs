using System.Text.Json;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Net;
using MudPlay.Services;

namespace MudPlay.Game.Gmcp;

// Hands the server's GMCP data to the owners the text path feeds. Each owner is
// told which packages the server owns (its Gmcp... flags), so its text parser
// stands aside while a package is Active and in hand, and gets the snapshot
// through its own Apply method, so the single-writer owners still do every write.
// A package that can't be applied goes back to text. In Shadow mode the text is the
// source: the fields both sources carry are compared and differences logged under
// "GMCP shadow", and the server's data stands in only where the text hasn't answered
// (GmcpIntegration.Fallback).
public sealed partial class GmcpIntegration
{
    // Every package the client takes, in the order the settings list them.
    public static readonly string[] Packages =
    [
        "Anomaly.Char.Vitals", "Anomaly.Char.Status", "Anomaly.Char.Stats",
        "Anomaly.Room.Info", "Anomaly.Room.Occupants", "Anomaly.Room.Lair", "Anomaly.Mob",
        "Anomaly.Items.List", "Anomaly.Items.Detail", "Anomaly.Items.Usable", "Anomaly.Shop.List",
        "Anomaly.Spells.List", "Anomaly.Char.Effects", "Anomaly.Char.Abilities",
        "Anomaly.Group.Members", "Anomaly.Comm.Line", "Anomaly.Quests.Names",
    ];

    private readonly PromptParser _player;
    private readonly StatParser _stats;
    private readonly RoomGraphManager _graph;
    private readonly RoomTracker _tracker;
    private readonly RoomEntityClassifier _classifier;
    private readonly CombatStateTracker _combat;
    private readonly EncumbranceParser _encumbrance;
    private readonly LogService _log;
    private Conditions.ConditionTracker? _conditions;
    private Stealth.StealthManager? _stealth;
    private MonsterHpTracker? _hp;
    private Func<string, object, Task>? _send;

    // What has been handed to the owners, so a package lost to a reset or a mode
    // change can be taken back from them.
    private bool _roomApplied;
    private bool _hadRoomAuthority;
    private bool _occupantsApplied;
    // A new room's snapshot held back until its text display is read (see ApplyRoomOnceTextShown).
    private bool _roomHeld;
    private int _generation;

    public GmcpState State { get; } = new();

    public GmcpIntegration(PromptParser player, StatParser stats, RoomGraphManager graph, RoomTracker tracker,
        RoomEntityClassifier classifier, CombatStateTracker combat, EncumbranceParser encumbrance, LogService log,
        MessageRouter router)
    {
        _player = player;
        _stats = stats;
        _graph = graph;
        _tracker = tracker;
        _classifier = classifier;
        _combat = combat;
        _encumbrance = encumbrance;
        _log = log;

        player.GmcpOwnsVitals = () => State.Owns("Anomaly.Char.Vitals");
        player.GmcpOwnsStatus = () => State.Owns("Anomaly.Char.Status");
        stats.GmcpOwnsVitals = player.GmcpOwnsVitals;
        stats.GmcpOwnsStats = () => State.Owns("Anomaly.Char.Stats");
        combat.GmcpOwnsStatus = player.GmcpOwnsStatus;
        encumbrance.GmcpOwnsStatus = player.GmcpOwnsStatus;
        classifier.GmcpOwnsOccupants = OwnsOccupants;
        classifier.TextObserved += ObserveOccupants;
        stats.ScreenParsed += _ => NoteTextRead(TextRead.StatScreen);
        State.Diagnostic += message => log.Warn("GMCP", message);
        State.Changed += Apply;
        State.Communication += OnCommunication;
        // A room too dark to see, or a blind move, shows one line and nobody in it.
        foreach (string shownNobody in new[] { Services.Patterns.KnownPatterns.RoomPitchBlack,
                     Services.Patterns.KnownPatterns.RoomVeryDark, Services.Patterns.KnownPatterns.BlindMoveStarved })
            router.Subscribe(shownNobody, _ => ReleaseHeldRoom("its display shows nobody", shownDark: true));
    }

    public void SetSender(Func<string, object, Task> send) => _send = send;

    public void BindConsumers(Conditions.ConditionTracker conditions, Stealth.StealthManager stealth, MonsterHpTracker hp)
    {
        _conditions = conditions;
        _stealth = stealth;
        _hp = hp;
        stealth.GmcpOwnsStatus = () => State.Owns("Anomaly.Char.Status");
        // A monster carries the server's id only from an Active roster or the Shadow
        // fallback's match (ServerMonsterNamed), so a Shadow reading reaches no other.
        hp.GmcpRead = uid =>
            State.Mode("Anomaly.Mob") != GmcpMode.Off && State.LiveMonsters.TryGetValue(uid, out JsonElement live)
                ? new MonsterHpRead(live.GetProperty("maxhp").GetInt32(), live.GetProperty("hp").GetInt32())
                : null;
        hp.ServerIdFor = ServerMonsterNamed;
    }

    public void Configure(IReadOnlyDictionary<string, GmcpMode> modes)
    {
        foreach (string package in Packages) State.SetMode(package, modes.GetValueOrDefault(package, GmcpMode.Shadow));
    }

    public void Receive(GmcpMessage message)
    {
        State.Receive(message);
        if (message.Package is "Anomaly.Items.List" or "Anomaly.Shop.List" or "Anomaly.Session.Reset") RequestMissingCards();
    }

    public void Disconnected() => State.Disconnected();

    // The server's lair reading for a room: Known when it is the current room's and
    // the package is owned; EligibleAt null when the server gives no timer.
    public (bool Known, DateTimeOffset? EligibleAt) LairEligibility(RoomKey room)
    {
        if (!State.Owns("Anomaly.Room.Lair") || State.Snapshot("Anomaly.Room.Lair") is not { } snapshot
            || GmcpProjection.Key(snapshot.GetProperty("room").GetString()!) != room)
            return (false, null);
        JsonElement lair = snapshot.GetProperty("lair");
        if (lair.ValueKind != JsonValueKind.Object) return (true, null);
        return DateTimeOffset.TryParse(lair.GetProperty("eligibleAt").GetString(),
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None,
                out DateTimeOffset deadline)
            ? (true, deadline)
            : (true, null);
    }

    // Asks for every snapshot again, and for each monster of the room its static
    // information. Events (Comm.Line), per-item answers and the monster family
    // itself are not snapshots and aren't asked for.
    public async Task RefreshAsync()
    {
        if (_send is null || !State.Compatible || !State.InSession) return;
        foreach (string package in Packages.Where(p =>
                     p is not ("Anomaly.Mob" or "Anomaly.Comm.Line" or "Anomaly.Items.Detail" or "Anomaly.Items.Usable")))
            await _send("Anomaly.Request", new { package });
        if (State.Snapshot("Anomaly.Room.Occupants") is not { } occupants) return;
        foreach (JsonElement monster in occupants.GetProperty("mobs").EnumerateArray())
            await _send("Anomaly.Request", new { package = "Anomaly.Mob.Info", uid = monster.GetProperty("uid").GetInt64() });
    }

    // The roster is the server's only when every monster in it has live state to
    // read, which the Mob package supplies; an empty roster needs nothing more.
    private bool OwnsOccupants() =>
        State.Owns("Anomaly.Room.Occupants")
        && (State.Snapshot("Anomaly.Room.Occupants")!.Value.GetProperty("mobs").GetArrayLength() == 0
            || State.Mode("Anomaly.Mob") == GmcpMode.Active);

    // Runs on every change of the state: package is the one that changed, "*" for a
    // reset or a hello that touches them all, or GmcpState.RoomReset when only the
    // room's snapshots went.
    private void Apply(string package)
    {
        if (!State.Owns("Anomaly.Char.Status")) _conditions?.ApplyGmcpFlags(null);
        if (_generation != State.SessionGeneration && (_roomApplied || _hadRoomAuthority))
        {
            _tracker.ResetGmcpSession();
            _roomApplied = false;
            _hadRoomAuthority = false;
        }
        if (_generation != State.SessionGeneration || !State.Compatible || State.Mode("Anomaly.Room.Info") != GmcpMode.Active)
        {
            _generation = State.SessionGeneration;
            _graph.ClearGmcpRooms();
        }

        // A scoped room reset is the server's "entering a new room": the arriving
        // room's snapshot follows and answers the pending move. Dropping the position
        // here would read as a lost location mid-step and send the walker into an rm
        // resync on every step.
        bool roomArriving = State.Compatible && State.InSession && State.CurrentRoom is not null
            && State.Snapshot("Anomaly.Room.Info") is null;
        if (_roomApplied && !State.Owns("Anomaly.Room.Info")
            && !(roomArriving && State.Mode("Anomaly.Room.Info") == GmcpMode.Active))
        {
            _roomApplied = false;
            _tracker.InvalidateGmcpRoom();
        }
        if (_occupantsApplied && !OwnsOccupants()
            && !(roomArriving && State.Mode("Anomaly.Room.Occupants") == GmcpMode.Active))
        {
            _occupantsApplied = false;
            _classifier.NoteGameLeft();
        }

        if (package == "Anomaly.Char.Vitals") CompareVitals();

        string applying = package;
        try
        {
            ApplyMilestone2(package, ref applying);
            if (Applies(package, "Anomaly.Char.Vitals"))
            {
                applying = "Anomaly.Char.Vitals";
                ApplyVitals(State.Snapshot(applying)!.Value);
            }
            if (Applies(package, "Anomaly.Char.Stats"))
            {
                applying = "Anomaly.Char.Stats";
                _stats.ApplyGmcpStats(State.Snapshot(applying)!.Value);
            }
            if (Applies(package, "Anomaly.Char.Status"))
            {
                applying = "Anomaly.Char.Status";
                ApplyStatus(State.Snapshot(applying)!.Value);
            }
            // Occupants before the room: the room's confirmation is what lets a walk
            // take its next step, so who is in it has to be known first (a reset or
            // refresh that brings both).
            if ((package == "*" || package == "Anomaly.Room.Occupants" || package.StartsWith("Anomaly.Mob", StringComparison.Ordinal))
                && OwnsOccupants())
            {
                applying = "Anomaly.Room.Occupants";
                _occupantsApplied = true;
                IReadOnlyList<RoomEntity> roster = GmcpProjection.Entities(State, notListed: name => _party?.IsTheLeaderWeFollow(name) == true);
                // The held room's list waits for the room, which reads it.
                if (_roomHeld) _classifier.HoldGmcpUntilRoomConfirmed(roster);
                else _classifier.ApplyGmcp(roster,
                    walkedOut: package == "Anomaly.Mob.Gone" && State.LastGoneWalkedOut);
                _hp?.NotifyGmcpChanged();
            }
            else if (package.StartsWith("Anomaly.Mob", StringComparison.Ordinal)) _hp?.NotifyGmcpChanged();
            if (Applies(package, "Anomaly.Room.Info"))
            {
                applying = "Anomaly.Room.Info";
                ApplyRoomOnceTextShown(State.Snapshot(applying)!.Value);
            }
            ApplyFallback(package, ref applying);
        }
        catch (Exception ex) when (IsApplyFault(ex))
        {
            _log.Warn("GMCP", $"Cannot apply {applying}: {ex.Message}; returning affected state to text.");
            State.SetMode(applying, GmcpMode.Off);
        }
        // Apart from the rest: a fault above must not leave one session's quest names
        // standing on a reset that should drop them.
        ApplyQuestNames(package);
    }

    // What a snapshot of the wrong shape throws as it is read.
    private static bool IsApplyFault(Exception ex) =>
        ex is InvalidOperationException or FormatException or OverflowException or ArgumentException or KeyNotFoundException;

    private bool Applies(string changed, string package) =>
        (changed == "*" || changed == package) && State.Owns(package);

    private void ApplyVitals(JsonElement value)
    {
        ApplyPromptVitals(value);
        _stats.ApplyGmcpVitals(value, expGainCounts: _vitalsGeneration == State.SessionGeneration);
        _vitalsGeneration = State.SessionGeneration;
    }

    // The vitals a prompt carries.
    private void ApplyPromptVitals(JsonElement value)
    {
        int maxMana = value.GetProperty("maxmana").GetInt32();
        ManaType manaType = maxMana == 0 ? ManaType.None
            : value.GetProperty("manaType").GetString() == "kai" ? ManaType.Kai
            : ManaType.Mana;
        _player.ApplyGmcpVitals(value.GetProperty("hp").GetInt32(), value.GetProperty("maxhp").GetInt32(),
            value.GetProperty("mana").GetInt32(), maxMana, manaType);
    }

    // The session whose vitals were last applied; -1 before any.
    private int _vitalsGeneration = -1;

    private static readonly (string Field, Models.GameData.MessageFlags Flag)[] StatusFlags =
    [
        ("blind", Models.GameData.MessageFlags.Blinded),
        ("confused", Models.GameData.MessageFlags.Confused),
        ("poisoned", Models.GameData.MessageFlags.Poisoned),
        ("diseased", Models.GameData.MessageFlags.Diseased),
    ];

    private void ApplyStatus(JsonElement value)
    {
        _player.ApplyGmcpPosition(value.GetProperty("resting").GetBoolean(), value.GetProperty("meditating").GetBoolean());
        _combat.ApplyGmcpCombat(value.GetProperty("combat").GetBoolean());
        _stealth?.ApplyGmcpStealth(value.GetProperty("sneaking").GetBoolean(), value.GetProperty("hidden").GetBoolean());
        Models.GameData.MessageFlags flags = Models.GameData.MessageFlags.None;
        foreach ((string field, Models.GameData.MessageFlags flag) in StatusFlags)
            if (value.GetProperty(field).GetBoolean()) flags |= flag;
        _conditions?.ApplyGmcpFlags(flags);
        string band = value.GetProperty("encumbrance").GetProperty("band").GetString()!;
        _encumbrance.ApplyGmcp(Enum.Parse<EncumbranceLevel>(band, ignoreCase: true));
    }

    // Room.Info comes ahead of the arriving room's occupants and its text display.
    // Confirmed at once, the room let a loop walk on past a monster nobody knew was
    // there yet (report paradigm-20261029-071539). A new room is held until its text
    // display has been read, as the text path confirms it at "Obvious exits:": by
    // then its "You notice … here." (floor items, which the server's room data
    // doesn't carry) and "Also here:" are in, and the server's roster with them. A
    // dark or blind arrival's one-line display lets it through too. The snapshot
    // also comes ahead of the text that says why we moved: a follower's "--
    // Following your Party leader <dir> --" (export gmcp-20261029-070501), and a
    // death's lives line. Applied first, a drag left a phantom move queued from the
    // new room for the rest of the drag. So any move from a known room waits for its
    // text; only a snapshot with no position before it (entering the game) lands at
    // once, and so does a change to the room we're in (a door, a refresh).
    private void ApplyRoomOnceTextShown(JsonElement value)
    {
        RoomKey key = GmcpProjection.Key(value.GetProperty("room").GetString()!);
        bool newRoom = _tracker.State.CurrentRoom?.Key != key;
        if (newRoom && _tracker.State.CurrentRoom is not null)
        {
            // The map learns the room now; only our place on it waits.
            _graph.SetGmcpRoom(GmcpProjection.Room(value, _graph.GetRoom(key)));
            _roomHeld = true;
            return;
        }
        _roomHeld = false;
        ApplyRoom(value);
    }

    private bool IsTheHeldRoomsDisplay(RoomObservation observation) =>
        _roomHeld && State.Snapshot("Anomaly.Room.Info") is { } held
        && string.Equals(held.GetProperty("name").GetString()?.Trim(), observation.Name.Trim(), StringComparison.OrdinalIgnoreCase);

    // Also runs from the text lines, outside Apply, so it hands a bad snapshot back
    // to text the way Apply does.
    private void ReleaseHeldRoom(string how, bool shownDark = false)
    {
        if (!_roomHeld) return;
        _roomHeld = false;
        if (!State.Owns("Anomaly.Room.Info")) return;
        _log.Debug("GMCP", $"Arriving room confirmed: {how}.");
        try
        {
            ApplyRoom(State.Snapshot("Anomaly.Room.Info")!.Value, shownDark);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException
                                       or ArgumentException or KeyNotFoundException)
        {
            _log.Warn("GMCP", $"Cannot apply Anomaly.Room.Info: {ex.Message}; returning affected state to text.");
            State.SetMode("Anomaly.Room.Info", GmcpMode.Off);
        }
    }

    private void ApplyRoom(JsonElement value, bool shownDark = false)
    {
        RoomKey key = GmcpProjection.Key(value.GetProperty("room").GetString()!);
        Room room = GmcpProjection.Room(value, _graph.GetRoom(key));
        _graph.SetGmcpRoom(room);
        _roomApplied = true;
        _hadRoomAuthority = true;
        _tracker.ApplyGmcpRoom(room, GmcpProjection.Observation(value), shownDark);
    }

    // ----- Shadow comparisons ----------------------------------------------

    // The vitals are compared as they arrive, against the prompt before them: the
    // game prints a change (a hit, a regen tick) and its prompt first, and the
    // server's vitals follow, up to a second behind for a tick. Compared at the
    // prompt, almost every tick read as a mismatch: 267 in a 22-minute session
    // (export gmcp-20261029-152755), filling the warnings a bug report keeps. A
    // move's data is the exception: it comes ahead of the room's display and its
    // prompt, so vitals that arrive mid-move wait for the next.
    private PromptObservation? _lastPrompt;

    public void ObservePrompt(PromptObservation prompt)
    {
        _lastPrompt = prompt;
        NoteTextRead(TextRead.Prompt);
    }

    private void CompareVitals()
    {
        if (State.Mode("Anomaly.Char.Vitals") != GmcpMode.Shadow || _lastPrompt is not { } prompt
            || _tracker.HasQueuedMoves || State.Snapshot("Anomaly.Char.Vitals") is not { } value)
            return;
        Compare("Char.Vitals.hp", value.GetProperty("hp").GetInt32(), prompt.Hp);
        Compare("Char.Vitals.mana", value.GetProperty("mana").GetInt32(), prompt.Mana);
    }

    public void ObserveRoom(RoomObservation observation)
    {
        // The held room's own display: the server answers in order and sends a room's
        // data just ahead of its display, so a peek can't come between them. The peek
        // flag alone kept it held when a `look <dir>` showed nothing (no exit that way)
        // and a move followed within the flag's 3 s.
        if (!_tracker.IsPeekSuppressed() || IsTheHeldRoomsDisplay(observation))
            ReleaseHeldRoom("the text display showed who is here");
        if (_tracker.IsPeekSuppressed() || State.Mode("Anomaly.Room.Info") != GmcpMode.Shadow
            || State.Snapshot("Anomaly.Room.Info") is not { } value)
            return;
        RoomObservation expected = GmcpProjection.Observation(value);
        Compare("Room.Info.name", expected.Name, observation.Name);
        Compare("Room.Info.exits", string.Join(",", expected.Exits.Order()), string.Join(",", observation.Exits.Order()));
    }

    // Hidden occupants are compared only when the text shows them too.
    private void ObserveOccupants(RoomEntitiesObservation observation)
    {
        if (State.Mode("Anomaly.Room.Occupants") != GmcpMode.Shadow || !State.RosterComplete) return;
        static string Names(IEnumerable<RoomEntity> entities) =>
            string.Join("|", entities.Select(e => e.RawName.ToLowerInvariant()).Order(StringComparer.Ordinal));
        HashSet<string> visible = observation.Entities.Select(e => e.RawName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Compare("Room.Occupants",
            Names(GmcpProjection.Entities(State, visibleOnly: true, textVisible: visible)),
            Names(observation.Entities));
    }

    private void Compare<T>(string field, T expected, T observed)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, observed))
            _log.Warn("GMCP shadow", $"{field}: GMCP={expected}; text={observed}");
    }
}
