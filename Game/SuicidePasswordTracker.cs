using System.Text;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game;

// Passive observer for the in-game set suicide / suicide password flows.
// Drives an EngineSendGate to pause every engine while the user is in a
// password-entry prompt (so a stray par poll doesn't end up becoming the
// password), and captures the password the user types so we can store it
// encrypted on the CharacterProfile for the @suicide consumer to use.
//
// The user types the flow themselves; we don't run a wizard or send commands
// on their behalf. We just watch the server prompts, lock the gate, watch the
// user's outbound for the bytes that follow, and commit on the
// Password Changed success line.
//
// State machine (all transitions clear the gate + state if a terminator fires):
//   Idle — gate clear, no pending capture.
//   AwaitingOldPassword — server printed "Enter the current password:"; the
//     next outbound line is the old password (we don't store it, just pass
//     through). On "Invalid password specified." the flow aborts (back to
//     Idle without touching stored). Otherwise "Enter New Password:" follows
//     and we transition to AwaitingNewPassword.
//   AwaitingNewPassword — server printed "Enter New Password:"; the next
//     outbound line is the new password. We tentatively capture it, waiting
//     for Password Changed to commit or Password NOT changed to discard.
//   AwaitingUsePassword — user typed suicide and server printed "Enter your
//     suicide password:". We just gate the engine here so auto-sends don't end
//     up sent as the password attempt; no capture, no profile change.
//
// The pro-command response line "You do not have a suicide password set." is
// treated as authoritative — we wipe the stored password regardless of state,
// since the realm's view differs from our cached one. The reroll line "After
// a LONG thought, you take your own life" (successful suicide) wipes it too —
// the old character is gone.
public sealed class SuicidePasswordTracker : IDisposable
{
    public enum FlowState
    {
        Idle,
        AwaitingOldPassword,
        AwaitingNewPassword,
        AwaitingUsePassword,
    }

    private readonly EngineSendGate _gate;
    private readonly ProfileService _profile;
    private readonly PasswordProtector _protector;
    private readonly LogService? _log;
    private readonly List<IDisposable> _subs = new();
    private bool _disposed;

    private FlowState _state = FlowState.Idle;

    // ----- Outbound-line sniffer (capture model) -------------------------
    // The state-machine prompts are NOT reliable timing for password
    // capture — the server's prompt-ack burst arrives AFTER the user
    // has finished typing both old AND new passwords (Wire Inspector
    // verified: "Enter the current password:****<CR>...Enter New
    // Password:****<CR>...Password changed" all in one chunk). By the
    // time OnNewPasswordPrompt flips state to AwaitingNewPassword, the
    // user's bytes have already passed through ObserveOutbound with
    // state stuck on Idle / AwaitingOldPassword — and the state gate
    // early-returned every one of them.
    //
    // Resolved by sniffing OUTBOUND bytes continuously: when the user
    // sends `set suicide`, arm capture; track the latest completed
    // non-empty outbound line until "Password changed" lands; that
    // latest line IS the new password (the old-password line gets
    // shifted out by the sniffer's rolling-1-deep model). Disarm on
    // any flow terminator.
    private bool _captureArmed;
    // Char accumulator for the line currently being typed.
    private readonly StringBuilder _currentOutLine = new();
    // Latest completed non-empty outbound line while armed — the new-password
    // candidate at commit time.
    private string? _latestArmedLine;

    // Current state of the flow — exposed for tests + diagnostics.
    public FlowState State => _state;

