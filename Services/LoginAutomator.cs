using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FujinTerm.Models.Profile;
using FujinTerm.Models.Settings;

namespace FujinTerm.Services;

/// <summary>
/// Drives the per-character BBS handshake: watches the post-IAC byte stream
/// for the login + password prompts, sends the configured credentials, then
/// walks the per-character menu-nav sequence until the final step's pattern
/// fires <see cref="LoggedIntoGame"/>. Fed via <see cref="Feed"/> (typically
/// from the same buffer that reaches the terminal emulator) and writes
/// outgoing replies through the <c>sendText</c> callback handed in at
/// construction.
/// </summary>
/// <remarks>
/// State machine: a linear queue of <see cref="AutomationStep"/>s, one
/// "step" at a time. Each step has a per-step timeout — failure aborts the
/// whole sequence rather than retrying. CSI escapes are stripped inline
/// (same shape as <see cref="WirePromptScanner"/>) so a colorised login
/// prompt still matches.
/// </remarks>
public sealed class LoginAutomator : IDisposable
{
    private const int BufferCap = 4096;

    private readonly IReadOnlyList<AutomationStep> _steps;
    private readonly Func<string, CancellationToken, Task> _sendText;
    private readonly Action<string>? _log;
    private readonly StringBuilder _buffer = new(BufferCap);
    // Guards _buffer / _state / _stepIndex / _resolving / _stepCts so the
    // post-ConfigureAwait(false) continuation in ResolveAndSendAsync can't
    // race with UI-thread Feed calls or the Task.Delay timeout callback.
    private readonly object _lock = new();

    private StripState _state;
    private int _stepIndex;
    private bool _started;
    private bool _disposed;
    private bool _resolving;
    private CancellationTokenSource? _stepCts;

    /// <summary>Fired after the final step matches and its response is sent.</summary>
    public event Action? LoggedIntoGame;

    /// <summary>Fired when a step times out or fails. Payload is a short reason.</summary>
    public event Action<string>? Aborted;

    /// <summary>True once <see cref="Start"/> has been called and the queue has steps left.</summary>
    public bool IsRunning => _started && !_disposed && _stepIndex < _steps.Count;

    /// <summary>Zero-based index of the step currently being awaited. Exposed for diagnostics.</summary>
    public int CurrentStepIndex => _stepIndex;

    /// <summary>Total number of steps in the queue.</summary>
    public int StepCount => _steps.Count;

    public LoginAutomator(
        IReadOnlyList<AutomationStep> steps,
        Func<string, CancellationToken, Task> sendText,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(sendText);
        _steps = steps;
        _sendText = sendText;
        _log = log;
    }

    /// <summary>
    /// Build the canonical step queue for a BBS handshake: login prompt →
    /// username, password prompt → password from <paramref name="credStore"/>,
    /// then each <see cref="BbsCredentials.MenuNavSteps"/> entry in order.
    /// Returns <c>null</c> when there's nothing to automate (no credentials
    /// or blank username) — the caller skips automation in that case.
    /// </summary>
    public static IReadOnlyList<AutomationStep>? BuildSteps(
        BbsProfile bbs,
        BbsCredentials? credentials,
        ICredentialStore credStore)
    {
        ArgumentNullException.ThrowIfNull(bbs);
        ArgumentNullException.ThrowIfNull(credStore);

        if (credentials is null || string.IsNullOrWhiteSpace(credentials.Username))
        {
            return null;
        }

        List<AutomationStep> steps = new();

        string username = credentials.Username;
        steps.Add(new AutomationStep(
            bbs.LoginPromptPattern,
            MenuStepMatchType.Literal,
            () => Task.FromResult<string?>(username + "\r"),
            timeoutSeconds: 30));

        string? passwordId = credentials.PasswordCredentialId;
        steps.Add(new AutomationStep(
            bbs.PasswordPromptPattern,
            MenuStepMatchType.Literal,
            async () =>
            {
                if (passwordId is null) return null;
                string? pw = await credStore.GetAsync(passwordId).ConfigureAwait(false);
                return pw is null ? null : pw + "\r";
            },
            timeoutSeconds: 30));

        foreach (MenuStep ms in credentials.MenuNavSteps)
        {
            MenuStep captured = ms;
            string send = UnescapeSend(captured.Send);
            steps.Add(new AutomationStep(
                captured.WaitForPattern,
                captured.MatchType,
                () => Task.FromResult<string?>(send),
                Math.Max(1, captured.TimeoutSeconds)));
        }

        return steps;
    }

    /// <summary>Begin the automation. Arms the timeout for the first step.</summary>
    public void Start()
    {
        bool done;
        lock (_lock)
        {
            if (_started || _disposed) return;
            _started = true;
            done = _steps.Count == 0;
            if (!done) ArmStepTimeoutLocked();
        }
        if (done) FireDone();
    }

