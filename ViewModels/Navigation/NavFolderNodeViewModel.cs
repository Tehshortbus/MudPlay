using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FujinTerm.ViewModels.Navigation;

// One folder node in a navigation tree (Manage dialog or the rail). Holds
// its own stored /-separated Path plus a mixed Children collection: nested
// NavFolderNodeViewModels first, then the leaf row view-models (loops /
// lairs / favourites) filed directly under it.
//
// The leaf rows keep their existing types (LoopRowViewModel, ManagerLoopRow,
// FavoriteRowViewModel, …) so the surrounding XAML row templates are reused
// verbatim — the TreeView picks the right template by runtime type. Building
// the tree from a flat row list is NavTreeBuilder's job.
public sealed partial class NavFolderNodeViewModel : ObservableObject
{
    public NavFolderNodeViewModel(string path, string name, bool isExpanded = true)
    {
        Path = path;
        Name = name;
        IsExpanded = isExpanded;
    }

    // Full stored folder path (e.g. "Cities/Silvermere"). Never empty — the root isn't a node.
    public string Path { get; }

    // Last path segment — the folder's own display label.
    public string Name { get; }

    // Child folders (as NavFolderNodeViewModel) then leaf rows, both pre-sorted by the builder.
    public ObservableCollection<object> Children { get; } = new();

    // Tree expand/collapse state. The builder seeds the starting value per
    // surface (the rail's Loops+Lairs tree starts collapsed; the Manage dialog
    // and GOTO tree start expanded); NavTreeBuilder preserves a user's per-folder
    // override across rebuilds.
    [ObservableProperty] private bool _isExpanded;
}
