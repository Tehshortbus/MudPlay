using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FujinTerm.Game.Map;

// Path helpers shared by the three navigation organisers that group their
// entries into folders: GOTO favourites (FavoritesStore), saved loops
// (LoopManager), and Auto-Lair setups (LairManager). All three use the same
// /-separated folder vocabulary (e.g. "Cities/Silvermere", "" = root), so the
// split / join / ancestor logic lives here once rather than being
// re-implemented per store.
//
// Normalisation is deliberately conservative: it trims each segment, drops
// empty segments (so "a//b/" → "a/b"), and never lets . / .. through
// (path-traversal guard — folder paths feed real filesystem subdirectories for
// loops/lairs).
public static class NavFolders
{
    // The folder separator. Always /, even on Windows — the stored vocabulary
    // is platform-independent; filesystem callers translate to
    // Path.DirectorySeparatorChar.
    public const char Separator = '/';

    // Canonicalise a user- or disk-derived folder path to the stored form:
    // trimmed segments, no empties, no ./.., joined by Separator. Returns empty
    // for null / whitespace / root.
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

    // Split a normalised path into its segments. Empty path → empty array.
    public static string[] Segments(string? path)
    {
        string norm = Normalize(path);
        return norm.Length == 0
            ? Array.Empty<string>()
            : norm.Split(Separator);
    }

    // Append child (a single segment or sub-path) to parent, normalising the
    // result. Either side may be empty.
    public static string Combine(string? parent, string? child)
    {
        string p = Normalize(parent);
        string c = Normalize(child);
        if (p.Length == 0) return c;
        if (c.Length == 0) return p;
        return p + Separator + c;
    }

    // Parent folder of path, or empty at the root.
    public static string Parent(string? path)
    {
        string norm = Normalize(path);
        int i = norm.LastIndexOf(Separator);
        return i < 0 ? string.Empty : norm[..i];
    }

    // Last segment (the folder's own display name), or empty for the root.
    public static string LastSegment(string? path)
    {
        string norm = Normalize(path);
        int i = norm.LastIndexOf(Separator);
        return i < 0 ? norm : norm[(i + 1)..];
    }

    // True when path equals ancestor or sits anywhere beneath it. Used by
    // rename / delete to find every entry in a folder subtree. Root ("") is an
    // ancestor of everything.
    public static bool IsSelfOrDescendant(string? ancestor, string? path)
    {
        string a = Normalize(ancestor);
        string p = Normalize(path);
        if (a.Length == 0) return true;
        if (p.Length < a.Length) return false;
        if (!p.StartsWith(a, StringComparison.OrdinalIgnoreCase)) return false;
        return p.Length == a.Length || p[a.Length] == Separator;
    }

    // Re-root path from under oldRoot to under newRoot. Caller guarantees path
    // is a self-or-descendant of oldRoot (see IsSelfOrDescendant).
    public static string Rebase(string oldRoot, string newRoot, string path)
    {
        string a = Normalize(oldRoot);
        string p = Normalize(path);
        string tail = a.Length == 0 ? p : p[a.Length..].TrimStart(Separator);
        return Combine(newRoot, tail);
    }

    // Folder (in stored / form) of filePath relative to rootDir. Empty when the
    // file sits directly in the root. Bridges the filesystem (real
    // subdirectories under the BBS Loops folder) into the stored vocabulary
    // used by loops + lairs.
    public static string RelativeFolder(string rootDir, string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir)) return string.Empty;
        string rel = Path.GetRelativePath(rootDir, dir);
        if (rel == "." || rel.StartsWith("..", StringComparison.Ordinal)) return string.Empty;
        return Normalize(rel);
    }

    // Absolute on-disk directory for stored folder under rootDir, translating
    // Separator to the platform separator. Returns rootDir for the empty root.
    public static string ToDirectory(string rootDir, string? folder)
    {
        string[] segs = Segments(folder);
        return segs.Length == 0 ? rootDir : Path.Combine(rootDir, Path.Combine(segs));
    }

    // Every ancestor folder of the given paths, including the paths themselves,
    // as a de-duplicated set — the full set of nodes a tree view must render so
    // intermediate folders aren't missing. Excludes the empty root.
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
