namespace MudPlay.Models.GameData;

// Observation-only fields for one player — what the server told us via
// who / look. Kept per realm (AppPaths.RealmPlayersFile): every character
// we play on the realm shares it, and the same name on another realm is a
// different person.
//
// Mutations to this record come from the server-output parsers
// (WhoListParser and LookParser). User-authored
// per-character fields live separately on PlayerCustomization at the
// Character tier. The one authored field stored HERE is AccountName: it's
// realm-scoped truth (an account belongs to a BBS, shared across all our
// alts on that realm), so it is stored with the observation rather than in
// the per-character customization layer — and
// there's no wire source for the account→character link, so the user
// authors it via the edit dialog. PlayerDatabase merges both layers for
// display.
//
// GivenName is the first word of the in-game name (the "Forged" in "Forged
// Paradigm"), may be empty for legacy records; FamilyName is the remainder
// after the first space, empty for single-word names. Class and Race come from
// a look at the player (LookParser) or what their client states (who doesn't
// carry them). Alignment is the most recent seen on who ("Neutral" when the
// column was blank). Title is the most recent seen on who; class + level
// range can be inferred from it via GameData.ClassTitleTable. Gang is
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
// a title only pins a 5-level band. LevelAt stamps WHEN that exact level was
// learned: the party level-gate check treats an exact level older than the
// staleness horizon as due for a fresh @level re-probe (a member could have
// levelled since), so a long-lived exact reading doesn't silently drift out
// of date. null whenever Level is null (no reading to age). Version is the
// client + version string from an @version probe reply (VersionAt stamps when),
// and LastPartiedUtc is when we last started partying with this player — both
// learned by the party stats probe.
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
    // When the exact Level was recorded (UTC), for the party level-gate
    // staleness re-probe. null whenever Level is null.
    DateTime? LevelAt = null,
    // Authored realm-tier override: the player's BBS account name, when it
    // differs from their in-game given name. Some boards key their presence
    // lines (logon / logoff) on the account name instead of the character
    // name; the disconnect-watcher matches a captured name against this
    // first, falling back to the given name when it's null. Both account and
    // in-game names are unique, so the mapping is 1:1. null = no override
    // (account name equals the in-game name, the common case).
    string? AccountName = null,
    // The player's client + version string from an @version probe reply — e.g.
    // "MudPlay 2.37.0", "MegaMud 1.03u" — recorded verbatim from whatever their
    // client answered. null until they answer one. VersionAt stamps when it was
    // learned (null whenever Version is null).
    string? Version = null,
    DateTime? VersionAt = null,
    // When we last STARTED partying with this player (UTC), driving the
    // once-per-local-day "first party of the day" stats probe (@level + @version).
    // null if we've never partied with them since this record was created.
    DateTime? LastPartiedUtc = null,
    // When PlayerLookManager last auto-sent `look <name>` at this player
    // (UTC), driving the once-per-local-day rule on BOTH its toggles. null if
    // never. Stamped on SEND rather than on a parsed reply, so a look whose
    // response we could not read (a shadowy figure, a truncated block) still
    // counts -- otherwise the throttle would never engage for exactly the
    // players it can learn nothing from.
    DateTime? LastLookedUtc = null,
    // Authored, and stored here rather than with the per-character settings: how we
    // stand with a player is a fact about the realm, shared by every character we
    // play on it. Relationship defaults to Neutral for everyone. PvpResponse is this
    // player's own answer in place of the PvP settings' general one; null uses the
    // general one.
    PlayerRelationship Relationship = PlayerRelationship.Neutral,
    PvpAction? PvpResponse = null)
{
    // Combined display name — "GivenName FamilyName", trimmed: the key players
    // are looked up and customised by (case-insensitive).
    public string DisplayName =>
        string.IsNullOrEmpty(FamilyName) ? GivenName : $"{GivenName} {FamilyName}";

    // A room-listing name with no record that reads as a player's: capitalised, one
    // or two words of letters, where monsters are listed in lower case. A client
    // heuristic, not engine data.
    public static bool LooksLikePlayerName(string rawName)
    {
        string[] words = rawName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 1 or > 2 || !char.IsUpper(words[0][0])) return false;
        foreach (string word in words)
            foreach (char c in word)
                if (!char.IsLetter(c) && c != '\'' && c != '-') return false;
        return true;
    }

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
//
// DupeUsedAtUtc / DupedPlayer are the once-only lock on this player's @dupe: set
// by the @dupe handler when they spend it (and who they duplicated onto), cleared
// only when the user resets it in the edit dialog. null = @dupe still available.
public readonly record struct PlayerCustomization(
    PlayerRemoteControls RemoteControls = PlayerRemoteControls.None,
    bool InviteToPartyIfSeen = false,
    bool JoinPartyIfInvited = false,
    bool DontAutoDelete = false,
    string? Notes = null,
    DateTime? DupeUsedAtUtc = null,
    string? DupedPlayer = null)
{
    // True when every field holds the default value. Drives the "don't persist" rule,
    // so the @dupe lock must count: a player whose only non-default value is a spent
    // @dupe would otherwise be pruned and silently get their @dupe back.
    public bool IsDefault
        => RemoteControls == PlayerRemoteControls.None
        && !InviteToPartyIfSeen
        && !JoinPartyIfInvited
        && !DontAutoDelete
        && string.IsNullOrEmpty(Notes)
        && DupeUsedAtUtc is null;

    // The @dupe lock belongs to the engine, not to the editors. An editor works from
    // a snapshot taken when its dialog opened, so writing its copy back would undo a
    // @dupe that landed in the meantime without the user ever asking. Take the lock
    // from the live customization instead; only an explicit reset clears it.
    public PlayerCustomization KeepDupeLockFrom(PlayerCustomization live, bool reset) => this with
    {
        DupeUsedAtUtc = reset ? null : live.DupeUsedAtUtc,
        DupedPlayer   = reset ? null : live.DupedPlayer,
    };
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
    // When the exact Level was recorded (UTC) — see PlayerObservation.LevelAt.
    DateTime? LevelAt = null,
    // Mirrors PlayerObservation.AccountName (realm tier) into the merged row so
    // the edit dialog can show + author it. null = account name equals the
    // in-game name.
    string? AccountName = null,
    // Mirror of PlayerObservation.Version / VersionAt / LastPartiedUtc (realm tier)
    // so the edit dialog + Players tab can display them.
    string? Version = null,
    DateTime? VersionAt = null,
    DateTime? LastPartiedUtc = null,
    // Mirror of PlayerCustomization.DupeUsedAtUtc / DupedPlayer (Character tier) so
    // the edit dialog can show the @dupe lock and offer the reset.
    DateTime? DupeUsedAtUtc = null,
    string? DupedPlayer = null,
    // Mirror of PlayerObservation.Relationship / PvpResponse (realm tier).
    PlayerRelationship Relationship = PlayerRelationship.Neutral,
    PvpAction? PvpResponse = null)
{
    // Combined display name — "GivenName FamilyName", trimmed. Identical
    // contract to PlayerObservation.DisplayName so callers don't have to
    // know which type they're holding.
    public string DisplayName =>
        string.IsNullOrEmpty(FamilyName) ? GivenName : $"{GivenName} {FamilyName}";

    public static (string Given, string Family) SplitName(string? name)
        => PlayerObservation.SplitName(name);

    // Combine a realm-tier observation with the loaded character's
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
        LevelAt:             obs.LevelAt,
        AccountName:         obs.AccountName,
        Version:             obs.Version,
        VersionAt:           obs.VersionAt,
        LastPartiedUtc:      obs.LastPartiedUtc,
        DupeUsedAtUtc:       cust.DupeUsedAtUtc,
        DupedPlayer:         cust.DupedPlayer,
        Relationship:        obs.Relationship,
        PvpResponse:         obs.PvpResponse);

    // The customization slice of this merged row, for writing back to the
    // Character layer.
    public PlayerCustomization ToCustomization() => new(
        RemoteControls:      RemoteControls,
        InviteToPartyIfSeen: InviteToPartyIfSeen,
        JoinPartyIfInvited:  JoinPartyIfInvited,
        DontAutoDelete:      DontAutoDelete,
        Notes:               Notes,
        DupeUsedAtUtc:       DupeUsedAtUtc,
        DupedPlayer:         DupedPlayer);
}

