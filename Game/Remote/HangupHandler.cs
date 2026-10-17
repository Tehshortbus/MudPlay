using System.Text;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// The HangupDisconnect permission category. Currently registers only the
// @hangup handler — raises the HangupSignal "intentional hangup" intent and
// sends the configured GameCommands.ExitCommand (default =x) to the wire when an
// authorised sender requests it.
//
// Sender authorisation is handled by the engine via RemoteCommandCatalog — only
// players whose RemoteControls includes HangupDisconnect can fire this handler.
// Default deny for unknown players.
//
// The SignalHangup call raises both the disconnect-intent and entry-suppression
// flags BEFORE the wire command lands, so MainWindowViewModel's Disconnected
// handler classifies the drop as HangupInitiated (no reactive auto-reconnect)
// and MainMenuEntryAutomation skips arming the entry latch on the next manual
// reconnect — user reads what's on the screen and types their entry command
// themselves. The post-entry stat / i refresh stays off the wire too, since it
// only fires alongside the auto-entry. HealthManager's low-HP hangup raises the
// same signal.
public sealed class HangupHandler : IDisposable
{
    private static readonly string[] RegisteredCommands = { "@hangup" };

    private readonly RemoteCommandManager _engine;
    private readonly GameCommands _commands;
    private readonly HangupSignal _signal;
    private Action<byte[]>? _wireSender;
    private Action? _logHangupPenalty;
    private bool _disposed;

    public HangupHandler(RemoteCommandManager engine, GameCommands commands, HangupSignal signal)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(signal);
        _engine = engine;
        _commands = commands;
        _signal = signal;

        if (!RemoteCommandCatalog.TryGetCategory("@hangup", out PlayerRemoteControls category))
            throw new InvalidOperationException("RemoteCommandCatalog missing entry for '@hangup'.");
        _engine.RegisterHandler("@hangup", category, OnHangup);
    }

    // Bind the wire-sender — same shape as PartyEssentialHandlers.
    // MainWindowViewModel supplies SendUserInput. Without it the handler still
    // authorises the @hangup AND raises the HangupSignal intent flags, but
    // produces no wire output.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Bind the log line that says what the realm's hang-up penalty (Settings →
    // BBS) makes of the @hangup just sent. It reports and nothing more.
    public void SetHangupPenaltyLog(Action logHangupPenalty)
    {
        ArgumentNullException.ThrowIfNull(logHangupPenalty);
        _logHangupPenalty = logHangupPenalty;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (string cmd in RegisteredCommands) _engine.UnregisterHandler(cmd);
    }

    private void OnHangup(RemoteCommandContext ctx)
    {
        string command = _commands.ExitCommand;
        if (string.IsNullOrEmpty(command)) return;
        // Raise the signal BEFORE the wire write so the Disconnected
        // event handler (which can race with the wire round-trip) sees
        // the intent flag and classifies the drop as HangupInitiated.
        // Set even when there's no wire-sender so a
        // pre-connection invocation still records the intent.
        _signal.SignalHangup();
        if (_wireSender is null) return;
        byte[] bytes = Encoding.Latin1.GetBytes(command + "\r");
        _wireSender(bytes);
        _logHangupPenalty?.Invoke();
    }
}
