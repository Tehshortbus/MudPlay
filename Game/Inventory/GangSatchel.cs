using System.Text.RegularExpressions;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Inventory;

// Anomaly's gang satchel (GAME_MECHANICS "Gang satchel"): every gang-house item
// the character holds rides inside it, and `i` lists only the satchel itself (plus
// the worn emblem, as worn gear). A house key in it opens its door like one on the
// ring, so the client reads the satchel by looking at it, and every "do I hold
// this" question asks the satchel too, through InventorySnapshot.Satchel.
//
// The look goes out once when an `i` shows the satchel and its contents are
// unknown, and again when an item picked up since the last look is missing from
// the next `i` (it went into the satchel). One unanswered look is all a session
// sends, so a board that answers it differently costs one command, not one per
// `i`. Lines are read on the UI thread.
public sealed partial class GangSatchel
{
    public const string ItemName = "gang satchel";
    public const string LookCommand = "l gang satchel";
    private const string Header = "Your gang satchel holds:";
    private const string WornMark = "(worn)";
    // A look that never ends in a prompt (the link dropped mid-look) is closed here.
    private const int MaxRows = 40;

    public readonly record struct House(string Name, IReadOnlyList<string> Items);

    private readonly LogService? _log;
    private readonly Func<bool> _held;
    private readonly Action<string> _send;
    private LineExtractor? _lines;

    private IReadOnlyList<House> _houses = Array.Empty<House>();
    private IReadOnlyList<string> _unworn = Array.Empty<string>();
    private bool _carried;
    private bool _read;
    private bool _owed;
    private bool _unanswered;
    private readonly HashSet<string> _takenSinceRead = new(StringComparer.OrdinalIgnoreCase);

    private bool _capturing;
    private readonly List<string> _rows = new();

    // held: nothing may be sent now (master switch off, not in the game, send gate
    // held); the look stays owed until Retry.
    public GangSatchel(Func<bool> held, Action<string> send, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(send);
        _held = held;
        _send = send;
        _log = log;
    }

    // The satchel's contents changed, or were let go.
    public event Action? Changed;

    // True once the last `i` listed the satchel.
    public bool IsCarried => _carried;

    // True once a look at the satchel has been read.
    public bool IsRead => _read;

    // True while a look is owed and hasn't gone out.
    public bool LookOwed => _owed;

    // Each house as the look listed it, its items as printed.
    public IReadOnlyList<House> Houses => _houses;

    // What the satchel holds besides worn gear, one entry per item, a pile behind
    // its count ("2 silver key") as the key ring lists it. A worn emblem is left
    // out: `i` already lists it with the worn gear.
    public IReadOnlyList<string> Unworn => _unworn;

    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (ReferenceEquals(_lines, lines)) return;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    // ----- what `i` and the item lines tell it --------------------------

    // Reads the inventory's `i` and item lines, and puts the contents into its
    // snapshot.
    public void Follow(InventoryManager inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        inventory.SatchelContents = () => _read ? _unworn : null;
        Changed += inventory.NoteSatchelChanged;
        inventory.FullInventoryParsed += () => OnInventoryRead(inventory.Snapshot);
        inventory.ItemTaken += (name, _) => NoteTaken(name);
        inventory.ItemDropped += NoteLeft;
        inventory.ItemSold += (name, count, _) => NoteLeft(name, count);
        inventory.ItemGivenAway += (name, count, _) => NoteLeft(name, count);
        inventory.ItemHidden += name => NoteLeft(name, 1);
    }

    // A full `i` was read.
    private void OnInventoryRead(InventorySnapshot snapshot)
    {
        if (snapshot.PackCount(ItemName) == 0)
        {
            _takenSinceRead.Clear();
            if (!_carried && !_read) return;
            _carried = false;
            _read = false;
            _owed = false;
            SetContents(Array.Empty<House>());
            _log?.Info(InventoryManager.LogCategory, "The inventory lists no gang satchel: its contents are let go.");
            return;
        }

        _carried = true;
        string? reason = !_read ? "its contents aren't known"
            : _takenSinceRead.FirstOrDefault(name => !Lists(snapshot, name)) is { } gone
                ? $"'{gone}' was picked up and the inventory doesn't list it"
                : null;
        _takenSinceRead.Clear();
        if (reason is not null) Ask(reason);
    }

    // An item was picked up: a gang item goes straight into the satchel.
    private void NoteTaken(string name)
    {
        if (_carried && !string.IsNullOrWhiteSpace(name)) _takenSinceRead.Add(name.Trim());
    }

