using System.Text;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

/// <summary>
/// Consumer of <see cref="RemoteCommandManager"/> for the
/// <c>@relog</c> command (<c>HangupDisconnect</c> permission category).
/// Performs a graceful relog: arms <see cref="RelogSignal"/> and then
/// sends the configured <see cref="GameCommands.ExitCommand"/> (default
/// <c>=x</c>) so the BBS logs the character out cleanly and drops the
/// carrier. MainWindowViewModel's Disconnected handler sees the relog
/// intent and forces an unconditional dial-back, at which point the
/// normal login automation logs the character straight back in.
/// </summary>
/// <remarks>
/// <para>
/// Sibling of <see cref="HangupHandler"/> — same category, same wire
/// shape (exit command then carrier drop), opposite reconnect intent.
/// @hangup suppresses the reactive reconnect AND the next entry
/// automation (user reads the screen, types in manually). @relog forces
/// the reconnect and lets the login automation run, so the whole
/// round-trip is automatic. The graceful <c>=x</c> exit (rather than a
/// hard socket drop) is deliberate: it logs the character out of the
/// realm first, avoiding the in-world ghosting / "already playing"
/// prompt a raw carrier-loss would risk on the immediate reconnect.
/// </para>
/// <para>
/// No reply is sent — we're dropping the carrier, so a reply could not
/// reach the sender anyway (matches <see cref="HangupHandler"/>).
/// </para>
/// </remarks>
public sealed class RelogHandler : IDisposable
{
    private readonly RemoteCommandManager _engine;
    private readonly GameCommands _commands;
    private readonly RelogSignal _signal;
    private Action<byte[]>? _wireSender;
    private Func<bool>? _hangupsDisabled;
    private bool _disposed;

    public RelogHandler(RemoteCommandManager engine, GameCommands commands, RelogSignal signal)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(signal);
        _engine = engine;
        _commands = commands;
        _signal = signal;

        if (!RemoteCommandCatalog.TryGetCategory("@relog", out PlayerRemoteControls category))
            throw new InvalidOperationException("RemoteCommandCatalog missing entry for '@relog'.");
        _engine.RegisterHandler("@relog", category, OnRelog);
    }

    /// <summary>
    /// Bind the wire-sender — same shape as <see cref="HangupHandler"/>.
    /// MainWindowViewModel supplies <c>SendUserInput</c>. Without it the
    /// handler still authorises the @relog AND raises the
    /// <see cref="RelogSignal"/> intent, but produces no wire output —
    /// useful for tests.
    /// </summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    /// <summary>
    /// Bind the master "Disable hangups" check. When it returns
    /// <c>true</c>, <c>@relog</c> is a no-op — a relog is a carrier drop
    /// followed by a forced reconnect, so it counts as an automatic
    /// hangup the user has opted out of. Read live so a mid-session toggle
    /// takes effect immediately.
    /// </summary>
    public void SetHangupsDisabledCheck(Func<bool> disabled)
    {
        ArgumentNullException.ThrowIfNull(disabled);
        _hangupsDisabled = disabled;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterHandler("@relog");
    }

    private void OnRelog(RemoteCommandContext ctx)
    {
        // Master kill-switch: only an explicit local action may drop the
        // carrier. A relog is a drop-then-redial, so it's suppressed too —
        // silently, since this command never replies.
        if (_hangupsDisabled?.Invoke() ?? false) return;

        string command = _commands.ExitCommand;
        if (string.IsNullOrEmpty(command)) return;
        // Arm BEFORE the wire write so the Disconnected event handler
        // (which can race with the wire round-trip) sees the relog
        // intent and forces the dial-back regardless of the per-BBS
        // reconnect toggles. Set even with no wire-sender so a test or
        // pre-connection invocation still records the intent.
        _signal.SignalRelog();
        if (_wireSender is null) return;
        byte[] bytes = Encoding.Latin1.GetBytes(command + "\r");
        _wireSender(bytes);
    }
}
