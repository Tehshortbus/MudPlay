using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Settings;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Toolbar" tab. Global-tier — controls which icons appear on the
/// main window's toolbar. Bound directly against the live
/// <see cref="ToolbarConfig"/> mirror; Apply also persists to disk
/// via <see cref="SettingsService"/>, Discard re-reads from disk.
/// </summary>
public sealed partial class ToolbarSectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "Toolbar";

    private readonly SettingsService _settings;
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

    // ----- Edit-time mirror of the toolbar config -----
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

    public ToolbarSectionViewModel(SettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        LoadFromDisk();
        _suppressDirty = false;
    }

    public override void Apply()
    {
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

        GlobalSettings g = _settings.Current;
        g.Settings ??= new();
        g.Settings[TabKey] = JsonSerializer.SerializeToElement(dto);
        _settings.Save();  // fires GlobalSettingsChanged → AppServices rehydrates the live mirror.
        ClearDirty();
    }

    public override void Discard()
    {
        _suppressDirty = true;
        LoadFromDisk();
        _suppressDirty = false;
        ClearDirty();
    }

    private void LoadFromDisk()
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
        GlobalSettings g = _settings.Current;
        if (g.Settings is null) return new();
        if (!g.Settings.TryGetValue(TabKey, out JsonElement json)) return new();
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
