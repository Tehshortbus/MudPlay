using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// Base for one section in the <see cref="SettingsWindowViewModel"/>'s
/// sidebar. Concrete subclasses (one per tab — General, Display, Comms,
/// Health, etc.) land in their own PR per the Phase 4 plan; until they
/// arrive the shell renders a <see cref="PlaceholderSectionViewModel"/>
/// for every section so the sidebar shows the full surface area from
/// day one.
/// </summary>
/// <remarks>
/// Pending changes live in the section — the shell only knows
/// "is anything dirty?" via <see cref="IsDirty"/> and routes Apply /
/// Discard through the virtuals. Settings get persisted through
/// <see cref="SettingsResolver.WriteAt{T}"/> at the scope the shell
/// is currently editing (Char / BBS / Global).
/// </remarks>
public abstract partial class SettingsSectionViewModel : ObservableObject
{
    /// <summary>Stable identifier — sidebar selection persists across reopens against this.</summary>
    public abstract string Id { get; }

    /// <summary>Title shown in the sidebar.</summary>
    public abstract string Title { get; }

    /// <summary>Group header this section belongs under in the sidebar (e.g., "General", "Character").</summary>
    public abstract string GroupName { get; }

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

    /// <summary>
    /// Persist this section's pending edits at <paramref name="scope"/>.
    /// Default no-op for placeholder / read-only sections.
    /// </summary>
    public virtual void Apply(SettingsTier scope, SettingsResolver resolver) { }

    /// <summary>Drop pending edits and re-read from the resolver.</summary>
    public virtual void Discard() { }
}
