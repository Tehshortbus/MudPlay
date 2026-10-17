using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Models.Profile;

namespace MudPlay.Services;

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

    // What a click runs. A split button runs its picked sub-action (see
    // SelectedSubAction); an ordinary button runs the catalogue command.
    public ICommand? Command => HasSubActions ? _runSelected : _command;
    private readonly ICommand? _command;
    private readonly ICommand? _runSelected;

    // Toolbar button's Active visual state (amber).
    [ObservableProperty] private bool _isActive;

    // Toolbar button's Danger visual state (red hover).
    [ObservableProperty] private bool _isDanger;

    // True → show AlternateIconResourceKey in place of IconResourceKey.
    [ObservableProperty] private bool _showAlternate;

    // Short text shown in place of the icon (e.g. the Combat-Profile cycle button's
    // "P1"). Empty/null → the icon renders as usual.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadgeText))]
    private string? _badgeText;
    public bool HasBadgeText => !string.IsNullOrEmpty(BadgeText);

    // A split button's ▾ menu (ToolbarItemCatalogue.Entry.SubActions), commands
    // resolved. Empty for an ordinary button.
    public IReadOnlyList<ToolbarMenuAction> SubActions { get; }
    public bool HasSubActions => SubActions.Count > 0;

    // The sub-action a click runs — the character's saved pick, else the first.
    [ObservableProperty] private ToolbarMenuAction? _selectedSubAction;

    // Saves a pick (the sub-action's Key) to the character.
    private readonly Action<string>? _saveChoice;

    // The ▾ menu's pick only re-points the button: nothing is sent until the button
    // itself is clicked, so a sweep like Drop everything can't fire off a mis-pick.
    public void Choose(ToolbarMenuAction action)
    {
        SelectedSubAction = action;
        _saveChoice?.Invoke(action.Key);
    }

    private void RunSelected()
    {
        if (SelectedSubAction is { Command: { } cmd } a && cmd.CanExecute(a.Parameter))
            cmd.Execute(a.Parameter);
    }

    // The tooltip names what a click will do now.
    partial void OnSelectedSubActionChanged(ToolbarMenuAction? value)
    {
        if (value is not null) Tooltip = $"{Label}: {value.Label} — ▾ to change";
    }

    public ToolbarButtonItem(
        ToolbarItemKind kind,
        string? actionId,
        string label,
        string? iconResourceKey,
        string tooltip,
        ICommand? command,
        string? alternateIconResourceKey = null,
        IReadOnlyList<ToolbarMenuAction>? subActions = null,
        string? savedChoice = null,
        Action<string>? saveChoice = null)
    {
        SubActions = subActions ?? Array.Empty<ToolbarMenuAction>();
        Kind = kind;
        ActionId = actionId;
        Label = label;
        IconResourceKey = iconResourceKey;
        _tooltip = tooltip;
        _command = command;
        AlternateIconResourceKey = alternateIconResourceKey;
        _saveChoice = saveChoice;
        if (HasSubActions)
        {
            _runSelected = new CommunityToolkit.Mvvm.Input.RelayCommand(RunSelected);
            SelectedSubAction = SubActions.FirstOrDefault(a => a.Key == savedChoice) ?? SubActions[0];
        }
    }
}

// One resolved entry in a split toolbar button's ▾ menu. Key is what the character's
// pick is saved under; Parameter is handed to Command.
public sealed record ToolbarMenuAction(
    string Label, System.Windows.Input.ICommand? Command, string? Tooltip, string Key, string? Parameter = null);
