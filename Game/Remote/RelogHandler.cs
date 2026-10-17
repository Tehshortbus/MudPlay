using System.Text;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// Consumer of RemoteCommandManager for the @relog command (HangupDisconnect
// permission category). Performs a graceful relog: arms RelogSignal and then
// sends the configured GameCommands.ExitCommand (default =x) so the BBS logs
// the character out cleanly and drops the carrier. MainWindowViewModel's
// Disconnected handler sees the relog intent and forces an unconditional
// dial-back, at which point the normal login automation logs the character
// straight back in.
//
// Sibling of HangupHandler — same category, same wire shape (exit command then
// carrier drop), opposite reconnect intent. @hangup suppresses the reactive
// reconnect AND the next entry automation (user reads the screen, types in
// manually). @relog forces the reconnect and lets the login automation run, so
// the whole round-trip is automatic. The graceful =x exit (rather than a hard
// socket drop) is deliberate: it logs the character out of the realm first,
// avoiding the in-world ghosting / "already playing" prompt a raw carrier-loss
// would risk on the immediate reconnect.
//
// No reply is sent — we're dropping the carrier, so a reply could not reach the
// sender anyway (matches HangupHandler).
public sealed class RelogHandler : IDisposable
{
    private readonly RemoteCommandManager _engine;
    private readonly GameCommands _commands;
    private readonly RelogSignal _signal;
    private Action<byte[]>? _wireSender;
    private Action? _logHangupPenalty;
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

    // Bind the wire-sender — same shape as HangupHandler. MainWindowViewModel
    // supplies SendUserInput. Without it the handler still authorises the @relog
    // AND raises the RelogSignal intent, but produces no wire output.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Bind the log line that says what the realm's hang-up penalty (Settings →
    // BBS) makes of the @relog just sent. It reports and nothing more.
    public void SetHangupPenaltyLog(Action logHangupPenalty)
    {
        ArgumentNullException.ThrowIfNull(logHangupPenalty);
        _logHangupPenalty = logHangupPenalty;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterHandler("@relog");
    }

    private void OnRelog(RemoteCommandContext ctx)
    {
        string command = _commands.ExitCommand;
        if (string.IsNullOrEmpty(command)) return;
        // Arm BEFORE the wire write so the Disconnected event handler
        // (which can race with the wire round-trip) sees the relog
        // intent and forces the dial-back regardless of the per-BBS
        // reconnect toggles. Set even with no wire-sender so a
        // pre-connection invocation still records the intent.
        _signal.SignalRelog();
        if (_wireSender is null) return;
        byte[] bytes = Encoding.Latin1.GetBytes(command + "\r");
        _wireSender(bytes);
        _logHangupPenalty?.Invoke();
    }
}
