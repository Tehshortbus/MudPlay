using MudPlay.Game.Map;

namespace MudPlay.ViewModels.Navigation;

// One room in the running-loop rail — the live-editable green counterpart to the
// builder's LoopBuilderRow. Carries the same per-waypoint fields the running-loop
// editor tunes in place (command / delay / do-not-rest / do-not-attack / rest-up-here /
// lap wait), plus IsCurrentRoom so the rail can highlight the room the loop is
// currently in.
// Add / remove / reorder aren't offered while running, so — unlike LoopBuilderRow —
// this row has no move/remove affordance.
public sealed record RunningLoopRow(
    int Index, RoomKey Key, string Name, string? Command, long DelayMs,
    bool DoNotRest, bool DoNotAttack, bool IsCurrentRoom, bool RestHereHp = false, bool RestHereMana = false,
    long LapWaitMs = 0, bool StartHere = false)
{
    public bool HasCommand => !string.IsNullOrWhiteSpace(Command);
    public bool HasDoNotRest => DoNotRest;
    public bool HasDoNotAttack => DoNotAttack;
    public bool HasRestHere => RestHereHp || RestHereMana;
    public string RestHereTip => RestHereHp && RestHereMana ? "Rest up here (HP and mana)"
        : RestHereHp ? "Rest up here (HP)" : "Rest up here (mana)";
    public bool HasLapWait => LapWaitMs > 0;
    public string LapWaitTip => $"Wait here until lap time {LoopDelay.Describe(LapWaitMs)}";
    public bool HasStartHere => StartHere;
}
