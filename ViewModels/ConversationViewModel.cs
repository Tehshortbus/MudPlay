using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.ViewModels;

// View-model behind the Conversation window. Mirrors entries from
// ChatHistoryStore.Entries through a channel + search filter into Rows, owns
// the per-channel show / hide toggles, and drives the bottom input field
// that sends typed text to the game.
public sealed partial class ConversationViewModel : ObservableObject, IDisposable
{
    private readonly ChatHistoryStore _history;
    private readonly CommandHistory _commands;
    private readonly CommandHistoryNavigator _nav;
    private readonly Action<string> _sendUserText;
    private readonly ProfileService _profile;
    // Two brushes per channel: the accent (channel tag / speaker / toolbar
    // toggle) and the message-body text colour. Both start from the theme
    // defaults and are overlaid with the character's per-channel Talk overrides.
    private readonly Dictionary<ChatChannel, IBrush> _channelBrushes;
    private readonly Dictionary<ChatChannel, IBrush> _textBrushes;
    private bool _disposed;

    // Guards OnInputTextChanged so a recall-driven InputText set (Up/Down
    // or dropdown pick) doesn't reset the very navigation cursor it's
    // driven by — only the user typing should reset it.
    private bool _suppressNavReset;

    public ObservableCollection<ConversationRowViewModel> Rows { get; } = new();

    // Recent commands for the recall dropdown, newest first.
    public ObservableCollection<string> RecentCommands { get; } = new();

    // Row typography resolved once from the character's Talk settings so the
    // window can be re-fonted from Settings → Talk without touching each row
    // template. Empty ConvoFont falls back to the bundled JetBrains Mono
    // (FontMono); ConvoFontSize <= 0 falls back to the built-in 12. Applied on
    // window open — a read-only toggle window picks up edits on the next open.
    private const double DefaultMessageFontSize = 12;   // points
    // The Talk size setting is in POINTS, but these values bind straight to
    // Avalonia TextBlock.FontSize, which is DIP — so convert, exactly like the
    // terminal and Backscroll do at their draw sites. Without it every size
    // renders ~25% small and a size change barely moves (the "does nothing" bug).
    private const double PointToPixel = 96.0 / 72.0;
    public FontFamily RowFontFamily { get; }
    // Message body size in POINTS (from Talk settings); MessageFontSize / MetaFontSize
    // are the DIP equivalents Avalonia actually renders.
    private readonly double _messagePointSize;
    public double MessageFontSize => _messagePointSize * PointToPixel;
    // Timestamp / channel-tag / speaker sit one point smaller than the message
    // body — take the point off BEFORE the DIP conversion so the gap stays a
    // true point, not ~0.75pt.
    public double MetaFontSize => Math.Max(1, _messagePointSize - 1) * PointToPixel;

    // Accent brushes the filter-toolbar toggles paint themselves with — the
    // per-channel Label colour after overrides. Resolved once at open.
    public IBrush GossipBrush    => ChannelBrush(ChatChannel.Gossip);
    public IBrush LocalBrush     => ChannelBrush(ChatChannel.Local);
    public IBrush TelepathBrush  => ChannelBrush(ChatChannel.TelepathIncoming);
    public IBrush GangpathBrush  => ChannelBrush(ChatChannel.Gangpath);
    public IBrush BroadcastBrush => ChannelBrush(ChatChannel.Broadcast);
    public IBrush YellBrush      => ChannelBrush(ChatChannel.Yell);
    public IBrush RealmBrush     => ChannelBrush(ChatChannel.RealmEvent);

    // Per-channel filter toggles. Default true (everything visible).
    // Telepaths in + out share one toggle — they're conceptually the same
    // private-message stream from the user's perspective.
    [ObservableProperty] private bool _showGossip     = true;
    [ObservableProperty] private bool _showLocal      = true;
    [ObservableProperty] private bool _showTelepath   = true;
    [ObservableProperty] private bool _showGangpath   = true;
    [ObservableProperty] private bool _showBroadcast  = true;
    [ObservableProperty] private bool _showYell       = true;
    [ObservableProperty] private bool _showRealmEvent = true;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _autoScroll   = true;

    [ObservableProperty] private string _inputText = string.Empty;

