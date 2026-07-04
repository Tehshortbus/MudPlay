using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

// The @kill <target> command — a party member asks us to engage a named monster
// on their behalf. It simply retargets our combat engine: the named monster
// becomes our current target and we engage it this round, with CombatManager
// determining the attack type (weapon swap / attack spell / backstab) exactly as
// it would for any single target. The retarget fires even when master
// auto-attack is off — an explicit-engage request bypasses the master switch.
//
// Requires ExecuteCommands per the catalog ("do something on my behalf" tier) —
// an action request, not a party coordination signal, so it's per-player
// flag-gated like @do / @heal rather than party-whitelisted. Authorisation is
// enforced by the engine before this handler runs.
//
// Success is silent — a retarget produces no reply, mirroring how the local
// engine retargets without chatter. A missing target name is a generic failure:
// it falls through to FailureMessage, gated by WarnOnDenial (suppressed when the
// user has muted denial replies).
public sealed class KillHandler : IDisposable
{
    private const string LogCategory = "RemoteCmd";

    private readonly RemoteCommandManager _engine;
    private readonly Action<string> _retarget;
    private readonly LogService? _log;
    private bool _disposed;

    public KillHandler(RemoteCommandManager engine, Action<string> retarget, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(retarget);
        _engine = engine;
        _retarget = retarget;
        _log = log;

        if (!RemoteCommandCatalog.TryGetCategory("@kill", out PlayerRemoteControls category))
            throw new InvalidOperationException("RemoteCommandCatalog missing entry for '@kill'.");
        _engine.RegisterHandler("@kill", category, OnKill);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterHandler("@kill");
    }

    private void OnKill(RemoteCommandContext ctx)
    {
        if (ctx.Args.Count == 0)
        {
            // No target named — generic syntax failure. Falls through to the
            // configured FailureMessage, gated by WarnOnDenial.
            if (_engine.WarnOnDenial)
                ctx.Reply(_engine.FailureMessage);
            return;
        }

        // Re-join tokens so multi-word monster names ("giant rat") survive the
        // engine's whitespace tokenisation.
        string target = string.Join(' ', ctx.Args);
        _retarget(target);
        _log?.Log(LogSeverity.Info, LogCategory, $"@kill from {ctx.Sender}: '{target}'");
        // Silent on success — a retarget makes no reply.
    }
}
