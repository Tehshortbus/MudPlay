using System.Text;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

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
// themselves. The post-entry stat/exp/i refresh stays off the wire too, since it
// only fires alongside the auto-entry. Future hang-up-if-naked / hang-up-if-low-HP
// automation reuses the same signal pattern.
public sealed class HangupHandler : IDisposable
{
    private static readonly string[] RegisteredCommands = { "@hangup" };

    private readonly RemoteCommandManager _engine;
    private readonly GameCommands _commands;
    private readonly HangupSignal _signal;
    private Action<byte[]>? _wireSender;
    private Func<bool>? _hangupsDisabled;
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
    // produces no actual wire output — useful for tests.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Bind the master "Disable hangups" check. When it returns true, @hangup is a
    // no-op — the user has declared the client may only disconnect on an explicit
    // local action, so a remote sender can't drop the carrier. Read live so a
    // mid-session toggle takes effect immediately.
    public void SetHangupsDisabledCheck(Func<bool> disabled)
    {
        ArgumentNullException.ThrowIfNull(disabled);
        _hangupsDisabled = disabled;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (string cmd in RegisteredCommands) _engine.UnregisterHandler(cmd);
    }

    private void OnHangup(RemoteCommandContext ctx)
    {
        // Master kill-switch: the user has declared only an explicit local
        // action may disconnect. Drop the remote @hangup silently — no
        // signal, no wire write. (No reply either: this command never
        // replies, and the carrier stays up regardless.)
        if (_hangupsDisabled?.Invoke() ?? false) return;

        string command = _commands.ExitCommand;
        if (string.IsNullOrEmpty(command)) return;
        // Raise the signal BEFORE the wire write so the Disconnected
        // event handler (which can race with the wire round-trip) sees
        // the intent flag and classifies the drop as HangupInitiated.
        // Set even when there's no wire-sender so a test or
        // pre-connection invocation still records the intent.
        _signal.SignalHangup();
        if (_wireSender is null) return;
        byte[] bytes = Encoding.Latin1.GetBytes(command + "\r");
        _wireSender(bytes);
    }
}
