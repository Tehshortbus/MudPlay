using System.Text;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

/// <summary>
/// Consumer of <see cref="RemoteCommandManager"/> for the
/// <c>HangupDisconnect</c> permission category. Currently registers
/// only the <c>@hangup</c> handler — sends the configured
/// <see cref="GameCommands.ExitCommand"/> (default <c>=x</c>) to the
/// wire when an authorised sender requests it.
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
/// The full cleanup-warning automation flow (X → wait 6–8s → exit) and
/// the matching first-session / post-cleanup re-login flow (which sends
/// <see cref="GameCommands.EntryCommand"/>) ship in a follow-up PR
/// once a small scheduler + main-menu detection pattern exist. This
/// handler is the direct-from-remote shortcut: if a trusted player
/// telepaths <c>@hangup</c>, send the exit command immediately and
/// let the BBS handle the rest.
/// </para>
/// </remarks>
public sealed class HangupHandler : IDisposable
{
    private static readonly string[] RegisteredCommands = { "@hangup" };

    private readonly RemoteCommandManager _engine;
    private readonly GameCommands _commands;
    private Action<byte[]>? _wireSender;
    private bool _disposed;

    public HangupHandler(RemoteCommandManager engine, GameCommands commands)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(commands);
        _engine = engine;
        _commands = commands;

        if (!RemoteCommandCatalog.TryGetCategory("@hangup", out PlayerRemoteControls category))
            throw new InvalidOperationException("RemoteCommandCatalog missing entry for '@hangup'.");
        _engine.RegisterHandler("@hangup", category, OnHangup);
    }

    /// <summary>
    /// Bind the wire-sender — same shape as PartyEssentialHandlers.
    /// MainWindowViewModel supplies <c>SendUserInput</c>. Without it
    /// the handler still authorises and acknowledges the @hangup but
    /// produces no actual wire output.
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
        if (_wireSender is null) return;
        string command = _commands.ExitCommand;
        if (string.IsNullOrEmpty(command)) return;
        byte[] bytes = Encoding.Latin1.GetBytes(command + "\r");
        _wireSender(bytes);
    }
}
