using System.Text;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

/// <summary>
/// Consumer of <see cref="RemoteCommandManager"/> for the
/// <c>HangupDisconnect</c> permission category. Currently registers
/// only the <c>@hangup</c> handler — raises the
/// <see cref="HangupSignal"/> "intentional hangup" intent and sends
/// the configured <see cref="GameCommands.ExitCommand"/> (default
/// <c>=x</c>) to the wire when an authorised sender requests it.
/// </summary>
/// <remarks>
/// <para>
/// Sender authorisation is handled by the engine via
/// <see cref="RemoteCommandCatalog"/> — only players whose
/// <see cref="PlayerCustomization.RemoteControls"/> includes
/// <see cref="PlayerRemoteControls.HangupDisconnect"/> can fire this
/// handler. Default deny for unknown players.
/// </para>
/// <para>
/// The <see cref="HangupSignal.SignalHangup"/> call raises both the
/// disconnect-intent and entry-suppression flags BEFORE the wire
/// command lands, so MainWindowViewModel's Disconnected handler
/// classifies the drop as <c>HangupInitiated</c> (no reactive
/// auto-reconnect) and MainMenuEntryAutomation skips arming the
/// entry latch on the next manual reconnect — user reads what's on
/// the screen and types their entry command themselves. The
/// post-entry <c>stat</c>/<c>exp</c>/<c>i</c> refresh stays off the
/// wire too, since it only fires alongside the auto-entry. Future
/// hang-up-if-naked / hang-up-if-low-HP automation reuses the same
/// signal pattern.
/// </para>
/// </remarks>
public sealed class HangupHandler : IDisposable
{
    private static readonly string[] RegisteredCommands = { "@hangup" };

    private readonly RemoteCommandManager _engine;
    private readonly GameCommands _commands;
    private readonly HangupSignal _signal;
    private Action<byte[]>? _wireSender;
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

    /// <summary>
    /// Bind the wire-sender — same shape as PartyEssentialHandlers.
    /// MainWindowViewModel supplies <c>SendUserInput</c>. Without it
    /// the handler still authorises the @hangup AND raises the
    /// HangupSignal intent flags, but produces no actual wire
    /// output — useful for tests.
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
    }

    private void OnHangup(RemoteCommandContext ctx)
    {
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
