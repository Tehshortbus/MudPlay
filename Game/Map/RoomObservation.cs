using System.Collections.Generic;

namespace FujinTerm.Game.Map;

/// <summary>
/// One observed room display — the inputs <see cref="RoomTracker"/>
/// needs to decide a state transition. <see cref="Name"/> is the
/// room title line; <see cref="Exits"/> is the parsed set of
/// directions from the <c>Obvious exits:</c> line. The parser that
/// surfaces these values lands in PR 7.1b; PR 7.1 takes them as a
/// pre-parsed input so the FSM stays testable in isolation.
/// </summary>
public readonly record struct RoomObservation(string Name, IReadOnlySet<Direction> Exits);
