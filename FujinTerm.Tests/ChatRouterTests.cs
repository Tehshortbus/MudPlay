using FujinTerm.Game;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

public sealed class ChatRouterTests
{
    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    /// <summary>
    /// Build a router with the default catalog seeded and a ChatRouter
    /// wired to it; collect every EntryClassified event into a list for
    /// assertions.
    /// </summary>
    private static (MessageRouter router, ChatRouter chat, List<ChatLogEntry> entries) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        List<ChatLogEntry> entries = new();
        chat.EntryClassified += entries.Add;
        return (router, chat, entries);
    }

    [Fact]
    public void Gossip_ClassifiesWithSpeakerAndMessage()
    {
        var (router, _, entries) = Setup();
        router.Dispatch(Line("Forged gossips: hello world"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.Gossip, e.Channel);
        Assert.Equal("Forged",           e.Speaker);
        Assert.Equal("hello world",      e.Message);
    }

    [Fact]
    public void TelepathIn_ClassifiesAsTelepathIncoming()
    {
        var (router, _, entries) = Setup();
        router.Dispatch(Line("Forged telepaths: pst!"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.TelepathIncoming, e.Channel);
        Assert.Equal("Forged", e.Speaker);
        Assert.Equal("pst!",   e.Message);
    }

    [Fact]
    public void TelepathOut_ClassifiesAsOutgoingWithRecipientAsSpeaker()
    {
        var (router, _, entries) = Setup();
        router.Dispatch(Line("--- Telepath sent to Goblin ---"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.TelepathOutgoing, e.Channel);
        Assert.Equal("Goblin", e.Speaker);
        Assert.Equal(string.Empty, e.Message);
    }

    [Fact]
    public void TelepathOut_TypedLineOnScreen_AttributesMessage()
    {
        var (router, _, entries) = Setup();
        // The typed /-line renders on the terminal and is sniffed off the screen.
        router.Dispatch(Line("/Goblin hello there"));
        router.Dispatch(Line("--- Telepath sent to Goblin ---"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.TelepathOutgoing, e.Channel);
        Assert.Equal("Goblin",      e.Speaker);
        Assert.Equal("hello there",  e.Message);
    }

    [Fact]
    public void TelepathOut_EngineOutboundBurst_AttributesMessage()
    {
        var (router, chat, entries) = Setup();
        // An engine broadcast (@wealth toll probe) never touches the screen —
        // the raw "/<recipient> <atCommand>\r" burst must attribute the entry.
        chat.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes("/Raijin @wealth\r"));
        router.Dispatch(Line("--- Telepath sent to Raijin ---"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.TelepathOutgoing, e.Channel);
        Assert.Equal("Raijin",  e.Speaker);
        Assert.Equal("@wealth", e.Message);
    }

    [Fact]
    public void ObserveOutbound_NonTelepathBytes_LeaveMessageEmpty()
    {
        var (router, chat, entries) = Setup();
        // A plain movement burst isn't a /-telepath — it must not seed a message.
        chat.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes("north\r"));
        router.Dispatch(Line("--- Telepath sent to Raijin ---"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(string.Empty, e.Message);
    }

    [Fact]
    public void Yell_SelfHasNoSpeaker()
    {
        var (router, _, entries) = Setup();
        router.Dispatch(Line(@"You yell ""help""")) ;

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.Yell, e.Channel);
        Assert.Null(e.Speaker);
        Assert.Equal("help", e.Message);
    }

    [Fact]
    public void Yell_OtherHasSpeaker()
    {
        var (router, _, entries) = Setup();
        router.Dispatch(Line(@"Forged yells ""ouch"""));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.Yell, e.Channel);
        Assert.Equal("Forged", e.Speaker);
        Assert.Equal("ouch",   e.Message);
    }

    [Fact]
    public void PlayerEnters_ClassifiesAsRealmEvent()
    {
        var (router, _, entries) = Setup();
        router.Dispatch(Line("Forged just entered the Realm."));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.RealmEvent, e.Channel);
        Assert.Equal("Forged",               e.Speaker);
        Assert.Equal("entered the Realm",    e.Message);
    }

    [Fact]
    public void Dispose_UnsubscribesAllHandlers()
    {
        var (router, chat, entries) = Setup();
        chat.Dispose();
        router.Dispatch(Line("Forged gossips: hi"));
        Assert.Empty(entries);
        Assert.Equal(0, router.SubscriptionCount);
    }

    [Fact]
    public void NonChatLines_AreIgnored()
    {
        var (router, _, entries) = Setup();
        router.Dispatch(Line("Forged slashes Goblin for 17 damage!"));
        router.Dispatch(Line("Obvious exits: north, south"));
        Assert.Empty(entries);
    }
}
