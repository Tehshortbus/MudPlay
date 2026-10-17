using MudPlay.Game;

namespace MudPlay.Tests;

// Another player's line on a chat channel.
internal readonly record struct ChatLine(ChatChannel Channel, string Sender, string Message)
{
    // The game's own wording for the line on its channel.
    public string Wire => Channel switch
    {
        ChatChannel.TelepathIncoming => $"{Sender} telepaths: {Message}",
        ChatChannel.Gossip => $"{Sender} gossips: {Message}",
        ChatChannel.Gangpath => $"{Sender} gangpaths: {Message}",
        ChatChannel.Broadcast => $"Broadcast from {Sender} \"{Message}\"",
        ChatChannel.Local => $"{Sender} says \"{Message}\"",
        ChatChannel.Yell => $"{Sender} yells \"{Message}\"",
        _ => throw new ArgumentOutOfRangeException(nameof(Channel), Channel, "no player speaks on this channel"),
    };
}
