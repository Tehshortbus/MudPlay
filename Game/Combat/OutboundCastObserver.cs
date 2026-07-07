using System.Text;

namespace FujinTerm.Game.Combat;

// Sniffs outbound user commands for a manually-typed spell cast so the combat
// engine reacts to a hand-cast the same way it reacts to its own between-round
// casts.
//
// In this realm a spell is cast by typing its cast-code directly ("swan",
// "swan rat") — no "c" verb precursor. Casting mid-fight drops combat for that
// round (*Combat Off*). The auto-attack engine already resumes promptly on that
// Off WHEN it fired the cast itself (CastingDirector.CastFired arms
// CombatManager.NoteBetweenRoundCast). A hand-typed cast never routes through
// the director, so without this observer the weapon attack idles until the next
// combat-round tick (a full round late) or a manual room re-parse. Recognising
// the typed cast-code here and arming the same signal closes that gap: a manual
// cast that breaks combat while the target is still alive re-attacks at once.
//
// Only cast-code recognition happens here. Every downstream guard
// (weapon-mode-only, live-target-present, our-cast-recency, resume pacing)
// stays in CombatManager.OnCombatStatus, so a false positive is inert — arming
// the signal outside combat, or on the engine's own cast, changes nothing.
//
// Hooked into the wire-send pipeline by MainWindowViewModel.SendUserInput,
// alongside the trainer-menu / stat / movement observers. Short payloads only —
// anything past ~64 bytes can't be a bare cast command.
public sealed class OutboundCastObserver
{
    private const int MaxBytes = 64;

    private readonly Func<string, bool> _isCastCode;
    private readonly Action _onManualCast;

    public OutboundCastObserver(Func<string, bool> isCastCode, Action onManualCast)
    {
        ArgumentNullException.ThrowIfNull(isCastCode);
        ArgumentNullException.ThrowIfNull(onManualCast);
        _isCastCode = isCastCode;
        _onManualCast = onManualCast;
    }

    public void ObserveOutbound(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaxBytes) return;
        string cmd = Encoding.Latin1.GetString(bytes)
            .TrimEnd('\r', '\n', '\0')
            .Trim();
        if (cmd.Length == 0) return;

        // First whitespace-delimited token is the cast-code; the remainder (if
        // any) is the target ("swan rat"). A bare code ("swan") self-targets.
        int space = cmd.IndexOf(' ');
        string first = space >= 0 ? cmd[..space] : cmd;
        if (_isCastCode(first)) _onManualCast();
    }
}
