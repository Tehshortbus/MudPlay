using System.Text;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Remote;

// Consumer of RemoteCommandManager for the @suicide remote command. Authorised
// callers — players with the PlayerRemoteControls.SysopCommands ("Elevated
// Commands") flag — can request the local character commit suicide; the engine's
// lives-based policy block already protects against destructive misuse below
// RemoteCommandManager.MaxSuicideLivesThreshold.
//
// Two wire flows, picked based on whether the loaded profile carries a stored
// encrypted suicide password:
//   - Have stored password — send suicide\r, then wait for the realm's "Enter
//     your suicide password:" on the terminal's cursor row and answer it with the
//     decrypted password. If the realm doesn't ask within PromptWait the password
//     is never sent and the sender is told. "Invalid password specified." →
//     telepath the sender so they know the stored value is stale.
//   - No stored password — can't blindly send suicide because if the realm
//     actually has a password set we'd hang at the prompt with no way to answer.
//     Pre-check via pro: between sending pro and the NEXT statline prompt
//     arriving, watch for "You do not have a suicide password set.". If observed,
//     the realm has no password set — fire suicide\r (kills immediately, no
//     prompt). If the next statline arrives without the line firing, the realm
//     DOES have a password we don't have stored; telepath the sender with
//     {Suicide failed, password set in game but not stored.} and log the
//     profile/realm mismatch.
//
// Bypasses EngineSendGate deliberately — we're the flow's initiator, not a
// victim of it. The wire-sender bound by MainWindowViewModel here is the RAW
// SendUserInput, not the gate-wrapped one every other engine receives.
public sealed class SuicideHandler : IDisposable
{
    private static readonly string[] RegisteredCommands = { "@suicide" };

    private readonly RemoteCommandManager _engine;
    private readonly ProfileService _profile;
    private readonly PasswordProtector _protector;
    private readonly WirePromptScanner _promptScanner;
    private readonly LogService? _log;
    private readonly IDisposable _invalidSub;
    private readonly IDisposable _notSetSub;
    private readonly Action<PromptObservation> _promptHandler;
    private Action<byte[]>? _wireSender;
    private bool _disposed;

    // Refusal callback for the last @suicide invocation that took the
    // have-stored-password branch, captured at dispatch time and consumed by the
    // invalid-password line if it fires. Both callbacks here are the context's
    // Refuse: the only thing this handler ever answers is a failure.
    private Action<string>? _pendingReply;

    // Refusal callback + window state for an in-flight no-stored-password pro
    // pre-check. _seenNotSetInProWindow flips true when the canonical "not set"
    // line fires during the window; the next PromptObserved firing decides the
    // outcome based on that flag.
    private Action<string>? _proPendingReply;
    private bool _seenNotSetInProWindow;

    // When `suicide` went out with a stored password still to send; null when no
    // password is owed. The password is sent only on the game's own prompt for it,
    // and not at all once PromptWait has passed.
    private DateTimeOffset? _promptAwaitedSince;
    private readonly IMessagePattern _passwordPrompt;
    private readonly MessageRouter _router;
    internal static readonly TimeSpan PromptWait = TimeSpan.FromSeconds(10);
    private readonly Func<DateTimeOffset> _now;

    // True from `suicide` going out until the password is sent or given up on.
    // Read off the UI thread too (the sent-command trail asks from whichever
    // thread sends), so it is a flag of its own beside the time.
    public bool AwaitingPasswordPrompt => _awaitingPrompt;
    private volatile bool _awaitingPrompt;

    // True only while the password line itself is being handed to the wire, so
    // nothing that records outbound commands takes it.
    public bool SendingPassword { get; private set; }

