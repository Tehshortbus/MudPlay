using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Cash;
using FujinTerm.Game.Combat;
using FujinTerm.Game.Remote;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="SessionResetHandler"/> — <c>@reset</c> zeroes the session-stats
/// trackers and clears the transaction ledger, the same wipe the window button
/// performs. Pins the reset reaching every tracker, the ungated success ack,
/// and the AlterSettings permission gate (an unauthorised sender resets
/// nothing). An injected clock makes the trackers' time arithmetic
/// deterministic.
/// </summary>
public sealed class SessionResetHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public void Advance(double minutes) => Now += TimeSpan.FromMinutes(minutes);
    }

    private sealed class Setup : IDisposable
    {
        public RemoteCommandManager Engine { get; }
        public SessionResetHandler Handler { get; }
        public PlayerDatabase Players { get; }
        public CombatSessionTracker Combat { get; }
        public TimeAnalysisTracker Time { get; }
        public SessionActivityTracker Activity { get; }
        public TransactionHistoryTracker Transactions { get; }
        public Clock Clock { get; } = new();

        public Setup()
        {
            MessageRouter router = new();
            DefaultPatterns.Seed(router);
            ChatRouter chat = new(router);
            PartyState party = new();
            Players = new PlayerDatabase();
            Engine = new RemoteCommandManager(chat, party, Players);
            Combat = new CombatSessionTracker(router, new RoundDamageTracker(router, new PlayerState()));
            Time = new TimeAnalysisTracker(() => Clock.Now);
            Activity = new SessionActivityTracker(() => Clock.Now);
            Transactions = new TransactionHistoryTracker(() => Clock.Now);
            Handler = new SessionResetHandler(Engine, Combat, Time, Activity, Transactions);
        }

        public void Dispose() => Handler.Dispose();
    }

    private static ChatLogEntry Telepath(string sender, string msg) =>
        new(Now, ChatChannel.TelepathIncoming, sender, msg, $"{sender} telepaths: {msg}");

    private static void SeedPlayer(PlayerDatabase db, string name, PlayerRemoteControls controls)
    {
        db.RecordObservation(name, null, null, null, null, null, null, Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
    }

    [Fact]
    public void Reset_WithPermission_ZeroesActivityCounters()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Leader", PlayerRemoteControls.AlterSettings);
        for (int i = 0; i < 5; i++) s.Activity.NoteKill();
        s.Activity.NoteExperience(4200);

        s.Engine.DispatchForTests(Telepath("Leader", "@reset"));

        SessionActivityStats stats = s.Activity.Snapshot();
        Assert.Equal(0, stats.MonstersKilled);
        Assert.Equal(0L, stats.ExperienceEarned);
    }

    [Fact]
    public void Reset_WithPermission_ClearsTransactionLedger()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Leader", PlayerRemoteControls.AlterSettings);
        s.Transactions.NoteBankDeposit(5000);
        s.Transactions.NoteStash(new[] { ("gold", 400L) }, new[] { "a torch" });
        Assert.Equal(2, s.Transactions.Snapshot().Count);

        s.Engine.DispatchForTests(Telepath("Leader", "@reset"));

        Assert.Empty(s.Transactions.Snapshot());
    }

    [Fact]
    public void Reset_RestartsTimeAnalysisClock()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Leader", PlayerRemoteControls.AlterSettings);
        s.Time.NoteInGame(); // in-game, so Time Analysis is accruing
        s.Clock.Advance(30); // 30 min of session time accrues

        s.Engine.DispatchForTests(Telepath("Leader", "@reset"));
        s.Clock.Advance(5);

        // @reset re-anchors but stays armed → only the post-reset 5 min remains.
        Assert.Equal(TimeSpan.FromMinutes(5), s.Time.Snapshot().TimeOn);
    }

    [Fact]
    public void Reset_Success_RepliesAck()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Leader", PlayerRemoteControls.AlterSettings);

        s.Engine.DispatchForTests(Telepath("Leader", "@reset"));

        Assert.NotEmpty(s.Engine.LastSentForTests);
        string reply = Encoding.Latin1.GetString(s.Engine.LastSentForTests[^1]);
        Assert.Contains("reset", reply);
    }

    [Fact]
    public void Reset_WithoutAlterSettings_DoesNothing()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Rando", PlayerRemoteControls.QueryHealthStatus);
        for (int i = 0; i < 3; i++) s.Activity.NoteKill();

        s.Engine.DispatchForTests(Telepath("Rando", "@reset"));

        // Permission gate blocks the handler — counters untouched, no reply.
        Assert.Equal(3, s.Activity.Snapshot().MonstersKilled);
    }
}
