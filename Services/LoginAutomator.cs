using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;


// Drives the per-character BBS handshake. Walks the menu-nav sequence the user
// authored on the BBS section — wait for a pattern, send a reply, move on —
// until the final step's response is sent and LoggedIntoGame fires. The reply
// text supports two case-insensitive placeholders: {username} / {userid} for
// the configured username and {password} / {passwd} for the password decrypted
// from PasswordProtector.
//
// One-shot credential guard: once a step containing {username} (resp.
// {password}) has been answered AND the next step's pattern matches — meaning
// the BBS accepted the response — that placeholder is "locked" for the rest of
// the session. A later step that references the same placeholder aborts the
// automator, so a malicious server can't re-prompt "what is your username
// again?" mid-session and get it echoed back.
//
// State is lock-guarded so the UI-thread Feed call can't race the
// post-ConfigureAwait(false) continuation in ResolveAndSendAsync or the
// per-step timeout callback.
public sealed class LoginAutomator : IDisposable
{
    private const int BufferCap = 4096;

    // Case-insensitive — users may type "{Username}" or "{USERNAME}" and
    // still expect substitution. Compiled once at type init.
    private static readonly Regex UserPlaceholder =
        new(@"\{user(?:name|id)\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PasswordPlaceholder =
        new(@"\{pass(?:word|wd)\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IReadOnlyList<AutomationStep> _steps;
    private readonly string? _username;
    private readonly Func<Task<string?>>? _resolvePassword;
    private readonly Func<string, CancellationToken, Task> _sendText;
    private readonly Action<string>? _log;
    private readonly StringBuilder _buffer = new(BufferCap);
    private readonly object _lock = new();

    private StripState _state;
    private int _stepIndex;
    private bool _started;
    private bool _disposed;
    private bool _resolving;
    private bool _usernamePending;
    private bool _passwordPending;
    private bool _usernameLocked;
    private bool _passwordLocked;

    // Fired after the final step matches and its response is sent.
    public event Action? LoggedIntoGame;

    // Fired when a step's send fails (no value to substitute, send IO error,
    // etc.). Payload is a short reason.
    public event Action<string>? Aborted;

    public bool IsRunning => _started && !_disposed && _stepIndex < _steps.Count;

    // Zero-based index of the step currently being awaited; exposed for diagnostics.
    public int CurrentStepIndex => _stepIndex;

    public int StepCount => _steps.Count;

    public LoginAutomator(
        IReadOnlyList<AutomationStep> steps,
        string? username,
        Func<Task<string?>>? resolvePassword,
        Func<string, CancellationToken, Task> sendText,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(sendText);
        _steps = steps;
        _username = string.IsNullOrEmpty(username) ? null : username;
        _resolvePassword = resolvePassword;
        _sendText = sendText;
        _log = log;
    }

    // Convert each persisted MenuStep into its runtime form.
    public static IReadOnlyList<AutomationStep> BuildSteps(BbsCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        List<AutomationStep> steps = new(credentials.MenuNavSteps.Count);
        foreach (MenuStep ms in credentials.MenuNavSteps)
        {
            steps.Add(new AutomationStep(ms.WaitForPattern, ms.Send));
        }
        return steps;
    }

    // Build a ready-to-start automator from a character's BBS credentials.
    // Returns null when there's nothing to do — no credentials, or the
    // credentials carry no menu-nav steps.
    public static LoginAutomator? TryBuild(
        BbsCredentials? credentials,
        PasswordProtector passwords,
        Func<string, CancellationToken, Task> sendText,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(passwords);
        ArgumentNullException.ThrowIfNull(sendText);
        if (credentials is null) return null;
        if (credentials.MenuNavSteps.Count == 0) return null;

        Func<Task<string?>>? resolvePassword = null;
        if (credentials.EncryptedPassword is { } blob)
        {
            // Decrypt lazily — the protector is sync and fast, but we keep
            // the async signature so the automator can stay agnostic of
            // where the secret comes from (future OS keychain swap, etc.).
            resolvePassword = () => Task.FromResult(passwords.Unprotect(blob));
        }

        // Username decrypted at build time — the BBS receives it
        // plaintext on the wire so there's no point keeping it sealed
        // once we're inside the login flow.
        string username = credentials.EncryptedUsername is { } usernameBlob
            ? (passwords.Unprotect(usernameBlob) ?? string.Empty)
            : string.Empty;

        IReadOnlyList<AutomationStep> steps = BuildSteps(credentials);
        return new LoginAutomator(steps, username, resolvePassword, sendText, log);
    }

    // Begin the automation.
    public void Start()
    {
        bool done;
        lock (_lock)
        {
            if (_started || _disposed) return;
            _started = true;
            done = _steps.Count == 0;
        }
        if (done) FireDone();
    }

    // Feed display bytes (post-IAC, pre-emulator). CSI sequences are stripped
    // inline; the resulting plain text is appended to a rolling buffer and the
    // current step's pattern is tested against it.
    public void Feed(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            if (_disposed || !_started || _stepIndex >= _steps.Count) return;

            foreach (byte b in data)
            {
                switch (_state)
                {
                    case StripState.Normal:
                        if (b == 0x1B) _state = StripState.EscSeen;
                        else if (b >= 0x20 && b < 0x7F) _buffer.Append((char)b);
                        else if (b == (byte)'\r' || b == (byte)'\n') _buffer.Append((char)b);
                        break;

                    case StripState.EscSeen:
                        _state = b == (byte)'[' ? StripState.Csi : StripState.Normal;
                        break;

                    case StripState.Csi:
                        if (b >= 0x40 && b <= 0x7E) _state = StripState.Normal;
                        break;
                }
            }

            if (_buffer.Length > BufferCap)
            {
                _buffer.Remove(0, _buffer.Length - BufferCap);
            }
        }

        TryAdvance();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
    }

    // ----- Internals ----------------------------------------------------

    private void TryAdvance()
    {
        AutomationStep step;
        int dispatchIndex;
        lock (_lock)
        {
            if (_disposed || _resolving || _stepIndex >= _steps.Count) return;
            step = _steps[_stepIndex];
            string text = _buffer.ToString();
            if (!step.TryMatch(text, out int matchEnd)) return;

            _buffer.Remove(0, matchEnd);

            // Matching this step means the BBS moved on from the previous
            // prompt — promote any pending credential lock now.
            if (_usernamePending) { _usernameLocked = true; _usernamePending = false; }
            if (_passwordPending) { _passwordLocked = true; _passwordPending = false; }

            _resolving = true;
            dispatchIndex = _stepIndex;
        }
        _ = ResolveAndSendAsync(step, dispatchIndex);
    }

    private async Task ResolveAndSendAsync(AutomationStep step, int indexAtDispatch)
    {
        string template = step.SendTemplate;
        bool usesUser = UserPlaceholder.IsMatch(template);
        bool usesPw = PasswordPlaceholder.IsMatch(template);

        // One-shot credential guard. Set BEFORE we send — so a malicious
        // server that re-prompts after acceptance can't trick us into
        // echoing the secret a second time.
        if (usesUser && _usernameLocked)
        {
            Abort($"step {indexAtDispatch + 1}: username already accepted; refusing to resend");
            return;
        }
        if (usesPw && _passwordLocked)
        {
            Abort($"step {indexAtDispatch + 1}: password already accepted; refusing to resend");
            return;
        }
        if (usesUser && string.IsNullOrEmpty(_username))
        {
            Abort($"step {indexAtDispatch + 1}: send references {{username}} but no username is configured");
            return;
        }

        string? password = null;
        if (usesPw)
        {
            if (_resolvePassword is null)
            {
                Abort($"step {indexAtDispatch + 1}: send references {{password}} but no password is configured");
                return;
            }
            try
            {
                password = await _resolvePassword().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Abort($"step {indexAtDispatch + 1}: password lookup failed: {ex.Message}");
                return;
            }
            if (password is null)
            {
                Abort($"step {indexAtDispatch + 1}: password not found in credential store");
                return;
            }
        }

        string send = Substitute(template, usesUser ? _username : null, usesPw ? password : null);

        lock (_lock)
        {
            if (_disposed) return;
            if (usesUser) _usernamePending = true;
            if (usesPw) _passwordPending = true;
        }

        try
        {
            await _sendText(send, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Abort($"step {indexAtDispatch + 1}: send failed: {ex.Message}");
            return;
        }
        if (_disposed) return;

        bool done;
        lock (_lock)
        {
            if (_disposed) return;
            _log?.Invoke($"LoginAutomator: matched step {indexAtDispatch + 1}/{_steps.Count}");
            _stepIndex++;
            _resolving = false;
            done = _stepIndex >= _steps.Count;
        }

        if (done) { FireDone(); return; }
        TryAdvance();
    }

    private void Abort(string reason)
    {
        lock (_lock)
        {
            if (_disposed) return;
        }
        _log?.Invoke($"LoginAutomator aborted: {reason}");
        Aborted?.Invoke(reason);
        Dispose();
    }

    private void FireDone()
    {
        lock (_lock)
        {
            if (_disposed) return;
        }
        _log?.Invoke($"LoginAutomator: all {_steps.Count} step(s) complete");
        var done = LoggedIntoGame;
        Dispose();
        done?.Invoke();
    }

    private static string Substitute(string template, string? username, string? password)
    {
        string r = template;
        if (username is not null) r = UserPlaceholder.Replace(r, username);
        if (password is not null) r = PasswordPlaceholder.Replace(r, password);
        // Auto-append the CR — every menu-nav response is "type X, hit Enter".
        // Forces the user to NOT have to think about escape sequences when
        // authoring steps; the BBS sees a complete submitted line either way.
        return r + "\r";
    }

    private enum StripState : byte { Normal, EscSeen, Csi }
}

// One step in the LoginAutomator queue: the substring to wait for and the raw
// reply template (with optional {username} / {password} placeholders).
// Substitution and the trailing CR are added by the automator at send time.
public sealed class AutomationStep
{
    public string WaitForPattern { get; }
    public string SendTemplate { get; }

    public AutomationStep(string waitForPattern, string sendTemplate)
    {
        WaitForPattern = waitForPattern ?? string.Empty;
        SendTemplate = sendTemplate ?? string.Empty;
    }

    // Returns true plus the index just past the matched span if text contains
    // the step's pattern (case-insensitive substring). The caller uses matchEnd
    // to trim the buffer so the same characters can't satisfy a later step.
    public bool TryMatch(string text, out int matchEnd)
    {
        matchEnd = 0;
        if (string.IsNullOrEmpty(WaitForPattern)) return false;
        int idx = text.IndexOf(WaitForPattern, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        matchEnd = idx + WaitForPattern.Length;
        return true;
    }
}