    public SuicideHandler(
        RemoteCommandManager engine,
        MessageRouter router,
        ProfileService profile,
        PasswordProtector protector,
        WirePromptScanner promptScanner,
        LogService? log = null,
        Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(promptScanner);
        _engine        = engine;
        _profile       = profile;
        _protector     = protector;
        _promptScanner = promptScanner;
        _log           = log;

        if (!RemoteCommandCatalog.TryGetCategory("@suicide", out PlayerRemoteControls category))
            throw new InvalidOperationException("RemoteCommandCatalog missing entry for '@suicide'.");
        _engine.RegisterHandler("@suicide", category, OnSuicide);

        if (!router.TryGetPattern(KnownPatterns.SuicidePromptUseSuicide, out _passwordPrompt))
            throw new InvalidOperationException("No catalog pattern for the suicide password prompt.");
        if (!IsPasswordPrompt(PasswordPromptText))
            throw new InvalidOperationException("The catalog's suicide password prompt is no longer the text this handler waits for.");
        _router = router;
        _router.LineDispatched += OnLineDispatched;

        _invalidSub = router.Subscribe(KnownPatterns.SuicideInvalidPassword, _ => OnInvalid());
        // SuicideNotSet doubles as the no-stored-password pre-check
        // confirmation: if we sent `pro` and this fires before the
        // next statline arrives, suicide on this realm is unprompted
        // and we can fire it on the prompt-observation path.
        // SuicidePasswordTracker also subscribes to this pattern (to
        // wipe stored values when the realm's view disagrees with
        // ours); both fire independently, no interaction.
        _notSetSub = router.Subscribe(KnownPatterns.SuicideNotSet, _ => OnNotSetObserved());
        // Next statline closes the pro window — fires the decision
        // based on whether SuicideNotSet was seen during the window.
        _promptHandler = OnPromptObserved;
        _promptScanner.PromptObserved += _promptHandler;
    }

