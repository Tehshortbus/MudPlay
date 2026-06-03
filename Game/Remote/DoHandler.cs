using System.Text;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

/// <summary>
/// Consumer of <see cref="RemoteCommandManager"/> for the
/// <c>@do &lt;command&gt;</c> passthrough — the highest-trust verb in
/// the @-command catalogue. The sender's args are joined back into
/// the original command string and shipped on the wire as if the
/// local user had typed them.
/// </summary>
/// <remarks>
/// <para>
/// Sender authorisation is handled by the engine via
/// <see cref="RemoteCommandCatalog"/> — <c>@do</c> requires
/// <see cref="PlayerRemoteControls.ExecuteCommands"/>, which the
/// Players-tab tooltip flags as "do something on my behalf". Default
/// deny for any player without an explicit grant; recommend granting
/// only to known trusted players (party leader, sysop-ranked
/// teammates, etc.).
/// </para>
/// <para>
/// Hard-blocks land at engine level before this handler runs:
/// <list type="bullet">
///   <item><c>@do reroll</c> is always denied (the reroll token
///         match in <see cref="RemoteCommandManager.IsHardBlocked"/>
///         catches command + args).</item>
///   <item><c>@do suicide</c> is gated by
///         <see cref="RemoteCommandManager.LivesProvider"/> through
///         <see cref="RemoteCommandManager.GetSuicidePolicyBlockReply"/> —
///         denied when remaining lives ≤
///         <see cref="RemoteCommandManager.MaxSuicideLivesThreshold"/>.</item>
/// </list>
/// </para>
/// <para>
/// Wire-sender is bound to the gate-wrapped sender (same as the rest
/// of the engine-side handlers) so a malicious caller can't slip
/// bytes onto the wire while
/// <see cref="EngineSendGate"/> is locked during a suicide-password
/// entry prompt.
/// </para>
/// </remarks>
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

    /// <summary>
    /// Bind the wire-sender — same shape as PartyEssentialHandlers.
    /// MainWindowViewModel supplies the gate-wrapped <c>SendUserInput</c>.
    /// </summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    /// <summary>Test seam — most recent bytes the handler asked to write.</summary>
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
        // Re-join the args with single spaces. The engine tokenised on
        // whitespace + RemoveEmptyEntries, so multi-space sequences in
        // the original message collapse to one space here. That's fine
        // for MUD commands (any modern MUD parses ' '+ between tokens
        // identically), and matters less than the safety of NOT
        // round-tripping arbitrary bytes from a remote source.
        string command = string.Join(" ", ctx.Args);
        byte[] bytes = Encoding.Latin1.GetBytes(command + "\r");
        LastSentForTests.Add(bytes);
        _wireSender?.Invoke(bytes);
        _log?.Log(LogSeverity.Info, "RemoteCmd",
            $"@do from {ctx.Sender}: '{command}'");
    }
}
