using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// The permission check comes before every check that answers with a reason of
// its own, so a sender without the permission learns nothing but the generic
// failure; and the suicide rules match the exact command words.
public sealed class RemoteCommandCheckOrderTests
{
    // The router each engine reads, which a test's chat lines go through.
    private readonly Dictionary<RemoteCommandManager, MessageRouter> _routers = new();

    // What the remote-command engine put on the wire, through the sender the client binds.
    private readonly List<byte[]> _replies = new();

    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private (RemoteCommandManager engine, PlayerDatabase players) Engine()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerDatabase players = new(new ProfileService(), () => null);
        RemoteCommandManager engine = new(new ChatRouter(router), new PartyState(), players);
        _routers[engine] = router;
        engine.SetWireSender(_replies.Add);
        return (engine, players);
    }

    private static ChatLine Telepath(string sender, string msg) =>
        new(ChatChannel.TelepathIncoming, sender, msg);

    private static void Seed(PlayerDatabase db, string name, PlayerRemoteControls controls)
    {
        db.RecordObservation(name, @class: null, race: null, alignment: null,
            title: null, gang: null, role: null, nowUtc: Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
    }

    private List<string> Sent() =>
        _replies.Select(b => Encoding.Latin1.GetString(b)).ToList();

    [Fact]
    public void Suicide_FromASenderWithoutThePermission_GetsOnlyTheGenericFailure()
    {
        var (engine, players) = Engine();
        Seed(players, "Stranger", PlayerRemoteControls.All & ~PlayerRemoteControls.SysopCommands);
        engine.LivesProvider = () => 2;
        engine.MaxSuicideLivesThreshold = 5;
        engine.RegisterHandler("@suicide", PlayerRemoteControls.SysopCommands, _ => { });

        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@suicide"));

        Assert.Equal("/Stranger {command invalid or not allowed}\r", Assert.Single(Sent()));
    }

    [Fact]
    public void Suicide_FromAPlayerNeverSeen_NeverNamesTheLivesCount()
    {
        var (engine, _) = Engine();
        engine.LivesProvider = () => 2;
        engine.RegisterHandler("@suicide", PlayerRemoteControls.SysopCommands, _ => { });

        ChatLines.Hear(_routers[engine], Telepath("Nobody", "@suicide"));

        string reply = Assert.Single(Sent());
        Assert.DoesNotContain("lives", reply);
        Assert.DoesNotContain("2", reply);
    }

    [Fact]
    public void Suicide_WithoutThePermission_IsSilentWhenWarnIsOff()
    {
        var (engine, _) = Engine();
        engine.WarnOnDenial = false;
        engine.LivesProvider = () => 2;
        engine.RegisterHandler("@suicide", PlayerRemoteControls.SysopCommands, _ => { });

        ChatLines.Hear(_routers[engine], Telepath("Nobody", "@suicide"));

        Assert.Empty(_replies);
    }

    [Fact]
    public void DoSuicide_FromASenderWithoutThePermission_GetsOnlyTheGenericFailure()
    {
        var (engine, players) = Engine();
        Seed(players, "Stranger", PlayerRemoteControls.All & ~PlayerRemoteControls.ExecuteCommands);
        bool fired = false;
        engine.RegisterHandler("@do", PlayerRemoteControls.ExecuteCommands, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@do suicide"));

        Assert.False(fired);
        Assert.Equal("/Stranger {command invalid or not allowed}\r", Assert.Single(Sent()));
    }

    // "@suicidal" in gang chat is a word, not a command: it is neither answered
    // nor held to the lives rule.
    [Theory]
    [InlineData("@suicidal")]
    [InlineData("@suicide-watch tonight")]
    public void AWordThatOnlyContainsSuicide_IsNotACommand(string message)
    {
        var (engine, players) = Engine();
        Seed(players, "Trusted", PlayerRemoteControls.All);
        engine.LivesProvider = () => 1;
        engine.RegisterHandler("@suicide", PlayerRemoteControls.SysopCommands, _ => { });

        ChatLines.Hear(_routers[engine], Telepath("Trusted", message));

        Assert.Empty(_replies);
    }

    // The lives rule is @suicide's own: another command is not refused for having
    // the word among what it was sent.
    [Fact]
    public void AnotherCommandNamingSuicide_IsNotHeldToTheLivesRule()
    {
        var (engine, players) = Engine();
        Seed(players, "Trusted", PlayerRemoteControls.All);
        engine.LivesProvider = () => 1;
        engine.MaxSuicideLivesThreshold = 5;
        IReadOnlyList<string>? got = null;
        engine.RegisterHandler("@have", PlayerRemoteControls.QueryInventory, ctx => got = ctx.Args);

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@have suicide note"));

        Assert.Equal(new[] { "suicide", "note" }, got);
        Assert.Empty(_replies);
    }

    // @do sends its words to the game, so only the game's own word is refused: a
    // longer word that contains it is not the command.
    [Theory]
    [InlineData("@do say suicidal")]
    [InlineData("@do suicidal")]
    [InlineData("@do say unsuicided")]
    public void Do_WithALongerWordThatContainsSuicide_IsSent(string message)
    {
        var (engine, players) = Engine();
        Seed(players, "Trusted", PlayerRemoteControls.All);
        bool fired = false;
        engine.RegisterHandler("@do", PlayerRemoteControls.ExecuteCommands, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Trusted", message));

        Assert.True(fired);
        Assert.Empty(_replies);
    }

    // Punctuation and quotes around the word don't hide it.
    [Theory]
    [InlineData("@do suicide!")]
    [InlineData("@do suicide.")]
    [InlineData("@do \"suicide\"")]
    [InlineData("@do 'suicide'")]
    [InlineData("@do suicide;")]
    [InlineData("@do rest;suicide")]
    [InlineData("@do (Suicide)")]
    public void Do_WithSuicideInPunctuationOrQuotes_IsRefused(string message)
    {
        var (engine, players) = Engine();
        Seed(players, "Trusted", PlayerRemoteControls.All);
        bool fired = false;
        engine.RegisterHandler("@do", PlayerRemoteControls.ExecuteCommands, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Trusted", message));

        Assert.False(fired);
        Assert.Equal("/Trusted {@do suicide is not allowed, use @suicide}\r", Assert.Single(Sent()));
    }

    [Fact]
    public void Do_WithTheWordSuicide_InAnyCase_IsRefusedForAGrantedSender()
    {
        var (engine, players) = Engine();
        Seed(players, "Trusted", PlayerRemoteControls.All);
        bool fired = false;
        engine.RegisterHandler("@do", PlayerRemoteControls.ExecuteCommands, _ => fired = true);

        ChatLines.Hear(_routers[engine], Telepath("Trusted", "@do SUICIDE"));

        Assert.False(fired);
        Assert.Equal("/Trusted {@do suicide is not allowed, use @suicide}\r", Assert.Single(Sent()));
    }
}