    // Bind the RAW wire-sender (NOT the gate-wrapped one). Every other engine in
    // the app uses the wrapped sender so the SuicidePasswordTracker can pause them
    // mid-flow; this handler is the exception because it OWNS the flow and needs
    // its sends to land even while the gate is locked.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (string cmd in RegisteredCommands) _engine.UnregisterHandler(cmd);
        _invalidSub.Dispose();
        _notSetSub.Dispose();
        _promptScanner.PromptObserved -= _promptHandler;
        _router.LineDispatched -= OnLineDispatched;
        // A wait in progress goes with the handler: neither hold may outlive it.
        EndWait();
    }

    private void OnSuicide(RemoteCommandContext ctx)
    {
        if (_wireSender is null) return;

        string? password = null;
        if (_profile.Current is { EncryptedSuicidePassword: { Length: > 0 } blob })
            password = _protector.Unprotect(blob);

        if (!string.IsNullOrEmpty(password))
        {
            // Have-stored-password branch. Only the command goes out now; the
            // password follows when the game is seen asking for it
            // (ObserveCursorRow). Sent back-to-back, a `suicide` the game refused
            // left the password to arrive as an ordinary command, echoed in clear
            // on the terminal and into anything that copies it.
            if (_promptAwaitedSince is { } since)
            {
                if (_now() - since <= PromptWait)
                {
                    ctx.Refuse("@suicide already in-flight, try again shortly");
                    return;
                }
                GiveUpOnPrompt();
            }
            _pendingReply = why => ctx.Refuse(why);
            _promptAwaitedSince = _now();
            _awaitingPrompt = true;
            // Nothing an engine sends may reach the game between the command and
            // its password: the game would take that as the password, and the
            // real one would then land at the ordinary prompt, in clear. A
            // telepath already queued is such a line too, and the pacer writes it
            // from its own timer, past the gate.
            SendGate?.Hold(GateHold);
            Telepaths?.Hold();
            SendOwn("suicide");
            _log?.Log(LogSeverity.Info, "Suicide",
                "@suicide: sent `suicide`; the stored password goes out when the game asks for it.");
            return;
        }

        // No stored password — pre-check via `pro` to disambiguate
        // realm-has-no-password (safe to suicide) from realm-has-
        // password-we-don't (mismatch, refuse + warn).
        if (_proPendingReply is not null)
        {
            // Another @suicide is already in the pre-check window —
            // refuse defensively rather than queueing.
            ctx.Refuse("@suicide already in-flight, try again shortly");
            return;
        }
        _proPendingReply = why => ctx.Refuse(why);
        _seenNotSetInProWindow = false;
        _wireSender(Encoding.Latin1.GetBytes("pro\r"));
        _log?.Log(LogSeverity.Info, "Suicide",
            "@suicide with no stored password — running `pro`; will decide on next statline.");
    }

    // The text on the terminal's cursor row, given on every screen update while
    // AwaitingPasswordPrompt. The game's "Enter your suicide password:" has no line
    // end until it is answered, so it never reaches the message router as a line
    // before the password is due: the row is where it can be seen in time.
    public void ObserveCursorRow(string rowText)
    {
        if (_promptAwaitedSince is not { } since) return;
        if (_now() - since > PromptWait) { GiveUpOnPrompt(); return; }
        if (!ShowsPasswordPrompt(rowText)) return;

        string? password = _profile.Current is { EncryptedSuicidePassword: { Length: > 0 } blob }
            ? _protector.Unprotect(blob)
            : null;
        if (string.IsNullOrEmpty(password) || _wireSender is null)
        {
            // The stored password went away between the command and the prompt
            // (a `pro` that reported none set wipes it).
            EndWait();
            FailPending("the stored suicide password was gone when the game asked for it: nothing sent",
                "suicide failed, no password stored");
            return;
        }
        // The holds come down only once the password is on the wire: let go of
        // first, a waiting telepath would go out ahead of it and be the answer.
        SendingPassword = true;
        try { SendOwn(password); }
        finally
        {
            SendingPassword = false;
            EndWait();
        }
        _log?.Log(LogSeverity.Info, "Suicide", "@suicide: the game asked for the password; sent.");
    }

    // The row must BE the prompt: alone, or straight after the game's statline.
    // The prompt's words inside anything else (a telepath quoting them) are a
    // player's text, and never a reason to send a password. The statline is the
    // one Settings -> Statline set, so the prompt scanner's own pattern says
    // where it ends; a statline that pattern doesn't read is "anything else".
    private bool ShowsPasswordPrompt(string? rowText)
    {
        if (string.IsNullOrWhiteSpace(rowText)) return false;
        if (Terminal.ChatLineDetector.IsChat(rowText)) return false;
        string text = rowText.Trim();
        if (text.Equals(PasswordPromptText, StringComparison.Ordinal)) return true;
        return _promptScanner.TrySplitPromptRow(text, out string rest)
            && rest.Trim().Equals(PasswordPromptText, StringComparison.Ordinal);
    }

    private bool IsPasswordPrompt(string text) =>
        _passwordPrompt.TryMatch(
            new Terminal.LineExtractor.EmittedLine(
                text, Array.Empty<Terminal.CellAttributes>(), DateTimeOffset.UnixEpoch, IsPromptLine: false),
            out _);

    // The game's prompt for the password
    // (GAME_MECHANICS "Character settings — `set` and the `pro` sheet").
    // Checked against the message catalogue's own pattern at construction, so
    // the two can't drift apart.
    internal const string PasswordPromptText = "Enter your suicide password:";

    // What the game says instead of asking, when it won't take the command.
    internal const string RefusedPrefix = "You may not suicide or reroll";

    private const string GateHold = "SuicideCommand";

    // The send gate, held from `suicide` going out until the wait ends. Null when
    // unwired.
    public EngineSendGate? SendGate { get; set; }

    // The telepath pacer, held over the same stretch (TelepathPacer.Hold). Null
    // when unwired.
    public TelepathPacer? Telepaths { get; set; }

    private bool _sendingOwn;

    private void SendOwn(string line)
    {
        _sendingOwn = true;
        try { _wireSender?.Invoke(Encoding.Latin1.GetBytes(line + "\r")); }
        finally { _sendingOwn = false; }
    }

    // The one way a wait ends, however it ends: answered, refused, timed out or
    // cancelled by another line. Both holds come down here.
    private void EndWait()
    {
        _promptAwaitedSince = null;
        _awaitingPrompt = false;
        SendGate?.Release(GateHold);
        Telepaths?.Release();
    }

    // Every line that reaches the wire, whoever sent it. The send gate keeps the
    // engines quiet during the wait, but not the user, a hang-up or the trainer
    // form: whatever goes out first is what the game reads at its prompt. The
    // wait ends there and the stored password is not sent after it.
    public void NoteLineSent(ReadOnlySpan<byte> data)
    {
        if (_promptAwaitedSince is null || _sendingOwn) return;
        if (data.IndexOfAny((byte)'\r', (byte)'\n') < 0) return;
        EndWait();
        FailPending("another line went out before the game asked for the password: the stored password was not sent",
            "suicide cancelled, another command went out first");
    }

    private void OnLineDispatched(Terminal.LineExtractor.EmittedLine line)
    {
        if (_promptAwaitedSince is null || line.IsChat) return;
        if (!line.Text.TrimStart().StartsWith(RefusedPrefix, StringComparison.Ordinal)) return;
        EndWait();
        FailPending($"the game refused the command (\"{line.Text.Trim()}\"): the password was not sent",
            "suicide refused by the game");
    }

    // Called about once a second by the client's own clock: the wait holds the
    // send gate, and a game that says nothing at all must not hold it for ever.
    public void CheckTimeout()
    {
        if (_promptAwaitedSince is { } since && _now() - since > PromptWait) GiveUpOnPrompt();
    }

    private void GiveUpOnPrompt()
    {
        EndWait();
        FailPending(
            $"the game did not ask for the suicide password within {PromptWait.TotalSeconds:0} s of `suicide`: the password was not sent",
            "suicide failed, the game did not ask for the password");
    }

    private void FailPending(string logLine, string replyText)
    {
        Action<string>? reply = _pendingReply;
        _pendingReply = null;
        _log?.Log(LogSeverity.Warn, "Suicide", "@suicide: " + logLine);
        // A refusal: nothing is sent with "warn sender on invalid / denied" off.
        reply?.Invoke(replyText);
    }

    private void OnInvalid()
    {
        // Only react if we have a pending @suicide invocation —
        // otherwise this Invalid line is from a manual `suicide`
        // attempt the user made themselves and isn't ours to
        // reply for.
        if (_pendingReply is null) return;
        Action<string> reply = _pendingReply;
        _pendingReply = null;
        // A refusal: nothing is sent with "warn sender on invalid / denied" off.
        reply("invalid suicide password is stored, unable");
    }

    // "You do not have a suicide password set." observed. While the pro window is
    // open we flag the observation; the OnPromptObserved path then fires suicide
    // on the next statline arrival. We don't act immediately because the line and
    // the prompt arrive in the same wire chunk and we want a single decision
    // point per pro round-trip.
    private void OnNotSetObserved()
    {
        if (_proPendingReply is null) return;
        _seenNotSetInProWindow = true;
    }

    // Next statline arrived after we sent pro — pro round-trip is complete. If
    // _seenNotSetInProWindow is true the realm confirmed no password set; send
    // the unprompted suicide. Otherwise the realm has a password set that we
    // don't have stored; log the mismatch + telepath the sender.
    private void OnPromptObserved(PromptObservation _)
    {
        // A statline is also the tick that ends a password wait nothing answered.
        if (_promptAwaitedSince is { } since && _now() - since > PromptWait) GiveUpOnPrompt();
        if (_proPendingReply is null) return;
        Action<string> reply = _proPendingReply;
        bool ok = _seenNotSetInProWindow;
        _proPendingReply = null;
        _seenNotSetInProWindow = false;

        if (ok)
        {
            _log?.Log(LogSeverity.Info, "Suicide",
                "Realm confirmed no suicide password set — sending unprompted `suicide`.");
            _wireSender?.Invoke(Encoding.Latin1.GetBytes("suicide\r"));
            return;
        }

        _log?.Log(LogSeverity.Warn, "Suicide",
            "Profile has no stored suicide password, but `pro` did not report "
            + "'You do not have a suicide password set.' before the next statline — "
            + "the realm has a password set that we don't have. Run `set suicide` "
            + "to capture it.");
        reply("Suicide failed, password set in game but not stored.");
    }
}
