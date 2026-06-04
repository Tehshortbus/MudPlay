using System.Collections.Generic;

namespace FujinTerm.Game.Map;

/// <summary>
/// One observed room display — the inputs <see cref="RoomTracker"/>
/// needs to decide a state transition. <see cref="Name"/> is the
/// room title line; <see cref="Exits"/> is the parsed set of
/// directions from the <c>Obvious exits:</c> line.
/// <see cref="OpenDoorDirections"/> lists directions whose modifier
/// was <c>"open door"</c> (the door is already open — walker can
/// skip the bash/pick FSM). The parser that surfaces these values
/// lands in PR 7.1b; PR 7.1 takes them as pre-parsed inputs so the
/// FSM stays testable in isolation.
/// </summary>
public readonly record struct RoomObservation(
    string Name,
    IReadOnlySet<Direction> Exits,
    IReadOnlySet<Direction>? OpenDoorDirections = null);
