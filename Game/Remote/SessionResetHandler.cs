using FujinTerm.Game.Combat;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

// Consumer of RemoteCommandManager for the @reset command — a party member
// zeroes our live session-stats trackers, the same wipe the Session Stats
// window's "Reset session" button performs. Matches the natural farming-loop
// rhythm: the leader fires @reset at the start of a lap so every member's
// kills/hour and combat observations re-anchor to the new circuit (see
// LoopRunner.LoopEventKind.ReachedFirstWaypoint).
//
// Resets the session-stats trackers — CombatSessionTracker, TimeAnalysisTracker,
// and SessionActivityTracker — restarting their clocks and clearing their
// counters, exactly as the window button does. The transaction-history ledger is
// deliberately NOT touched: it is user-owned and cleared only by the user or the
// connect / character-switch boundary, never by a party @reset. Requires the
// PlayerRemoteControls.AlterSettings permission per the catalog (a "do something
// on my behalf" verb). The success ack is ungated; only failure replies obey
// RemoteCommandManager.WarnOnDenial, and once authorised this command can't
// fail. Like the other remote handlers it runs on the marshalled dispatch
// thread, so the lock-free trackers stay safe.
public sealed class SessionResetHandler : IDisposable
{
    private const string Command = "@reset";
    private const string LogCategory = "RemoteCmd";

    private readonly RemoteCommandManager _engine;
    private readonly CombatSessionTracker _combat;
    private readonly TimeAnalysisTracker _time;
    private readonly SessionActivityTracker _activity;
    private readonly LogService? _log;
    private bool _disposed;

    public SessionResetHandler(
        RemoteCommandManager engine,
        CombatSessionTracker combat,
        TimeAnalysisTracker time,
        SessionActivityTracker activity,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(combat);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(activity);
        _engine = engine;
        _combat = combat;
        _time = time;
        _activity = activity;
        _log = log;

        if (!RemoteCommandCatalog.TryGetCategory(Command, out PlayerRemoteControls category))
            throw new InvalidOperationException("RemoteCommandCatalog missing entry for '@reset'.");
        _engine.RegisterHandler(Command, category, OnReset);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterHandler(Command);
    }

    private void OnReset(RemoteCommandContext ctx)
    {
        _combat.Reset();
        _time.Reset();
        _activity.Reset();
        _log?.Log(LogSeverity.Info, LogCategory, $"@reset from {ctx.Sender}: session counters zeroed.");
        ctx.Reply("session counters reset");
    }
}
