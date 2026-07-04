using System.Text;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

// The @do <command> passthrough — the highest-trust verb in the @-command
// catalogue. The sender's args are joined back into the original command string
// and shipped on the wire as if the local user had typed them.
//
// Sender authorisation is handled by the engine via RemoteCommandCatalog — @do
// requires PlayerRemoteControls.ExecuteCommands, which the Players-tab tooltip
// flags as "do something on my behalf". Default deny for any player without an
// explicit grant; recommend granting only to known trusted players (party leader,
// sysop-ranked teammates, etc.).
//
// Hard-blocks land at engine level before this handler runs:
//   - @do reroll is always denied (the reroll token match in IsHardBlocked
//     catches command + args).
//   - @do suicide is gated by LivesProvider through GetSuicidePolicyBlockReply —
//     denied when remaining lives ≤ MaxSuicideLivesThreshold.
//
// Wire-sender is bound to the gate-wrapped sender (same as the rest of the
// engine-side handlers) so a malicious caller can't slip bytes onto the wire
// while EngineSendGate is locked during a suicide-password entry prompt.
public sealed class DoHandler : IDisposable
{
    private static readonly string[] RegisteredCommands = { "@do" };

    private readonly RemoteCommandManager _engine;
    private readonly LogService? _log;
    private Action<byte[]>? _wireSender;
    private bool _disposed;

    public DoHandler(RemoteCommandManager engine, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _log = log;

        if (!RemoteCommandCatalog.TryGetCategory("@do", out PlayerRemoteControls category))
            throw new InvalidOperationException("RemoteCommandCatalog missing entry for '@do'.");
        _engine.RegisterHandler("@do", category, OnDo);
    }

    // Bind the wire-sender — same shape as PartyEssentialHandlers.
    // MainWindowViewModel supplies the gate-wrapped SendUserInput.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Test seam — most recent bytes the handler asked to write.
    internal List<byte[]> LastSentForTests { get; } = new();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (string cmd in RegisteredCommands) _engine.UnregisterHandler(cmd);
    }

    private void OnDo(RemoteCommandContext ctx)
    {
        if (ctx.Args.Count == 0) return;
        if (_wireSender is null) return;
        // Re-join the args with single spaces. The engine tokenised on
        // whitespace + RemoveEmptyEntries, so multi-space sequences in
        // the original message collapse to one space here. That's fine
        // for MUD commands (any modern MUD parses ' '+ between tokens
        // identically), and matters less than the safety of NOT
        // round-tripping arbitrary bytes from a remote source.
        string command = string.Join(" ", ctx.Args);
        byte[] bytes = Encoding.Latin1.GetBytes(command + "\r");
        LastSentForTests.Add(bytes);
        _wireSender(bytes);
        _log?.Log(LogSeverity.Info, "RemoteCmd",
            $"@do from {ctx.Sender}: '{command}'");
        // Acknowledge the request — sender gets {ok} on the same
        // channel they used, mirroring the existing curly-brace
        // meta-line convention every other handler reply uses. Lets
        // the sender know the command landed (not just that they got
        // permission to send it).
        ctx.Reply("ok");
    }
}
