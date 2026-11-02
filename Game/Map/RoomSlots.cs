namespace MudPlay.Game.Map;

// Where each room of a set of keys sits in a flat array. MajorMUD numbers the rooms
// of a map 1..N with next to no gaps (a realm's 88,000 rooms fill 88,050 numbers),
// so each map's rooms take one run of slots and a room's slot is its map's first
// slot plus its number: an index, with no key stored and nothing hashed. The tables
// by room this replaces were dictionaries, which spent several times what their
// values took on keys, hash codes and spare capacity.
//
// A room number far past the rest of its map, a map number past any real one, or a
// key that isn't a real room would stretch a run over slots nothing fills, so those
// few keys are numbered after the runs through a dictionary of their own.
//
// Immutable once made, so it is read from any thread.
internal sealed class RoomSlots
{
    // Map numbers above this are never given a run. Real maps stay in the hundreds.
    private const int MaxRunMap = 4096;

    // How far a run may stretch past the rooms it holds before the numbers beyond
    // are left to the outliers: twice the rooms, and a little more for a small map.
    private const int RunSlack = 64;

    public static readonly RoomSlots None = new([], [], null, 0);

    // By map number: the map's first slot, and how many room numbers (1..span) its
    // run covers. Span 0 is a map with no run.
    private readonly int[] _first;
    private readonly int[] _span;
    private readonly Dictionary<RoomKey, int>? _outliers;

    // Slots in all: every array laid out by these slots is this long.
    public int Count { get; }

    private RoomSlots(int[] first, int[] span, Dictionary<RoomKey, int>? outliers, int count)
    {
        _first = first;
        _span = span;
        _outliers = outliers;
        Count = count;
    }

    // Slots for these keys. A key given twice is one slot.
    public static RoomSlots For(IEnumerable<RoomKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        Dictionary<int, List<int>> numbersByMap = new();
        List<RoomKey> outliers = new();
        foreach (RoomKey key in keys)
        {
            if (key.Map <= 0 || key.Map > MaxRunMap || key.Room <= 0)
            {
                outliers.Add(key);
                continue;
            }
            if (!numbersByMap.TryGetValue(key.Map, out List<int>? numbers))
                numbersByMap[key.Map] = numbers = new List<int>();
            numbers.Add(key.Room);
        }
        if (numbersByMap.Count == 0 && outliers.Count == 0) return None;

        int maps = numbersByMap.Count == 0 ? 0 : numbersByMap.Keys.Max() + 1;
        int[] first = new int[maps];
        int[] span = new int[maps];
        int next = 0;
        foreach (int map in numbersByMap.Keys.Order())
        {
            List<int> numbers = numbersByMap[map];
            numbers.Sort();
            // The run ends at the last number no further out than twice the rooms
            // up to it: the numbers past that go to the outliers.
            int runEnd = 0, held = 0;
            for (int i = 0; i < numbers.Count; i++)
            {
                if (i > 0 && numbers[i] == numbers[i - 1]) continue;
                held++;
                if (numbers[i] <= 2 * held + RunSlack) runEnd = numbers[i];
            }
            first[map] = next;
            span[map] = runEnd;
            next += runEnd;
            foreach (int number in numbers)
                if (number > runEnd) outliers.Add(new RoomKey(map, number));
        }

        Dictionary<RoomKey, int>? outlying = null;
        foreach (RoomKey key in outliers)
        {
            outlying ??= new Dictionary<RoomKey, int>();
            if (outlying.TryAdd(key, next)) next++;
        }
        return new RoomSlots(first, span, outlying, next);
    }

    // The key's slot, or -1 when none was laid out for it. A number in a gap of its
    // map's run has a slot too; whatever is laid out by the slots holds nothing there.
    public int SlotOf(RoomKey key)
    {
        if ((uint)key.Map < (uint)_span.Length)
        {
            int offset = key.Room - 1;
            if ((uint)offset < (uint)_span[key.Map]) return _first[key.Map] + offset;
        }
        return _outliers is not null && _outliers.TryGetValue(key, out int slot) ? slot : -1;
    }
}
