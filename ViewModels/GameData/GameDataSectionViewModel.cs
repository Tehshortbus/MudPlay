using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels.GameData;

// Base for one tab in the Game Data Browser sidebar. Each tab represents one game-data
// table (Monsters / Items / Spells / TextBlocks / etc.) plus its user-overrides view.
//
// Mirrors SettingsSectionViewModel's shape — stable id for sidebar selection, title for
// the sidebar label, the content view, and search labels. The browser shell binds
// against this base.
public abstract partial class GameDataSectionViewModel : ObservableObject, IDisposable
{
    // Stable identifier — sidebar selection persists across reopens against this.
    public abstract string Id { get; }

    // Title shown in the sidebar (e.g. "Monsters").
    public abstract string Title { get; }

    // Substring tokens fed to the shell's search box. Default: title only. Tabs that wrap
    // real fields override to include label / column names so the search box jumps from
    // "ability" → Items.
    public virtual IEnumerable<string> SearchableLabels => new[] { Title };

    // Sidebar grouping: true places the section in the bottom "Imported tables" group,
    // false in the top "Tables + editors" group. JSON tables and their derived views
    // (Unobtainable, Quest Flags) belong with the tables; engine tabs / placeholders don't.
    public virtual bool ShowInTableGroup => false;

    // The editor UserControl rendered in the shell's content pane. Lazy — constructed on
    // first access so an unselected section pays no UI cost.
    public abstract Control View { get; }

    // Unsubscribe from any long-lived service events the section subscribed to in its
    // ctor (GameDataCache.ActiveSetChanged, engine CollectionChanged, etc.). Called by
    // GameDataBrowserViewModel.Dispose when the browser window closes — without it, every
    // browser open leaks a fresh set of section VMs (their cached rows + Views) since the
    // singleton services keep their event subscriptions alive. Default impl is a no-op
    // for sections that don't subscribe to anything external (placeholder rows /
    // read-only Info tab).
    public virtual void Dispose() { }

}

// Where GameDataBrowserViewModel.NavigateToRecord jumps to. TargetSectionId is the
// section the browser should make active; RowSelector is an optional predicate that
// picks one row in the target section after activation (null to skip auto-selection).
public sealed record NavigationRequest(
    string TargetSectionId,
    Func<Tables.GameDataRow, bool>? RowSelector);
