using Avalonia.Media;
using FujinTerm.Game;

namespace FujinTerm.ViewModels;

/// <summary>
/// One displayed row in the <see cref="ConversationViewModel"/>'s filtered
/// list. Wraps a <see cref="ChatLogEntry"/> with the channel-color brush +
/// timestamp / channel-tag / speaker / message display strings the XAML
/// binds to.
/// </summary>
public sealed class ConversationRowViewModel
{
    public ChatLogEntry Entry { get; }

    public string TimestampText { get; }
    public string ChannelText { get; }
    public string SpeakerText { get; }
    public string MessageText { get; }
    public IBrush ChannelBrush { get; }
    public bool IsDaySeparator => Entry.Channel == ChatChannel.DaySeparator;

    public ConversationRowViewModel(ChatLogEntry entry, Func<ChatChannel, IBrush> brushLookup)
    {
        Entry = entry;
        TimestampText = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss");
        ChannelText   = ChannelAbbrev(entry.Channel);
        SpeakerText   = entry.Speaker ?? string.Empty;
        MessageText   = entry.Message;
        ChannelBrush  = brushLookup(entry.Channel);
    }

    private static string ChannelAbbrev(ChatChannel c) => c switch
    {
        ChatChannel.Gossip            => "GOSS",
        ChatChannel.Local             => "SAY",
        ChatChannel.TelepathIncoming  => "TELE",
        ChatChannel.TelepathOutgoing  => "→TEL",
        ChatChannel.Gangpath          => "GANG",
        ChatChannel.Broadcast         => "BCAST",
        ChatChannel.Yell              => "YELL",
        ChatChannel.RealmEvent        => "REALM",
        ChatChannel.DaySeparator      => string.Empty,
        _ => "?",
    };
}
