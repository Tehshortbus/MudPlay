using System.Collections.ObjectModel;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

/// <summary>
/// Live observable mirror of the active character profile's toolbar
/// layout. The main window's toolbar is an <c>ItemsControl</c> bound to
/// <see cref="Layout"/>; the Settings → Toolbar editor mutates the
/// same collection. <see cref="AppServices"/> hydrates on every
/// <see cref="ProfileService.ProfileLoaded"/> /
/// <see cref="ProfileService.ProfileMutated"/> tick and resets to
/// defaults on <see cref="ProfileService.ProfileClosed"/>.
/// </summary>
public sealed class ToolbarConfig
{
    /// <summary>
    /// Ordered toolbar items. Top-to-bottom in the editor ≡
    /// left-to-right on the rendered toolbar.
    /// </summary>
    public ObservableCollection<ToolbarItem> Layout { get; } = new();

    /// <summary>Replace the live layout with the items in <paramref name="dto"/>.</summary>
    public void ApplyFrom(ToolbarSettings dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ReplaceAll(dto.Layout is { Count: > 0 } ? dto.Layout : ToolbarDefaults.Build());
    }

    /// <summary>Capture the live layout into a fresh DTO for serialisation.</summary>
    public ToolbarSettings Snapshot()
    {
        List<ToolbarItem> copy = new(Layout.Count);
        foreach (ToolbarItem item in Layout)
        {
            copy.Add(new ToolbarItem { Kind = item.Kind, ActionId = item.ActionId });
        }
        return new ToolbarSettings { Layout = copy };
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
