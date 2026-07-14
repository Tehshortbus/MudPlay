using System.Collections.Generic;

namespace FujinTerm.Game.Map;

// One action step required to unlock a multi-action hidden exit.
// Multi-action exits encode the action data in OTHER exit fields on the
// source (or a different) room using the format:
// Action#N [on the {dir} exit of this room]: cmd1, cmd2, cmd3.
//
// StepNumber matches the #N in the source field and dictates execution
// order when the exit's modifier reads "specific order". Under "any
// order" the walker may pick any sequence; currently it runs them
// StepNumber-ascending.
//
// Commands are ALTERNATIVES — any one of them satisfies the step. The
// walker sends the first.
//
// RemoteSourceRoom identifies where the action data was found. When the
// data lived in the same room as the multi-action exit (the common case —
// "[on the X exit of this room]") the field is null. When the action data
// lived in a DIFFERENT room ("[on the X exit of room M/R]") the field
// carries that room's key — the lever governing this exit sits in that
// other room (e.g. a guardroom lever lifting the adjacent gate). The path
// expander routes a go-act-return detour to each such room to work the
// switch, then crosses the exit.
//
// RequiredItemId is the item the crosser must hold to perform this step,
// parsed from a trailing "(Item: N)" on the action cell (e.g. "hold up
// amber talisman (Item: 815)"). 0 when the step needs no item. The step's
// commands are alternatives that ALL depend on the same held item, so the
// exit is impassable without it — MovementFilter routes around a
// multi-action exit whose required item isn't in hand.
public sealed record ExitAction(
    int StepNumber,
    IReadOnlyList<string> Commands,
    RoomKey? RemoteSourceRoom,
    int RequiredItemId = 0);
