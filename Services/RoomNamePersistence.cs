using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using FujinTerm.Game.Map;

namespace FujinTerm.Services;

// Writes a single learned-name update back to the active set's Rooms.json file.
// Matches the row by ("Map Number", "Room Number") and mutates its "Name" field
// in place, preserving the rest of the document.
//
// We accept the per-write file-rewrite cost (~5MB JSON on a real dataset)
// because the prompt is rare in practice — only fires for the ~140 ganghouse
// rooms a player can stumble into. Atomic via temp-file-and-rename so an
// interrupted write can't corrupt the source file.
//
// Concurrent writes are not supported — the writer assumes the prompt UI
// serialises confirmations. The cache is evicted after the write so the next
// consumer that asks for Rooms.json re-loads the updated file from disk.
public sealed class RoomNamePersistence
{
    private readonly GameDataCache _cache;
    private readonly LogService? _log;

    public RoomNamePersistence(GameDataCache cache, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _log = log;
    }

    // Persist name as the Name field for the row matching key in the active
    // set's Rooms.json. Returns true on success.
    public bool Persist(RoomKey key, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (_cache.ActiveSet is not { } setName) return false;

        string path = Path.Combine(_cache.GameDataRoot, setName, "Rooms.json");
        if (!File.Exists(path))
        {
            _log?.Log(LogSeverity.Warn, "RoomNamePersist",
                $"Rooms.json missing at '{path}'; learned name '{name}' for {key} not persisted.");
            return false;
        }

        string original = File.ReadAllText(path);
        JsonNode? root;
        try { root = System.Text.Json.Nodes.JsonNode.Parse(original); }
        catch (JsonException ex)
        {
            _log?.Log(LogSeverity.Error, "RoomNamePersist",
                $"Rooms.json parse failed at '{path}': {ex.Message}");
            return false;
        }
        if (root is not System.Text.Json.Nodes.JsonArray rows) return false;

        System.Text.Json.Nodes.JsonObject? hit = null;
        foreach (System.Text.Json.Nodes.JsonNode? node in rows)
        {
            if (node is not System.Text.Json.Nodes.JsonObject row) continue;
            if ((int?)row["Map Number"]  != key.Map)  continue;
            if ((int?)row["Room Number"] != key.Room) continue;
            hit = row;
            break;
        }
        if (hit is null)
        {
            _log?.Log(LogSeverity.Warn, "RoomNamePersist",
                $"No Rooms.json row for {key}; learned name '{name}' not persisted.");
            return false;
        }

        hit["Name"] = name;

        // Temp-file + rename for atomicity. The temp file lives in the
        // same directory so the rename is a same-filesystem move.
        string tempPath = path + ".learned.tmp";
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(tempPath, root.ToJsonString(opts));
        File.Move(tempPath, path, overwrite: true);

        // Evict the cached JsonDocument so the next read re-picks up
        // the disk content with the learned name.
        _cache.EvictTable("Rooms");

        _log?.Log(LogSeverity.Info, "RoomNamePersist",
            $"Persisted name '{name}' for {key} in '{setName}/Rooms.json'.");
        return true;
    }
}
