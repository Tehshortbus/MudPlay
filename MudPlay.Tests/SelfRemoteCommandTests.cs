using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// `!@<command>` typed by the user runs the remote command on this client with no
// permission check, prints each reply in brackets, and is never a game command;
// anything else is left for the game.
public sealed class SelfRemoteCommandTests
{
    private readonly List<string> _printed = new();
    private readonly RemoteCommandManager _engine;
    private readonly SelfRemoteCommand _self;

    public SelfRemoteCommandTests()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        _engine = new RemoteCommandManager(new ChatRouter(router), new PartyState(),
            new PlayerDatabase(new ProfileService(), () => null));
        _self = new SelfRemoteCommand(_engine, _printed.Add);
    }

    [Fact]
    public void TypedCommand_RunsHere_WithItsArgs_AndPrintsTheReplyInBrackets()
    {
        List<string> args = new();
        string? sender = null;
        // A sysop-level command no player in the (empty) database may send.
        _engine.RegisterHandler("@elevated", PlayerRemoteControls.SysopCommands, ctx =>
        {
            args.AddRange(ctx.Args);
            sender = ctx.Sender;
            ctx.Reply("done");
        });

        Assert.True(_self.TryHandleTypedLine("  !@Elevated north 3  "));

        Assert.Equal(new[] { "north", "3" }, args);
        Assert.Equal(SelfRemoteCommand.SenderName, sender);
        Assert.Equal("[done]", Assert.Single(_printed));
    }

    [Theory]
    [InlineData("@health")]
    [InlineData("say !@health")]
    [InlineData("!health")]
    public void OtherLines_AreLeftForTheGame(string line)
    {
        Assert.False(_self.TryHandleTypedLine(line));
        Assert.Empty(_printed);
    }

    [Fact]
    public void UnknownCommand_IsSaidSo_AndNothingIsSent()
    {
        Assert.True(_self.TryHandleTypedLine("!@nosuch"));
        Assert.Equal("[@nosuch is not a remote command]", Assert.Single(_printed));
    }

    [Fact]
    public void RemoteCommandsSwitchedOff_IsSaidSo()
    {
        bool ran = false;
        _engine.RegisterHandler("@ping", PlayerRemoteControls.QueryVersion, _ => ran = true);
        _engine.MasterDisable = true;

        Assert.True(_self.TryHandleTypedLine("!@ping"));
        Assert.False(ran);
        Assert.Equal("[remote commands are switched off]", Assert.Single(_printed));
    }

    [Fact]
    public void BarePrefix_PrintsHowToUseIt()
    {
        Assert.True(_self.TryHandleTypedLine("!@"));
        Assert.StartsWith("[Type a remote command after !@", Assert.Single(_printed));
    }
}
