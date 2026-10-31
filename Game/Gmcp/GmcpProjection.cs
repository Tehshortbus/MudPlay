using System.Text.Json;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;

namespace MudPlay.Game.Gmcp;

// Turns the server's room and occupant snapshots into the client's own types: a
// Room for the graph and tracker, the RoomObservation a room display would give,
// and the roster's RoomEntity list.
internal static class GmcpProjection
{
    // The server writes a room as "map,room".
    public static RoomKey Key(string wire)
    {
        if (!RoomKey.TryParseWire(wire.Replace(',', '/'), out RoomKey key)) throw new FormatException("Invalid room key.");
        return key;
    }

    // The room as the server shows it, over the graph's own record of it (previous):
    // what the server doesn't carry (trap damage, multi-action steps, casts on the
    // way through) is kept from the graph for an exit that still leads where it did.
    public static Room Room(JsonElement value, Room? previous)
    {
        // Command teleports have no cardinal GMCP slot; retain those graph edges.
        Dictionary<Direction, RoomExit> exits = previous?.Exits
            .Where(e => e.Key > Direction.D)
            .ToDictionary(e => e.Key, e => e.Value) ?? new();
        foreach (JsonElement edge in value.GetProperty("exits").EnumerateArray())
        {
            Direction direction = Enum.Parse<Direction>(edge.GetProperty("dir").GetString()!, ignoreCase: true);
            RoomKey target = Key(edge.GetProperty("to").GetString()!);
            RoomExit prior = previous is not null && previous.Exits.TryGetValue(direction, out RoomExit old) && old.Target == target
                ? old
                : default;
            exits.Add(direction, Exit(edge, target, prior));
        }

        RoomKey id = Key(value.GetProperty("room").GetString()!);
        Room basis = previous ?? new Room { Key = id, Name = "", Exits = exits };
        return basis with
        {
            Name = value.GetProperty("name").GetString()!,
            Light = value.GetProperty("light").GetInt32(),
            Spell = OptionalInt(value, "spell"),
            Exits = exits,
            ExitMask = exits.Keys.Where(d => d <= Direction.D).Aggregate(0u, (mask, d) => mask | (1u << (int)d)),
        };
    }

    private static RoomExit Exit(JsonElement edge, RoomKey target, RoomExit prior)
    {
        JsonElement door = edge.GetProperty("door");
        JsonElement gate = edge.GetProperty("gate");
        bool hasDoor = door.ValueKind == JsonValueKind.Object;
        bool hasGate = gate.ValueKind == JsonValueKind.Object;
        int key = hasDoor ? OptionalInt(door, "key") : 0;
        RoomExitHint hint = edge.GetProperty("kind").GetString() switch
        {
            // The server can't say a hidden exit needs several actions; the graph can.
            "hidden" => prior.Hint == RoomExitHint.MultiActionHidden ? prior.Hint : RoomExitHint.SearchableHidden,
            "text" => RoomExitHint.Text,
            "item" => RoomExitHint.Item,
            "toll" => RoomExitHint.Toll,
            "key" => RoomExitHint.KeyLocked,
            // Nor that a lever or a room command opens a door or gate (`lift portcullis`
            // at 8/909): the graph promoted it for that, and the door handler can't.
            "door" or "gate" when prior is { Hint: RoomExitHint.MultiActionHidden, MultiAction: not null } => prior.Hint,
            "door" or "gate" => key > 0 ? RoomExitHint.KeyLocked : RoomExitHint.Door,
            _ => RoomExitHint.None,
        };
        string? phrase = edge.GetProperty("phrase").GetString();
        // The server has no kind for a trap, a teleport or a ticket and calls such an
        // exit "normal" (export gmcp-20261029-143336 shows the kinds it does send). Read
        // as plain, the walker would step onto a trap undisarmed or walk a ticket gate
        // it can't pass, so the graph's reading stands, with its ticket and commands.
        if (hint == RoomExitHint.None && prior.Hint is RoomExitHint.Trap or RoomExitHint.Teleport or RoomExitHint.Ticket)
            return prior with { Target = target };
        return prior with
        {
            Target = target,
            Hint = hint,
            KeyItemId = key > 0 ? key : hasGate ? OptionalInt(gate, "item") : 0,
            CanBash = hasDoor && door.GetProperty("bashable").GetBoolean(),
            // The toll is in copper; the exit keeps it in gold. The server sends no
            // fare, so a boat's is kept from the graph.
            TollGold = OptionalInt(edge, "toll") / 100,
            TextCommands = phrase is null ? null : new[] { phrase },
            MinLevel = hasGate ? OptionalInt(gate, "minLevel") : 0,
            MaxLevel = hasGate ? OptionalInt(gate, "maxLevel") : 0,
            ClassGate = hasGate ? OptionalInt(gate, "class") : 0,
            RaceGate = hasGate ? OptionalInt(gate, "race") : 0,
        };
    }

