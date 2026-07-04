using System.Runtime.InteropServices;
using System.Text;

namespace FujinTerm.Services;

// Sets the OS process name (the comm field shown by ps, top, htop, and
// process managers) to the active {char}@{bbs} identity, so a user running
// several FujinTerm instances side-by-side can tell them apart at the process
// level. Linux-only via prctl(PR_SET_NAME); a silent no-op on every other
// platform (Windows / macOS expose no equivalently cheap per-process rename,
// and the window title already carries the readable form there).
public static class ProcessTitle
{
    // PR_SET_NAME's option constant, and the kernel's hard cap on the comm
    // name: 16 bytes including the trailing NUL → 15 usable bytes.
    private const int PrSetName = 15;
    private const int MaxNameBytes = 15;

    // Compose {profile}@{bbs} from the loaded profile + active BBS and push it
    // to the kernel as this process's name. Either part may be null: yields the
    // char alone, @{bbs} alone, or the bare app name when both are absent.
    // Over-long names are truncated to the 15-byte comm limit without splitting
    // a multi-byte UTF-8 sequence.
    public static void Set(string? profile, string? bbs)
    {
        if (!OperatingSystem.IsLinux()) return;

        byte[] buffer = ToCommBytes(Compose(profile, bbs));
        try
        {
            prctl(PrSetName, buffer, 0, 0, 0);
        }
        // The comm name is cosmetic — if libc / prctl is somehow unavailable,
        // skip rather than crash the app over a process-list label.
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private static string Compose(string? profile, string? bbs)
    {
        bool hasProfile = !string.IsNullOrWhiteSpace(profile);
        bool hasBbs = !string.IsNullOrWhiteSpace(bbs);
        if (hasProfile && hasBbs) return $"{profile}@{bbs}";
        if (hasProfile) return profile!;
        if (hasBbs) return $"@{bbs}";
        return "FujinTerm";
    }

    private static byte[] ToCommBytes(string name)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(name);
        int len = Math.Min(utf8.Length, MaxNameBytes);
        // Don't cut mid-sequence: if the first dropped byte is a UTF-8
        // continuation byte (10xxxxxx), back the cut up to the run's start.
        while (len > 0 && len < utf8.Length && (utf8[len] & 0xC0) == 0x80) len--;
        byte[] buffer = new byte[len + 1]; // +1 leaves the trailing NUL.
        Array.Copy(utf8, buffer, len);
        return buffer;
    }

    // Classic DllImport rather than LibraryImport: the source-generated
    // marshaller needs project-wide AllowUnsafeBlocks, which isn't worth
    // enabling for one cosmetic interop call.
    [DllImport("libc", EntryPoint = "prctl")]
    private static extern int prctl(int option, byte[] arg2, nuint arg3, nuint arg4, nuint arg5);
}
