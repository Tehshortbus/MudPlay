using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FujinTerm.ViewModels.GameData;

/// <summary>
/// Base for one tab in the Game Data Browser sidebar. Each tab
/// represents one game-data table (Monsters / Items / Spells /
/// TextBlocks / etc.) plus its user-overrides view. Real tabs ship in
/// Phase 5 PRs 5.5+; PR 5.4 wires the shell and a placeholder
/// implementation that advertises the eventual surface.
/// </summary>
/// <remarks>
/// Mirrors <see cref="ViewModels.Settings.SettingsSectionViewModel"/>'s
/// shape — stable id for sidebar selection, title for the sidebar
/// label, the content view, and search labels. The browser shell binds
/// against this base.
/// </remarks>
public abstract partial class GameDataSectionViewModel : ObservableObject
{
    /// <summary>Stable identifier — sidebar selection persists across reopens against this.</summary>
    public abstract string Id { get; }

    /// <summary>Title shown in the sidebar (e.g. "Monsters").</summary>
    public abstract string Title { get; }

    /// <summary>
    /// Substring tokens fed to the shell's search box. Default: title
    /// only. Tabs that wrap real fields override to include label /
    /// column names so the search box jumps from "ability" → Items.
    /// </summary>
    public virtual IEnumerable<string> SearchableLabels => new[] { Title };

    /// <summary>
    /// The editor UserControl rendered in the shell's content pane.
    /// Lazy — constructed on first access so an unselected section
    /// pays no UI cost.
    /// </summary>
    public abstract Control View { get; }
}
