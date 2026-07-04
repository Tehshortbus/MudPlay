using System.Text;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Remote;

// Consumer of RemoteCommandManager for the @suicide remote command. Authorised
// callers — players with the PlayerRemoteControls.SysopCommands ("Elevated
// Commands") flag — can request the local character commit suicide; the engine's
// lives-based policy block already protects against destructive misuse below
// RemoteCommandManager.MaxSuicideLivesThreshold.
//
// Two wire flows, picked based on whether the loaded profile carries a stored
// encrypted suicide password:
//   - Have stored password — send suicide\r, then the decrypted password as the
//     next line. Realm prompts "Enter your suicide password:" and our second
//     send consumes it. "Invalid password specified." → telepath the sender so
//     they know the stored value is stale.
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

    // Reply callback for the last @suicide invocation that took the
    // have-stored-password branch, captured at dispatch time and consumed by the
    // invalid-password line if it fires.
    private Action<string>? _pendingReply;

    // Reply callback + window state for an in-flight no-stored-password pro
    // pre-check. _seenNotSetInProWindow flips true when the canonical "not set"
    // line fires during the window; the next PromptObserved firing decides the
    // outcome based on that flag.
    private Action<string>? _proPendingReply;
    private bool _seenNotSetInProWindow;

    public SuicideHandler(
        RemoteCommandManager engine,
        MessageRouter router,
        ProfileService profile,
        PasswordProtector protector,
        WirePromptScanner promptScanner,
        LogService? log = null)
    {
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

    // Test seam — fire the prompt-observation hook without feeding the scanner
    // real bytes.
    internal void FireNextPromptForTests() => OnPromptObserved(default);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (string cmd in RegisteredCommands) _engine.UnregisterHandler(cmd);
        _invalidSub.Dispose();
        _notSetSub.Dispose();
        _promptScanner.PromptObserved -= _promptHandler;
    }

    private void OnSuicide(RemoteCommandContext ctx)
    {
        if (_wireSender is null) return;

        string? password = null;
        if (_profile.Current is { EncryptedSuicidePassword: { Length: > 0 } blob })
            password = _protector.Unprotect(blob);

        if (!string.IsNullOrEmpty(password))
        {
            // Have-stored-password branch — send the command + the
            // password back-to-back. The realm's prompt is consumed
            // by the second line; Invalid → telepath sender.
            _pendingReply = ctx.Reply;
            _wireSender(Encoding.Latin1.GetBytes("suicide\r"));
            _wireSender(Encoding.Latin1.GetBytes(password + "\r"));
            return;
        }

        // No stored password — pre-check via `pro` to disambiguate
        // realm-has-no-password (safe to suicide) from realm-has-
        // password-we-don't (mismatch, refuse + warn).
        if (_proPendingReply is not null)
        {
            // Another @suicide is already in the pre-check window —
            // refuse defensively rather than queueing.
            if (_engine.WarnOnDenial)
                ctx.Reply("@suicide already in-flight, try again shortly");
            return;
        }
        _proPendingReply = ctx.Reply;
        _seenNotSetInProWindow = false;
        _wireSender(Encoding.Latin1.GetBytes("pro\r"));
        _log?.Log(LogSeverity.Info, "Suicide",
            "@suicide with no stored password — running `pro`; will decide on next statline.");
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
        // Gate the failure reply on the engine's WarnOnDenial flag,
        // same policy the engine's own denial paths obey: when the
        // user has unchecked "warn sender on invalid / denied",
        // failure responses are suppressed regardless of how
        // specific the reason is.
        if (!_engine.WarnOnDenial) return;
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
        if (!_engine.WarnOnDenial) return;
        reply("Suicide failed, password set in game but not stored.");
    }
}
