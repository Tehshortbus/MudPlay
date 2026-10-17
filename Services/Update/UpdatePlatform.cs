using System;
using System.Runtime.InteropServices;

namespace MudPlay.Services.Update;

// Pure platform facts the updater needs: which release asset matches THIS build,
// and whether we're a self-contained install that can actually be replaced (vs a
// `dotnet run` dev build, where self-update is a no-op). No I/O beyond reading the
// process path.
public static class UpdatePlatform
{
    // The RID segment that names this platform's release asset:
    // linux-x64 / win-x64 / osx-x64 / osx-arm64. Null when the running OS/arch has
    // no published build (e.g. linux-arm64) — the caller then reports
    // NoAssetForPlatform rather than downloading the wrong archive.
    public static string? CurrentRid()
    {
        string? os =
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)   ? "linux" :
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"   :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX)     ? "osx"   : null;
        if (os is null) return null;

        string? arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64   => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        if (arch is null) return null;

        // Published matrix: linux-x64, win-x64, osx-x64, osx-arm64. linux/win only
        // ship x64; osx ships both. Anything outside that has no asset.
        return (os, arch) switch
        {
            ("linux", "x64")  => "linux-x64",
            ("win",   "x64")  => "win-x64",
            ("osx",   "x64")  => "osx-x64",
            ("osx",   "arm64")=> "osx-arm64",
            _ => null,
        };
    }

    // Windows ships a .zip; linux/macOS ship .tar.gz.
    public static string ArchiveExtension(string rid) =>
        rid.StartsWith("win", StringComparison.Ordinal) ? ".zip" : ".tar.gz";

    // The tail every asset for this platform ends with, e.g. "-linux-x64.tar.gz".
    // Null when this platform has no published build.
    public static string? AssetSuffix()
    {
        string? rid = CurrentRid();
        return rid is null ? null : $"-{rid}{ArchiveExtension(rid)}";
    }

    // True when name is the release asset for THIS platform — a MudPlay archive
    // whose tail matches AssetSuffix(). Case-insensitive on the extension only; the
    // publisher's naming is otherwise exact.
    public static bool MatchesCurrentPlatform(string? assetName)
    {
        if (string.IsNullOrEmpty(assetName)) return false;
        if (AssetSuffix() is not { } suffix) return false;
        return assetName.StartsWith("MudPlay", StringComparison.OrdinalIgnoreCase)
            && assetName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }

    // The directory the running executable lives in — the install root a self-update
    // replaces. Null when the process path is unavailable.
    public static string? InstallDirectory()
    {
        string? exe = Environment.ProcessPath;
        return string.IsNullOrEmpty(exe) ? null : System.IO.Path.GetDirectoryName(exe);
    }

    // The step log a failed swap leaves beside the executable, when it's fresh —
    // the swap relaunches the old build straight after writing it, so a recent one
    // means this launch is that relaunch. An older one was already announced.
    public static string? RecentFailedUpdateLog(TimeSpan within, string? exePath = null)
    {
        exePath ??= Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || System.IO.Path.GetDirectoryName(exePath) is not { Length: > 0 } dir)
            return null;
        string log = System.IO.Path.Combine(dir, "MudPlay-update-failed.log");
        try
        {
            return System.IO.File.Exists(log) && DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(log) <= within
                ? log
                : null;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // The Windows swap renames the replaced executable aside ("MudPlay.exe.old-<n>")
    // because a client may still be running it. Delete the ones nothing runs any
    // more; one still in use stays for a later startup. Returns how many went.
    public static int DeleteReplacedExecutables(string? exePath = null)
    {
        exePath ??= Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return 0;
        string? dir = System.IO.Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return 0;
        string[] olds;
        // Runs before the app's crash net, so a folder we can't list must not stop startup.
        try { olds = System.IO.Directory.GetFiles(dir, System.IO.Path.GetFileName(exePath) + ".old-*"); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { return 0; }
        int deleted = 0;
        foreach (string old in olds)
        {
            try { System.IO.File.Delete(old); deleted++; }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                // Another client is still running that copy; the next startup retries.
            }
        }
        return deleted;
    }

    // Whether this is a replaceable self-contained install rather than a dev run.
    // A `dotnet run` launches through the "dotnet" muxer or an apphost sitting under
    // a bin/Debug|Release tree — replacing either would be wrong (and pointless). We
    // require: a real apphost process path (not the dotnet muxer) whose install dir
    // isn't a build-output folder. Conservative on purpose — a false negative just
    // disables self-update (the user still sees the notice + can update by hand), a
    // false positive could clobber a working tree.
    public static bool IsSelfContainedInstall()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        string file = System.IO.Path.GetFileNameWithoutExtension(exe);
        if (file.Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return false;

        string? dir = System.IO.Path.GetDirectoryName(exe);
        if (string.IsNullOrEmpty(dir)) return false;

        string norm = dir.Replace('\\', '/');
        if (norm.Contains("/bin/Debug/", StringComparison.OrdinalIgnoreCase) ||
            norm.Contains("/bin/Release/", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}
