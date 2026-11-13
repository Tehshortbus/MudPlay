using System.Globalization;
using System.Text;
using MudPlay.Game.Calculators;
using MudPlay.Game.Cash;
using MudPlay.Game.Combat;

namespace MudPlay.Services;

// Renders one snapshot of the Session Statistics window's figures as a block of
// text for the session-statistics log: the same three sections under the same
// row names (Player Statistics, Time Analysis, Session Statistics), so a block
// reads against the window. Built straight from the trackers' snapshots, so it
// doesn't need the window open.
public static class SessionStatsLogFormatter
{
    // The running loop's lap readouts, as the loop runner has them.
    public readonly record struct Laps(
        string? LoopName, bool Running, int Completed, TimeSpan? Last, TimeSpan? Average, TimeSpan? Current);

    // why: what made this block ("every 5 min", "left the game").
    public static string Format(
        DateTimeOffset at, string who, string why,
        CombatSessionStats combat, TimeAnalysisStats time, SessionActivityStats activity,
        (TimeToLevelEstimator.Result Estimate, TimeSpan? Remaining) tnl, int level,
        string? runicName, Laps laps)
    {
        StringBuilder sb = new();
        sb.Append("===== Session statistics · ").Append(at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
          .Append(" · ").Append(who).Append(" · ").Append(why).AppendLine(" =====");

        sb.AppendLine("  Player Statistics");
        sb.AppendLine(Row("Offense", "#", "min–max", "avg", "rate"));
        sb.AppendLine(Row("Hit", Count(combat.Hits), Range(combat.HitMinDamage, combat.HitMaxDamage), Avg(combat.HitAvgDamage, combat.Hits), Pct(combat.HitPercent, combat.TotalSwings)));
        sb.AppendLine(Row("Miss", Count(combat.Misses), "", "", Pct(combat.MissPercent, combat.TotalSwings)));
        sb.AppendLine(Row("Crit", Count(combat.Crits), Range(combat.CritMinDamage, combat.CritMaxDamage), Avg(combat.CritAvgDamage, combat.Crits), Pct(combat.CritPercent, combat.Hits + combat.Crits)));
        sb.AppendLine(Row("Backstab", Count(combat.Backstabs), Range(combat.BackstabMinDamage, combat.BackstabMaxDamage), Avg(combat.BackstabAvgDamage, combat.Backstabs), Pct(combat.BackstabPercent, combat.BackstabAttempts)));
        sb.AppendLine(Row("BS miss", Count(combat.BackstabFails), "", "", Pct(combat.BackstabFailPercent, combat.BackstabAttempts)));
        sb.AppendLine(Row("Round", Count(combat.RoundsWithDamage), Range(combat.RoundMinDamage, combat.RoundMaxDamage), Avg(combat.RoundAvgDamage, combat.RoundsWithDamage), ""));
        sb.AppendLine(Row("Rounds/kill", Count(combat.TimedKills), Range(combat.RoundsToKillMin, combat.RoundsToKillMax),
            combat.TimedKills <= 0 ? "" : combat.AvgRoundsToKill.ToString("F1", CultureInfo.InvariantCulture), ""));
        if (combat.ProcHits > 0)
            sb.AppendLine(Row("Proc", Count(combat.ProcHits), Range(combat.ProcMinDamage, combat.ProcMaxDamage), Avg(combat.ProcAvgDamage, combat.ProcHits), ""));
        foreach (SpellCombatStat spell in combat.Spells)
            sb.AppendLine(Row(spell.Name, Count(spell.Casts), spell.RangeText, Avg(spell.AvgDamage, spell.Landed), Pct(spell.AccuracyPercent, spell.Casts)));
        sb.AppendLine(Row("Defense", "#", "min–max", "avg", "rate"));
        sb.AppendLine(Row("Hit by", Count(combat.MobHits), Range(combat.HitTakenMinDamage, combat.HitTakenMaxDamage), Avg(combat.HitTakenAvgDamage, combat.MobHits), Pct(combat.HitTakenPercent, combat.IncomingAttacks)));
        sb.AppendLine(Row("Dodge/Miss", Count(combat.AvoidedAttacks), "", "", Pct(combat.AvoidPercent, combat.IncomingAttacks)));

        sb.AppendLine("  Time Analysis");
        sb.AppendLine(Pair("Duration", Span(time.TimeOn)));
        sb.AppendLine(Pair("Moving", Span(time.Moving)));
        sb.AppendLine(Pair("Attacking", Span(time.Attacking)));
        sb.AppendLine(Pair("Resting", Span(time.Resting)));
        sb.AppendLine(Pair("Waiting", Span(time.Waiting)));
        sb.AppendLine(Pair("Resting HPs", Span(time.RestingHp)));
        sb.AppendLine(Pair("Resting Mana", Span(time.RestingMa)));
        sb.AppendLine(Pair("Blinded", Span(time.Blinded)));
        sb.AppendLine(Pair("Poisoned", Span(time.Poisoned)));
        sb.AppendLine(Pair("Diseased", Span(time.Diseased)));
        sb.AppendLine(Pair("Confused", Span(time.Confused)));
        sb.AppendLine(Pair("Held", Span(time.Held)));
        sb.AppendLine(Pair("Sneak", activity.SneakPercent is { } sneak
            ? $"{RateText.Percent(sneak)} ({N(activity.SneakHeld)} of {N(activity.SneakEntries)} rooms)" : "—"));
        sb.AppendLine(Pair("Disarm Trap", activity.DisarmPercent is { } disarm
            ? $"{N(activity.TrapsDisarmed)} · {RateText.Percent(disarm)} ({N(activity.DisarmAttempts)} attempts)" : "—"));
        sb.AppendLine(Pair("Walk Latency", activity.AverageStep is { } step
            ? string.Create(CultureInfo.InvariantCulture, $"{step.TotalSeconds:F2}s over {N(activity.Steps)} steps") : "—"));
        sb.AppendLine(Pair("Loop laps", laps.LoopName is { Length: > 0 } loop
            ? $"{loop}{(laps.Running ? "" : " (not running)")}" : "—"));
        sb.AppendLine(Pair("Laps completed", N(laps.Completed)));
        sb.AppendLine(Pair("Last lap", SpanOrDash(laps.Last)));
        sb.AppendLine(Pair("Average lap", SpanOrDash(laps.Average)));
        sb.AppendLine(Pair("Current lap", SpanOrDash(laps.Current)));

        sb.AppendLine("  Session Statistics");
        sb.AppendLine(Pair("Kills", N(activity.MonstersKilled)));
        sb.AppendLine(Pair("Kills / hour", activity.KillsPerHour.ToString("F1", CultureInfo.InvariantCulture)));
        sb.AppendLine(Pair("Experience", N(activity.ExperienceEarned)));
        sb.AppendLine(Pair("Exp / hour", N((long)activity.ExperiencePerHour)));
        sb.AppendLine(Pair("Exp needed", level > 0 && tnl.Estimate.TargetLevel > 0
            ? $"{N(tnl.Estimate.ExpNeeded)} (L{tnl.Estimate.TargetLevel})" : "—"));
        sb.AppendLine(Pair("Will level in", level <= 0 || tnl.Estimate.TargetLevel <= 0 ? "—"
            : tnl.Remaining switch
            {
                null => "rate unknown",
                { } eta when eta <= TimeSpan.Zero => "ready to level",
                { } eta => Span(eta),
            }));
        sb.AppendLine(Pair("Coin collected", $"{CurrencyFormat.Denominate(activity.CurrencyCollected, runicName)} ({N(activity.CoinsCollected)} coins)"));
        sb.AppendLine(Pair("Coin deposited / sold", CurrencyFormat.Denominate(activity.CurrencyDeposited, runicName)));
        sb.AppendLine(Pair("Coin stashed", $"{CurrencyFormat.Denominate(activity.CurrencyStashed, runicName)} ({N(activity.CoinsStashed)} coins)"));
        sb.AppendLine(Pair("Income / hour", $"{CurrencyFormat.Denominate(activity.CurrencyPerHour, runicName)} ({N((long)activity.CoinsPerHour)} coins)"));
        sb.AppendLine(Pair("Items collected", N(activity.ItemsCollected)));
        sb.AppendLine(Pair("Items sold", N(activity.ItemsSold)));
        // Ends on a newline, so the writer's own line break leaves a blank line
        // between one block and the next.
        sb.AppendLine(Pair("Items stashed", N(activity.ItemsStashed)));
        return sb.ToString();
    }

    private static string Row(string name, string count, string range, string avg, string rate) =>
        $"    {name,-14}{count,8}{range,12}{avg,8}{rate,9}".TrimEnd();

    private static string Pair(string name, string value) => $"    {name,-24}{value}";

    private static string Count(int n) => "×" + n.ToString(CultureInfo.InvariantCulture);

    private static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string Range(int min, int max) =>
        max <= 0 ? "—" : string.Create(CultureInfo.InvariantCulture, $"{min}–{max}");

    // An average or a rate over nothing is left blank, not shown as zero.
    private static string Avg(double value, int over) =>
        over <= 0 ? "" : value.ToString("F0", CultureInfo.InvariantCulture);

    private static string Pct(double percent, int over) => over <= 0 ? "" : RateText.Percent(percent);

    private static string Span(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}");

    private static string SpanOrDash(TimeSpan? t) => t is { } span ? Span(span) : "—";
}
