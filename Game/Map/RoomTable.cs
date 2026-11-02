namespace MudPlay.Game.Map;

// A value for each room of a fixed set of keys, in one array laid out by RoomSlots:
// what a Dictionary keyed by room did, at the cost of the values alone.
//
// Values are put in while it is built and may be replaced after (the room graph
// swaps in a renamed room); a key the slots weren't laid out for can't be put in.
// Values come back in the order their keys were first put in. One writer at a time;
// a reader on another thread sees each value whole only where T is a reference, so a
// table of structs is filled before anything else reads it.
internal sealed class RoomTable<T>
{
    public static RoomTable<T> Empty => new(RoomSlots.None);

    private readonly RoomSlots _slots;
    private readonly T[] _values;
    private readonly ulong[] _held;

    // Keys in the order they were first put in, Count of them in use. Kept side by
    // side so a pass over the keys alone never reads the values.
    private readonly RoomKey[] _keys;

    public RoomTable(RoomSlots slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        _slots = slots;
        _values = new T[slots.Count];
        _held = new ulong[(slots.Count + 63) / 64];
        _keys = new RoomKey[slots.Count];
    }

    // Keys holding a value.
    public int Count { get; private set; }

    public IEnumerable<RoomKey> Keys
    {
        get
        {
            for (int i = 0; i < Count; i++) yield return _keys[i];
        }
    }

    public IEnumerable<T> Values
    {
        get
        {
            for (int i = 0; i < Count; i++) yield return _values[_slots.SlotOf(_keys[i])];
        }
    }

    public bool ContainsKey(RoomKey key) => Held(_slots.SlotOf(key));

    public bool TryGetValue(RoomKey key, out T value)
    {
        int slot = _slots.SlotOf(key);
        if (Held(slot))
        {
            value = _values[slot];
            return true;
        }
        value = default!;
        return false;
    }

    public T this[RoomKey key]
    {
        get => TryGetValue(key, out T value)
            ? value
            : throw new KeyNotFoundException($"The given key '{key}' was not present in the table.");
        set
        {
            if (!Set(key, value)) throw new ArgumentException($"No slot was laid out for '{key}'.", nameof(key));
        }
    }

    // Put value at key, replacing any there. False when no slot was laid out for key.
    public bool Set(RoomKey key, T value)
    {
        int slot = _slots.SlotOf(key);
        if (slot < 0) return false;
        _values[slot] = value;
        if (!Held(slot))
        {
            _held[slot >> 6] |= 1UL << slot;
            _keys[Count++] = key;
        }
        return true;
    }

    private bool Held(int slot) => slot >= 0 && (_held[slot >> 6] & (1UL << slot)) != 0;
}
