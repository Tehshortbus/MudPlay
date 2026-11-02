namespace MudPlay.Game.Map;

// A list of values for each room, every list in one array laid out by RoomSlots:
// room k's list is the run of values from its slot's start to the next slot's. A
// dictionary of lists cost a dictionary entry, a list and an array for each room,
// several times the values themselves.
//
// Immutable once made, so it is read from any thread.
internal sealed class RoomLists<T>
{
    public static readonly RoomLists<T> Empty = new(RoomSlots.None, [0], []);

    private readonly RoomSlots _slots;
    // By slot: where its list starts in _values. One more than there are slots, so a
    // list ends where the next slot's starts.
    private readonly int[] _starts;
    private readonly T[] _values;

    private RoomLists(RoomSlots slots, int[] starts, T[] values)
    {
        _slots = slots;
        _starts = starts;
        _values = values;
    }

    // The lists as given, each kept in its order. An empty list is no list.
    public static RoomLists<T> From(IReadOnlyDictionary<RoomKey, List<T>> lists)
    {
        ArgumentNullException.ThrowIfNull(lists);
        if (lists.Count == 0) return Empty;
        RoomSlots slots = RoomSlots.For(lists.Keys);
        int[] lengths = new int[slots.Count];
        int total = 0;
        foreach ((RoomKey key, List<T> list) in lists)
        {
            lengths[slots.SlotOf(key)] = list.Count;
            total += list.Count;
        }
        int[] starts = new int[slots.Count + 1];
        for (int slot = 0; slot < slots.Count; slot++) starts[slot + 1] = starts[slot] + lengths[slot];
        T[] values = new T[total];
        foreach ((RoomKey key, List<T> list) in lists) list.CopyTo(values, starts[slots.SlotOf(key)]);
        return new RoomLists<T>(slots, starts, values);
    }

    // The room's list; empty when it has none.
    public ReadOnlySpan<T> Of(RoomKey key)
    {
        int slot = _slots.SlotOf(key);
        return slot < 0 ? default : _values.AsSpan(_starts[slot], _starts[slot + 1] - _starts[slot]);
    }

    // The same list for a caller that keeps it or reads it as a list.
    public IReadOnlyList<T> ListOf(RoomKey key)
    {
        int slot = _slots.SlotOf(key);
        if (slot < 0 || _starts[slot] == _starts[slot + 1]) return Array.Empty<T>();
        return new ArraySegment<T>(_values, _starts[slot], _starts[slot + 1] - _starts[slot]);
    }
}
