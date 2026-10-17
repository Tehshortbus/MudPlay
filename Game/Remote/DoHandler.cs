using System.Text;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// The @do <command> passthrough — the highest-trust verb in the @-command
// catalogue. The sender's args are joined back into the original command string
// and shipped on the wire as if the local user had typed them.
//
// Sender authorisation is handled by the engine via RemoteCommandCatalog — @do
// requires PlayerRemoteControls.ExecuteCommands (the "Execute commands" box in the
// player edit dialog). Default deny for any player without an
// explicit grant; recommend granting only to known trusted players (party leader,
// sysop-ranked teammates, etc.).
//
// Hard-blocks land at engine level before this handler runs:
//   - @do reroll is always denied (the reroll token match in IsHardBlocked
//     catches command + args).
//   - @do with the word `suicide` among what it would send is always refused,
//     with a pointer at @suicide (GetForcedSuicideRedirectReply), once the
//     sender's permission has been checked.
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

    // Who asked for the command going out right now, while it is being sent; null
    // otherwise. The send is the gate-wrapped one, so to an outbound observer the
    // line is the client's; a `break` asked for this way holds the combat engine's
    // attack as the user's own does (user, 2026-10-10), and the observer that reads
    // it needs to know the line is one of these, and whose. Read at the wire, so a
    // line the gate dropped holds nothing.
    public string? SendingFor { get; private set; }

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
        // round-tripping arbitrary bytes from a remote source. For the same reason
        // nothing outside plain ASCII goes out as given (WireText).
        string command = WireText.Ascii(string.Join(" ", ctx.Args));
        byte[] bytes = Encoding.Latin1.GetBytes(command + "\r");
        SendingFor = ctx.Sender;
        try { _wireSender(bytes); }
        finally { SendingFor = null; }
        _log?.Log(LogSeverity.Info, "RemoteCmd",
            $"@do from {ctx.Sender}: '{Recovery.CorpseRecoveryLines.Redact(command)}'");
        // Acknowledge the request — sender gets {ok} on the same
        // channel they used, mirroring the existing curly-brace
        // meta-line convention every other handler reply uses. Lets
        // the sender know the command landed (not just that they got
        // permission to send it).
        ctx.Reply("ok");
    }
}
