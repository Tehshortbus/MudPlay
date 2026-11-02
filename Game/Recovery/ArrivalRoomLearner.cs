using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Recovery;

// Learns where the dead of a room arrive from the deaths the client sees. Each
// room names the room its dead go to, and the imported game data doesn't carry
// that (GAME_MECHANICS "Death threshold & consequences"): for Paradigm's own
// rooms, and for a board whose rooms were edited, watching a death is the only
// way to find out. What is learned is kept with the game-data set
// (LearnedArrivalRoomStore) and read by the hang-up death check
// (HangupDeathArrivalRooms), for the rooms no list covers.
//
// A reading is made only when both ends are certain:
//   - the death was seen (its lines read), in a room the map was sure of;
//   - it was not an arena death. That one is the arena's own rule, and where it
//     puts the character says nothing of a death that costs a life;
//   - the room the character arrived in is one a room display showed after the
//     death, while the map was still waiting for the death to place it, with
//     nothing sent in between that could have moved the character, and the map
//     is sure of it.
// A graveyard too dark to show, a display the map can't place, a move or a room
// command sent before the room was drawn, a drag the map followed: nothing is
// learned.
//
// The death lines print no room themselves, so the first display afterwards is
// whatever was asked for, and one asked for while the death was still being
// carried out can be of the room died in (PlayerDeathMovementHalt). That display
// is not the arrival: it teaches nothing, and the next one is waited for, for a
// short while. A room that really is its own graveyard is never learned this way.
// On Stock the list has those rooms; elsewhere they stay unlearned, which costs
// nothing, since standing in the room it left says nothing of a room like that.
//
// A death that ends as the room's land already has it (LearnedArrivalLookup), in
// a room with no reading of its own, makes no entry for the room: it is counted
// on the reading it bears out. Only a room's first word and its exceptions get an
// entry, so the file stays as small as what it says.
//
// Where the Stock list is the world's own (an unmodified Stock set, a zone
// Paradigm shares with Stock) nothing is learned, and a death that ends where the
// list says is no news. One that ends somewhere the list doesn't allow is:
//   - on an unmodified Stock set, a sign that the BOARD's rooms were edited while
//     it still runs on the standard data. It is counted on the board
//     (boardContradicted, StandardListContradictions), and nothing is learned
//     from it until the board is marked: from then on it is treated as any board
//     with edited rooms, where the list says yes and never no, and what is
//     learned is read;
//   - on Paradigm, said in the log and left at that.
public sealed class ArrivalRoomLearner
{
    // How long after a death a room display is still taken for the room it left
    // the character in. The graveyard's own display has been seen to take 37
    // seconds to come (PlayerDeathMovementHalt asks for it after 2.5), so a minute
    // covers a slow one. It is a backstop, not the test: whatever could move the
    // character meanwhile ends the wait at once.
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private readonly RoomTracker _tracker;
    private readonly LearnedArrivalRoomStore _store;
    private readonly LearnedArrivalLookup _lookup;
    private readonly Func<HangupArrivalListTrust> _trust;
    private readonly Func<RoomKey, RoomKey, bool>? _couldBeTakenFor;
    private readonly Func<RoomKey, bool>? _inAlbion;
    private readonly Func<string, bool>? _isExitCommandSomewhere;
    private readonly Action<RoomKey, RoomKey, IReadOnlyList<RoomKey>>? _boardContradicted;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;

    // The room of a death just seen, and when, until the room it arrived in is
    // drawn.
    private RoomKey? _diedIn;
    private DateTimeOffset _diedAt;

