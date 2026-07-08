namespace FujinTerm.Models.GameData;

// Observation-only fields for one player — what the server told us via
// who / look. Lives at the BBS tier: every player observed on a given BBS
// is stored under Data/BBS/{bbs-name}/players.json. Same display name on a
// different BBS represents a different person, so the per-BBS scope matches
// the social reality.
//
// Mutations to this record come from the server-output parsers
// (WhoListParser and the planned look-on-player parser). User-authored
// per-character fields live separately on PlayerCustomization at the
// Character tier. The one authored field stored HERE is AccountName: it's
// BBS-scoped truth (an account belongs to a BBS, shared across all our
// alts on that realm), so it belongs at the BBS tier next to the
// observation rather than in the per-character customization layer — and
// there's no wire source for the account→character link, so the user
// authors it via the edit dialog. PlayerDatabase merges both layers for
// display.
//
// GivenName is the first word of the in-game name (the "Forged" in "Forged
// Paradigm"), may be empty for legacy records; FamilyName is the remainder
// after the first space, empty for single-word names. Class comes from a
// future look / @health parser (who doesn't carry it); Race is the same
// source. Alignment is the most recent seen on who ("Neutral" when the
// column was blank). Title is the most recent seen on who; class + level
// range can be inferred from it via the future class-titles table. Gang is
// the most recent gang/guild name ("of …" suffix on who), empty when
// ungang'd. Role is the MegaMUD-style trailing marker — M mudop, S sysop,
// V visitor, null for regular players. Equipment is the most recent
// loadout seen on look <player> (empty list = explicit "Nothing"; null =
// never looked at). LastGreetedUtc is when GreetManager last auto-greeted
// this player (null if never), driving the once-per-local-day greet rule.
// Level is the exact level from an @level probe reply
// (PlayerDatabase.RecordLevel); null until the player answers one — the
// title-derived range from GameData.ClassTitleTable is the only signal
// before that, and Level is authoritative over the range once set because
// a title only pins a 5-level band.
public sealed record PlayerObservation(
    string GivenName,
    string FamilyName,
    string? Class,
    string? Race,
    string? Alignment,
    string? Title,
    string? Gang,
    string? Role,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc,
    IReadOnlyList<EquipmentItem>? Equipment = null,
    DateTime? LastGreetedUtc = null,
    int? Level = null,
    // Authored BBS-tier override: the player's BBS account name, when it
    // differs from their in-game given name. Some boards key their presence
    // lines (logon / logoff) on the account name instead of the character
    // name; the disconnect-watcher matches a captured name against this
    // first, falling back to the given name when it's null. Both account and
    // in-game names are unique, so the mapping is 1:1. null = no override
    // (account name equals the in-game name, the common case).
    string? AccountName = null)
{
    // Combined display name — "GivenName FamilyName", trimmed. Used by the
    // database's case-insensitive lookup and by the customization
    // dictionary as the key.
    public string DisplayName =>
        string.IsNullOrEmpty(FamilyName) ? GivenName : $"{GivenName} {FamilyName}";

    // Split a wire-format name (e.g. "Forged Paradigm") into given + family
    // on the first whitespace. Multi-space names treat everything after the
    // first space as the family name; single-word names get an empty
    // family. null / empty input returns empty strings so observation
    // writes don't fail on garbage.
    public static (string Given, string Family) SplitName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return (string.Empty, string.Empty);
        string trimmed = name.Trim();
        int space = trimmed.IndexOf(' ');
        return space < 0
            ? (trimmed, string.Empty)
            : (trimmed[..space], trimmed[(space + 1)..].TrimStart());
    }
}

// User-authored per-player settings for the loaded character. Lives at the
// Character tier on CharacterProfile.PlayerCustomizations; only entries
// that hold a non-default value are persisted so a fresh profile doesn't
// get bloated with one entry per observed stranger (see IsDefault).
//
// RemoteControls is the bitmask of @-command categories the user allows
// from this player. InviteToPartyIfSeen auto-invites this player when our
// character spots them in the room. JoinPartyIfInvited auto-accepts party
// invites from this player. DontAutoDelete skips this record during
// stale-record cleanup. Notes is a free-form note from the edit dialog.
public readonly record struct PlayerCustomization(
    PlayerRemoteControls RemoteControls = PlayerRemoteControls.None,
    bool InviteToPartyIfSeen = false,
    bool JoinPartyIfInvited = false,
    bool DontAutoDelete = false,
    string? Notes = null)
{
    // True when every field holds the default value. Drives the "don't persist" rule.
    public bool IsDefault
        => RemoteControls == PlayerRemoteControls.None
        && !InviteToPartyIfSeen
        && !JoinPartyIfInvited
        && !DontAutoDelete
        && string.IsNullOrEmpty(Notes);
}

