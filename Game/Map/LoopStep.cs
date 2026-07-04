using System.Text.Json.Serialization;

namespace FujinTerm.Game.Map;

// One unit of work in a saved navigation loop. Mirrors WalkStep shape so
// the loop runner can share the walker's execution path, but adds a
// DelayMs field for "wait X ms before moving on" pauses the user can
// attach to custom commands (dep 100, ask barmaid pie, etc.).
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(MoveLoopStep),    typeDiscriminator: "move")]
[JsonDerivedType(typeof(CommandLoopStep), typeDiscriminator: "command")]
public abstract record LoopStep
{
    // Display label for the Navigation right rail / loop editor list.
    [JsonIgnore]
    public abstract string Display { get; }
}

// A planar movement step. Same execution semantics as MoveStep.
public sealed record MoveLoopStep(Direction Direction) : LoopStep
{
    public override string Display => Direction switch
    {
        Map.Direction.N  => "north",
        Map.Direction.S  => "south",
        Map.Direction.E  => "east",
        Map.Direction.W  => "west",
        Map.Direction.NE => "northeast",
        Map.Direction.NW => "northwest",
        Map.Direction.SE => "southeast",
        Map.Direction.SW => "southwest",
        Map.Direction.U  => "up",
        Map.Direction.D  => "down",
        _ => "?",
    };
}

// A free-text in-room command (e.g. dep 100, ask barmaid pie). DelayMs is
// the runner's wait between sending the command and proceeding to the next
// step; 0 means "advance as soon as the next prompt fires" (the same
// PromptObserved confirmation the walker uses for CommandStep).
public sealed record CommandLoopStep(string Command, int DelayMs = 0) : LoopStep
{
    public override string Display => DelayMs > 0
        ? $"{Command} (wait {DelayMs}ms)"
        : Command;
}