    //   trust           — how far the Stock list goes for the active game data.
    //   couldBeTakenFor — RoomGraphManager.CouldBeTakenFor: the map can confirm a
    //                     listed arrival room as a room that looks the same.
    //   inAlbion        — the room is in Paradigm's Albion, whose dead are known
    //                     to arrive in the Burial Vault.
    //   isExitCommandSomewhere — RoomGraphManager.IsExitCommandSomewhere: the line
    //                     is a text exit or a room command of some room.
    //   boardContradicted — StandardListContradictions.Note: a death on an
    //                     unmodified Stock set (room died in, room arrived in)
    //                     ended where the list (the rooms it allows) doesn't.
    //                     It says so itself, and the trust read after it is the
    //                     board's new one.
    public ArrivalRoomLearner(
        RoomTracker tracker,
        LearnedArrivalRoomStore store,
        LearnedArrivalLookup lookup,
        Func<HangupArrivalListTrust> trust,
        Func<RoomKey, RoomKey, bool>? couldBeTakenFor = null,
        Func<RoomKey, bool>? inAlbion = null,
        Func<string, bool>? isExitCommandSomewhere = null,
        Action<RoomKey, RoomKey, IReadOnlyList<RoomKey>>? boardContradicted = null,
        Func<DateTimeOffset>? now = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(trust);
        _tracker = tracker;
        _store = store;
        _lookup = lookup;
        _trust = trust;
        _couldBeTakenFor = couldBeTakenFor;
        _inAlbion = inAlbion;
        _isExitCommandSomewhere = isExitCommandSomewhere;
        _boardContradicted = boardContradicted;
        _now = now ?? (static () => DateTimeOffset.UtcNow);
        _log = log;
    }

    // RoomTracker.PlayerDeathObserved.
    public void OnPlayerDied()
    {
        _diedIn = null;
        if (_tracker.LastDeathTookNothing || !_tracker.LastDeathRoomConfirmed) return;
        _diedIn = _tracker.LastDeathRoom;
        _diedAt = _now();
    }

    // OutboundMovementObserver.MoveSent: the room the next display shows is no
    // longer the one the death left the character in.
    public void NoteMoveSent() => Forget();

    // Every line going out, as the other outbound observers see it. A room
    // command (`touch altar`) moves the character and is no direction or `go`,
    // so the movement observer doesn't call it a move. With no room known after
    // a death, any line that is an exit command of SOME room is taken for one.
    public void ObserveOutbound(ReadOnlySpan<byte> bytes)
    {
        if (_diedIn is null || _isExitCommandSomewhere is null || bytes.IsEmpty || bytes.Length > 64) return;
        string line = Encoding.Latin1.GetString(bytes).TrimEnd('\r', '\n', '\0').Trim();
        if (line.Length > 0 && _isExitCommandSomewhere(line)) Forget();
    }

    // The game was left, or the link dropped, before the room was drawn: on the
    // way back in the character may have been played from somewhere else.
    public void Forget() => _diedIn = null;

    // RoomTracker.StateChanged, for the transitions a room display decided.
    public void OnRoomStateChanged(RoomTransition transition)
    {
        if (_diedIn is not { } diedIn || !transition.Displayed) return;
        if (_now() - _diedAt > Window)
        {
            Forget();
            return;
        }
        // Only a display the map was still waiting on the death for: nothing placed
        // since the death (PendingRespawn), or the room died in drawn once more.
        // One that settles a move the map knew of (a drag by the party leader, the
        // character's own step) comes from Pending and shows where that move led.
        // A summon in that moment can't be told from the death's own relocation:
        // the map hears of neither, and the room it shows is learned. A known limit.
        bool awaited = transition.PreviousConfidence == RoomConfidence.PendingRespawn
            || (transition.PreviousConfidence == RoomConfidence.Confirmed && transition.PreviousRoom?.Key == diedIn);
        if (!awaited)
        {
            Forget();
            return;
        }
        // A display of the room died in: asked for before the death had moved the
        // character. The room it arrived in is still to come.
        if (transition.NewConfidence == RoomConfidence.Confirmed && transition.NewRoom?.Key == diedIn) return;

        _diedIn = null;
        // A room the map can't tell from a look-alike it doesn't place; the
        // look-alike stands in, as Learn takes look-alikes for one another.
        RoomKey? arrivedIn = transition.NewConfidence == RoomConfidence.Confirmed && transition.NewRoom is { } placed
            ? placed.Key : _tracker.DisplayLookAlike;
        if (arrivedIn is not { } arrived)
        {
            _log?.Debug(LearnedArrivalRoomStore.LogCategory,
                $"nothing learned from the death in {diedIn}: the map couldn't place the room it arrived in");
            return;
        }
        Learn(diedIn, arrived);
    }