// Per-player allowed remote-command categories: MegaMUD's "Allowed Remote
// Control" panel's 12 grouped categories, plus four MudPlay query categories
// (@timer, @death, @roomba, @quest). RemoteCommandManager consults this bitmask
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

    // Boss respawn timers being tracked — @timer.
    QueryBossTimers     = 1 << 12,

    // Unrecovered deaths in the recovery log — @death.
    QueryDeaths         = 1 << 13,

    // Item last-seen-in-room queries against the Roomba item-sighting log —
    // @roomba.
    QueryItemLocation   = 1 << 14,

    // Quest-progress queries — @quest. Reports the character's marked-complete
    // quest state, and (on Paradigm, or stock with sys-god access) the live quest
    // flag step read off the game via `abil` / `sys god … abil`. Its own category,
    // like the other MudPlay-specific query extensions, so it can be granted apart
    // from the rest.
    QueryQuests         = 1 << 15,

    // Spell Book queries — @spells: the learned spells, the ones learnable at the
    // character's level, or whether one spell is learned. Its own category, like the
    // other MudPlay-specific query extensions.
    QuerySpells         = 1 << 16,

    // Convenience — every category above flipped on.
    All = QueryVersion | QueryExperience | QueryHealthStatus | QueryLocation
        | QueryInventory | RequestInvite | MovePlayer | ExecuteCommands
        | HangupDisconnect | AlterSettings | DivertConversations | SysopCommands
        | QueryBossTimers | QueryDeaths | QueryItemLocation | QueryQuests | QuerySpells,
}

// One equipment slot's contents from a look <player> response. SlotLabel
// is the literal label printed by the server (e.g. "Torso", "Weapon Hand",
// "Two Handed"); we don't normalise — different realms print 2H weapons
// with either "Weapon Hand" or "Two Handed" and consumers can treat both
// equivalently when needed. Wrist / Finger / Worn can repeat across
// multiple items. ItemName is the item display name as printed.
public readonly record struct EquipmentItem(string SlotLabel, string ItemName);