// Merged display view — the observation fields + the customization fields
// for one player. Built by PlayerDatabase for the UI; not persisted
// directly.
//
// The split is invisible to the table view + edit dialog (they keep
// reading one record), but writes have to go to the right layer:
// observation writes call PlayerDatabase.RecordObservation; customization
// writes (from the edit dialog Save path) call
// PlayerDatabase.EditCustomization.
public sealed record PlayerRecord(
    string GivenName,
    string FamilyName,
    string? Class,
    string? Race,
    string? Alignment,
    string? Title,
    string? Gang,
    string? Role,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc,
    string? Notes = null,
    PlayerRemoteControls RemoteControls = PlayerRemoteControls.None,
    bool InviteToPartyIfSeen = false,
    bool JoinPartyIfInvited = false,
    bool DontAutoDelete = false,
    IReadOnlyList<EquipmentItem>? Equipment = null,
    int? Level = null,
    // Mirrors PlayerObservation.AccountName (BBS tier) into the merged row so
    // the edit dialog can show + author it. null = account name equals the
    // in-game name.
    string? AccountName = null)
{
    // Combined display name — "GivenName FamilyName", trimmed. Identical
    // contract to PlayerObservation.DisplayName so callers don't have to
    // know which type they're holding.
    public string DisplayName =>
        string.IsNullOrEmpty(FamilyName) ? GivenName : $"{GivenName} {FamilyName}";

    public static (string Given, string Family) SplitName(string? name)
        => PlayerObservation.SplitName(name);

    // Combine a BBS-tier observation with the loaded character's
    // customization (if any) into a single display row.
    public static PlayerRecord Merge(PlayerObservation obs, PlayerCustomization cust) => new(
        GivenName:           obs.GivenName,
        FamilyName:          obs.FamilyName,
        Class:               obs.Class,
        Race:                obs.Race,
        Alignment:           obs.Alignment,
        Title:               obs.Title,
        Gang:                obs.Gang,
        Role:                obs.Role,
        FirstSeenUtc:        obs.FirstSeenUtc,
        LastSeenUtc:         obs.LastSeenUtc,
        Notes:               cust.Notes,
        RemoteControls:      cust.RemoteControls,
        InviteToPartyIfSeen: cust.InviteToPartyIfSeen,
        JoinPartyIfInvited:  cust.JoinPartyIfInvited,
        DontAutoDelete:      cust.DontAutoDelete,
        Equipment:           obs.Equipment,
        Level:               obs.Level,
        AccountName:         obs.AccountName);

    // Pull just the customization slice off this merged row (used by the edit dialog Save path).
    public PlayerCustomization ToCustomization() => new(
        RemoteControls:      RemoteControls,
        InviteToPartyIfSeen: InviteToPartyIfSeen,
        JoinPartyIfInvited:  JoinPartyIfInvited,
        DontAutoDelete:      DontAutoDelete,
        Notes:               Notes);
}

// Per-player allowed remote-command categories. Matches MegaMUD's "Allowed
// Remote Control" panel layout — 12 grouped categories that span the
// individual @-command set. RemoteCommandManager consults this bitmask
// before dispatching any @-command from a non-party player.
//
// Empty (None) means "deny every category" — the default for
// newly-observed players. All grants the full set in one flag. Hard-blocks
// for destructive commands (@do reroll / @do suicide when lives ≤
// threshold, @party reroll / @party suicide always) bypass this bitmask
// entirely — those are engine policy, not per-player choices.
[Flags]
public enum PlayerRemoteControls
{
    None                = 0,

    // Identification — @version.
    QueryVersion        = 1 << 0,

    // Experience snapshot — @exp.
    QueryExperience     = 1 << 1,

    // Vital signs + status — @health, @status, @lives, @party (status form).
    QueryHealthStatus   = 1 << 2,

    // Where am I — @where.
    QueryLocation       = 1 << 3,

    // Inventory snapshot — @have, @wealth, @enc.
    QueryInventory      = 1 << 4,

    // Solicit a party invite — @invite.
    RequestInvite       = 1 << 5,

    // Direct movement — @goto, @follow.
    MovePlayer          = 1 << 6,

    // @do <command> passthrough — highest trust.
    ExecuteCommands     = 1 << 7,

    // Force-disconnect — @hangup.
    HangupDisconnect    = 1 << 8,

    // Toggle auto-modes / engine settings — @auto-*.
    AlterSettings       = 1 << 9,

    // Re-route incoming chat — @divert.
    DivertConversations = 1 << 10,

    // Admin / wizard commands (sysop-only on most realms).
    SysopCommands       = 1 << 11,

    // Convenience — every category above flipped on.
    All = QueryVersion | QueryExperience | QueryHealthStatus | QueryLocation
        | QueryInventory | RequestInvite | MovePlayer | ExecuteCommands
        | HangupDisconnect | AlterSettings | DivertConversations | SysopCommands,
}

// One equipment slot's contents from a look <player> response. SlotLabel
// is the literal label printed by the server (e.g. "Torso", "Weapon Hand",
// "Two Handed"); we don't normalise — different realms print 2H weapons
// with either "Weapon Hand" or "Two Handed" and consumers can treat both
// equivalently when needed. Wrist / Finger / Worn can repeat across
// multiple items. ItemName is the item display name as printed.
public readonly record struct EquipmentItem(string SlotLabel, string ItemName);