    private void Learn(RoomKey diedIn, RoomKey arrivedIn)
    {
        bool Fits(IEnumerable<RoomKey> known) =>
            known.Any(room => room == arrivedIn || _couldBeTakenFor?.Invoke(room, arrivedIn) == true);

        HangupArrivalListTrust trust = _trust();
        // What a death there was expected to show, and on whose word.
        IReadOnlyList<RoomKey>? expected = HangupDeathArrivalRooms.DeathArrivalsOn(diedIn, trust);
        string onWhoseWord = "the Stock list";
        // The board's own count has said what there is to say of this death.
        bool said = false;
        if (expected is not null)
        {
            if (Fits(expected)) return;
            if (trust == HangupArrivalListTrust.Paradigm)
            {
                _log?.Warn(LearnedArrivalRoomStore.LogCategory,
                    $"A death seen in {diedIn} ended in {arrivedIn}, where the Stock list has its dead arrive in "
                    + $"{string.Join(" or ", expected)}. Not learned: the list stands for the zones Paradigm shares with "
                    + "Stock. The list or the map is wrong about one of these rooms.");
                return;
            }
            if (trust == HangupArrivalListTrust.VanillaStock)
            {
                _boardContradicted?.Invoke(diedIn, arrivedIn, expected);
                // Still the world's own list (the board isn't marked yet, or
                // nobody is keeping count of boards): nothing is learned.
                if (_trust() == HangupArrivalListTrust.VanillaStock) return;
                said = true;
            }
        }
        bool albion = expected is null && trust == HangupArrivalListTrust.Paradigm && _inAlbion?.Invoke(diedIn) == true;
        if (albion)
        {
            // Albion's dead are known to arrive in the Burial Vault: seeing it adds nothing.
            expected = new[] { HangupDeathArrivalRooms.ParadigmAlbionVault };
            onWhoseWord = "Albion";
            if (Fits(expected)) return;
        }
        // What the deaths seen in its land say. Not asked in Albion, where a land's
        // reading is not set against the vault and only the room's own is.
        else if (_lookup.LandSays(diedIn).Arrivals is { } land)
        {
            // As its land already has it, and the room has no reading of its own:
            // the death is counted on the reading it bears out, and no entry is
            // made for the room. The land goes on speaking for it, and the file
            // doesn't come to hold every room anyone died in.
            if (!_store.HasReadingOf(diedIn))
                foreach (LearnedArrival reading in land)
                    if (Fits(new[] { reading.Arrival }) && _store.LearnNearby(reading.SeenIn, diedIn, reading.Arrival))
                        return;
            if (expected is null)
            {
                expected = land.Select(l => l.Arrival).Distinct().ToList();
                onWhoseWord = $"a death seen in {land[0].SeenIn}, a room of the same land,";
            }
        }

        LearnedArrivalOutcome outcome = _store.Learn(diedIn, arrivedIn);
        // The room's own reading is kept and wins for that room; the land keeps
        // its reading for its other rooms.
        if (outcome == LearnedArrivalOutcome.Learned && !said && expected is not null && !Fits(expected))
            _log?.Warn(LearnedArrivalRoomStore.LogCategory,
                $"A death seen in {diedIn} ended in {arrivedIn}, not in {string.Join(" or ", expected)} as {onWhoseWord} had "
                + "it. Recorded as that room's own: it is what is used for that room from now on.");
    }
}
