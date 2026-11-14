namespace MudPlay.Game.Combat;

// A monster's death read from its death text: which monster, the line, and when.
public readonly record struct MonsterDeathLine(string Monster, string Line, DateTimeOffset At);
