using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Api;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// `@stop!` is `@stop` said with emphasis. The catalog always read it that way;
// the dispatcher and the local API now do too.
public sealed class RemoteCommandEmphasisTests
{
    // The router each engine reads, which a test's chat lines go through.
    private readonly Dictionary<RemoteCommandManager, MessageRouter> _routers = new();

    // What the remote-command engine put on the wire, through the sender the client binds.
    private readonly List<byte[]> _replies = new();

    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private (RemoteCommandManager engine, List<RemoteCommandContext> seen) Engine()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerDatabase players = new(new ProfileService(), () => null);
        players.RecordObservation("Leader", @class: null, race: null, alignment: null,
            title: null, gang: null, role: null, nowUtc: Now);
        players.EditCustomization("Leader", new PlayerCustomization(RemoteControls: PlayerRemoteControls.All));
        RemoteCommandManager engine = new(new ChatRouter(router), new PartyState(), players);
        _routers[engine] = router;
        engine.SetWireSender(_replies.Add);
        List<RemoteCommandContext> seen = new();
        engine.RegisterHandler("@stop", PlayerRemoteControls.MovePlayer, seen.Add);
        return (engine, seen);
    }

    private static ChatLine Telepath(string msg) =>
        new(ChatChannel.TelepathIncoming, "Leader", msg);

    [Fact]
    public void ACommandWithATrailingBang_IsDispatchedAsTheBareCommand()
    {
        var (engine, seen) = Engine();

        ChatLines.Hear(_routers[engine], Telepath("@stop! now"));

        RemoteCommandContext ctx = Assert.Single(seen);
        Assert.Equal("@stop", ctx.Command);
        Assert.Equal(new[] { "now" }, ctx.Args);
    }

    [Fact]
    public void OnlyOneBangIsTaken_AndABangAloneIsNoCommand()
    {
        var (engine, seen) = Engine();

        ChatLines.Hear(_routers[engine], Telepath("@stop!!"));
        ChatLines.Hear(_routers[engine], Telepath("@!"));

        Assert.Empty(seen);
        Assert.Empty(_replies);
    }

    [Fact]
    public void ARelayedCommandWithABang_IsKnown_AndSentBackAsTyped()
    {
        var (engine, _) = Engine();

        ChatLines.Hear(_routers[engine], Telepath("&@stop!"));

        Assert.Equal("/Leader @stop!\r", Encoding.Latin1.GetString(Assert.Single(_replies)));
    }

    [Fact]
    public void ALocalInvocationWithABang_RunsTheBareCommand()
    {
        var (engine, seen) = Engine();

        Assert.Equal(RemoteCommandManager.LocalInvokeResult.Ok, engine.TryInvokeLocal("@stop!", null, _ => { }));

        Assert.Equal("@stop", Assert.Single(seen).Command);
        Assert.True(LocalApiActions.IsKnown("@stop!"));
        Assert.False(LocalApiActions.IsDestructive("@stop!"));
    }

    [Theory]
    [InlineData("@stop!", "@stop")]
    [InlineData("@stop", "@stop")]
    [InlineData("@!", "@!")]
    [InlineData("!", "!")]
    public void WithoutEmphasis_TakesOneTrailingBangOffAWord(string typed, string read) =>
        Assert.Equal(read, RemoteCommandCatalog.WithoutEmphasis(typed));
}
