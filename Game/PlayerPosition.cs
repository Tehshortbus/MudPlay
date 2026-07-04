namespace FujinTerm.Game;

// Position / posture the status line reports between parens
// ([HP=… (Resting)]: etc.). Drives both UI hints (status bar shows
// "Resting") and automation gates (HealthManager pauses rest commands
// when already resting).
public enum PlayerPosition
{
    Standing,
    Resting,
    Meditating,
}
