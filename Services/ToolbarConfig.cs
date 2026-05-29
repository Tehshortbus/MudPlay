using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

/// <summary>
/// Live observable mirror of the active character profile's
/// <see cref="ToolbarSettings"/>. The main window's toolbar buttons bind
/// their <c>IsVisible</c> here so loading a profile or saving a Settings
/// → Toolbar edit applies without a relaunch.
/// <see cref="AppServices"/> hydrates this on every
/// <see cref="ProfileService.ProfileLoaded"/> /
/// <see cref="ProfileService.ProfileMutated"/> tick and resets to
/// defaults on <see cref="ProfileService.ProfileClosed"/>.
/// </summary>
public sealed partial class ToolbarConfig : ObservableObject
{
    [ObservableProperty] private bool _showConnect = true;
    [ObservableProperty] private bool _showSettings = true;
    [ObservableProperty] private bool _showNavigation = true;
    [ObservableProperty] private bool _showBackscroll = true;
    [ObservableProperty] private bool _showCapture = true;
    [ObservableProperty] private bool _showWireInspector = true;
    [ObservableProperty] private bool _showConversation = true;
    [ObservableProperty] private bool _showParty = true;
    [ObservableProperty] private bool _showWorkshop = true;
    [ObservableProperty] private bool _showSpellBook = true;
    [ObservableProperty] private bool _showSessionStats = true;
    [ObservableProperty] private bool _showGameDataBrowser = true;
    [ObservableProperty] private bool _showLog = true;

    /// <summary>Apply the persisted DTO onto the live observable.</summary>
    public void ApplyFrom(ToolbarSettings dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
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

    /// <summary>Capture the current live state back into a DTO for serialisation.</summary>
    public ToolbarSettings Snapshot() => new()
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
}
