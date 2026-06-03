using System.Text;
using Avalonia.Threading;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Remote;

/// <summary>
/// Consumer of <see cref="RemoteCommandManager"/> for the
/// <c>@suicide</c> remote command. Authorised callers — players with
/// the <see cref="PlayerRemoteControls.SysopCommands"/> ("Elevated
/// Commands") flag — can request the local character commit suicide;
/// the engine's lives-based policy block already protects against
/// destructive misuse below <see cref="RemoteCommandManager.MaxSuicideLivesThreshold"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two wire flows, picked based on whether the loaded profile carries
/// a stored encrypted suicide password:
/// </para>
/// <list type="bullet">
///   <item><b>Have stored password</b> — send <c>suicide\r</c>, then
///         the decrypted password as the next line. Realm prompts
///         "Enter your suicide password:" and our second send
///         consumes it. <c>Invalid password specified.</c> →
///         telepath the sender so they know the stored value is
///         stale.</item>
///   <item><b>No stored password</b> — can't blindly send
///         <c>suicide</c> because if the realm actually has a
///         password set we'd hang at the prompt with no way to
///         answer. Pre-check via <c>pro</c>: if the realm replies
///         <c>"You do not have a suicide password set."</c> within
///         <see cref="ProCheckWindow"/>, suicide is unprompted on
///         the realm side and we just send <c>suicide\r</c> (kills
///         immediately). If the line doesn't fire within the
///         window, the realm has a password we don't have stored —
///         log the profile/realm mismatch + telepath the sender to
///         run <c>set suicide</c>.</item>
/// </list>
/// <para>
/// Bypasses <see cref="EngineSendGate"/> deliberately — we're the
/// flow's initiator, not a victim of it. The wire-sender bound by
/// MainWindowViewModel here is the RAW <c>SendUserInput</c>, not the
/// gate-wrapped one every other engine receives.
/// </para>
/// </remarks>
public sealed class SuicideHandler : IDisposable
{
    private static readonly string[] RegisteredCommands = { "@suicide" };

    private readonly RemoteCommandManager _engine;
    private readonly ProfileService _profile;
    private readonly PasswordProtector _protector;
    private readonly LogService? _log;
    private readonly IDisposable _invalidSub;
    private readonly IDisposable _notSetSub;
    private Action<byte[]>? _wireSender;
    private bool _disposed;

    /// <summary>
    /// Reply callback for the last @suicide invocation that took the
    /// have-stored-password branch, captured at dispatch time and
    /// consumed by the invalid-password line if it fires.
    /// </summary>
    private Action<string>? _pendingReply;

    /// <summary>State for an in-flight no-stored-password pro pre-check.</summary>
    private ProCheckState? _proCheck;
    private DispatcherTimer? _proCheckTimer;

    /// <summary>
    /// How long we wait for the <c>"You do not have a suicide password
    /// set."</c> line after sending <c>pro</c> before deciding the
    /// realm DOES have a password set (and we don't have it stored).
    /// Default 5 s — pro replies arrive within a second on a normal
    /// connection; the extra slack covers laggy realms and partial-
    /// chunk delivery.
    /// </summary>
    public TimeSpan ProCheckWindow { get; set; } = TimeSpan.FromSeconds(5);

    public SuicideHandler(
        RemoteCommandManager engine,
        MessageRouter router,
        ProfileService profile,
        PasswordProtector protector,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(protector);
        _engine    = engine;
        _profile   = profile;
        _protector = protector;
        _log       = log;

        if (!RemoteCommandCatalog.TryGetCategory("@suicide", out PlayerRemoteControls category))
            throw new InvalidOperationException("RemoteCommandCatalog missing entry for '@suicide'.");
        _engine.RegisterHandler("@suicide", category, OnSuicide);

        _invalidSub = router.Subscribe(KnownPatterns.SuicideInvalidPassword, _ => OnInvalid());
        // SuicideNotSet doubles as the no-stored-password pre-check
        // confirmation: if we sent `pro` and this fires inside the
        // window, suicide on this realm is unprompted and we can fire
        // it immediately. SuicidePasswordTracker also subscribes to
        // this pattern (to wipe stored values when the realm's view
        // disagrees with ours); both fire independently, no
        // interaction.
        _notSetSub = router.Subscribe(KnownPatterns.SuicideNotSet, _ => OnNotSetObserved());
    }

