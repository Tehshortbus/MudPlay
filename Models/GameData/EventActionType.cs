namespace FujinTerm.Models.GameData;

/// <summary>
/// What a <see cref="ScheduledEvent"/> does when its trigger fires.
/// One action per event.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>WalkTo</b> — start a walk to
///   <see cref="ScheduledEvent.WalkToTarget"/> via
///   <c>AutoWalkManager</c>. Stops any running loop / auto-lair first
///   (supersede semantics — same as the <c>@goto</c> remote command).</item>
///   <item><b>Loop</b> — start the saved loop named
///   <see cref="ScheduledEvent.LoopName"/> via <c>LoopRunner.Start</c>.
///   Stops auto-lair + walker first.</item>
///   <item><b>AutoLair</b> — start the saved auto-lair setup named
///   <see cref="ScheduledEvent.AutoLairSetupName"/> via
///   <c>AutoLairManager.Start</c>. Stops loop + walker first.</item>
///   <item><b>Command</b> — send
///   <see cref="ScheduledEvent.CommandText"/> on the wire. Supports
///   multi-fire via <c>^M</c> and <c>;</c> separators; each chunk is
///   sent as its own CR-terminated line. Matches the splitter the
///   shipped <c>Macro</c> / <c>Trigger</c> / <c>Alias</c> command
///   surfaces already use.</item>
/// </list>
/// </remarks>
public enum EventActionType
{
    WalkTo = 0,
    Loop = 1,
    AutoLair = 2,
    Command = 3,
}
