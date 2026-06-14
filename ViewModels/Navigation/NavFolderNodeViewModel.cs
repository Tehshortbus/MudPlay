using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FujinTerm.ViewModels.Navigation;

/// <summary>
/// One folder node in a navigation tree (Manage dialog or the rail).
/// Holds its own stored <c>/</c>-separated <see cref="Path"/> plus a
/// mixed <see cref="Children"/> collection: nested
/// <see cref="NavFolderNodeViewModel"/>s first, then the leaf row
/// view-models (loops / lairs / favourites) filed directly under it.
/// </summary>
/// <remarks>
/// The leaf rows keep their existing types (<c>LoopRowViewModel</c>,
/// <c>ManagerLoopRow</c>, <c>FavoriteRowViewModel</c>, …) so the
/// surrounding XAML row templates are reused verbatim — the
/// <see cref="Avalonia.Controls.TreeView"/> picks the right template by
/// runtime type. Building the tree from a flat row list is
/// <see cref="NavTreeBuilder"/>'s job.
/// </remarks>
public sealed partial class NavFolderNodeViewModel : ObservableObject
{
    public NavFolderNodeViewModel(string path, string name)
    {
        Path = path;
        Name = name;
    }

    /// <summary>Full stored folder path (e.g. <c>"Cities/Silvermere"</c>). Never empty — the root isn't a node.</summary>
    public string Path { get; }

    /// <summary>Last path segment — the folder's own display label.</summary>
    public string Name { get; }

    /// <summary>Child folders (as <see cref="NavFolderNodeViewModel"/>) then leaf rows, both pre-sorted by the builder.</summary>
    public ObservableCollection<object> Children { get; } = new();

    /// <summary>Tree expand/collapse state. Folders start expanded so a freshly-built tree shows everything.</summary>
    [ObservableProperty] private bool _isExpanded = true;
}