    // Cap the input box at the terminal's own local-input line limit (the MUD's
    // wire-level cap), so a line typed here can't exceed what the terminal would
    // accept — the two stay in lockstep off the single LocalInputBuffer.MaxLength.
    public int MaxInputLength => LocalInputBuffer.MaxLength;

    // Drives the input box's recall-dropdown button (disabled when nothing's been sent yet).
    [ObservableProperty] private bool _hasRecentCommands;

    // Fired by the window's code-behind to scroll the newest row into view.
    public event Action<ConversationRowViewModel>? ScrollToRowRequested;

    public ConversationViewModel(ChatHistoryStore history, CommandHistory commands, Action<string> sendUserText, Application app, TalkSettings talk, ProfileService profile)
    {
        _history = history;
        _commands = commands;
        _nav = new CommandHistoryNavigator(commands);
        _sendUserText = sendUserText;
        _profile = profile;
        _channelBrushes = BuildChannelBrushMap(app);
        _textBrushes = BuildTextBrushMap(app);
        ApplyColorOverrides(talk.ChannelColors);

        RowFontFamily = ResolveFont(app, talk.ConvoFont);
        _messagePointSize = talk.ConvoFontSize > 0 ? talk.ConvoFontSize : DefaultMessageFontSize;

        // Seed the header toggles from the character's saved state. Direct field
        // writes (not the generated setters) so no change notification fires and
        // this restore isn't mistaken for a user edit that needs persisting.
        _showGossip     = talk.ConvoShowGossip;
        _showLocal      = talk.ConvoShowLocal;
        _showTelepath   = talk.ConvoShowTelepath;
        _showGangpath   = talk.ConvoShowGangpath;
        _showBroadcast  = talk.ConvoShowBroadcast;
        _showYell       = talk.ConvoShowYell;
        _showRealmEvent = talk.ConvoShowRealmEvent;
        _autoScroll     = talk.ConvoAutoScroll;

        Rebuild();
        RebuildRecentCommands();
        // INotifyCollectionChanged: subscribe to the live history so new
        // entries flow into Rows. ReadOnlyObservableCollection<T> forwards
        // its underlying collection's events.
        ((INotifyCollectionChanged)_history.Entries).CollectionChanged += OnHistoryChanged;
        _commands.Changed += OnCommandsChanged;
    }

    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            Rebuild();
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            foreach (ChatLogEntry entry in e.NewItems)
            {
                if (Passes(entry)) AddRow(entry, scrollIntoView: true);
            }
            return;
        }

        // Removal / move / replace aren't expected from the store today,
        // but a full rebuild is the safe fallback.
        Rebuild();
    }

    partial void OnShowGossipChanged(bool value)      { Rebuild(); PersistFilters(); }
    partial void OnShowLocalChanged(bool value)       { Rebuild(); PersistFilters(); }
    partial void OnShowTelepathChanged(bool value)    { Rebuild(); PersistFilters(); }
    partial void OnShowGangpathChanged(bool value)    { Rebuild(); PersistFilters(); }
    partial void OnShowBroadcastChanged(bool value)   { Rebuild(); PersistFilters(); }
    partial void OnShowYellChanged(bool value)        { Rebuild(); PersistFilters(); }
    partial void OnShowRealmEventChanged(bool value)  { Rebuild(); PersistFilters(); }
    partial void OnAutoScrollChanged(bool value)      => PersistFilters();
    // Search text is transient — it filters the live view but isn't persisted.
    partial void OnSearchTextChanged(string value)    => Rebuild();

    // Write the header toggle state back to the character's "Talk" section. A
    // read-modify-write on just these fields, so it can't clobber the other Talk
    // settings (or a colour/font edit made in the Settings window).
    private void PersistFilters()
    {
        _profile.UpdateSection<TalkSettings>("Talk", dto =>
        {
            dto.ConvoShowGossip     = ShowGossip;
            dto.ConvoShowLocal      = ShowLocal;
            dto.ConvoShowTelepath   = ShowTelepath;
            dto.ConvoShowGangpath   = ShowGangpath;
            dto.ConvoShowBroadcast  = ShowBroadcast;
            dto.ConvoShowYell       = ShowYell;
            dto.ConvoShowRealmEvent = ShowRealmEvent;
            dto.ConvoAutoScroll     = AutoScroll;
        });
    }

    private void Rebuild()
    {
        Rows.Clear();
        foreach (ChatLogEntry entry in _history.Entries)
        {
            if (Passes(entry)) AddRow(entry, scrollIntoView: false);
        }
        // A filter-toggle / search rebuild repopulates the whole list. Scroll
        // once to the freshest row at the end rather than per row — invoking
        // ScrollToRowRequested for every added row posts a deferred
        // ScrollIntoView each time, so the virtualizing panel shudders down
        // through the entire history before settling instead of a smooth
        // hide / show.
        if (Rows.Count > 0) ScrollToRowRequested?.Invoke(Rows[^1]);
    }

    private void AddRow(ChatLogEntry entry, bool scrollIntoView)
    {
        ConversationRowViewModel row = new(entry, ChannelBrush, TextBrush);
        Rows.Add(row);
        if (scrollIntoView) ScrollToRowRequested?.Invoke(row);
    }

    // Filter predicate: channel toggle + substring search across speaker and
    // message. Day separators always pass (they're visual breaks, not
    // content).
    private bool Passes(ChatLogEntry entry)
    {
        if (entry.Channel == ChatChannel.DaySeparator) return true;
        if (!ChannelAllowed(entry.Channel)) return false;
        if (string.IsNullOrEmpty(SearchText)) return true;
        return (entry.Speaker?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false)
            || entry.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private bool ChannelAllowed(ChatChannel c) => c switch
    {
        ChatChannel.Gossip            => ShowGossip,
        ChatChannel.Local             => ShowLocal,
        ChatChannel.TelepathIncoming  => ShowTelepath,
        ChatChannel.TelepathOutgoing  => ShowTelepath,
        ChatChannel.Gangpath          => ShowGangpath,
        ChatChannel.Broadcast         => ShowBroadcast,
        ChatChannel.Yell              => ShowYell,
        ChatChannel.RealmEvent        => ShowRealmEvent,
        // Server PvP announcements share the Realm filter — no separate toggle.
        ChatChannel.Server            => ShowRealmEvent,
        // Actions / emotes are room-local, so they ride the "say" (Local) toggle.
        ChatChannel.Social            => ShowLocal,
        _ => true,
    };

    private IBrush ChannelBrush(ChatChannel c)
        => _channelBrushes.TryGetValue(c, out IBrush? brush) ? brush : Brushes.Gray;

    private IBrush TextBrush(ChatChannel c)
        => _textBrushes.TryGetValue(c, out IBrush? brush) ? brush : Brushes.Gray;

    // Send InputText to the game and clear the field.
    [RelayCommand]
    private void SendInput()
    {
        if (string.IsNullOrEmpty(InputText)) return;
        _commands.Record(InputText);
        _sendUserText(InputText);
        InputText = string.Empty;
        _nav.Reset();
    }

    // Up-arrow recall — replace the input with an older sent command.
    public void RecallPrevious()
    {
        if (_nav.Previous(InputText) is { } text) SetInputSuppressed(text);
    }

    // Down-arrow recall — step toward the newest command / the in-progress line.
    public void RecallNext()
    {
        if (_nav.Next() is { } text) SetInputSuppressed(text);
    }

    private void SetInputSuppressed(string text)
    {
        _suppressNavReset = true;
        InputText = text;
        _suppressNavReset = false;
    }

    partial void OnInputTextChanged(string value)
    {
        // Only a fresh user edit resets recall browsing; our own recall
        // writes are suppressed so cycling stays anchored.
        if (!_suppressNavReset) _nav.Reset();
    }

    private void OnCommandsChanged() => RebuildRecentCommands();

    private void RebuildRecentCommands()
    {
        RecentCommands.Clear();
        IReadOnlyList<string> e = _commands.Entries;
        for (int i = e.Count - 1; i >= 0; i--)
            RecentCommands.Add(e[i]);
        HasRecentCommands = e.Count > 0;
    }

    // A stored avares:// URI wins; empty means the JetBrains Mono theme default.
    private static FontFamily ResolveFont(Application app, string uri)
    {
        if (!string.IsNullOrWhiteSpace(uri)) return new FontFamily(uri);
        return app.TryGetResource("FontMono", null, out object? v) && v is FontFamily f
            ? f : new FontFamily("monospace");
    }

    private static IBrush LookupBrush(Application app, string key)
        => app.TryGetResource(key, null, out object? v) && v is IBrush b ? b : Brushes.Gray;

    private static Dictionary<ChatChannel, IBrush> BuildChannelBrushMap(Application app)
        => new()
        {
            [ChatChannel.Gossip]            = LookupBrush(app, "AccentCyanBrush"),
            [ChatChannel.Local]             = LookupBrush(app, "ChromeFgBrush"),
            [ChatChannel.TelepathIncoming]  = LookupBrush(app, "AccentMagentaBrush"),
            [ChatChannel.TelepathOutgoing]  = LookupBrush(app, "AccentMagentaBrush"),
            [ChatChannel.Gangpath]          = LookupBrush(app, "AccentGreenBrush"),
            [ChatChannel.Broadcast]         = LookupBrush(app, "AccentYellowBrush"),
            [ChatChannel.Yell]              = LookupBrush(app, "AccentAmberBrush"),
            [ChatChannel.RealmEvent]        = LookupBrush(app, "AccentRedBrush"),
            [ChatChannel.Server]            = LookupBrush(app, "AccentRedBrush"),
            // Actions/emotes keep the say chip (white/chrome); only the body is green.
            [ChatChannel.Social]            = LookupBrush(app, "ChromeFgBrush"),
            [ChatChannel.DaySeparator]      = LookupBrush(app, "ChromeFgMutedBrush"),
        };

    // Message-body text defaults to the chrome foreground for every channel;
    // per-channel Text overrides replace individual entries.
    private static Dictionary<ChatChannel, IBrush> BuildTextBrushMap(Application app)
    {
        IBrush def = LookupBrush(app, "ChromeFgBrush");
        Dictionary<ChatChannel, IBrush> map = new();
        foreach (ChatChannel c in Enum.GetValues<ChatChannel>()) map[c] = def;
        // Actions/emotes render their body full green (their native wire colour).
        map[ChatChannel.Social] = LookupBrush(app, "AccentGreenBrush");
        return map;
    }

    // Overlay the character's per-channel Talk colours onto the default maps.
    // Keys are the seven user-facing channel groups; both telepath directions
    // and Server/RealmEvent collapse onto their group so one override covers
    // the whole stream. A blank or unparseable hex leaves the default intact.
    private void ApplyColorOverrides(Dictionary<string, ChannelColor>? overrides)
    {
        if (overrides is null) return;
        foreach (ChatChannel c in Enum.GetValues<ChatChannel>())
        {
            if (GroupKey(c) is not { } key) continue;
            if (!overrides.TryGetValue(key, out ChannelColor? co) || co is null) continue;
            if (ParseBrush(co.Label) is { } lb) _channelBrushes[c] = lb;
            if (ParseBrush(co.Text)  is { } tb) _textBrushes[c] = tb;
        }
    }

    // The stable override key for a channel — the seven filter-toggle groups.
    // Channels with no toggle (DaySeparator) return null and take no override.
    private static string? GroupKey(ChatChannel c) => c switch
    {
        ChatChannel.Gossip           => "Gossip",
        ChatChannel.Local            => "Local",
        ChatChannel.TelepathIncoming => "Telepath",
        ChatChannel.TelepathOutgoing => "Telepath",
        ChatChannel.Gangpath         => "Gangpath",
        ChatChannel.Broadcast        => "Broadcast",
        ChatChannel.Yell             => "Yell",
        ChatChannel.RealmEvent       => "RealmEvent",
        ChatChannel.Server           => "RealmEvent",
        // Grouped with Local so a "say" colour override + the say toggle cover it.
        ChatChannel.Social           => "Local",
        _ => null,
    };

    private static IBrush? ParseBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return new SolidColorBrush(Color.Parse(hex)); }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ((INotifyCollectionChanged)_history.Entries).CollectionChanged -= OnHistoryChanged;
        _commands.Changed -= OnCommandsChanged;
    }
}
