using System.Globalization;
using MudPlay.Game.Calculators;
using MudPlay.Game.Combat;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// Read-only handler for the two QueryExperience commands:
//   - @exp — a MegaMUD-style session progress line: exp MADE this session (the
//     running SessionActivityTracker total, which @reset zeroes — most parties
//     @reset at the start of a loop, or auto-reset on loop start), exp NEEDED for
//     the next level and which level that is (with the banked-levels ratio the
//     status-bar TNL shows, "+N.NN lvls"), the compact exp-per-hour rate, and the
//     time to that level at the current rate ("Made: 474,216,179  Needed: 545,045,125
//     (L72, +2.14 lvls)  Rate: 14.3 m/hr  Will level in: 1d 14h 12m").
//   - @level — current level, total accumulated experience, and experience still
//     needed for the next level.
// Both reply on the sender's channel. Progression figures come from PlayerStats
// (the server's vitals, or the periodic stat / exp snapshot); the rate + session
// total come from SessionActivityTracker. The engine gates authorisation via
// RemoteCommandCatalog before the handler runs.
//
// Where the client reads the exp from the `exp` line rather than the server's
// vitals, an @exp more than ExpLineStaleAfter since the last one (or with none
// read yet) sends `exp` first and answers once it is read, or after ExpReadWait.
// Every @exp heard while that is out is answered by the same read.
public sealed class ExperienceQueryHandler : IDisposable
{
    private static readonly string[] RegisteredCommands = { "@exp", "@level" };

    private readonly RemoteCommandManager _engine;
    private readonly PlayerStats _stats;
    private readonly SessionActivityTracker _activity;
    private readonly GameDataCache _gameData;
    private readonly StatParser _parser;
    private readonly Action<string> _send;
    private readonly Action<TimeSpan, Action> _armTimer;
    private readonly Func<DateTime> _now;
    private readonly LogService? _log;
    private readonly List<RemoteCommandContext> _awaitingExp = new();
    private int _expRead;
    private bool _disposed;

    public static readonly TimeSpan ExpLineStaleAfter = TimeSpan.FromHours(1);
    public static readonly TimeSpan ExpReadWait = TimeSpan.FromSeconds(5);

