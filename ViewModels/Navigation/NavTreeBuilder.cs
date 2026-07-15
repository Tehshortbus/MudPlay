using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using FujinTerm.Game.Map;

namespace FujinTerm.ViewModels.Navigation;

// Turns a flat list of row view-models — each tagged with a stored
// /-separated folder path — plus the set of folders that must exist even
// when empty into the nested folder/leaf tree the navigation surfaces
// render. Shared by the Manage dialog (loops + lairs) and the rail (gotos +
// loops + lairs) so the grouping logic lives in one place.
public static class NavTreeBuilder
{
    // Build the top-level node list for rows. Folders sort before leaves;
    // both alphabetical (case-insensitive, via SortKeyOf). folderOf returns
    // a row's stored folder (empty = root). allFolders seeds folders that
    // have no rows yet (empty folders the user created) so they still
    // render. defaultExpanded seeds each folder's starting expand state — the
    // rail's Loops+Lairs tree passes false (start collapsed); the Manage dialog
    // and GOTO tree keep the default true.
    public static List<object> Build<TRow>(
        IEnumerable<TRow> rows,
        Func<TRow, string?> folderOf,
        IEnumerable<string> allFolders,
        bool defaultExpanded = true)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(folderOf);
        ArgumentNullException.ThrowIfNull(allFolders);

        var folders = new Dictionary<string, NavFolderNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<object>();

        // Ensure a folder node (and all its ancestors) exists, linking
        // each into its parent's Children. Returns the deepest node.
        NavFolderNodeViewModel Ensure(string path)
        {
            if (folders.TryGetValue(path, out NavFolderNodeViewModel? existing))
                return existing;

            var node = new NavFolderNodeViewModel(path, NavFolders.LastSegment(path), defaultExpanded);
            folders[path] = node;

            string parent = NavFolders.Parent(path);
            if (parent.Length == 0) roots.Add(node);
            else Ensure(parent).Children.Add(node);
            return node;
        }

        // Seed empty + ancestor folders first so intermediate nodes
        // aren't missing when a row lives several levels deep.
        foreach (string folder in NavFolders.ExpandAncestors(allFolders))
            Ensure(folder);

        foreach (TRow row in rows)
        {
            string folder = NavFolders.Normalize(folderOf(row));
            if (folder.Length == 0) roots.Add(row!);
            else Ensure(folder).Children.Add(row!);
        }

        SortNodes(roots);
        foreach (NavFolderNodeViewModel node in folders.Values)
            SortNodes(node.Children);

        return roots;
    }

    // Replace target's contents with a freshly built tree, preserving folder
    // expand/collapse state across the rebuild (keyed by folder path) so a
    // refresh doesn't snap every folder back to its default. defaultExpanded is
    // the per-surface starting state (see Build).
    public static void Sync<TRow>(
        ObservableCollection<object> target,
        IEnumerable<TRow> rows,
        Func<TRow, string?> folderOf,
        IEnumerable<string> allFolders,
        bool defaultExpanded = true)
    {
        ArgumentNullException.ThrowIfNull(target);
        var overridden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectOverrides(target, overridden, defaultExpanded);

        List<object> built = Build(rows, folderOf, allFolders, defaultExpanded);
        RestoreOverrides(built, overridden, defaultExpanded);

        target.Clear();
        foreach (object node in built) target.Add(node);
    }

    private static void SortNodes(IList<object> nodes)
    {
        var ordered = nodes
            .OrderBy(n => n is NavFolderNodeViewModel ? 0 : 1)
            .ThenBy(SortKeyOf, StringComparer.OrdinalIgnoreCase)
            .ToList();
        nodes.Clear();
        foreach (object n in ordered) nodes.Add(n);
    }

    private static string SortKeyOf(object node) => node switch
    {
        NavFolderNodeViewModel f => f.Name,
        LoopRowViewModel l => l.Name,
        LairSetupRowViewModel s => s.Name,
        FavoriteRowViewModel r => r.Label,
        ManagerLoopRow ml => ml.Name,
        ManagerLairSetupRow msr => msr.Name,
        _ => node.ToString() ?? string.Empty,
    };

    // Expand/collapse state is keyed by folder path. We record the folders the
    // user toggled AWAY from the tree's default (a collapsed folder in an
    // expand-by-default tree, or an expanded folder in a collapse-by-default
    // one) so a rebuild keeps that override instead of snapping every folder
    // back to its default.
    private static void CollectOverrides(IEnumerable<object> nodes, HashSet<string> overridden, bool defaultExpanded)
    {
        foreach (object node in nodes)
        {
            if (node is NavFolderNodeViewModel f)
            {
                if (f.IsExpanded != defaultExpanded) overridden.Add(f.Path);
                CollectOverrides(f.Children, overridden, defaultExpanded);
            }
        }
    }

    private static void RestoreOverrides(IEnumerable<object> nodes, HashSet<string> overridden, bool defaultExpanded)
    {
        foreach (object node in nodes)
        {
            if (node is NavFolderNodeViewModel f)
            {
                if (overridden.Contains(f.Path)) f.IsExpanded = !defaultExpanded;
                RestoreOverrides(f.Children, overridden, defaultExpanded);
            }
        }
    }
}
