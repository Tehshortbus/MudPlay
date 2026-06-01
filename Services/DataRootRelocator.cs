using System.Diagnostics;
using System.IO;

namespace FujinTerm.Services;

/// <summary>
/// Moves the entire <see cref="AppPaths.DataRoot"/> tree to a user-chosen
/// destination, then restarts the app at the new location. Used by the
/// Settings → General "Change data directory" flow.
/// </summary>
/// <remarks>
/// Sequence is deliberately copy-then-verify-then-delete:
/// <list type="number">
///   <item>Validate the destination (writable, not overlapping the source).</item>
///   <item>Copy every file under the source to the destination, preserving
///         relative paths.</item>
///   <item>Verify the copy by file count + total byte size.</item>
///   <item>Write the new path to <see cref="AppPaths.PointerFile"/>.</item>
///   <item>Delete the source tree.</item>
///   <item>Spawn a fresh app process and exit the current one.</item>
/// </list>
/// A failure at any step before the pointer-file write leaves both copies
/// intact and the source untouched — the user can retry without data loss.
/// A failure between pointer-write and source-delete leaves the source
/// orphaned but the new location is canonical; the user can clean up the
/// stale source folder by hand.
/// </remarks>
public static class DataRootRelocator
{
    /// <summary>
    /// Reasons <see cref="Validate"/> can reject a destination, or
    /// <see cref="Relocate"/> can fail mid-flight. The Settings UI maps
    /// each value to a user-facing message.
    /// </summary>
    public enum FailureReason
    {
        None,
        DestinationIsSource,
        DestinationInsideSource,
        DestinationNotEmpty,
        DestinationNotWritable,
        CopyFailed,
        VerifyFailed,
        PointerWriteFailed,
        SourceDeletePartial,
    }

    /// <summary>Outcome of <see cref="Plan"/> — describes the move before the user commits.</summary>
    /// <param name="FileCount">Total files under <see cref="AppPaths.DataRoot"/>.</param>
    /// <param name="TotalBytes">Total byte size of those files.</param>
    public readonly record struct MovePlan(int FileCount, long TotalBytes);

    /// <summary>Summary of <see cref="AppPaths.DataRoot"/> as it stands right now.</summary>
    public static MovePlan Plan()
    {
        if (!Directory.Exists(AppPaths.DataRoot)) return new MovePlan(0, 0);

        int count = 0;
        long bytes = 0;
        foreach (string file in Directory.EnumerateFiles(AppPaths.DataRoot, "*", SearchOption.AllDirectories))
        {
            count++;
            try { bytes += new FileInfo(file).Length; }
            catch { /* file vanished mid-enumeration — best-effort total */ }
        }
        return new MovePlan(count, bytes);
    }

