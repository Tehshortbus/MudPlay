using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

/// <summary>
/// Live observable mirror of the active character profile's toolbar
/// layout + visibility / orientation. The main window's toolbar
/// borders bind to the derived <see cref="ShowTop"/> /
/// <see cref="ShowBottom"/> / <see cref="ShowLeft"/> /
/// <see cref="ShowRight"/> flags so the right border lights up
/// automatically when the user toggles Settings → Toolbar's Show /
/// Vertical / Side controls.
/// <see cref="AppServices"/> hydrates on every
/// <see cref="ProfileService.ProfileLoaded"/> /
/// <see cref="ProfileService.ProfileMutated"/> tick and resets to
/// defaults on <see cref="ProfileService.ProfileClosed"/>.
/// </summary>
public sealed partial class ToolbarConfig : ObservableObject
{
    /// <summary>
    /// Ordered toolbar items. Top-to-bottom in the editor ≡
    /// left-to-right on the rendered horizontal toolbar (top-to-bottom
    /// when vertical).
    /// </summary>
    public ObservableCollection<ToolbarItem> Layout { get; } = new();

    /// <summary>Master visibility — false hides every orientation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTop))]
    [NotifyPropertyChangedFor(nameof(ShowBottom))]
    [NotifyPropertyChangedFor(nameof(ShowLeft))]
    [NotifyPropertyChangedFor(nameof(ShowRight))]
    private bool _visible = true;

    /// <summary>True = mount on the side picked by <see cref="Side"/>; false = mount on the edge picked by <see cref="HorizontalSide"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTop))]
    [NotifyPropertyChangedFor(nameof(ShowBottom))]
    [NotifyPropertyChangedFor(nameof(ShowLeft))]
    [NotifyPropertyChangedFor(nameof(ShowRight))]
    private bool _vertical;

    /// <summary>Edge for the vertical mount. Ignored when <see cref="Vertical"/> = false.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLeft))]
    [NotifyPropertyChangedFor(nameof(ShowRight))]
    private ToolbarSide _side = ToolbarSide.Left;

    /// <summary>Edge for the horizontal mount. Ignored when <see cref="Vertical"/> = true.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTop))]
    [NotifyPropertyChangedFor(nameof(ShowBottom))]
    private ToolbarHorizontalSide _horizontalSide = ToolbarHorizontalSide.Top;

    /// <summary>True when the horizontal-top toolbar border should render.</summary>
    public bool ShowTop    => Visible && !Vertical && HorizontalSide == ToolbarHorizontalSide.Top;
    /// <summary>True when the horizontal-bottom toolbar border should render.</summary>
    public bool ShowBottom => Visible && !Vertical && HorizontalSide == ToolbarHorizontalSide.Bottom;
    /// <summary>True when the vertical-left toolbar border should render.</summary>
    public bool ShowLeft   => Visible &&  Vertical && Side == ToolbarSide.Left;
    /// <summary>True when the vertical-right toolbar border should render.</summary>
    public bool ShowRight  => Visible &&  Vertical && Side == ToolbarSide.Right;

    /// <summary>Replace the live layout + visibility / orientation with values from <paramref name="dto"/>.</summary>
    public void ApplyFrom(ToolbarSettings dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ReplaceAll(dto.Layout is { Count: > 0 } ? dto.Layout : ToolbarDefaults.Build());
        Visible        = dto.Visible;
        Vertical       = dto.Vertical;
        Side           = dto.Side;
        HorizontalSide = dto.HorizontalSide;
    }

    /// <summary>Capture the live state into a fresh DTO for serialisation.</summary>
    public ToolbarSettings Snapshot()
    {
        List<ToolbarItem> copy = new(Layout.Count);
        foreach (ToolbarItem item in Layout)
        {
            copy.Add(new ToolbarItem { Kind = item.Kind, ActionId = item.ActionId });
        }
        return new ToolbarSettings
        {
            Layout         = copy,
            Visible        = Visible,
            Vertical       = Vertical,
            Side           = Side,
            HorizontalSide = HorizontalSide,
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
