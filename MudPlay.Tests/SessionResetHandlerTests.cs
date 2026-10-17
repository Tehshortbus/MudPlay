using System.Text;
using MudPlay.Game;
using MudPlay.Game.Cash;
using MudPlay.Game.Combat;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// SessionResetHandler — @reset zeroes the session-stats
// trackers (combat / time / activity), the same wipe the window button performs,
// but leaves the user-owned transaction ledger untouched. Pins the reset
// reaching the stat trackers, the ledger staying intact, the ungated success
// ack, and the AlterSettings permission gate (an unauthorised sender resets
// nothing). An injected clock makes the trackers' time arithmetic
// deterministic.
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
        // The router the engine's chat lines arrive through.
        public MessageRouter Router { get; }
        // What Engine put on the wire, through the sender the client binds.
        public List<byte[]> Sent { get; } = new();
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
            Router = router;
            DefaultPatterns.Seed(router);
            ChatRouter chat = new(router);
            PartyState party = new();
            Players = new PlayerDatabase(new ProfileService(), () => null);
            Engine = new RemoteCommandManager(chat, party, Players);
            Engine.SetWireSender(Sent.Add);
            Combat = new CombatSessionTracker(router, new RoundDamageTracker(router, new PlayerState()));
            Time = new TimeAnalysisTracker(() => Clock.Now);
            Activity = new SessionActivityTracker(() => Clock.Now);
            Transactions = new TransactionHistoryTracker(() => Clock.Now);
            Handler = new SessionResetHandler(Engine, Combat, Time, Activity);
        }

        public void Dispose() => Handler.Dispose();
    }

    private static ChatLine Telepath(string sender, string msg) =>
        new(ChatChannel.TelepathIncoming, sender, msg);

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

        ChatLines.Hear(s.Router, Telepath("Leader", "@reset"));

        SessionActivityStats stats = s.Activity.Snapshot();
        Assert.Equal(0, stats.MonstersKilled);
        Assert.Equal(0L, stats.ExperienceEarned);
    }

    [Fact]
    public void Reset_RestartsTimeAnalysisClock()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Leader", PlayerRemoteControls.AlterSettings);
        s.Time.NoteInGame(); // in-game, so Time Analysis is accruing
        s.Clock.Advance(30); // 30 min of session time accrues

        ChatLines.Hear(s.Router, Telepath("Leader", "@reset"));
        s.Clock.Advance(5);

        // @reset re-anchors but stays armed → only the post-reset 5 min remains.
        Assert.Equal(TimeSpan.FromMinutes(5), s.Time.Snapshot().TimeOn);
    }

    [Fact]
    public void Reset_Success_RepliesAck()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Leader", PlayerRemoteControls.AlterSettings);

        ChatLines.Hear(s.Router, Telepath("Leader", "@reset"));

        Assert.NotEmpty(s.Sent);
        string reply = Encoding.Latin1.GetString(s.Sent[^1]);
        Assert.Contains("reset", reply);
    }

    [Fact]
    public void Reset_WithoutAlterSettings_DoesNothing()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Rando", PlayerRemoteControls.QueryHealthStatus);
        for (int i = 0; i < 3; i++) s.Activity.NoteKill();

        ChatLines.Hear(s.Router, Telepath("Rando", "@reset"));

        // Permission gate blocks the handler — counters untouched, no reply.
        Assert.Equal(3, s.Activity.Snapshot().MonstersKilled);
    }
}
