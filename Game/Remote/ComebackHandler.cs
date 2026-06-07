using FujinTerm.Game.Recovery;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

/// <summary>
/// Phase 9 Cluster 5d — handler for the <c>@comeback</c> remote
/// command. A party member sends <c>@comeback</c> after we've died
/// to request that we resume / rejoin the party; this handler
/// forwards the request to
/// <see cref="DeathRecoveryManager.RequestComeback"/>.
/// </summary>
/// <remarks>
/// <para>
/// v1 fires the <see cref="DeathRecoveryManager.ComebackRequested"/>
/// event with the sender's name as the requester. Subscribers (the
/// future walker-reroute + invite sequence) drive the actual
/// resume; v1 just signals.
/// </para>
/// <para>
/// Catalog permission tier: <c>None</c> — every active party member
/// can request a comeback, matching the social baseline of party
/// play. Non-party senders fall through the engine's party
/// whitelist gate without ever reaching this handler.
/// </para>
/// </remarks>
public sealed class ComebackHandler : IDisposable
{
    private const string LogCategory = "RemoteCmd";

    private readonly RemoteCommandManager _engine;
    private readonly DeathRecoveryManager _recovery;
    private readonly LogService? _log;
    private bool _disposed;

    public ComebackHandler(
        RemoteCommandManager engine,
        DeathRecoveryManager recovery,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(recovery);
        _engine = engine;
        _recovery = recovery;
        _log = log;

        if (!RemoteCommandCatalog.TryGetCategory("@comeback", out Models.GameData.PlayerRemoteControls category))
            throw new InvalidOperationException("RemoteCommandCatalog missing entry for '@comeback'.");
        _engine.RegisterHandler("@comeback", category, OnComeback);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterHandler("@comeback");
    }

    private void OnComeback(RemoteCommandContext ctx)
    {
        _log?.Log(LogSeverity.Info, LogCategory,
            $"@comeback from {ctx.Sender}");
        _recovery.RequestComeback(ctx.Sender);
        ctx.Reply("ok");
    }
}
