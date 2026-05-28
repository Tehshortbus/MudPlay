using System.Collections.ObjectModel;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// One header in the Settings sidebar with the sections that live under it.
/// The shell builds these from its flat <c>Sections</c> collection so search
/// can run against a single list while the sidebar renders the grouped view.
/// </summary>
public sealed class SettingsSectionGroup
{
    public string Header { get; }
    public ObservableCollection<SettingsSectionViewModel> Sections { get; }

    public SettingsSectionGroup(string header, IEnumerable<SettingsSectionViewModel> sections)
    {
        Header = header;
        Sections = new ObservableCollection<SettingsSectionViewModel>(sections);
    }
}