    public SuicidePasswordTracker(
        MessageRouter router,
        EngineSendGate gate,
        ProfileService profile,
        PasswordProtector protector,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(protector);
        _gate      = gate;
        _profile   = profile;
        _protector = protector;
        _log       = log;

        _subs.Add(router.Subscribe(KnownPatterns.SuicidePromptOldPassword,  _ => OnOldPasswordPrompt()));
        _subs.Add(router.Subscribe(KnownPatterns.SuicidePromptNewPassword,  _ => OnNewPasswordPrompt()));
        _subs.Add(router.Subscribe(KnownPatterns.SuicidePromptUseSuicide,   _ => OnUsePasswordPrompt()));
        _subs.Add(router.Subscribe(KnownPatterns.SuicideInvalidPassword,    _ => OnInvalidPassword()));
        _subs.Add(router.Subscribe(KnownPatterns.SuicidePasswordChanged,    _ => OnPasswordChanged()));
        _subs.Add(router.Subscribe(KnownPatterns.SuicidePasswordNotChanged, _ => OnPasswordNotChanged()));
        _subs.Add(router.Subscribe(KnownPatterns.SuicideNotSet,             _ => OnNotSet()));
        _subs.Add(router.Subscribe(KnownPatterns.Reroll,                    _ => OnReroll()));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (IDisposable s in _subs) s.Dispose();
        _subs.Clear();
    }

    // Called by the wire-send path on every outbound byte so the sniffer can
    // track the user-typed lines that need to land as the captured suicide
    // password. Runs ALWAYS (not gated on the state machine) because the
    // inbound prompt acks can arrive in a single burst AFTER the user has
    // finished typing — gating on state would silently miss every byte. The
    // arming flag (_captureArmed) decides whether the completed line is the
    // new-password candidate or unrelated traffic.
    //
    // Handles both wire-send modes transparently:
    //   Char-mode (the real MajorMUD path — server uses Telnet ECHO
    //     suppression so each keystroke ships individually).
    //   Line-mode (LocalInputBuffer flushes the entire line + CR in one call).
    // Backspace bytes (0x08 / 0x7F) shrink the in-flight line so a typo +
    // correct produces the right captured value.
    public void ObserveOutbound(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;

        foreach (byte b in bytes)
        {
            switch (b)
            {
                case 0x0D: // CR
                case 0x0A: // LF
                    FinalizeOutboundLine();
                    break;
                case 0x08: // backspace
                case 0x7F: // delete
                    if (_currentOutLine.Length > 0) _currentOutLine.Length--;
                    break;
                case 0x00: // NUL — skip
                    break;
                default:
                    _currentOutLine.Append((char)b);
                    break;
            }
        }
    }

    // Process a completed outbound line. When the line is the set suicide
    // arming trigger, flip _captureArmed. While armed, every subsequent
    // non-empty completed line becomes the new _latestArmedLine — the rolling
    // 1-deep buffer preserves the LATEST candidate so the old-password line
    // (which the user typed first) is overwritten by the new-password line
    // (which they typed second), and OnPasswordChanged commits the right value.
    private void FinalizeOutboundLine()
    {
        string completed = _currentOutLine.ToString();
        _currentOutLine.Clear();
        if (completed.Length == 0) return;

        if (!_captureArmed)
        {
            // Arm on outbound `set suicide`. Case-insensitive because
            // MUDs accept any casing. The bare `suicide` (use-pw form)
            // does NOT change the password, so it doesn't arm.
            if (completed.Trim().Equals("set suicide", StringComparison.OrdinalIgnoreCase))
            {
                _captureArmed = true;
                _log?.Log(LogSeverity.Info, "Suicide",
                    "Outbound `set suicide` observed — capture armed for the next 'Password changed'.");
            }
            return;
        }

        // Armed: rolling 1-deep buffer keeps only the latest line.
        // Old-password ⇒ overwritten when new-password arrives ⇒
        // OnPasswordChanged commits the new-password value.
        _latestArmedLine = completed;
        _log?.Log(LogSeverity.Debug, "Suicide",
            $"Captured outbound line while armed ({completed.Length} char).");
    }

    // ----- Server-line handlers ------------------------------------------

    private void OnOldPasswordPrompt()
    {
        // Server is asking for the current password — change-existing
        // flow. Lock the gate; we don't need to capture the old value
        // (it's already what we have stored, presumably).
        _state = FlowState.AwaitingOldPassword;
        _gate.IsLocked = true;
        _log?.Log(LogSeverity.Info, "Suicide",
            "Detected change-password flow — engine gate LOCKED for old-password entry.");
    }

