using MudPlay.Services;

namespace MudPlay.Game;

// "Sysop god lives" power: the moment the client observes the character's OWN
// death, auto-recover the life just spent by sending the sysop command
// `sys god <name> add life`. Gated on the per-BBS SysopGodLives credential flag —
// the command is refused (and meaningless) without real sysop access on the board,
// so an ordinary account never sends it.
//
// The command wording is game-confirmed (user, 2026-09-04): lowercase `sys god`,
// the character's OWN name, then `add life`. One send per death — the life lost is
// a single life, so there's nothing to loop. The send rides the raw engine wire so
// it goes out even while the character is dead / dropped (SendGameCommand bypasses
// the EngineSendGate that holds wrapped engines during death).
public sealed class SysopGodLifeRecovery
{
    private readonly Func<bool> _enabled;
    private readonly Func<string?> _characterName;
    private readonly Action<string> _send;
    private readonly LogService? _log;

    public SysopGodLifeRecovery(
        Func<bool> enabled, Func<string?> characterName, Action<string> send, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(characterName);
        ArgumentNullException.ThrowIfNull(send);
        _enabled = enabled;
        _characterName = characterName;
        _send = send;
        _log = log;
    }

    // Call when the client detects the character's own death.
    public void OnDeath()
    {
        if (!_enabled()) return;
        string? name = _characterName()?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            _log?.Info("SysopGodLife",
                "Own death detected and Sysop god lives is on, but the character name isn't known yet — can't add a life.");
            return;
        }
        string command = $"sys god {name} add life";
        _log?.Info("SysopGodLife", $"Own death detected — sending '{command}' to recover the lost life.");
        _send(command);
    }
}
