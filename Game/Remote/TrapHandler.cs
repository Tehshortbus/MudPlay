using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

// Consumer of RemoteCommandManager for @trap <direction> and @trap stop. The
// actual search → disarm state machine lives in TrapDisarmManager; this handler
// is the auth + queue boundary.
//
// Two responsibilities:
//   1. Direction parsing — normalise the sender's arg
//      (n / north / ne / northeast / ...) into the canonical short form before
//      queueing.
//   2. Channel-aware soft-gate on TrapDisarmManager.CanDisarm:
//        - Telepath / Gangpath + no skill → reply {can't disarm — no Traps
//          skill} so the sender knows we're not the right target. Gated on
//          RemoteCommandManager.WarnOnDenial.
//        - Local (Say) + no skill → silent. Broadcast channels reach every
//          player in the room; only trap-skilled characters should answer, and a
//          chorus of "{can't disarm}" replies from everyone else would be noise.
//
// Hard-blocks (reroll, suicide-lives) don't apply to @trap — the destructive
// verbs aren't in the wire path here. The PlayerRemoteControls.ExecuteCommands
// tier check on the engine side gates who can ask at all.
public sealed class TrapHandler : IDisposable
{
    private static readonly string[] RegisteredCommands = { "@trap" };

    private readonly RemoteCommandManager _engine;
    private readonly TrapDisarmManager _manager;
    private bool _disposed;

    public TrapHandler(RemoteCommandManager engine, TrapDisarmManager manager)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(manager);
        _engine = engine;
        _manager = manager;

        if (!RemoteCommandCatalog.TryGetCategory("@trap", out PlayerRemoteControls category))
            throw new InvalidOperationException("RemoteCommandCatalog missing entry for '@trap'.");
        _engine.RegisterHandler("@trap", category, OnTrap);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (string cmd in RegisteredCommands) _engine.UnregisterHandler(cmd);
    }

    private void OnTrap(RemoteCommandContext ctx)
    {
        // No args → missing direction. Per user spec: denial reply
        // with a hint, gated on WarnOnDenial.
        if (ctx.Args.Count == 0)
        {
            if (!_engine.WarnOnDenial) return;
            ctx.Reply("missing direction (e.g. @trap n)");
            return;
        }

        string arg0 = ctx.Args[0];

        // `@trap stop` aborts the current flow + drains the queue.
        // Anyone with ExecuteCommands grant can stop (per user spec);
        // the stop sender gets {ok} ack via the regular reply path,
        // each cancelled queued request's sender gets a "Trap flow
        // stopped." telepath via their captured reply callback.
        if (arg0.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            _manager.StopAll();
            ctx.Reply("ok");
            return;
        }

        // Direction parsing.
        string? direction = TrapDisarmManager.NormaliseDirection(arg0);
        if (direction is null)
        {
            if (!_engine.WarnOnDenial) return;
            ctx.Reply($"unknown direction '{arg0}'");
            return;
        }

        // Soft-gate on Traps skill — channel-aware silence on Say to
        // avoid the broadcast-chorus noise.
        if (!_manager.CanDisarm)
        {
            if (ctx.Channel == RemoteChannel.Local) return;
            if (!_engine.WarnOnDenial) return;
            ctx.Reply("can't disarm — no Traps skill");
            return;
        }

        // Queue with the channel-bound reply callback. The closure
        // captures (channel, sender) so when the manager invokes it
        // later from any state-machine terminal point, the reply
        // routes back via the same channel the @trap arrived on.
        _manager.Enqueue(direction, ctx.Sender, text => ctx.Reply(text));
    }
}
