namespace MudPlay.Game.Map;

// Why a Roomba sweep left a floor item where it was, for surfacing in GhSweepReport
// / the GH Management tab so "left in place" isn't an undifferentiated bucket.
public enum GhLeftReason
{
    // No labeled room matched the item and there was no catch-all room.
    NoMatchingRoom,
    // A `get` for it failed because it was gone by sort time (decayed / taken).
    GoneBySortTime,
    // A hidden stack recon's searches found, which the sort's own search did not:
    // each search rolls for each hidden stack again. It is still where it was.
    NotFoundBySearch,
    // The game refused the `get` outright ("A strange force stops you from getting
    // this item."). It will refuse it every time, so the item stays where it lies.
    RefusedByGame,
    // The game lets a character hold only one of it at a time, and one is held
    // already that the sweep didn't pick up ("An unseen force prevents you from
    // grabbing that item!"), so no copy can be taken until that one is gone.
    OneAlreadyHeld,
    // Too heavy to ever carry within the working encumbrance budget, so no
    // delivery could free enough room to move it.
    TooHeavy,
    // Every room that could hold it — each labeled match, then the catch-all —
    // refused the drop as full. Requeueing it would just retry the same wall, so
    // it waits for the next sweep, by which time the rooms may have space.
    AllDestinationsFull,
    // A drop was refused with the currency-syntax misparse and a fresh `i` showed
    // the item isn't held at all — a pickup the ledger recorded that never landed.
    // The move is a phantom, so it's removed rather than retried.
    NotActuallyCarried,
    // Two Sorting laps in a row completed no moves: the item couldn't be sorted
    // this sweep (its destination stayed full, a hidden item never surfaced, or
    // what's left is too heavy for the budget the player's own load leaves). Left
    // where it is rather than circling the circuit forever.
    CouldNotComplete,
    // The user put the item on Roomba's ignore list.
    Ignored,
}
