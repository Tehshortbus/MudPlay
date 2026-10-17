using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// Nothing outside ASCII reaches the wire in a remote-command answer: a long dash
// can't be encoded in Latin-1 at all and arrived as `?`.
public sealed class WireTextTests
{
    // The router each engine reads, which a test's chat lines go through.
    private readonly Dictionary<RemoteCommandManager, MessageRouter> _routers = new();

    // What the remote-command engine put on the wire, through the sender the client binds.
    private readonly List<byte[]> _replies = new();

    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("plain text, 100% ASCII: {ok}", "plain text, 100% ASCII: {ok}")]
    [InlineData("got you — resuming", "got you - resuming")]
    [InlineData("multi: mmis · normal: lbol", "multi: mmis . normal: lbol")]
    [InlineData("a loop (≥2 coords)", "a loop (>=2 coords)")]
    [InlineData("King’s Road", "King's Road")]
    [InlineData("wait…", "wait...")]
    [InlineData("café", "caf?")]
    [InlineData("", "")]
    public void Ascii_SpellsOutWhatTheWireCannotCarry(string text, string expected)
    {
        Assert.Equal(expected, WireText.Ascii(text));
        Assert.All(WireText.Ascii(text), c => Assert.InRange(c, ' ', '~'));
    }

    [Fact]
    public void Ascii_LeavesPlainTextAsTheSameString()
    {
        string text = "walking to Town Square (1/1)";
        Assert.Same(text, WireText.Ascii(text));
    }

    // Whatever a handler hands the engine, what goes out encodes in Latin-1
    // without loss, on every channel.
    [Theory]
    [InlineData(ChatChannel.TelepathIncoming)]
    [InlineData(ChatChannel.Gangpath)]
    [InlineData(ChatChannel.Local)]
    public void AReplyOrRefusal_WithATypographicCharacter_GoesOutInAscii(ChatChannel channel)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerDatabase players = new(new ProfileService(), () => null);
        players.RecordObservation("Friend", @class: null, race: null, alignment: null,
            title: null, gang: null, role: null, nowUtc: Now);
        players.EditCustomization("Friend", new PlayerCustomization(RemoteControls: PlayerRemoteControls.All));
        RemoteCommandManager engine = new(new ChatRouter(router), new PartyState(), players);
        _routers[engine] = router;
        engine.SetWireSender(_replies.Add);
        engine.RegisterHandler("@probe", PlayerRemoteControls.QueryVersion, ctx =>
        {
            ctx.Reply("King’s Road — 42 rooms · 3 lines");
            ctx.Refuse("needs ≥2 rooms");
        });

        ChatLines.Hear(_routers[engine], new ChatLine(channel, "Friend", "@probe"));

        Assert.Equal(2, _replies.Count);
        foreach (byte[] sent in _replies)
        {
            Assert.All(sent, b => Assert.True(b is (byte)'\r' or (>= 0x20 and <= 0x7E)));
            string line = Encoding.Latin1.GetString(sent);
            Assert.DoesNotContain("?", line);
        }
        Assert.Contains("King's Road - 42 rooms . 3 lines", Encoding.Latin1.GetString(_replies[0]));
        Assert.Contains("needs >=2 rooms", Encoding.Latin1.GetString(_replies[1]));
    }

    // Every line @help can send is written in ASCII to begin with.
    [Fact]
    public void EveryHelpLine_IsWrittenInAscii()
    {
        foreach ((string command, RemoteCommandHelp help) in RemoteCommandCatalog.Help)
        {
            // Text that is already ASCII comes back from the sanitiser unchanged.
            Assert.Equal(help.Syntax, WireText.Ascii(help.Syntax));
            Assert.Equal(help.Description, WireText.Ascii(help.Description));
        }
    }

    // The fixed answers handlers and engines hand out by name.
    [Fact]
    public void TheNamedAnswers_AreWrittenInAscii()
    {
        string[] answers =
        {
            PartyComebackManager.LoopGateRefusal,
            PartyComebackManager.LairGateReply,
            PartyComebackManager.TrainTripReply,
            InventoryActionHandler.NotInBank,
            TrapReply.Stopped,
            TrapReply.Attempting("n"),
            TrapReply.Disarmed("n"),
            TrapReply.DisarmedEarlier("n", TimeSpan.FromSeconds(30)),
            TrapReply.AlreadyDisarmed("n"),
            TrapReply.NoTrap("n"),
            TrapReply.NoTrapAfterFailures("n", 5),
            TrapReply.CouldNotDisarm("n", 5),
        };

        Assert.All(answers, a => Assert.Equal(a, WireText.Ascii(a)));
    }
}
