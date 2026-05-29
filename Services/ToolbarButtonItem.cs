using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

/// <summary>
/// Per-row view-model that the main window's dynamic toolbar
/// <c>ItemsControl</c> binds to. Built by <c>MainWindowViewModel</c>
/// from <see cref="ToolbarConfig.Layout"/> via
/// <see cref="ToolbarItemCatalogue"/>; carries every property the
/// XAML template needs (icon resource key, command, tooltip, plus a
/// pair of observable state flags for the connect / capture buttons).
/// </summary>
public sealed partial class ToolbarButtonItem : ObservableObject
{
    public ToolbarItemKind Kind { get; }
    public string? ActionId { get; }

    public bool IsButton => Kind == ToolbarItemKind.Button;
    public bool IsSeparator => Kind == ToolbarItemKind.Separator;

    public string Label { get; }
    public string? IconResourceKey { get; }

    /// <summary>
    /// Optional secondary icon used by the connection-toggle button to
    /// swap between "plug" (disconnected) and "unplug" (connected).
    /// </summary>
    public string? AlternateIconResourceKey { get; }

    public string Tooltip { get; }
    public ICommand? Command { get; }

    /// <summary>Toolbar button's <c>Active</c> visual state (amber).</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>Toolbar button's <c>Danger</c> visual state (red hover).</summary>
    [ObservableProperty] private bool _isDanger;

    /// <summary>True → show <see cref="AlternateIconResourceKey"/> in place of <see cref="IconResourceKey"/>.</summary>
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
        Tooltip = tooltip;
        Command = command;
        AlternateIconResourceKey = alternateIconResourceKey;
    }
}
