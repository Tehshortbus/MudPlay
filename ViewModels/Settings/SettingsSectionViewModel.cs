using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// Base for one section in the <see cref="SettingsWindowViewModel"/>'s
/// sidebar. Every settings tab — including app-wide ones like Display
/// and Toolbar — lives on the loaded character profile, so sections
/// don't take a runtime scope; each one knows where its data lives.
/// </summary>
/// <remarks>
/// Game-data record overrides use the four-tier (Defaults / Global /
/// BBS / Char) hierarchy via <see cref="Services.SettingsResolver"/>;
/// that lives in the Phase 5 Game Data Browser, not here. Settings-tab
/// data has no tier picker.
/// </remarks>
public abstract partial class SettingsSectionViewModel : ObservableObject
{
    /// <summary>Stable identifier — sidebar selection persists across reopens against this.</summary>
    public abstract string Id { get; }

    /// <summary>Title shown in the sidebar.</summary>
    public abstract string Title { get; }

    /// <summary>True when this section has unapplied edits.</summary>
    public virtual bool IsDirty => false;

    /// <summary>
    /// Substring tokens fed to the shell's search box. Default: title only.
    /// Tabs that wrap real fields override to include label text so the
    /// search box jumps from "rest" → Health tab.
    /// </summary>
    public virtual IEnumerable<string> SearchableLabels => new[] { Title };

    /// <summary>
    /// The editor UserControl rendered in the shell's content pane. Lazy —
    /// constructed on first access so an unselected section pays no UI cost.
    /// </summary>
    public abstract Control View { get; }

    /// <summary>Persist this section's pending edits. Default no-op for placeholders.</summary>
    public virtual void Apply() { }

    /// <summary>Drop pending edits and re-read from the underlying store.</summary>
    public virtual void Discard() { }
}
