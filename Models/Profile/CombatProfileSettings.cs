using System.Collections.Generic;

namespace MudPlay.Models.Profile;

// The character's casting-spell-profile set — the ordered list of named combat
// spell profiles plus which one is active. Persisted as the top-level
// CharacterProfile.CombatProfiles blob (like Equipment / PartyBuffs), never a
// tier-merged Settings section, since it is whole-character state.
//
// Invariant maintained by CombatProfileManager: there is always at least one
// profile, and the active profile's spell fields mirror the live
// CombatSettings' spell fields (both change only on Save or a profile switch).
public sealed class CombatProfileSettings
{
    // Profiles in display order; the 1-based position is the user-facing "number".
    public List<CombatSpellProfile> Profiles { get; set; } = new();

    // Id of the active profile. Empty / unknown resolves to the first profile.
    public string ActiveId { get; set; } = string.Empty;

    // Groups the user has taken out of combat profiles ("Include in combat profile"
    // unchecked): one value for the whole character. Kept true by holding every
    // profile's copy of a shared group identical, so a switch changes nothing there.
    // Null / empty = every group is per profile.
    public List<CombatProfileGroup>? SharedGroups { get; set; }

    // Schema version, so a one-time back-fill can run when the shape of a profile
    // grows. Profiles from before combat profiles became a full loadout carry only
    // the spell config; their Health / weapon / Spells-subset fields default to
    // blank. Left at those defaults they would overwrite the character's real
    // (previously shared) settings on the first switch, so CombatProfileManager
    // back-fills every profile from the live sections once, then stamps this.
    // 0 = pre-full-loadout; FullLoadoutVersion = migrated.
    public int SchemaVersion { get; set; }

    // The schema version once the full-loadout back-fill has run.
    public const int FullLoadoutVersion = 1;

    // Action order (spells-first / physical-first / custom cycle) became per-profile at
    // this version. Profiles created earlier deserialize their new ActionOrder field to
    // the enum default, which would overwrite the character's real (previously shared)
    // action order on the first switch — so it's back-filled from the live value once.
    public const int PerProfileActionOrderVersion = 2;

    // Run direction / break-before-running, self-bless timing and the Party-tab
    // healing + bless became per-profile at this version; back-filled from the live
    // values once, like PerProfileActionOrderVersion.
    public const int PerProfileRunBlessPartyVersion = 3;
}
