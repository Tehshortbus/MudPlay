using FujinTerm.Game.Calculators;
using FujinTerm.Game.Combat;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

// Read-only handler for the two QueryExperience commands:
//   - @exp — session experience earned, the exp-per-hour rate, and an estimated
//     time-to-level.
//   - @level — current level, total accumulated experience, and experience still
//     needed for the next level.
// Both reply on the sender's channel and never touch the wire, so no wire-sender
// is bound. Progression figures come from PlayerStats (the periodic stat / exp
// snapshot); the rate + session total come from SessionActivityTracker. The
// engine gates authorisation via RemoteCommandCatalog before the handler runs.
public sealed class ExperienceQueryHandler : IDisposable
{
    private static readonly string[] RegisteredCommands = { "@exp", "@level" };

    private readonly RemoteCommandManager _engine;
    private readonly PlayerStats _stats;
    private readonly SessionActivityTracker _activity;
    private bool _disposed;

    public ExperienceQueryHandler(
        RemoteCommandManager engine,
        PlayerStats stats,
        SessionActivityTracker activity)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(activity);
        _engine = engine;
        _stats = stats;
        _activity = activity;

        Register("@exp", OnExp);
        Register("@level", OnLevel);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (string cmd in RegisteredCommands) _engine.UnregisterHandler(cmd);
    }

    private void Register(string command, Action<RemoteCommandContext> handler)
    {
        if (!RemoteCommandCatalog.TryGetCategory(command, out PlayerRemoteControls category))
            throw new InvalidOperationException(
                $"RemoteCommandCatalog missing entry for '{command}'. Add it to the Map before registering.");
        _engine.RegisterHandler(command, category, handler);
    }

    // @level — "Level N, X exp, Y to next level". The exp-to-next figure comes
    // from the game's exp line (PlayerStats.LevelExpSpan is 0 until that line is
    // parsed), so we only advertise it once seen; before that we point the sender
    // at exp.
    private void OnLevel(RemoteCommandContext ctx)
    {
        if (_stats.Level <= 0) { ctx.Reply("level unknown - parse a stat screen first (type stat)"); return; }
        string toNext = _stats.LevelExpSpan > 0
            ? $"{_stats.ExpToNext:N0} to next level"
            : "exp-to-next unknown (type exp)";
        ctx.Reply($"Level {_stats.Level}, {_stats.Exp:N0} exp, {toNext}");
    }

    // @exp — session exp earned + exp/hour + ETA to next level. The ETA reuses
    // ExperienceTableCalculator.CalcTimeToLevel with the already-remaining
    // PlayerStats.ExpToNext as the "needed" figure (current exp 0), so a
    // zero/negative remaining reads as "ready to level". Rate comes from the
    // whole-session average the Session Stats panel prints — same figure, so the
    // two stay consistent.
    private void OnExp(RemoteCommandContext ctx)
    {
        SessionActivityStats snap = _activity.Snapshot();
        double rate = snap.ExperiencePerHour;
        string ratePart = rate > 0 ? $"{rate:N0}/hr" : "rate unknown";

        string? etaPart = null;
        if (rate > 0)
        {
            if (_stats.LevelExpSpan <= 0)
            {
                etaPart = "type exp for ETA";
            }
            else
            {
                TimeSpan? eta = ExperienceTableCalculator.CalcTimeToLevel(_stats.ExpToNext, 0, (long)rate);
                etaPart = eta is null
                    ? null
                    : eta.Value <= TimeSpan.Zero ? "ready to level" : $"~{FormatEta(eta.Value)} to level";
            }
        }

        string body = $"{snap.ExperienceEarned:N0} exp this session, {ratePart}";
        ctx.Reply(etaPart is null ? body : $"{body}, {etaPart}");
    }

    // Compact h/m/s rendering for the time-to-level estimate.
    private static string FormatEta(TimeSpan ts)
    {
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m";
        return $"{ts.Seconds}s";
    }
}
