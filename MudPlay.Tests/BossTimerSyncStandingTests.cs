using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// What the collector says each responder's set is to the requests the user made
// (TimerSyncStanding), which is what the merge window goes by when it decides
// whether a timer is merged in, synced or listed.
public sealed class BossTimerSyncStandingTests
{
    private const string Us = "Ourself";

    private static readonly BossTimerSyncRecord[] OneTimer =
        { new(1, "a", new DateTimeOffset(2026, 8, 22, 1, 0, 0, TimeSpan.Zero)) };

    private readonly MessageRouter _router = new();
    private readonly ChatRouter _chat;
    private readonly BossTimerSyncCollector _collector;
    private readonly List<BossTimerSyncResponse> _got = new();

    public BossTimerSyncStandingTests()
    {
        DefaultPatterns.Seed(_router);
        _chat = new ChatRouter(_router);
        _collector = new BossTimerSyncCollector(_chat,
            isOurName: name => name.Equals(Us, StringComparison.OrdinalIgnoreCase));
        _collector.ResponseReceived += _got.Add;
    }

    private static MudPlay.Terminal.LineExtractor.EmittedLine Shown(string text) =>
        new(text, new MudPlay.Terminal.CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    // What the user types at the terminal is told as it goes out, as the user's
    // own; the board then shows the line back.
    private void UserTypes(string line)
    {
        _chat.ObserveOutbound(Encoding.Latin1.GetBytes(line + "\r"), usersOwn: true);
        _router.Dispatch(Shown(line));
    }

    // A line the client put on the wire by itself or for another player.
    private void ClientSends(string line) =>
        _chat.ObserveOutbound(Encoding.Latin1.GetBytes(line + "\r"), usersOwn: false);

    private void GameConfirms(string name) => _router.Dispatch(Shown($"--- Telepath sent to {name} ---"));

    // Our own request coming back on a shared channel under our name.
    private void SeesUsAsk(ChatChannel channel) =>
        ChatLines.Hear(_router, channel, Us, "@timer sync");

    private void Answers(string sender, ChatChannel channel, int chunkChars = 100)
    {
        IReadOnlyList<string> chunks = BossTimerSyncCodec.Chunk(BossTimerSyncCodec.Encode(OneTimer), chunkChars);
        for (int i = 0; i < chunks.Count; i++)
        {
            string msg = $"{{{BossTimerQueryHandler.SyncResponseToken} {i + 1}/{chunks.Count} {chunks[i]}}}";
            ChatLines.Hear(_router, channel, sender, msg);
        }
    }

    private TimerSyncStanding[] Standings => _got.Select(r => r.Standing).ToArray();

    // A request of the user's on one channel, and what an answer on a channel from
    // a player is to it. The one player a telepath reached, alone, is the one asked;
    // anyone on a shared channel the user asked on is one of several, and so is a
    // telepath after a say (a sneaking client answers a say that way); a telepath
    // from a player the user did not ask answers nothing.
    [Theory]
    [InlineData(RemoteChannel.Telepath, "Bob", ChatChannel.TelepathIncoming, "Bob", TimerSyncStanding.TheOneAsked)]
    [InlineData(RemoteChannel.Telepath, "bob", ChatChannel.TelepathIncoming, "Bob", TimerSyncStanding.TheOneAsked)]
    [InlineData(RemoteChannel.Telepath, "Bob", ChatChannel.TelepathIncoming, "Sue", TimerSyncStanding.NotAsked)]
    [InlineData(RemoteChannel.Telepath, "Bob", ChatChannel.TelepathIncoming, "Bo", TimerSyncStanding.NotAsked)]
    [InlineData(RemoteChannel.Telepath, null, ChatChannel.TelepathIncoming, "Bob", TimerSyncStanding.NotAsked)]
    [InlineData(RemoteChannel.Gangpath, null, ChatChannel.Gangpath, "Bob", TimerSyncStanding.OneOfSeveral)]
    [InlineData(RemoteChannel.Broadcast, null, ChatChannel.Broadcast, "Bob", TimerSyncStanding.OneOfSeveral)]
    [InlineData(RemoteChannel.Local, null, ChatChannel.Local, "Bob", TimerSyncStanding.OneOfSeveral)]
    [InlineData(RemoteChannel.Local, null, ChatChannel.TelepathIncoming, "Bob", TimerSyncStanding.OneOfSeveral)]
    public void AnAnswerToARequestOfTheUsers_SaysWhatItIsToIt(
        RemoteChannel askedOn, string? reached, ChatChannel heardOn, string sender, TimerSyncStanding standing)
    {
        _collector.Begin(askedOn, reached);

        Answers(sender, heardOn);

        BossTimerSyncResponse response = Assert.Single(_got);
        Assert.Equal(TransferChannels.Of(heardOn), response.Channel);
        Assert.Equal(standing, response.Standing);
    }

    // A request the client sent for another player's remote command is listened on,
    // and nothing that answers it is an answer to the user: on every channel, the
    // player it was telepathed to included.
    [Theory]
    [InlineData(RemoteChannel.Telepath, "Mallory", ChatChannel.TelepathIncoming)]
    [InlineData(RemoteChannel.Gangpath, null, ChatChannel.Gangpath)]
    [InlineData(RemoteChannel.Broadcast, null, ChatChannel.Broadcast)]
    [InlineData(RemoteChannel.Local, null, ChatChannel.Local)]
    [InlineData(RemoteChannel.Local, null, ChatChannel.TelepathIncoming)]
    public void AnAnswerToARequestTheClientSentForAnotherPlayer_AnswersNothingOfTheUsers(
        RemoteChannel askedOn, string? reached, ChatChannel heardOn)
    {
        _collector.Begin(askedOn, reached, byUser: false);

        Answers("Mallory", heardOn);

        Assert.Equal(TimerSyncStanding.NotAsked, Assert.Single(_got).Standing);
        Assert.False(_collector.OnePlayerAsked);
    }

    // More than one player asked by telepath: each is asked, and neither is the one
    // player asked.
    [Fact]
    public void TwoPlayersAskedByTelepath_AreEachOneOfSeveral()
    {
        _collector.Begin(RemoteChannel.Telepath, "Bob");
        Assert.True(_collector.OnePlayerAsked);
        UserTypes("/su @timer sync");
        GameConfirms("Sue");
        Assert.False(_collector.OnePlayerAsked);

        Answers("Sue", ChatChannel.TelepathIncoming);
        Answers("Bob", ChatChannel.TelepathIncoming);
        Answers("Mallory", ChatChannel.TelepathIncoming);

        Assert.Equal(
            new[] { TimerSyncStanding.OneOfSeveral, TimerSyncStanding.OneOfSeveral, TimerSyncStanding.NotAsked },
            Standings);
    }

    // Once a shared channel is listened on as well, the telepathed player is one of
    // several, whichever was asked first and whoever asked on the shared channel:
    // typed by the user, or sent by the client for another player.
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void OnceASharedChannelIsListenedOn_TheTelepathedPlayerIsOneOfSeveral(bool sharedFirst, bool sharedByUser)
    {
        void AskTheGang()
        {
            if (sharedByUser) UserTypes("bg @timer sync"); else ClientSends("bg @timer sync");
            SeesUsAsk(ChatChannel.Gangpath);
        }
        _collector.Begin(RemoteChannel.Telepath, byUser: false);   // open and listening, nobody asked
        if (sharedFirst) AskTheGang();
        UserTypes("/Bob @timer sync");
        GameConfirms("Bob");
        if (!sharedFirst)
        {
            Answers("Bob", ChatChannel.TelepathIncoming);
            Assert.Equal(TimerSyncStanding.TheOneAsked, Assert.Single(_got).Standing);
            AskTheGang();
        }

        Answers("Bob", ChatChannel.TelepathIncoming);
        Answers("Ann", ChatChannel.Gangpath);

        Assert.Equal(TimerSyncStanding.OneOfSeveral, _got[^2].Standing);
        Assert.Equal(sharedByUser ? TimerSyncStanding.OneOfSeveral : TimerSyncStanding.NotAsked, _got[^1].Standing);
    }

    // Asked by telepath with no word yet from the game of who it reached, nobody
    // has been asked: the name typed is not a player's name until the game says so.
    [Fact]
    public void UntilTheGameNamesWhoATelepathReached_NobodyWasAsked()
    {
        _collector.Begin(RemoteChannel.Telepath);
        UserTypes("/Bob @timer sync");

        Answers("Bob", ChatChannel.TelepathIncoming);
        Assert.False(_collector.OnePlayerAsked);

        GameConfirms("Bob");
        Assert.True(_collector.OnePlayerAsked);
        Answers("Bob", ChatChannel.TelepathIncoming);

        Assert.Equal(new[] { TimerSyncStanding.NotAsked, TimerSyncStanding.TheOneAsked }, Standings);
    }

    // A telepath of ours that the client sent for another player (their relay-back,
    // a `@do` of theirs) asks nobody on the user's behalf, whatever the board then
    // shows of it; the user's own, confirmed, asks the player it reached.
    [Fact]
    public void ATelepathTheClientSentForAnotherPlayer_AsksNobody()
    {
        _collector.Begin(RemoteChannel.Telepath, byUser: false);

        ClientSends("/Friend @timer sync");
        _router.Dispatch(Shown("/Friend @timer sync"));
        GameConfirms("Friend");
        UserTypes("/Bob @timer sync");
        GameConfirms("Bob");
        Answers("Friend", ChatChannel.TelepathIncoming);
        Answers("Bob", ChatChannel.TelepathIncoming);

        Assert.Equal(new[] { TimerSyncStanding.NotAsked, TimerSyncStanding.TheOneAsked }, Standings);
    }

    // The confirmation of some other telepath asks nobody for timers. The user's
    // request drew no confirmation (its letters fitted two players, or it was not
    // sent); then a telepath to another player is confirmed, the user's own or one
    // the client sent. That player was not asked, and neither was anyone.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheConfirmationOfAnotherTelepath_AsksNobody(bool usersOwn)
    {
        _collector.Begin(RemoteChannel.Telepath, byUser: false);
        UserTypes("/bo @timer sync");

        if (usersOwn) UserTypes("/Sue hello"); else ClientSends("/Sue @health");
        GameConfirms("Sue");
        Answers("Sue", ChatChannel.TelepathIncoming);

        Assert.Equal(TimerSyncStanding.NotAsked, Assert.Single(_got).Standing);
        Assert.False(_collector.OnePlayerAsked);
    }

    // A set is as public as its most public piece: with one line of it heard on the
    // broadcast channel it is a set heard there, whichever channel the last came by,
    // and it is the answer of no one player.
    [Fact]
    public void ASetWithAPieceHeardOnBroadcast_IsASetHeardOnBroadcast()
    {
        _collector.Begin(RemoteChannel.Broadcast);
        _collector.Begin(RemoteChannel.Telepath, "Bob");
        IReadOnlyList<string> chunks = BossTimerSyncCodec.Chunk(BossTimerSyncCodec.Encode(OneTimer), 6);
        Assert.True(chunks.Count >= 2);

        for (int i = 0; i < chunks.Count; i++)
        {
            string line = $"{{{BossTimerQueryHandler.SyncResponseToken} {i + 1}/{chunks.Count} {chunks[i]}}}";
            ChatChannel by = i == chunks.Count - 1 ? ChatChannel.TelepathIncoming : ChatChannel.Broadcast;
            ChatLines.Hear(_router, by, "Bob", line);
        }

        BossTimerSyncResponse response = Assert.Single(_got);
        Assert.Equal(RemoteChannel.Broadcast, response.Channel);
        Assert.Equal(TimerSyncStanding.OneOfSeveral, response.Standing);
    }

    // A request adds to what was asked: a responder half-way through a set keeps
    // what has arrived, and one whose set was delivered may send again.
    [Fact]
    public void AFurtherRequest_KeepsAHalfReceivedSet_AndLetsADeliveredResponderAnswerAgain()
    {
        _collector.Begin(RemoteChannel.Gangpath);
        Answers("Bob", ChatChannel.Gangpath);
        IReadOnlyList<string> chunks = BossTimerSyncCodec.Chunk(BossTimerSyncCodec.Encode(OneTimer), 6);
        Assert.True(chunks.Count >= 2);
        string Line(int i) => $"{{{BossTimerQueryHandler.SyncResponseToken} {i + 1}/{chunks.Count} {chunks[i]}}}";
        ChatLines.Hear(_router, ChatChannel.Gangpath, "Sue", Line(0));

        _collector.Begin(RemoteChannel.Local);

        for (int i = 1; i < chunks.Count; i++)
            ChatLines.Hear(_router, ChatChannel.Gangpath, "Sue", Line(i));
        Answers("Bob", ChatChannel.Local);

        Assert.Equal(new[] { "Bob", "Sue", "Bob" }, _got.Select(r => r.Sender));
    }

    // A set that never completes is never delivered, so it is compared with
    // nothing; and a set whose pieces do not decode is discarded whole.
    [Fact]
    public void ASetNeverCompleted_OrThatDoesNotDecode_IsNeverDelivered()
    {
        _collector.Begin(RemoteChannel.Gangpath);
        IReadOnlyList<string> chunks = BossTimerSyncCodec.Chunk(BossTimerSyncCodec.Encode(OneTimer), 6);
        Assert.True(chunks.Count >= 2);
        string first = $"{{{BossTimerQueryHandler.SyncResponseToken} 1/{chunks.Count} {chunks[0]}}}";
        ChatLines.Hear(_router, ChatChannel.Gangpath, "Bob", first);

        // Each piece is well formed and the whole decodes to nothing.
        string a = $"{{{BossTimerQueryHandler.SyncResponseToken} 1/2 {chunks[0]}}}";
        string b = $"{{{BossTimerQueryHandler.SyncResponseToken} 2/2 !!!!}}";
        ChatLines.Hear(_router, ChatChannel.Gangpath, "Sue", a);
        ChatLines.Hear(_router, ChatChannel.Gangpath, "Sue", b);

        Assert.Empty(_got);
    }
}