    /// <summary>
    /// Bind the RAW wire-sender (NOT the gate-wrapped one). Every
    /// other engine in the app uses the wrapped sender so the
    /// SuicidePasswordTracker can pause them mid-flow; this handler
    /// is the exception because it OWNS the flow and needs its sends
    /// to land even while the gate is locked.
    /// </summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    /// <summary>Test seam — manually fire the pro-check timeout without a real timer.</summary>
    internal void TimeoutProCheckForTests() => OnProCheckTimeout();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (string cmd in RegisteredCommands) _engine.UnregisterHandler(cmd);
        _invalidSub.Dispose();
        _notSetSub.Dispose();
        StopProCheckTimer();
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
        if (_proCheck is not null)
        {
            // Another @suicide is already in the pre-check window —
            // refuse defensively rather than queueing.
            if (_engine.WarnOnDenial)
                ctx.Reply("@suicide already in-flight, try again shortly");
            return;
        }
        _proCheck = new ProCheckState(ctx.Reply);
        _wireSender(Encoding.Latin1.GetBytes("pro\r"));
        _log?.Log(LogSeverity.Info, "Suicide",
            "@suicide with no stored password — running `pro` to check realm state.");
        StartProCheckTimer();
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

    /// <summary>
    /// "You do not have a suicide password set." observed. Two paths:
    /// either we're in the pro pre-check window (suicide is unprompted
    /// on this realm, fire it) OR the user typed <c>pro</c> manually
    /// (no @suicide pending, ignore).
    /// </summary>
    private void OnNotSetObserved()
    {
        if (_proCheck is null) return;
        ProCheckState state = _proCheck;
        _proCheck = null;
        StopProCheckTimer();
        _log?.Log(LogSeverity.Info, "Suicide",
            "Realm confirmed no suicide password set — sending unprompted `suicide`.");
        _wireSender?.Invoke(Encoding.Latin1.GetBytes("suicide\r"));
        _ = state;  // reply not needed on success — realm will kill us
    }

    /// <summary>
    /// pro pre-check window expired without
    /// <see cref="KnownPatterns.SuicideNotSet"/> firing — the realm
    /// has a suicide password set that we don't have stored. Mismatch
    /// case: log a warning so the user sees it in the LogPane, and
    /// telepath the sender so they know to ask the local user to
    /// re-run <c>set suicide</c>.
    /// </summary>
    private void OnProCheckTimeout()
    {
        if (_proCheck is null) return;
        ProCheckState state = _proCheck;
        _proCheck = null;
        StopProCheckTimer();
        _log?.Log(LogSeverity.Warn, "Suicide",
            "Profile has no stored suicide password, but `pro` did not report "
            + "'You do not have a suicide password set.' within the check window — "
            + "the realm has a password set that we don't have. Run `set suicide` "
            + "to capture it.");
        if (!_engine.WarnOnDenial) return;
        state.Reply("@suicide: no stored password but realm has one set (run `set suicide` to capture)");
    }

    // ----- Timer plumbing ------------------------------------------------

    private void StartProCheckTimer()
    {
        StopProCheckTimer();
        _proCheckTimer = new DispatcherTimer(
            interval: ProCheckWindow,
            priority: DispatcherPriority.Background,
            callback: (_, _) => OnProCheckTimeout());
        _proCheckTimer.Start();
    }

    private void StopProCheckTimer()
    {
        _proCheckTimer?.Stop();
        _proCheckTimer = null;
    }

    /// <summary>
    /// State for an in-flight no-stored-password pro pre-check.
    /// <see cref="Reply"/> is the channel-bound callback the engine
    /// captured at OnSuicide dispatch time so the mismatch reply
    /// lands back on the same channel the @suicide arrived on.
    /// </summary>
    private sealed record ProCheckState(Action<string> Reply);
}