    private void OnNewPasswordPrompt()
    {
        // Gate-management only — capture timing lives in the outbound
        // sniffer (see _captureArmed / _latestArmedLine). Two paths
        // land here:
        //   * Fresh set, no existing password — flow opens here directly.
        //   * Change-password flow — already in AwaitingOldPassword.
        _state = FlowState.AwaitingNewPassword;
        _gate.IsLocked = true;
        _log?.Log(LogSeverity.Info, "Suicide",
            "Awaiting new-password entry — engine gate LOCKED.");
    }

    private void OnUsePasswordPrompt()
    {
        // User typed `suicide` and a password is set — server is
        // asking for it. Lock so a stray auto-send doesn't become
        // the attempt; we don't capture anything (this is the user's
        // existing password being challenged).
        _state = FlowState.AwaitingUsePassword;
        _gate.IsLocked = true;
        _log?.Log(LogSeverity.Info, "Suicide",
            "Detected `suicide` use-flow — engine gate LOCKED for password challenge.");
    }

    private void OnInvalidPassword()
    {
        // Server bailed the flow. Don't commit the captured candidate;
        // disarm the sniffer + unlock the gate.
        Reset(reason: "Invalid password — flow aborted by server.");
    }

    private void OnPasswordChanged()
    {
        // Success. Commit the sniffer's latest captured line as the
        // new password. The rolling 1-deep buffer guarantees this is
        // the new-password value (the old-password line was already
        // overwritten when the user typed the new one).
        if (_captureArmed && _latestArmedLine is { } candidate && _profile.Current is { } profile)
        {
            profile.EncryptedSuicidePassword = _protector.Protect(candidate);
            _profile.Save();
            _profile.NotifyMutated();
            _log?.Log(LogSeverity.Info, "Suicide",
                $"Password Changed observed — stored encrypted on profile ({candidate.Length} char).");
        }
        else
        {
            _log?.Log(LogSeverity.Warn, "Suicide",
                $"Password Changed observed but no captured candidate to commit (armed={_captureArmed}, latest={_latestArmedLine?.Length ?? 0}).");
        }
        Reset(reason: "Password Changed.");
    }

    private void OnPasswordNotChanged()
    {
        Reset(reason: "Password NOT changed — no commit.");
    }

    private void OnNotSet()
    {
        // `pro` confirmed no password is set on the realm. Wipe the
        // stored value if we had one — the realm's view is
        // authoritative.
        if (_profile.Current is { } profile && profile.EncryptedSuicidePassword is not null)
        {
            profile.EncryptedSuicidePassword = null;
            _profile.Save();
            _profile.NotifyMutated();
            _log?.Log(LogSeverity.Info, "Suicide",
                "`pro` confirmed no password set — wiped stored encrypted value.");
        }
    }

    private void OnReroll()
    {
        // Successful suicide rerolled the character — the realm's stored
        // suicide password died with the old character, so wipe our cached
        // copy. Any in-flight set-flow is abandoned too (Reset).
        if (_profile.Current is { } profile && profile.EncryptedSuicidePassword is not null)
        {
            profile.EncryptedSuicidePassword = null;
            _profile.Save();
            _profile.NotifyMutated();
            _log?.Log(LogSeverity.Info, "Suicide",
                "Reroll observed — wiped stored encrypted suicide password (fresh character).");
        }
        Reset(reason: "Reroll — character rerolled.");
    }

    private void Reset(string reason)
    {
        _state = FlowState.Idle;
        _gate.IsLocked = false;
        // Disarm the outbound sniffer + drop any in-flight line so a
        // fresh flow doesn't inherit leftover bytes from an aborted
        // one (user typed half a password then server bailed with
        // "Invalid", etc.).
        _captureArmed = false;
        _latestArmedLine = null;
        _currentOutLine.Clear();
        _log?.Log(LogSeverity.Info, "Suicide", $"Engine gate UNLOCKED — {reason}");
    }
}
