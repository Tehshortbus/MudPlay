using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

// Base class for the not-yet-wired "stub" settings tabs — disabled controls
// with per-field tooltips describing what each will do. Persistence and control
// behaviour land when the tab is wired.
//
// Subclasses populate Groups and override Id / Title. Apply / Discard stay
// no-ops because every rendered control is IsEnabled="False"; there's nothing
// for the user to commit.
public abstract class StubSectionViewModel : SettingsSectionViewModel
{
    private Control? _view;

    // Visually-grouped stub field rows the shared view renders.
    public abstract IReadOnlyList<StubGroup> Groups { get; }

    // Short tag shown at the top of the tab noting the surface isn't wired yet.
    public abstract string PhaseTag { get; }

    // One-line description shown under the tab header.
    public abstract string Description { get; }

    public override Control View => _view ??= new StubSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels
        => Groups
            .SelectMany(g => g.Fields)
            .Select(f => f.Label)
            .Prepend(Title);
}
