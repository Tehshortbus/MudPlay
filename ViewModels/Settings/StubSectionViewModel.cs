using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// Base class for the 11 "stub" settings tabs (Health, Spells, Combat,
/// PvP, Party, Cash, Talk, Auto-Lair, Sounds, Other, Events) that ship
/// in Phase 4 PR 4.8 with disabled controls + per-field "wired in Phase
/// X" tooltips. Persistence and the actual control behaviour land in
/// the owning phase named by each tooltip.
/// </summary>
/// <remarks>
/// Subclasses populate <see cref="Groups"/> and override
/// <see cref="Id"/> / <see cref="Title"/>. Apply / Discard stay no-ops
/// because every rendered control is <c>IsEnabled="False"</c>; there's
/// nothing for the user to commit.
/// </remarks>
public abstract class StubSectionViewModel : SettingsSectionViewModel
{
    private Control? _view;

    /// <summary>Visually-grouped stub field rows the shared view renders.</summary>
    public abstract IReadOnlyList<StubGroup> Groups { get; }

    /// <summary>
    /// Single phase tag shown at the top of the tab so the user sees
    /// where this whole surface gets wired up.
    /// </summary>
    public abstract string PhaseTag { get; }

    /// <summary>One-line description shown under the tab header.</summary>
    public abstract string Description { get; }

    public override Control View => _view ??= new StubSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels
        => Groups
            .SelectMany(g => g.Fields)
            .Select(f => f.Label)
            .Prepend(Title);
}
