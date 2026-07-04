using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

// Live observable mirror of the active character profile's toolbar layout +
// visibility / position. The main window's toolbar borders bind to the derived
// ShowTop / ShowBottom / ShowLeft / ShowRight flags so the right border lights
// up automatically when the user toggles Settings → Toolbar's Show / Position
// controls. AppServices hydrates on every ProfileService.ProfileLoaded /
// ProfileMutated tick and resets to defaults on ProfileClosed.
public sealed partial class ToolbarConfig : ObservableObject
{
    // Ordered toolbar items. Top-to-bottom in the editor is left-to-right on
    // the rendered horizontal toolbar (top-to-bottom when vertical).
    public ObservableCollection<ToolbarItem> Layout { get; } = new();

    // Master visibility — false hides the toolbar on every edge.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTop))]
    [NotifyPropertyChangedFor(nameof(ShowBottom))]
    [NotifyPropertyChangedFor(nameof(ShowLeft))]
    [NotifyPropertyChangedFor(nameof(ShowRight))]
    private bool _visible = true;

    // Edge the toolbar docks to when visible.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTop))]
    [NotifyPropertyChangedFor(nameof(ShowBottom))]
    [NotifyPropertyChangedFor(nameof(ShowLeft))]
    [NotifyPropertyChangedFor(nameof(ShowRight))]
    private ToolbarPosition _position = ToolbarPosition.Top;

    // True when the horizontal-top toolbar border should render.
    public bool ShowTop    => Visible && Position == ToolbarPosition.Top;
    // True when the horizontal-bottom toolbar border should render.
    public bool ShowBottom => Visible && Position == ToolbarPosition.Bottom;
    // True when the vertical-left toolbar border should render.
    public bool ShowLeft   => Visible && Position == ToolbarPosition.Left;
    // True when the vertical-right toolbar border should render.
    public bool ShowRight  => Visible && Position == ToolbarPosition.Right;

    // Replace the live layout + visibility / position with values from dto.
    public void ApplyFrom(ToolbarSettings dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ReplaceAll(dto.Layout is { Count: > 0 } ? dto.Layout : ToolbarDefaults.Build());
        Visible  = dto.Visible;
        Position = dto.Position;
    }

    // Capture the live state into a fresh DTO for serialisation.
    public ToolbarSettings Snapshot()
    {
        List<ToolbarItem> copy = new(Layout.Count);
        foreach (ToolbarItem item in Layout)
        {
            copy.Add(new ToolbarItem { Kind = item.Kind, ActionId = item.ActionId });
        }
        return new ToolbarSettings
        {
            Layout   = copy,
            Visible  = Visible,
            Position = Position,
        };
    }

    private void ReplaceAll(IEnumerable<ToolbarItem> items)
    {
        Layout.Clear();
        foreach (ToolbarItem item in items)
        {
            Layout.Add(new ToolbarItem { Kind = item.Kind, ActionId = item.ActionId });
        }
    }
}
