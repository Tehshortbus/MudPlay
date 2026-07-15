using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using FujinTerm.ViewModels.Navigation;
using Xunit;

namespace FujinTerm.Tests;

// Pins NavTreeBuilder's per-surface default expand state and the rebuild
// preservation that keeps a user's per-folder override across a Sync. The rail's
// Loops+Lairs tree opts into collapse-by-default (defaultExpanded: false); the
// Manage dialog and GOTO tree keep expand-by-default.
public sealed class NavTreeBuilderTests
{
    private static List<NavFolderNodeViewModel> Folders(IEnumerable<object> nodes)
    {
        var found = new List<NavFolderNodeViewModel>();
        void Walk(IEnumerable<object> ns)
        {
            foreach (object n in ns)
            {
                if (n is NavFolderNodeViewModel f)
                {
                    found.Add(f);
                    Walk(f.Children);
                }
            }
        }
        Walk(nodes);
        return found;
    }

    private static NavFolderNodeViewModel FolderAt(IEnumerable<object> nodes, string path)
        => Folders(nodes).Single(f => f.Path == path);

    [Fact]
    public void Build_CollapseByDefault_FoldersStartCollapsed()
    {
        List<object> tree = NavTreeBuilder.Build(
            System.Array.Empty<string>(), s => s,
            new[] { "Cities/Silvermere" }, defaultExpanded: false);

        Assert.All(Folders(tree), f => Assert.False(f.IsExpanded));
    }

    [Fact]
    public void Build_ExpandByDefault_FoldersStartExpanded()
    {
        List<object> tree = NavTreeBuilder.Build(
            System.Array.Empty<string>(), s => s,
            new[] { "Cities/Silvermere" });

        Assert.All(Folders(tree), f => Assert.True(f.IsExpanded));
    }

    [Fact]
    public void Sync_CollapseByDefault_PreservesUserExpandOverride()
    {
        var target = new ObservableCollection<object>();
        var folders = new[] { "Cities", "Cities/Silvermere" };

        NavTreeBuilder.Sync<string>(target, System.Array.Empty<string>(), s => s, folders, defaultExpanded: false);
        // User expands one folder against the collapse-by-default grain.
        FolderAt(target, "Cities").IsExpanded = true;

        // A rebuild (loop/lair/folder change) must not snap it back shut.
        NavTreeBuilder.Sync<string>(target, System.Array.Empty<string>(), s => s, folders, defaultExpanded: false);

        Assert.True(FolderAt(target, "Cities").IsExpanded);
        Assert.False(FolderAt(target, "Cities/Silvermere").IsExpanded);
    }

    [Fact]
    public void Sync_ExpandByDefault_PreservesUserCollapseOverride()
    {
        var target = new ObservableCollection<object>();
        var folders = new[] { "Cities", "Cities/Silvermere" };

        NavTreeBuilder.Sync<string>(target, System.Array.Empty<string>(), s => s, folders);
        FolderAt(target, "Cities").IsExpanded = false;

        NavTreeBuilder.Sync<string>(target, System.Array.Empty<string>(), s => s, folders);

        Assert.False(FolderAt(target, "Cities").IsExpanded);
        Assert.True(FolderAt(target, "Cities/Silvermere").IsExpanded);
    }
}
