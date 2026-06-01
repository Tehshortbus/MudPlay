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
public abstract partial class GameDataSectionViewModel : ObservableObject, IDisposable
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

    /// <summary>
    /// Unsubscribe from any long-lived service events the section
    /// subscribed to in its ctor (<see cref="Services.GameDataCache.ActiveSetChanged"/>,
    /// engine <c>CollectionChanged</c>, etc.). Called by
    /// <see cref="GameDataBrowserViewModel.Dispose"/> when the browser
    /// window closes — without it, every browser open leaks a fresh
    /// set of section VMs (their cached rows + Views) since the
    /// singleton services keep their event subscriptions alive.
    /// Default impl is a no-op for sections that don't subscribe to
    /// anything external (placeholder rows / read-only Info tab).
    /// </summary>
    public virtual void Dispose() { }

    /// <summary>
    /// Raised when this section wants the browser to switch the active
    /// tab — typically because the user double-clicked a row that
    /// references a record in a different table (e.g. Shops →
    /// Rooms). Carrier of an optional row predicate the target should
    /// auto-select after activation.
    /// </summary>
    public event Action<NavigationRequest>? NavigationRequested;

    /// <summary>
    /// Section-side helper that fires <see cref="NavigationRequested"/>
    /// for the given target section + row predicate. Subclasses call
    /// this from their double-click / link-click handlers.
    /// </summary>
    /// <param name="targetSectionId">Stable <see cref="Id"/> of the target section.</param>
    /// <param name="rowSelector">
    /// Optional predicate the target section runs against its rows to
    /// pick + select one. Pass <c>null</c> when no auto-selection is
    /// wanted (just switch tabs).
    /// </param>
    protected void RequestNavigation(string targetSectionId, Func<Tables.GameDataRow, bool>? rowSelector = null)
        => NavigationRequested?.Invoke(new NavigationRequest(targetSectionId, rowSelector));
}

/// <summary>
/// Payload for <see cref="GameDataSectionViewModel.NavigationRequested"/>.
/// </summary>
/// <param name="TargetSectionId">Section the browser should make active.</param>
/// <param name="RowSelector">
/// Optional predicate that picks one row in the target section after
/// activation. <c>null</c> to skip auto-selection.
/// </param>
public sealed record NavigationRequest(
    string TargetSectionId,
    Func<Tables.GameDataRow, bool>? RowSelector);
