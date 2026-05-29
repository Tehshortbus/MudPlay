using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Toolbar" tab. Char-tier — each character profile owns its own
/// toolbar layout. Apply writes to <see cref="CharacterProfile.Settings"/>
/// under the <c>"Toolbar"</c> key + <see cref="ProfileService.Save"/>;
/// <see cref="AppServices"/> mirrors that back onto the live
/// <see cref="ToolbarConfig"/> via the <see cref="ProfileService.ProfileMutated"/>
/// hook so the live toolbar updates without a relaunch.
/// </summary>
public sealed partial class ToolbarSectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "Toolbar";

    private readonly ProfileService _profile;
    private bool _suppressDirty = true;
    private bool _dirty;
    private Control? _view;

    public override string Id => "toolbar";
    public override string Title => "Toolbar";
    public override bool IsDirty => _dirty;

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "Toolbar", "Connect", "Settings", "Navigation", "Backscroll",
        "Capture", "Wire Inspector", "Conversation", "Party", "Workshop",
        "Spell Book", "Session Stats", "Game Data Browser", "Log",
    };

    public override Control View => _view ??= new ToolbarSectionView { DataContext = this };

    /// <summary>True when any character profile is loaded (including unsaved drafts).</summary>
    public bool HasProfile => _profile.Current is not null;

    // ----- Edit-time mirror of the toolbar settings -----
    [ObservableProperty] private bool _showConnect;
    [ObservableProperty] private bool _showSettings;
    [ObservableProperty] private bool _showNavigation;
    [ObservableProperty] private bool _showBackscroll;
    [ObservableProperty] private bool _showCapture;
    [ObservableProperty] private bool _showWireInspector;
    [ObservableProperty] private bool _showConversation;
    [ObservableProperty] private bool _showParty;
    [ObservableProperty] private bool _showWorkshop;
    [ObservableProperty] private bool _showSpellBook;
    [ObservableProperty] private bool _showSessionStats;
    [ObservableProperty] private bool _showGameDataBrowser;
    [ObservableProperty] private bool _showLog;

    public ToolbarSectionViewModel(ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;

        LoadFromProfile();
        _suppressDirty = false;
    }

    public override void Apply()
    {
        if (_profile.Current is not { } profile) return;

        ToolbarSettings dto = new()
        {
            ShowConnect = ShowConnect,
            ShowSettings = ShowSettings,
            ShowNavigation = ShowNavigation,
            ShowBackscroll = ShowBackscroll,
            ShowCapture = ShowCapture,
            ShowWireInspector = ShowWireInspector,
            ShowConversation = ShowConversation,
            ShowParty = ShowParty,
            ShowWorkshop = ShowWorkshop,
            ShowSpellBook = ShowSpellBook,
            ShowSessionStats = ShowSessionStats,
            ShowGameDataBrowser = ShowGameDataBrowser,
            ShowLog = ShowLog,
        };

        profile.Settings ??= new();
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(dto);
        // Save() no-ops on drafts (no name yet to write to); NotifyMutated
        // always fires so AppServices re-hydrates the live ToolbarConfig
        // either way. Drafts carry the edit in memory until first Save.
        _profile.Save();
        _profile.NotifyMutated();
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
        ToolbarSettings dto = ReadOrDefault();
        ShowConnect = dto.ShowConnect;
        ShowSettings = dto.ShowSettings;
        ShowNavigation = dto.ShowNavigation;
        ShowBackscroll = dto.ShowBackscroll;
        ShowCapture = dto.ShowCapture;
        ShowWireInspector = dto.ShowWireInspector;
        ShowConversation = dto.ShowConversation;
        ShowParty = dto.ShowParty;
        ShowWorkshop = dto.ShowWorkshop;
        ShowSpellBook = dto.ShowSpellBook;
        ShowSessionStats = dto.ShowSessionStats;
        ShowGameDataBrowser = dto.ShowGameDataBrowser;
        ShowLog = dto.ShowLog;
    }

    private ToolbarSettings ReadOrDefault()
    {
        CharacterProfile? profile = _profile.Current;
        if (profile?.Settings is null) return new();
        if (!profile.Settings.TryGetValue(TabKey, out JsonElement json)) return new();
        return JsonSerializer.Deserialize<ToolbarSettings>(json.GetRawText()) ?? new();
    }

    private void Dirty()
    {
        if (_suppressDirty || _dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }

    private void ClearDirty()
    {
        if (!_dirty) return;
        _dirty = false;
        OnPropertyChanged(nameof(IsDirty));
    }

    partial void OnShowConnectChanged(bool value)           => Dirty();
    partial void OnShowSettingsChanged(bool value)          => Dirty();
    partial void OnShowNavigationChanged(bool value)        => Dirty();
    partial void OnShowBackscrollChanged(bool value)        => Dirty();
    partial void OnShowCaptureChanged(bool value)           => Dirty();
    partial void OnShowWireInspectorChanged(bool value)     => Dirty();
    partial void OnShowConversationChanged(bool value)      => Dirty();
    partial void OnShowPartyChanged(bool value)             => Dirty();
    partial void OnShowWorkshopChanged(bool value)          => Dirty();
    partial void OnShowSpellBookChanged(bool value)         => Dirty();
    partial void OnShowSessionStatsChanged(bool value)      => Dirty();
    partial void OnShowGameDataBrowserChanged(bool value)   => Dirty();
    partial void OnShowLogChanged(bool value)               => Dirty();
}
