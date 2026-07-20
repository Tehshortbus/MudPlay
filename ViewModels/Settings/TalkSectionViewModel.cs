using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

// "Talk" tab — engine-level policy for RemoteCommandManager. Follows the
// PartySectionViewModel pattern: Apply / Discard against an in-memory edit set,
// persist as the "Talk" entry in CharacterProfile.Settings, and push the
// resulting DTO into the live engine via ApplyToServices so changes take effect
// without a profile reload.
public sealed partial class TalkSectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "Talk";

    private readonly ProfileService _profile;
    private Control? _view;
    private bool _suppressDirty;
    private bool _dirty;

    public override string Id => "talk";
    public override string Title => "Talk";
    public override bool IsDirty => _dirty;

    // True when a profile is loaded — editor is hidden otherwise.
    public bool HasProfile => _profile.Current is not null;

    public string PhaseTag => "Phase 6 (RemoteCommandManager)";

    public string Description =>
        "Engine-level policy for inbound @-commands. " +
        "Per-channel disable rows below cover only the three channels the engine ever listens on — Gossip / Auction " +
        "/ Broadcast / Yell are hard-excluded engine-wide and don't need a toggle. " +
        "Per-player permissions live on the Game Data → Players tab; this tab is the master policy layer above them.";

    public override Control View => _view ??= new TalkSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "Talk", "Remote", "@-command", "Disallow",
        "telepaths", "gangpaths", "say", "local",
        "failure message", "party commands", "kill switch", "greet",
        "look", "look back", "looking at you", "inventory", "inspect players",
        "log conversations", "log transactions", "log lines", "chatlog", "logging",
        "conversation font", "font", "font size", "typeface",
    };

    // ----- Wired knobs -----

    [ObservableProperty] private bool _disallowAllRemoteCommands;

    [ObservableProperty] private bool _disallowPartyCommands;

    [ObservableProperty] private bool _disallowRemoteFromTelepaths;

    [ObservableProperty] private bool _disallowRemoteFromGangpaths;

    [ObservableProperty] private bool _disallowRemoteFromLocal;

    [ObservableProperty] private bool _warnOnInvalidRemoteCommand = true;

    [ObservableProperty] private string _remoteCommandFailureMessage = "command invalid or not allowed";

    // Settings → Talk "Greet players when first met". Char-tier; default off.
    [ObservableProperty] private bool _greetPlayersWhenFirstMet;

    // Settings → Talk reactive-look toggles. Char-tier; both default off.
    [ObservableProperty] private bool _lookBackWhenLookedAt;
    [ObservableProperty] private bool _lookAtPlayersOnArrival;

    // Session logging (Char-tier, default on). The two toggles persist the
    // Conversation window / Transaction history to rolling per-character files;
    // the shared cap keeps only the last N lines on disk.
    [ObservableProperty] private bool _logConversations = true;
    [ObservableProperty] private bool _logTransactions = true;
    [ObservableProperty] private int _logMaxLines = 2000;

    // ----- Conversation window typography (Char-tier) -----
    // Font family + size the Conversation window renders its rows with. The
    // default is the bundled JetBrains Mono at size 12, tagged "{default}" in
    // the pickers; a non-default pick persists as an avares:// URI / point size
    // and is read back when the window next opens.
    private const string DefaultConvoFontUri =
        "avares://FujinTerm/Assets/Fonts/JetBrainsMono-Regular.ttf#JetBrains Mono";
    private const double DefaultConvoFontSize = 12;

    public IReadOnlyList<FontFamilyOption> ConvoFontOptions { get; } = new[]
    {
        new FontFamilyOption("JetBrains Mono {default}", DefaultConvoFontUri),
        new FontFamilyOption("IBM Plex Sans",
            "avares://FujinTerm/Assets/Fonts/IBMPlexSans-Regular.ttf#IBM Plex Sans"),
        new FontFamilyOption("MX437 IBM VGA",
            "avares://FujinTerm/Assets/Fonts/Mx437_IBM_VGA_8x16.ttf#Mx437 IBM VGA 8x16"),
    };

    public IReadOnlyList<FontSizeOption> ConvoFontSizeOptions { get; } = BuildConvoFontSizes();

    [ObservableProperty] private FontFamilyOption? _selectedConvoFont;
    [ObservableProperty] private FontSizeOption? _selectedConvoFontSize;

    private static IReadOnlyList<FontSizeOption> BuildConvoFontSizes()
    {
        double[] sizes = { 10, 11, 12, 13, 14, 16, 18, 20 };
        List<FontSizeOption> list = new(sizes.Length);
        foreach (double s in sizes)
            list.Add(new FontSizeOption(
                s == DefaultConvoFontSize ? $"{s:0} {{default}}" : $"{s:0}", s));
        return list;
    }

    // ----- Per-channel Conversation colours (Char-tier) -----
    // One row per user-facing channel. Each holds an optional Label (accent) and
    // Text (message-body) colour; blank falls back to the theme default the
    // ConversationViewModel resolves. Keys mirror ConversationViewModel.GroupKey.
    public ObservableCollection<ChannelColorRowViewModel> ChannelColorRows { get; } = new();

    private void BuildChannelColorRows()
    {
        Color text = ResColor("ChromeFgBrush");
        (string Key, string Name, string LabelKey)[] channels =
        {
            ("Gossip",     "Gossip",    "AccentCyanBrush"),
            ("Local",      "Say",       "ChromeFgBrush"),
            ("Telepath",   "Telepath",  "AccentMagentaBrush"),
            ("Gangpath",   "Gang",      "AccentGreenBrush"),
            ("Broadcast",  "Broadcast", "AccentYellowBrush"),
            ("Yell",       "Yell",      "AccentAmberBrush"),
            ("RealmEvent", "Realm",     "ChromeFgMutedBrush"),
        };
        foreach ((string key, string name, string labelKey) in channels)
            ChannelColorRows.Add(new ChannelColorRowViewModel(
                key, name, ResColor(labelKey), text, MarkDirty));
    }

    // Resolve a theme brush resource to its underlying Color so the picker can
    // seed and compare against it. Solid brushes are the only kind in the
    // palette; anything else falls back to grey.
    private static Color ResColor(string key)
        => Application.Current is { } app
           && app.TryGetResource(key, null, out object? v) && v is ISolidColorBrush b
               ? b.Color : Colors.Gray;

    public TalkSectionViewModel() : this(AppServices.Current.Profile) { }

    public TalkSectionViewModel(ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;
        OnDispose(() =>
        {
            _profile.ProfileLoaded -= OnProfileChanged;
            _profile.ProfileClosed -= OnProfileClosedExternally;
        });
        BuildChannelColorRows();
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
    }

    public override void Apply()
    {
        if (_profile.Current is not { } profile) return;

        TalkSettings dto = new()
        {
            DisallowAllRemoteCommands        = DisallowAllRemoteCommands,
            DisallowPartyCommands            = DisallowPartyCommands,
            DisallowRemoteFromTelepaths      = DisallowRemoteFromTelepaths,
            DisallowRemoteFromGangpaths      = DisallowRemoteFromGangpaths,
            DisallowRemoteFromLocal          = DisallowRemoteFromLocal,
            WarnOnInvalidRemoteCommand       = WarnOnInvalidRemoteCommand,
            RemoteCommandFailureMessage      = RemoteCommandFailureMessage ?? string.Empty,
            GreetPlayersWhenFirstMet         = GreetPlayersWhenFirstMet,
            LookBackWhenLookedAt             = LookBackWhenLookedAt,
            LookAtPlayersOnArrival           = LookAtPlayersOnArrival,
            LogConversations                 = LogConversations,
            LogTransactions                  = LogTransactions,
            LogMaxLines                      = LogMaxLines,
            // Store "" / 0 for the default so the delta stays clean and tracks
            // the app default if it ever moves.
            ConvoFont = SelectedConvoFont is { } cf && cf.Uri != DefaultConvoFontUri ? cf.Uri : "",
            ConvoFontSize = SelectedConvoFontSize is { } cs && cs.Value != DefaultConvoFontSize ? cs.Value : 0,
            ChannelColors = BuildChannelColors(),
        };

        profile.Settings ??= new();
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(dto);
        _profile.Save();

        ApplyToServices(dto);
        ClearDirty();
    }

    public override void Discard()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        ClearDirty();
    }

    private void OnProfileChanged(CharacterProfile _) => ReloadAfterProfileSwap();
    private void OnProfileClosedExternally() => ReloadAfterProfileSwap();

    private void ReloadAfterProfileSwap()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        ClearDirty();
        OnPropertyChanged(nameof(HasProfile));
    }

    private void LoadFromProfile()
    {
        TalkSettings dto = ReadOrDefault();
        DisallowAllRemoteCommands       = dto.DisallowAllRemoteCommands;
        DisallowPartyCommands           = dto.DisallowPartyCommands;
        DisallowRemoteFromTelepaths     = dto.DisallowRemoteFromTelepaths;
        DisallowRemoteFromGangpaths     = dto.DisallowRemoteFromGangpaths;
        DisallowRemoteFromLocal         = dto.DisallowRemoteFromLocal;
        WarnOnInvalidRemoteCommand      = dto.WarnOnInvalidRemoteCommand;
        RemoteCommandFailureMessage     = dto.RemoteCommandFailureMessage;
        GreetPlayersWhenFirstMet        = dto.GreetPlayersWhenFirstMet;
        LookBackWhenLookedAt            = dto.LookBackWhenLookedAt;
        LookAtPlayersOnArrival          = dto.LookAtPlayersOnArrival;
        LogConversations                = dto.LogConversations;
        LogTransactions                 = dto.LogTransactions;
        LogMaxLines                     = dto.LogMaxLines;
        SelectedConvoFont = ConvoFontOptions.FirstOrDefault(o => o.Uri == dto.ConvoFont)
                            ?? ConvoFontOptions[0];
        SelectedConvoFontSize = ConvoFontSizeOptions.FirstOrDefault(o => o.Value == dto.ConvoFontSize)
                                ?? ConvoFontSizeOptions.First(o => o.Value == DefaultConvoFontSize);
        foreach (ChannelColorRowViewModel row in ChannelColorRows)
        {
            ChannelColor? co = null;
            dto.ChannelColors?.TryGetValue(row.Key, out co);
            row.Load(co?.Label, co?.Text);
        }

        // Mirror loaded settings into the live engine so the user's
        // policy applies from first connection, not just after the
        // Settings window is visited.
        ApplyToServices(dto);
    }

    // Collapse the row edits into the persisted dictionary, dropping channels
    // that are fully default (both slots blank) so the delta stays minimal.
    private Dictionary<string, ChannelColor>? BuildChannelColors()
    {
        Dictionary<string, ChannelColor> map = new();
        foreach (ChannelColorRowViewModel row in ChannelColorRows)
        {
            string? label = row.LabelOverride;
            string? text  = row.TextOverride;
            if (label is null && text is null) continue;
            map[row.Key] = new ChannelColor { Label = label, Text = text };
        }
        return map.Count == 0 ? null : map;
    }

    private TalkSettings ReadOrDefault()
    {
        CharacterProfile? profile = _profile.Current;
        if (profile?.Settings is null) return new TalkSettings();
        if (!profile.Settings.TryGetValue(TabKey, out JsonElement json)) return new TalkSettings();
        try
        {
            return JsonSerializer.Deserialize<TalkSettings>(json) ?? new TalkSettings();
        }
        catch
        {
            return new TalkSettings();
        }
    }

    private static void ApplyToServices(TalkSettings dto)
    {
        Game.Remote.RemoteCommandManager engine = AppServices.Current.RemoteCommands;
        engine.MasterDisable           = dto.DisallowAllRemoteCommands;
        engine.DisallowPartyDirectives = dto.DisallowPartyCommands;
        engine.DisableTelepathChannel  = dto.DisallowRemoteFromTelepaths;
        engine.DisableGangpathChannel  = dto.DisallowRemoteFromGangpaths;
        engine.DisableLocalChannel     = dto.DisallowRemoteFromLocal;
        engine.WarnOnDenial            = dto.WarnOnInvalidRemoteCommand;
        engine.FailureMessage          = dto.RemoteCommandFailureMessage ?? string.Empty;

        AppServices.Current.Greet.Enabled = dto.GreetPlayersWhenFirstMet;
        AppServices.Current.PlayerLook.LookBackWhenLookedAt   = dto.LookBackWhenLookedAt;
        AppServices.Current.PlayerLook.LookAtPlayersOnArrival = dto.LookAtPlayersOnArrival;
        AppServices.Current.SessionLog.ApplySettings(dto);
    }

    // ----- IsDirty plumbing -----

    private void ClearDirty()
    {
        _dirty = false;
        OnPropertyChanged(nameof(IsDirty));
    }

    partial void OnDisallowAllRemoteCommandsChanged(bool value)       => MarkDirty();
    partial void OnDisallowPartyCommandsChanged(bool value)           => MarkDirty();
    partial void OnDisallowRemoteFromTelepathsChanged(bool value)     => MarkDirty();
    partial void OnDisallowRemoteFromGangpathsChanged(bool value)     => MarkDirty();
    partial void OnDisallowRemoteFromLocalChanged(bool value)         => MarkDirty();
    partial void OnWarnOnInvalidRemoteCommandChanged(bool value)      => MarkDirty();
    partial void OnRemoteCommandFailureMessageChanged(string value)   => MarkDirty();
    partial void OnGreetPlayersWhenFirstMetChanged(bool value)        => MarkDirty();
    partial void OnLookBackWhenLookedAtChanged(bool value)            => MarkDirty();
    partial void OnLookAtPlayersOnArrivalChanged(bool value)          => MarkDirty();
    partial void OnLogConversationsChanged(bool value)                => MarkDirty();
    partial void OnLogTransactionsChanged(bool value)                 => MarkDirty();
    partial void OnLogMaxLinesChanged(int value)                      => MarkDirty();
    partial void OnSelectedConvoFontChanged(FontFamilyOption? value)  => MarkDirty();
    partial void OnSelectedConvoFontSizeChanged(FontSizeOption? value) => MarkDirty();

    private void MarkDirty()
    {
        if (_suppressDirty) return;
        if (_dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }
}
