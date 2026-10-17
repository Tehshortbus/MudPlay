using System.Text;
using MudPlay.Game;
using MudPlay.Services;

namespace MudPlay.Tests;

// A chat line dispatched through the router as the client receives one: its wire
// text fed through a ServerFeed, so the LineExtractor marks it chat and the router
// hands it to the conversation patterns ChatRouter (and through it the remote-command
// engine and the triggers) reads.
internal static class ChatLines
{
    public static void Hear(MessageRouter router, ChatLine line, DateTimeOffset? at = null)
        => new ServerFeed(router).Line(line.Wire, at);

    public static void Hear(MessageRouter router, ChatChannel channel, string sender, string message, DateTimeOffset? at = null)
        => Hear(router, new ChatLine(channel, sender, message), at);

    // Our own telepath as the client sees it go: the command on the wire, then the
    // game's acknowledgement, which ChatRouter pairs with it.
    public static void Telepath(MessageRouter router, ChatRouter chat, string to, string message, bool usersOwn = true)
    {
        chat.ObserveOutbound(Encoding.Latin1.GetBytes($"/{to} {message}\r"), usersOwn);
        new ServerFeed(router).Line($"--- Telepath sent to {to} ---");
    }
}
