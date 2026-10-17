using System.Collections.Generic;
using System.Linq;
using MudPlay.Models.Profile;

namespace MudPlay.Services;

// Command-line launch options, parsed once in Program.Main before the app
// starts. Today that's --profile: this instance loads the first named profile,
// and any extra names each launch their own instance (one process per profile,
// spawned by Program.Main). The chosen profile for THIS instance is stashed in
// RequestedProfileToken and consumed by the AppServices startup-profile block.
//
// The parse/resolve helpers are pure (no state); the
// static properties carry the parsed result across to AppServices at startup.
public static class StartupOptions
{
    // The single profile token this instance should load at startup (the first
    // --profile entry), or null when none was given. A token is either
    // "BBS/Name" (explicit) or a bare "Name" resolved uniquely across saved
    // profiles. Set once in Program.Main, read once by AppServices.
    public static string? RequestedProfileToken { get; set; }

    // How many sibling instances Program.Main spawned for the extra --profile
    // entries. Purely informational — logged once at startup.
    public static int SpawnedSiblings { get; set; }

    // --reconnect: dial the loaded profile's BBS on startup even when Settings →
    // General → Auto-connect is off. Only the self-updater passes it, so a client
    // that was mid-session before an update comes back in-game instead of cold.
    // Consumed once by the startup auto-connect check and cleared, so a later
    // profile load in the same session doesn't re-dial off a stale flag.
    public static bool ForceReconnect { get; set; }

    // Set when a --profile token was given but didn't resolve (a typo, or an
    // ambiguous bare name on more than one BBS), or when the startup profile
    // (--profile or auto-load last) failed to load. The main window surfaces it on
    // the terminal at startup so the user sees WHY their named profile didn't
    // load — instead of the app quietly coming up on a different / default
    // profile. Null when there's nothing to report.
    public static string? ProfileNotice { get; set; }

    // Collect every --profile value from the command line, splitting a
    // comma-separated list and honouring the flag repeated. Both "--profile x"
    // and "--profile=x" are accepted. Surrounding whitespace and empty entries
    // are dropped; internal spaces (a profile named "My Char") are kept.
    public static List<string> ParseProfileTokens(IReadOnlyList<string> args)
    {
        const string flag = "--profile";
        List<string> tokens = new();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            string? value = null;
            if (a.StartsWith(flag + "=", System.StringComparison.OrdinalIgnoreCase))
                value = a[(flag.Length + 1)..];
            else if (string.Equals(a, flag, System.StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                value = args[++i];
            if (value is null) continue;

            foreach (string part in value.Split(','))
            {
                string t = part.Trim();
                if (t.Length > 0) tokens.Add(t);
            }
        }
        return tokens;
    }

    // Resolve a single token to a saved profile. "BBS/Name" matches that exact
    // pair (case-insensitive); a bare "Name" matches when exactly one saved
    // profile carries that character name across all BBSes. Returns null with a
    // human-readable reason in error when the token doesn't resolve (bad shape /
    // not found / ambiguous). '/' can never appear in a BBS or profile name
    // (they're used as folder names), so it's an unambiguous pair separator.
    public static ProfileRef? ResolveToken(string token, IReadOnlyList<ProfileRef> available, out string? error)
    {
        error = null;
        string t = token.Trim();

        int slash = t.IndexOf('/');
        if (slash >= 0)
        {
            string bbs  = t[..slash].Trim();
            string name = t[(slash + 1)..].Trim();
            if (bbs.Length == 0 || name.Length == 0)
            {
                error = $"'{token}' is not a valid BBS/Name profile reference.";
                return null;
            }
            ProfileRef? hit = available.FirstOrDefault(p =>
                string.Equals(p.Bbs,  bbs,  System.StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.Name, name, System.StringComparison.OrdinalIgnoreCase));
            if (hit is null)
                error = $"no saved profile '{name}' on BBS '{bbs}'.";
            return hit;
        }

        // Bare name: accept only a unique match across BBSes.
        List<ProfileRef> matches = available
            .Where(p => string.Equals(p.Name, t, System.StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 1) return matches[0];
        if (matches.Count == 0)
            error = $"no saved profile named '{t}'.";
        else
            error = $"profile name '{t}' exists on multiple BBSes ({string.Join(", ", matches.Select(m => m.Bbs))}); " +
                    $"qualify it as \"BBS/{t}\".";
        return null;
    }
}
