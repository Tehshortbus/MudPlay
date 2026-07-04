namespace FujinTerm.Models.GameData;

// What a ScheduledEvent does when its trigger fires. One action per event.
//   WalkTo — start a walk to ScheduledEvent.WalkToTarget via
//     AutoWalkManager. Stops any running loop / auto-lair first
//     (supersede semantics — same as the @goto remote command).
//   Loop — start the saved loop named ScheduledEvent.LoopName via
//     LoopRunner.Start. Stops auto-lair + walker first.
//   AutoLair — start the saved auto-lair setup named
//     ScheduledEvent.AutoLairSetupName via AutoLairManager.Start.
//     Stops loop + walker first.
//   Command — send ScheduledEvent.CommandText on the wire. Supports
//     multi-fire via ^M and ; separators; each chunk is sent as its own
//     CR-terminated line. Matches the splitter the Macro / Trigger /
//     Alias command surfaces already use.
public enum EventActionType
{
    WalkTo = 0,
    Loop = 1,
    AutoLair = 2,
    Command = 3,
}
