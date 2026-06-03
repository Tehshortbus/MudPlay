using System.Text.Json.Serialization;

namespace FujinTerm.Game.Map;

/// <summary>
/// One unit of work in a saved navigation loop. Mirrors
/// <see cref="WalkStep"/> shape so the Phase 7.16 loop runner can
/// share the walker's execution path, but adds a
/// <see cref="CommandLoopStep.DelayMs"/> field for "wait X ms before
/// moving on" pauses the user can attach to custom commands
/// (<c>dep 100</c>, <c>ask barmaid pie</c>, etc.).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(MoveLoopStep),    typeDiscriminator: "move")]
[JsonDerivedType(typeof(CommandLoopStep), typeDiscriminator: "command")]
public abstract record LoopStep
{
    /// <summary>Display label for the Navigation right rail / loop editor list.</summary>
    [JsonIgnore]
    public abstract string Display { get; }
}

/// <summary>A planar movement step. Same execution semantics as <see cref="MoveStep"/>.</summary>
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

/// <summary>
/// A free-text in-room command (e.g. <c>dep 100</c>,
/// <c>ask barmaid pie</c>). <see cref="DelayMs"/> is the runner's
/// wait between sending the command and proceeding to the next step;
/// 0 means "advance as soon as the next prompt fires" (the same
/// PromptObserved confirmation the walker uses for CommandStep).
/// </summary>
public sealed record CommandLoopStep(string Command, int DelayMs = 0) : LoopStep
{
    public override string Display => DelayMs > 0
        ? $"{Command} (wait {DelayMs}ms)"
        : Command;
}
