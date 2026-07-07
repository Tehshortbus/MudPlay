using System;
using System.Collections.Generic;
using System.Linq;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels.Settings;

// One selectable source in the BBS section's "import logon steps from another
// character" picker: a single character's saved menu-nav sequence for one BBS.
// Only the steps travel on import — usernames and passwords never do, and a
// step's {username} / {password} placeholders resolve from the importing
// character's own credentials at connect time, so an imported flow works
// per-character without copying secrets.
public sealed record MenuNavImportOption(string ProfileName, string BbsName, IReadOnlyList<MenuStep> Steps)
{
    // Shown directly in the ComboBox (no ItemTemplate needed). Naming the BBS
    // lets the user tell a same-front-end candidate apart from a long-shot at a
    // glance; the step count hints how complete it is.
    public override string ToString() =>
        $"{ProfileName} @ {BbsName} ({Steps.Count} step{(Steps.Count == 1 ? "" : "s")})";

    // Project every saved character's per-BBS menu-nav sequences into import
    // options. Empty sequences are dropped, and so is the exact (character, BBS)
    // pair the user is editing right now — importing that back onto itself is a
    // no-op. Same-BBS candidates sort first (a matching front-end is the likeliest
    // clean fit), then by BBS, then by character name.
    public static IReadOnlyList<MenuNavImportOption> Build(
        IEnumerable<(string bbs, string name, CharacterProfile profile)> profiles,
        string editingBbs,
        string? currentBbs,
        string? currentName)
    {
        var options = new List<MenuNavImportOption>();
        foreach ((string bbs, string name, CharacterProfile profile) in profiles)
        {
            if (profile.BbsCredentials is not { } creds) continue;
            foreach ((string credBbs, BbsCredentials cred) in creds)
            {
                if (cred.MenuNavSteps.Count == 0) continue;
                bool isEditingTarget =
                    string.Equals(credBbs, editingBbs, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(bbs, currentBbs, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(name, currentName, StringComparison.Ordinal);
                if (isEditingTarget) continue;
                options.Add(new MenuNavImportOption(name, credBbs, cred.MenuNavSteps));
            }
        }

        return options
            .OrderByDescending(o => string.Equals(o.BbsName, editingBbs, StringComparison.OrdinalIgnoreCase))
            .ThenBy(o => o.BbsName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.ProfileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