    // An item left the character (dropped, given, sold, hidden). Satchel items are
    // handled like any other, so one of them may be what went.
    private void NoteLeft(string name, int count)
    {
        if (!_read || string.IsNullOrWhiteSpace(name) || count <= 0) return;
        string wanted = name.Trim();
        bool changed = false;
        var houses = new List<House>(_houses.Count);
        foreach (House house in _houses)
        {
            var items = new List<string>(house.Items.Count);
            foreach (string printed in house.Items)
            {
                (int copies, string itemName, bool worn) = Parse(printed);
                if (count > 0 && !worn && string.Equals(itemName, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    int taken = Math.Min(copies, count);
                    count -= taken;
                    copies -= taken;
                    changed = true;
                    if (copies == 0) continue;
                    items.Add(copies == 1 ? itemName : $"{copies} {itemName}");
                    continue;
                }
                items.Add(printed);
            }
            houses.Add(house with { Items = items });
        }
        if (!changed) return;
        _log?.Debug(InventoryManager.LogCategory, $"'{wanted}' left the gang satchel.");
        SetContents(houses);
    }

    // The master switch came back on, the game was entered, or the send gate let go.
    public void Retry()
    {
        if (_owed) Ask("it was owed");
    }

    // Another character, or the same one loaded again.
    public void Reset()
    {
        _carried = _read = _owed = _unanswered = false;
        _capturing = false;
        _rows.Clear();
        _takenSinceRead.Clear();
        SetContents(Array.Empty<House>());
    }

    private void Ask(string reason)
    {
        if (_unanswered && !_owed) return;
        if (_held())
        {
            _owed = true;
            return;
        }
        _owed = false;
        _unanswered = true;
        _log?.Info(InventoryManager.LogCategory, $"Looking in the gang satchel: {reason}.");
        _send(LookCommand);
    }

    private static bool Lists(InventorySnapshot snapshot, string name) =>
        snapshot.IsCarriedOrWorn(name)
        || (snapshot.Keys?.Any(k => string.Equals(InventorySnapshot.ParseKeyEntry(k).Name, name,
            StringComparison.OrdinalIgnoreCase)) ?? false);

    // ----- the look -----------------------------------------------------

    //   Your gang satchel holds:
    //   Arlysia Silver House: silver emblem, silver key, silver keyring
    //   Fellgate Sapphire Tent: blue key, blue keyring, sapphire emblem (worn)
    // The game wraps a long row; its continuation carries no house name and starts
    // with an item's lower-case name. The prompt, or any other line, ends the list.
    private void OnLine(LineExtractor.EmittedLine line)
    {
        string text = line.Text.Trim();
        if (!_capturing)
        {
            if (!line.IsPromptLine && text == Header)
            {
                _capturing = true;
                _rows.Clear();
            }
            return;
        }

        if (!line.IsPromptLine && _rows.Count < MaxRows)
        {
            if (HouseRowRegex().IsMatch(text))
            {
                _rows.Add(text);
                return;
            }
            if (_rows.Count > 0 && IsContinuation(text))
            {
                _rows[^1] += " " + text;
                return;
            }
        }
        Finish();
    }

    private static bool IsContinuation(string text) =>
        text.Length > 0 && (char.IsLower(text[0]) || char.IsDigit(text[0])) && text[^1] is not ('.' or '!' or '?');

    private void Finish()
    {
        _capturing = false;
        var houses = new List<House>(_rows.Count);
        foreach (string row in _rows)
        {
            Match m = HouseRowRegex().Match(row);
            string[] items = m.Groups["items"].Value.Split(", ",
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            houses.Add(new House(m.Groups["house"].Value.Trim(), items));
        }
        _rows.Clear();
        _carried = true;
        _read = true;
        _owed = false;
        _unanswered = false;
        _takenSinceRead.Clear();
        _log?.Info(InventoryManager.LogCategory,
            $"Read the gang satchel: {houses.Count} house(s), {houses.Sum(h => h.Items.Count)} item(s).");
        SetContents(houses);
    }

    private void SetContents(IReadOnlyList<House> houses)
    {
        _houses = houses;
        var unworn = new List<string>();
        foreach (House house in houses)
            foreach (string printed in house.Items)
                if (!Parse(printed).Worn) unworn.Add(printed);
        _unworn = unworn;
        Changed?.Invoke();
    }

    // "2 silver key" → (2, "silver key", false); "sapphire emblem (worn)" → (1,
    // "sapphire emblem", true).
    private static (int Count, string Name, bool Worn) Parse(string printed)
    {
        string text = printed.Trim();
        bool worn = text.EndsWith(WornMark, StringComparison.OrdinalIgnoreCase);
        if (worn) text = text[..^WornMark.Length].TrimEnd();
        (int count, string name) = InventorySnapshot.ParseKeyEntry(text);
        return (count, name, worn);
    }

    [GeneratedRegex(@"^(?<house>[A-Z][^:,]*?): (?<items>\S.*)$")]
    private static partial Regex HouseRowRegex();
}