    /// <summary>
    /// Feed display bytes (post-IAC, pre-emulator). CSI sequences are stripped
    /// inline; the resulting plain text is appended to a rolling buffer and
    /// the current step's pattern is tested against it.
    /// </summary>
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
            CancelStepTimeoutLocked();
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
            CancelStepTimeoutLocked();
            _resolving = true;
            dispatchIndex = _stepIndex;
        }
        _ = ResolveAndSendAsync(step, dispatchIndex);
    }

    private async Task ResolveAndSendAsync(AutomationStep step, int indexAtDispatch)
    {
        string? send;
        try
        {
            send = await step.ResolveSend().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Abort($"step {indexAtDispatch + 1}: {ex.Message}");
            return;
        }
        if (_disposed) return;
        if (send is null)
        {
            Abort($"step {indexAtDispatch + 1}: no value to send (missing password?)");
            return;
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
            if (!done) ArmStepTimeoutLocked();
        }

        if (done) { FireDone(); return; }
        TryAdvance();
    }

    private void ArmStepTimeoutLocked()
    {
        CancelStepTimeoutLocked();
        _stepCts = new CancellationTokenSource();
        CancellationToken token = _stepCts.Token;
        int seconds = Math.Max(1, _steps[_stepIndex].TimeoutSeconds);
        int armedAt = _stepIndex;
        string pattern = _steps[armedAt].WaitForPattern;

        _ = Task.Delay(TimeSpan.FromSeconds(seconds), token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            lock (_lock)
            {
                if (_disposed || _stepIndex != armedAt) return;
            }
            Abort($"step {armedAt + 1} timed out after {seconds}s waiting for \"{pattern}\"");
        }, TaskScheduler.Default);
    }

    private void CancelStepTimeoutLocked()
    {
        try { _stepCts?.Cancel(); } catch { }
        _stepCts?.Dispose();
        _stepCts = null;
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

    private static string UnescapeSend(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        // Users author menu-nav "Send" values in a text box where they can't
        // type literal CR / LF. Accept the common backslash escapes so a step
        // that needs to press Enter after a selection (e.g. "G\r") works.
        return raw.Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\t", "\t");
    }

    private enum StripState : byte { Normal, EscSeen, Csi }
}

/// <summary>
/// One step in the <see cref="LoginAutomator"/> queue: the pattern to wait
/// for, the deferred "what to send" resolver (so the password is only
/// pulled from the credential store when actually needed), and the per-step
/// timeout in seconds.
/// </summary>
public sealed class AutomationStep
{
    public string WaitForPattern { get; }
    public MenuStepMatchType MatchType { get; }
    public Func<Task<string?>> ResolveSend { get; }
    public int TimeoutSeconds { get; }

    private readonly Regex? _regex;

    public AutomationStep(
        string waitForPattern,
        MenuStepMatchType matchType,
        Func<Task<string?>> resolveSend,
        int timeoutSeconds)
    {
        WaitForPattern = waitForPattern ?? string.Empty;
        MatchType = matchType;
        ResolveSend = resolveSend ?? throw new ArgumentNullException(nameof(resolveSend));
        TimeoutSeconds = timeoutSeconds;

        _regex = matchType switch
        {
            MenuStepMatchType.Regex => SafeCompile(WaitForPattern),
            MenuStepMatchType.Wildcard => CompileWildcard(WaitForPattern),
            _ => null,
        };
    }

    /// <summary>
    /// Returns <c>true</c> + the index just past the matched span if
    /// <paramref name="text"/> contains the step's pattern. The caller uses
    /// <paramref name="matchEnd"/> to trim the buffer so the same characters
    /// can't satisfy a later step.
    /// </summary>
    public bool TryMatch(string text, out int matchEnd)
    {
        matchEnd = 0;
        if (string.IsNullOrEmpty(WaitForPattern)) return false;

        if (MatchType == MenuStepMatchType.Literal)
        {
            int idx = text.IndexOf(WaitForPattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            matchEnd = idx + WaitForPattern.Length;
            return true;
        }

        if (_regex is null) return false;
        Match m = _regex.Match(text);
        if (!m.Success) return false;
        matchEnd = m.Index + m.Length;
        return true;
    }

    private static Regex? SafeCompile(string pattern)
    {
        try { return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled); }
        catch (ArgumentException) { return null; }
    }

    private static Regex CompileWildcard(string pattern)
    {
        StringBuilder sb = new(pattern.Length * 2 + 2);
        foreach (char c in pattern)
        {
            if (c == '*') sb.Append(".*");
            else if (c == '?') sb.Append('.');
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
