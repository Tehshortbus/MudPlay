using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// Base for one tab in the <see cref="CharacterWorkshopViewModel"/>'s flat
/// tab strip. The Workshop mirrors MudProxy's Character Status dialog —
/// Character Info / Death Recovery / Level Projection / CP Allocation /
/// Quest Status / Equipment Manager. Death Recovery is wired to
/// DeathRecoveryManager; the rest are stubs until their Phase-10 PR ships.
/// </summary>
public abstract class WorkshopSectionViewModel : ObservableObject
{
    /// <summary>Stable identifier — tab selection persists across reopens against this.</summary>
    public abstract string Id { get; }

    /// <summary>Display title on the tab.</summary>
    public abstract string Title { get; }

    /// <summary>The content UserControl rendered under the tab.
    /// Constructed lazily on first access.</summary>
    public abstract Control View { get; }
}
