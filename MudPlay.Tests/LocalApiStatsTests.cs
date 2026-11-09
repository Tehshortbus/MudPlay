using System.Text.Json;
using MudPlay.Game.Calculators;
using MudPlay.Game.Combat;
using MudPlay.Services.Api;
using Xunit;

namespace MudPlay.Tests;

// GET /stats: Session Stats as JSON numbers, so a program driving the client can
// read exp/hr, kills/hr and time to level without parsing the bug report's
// sentences (issue #1197).
public sealed class LocalApiStatsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeAnalysisStats Idle = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero,
        TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

    private static JsonElement Render(object payload) =>
        JsonDocument.Parse(JsonSerializer.Serialize(payload, LocalApiJson.Options)).RootElement;

    [Fact]
    public void SessionFigures_ComeOutAsNumbers()
    {
        DateTimeOffset now = T0;
        SessionActivityTracker session = new(() => now);
        for (int i = 0; i < 3; i++) session.NoteKill();
        session.NoteExperience(5200);
        session.NoteCurrencyCollected(copper: 1500, coins: 15);
        session.NoteItemsCollected(2);
        now += TimeSpan.FromMinutes(30);

        TimeAnalysisStats time = new(
            TimeOn: TimeSpan.FromMinutes(30), Waiting: TimeSpan.FromMinutes(5), Moving: TimeSpan.FromMinutes(10),
            Attacking: TimeSpan.FromMinutes(12), RestingHp: TimeSpan.FromMinutes(2), RestingMa: TimeSpan.FromMinutes(1),
            Blinded: TimeSpan.Zero, Poisoned: TimeSpan.Zero, Diseased: TimeSpan.Zero, Confused: TimeSpan.Zero, Held: TimeSpan.Zero)
        {
            WaitingBy = [new WaitCauseTime("party", TimeSpan.FromMinutes(5))],
        };
        CombatSessionStats combat = default(CombatSessionStats) with
        {
            Hits = 6, Crits = 2, Misses = 2,
            HitMinDamage = 10, HitMaxDamage = 40, HitTotalDamage = 150,
            CritMinDamage = 60, CritMaxDamage = 90, CritTotalDamage = 150,
            Spells = [new SpellCombatStat("infernal flames", Landed: 4, Misses: 1, MinDamage: 80, MaxDamage: 120, TotalDamage: 400)],
        };
        TimeToLevelEstimator.Result estimate = new(BankableLevels: 1, BankableLevelsFractional: 1.4, TargetLevel: 74, Eta: null, ExpNeeded: 90_000);

        JsonElement json = Render(LocalApiState.Stats("Thunder", 73, session.Snapshot(), estimate,
            TimeSpan.FromHours(2), time, combat, ["infernal flames"]));

        JsonElement s = json.GetProperty("session");
        Assert.Equal(1800, s.GetProperty("rateWindowSeconds").GetDouble());
        Assert.Equal(3, s.GetProperty("kills").GetInt32());
        Assert.Equal(6, s.GetProperty("killsPerHour").GetDouble());
        Assert.Equal(5200, s.GetProperty("experience").GetInt64());
        Assert.Equal(10400, s.GetProperty("experiencePerHour").GetDouble());
        Assert.Equal(90_000, s.GetProperty("experienceNeeded").GetInt64());
        Assert.Equal(74, s.GetProperty("nextLevel").GetInt32());
        Assert.Equal(7200, s.GetProperty("levelInSeconds").GetDouble());
        Assert.Equal(1500, s.GetProperty("copperCollected").GetInt64());
        Assert.Equal(3000, s.GetProperty("copperPerHour").GetDouble());
        Assert.Equal(2, s.GetProperty("itemsCollected").GetInt32());

        JsonElement t = json.GetProperty("time");
        Assert.Equal(600, t.GetProperty("movingSeconds").GetDouble());
        Assert.Equal(720, t.GetProperty("attackingSeconds").GetDouble());
        JsonElement wait = Assert.Single(t.GetProperty("waitingBy").EnumerateArray());
        Assert.Equal("party", wait.GetProperty("cause").GetString());
        Assert.Equal(300, wait.GetProperty("seconds").GetDouble());

        JsonElement c = json.GetProperty("combat");
        Assert.Equal(10, c.GetProperty("attacks").GetInt32());
        Assert.Equal(80, c.GetProperty("hitPercent").GetDouble());
        Assert.Equal(25, c.GetProperty("critPercent").GetDouble());
        Assert.Equal(37.5, c.GetProperty("swingDamage").GetProperty("average").GetDouble());
        JsonElement spell = Assert.Single(c.GetProperty("spells").EnumerateArray());
        Assert.Equal(4, spell.GetProperty("landed").GetInt32());
        Assert.Equal(1, spell.GetProperty("resisted").GetInt32());
    }

    [Fact]
    public void UnknownLevelAndRate_AreLeftOut_NotZero()
    {
        // No exp-table entry for the next level and no exp rate yet: a program must
        // not read "0 needed, levels now".
        JsonElement json = Render(LocalApiState.Stats("Thunder", 73, new SessionActivityTracker(() => T0).Snapshot(),
            estimate: default, levelIn: null, Idle, default(CombatSessionStats) with { Spells = [] }, []));

        JsonElement s = json.GetProperty("session");
        Assert.False(s.TryGetProperty("experienceNeeded", out _));
        Assert.False(s.TryGetProperty("nextLevel", out _));
        Assert.False(s.TryGetProperty("levelInSeconds", out _));
        Assert.False(s.TryGetProperty("averageStepSeconds", out _));
        Assert.Equal(0, s.GetProperty("kills").GetInt32());
    }
}
