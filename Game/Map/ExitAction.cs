using System.Collections.Generic;

namespace FujinTerm.Game.Map;

/// <summary>
/// One action step required to unlock a <see cref="RoomExitHint.MultiActionHidden"/>
/// exit. Multi-action exits encode the action data in OTHER exit
/// fields on the source (or a different) room using the format:
/// <c>Action#N [on the {dir} exit of this room]: cmd1, cmd2, cmd3</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StepNumber"/> matches the <c>#N</c> in the source field
/// and dictates execution order when the exit's modifier reads
/// <c>specific order</c>. Under <c>any order</c> the walker may pick
/// any sequence; v1 simply runs them StepNumber-ascending.
/// </para>
/// <para>
/// <see cref="Commands"/> are ALTERNATIVES — any one of them
/// satisfies the step. The walker sends the first; future PRs may
/// pick smarter (last-known-good, shortest, etc.).
/// </para>
/// <para>
/// <see cref="RemoteSourceRoom"/> identifies where the action data
/// was found. When the data lived in the same room as the multi-
/// action exit (the common case — "[on the X exit of this room]")
/// the field is <c>null</c>. When the action data lived in a DIFFERENT
/// room (the cross-room expander case — "[on the X exit of room
/// M/R]") the field carries that room's key. v1 only executes
/// same-room actions; remote actions fail the walk with a clear
/// reason until the cross-room expander lands.
/// </para>
/// </remarks>
public sealed record ExitAction(
    int StepNumber,
    IReadOnlyList<string> Commands,
    RoomKey? RemoteSourceRoom);
