using System.Collections.Generic;
using System.Text.Json;
using FujinTerm.Game.Map;

namespace FujinTerm.Services;

// In-memory index of TBInfo.json for the active game-data set. Mirrors
// RoomGraphManager's lifecycle: subscribes to GameDataCache.ActiveSetChanged,
// loads the raw JSON via GameDataCache.GetRawTable, evicts the document after
// typed conversion (memory-hygiene parity), and re-publishes StoreReloaded for
// consumers. The teleport handler parses each entry's Action directive chain
// into the keyword → destination map the walker uses.
public sealed class TBInfoStore
{
    private readonly GameDataCache _cache;
    private readonly LogService? _log;
    private readonly Dictionary<int, TBInfoEntry> _entries = new();

    // Active set the store was last loaded from, or null if empty.
    public string? ActiveSet { get; private set; }

    // Number of entries in the active store (0 when no set is active or load
    // failed).
    public int EntryCount => _entries.Count;

    // Fires after every successful (re)load, including the transition to
    // no-set-active (empty store). Subscribers should drop any cached TBInfo
    // references and re-pull what they need.
    public event Action? StoreReloaded;

    public TBInfoStore(GameDataCache cache) : this(cache, log: null) { }

    public TBInfoStore(GameDataCache cache, LogService? log)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _log = log;
    }

    // Look up a single entry by its primary key. Returns null when absent.
    public TBInfoEntry? GetEntry(int number)
        => _entries.TryGetValue(number, out TBInfoEntry? e) ? e : null;

    // Read-only snapshot of every entry in load order. Empty when no set is
    // active.
    public IEnumerable<TBInfoEntry> Entries => _entries.Values;

    // Reload the store from setName's TBInfo.json. Pass null to clear. Safe to
    // call repeatedly. Wired by AppServices to GameDataCache.ActiveSetChanged.
    public void OnActiveSetChanged(string? setName)
    {
        _entries.Clear();
        ActiveSet = setName;

        if (string.IsNullOrWhiteSpace(setName))
        {
            _log?.Log(LogSeverity.Info, "TBInfo", "No active game-data set; TBInfo cleared.");
            StoreReloaded?.Invoke();
            return;
        }

        JsonDocument? doc = _cache.GetRawTable("TBInfo");
        if (doc is null)
        {
            _log?.Log(LogSeverity.Info, "TBInfo",
                $"Active set '{setName}' has no TBInfo.json; store is empty.");
            StoreReloaded?.Invoke();
            return;
        }

        int parsed = 0;
        int skipped = 0;
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (!TryReadEntry(row, out TBInfoEntry? entry))
            {
                skipped++;
                continue;
            }
            _entries[entry.Number] = entry;
            parsed++;
        }

        _cache.EvictTable("TBInfo");

        _log?.Log(LogSeverity.Info, "TBInfo",
            $"Loaded {parsed} TBInfo entry(ies) from '{setName}'"
            + (skipped > 0 ? $" ({skipped} malformed row(s) skipped)." : "."));

        StoreReloaded?.Invoke();
    }

    private static bool TryReadEntry(JsonElement row, out TBInfoEntry entry)
    {
        entry = null!;
        if (row.ValueKind != JsonValueKind.Object) return false;

        if (!row.TryGetProperty("Number", out JsonElement nEl)
            || nEl.ValueKind != JsonValueKind.Number
            || !nEl.TryGetInt32(out int number)
            || number <= 0)
            return false;

        int linkTo = 0;
        if (row.TryGetProperty("LinkTo", out JsonElement linkEl)
            && linkEl.ValueKind == JsonValueKind.Number)
            linkEl.TryGetInt32(out linkTo);

        string? action = null;
        if (row.TryGetProperty("Action", out JsonElement actEl)
            && actEl.ValueKind == JsonValueKind.String)
        {
            action = actEl.GetString();
            // MDB encodes empty as "\0" — normalise to null so consumers
            // don't carry a sentinel value through the directive parser.
            if (!string.IsNullOrEmpty(action) && action.Length == 1 && action[0] == '\0')
                action = null;
        }

        string? calledFrom = null;
        if (row.TryGetProperty("Called From", out JsonElement cfEl)
            && cfEl.ValueKind == JsonValueKind.String)
            calledFrom = cfEl.GetString();

        entry = new TBInfoEntry
        {
            Number = number,
            LinkTo = linkTo,
            Action = action,
            CalledFrom = calledFrom,
        };
        return true;
    }
}
