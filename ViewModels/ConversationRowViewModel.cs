using Avalonia.Media;
using FujinTerm.Game;

namespace FujinTerm.ViewModels;

// One displayed row in the ConversationViewModel's filtered list. Wraps a
// ChatLogEntry with the channel-color brush + timestamp / channel-tag /
// speaker / message display strings the XAML binds to.
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
        SpeakerText   = FormatSpeaker(entry);
        MessageText   = entry.Message;
        ChannelBrush  = brushLookup(entry.Channel);
    }

    private static string FormatSpeaker(ChatLogEntry entry)
    {
        if (entry.Channel == ChatChannel.DaySeparator) return string.Empty;

        // Speaker is null for self-actions whose regex didn't capture a
        // name (e.g. "You yell ..." — the Megamind regex matches the verb
        // shape literally). Surface those as "You" so every chat row has
        // a consistent "<who>" prefix.
        string speaker = string.IsNullOrEmpty(entry.Speaker) ? "You" : entry.Speaker!;

        // RealmEvent rows read as a sentence ("Raijin entered the Realm")
        // so the trailing colon would feel wrong. Every other channel
        // pairs "<who>" with the message body and gets the colon.
        return entry.Channel == ChatChannel.RealmEvent ? speaker : speaker + ":";
    }

    private static string ChannelAbbrev(ChatChannel c) => c switch
    {
        ChatChannel.Gossip            => "GOSS",
        ChatChannel.Local             => "SAY",
        ChatChannel.TelepathIncoming  => "←TELE",
        ChatChannel.TelepathOutgoing  => "→TELE",
        ChatChannel.Gangpath          => "GANG",
        ChatChannel.Broadcast         => "BCAST",
        ChatChannel.Yell              => "YELL",
        ChatChannel.RealmEvent        => "REALM",
        ChatChannel.DaySeparator      => string.Empty,
        _ => "?",
    };
}
