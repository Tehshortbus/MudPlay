namespace FujinTerm.Game;

/// <summary>
/// Position / posture the status line reports between parens
/// (<c>[HP=… (Resting)]:</c> etc.). Drives both UI hints (status bar
/// shows "Resting") and automation gates (Phase 13 HealthManager pauses
/// rest commands when already resting).
/// </summary>
public enum PlayerPosition
{
    Standing,
    Resting,
    Meditating,
}
