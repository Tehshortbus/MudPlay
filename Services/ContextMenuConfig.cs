using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Models.Profile;

namespace MudPlay.Services;

// Live observable mirror of the active character profile's terminal right-click
// menu layout (the entries below the pinned Favorites / Recent walk flyouts).
// The main window's code-behind rebuilds the ContextMenu from Layout and
// re-runs whenever it changes, so an edit in Settings applies to the next
// right-click without reopening. AppServices hydrates on every
// ProfileService.ProfileLoaded / ProfileMutated tick and resets to defaults on
// ProfileClosed — the same lifecycle as ToolbarConfig.
public sealed partial class ContextMenuConfig : ObservableObject
{
    // Ordered menu entries, top-to-bottom in the editor ≡ top-to-bottom in the
    // rendered right-click menu.
    public ObservableCollection<ContextMenuEntry> Layout { get; } = new();

    // Replace the live layout with the dto's (falling back to defaults when it's
    // null/empty), then signal a rebuild.
    public void ApplyFrom(ContextMenuSettings dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ReplaceAll(dto.Layout is { Count: > 0 } ? dto.Layout : ContextMenuDefaults.Build());
    }

    private void ReplaceAll(IEnumerable<ContextMenuEntry> items)
    {
        Layout.Clear();
        foreach (ContextMenuEntry item in items) Layout.Add(Clone(item));
    }

    // Deep copy — carries a folder's Children so the live layout doesn't alias
    // the dto's lists.
    private static ContextMenuEntry Clone(ContextMenuEntry item) => new()
    {
        Kind = item.Kind,
        Id = item.Id,
        Label = item.Label,
        Children = item.Children?.Select(Clone).ToList(),
    };
}
