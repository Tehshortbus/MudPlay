using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Remote;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="KillHandler"/> — <c>@kill &lt;target&gt;</c> retargets our combat
/// engine to a named monster. Pins the retarget call (single + multi-word
/// names), silent-on-success, the WarnOnDenial-gated missing-target failure,
/// and ExecuteCommands permission gating.
/// </summary>
public sealed class KillHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Setup : IDisposable
    {
        public RemoteCommandManager Engine { get; }
        public KillHandler Handler { get; }
        public PlayerDatabase Players { get; }
        public List<string> Retargets { get; } = new();

        public Setup()
        {
            MessageRouter router = new();
            DefaultPatterns.Seed(router);
            ChatRouter chat = new(router);
            PartyState party = new();
            Players = new PlayerDatabase();
            Engine = new RemoteCommandManager(chat, party, Players);
            Handler = new KillHandler(Engine, name => Retargets.Add(name));
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
    public void Kill_WithTarget_Retargets()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.ExecuteCommands);

        s.Engine.DispatchForTests(Telepath("Tank", "@kill kobold"));

        Assert.Equal(new[] { "kobold" }, s.Retargets);
    }

    [Fact]
    public void Kill_MultiWordName_JoinsTokens()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.ExecuteCommands);

        s.Engine.DispatchForTests(Telepath("Tank", "@kill giant rat"));

        Assert.Equal(new[] { "giant rat" }, s.Retargets);
    }

    [Fact]
    public void Kill_Success_IsSilent()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.ExecuteCommands);

        s.Engine.DispatchForTests(Telepath("Tank", "@kill kobold"));

        Assert.Empty(s.Engine.LastSentForTests);
    }

    [Fact]
    public void Kill_NoTarget_RepliesFailureWhenWarnOn()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.ExecuteCommands);
        s.Engine.WarnOnDenial = true;

        s.Engine.DispatchForTests(Telepath("Tank", "@kill"));

        Assert.Empty(s.Retargets);
        string reply = Encoding.Latin1.GetString(s.Engine.LastSentForTests[^1]);
        Assert.Contains(s.Engine.FailureMessage, reply);
    }

    [Fact]
    public void Kill_NoTarget_SilentWhenWarnOff()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.ExecuteCommands);
        s.Engine.WarnOnDenial = false;

        s.Engine.DispatchForTests(Telepath("Tank", "@kill"));

        Assert.Empty(s.Retargets);
        Assert.Empty(s.Engine.LastSentForTests);
    }

    [Fact]
    public void Kill_WithoutExecutePermission_DoesNotRetarget()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Rando", PlayerRemoteControls.QueryHealthStatus);

        s.Engine.DispatchForTests(Telepath("Rando", "@kill kobold"));

        Assert.Empty(s.Retargets);
    }
}
