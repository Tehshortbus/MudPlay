using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game;
using FujinTerm.Services;

namespace FujinTerm.ViewModels;

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
    private readonly Dictionary<ChatChannel, IBrush> _channelBrushes;
    private bool _disposed;

    // Guards OnInputTextChanged so a recall-driven InputText set (Up/Down
    // or dropdown pick) doesn't reset the very navigation cursor it's
    // driven by — only the user typing should reset it.
    private bool _suppressNavReset;

    public ObservableCollection<ConversationRowViewModel> Rows { get; } = new();

    // Recent commands for the recall dropdown, newest first.
    public ObservableCollection<string> RecentCommands { get; } = new();

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

    // Drives the input box's recall-dropdown button (disabled when nothing's been sent yet).
    [ObservableProperty] private bool _hasRecentCommands;

    // Fired by the window's code-behind to scroll the newest row into view.
    public event Action<ConversationRowViewModel>? ScrollToRowRequested;

    public ConversationViewModel(ChatHistoryStore history, CommandHistory commands, Action<string> sendUserText, Application app)
    {
        _history = history;
        _commands = commands;
        _nav = new CommandHistoryNavigator(commands);
        _sendUserText = sendUserText;
        _channelBrushes = BuildChannelBrushMap(app);

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

    partial void OnShowGossipChanged(bool value)      => Rebuild();
    partial void OnShowLocalChanged(bool value)       => Rebuild();
    partial void OnShowTelepathChanged(bool value)    => Rebuild();
    partial void OnShowGangpathChanged(bool value)    => Rebuild();
    partial void OnShowBroadcastChanged(bool value)   => Rebuild();
    partial void OnShowYellChanged(bool value)        => Rebuild();
    partial void OnShowRealmEventChanged(bool value)  => Rebuild();
    partial void OnSearchTextChanged(string value)    => Rebuild();

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
        ConversationRowViewModel row = new(entry, ChannelBrush);
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
        _ => true,
    };

    private IBrush ChannelBrush(ChatChannel c)
        => _channelBrushes.TryGetValue(c, out IBrush? brush) ? brush : Brushes.Gray;

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

    private static Dictionary<ChatChannel, IBrush> BuildChannelBrushMap(Application app)
    {
        IBrush Lookup(string key)
            => app.TryGetResource(key, null, out object? v) && v is IBrush b ? b : Brushes.Gray;

        return new()
        {
            [ChatChannel.Gossip]            = Lookup("AccentCyanBrush"),
            [ChatChannel.Local]             = Lookup("ChromeFgBrush"),
            [ChatChannel.TelepathIncoming]  = Lookup("AccentMagentaBrush"),
            [ChatChannel.TelepathOutgoing]  = Lookup("AccentMagentaBrush"),
            [ChatChannel.Gangpath]          = Lookup("AccentGreenBrush"),
            [ChatChannel.Broadcast]         = Lookup("AccentYellowBrush"),
            [ChatChannel.Yell]              = Lookup("AccentAmberBrush"),
            [ChatChannel.RealmEvent]        = Lookup("ChromeFgMutedBrush"),
            [ChatChannel.DaySeparator]      = Lookup("ChromeFgMutedBrush"),
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ((INotifyCollectionChanged)_history.Entries).CollectionChanged -= OnHistoryChanged;
        _commands.Changed -= OnCommandsChanged;
    }
}