    public ExperienceQueryHandler(
        RemoteCommandManager engine,
        PlayerStats stats,
        SessionActivityTracker activity,
        GameDataCache gameData,
        StatParser parser,
        Action<string> send,
        Action<TimeSpan, Action> armTimer,
        Func<DateTime>? now = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(gameData);
        _engine = engine;
        _stats = stats;
        _activity = activity;
        _gameData = gameData;
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _armTimer = armTimer ?? throw new ArgumentNullException(nameof(armTimer));
        _now = now ?? (() => DateTime.UtcNow);
        _log = log;

        Register("@exp", OnExp);
        Register("@level", OnLevel);
        _parser.ExpLineRead += AnswerAwaitingExp;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _parser.ExpLineRead -= AnswerAwaitingExp;
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
    // from the server's vitals or the game's exp line, so we only advertise it once
    // one has given it; before that we point the sender at exp.
    private void OnLevel(RemoteCommandContext ctx)
    {
        // Protocol, not a refusal: a leader's PartyLevelProbe reads "level unknown"
        // as this member having answered, so it goes whatever the Warn setting says.
        if (_stats.Level <= 0) { ctx.Reply("level unknown - parse a stat screen first (type stat)"); return; }
        string toNext = _parser.ExpToNextKnown
            ? $"{_stats.ExpToNext:N0} to next level"
            : "exp-to-next unknown (type exp)";
        ctx.Reply($"Level {_stats.Level}, {_stats.Exp:N0} exp, {toNext}");
    }

    // @exp — a MegaMUD-style progress line:
    //   "Made: <session exp>  Needed: <exp to next> (L<next level>)  Rate: <rate>/hr
    //    Will level in: <time>"
    // Made is the running session total (SessionActivityTracker.ExperienceEarned,
    // zeroed by @reset / loop-start auto-reset) and is ALWAYS shown, even before a
    // stat/exp screen — it's the "how are we doing this session" figure. Needed /
    // next-level / the ETA come off the server's vitals, or the game's exp line
    // (StatParser.ExpToNextKnown), so before either we report Made + rate and point
    // the sender at `exp`. The ETA reuses CalcTimeToLevel with ExpToNext as the needed
    // figure (current exp 0), so a zero/negative remaining reads "ready to level".
    // The (L<n>) tag is the level being worked toward — the current level + 1 — and
    // is dropped when the level isn't known yet.
    private void OnExp(RemoteCommandContext ctx)
    {
        if (_parser.ServerOwnsExp
            || _parser.ExpLineReadAt is { } read && _now() - read <= ExpLineStaleAfter)
        {
            ctx.Reply(ExpLine());
            return;
        }
        _awaitingExp.Add(ctx);
        if (_awaitingExp.Count > 1) return;
        int asked = _expRead;
        _log?.Info("RemoteCommand", _parser.ExpLineReadAt is null
            ? "@exp: no exp line read yet: sending exp before answering"
            : "@exp: the exp line is over an hour old: sending exp before answering");
        _send("exp");
        _armTimer(ExpReadWait, () =>
        {
            if (asked != _expRead || _awaitingExp.Count == 0) return;
            _log?.Info("RemoteCommand", $"@exp: no exp line within {ExpReadWait.TotalSeconds:0} s: answering with what is known");
            AnswerAwaitingExp();
        });
    }

    private void AnswerAwaitingExp()
    {
        _expRead++;
        if (_awaitingExp.Count == 0) return;
        RemoteCommandContext[] asked = _awaitingExp.ToArray();
        _awaitingExp.Clear();
        string line = ExpLine();
        foreach (RemoteCommandContext ctx in asked) ctx.Reply(line);
    }

    private string ExpLine()
    {
        SessionActivityStats snap = _activity.Snapshot();
        double rate = snap.ExperiencePerHour;
        string made = $"Made: {snap.ExperienceEarned:N0}";
        string ratePart = rate > 0 ? $"Rate: {FormatExpRate(rate)}/hr" : "Rate: unknown";

        if (!_parser.ExpToNextKnown) return $"{made}  {ratePart} (type exp for needed + time to level)";

        // Level tag: the next level, plus the banked-levels ratio the status-bar TNL
        // surfaces ("+N.NN") — how many levels' worth of exp the running total already
        // covers. Shared TimeToLevelEstimator so @exp and the TNL can't drift; the
        // ratio is dropped when the exp chart can't be resolved (no game data).
        string levelTag = string.Empty;
        if (_stats.Level > 0)
        {
            TimeToLevelEstimator.Result est = TimeToLevelEstimator.Estimate(_stats, _gameData, rate);
            levelTag = est.TargetLevel > 0
                ? $" (L{_stats.Level + 1}, +{TrainBudgetCalculator.FormatBankableLevels(est.BankableLevelsFractional)} lvls)"
                : $" (L{_stats.Level + 1})";
        }
        string needed = $"Needed: {_stats.ExpToNext:N0}{levelTag}";
        if (rate <= 0) return $"{made}  {needed}  Rate: unknown";

        TimeSpan? eta = ExperienceTableCalculator.CalcTimeToLevel(_stats.ExpToNext, 0, (long)rate);
        string willLevel = eta is null || eta.Value <= TimeSpan.Zero
            ? "Will level in: ready to level"
            : $"Will level in: {ExperienceTableCalculator.FormatTimeToLevel(eta.Value)}";

        return $"{made}  {needed}  {ratePart}  {willLevel}";
    }

    // Compact exp/hr for the @exp reply: exact comma-grouped below 100k, whole
    // thousands 100k–999k ("853 k"), millions with one decimal above ("1.1 m",
    // "10.1 m", "30 m"). The unit is space-separated from the value so the reply
    // reads like MegaMUD's ("14.3 m/hr"). ~30m/hr is the game's ceiling, so there's
    // no need for billions/trillions tiers. Deliberately distinct from
    // RateText.Compact (the narrow status-chip format, which abbreviates from 1k with
    // a decimal and an uppercase M) — the chat reply keeps small rates exact and
    // reads lowercase.
    internal static string FormatExpRate(double rate)
    {
        if (rate < 100_000) return rate.ToString("N0", CultureInfo.InvariantCulture);
        if (rate < 1_000_000) return string.Create(CultureInfo.InvariantCulture, $"{(long)(rate / 1000)} k");
        return string.Create(CultureInfo.InvariantCulture, $"{rate / 1_000_000d:0.#} m");
    }
}
