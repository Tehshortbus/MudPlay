using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// Base for one section in the <see cref="CharacterWorkshopViewModel"/>'s
/// left-rail navigation. The Workshop is the unified character hub
/// per Phase 9 PR 9.I direction — STATS / PROGRESS / EQUIP / COMBAT /
/// QUESTS / DEATH all live here. v1 ships the shell + Death section
/// wired to DeathRecoveryManager; the rest are stubs.
/// </summary>
public abstract class WorkshopSectionViewModel : ObservableObject
{
    /// <summary>Stable identifier — sidebar selection persists across reopens against this.</summary>
    public abstract string Id { get; }

    /// <summary>Display title in the sidebar.</summary>
    public abstract string Title { get; }

    /// <summary>The content UserControl rendered in the right pane.
    /// Constructed lazily on first access.</summary>
    public abstract Control View { get; }
}
