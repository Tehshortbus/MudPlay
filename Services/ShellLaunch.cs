using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FujinTerm.Services;

// Cross-platform "hand this off to the OS" launcher. Used to open log folders,
// docs, URLs, and similar bystander targets that don't belong inside a
// FujinTerm window. Each method swallows launcher failures and returns false
// rather than throwing — callers usually surface a status-bar message on
// failure.
public static class ShellLaunch
{
    // Open a file or folder path in the platform's default handler
    // (Explorer / Finder / xdg-open). Returns true on success.
    public static bool OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        return TryLaunch(path);
    }

    // Open an http:// / https:// URL in the user's default browser. Returns
    // true on success.
    public static bool OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        return TryLaunch(parsed.ToString());
    }

    private static bool TryLaunch(string target)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", target);
            }
            else
            {
                // Linux / *BSD: xdg-open. Doesn't throw on missing handlers —
                // it returns a non-zero exit code which we don't inspect here.
                Process.Start("xdg-open", target);
            }
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Handler binary not found (e.g., headless Linux without xdg-utils).
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }
}
