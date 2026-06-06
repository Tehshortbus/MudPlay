using System.Collections.Generic;
using System.Text.Json;
using FujinTerm.Game.Map;

namespace FujinTerm.Services;

/// <summary>
/// In-memory index of <c>TBInfo.json</c> for the active game-data
/// set. Mirrors <see cref="Game.Map.RoomGraphManager"/>'s lifecycle:
/// subscribes to <see cref="GameDataCache.ActiveSetChanged"/>, loads
/// the raw JSON via <see cref="GameDataCache.GetRawTable"/>, evicts
/// the document after typed conversion (memory-hygiene parity), and
/// re-publishes <see cref="StoreReloaded"/> for consumers.
/// </summary>
/// <remarks>
/// <para>
/// Commit 1 ships the load + lookup primitives. Commit 5 (teleport
/// handler) parses each entry's <see cref="TBInfoEntry.Action"/>
/// directive chain into the keyword → destination map the walker
/// uses.
/// </para>
/// </remarks>
public sealed class TBInfoStore
{
    private readonly GameDataCache _cache;
    private readonly LogService? _log;
    private readonly Dictionary<int, TBInfoEntry> _entries = new();

    /// <summary>Active set the store was last loaded from, or <c>null</c> if empty.</summary>
    public string? ActiveSet { get; private set; }

    /// <summary>Number of entries in the active store (<c>0</c> when no set is active or load failed).</summary>
    public int EntryCount => _entries.Count;

    /// <summary>
    /// Fires after every successful (re)load, including the
    /// transition to no-set-active (empty store). Subscribers should
    /// drop any cached TBInfo references and re-pull what they need.
    /// </summary>
    public event Action? StoreReloaded;

    public TBInfoStore(GameDataCache cache) : this(cache, log: null) { }

    public TBInfoStore(GameDataCache cache, LogService? log)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _log = log;
    }

    /// <summary>Look up a single entry by its primary key. Returns <c>null</c> when absent.</summary>
    public TBInfoEntry? GetEntry(int number)
        => _entries.TryGetValue(number, out TBInfoEntry? e) ? e : null;

    /// <summary>Read-only snapshot of every entry in load order. Empty when no set is active.</summary>
    public IEnumerable<TBInfoEntry> Entries => _entries.Values;

    /// <summary>
    /// Reload the store from <paramref name="setName"/>'s
    /// <c>TBInfo.json</c>. Pass <c>null</c> to clear. Safe to call
    /// repeatedly. Wired by <see cref="AppServices"/> to
    /// <see cref="GameDataCache.ActiveSetChanged"/>.
    /// </summary>
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
