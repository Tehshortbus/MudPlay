using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FujinTerm.Game.Map;

/// <summary>
/// Path helpers shared by the three navigation organisers that group
/// their entries into folders: GOTO favourites
/// (<see cref="Services.FavoritesStore"/>), saved loops
/// (<see cref="LoopManager"/>), and Auto-Lair setups
/// (<see cref="LairManager"/>). All three use the same <c>/</c>-separated
/// folder vocabulary (e.g. <c>"Cities/Silvermere"</c>, <c>""</c> = root),
/// so the split / join / ancestor logic lives here once rather than
/// being re-implemented per store.
/// </summary>
/// <remarks>
/// Normalisation is deliberately conservative: it trims each segment,
/// drops empty segments (so <c>"a//b/"</c> → <c>"a/b"</c>), and never
/// lets <c>.</c> / <c>..</c> through (path-traversal guard — folder
/// paths feed real filesystem subdirectories for loops/lairs).
/// </remarks>
public static class NavFolders
{
    /// <summary>The folder separator. Always <c>/</c>, even on Windows — the
    /// stored vocabulary is platform-independent; filesystem callers
    /// translate to <see cref="System.IO.Path.DirectorySeparatorChar"/>.</summary>
    public const char Separator = '/';

    /// <summary>
    /// Canonicalise a user- or disk-derived folder path to the stored
    /// form: trimmed segments, no empties, no <c>.</c>/<c>..</c>, joined
    /// by <see cref="Separator"/>. Returns <see cref="string.Empty"/>
    /// for null / whitespace / root.
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        var sb = new StringBuilder(path.Length);
        foreach (string raw in path.Split('/', '\\'))
        {
            string seg = raw.Trim();
            if (seg.Length == 0 || seg == "." || seg == "..") continue;
            if (sb.Length > 0) sb.Append(Separator);
            sb.Append(seg);
        }
        return sb.ToString();
    }

    /// <summary>Split a normalised path into its segments. Empty path → empty array.</summary>
    public static string[] Segments(string? path)
    {
        string norm = Normalize(path);
        return norm.Length == 0
            ? Array.Empty<string>()
            : norm.Split(Separator);
    }

    /// <summary>
    /// Append <paramref name="child"/> (a single segment or sub-path) to
    /// <paramref name="parent"/>, normalising the result. Either side may
    /// be empty.
    /// </summary>
    public static string Combine(string? parent, string? child)
    {
        string p = Normalize(parent);
        string c = Normalize(child);
        if (p.Length == 0) return c;
        if (c.Length == 0) return p;
        return p + Separator + c;
    }

    /// <summary>Parent folder of <paramref name="path"/>, or <see cref="string.Empty"/> at the root.</summary>
    public static string Parent(string? path)
    {
        string norm = Normalize(path);
        int i = norm.LastIndexOf(Separator);
        return i < 0 ? string.Empty : norm[..i];
    }

    /// <summary>Last segment (the folder's own display name), or <see cref="string.Empty"/> for the root.</summary>
    public static string LastSegment(string? path)
    {
        string norm = Normalize(path);
        int i = norm.LastIndexOf(Separator);
        return i < 0 ? norm : norm[(i + 1)..];
    }

    /// <summary>
    /// True when <paramref name="path"/> equals <paramref name="ancestor"/>
    /// or sits anywhere beneath it. Used by rename / delete to find every
    /// entry in a folder subtree. Root (<c>""</c>) is an ancestor of
    /// everything.
    /// </summary>
    public static bool IsSelfOrDescendant(string? ancestor, string? path)
    {
        string a = Normalize(ancestor);
        string p = Normalize(path);
        if (a.Length == 0) return true;
        if (p.Length < a.Length) return false;
        if (!p.StartsWith(a, StringComparison.OrdinalIgnoreCase)) return false;
        return p.Length == a.Length || p[a.Length] == Separator;
    }

    /// <summary>
    /// Re-root <paramref name="path"/> from under <paramref name="oldRoot"/>
    /// to under <paramref name="newRoot"/>. Caller guarantees
    /// <paramref name="path"/> is a self-or-descendant of
    /// <paramref name="oldRoot"/> (see <see cref="IsSelfOrDescendant"/>).
    /// </summary>
    public static string Rebase(string oldRoot, string newRoot, string path)
    {
        string a = Normalize(oldRoot);
        string p = Normalize(path);
        string tail = a.Length == 0 ? p : p[a.Length..].TrimStart(Separator);
        return Combine(newRoot, tail);
    }

    /// <summary>
    /// Folder (in stored <c>/</c> form) of <paramref name="filePath"/>
    /// relative to <paramref name="rootDir"/>. Empty when the file sits
    /// directly in the root. Bridges the filesystem (real subdirectories
    /// under the BBS Loops folder) into the stored vocabulary used by
    /// loops + lairs.
    /// </summary>
    public static string RelativeFolder(string rootDir, string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir)) return string.Empty;
        string rel = Path.GetRelativePath(rootDir, dir);
        if (rel == "." || rel.StartsWith("..", StringComparison.Ordinal)) return string.Empty;
        return Normalize(rel);
    }

    /// <summary>
    /// Absolute on-disk directory for stored folder
    /// <paramref name="folder"/> under <paramref name="rootDir"/>,
    /// translating <see cref="Separator"/> to the platform separator.
    /// Returns <paramref name="rootDir"/> for the empty root.
    /// </summary>
    public static string ToDirectory(string rootDir, string? folder)
    {
        string[] segs = Segments(folder);
        return segs.Length == 0 ? rootDir : Path.Combine(rootDir, Path.Combine(segs));
    }

    /// <summary>
    /// Every ancestor folder of the given paths, including the paths
    /// themselves, as a de-duplicated set — the full set of nodes a tree
    /// view must render so intermediate folders aren't missing. Excludes
    /// the empty root.
    /// </summary>
    public static IReadOnlyCollection<string> ExpandAncestors(IEnumerable<string?> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? raw in paths)
        {
            string cur = Normalize(raw);
            while (cur.Length > 0)
            {
                set.Add(cur);
                cur = Parent(cur);
            }
        }
        return set;
    }
}