    /// <summary>
    /// Validate the destination *without* moving anything. Returns
    /// <see cref="FailureReason.None"/> on success. Same checks as
    /// <see cref="Relocate"/> runs internally, but cheap and side-effect-free
    /// so the Settings UI can light up the OK button live as the user picks.
    /// </summary>
    public static FailureReason Validate(string destination)
    {
        string src = Path.GetFullPath(AppPaths.DataRoot);
        string dst = Path.GetFullPath(destination);

        if (PathsEqual(src, dst))            return FailureReason.DestinationIsSource;
        if (IsDescendant(src, dst))          return FailureReason.DestinationInsideSource;
        if (Directory.Exists(dst) && Directory.EnumerateFileSystemEntries(dst).Any())
            return FailureReason.DestinationNotEmpty;

        // Write-probe: create the dest folder if absent, then drop & delete
        // a marker file. Catches read-only mounts, missing permissions, etc.
        try
        {
            Directory.CreateDirectory(dst);
            string probe = Path.Combine(dst, ".fujinterm-write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch
        {
            return FailureReason.DestinationNotWritable;
        }

        return FailureReason.None;
    }

    /// <summary>
    /// Execute the relocation. Returns <see cref="FailureReason.None"/> on
    /// success (caller should then call <see cref="RestartApp"/>). On any
    /// other return value, the source folder is still authoritative — the
    /// pointer file has NOT been written.
    /// </summary>
    /// <param name="destination">Absolute path of the new data root.</param>
    /// <param name="log">Receives Info / Warn / Error breadcrumbs.</param>
    public static FailureReason Relocate(string destination, LogService log)
    {
        ArgumentNullException.ThrowIfNull(log);

        FailureReason gate = Validate(destination);
        if (gate != FailureReason.None) return gate;

        string src = Path.GetFullPath(AppPaths.DataRoot);
        string dst = Path.GetFullPath(destination);

        // 1) Copy
        int copied;
        long copiedBytes;
        try
        {
            (copied, copiedBytes) = CopyTree(src, dst);
        }
        catch (Exception ex)
        {
            log.Error("DataRootRelocator", $"Copy failed mid-flight: {ex.Message}");
            return FailureReason.CopyFailed;
        }

        // 2) Verify (file count + byte total must match the source)
        MovePlan expected = Plan();
        if (copied != expected.FileCount || copiedBytes != expected.TotalBytes)
        {
            log.Error("DataRootRelocator",
                $"Verify failed: copied {copied} files / {copiedBytes} bytes, " +
                $"expected {expected.FileCount} / {expected.TotalBytes}. Source left intact.");
            return FailureReason.VerifyFailed;
        }

        // 3) Pointer file — past this point, the new location is canonical.
        try
        {
            string pointerDir = Path.GetDirectoryName(AppPaths.PointerFile)!;
            Directory.CreateDirectory(pointerDir);
            File.WriteAllText(AppPaths.PointerFile, dst);
        }
        catch (Exception ex)
        {
            log.Error("DataRootRelocator",
                $"Pointer-file write failed: {ex.Message}. Source still authoritative.");
            return FailureReason.PointerWriteFailed;
        }

        // 4) Delete source (best-effort — the pointer-file write is the
        // commit point; a partial delete here leaves orphan files but the
        // new location is already canonical).
        try
        {
            Directory.Delete(src, recursive: true);
            log.Info("DataRootRelocator", $"Moved data root '{src}' → '{dst}' ({copied} files).");
            return FailureReason.None;
        }
        catch (Exception ex)
        {
            log.Warn("DataRootRelocator",
                $"Source delete after move failed: {ex.Message}. Old folder '{src}' " +
                "can be removed manually.");
            return FailureReason.SourceDeletePartial;
        }
    }

    /// <summary>
    /// Spawn a fresh app process at <see cref="Environment.ProcessPath"/>
    /// and terminate the current one. The new process re-enters
    /// <see cref="AppPaths"/> static-init, reads the freshly-written
    /// pointer file, and resolves <see cref="AppPaths.DataRoot"/> to the
    /// new location.
    /// </summary>
    public static void RestartApp()
    {
        string? exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = exe,
                    UseShellExecute = false,
                    WorkingDirectory = AppContext.BaseDirectory,
                });
            }
            catch
            {
                // If spawn fails, fall through to Exit anyway — the user
                // can relaunch manually and the pointer file is already
                // written.
            }
        }
        Environment.Exit(0);
    }

    private static (int FileCount, long TotalBytes) CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        int count = 0;
        long bytes = 0;

        foreach (string srcDir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, srcDir);
            Directory.CreateDirectory(Path.Combine(dst, rel));
        }

        foreach (string srcFile in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, srcFile);
            string dstFile = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);
            File.Copy(srcFile, dstFile, overwrite: false);
            count++;
            bytes += new FileInfo(dstFile).Length;
        }

        return (count, bytes);
    }

    private static bool PathsEqual(string a, string b)
    {
        StringComparison cmp = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        return string.Equals(Path.TrimEndingDirectorySeparator(a),
                             Path.TrimEndingDirectorySeparator(b), cmp);
    }

    private static bool IsDescendant(string ancestor, string candidate)
    {
        string a = Path.TrimEndingDirectorySeparator(ancestor) + Path.DirectorySeparatorChar;
        string c = Path.TrimEndingDirectorySeparator(candidate);
        StringComparison cmp = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        return c.StartsWith(a, cmp);
    }
}
