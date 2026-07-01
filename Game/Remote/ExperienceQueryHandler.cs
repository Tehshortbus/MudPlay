using FujinTerm.Game.Calculators;
using FujinTerm.Game.Combat;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

/// <summary>
/// Read-only <see cref="RemoteCommandManager"/> consumer for the two
/// <see cref="PlayerRemoteControls.QueryExperience"/> commands:
/// <list type="bullet">
///   <item><c>@exp</c> — session experience earned, the exp-per-hour
///         rate, and an estimated time-to-level.</item>
///   <item><c>@level</c> — current level, total accumulated experience,
///         and experience still needed for the next level.</item>
/// </list>
/// Both reply on the sender's channel and never touch the wire, so no
/// wire-sender is bound. Progression figures come from
/// <see cref="PlayerStats"/> (the periodic <c>stat</c> / <c>exp</c>
/// snapshot); the rate + session total come from
/// <see cref="SessionActivityTracker"/>. The engine gates authorisation
/// via <see cref="RemoteCommandCatalog"/> before the handler runs.
/// </summary>
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

    /// <summary>
    /// <c>@level</c> — "Level N, X exp, Y to next level". The exp-to-next
    /// figure comes from the game's <c>exp</c> line
    /// (<see cref="PlayerStats.LevelExpSpan"/> is 0 until that line is
    /// parsed), so we only advertise it once seen; before that we point
    /// the sender at <c>exp</c>.
    /// </summary>
    private void OnLevel(RemoteCommandContext ctx)
    {
        if (_stats.Level <= 0) { ctx.Reply("level unknown - parse a stat screen first (type stat)"); return; }
        string toNext = _stats.LevelExpSpan > 0
            ? $"{_stats.ExpToNext:N0} to next level"
            : "exp-to-next unknown (type exp)";
        ctx.Reply($"Level {_stats.Level}, {_stats.Exp:N0} exp, {toNext}");
    }

    /// <summary>
    /// <c>@exp</c> — session exp earned + exp/hour + ETA to next level.
    /// The ETA reuses <see cref="ExperienceTableCalculator.CalcTimeToLevel"/>
    /// with the already-remaining <see cref="PlayerStats.ExpToNext"/> as
    /// the "needed" figure (current exp 0), so a zero/negative remaining
    /// reads as "ready to level". Rate comes from the whole-session
    /// average the Session Stats panel prints — same figure, so the two
    /// stay consistent.
    /// </summary>
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

    /// <summary>Compact h/m/s rendering for the time-to-level estimate.</summary>
    private static string FormatEta(TimeSpan ts)
    {
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m";
        return $"{ts.Seconds}s";
    }
}
