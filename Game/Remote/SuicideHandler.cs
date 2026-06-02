using System.Text;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Remote;

/// <summary>
/// Consumer of <see cref="RemoteCommandManager"/> for the
/// <c>@suicide</c> remote command. Authorised callers — players with
/// the <see cref="PlayerRemoteControls.SysopCommands"/> ("Elevated
/// Commands") flag — can request the local character commit suicide;
/// the engine's lives-based hard-block already protects against
/// destructive misuse below <see cref="RemoteCommandManager.MaxSuicideLivesThreshold"/>.
/// </summary>
/// <remarks>
/// <para>
/// Wire flow once authorisation + lives gates pass:
/// </para>
/// <list type="number">
///   <item>Send <c>suicide\r</c>.</item>
///   <item>If the profile carries
///         <see cref="Models.Profile.CharacterProfile.EncryptedSuicidePassword"/>,
///         send the decrypted password as the next line so the
///         realm's "Enter your suicide password:" prompt is
///         consumed.</item>
///   <item>If the server responds with
///         "Invalid password specified." — telepath the original
///         sender <c>{invalid suicide password is stored, unable}</c>
///         so they know to refresh the stored password.</item>
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
    private readonly IDisposable _invalidSub;
    private Action<byte[]>? _wireSender;
    private bool _disposed;

    /// <summary>
    /// Replay callback for the last @suicide invocation, captured at
    /// dispatch time and consumed by the invalid-password line if it
    /// fires. <c>null</c> when no invocation is pending.
    /// </summary>
    private Action<string>? _pendingReply;

    public SuicideHandler(
        RemoteCommandManager engine,
        MessageRouter router,
        ProfileService profile,
        PasswordProtector protector)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(protector);
        _engine    = engine;
        _profile   = profile;
        _protector = protector;

        if (!RemoteCommandCatalog.TryGetCategory("@suicide", out PlayerRemoteControls category))
            throw new InvalidOperationException("RemoteCommandCatalog missing entry for '@suicide'.");
        _engine.RegisterHandler("@suicide", category, OnSuicide);

        _invalidSub = router.Subscribe(KnownPatterns.SuicideInvalidPassword, _ => OnInvalid());
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (string cmd in RegisteredCommands) _engine.UnregisterHandler(cmd);
        _invalidSub.Dispose();
    }

    private void OnSuicide(RemoteCommandContext ctx)
    {
        if (_wireSender is null) return;

        // Always send the suicide command — works fine on realms with
        // no password set (executes immediately). On realms with a
        // password set, the server will prompt
        // "Enter your suicide password:"; the second send below
        // consumes that prompt with the stored password.
        _wireSender(Encoding.Latin1.GetBytes("suicide\r"));

        string? password = null;
        if (_profile.Current is { EncryptedSuicidePassword: { Length: > 0 } blob })
            password = _protector.Unprotect(blob);

        if (!string.IsNullOrEmpty(password))
        {
            _pendingReply = ctx.Reply;
            _wireSender(Encoding.Latin1.GetBytes(password + "\r"));
        }
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
        reply("invalid suicide password is stored, unable");
    }
}
