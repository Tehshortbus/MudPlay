using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

// Per-row view-model that the main window's dynamic toolbar ItemsControl binds
// to. Built by MainWindowViewModel from ToolbarConfig.Layout via
// ToolbarItemCatalogue; carries every property the XAML template needs (icon
// resource key, command, tooltip, plus a pair of observable state flags for the
// connect / capture buttons).
public sealed partial class ToolbarButtonItem : ObservableObject
{
    public ToolbarItemKind Kind { get; }
    public string? ActionId { get; }

    public bool IsButton => Kind == ToolbarItemKind.Button;
    public bool IsSeparator => Kind == ToolbarItemKind.Separator;

    public string Label { get; }
    public string? IconResourceKey { get; }

    // Optional secondary icon used by the connection-toggle button to swap
    // between "plug" (disconnected) and "unplug" (connected).
    public string? AlternateIconResourceKey { get; }

    // Tooltip text (icon-only buttons use this as their label). Observable so
    // the movement Start button can re-label itself "Resume" when the active nav
    // mode is paused.
    [ObservableProperty] private string _tooltip;

    // Whether the button is currently actionable. Default true; the movement
    // Start / Pause / Stop rows toggle it with engine state so a button that
    // would no-op renders disabled (greyed) instead of firing. Bound to the
    // toolbar button's IsEnabled.
    [ObservableProperty] private bool _isActionEnabled = true;

    public ICommand? Command { get; }

    // Toolbar button's Active visual state (amber).
    [ObservableProperty] private bool _isActive;

    // Toolbar button's Danger visual state (red hover).
    [ObservableProperty] private bool _isDanger;

    // True → show AlternateIconResourceKey in place of IconResourceKey.
    [ObservableProperty] private bool _showAlternate;

    public ToolbarButtonItem(
        ToolbarItemKind kind,
        string? actionId,
        string label,
        string? iconResourceKey,
        string tooltip,
        ICommand? command,
        string? alternateIconResourceKey = null)
    {
        Kind = kind;
        ActionId = actionId;
        Label = label;
        IconResourceKey = iconResourceKey;
        _tooltip = tooltip;
        Command = command;
        AlternateIconResourceKey = alternateIconResourceKey;
    }
}
