using System.Text;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game;

/// <summary>
/// Passive observer for the in-game <c>set suicide</c> /
/// <c>suicide</c> password flows. Drives an
/// <see cref="EngineSendGate"/> to pause every engine while the
/// user is in a password-entry prompt (so a stray <c>par</c> poll
/// doesn't end up becoming the password), and captures the password
/// the user types so we can store it encrypted on the
/// <see cref="CharacterProfile"/> for the Phase 6 <c>@suicide</c>
/// consumer to use.
/// </summary>
/// <remarks>
/// <para>
/// The user is described as manually typing the flow themselves; we
/// don't run a wizard or send commands on their behalf. We just
/// watch the server prompts, lock the gate, watch the user's
/// outbound for the bytes that follow, and commit on the
/// <c>Password Changed</c> success line.
/// </para>
/// <para>
/// State machine (all transitions clear the gate + state if a
/// terminator fires):
/// </para>
/// <list type="bullet">
///   <item><b>Idle</b> — gate clear, no pending capture.</item>
///   <item><b>AwaitingOldPassword</b> — server printed
///         <c>"Enter the current password:"</c>; the next outbound
///         line is the old password (we don't store it, just pass
///         through). On <c>Invalid password specified.</c> the
///         flow aborts (back to Idle without touching stored).
///         Otherwise <c>Enter New Password:</c> follows and we
///         transition to <c>AwaitingNewPassword</c>.</item>
///   <item><b>AwaitingNewPassword</b> — server printed
///         <c>"Enter New Password:"</c>; the next outbound line
///         is the new password. We tentatively capture it,
///         waiting for <c>Password Changed</c> to commit or
///         <c>Password NOT changed</c> to discard.</item>
///   <item><b>AwaitingUsePassword</b> — user typed <c>suicide</c>
///         and server printed
///         <c>"Enter your suicide password:"</c>. We just gate
///         the engine here so auto-sends don't end up sent as the
///         password attempt; no capture, no profile change.</item>
/// </list>
/// <para>
/// The <c>pro</c>-command response line
/// <c>"You do not have a suicide password set."</c> is treated as
/// authoritative — we wipe the stored password regardless of state,
/// since the realm's view differs from our cached one.
/// </para>
/// </remarks>
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
    private string? _pendingNewPassword;

    /// <summary>
    /// Char-by-char accumulator for the password capture. Lets
    /// <see cref="ObserveOutbound"/> work regardless of whether the
    /// wire-send path is line-mode (LocalInputBuffer flushes the whole
    /// password + CR as one call) or char-mode (real path — MajorMUD's
    /// password prompts use server-side Telnet ECHO suppression, so each
    /// keystroke ships individually and the server echoes <c>*</c> back
    /// per char). The pre-fix "overwrite _pendingNewPassword each call"
    /// model silently lost everything but the latest char under char-
    /// mode, and the trailing CR cleared the field entirely, surfacing
    /// as "Password Changed observed but no captured candidate to
    /// commit." in the LogPane and a stay-<c>null</c>
    /// <see cref="CharacterProfile.EncryptedSuicidePassword"/> on disk.
    /// </summary>
    private readonly StringBuilder _passwordBuilder = new();

    /// <summary>Current state of the flow — exposed for tests + diagnostics.</summary>
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
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (IDisposable s in _subs) s.Dispose();
        _subs.Clear();
    }

    /// <summary>
    /// Called by the wire-send path on every outbound payload so we
    /// can capture the user-typed password during the
    /// <see cref="FlowState.AwaitingNewPassword"/> phase. No-op in
    /// every other state — we don't store the old password (we don't
    /// need it: it's the same as the new one was, before they changed
    /// it) or the use-suicide attempt.
    /// </summary>
    /// <remarks>
    /// Bytes are accumulated into <see cref="_passwordBuilder"/> until
    /// a CR / LF terminator arrives. Works for both wire-send modes:
    /// <list type="bullet">
    ///   <item><b>Char-mode</b> (real path on MajorMUD password
    ///         prompts) — server uses Telnet ECHO suppression, each
    ///         keystroke ships as its own ObserveOutbound call, server
    ///         echoes <c>*</c> per char.</item>
    ///   <item><b>Line-mode</b> — entire password + CR arrives in one
    ///         call from LocalInputBuffer's flush. The loop below
    ///         processes the chars and finalizes on the trailing CR
    ///         the same way as char-mode.</item>
    /// </list>
    /// Backspace bytes (<c>0x08</c> / <c>0x7F</c>) shrink the buffer so
    /// the user typing a wrong char and correcting it produces the
    /// right captured value.
    /// </remarks>
    public void ObserveOutbound(ReadOnlySpan<byte> bytes)
    {
        if (_state != FlowState.AwaitingNewPassword) return;
        if (bytes.IsEmpty) return;

        foreach (byte b in bytes)
        {
            switch (b)
            {
                case 0x0D: // CR
                case 0x0A: // LF
                    // Line terminator — finalize whatever we
                    // accumulated. Empty buffer = user just hit Enter
                    // without typing; let the server's "Password NOT
                    // changed" terminator drive the reset.
                    if (_passwordBuilder.Length > 0)
                    {
                        _pendingNewPassword = _passwordBuilder.ToString();
                        _log?.Log(LogSeverity.Info, "Suicide",
                            $"Captured candidate new password ({_pendingNewPassword.Length} char), waiting for confirmation.");
                    }
                    else
                    {
                        _pendingNewPassword = null;
                    }
                    _passwordBuilder.Clear();
                    return;
                case 0x08: // backspace
                case 0x7F: // delete
                    if (_passwordBuilder.Length > 0) _passwordBuilder.Length--;
                    break;
                case 0x00: // NUL — skip
                    break;
                default:
                    _passwordBuilder.Append((char)b);
                    break;
            }
        }
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
        // Two paths land here:
        //   * Fresh set, no existing password — flow opens here directly.
        //   * Change-password flow — already in AwaitingOldPassword.
        // Either way we now expect the user to type the new password.
        _state = FlowState.AwaitingNewPassword;
        _gate.IsLocked = true;
        _pendingNewPassword = null;
        // Reset the char accumulator — the old-password phase doesn't
        // touch it (state gate is AwaitingNewPassword in ObserveOutbound)
        // but defensive-clear in case a future state extension does.
        _passwordBuilder.Clear();
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
        // Server bailed the flow. Don't commit the pending value;
        // unlock + reset.
        _pendingNewPassword = null;
        Reset(reason: "Invalid password — flow aborted by server.");
    }

    private void OnPasswordChanged()
    {
        // Success. Commit the captured candidate to the profile.
        if (_pendingNewPassword is not null && _profile.Current is { } profile)
        {
            profile.EncryptedSuicidePassword = _protector.Protect(_pendingNewPassword);
            _profile.Save();
            _profile.NotifyMutated();
            _log?.Log(LogSeverity.Info, "Suicide",
                $"Password Changed observed — stored encrypted on profile.");
        }
        else
        {
            _log?.Log(LogSeverity.Warn, "Suicide",
                "Password Changed observed but no captured candidate to commit.");
        }
        _pendingNewPassword = null;
        Reset(reason: "Password Changed.");
    }

    private void OnPasswordNotChanged()
    {
        _pendingNewPassword = null;
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

    private void Reset(string reason)
    {
        _state = FlowState.Idle;
        _gate.IsLocked = false;
        // Drop any in-flight char accumulation so a fresh flow doesn't
        // inherit leftover bytes from an aborted one (e.g. user typed
        // half a password then the server bailed with "Invalid").
        _passwordBuilder.Clear();
        _log?.Log(LogSeverity.Info, "Suicide", $"Engine gate UNLOCKED — {reason}");
    }
}