    // What a room display would have shown: a hidden exit only once found, and never
    // an exit taken by a command (`go path`, `go manhole`), which "Obvious exits:"
    // doesn't list. Counted as shown, it read as a plain exit open to step through.
    public static RoomObservation Observation(JsonElement value)
    {
        HashSet<Direction> shown = new();
        HashSet<Direction> open = new();
        HashSet<Direction> closed = new();
        foreach (JsonElement edge in value.GetProperty("exits").EnumerateArray())
        {
            Direction direction = Enum.Parse<Direction>(edge.GetProperty("dir").GetString()!, ignoreCase: true);
            bool listed = edge.GetProperty("kind").GetString() != "text"
                && (!edge.GetProperty("hidden").GetBoolean() || edge.GetProperty("found").GetBoolean());
            if (listed) shown.Add(direction);
            JsonElement door = edge.GetProperty("door");
            if (door.ValueKind != JsonValueKind.Object) continue;
            if (door.GetProperty("state").GetString() == "open") open.Add(direction);
            else closed.Add(direction);
        }
        return new(value.GetProperty("name").GetString()!, shown, open, closed);
    }

    // The room's occupants, monsters first, as "Also here:" would list them. A
    // monster with no static information yet is left out. A hidden player stays off
    // until a search reveals them (monsters can't hide), and so does the leader we
    // follow, whom the game never lists (notListed). visibleOnly is the shadow
    // comparison with the text's roster: a hidden monster the text doesn't show is
    // dropped, and a hidden player the text shows after all is kept.
    public static IReadOnlyList<RoomEntity> Entities(GmcpState state, bool visibleOnly = false, IReadOnlySet<string>? textVisible = null,
        Func<string, bool>? notListed = null)
    {
        List<RoomEntity> result = new();
        if (state.Snapshot("Anomaly.Room.Occupants") is not { } occupants) return result;
        foreach (JsonElement mob in occupants.GetProperty("mobs").EnumerateArray())
        {
            long uid = mob.GetProperty("uid").GetInt64();
            if (!state.StaticMonsters.TryGetValue(uid, out JsonElement info)) continue;
            string display = info.GetProperty("display").GetString()!;
            if (visibleOnly && mob.GetProperty("hidden").GetBoolean() && textVisible?.Contains(display) != true) continue;
            result.Add(new(display, info.GetProperty("name").GetString()!, EntityKind.Monster, info.GetProperty("tpl").GetInt32(), uid));
        }
        foreach (JsonElement player in occupants.GetProperty("players").EnumerateArray())
        {
            string name = player.GetProperty("name").GetString()!;
            if (player.GetProperty("hidden").GetBoolean() && !(visibleOnly && textVisible?.Contains(name) == true)) continue;
            if (notListed?.Invoke(name) == true) continue;
            result.Add(new(name, name, EntityKind.Player, null));
        }
        return result;
    }

    private static int OptionalInt(JsonElement value, string name)
    {
        JsonElement field = value.GetProperty(name);
        return field.ValueKind == JsonValueKind.Null ? 0 : field.GetInt32();
    }
}
