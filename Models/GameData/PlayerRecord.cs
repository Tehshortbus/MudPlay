namespace FujinTerm.Models.GameData;

/// <summary>
/// Observation-only fields for one player — what the server told us via
/// <c>who</c> / <c>look</c>. Lives at the <b>BBS tier</b>: every player
/// observed on a given BBS is stored under
/// <c>Data/BBS/{bbs-name}/players.json</c>. Same display name on a
/// different BBS represents a different person, so the per-BBS scope
/// matches the social reality.
/// </summary>
/// <remarks>
/// Mutations to this record only come from the server-output parsers
/// (<see cref="Services.WhoListParser"/> and the planned look-on-player
/// parser). User-authored fields live separately on
/// <see cref="PlayerCustomization"/> at the <b>Character tier</b>; the
/// edit dialog never touches an observation. <see cref="Services.PlayerDatabase"/>
/// merges both layers for display.
/// </remarks>
/// <param name="GivenName">First word of the in-game name (the "Forged" in "Forged Paradigm"). May be empty for legacy records.</param>
/// <param name="FamilyName">Remainder of the in-game name after the first space. Empty when the player has a single-word name.</param>
/// <param name="Class">Most recent class seen — from a future <c>look</c> / <c>@health</c> parser. <c>who</c> doesn't carry it.</param>
/// <param name="Race">Most recent race seen — same source as <see cref="Class"/>.</param>
/// <param name="Alignment">Most recent alignment seen on <c>who</c>. <c>"Neutral"</c> when the alignment column was blank.</param>
/// <param name="Title">Most recent title seen on <c>who</c>. Class + level range can be inferred from the title via the future class-titles table.</param>
/// <param name="Gang">Most recent gang/guild name (<c>"of …"</c> suffix on <c>who</c>). Empty when the player is ungang'd.</param>
/// <param name="Role">MegaMUD-style trailing marker — <c>M</c> mudop, <c>S</c> sysop, <c>V</c> visitor, <c>null</c> for regular players.</param>
/// <param name="FirstSeenUtc">When this record was first created.</param>
/// <param name="LastSeenUtc">When this record was last refreshed by a <c>who</c> observation.</param>
/// <param name="Equipment">Most recent equipment loadout seen on <c>look &lt;player&gt;</c>. Empty list = explicit "Nothing"; <c>null</c> = never looked at.</param>
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
    IReadOnlyList<EquipmentItem>? Equipment = null)
{
    /// <summary>
    /// Combined display name — <c>"GivenName FamilyName"</c>, trimmed.
    /// Used by the database's case-insensitive lookup and by the
    /// customization dictionary as the key.
    /// </summary>
    public string DisplayName =>
        string.IsNullOrEmpty(FamilyName) ? GivenName : $"{GivenName} {FamilyName}";

    /// <summary>
    /// Split a wire-format name (e.g. <c>"Forged Paradigm"</c>) into
    /// given + family on the first whitespace. Multi-space names treat
    /// everything after the first space as the family name; single-word
    /// names get an empty family. <c>null</c> / empty input returns empty
    /// strings so observation writes don't fail on garbage.
    /// </summary>
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

/// <summary>
/// User-authored per-player settings for the loaded character.
/// Lives at the <b>Character tier</b> on
/// <see cref="Profile.CharacterProfile.PlayerCustomizations"/>; only
/// entries that hold a non-default value are persisted so a fresh
/// profile doesn't get bloated with one entry per observed stranger
/// (see <see cref="IsDefault"/>).
/// </summary>
/// <param name="RemoteControls">Bitmask of <c>@-command</c> categories the user allows from this player.</param>
/// <param name="InviteToPartyIfSeen">Auto-invite this player when our character spots them in the room.</param>
/// <param name="JoinPartyIfInvited">Auto-accept party invites from this player.</param>
/// <param name="DontAutoDelete">Skip this record during stale-record cleanup.</param>
/// <param name="Notes">Free-form note the user attached via the edit dialog.</param>
public readonly record struct PlayerCustomization(
    PlayerRemoteControls RemoteControls = PlayerRemoteControls.None,
    bool InviteToPartyIfSeen = false,
    bool JoinPartyIfInvited = false,
    bool DontAutoDelete = false,
    string? Notes = null)
{
    /// <summary>True when every field holds the default value. Drives the "don't persist" rule.</summary>
    public bool IsDefault
        => RemoteControls == PlayerRemoteControls.None
        && !InviteToPartyIfSeen
        && !JoinPartyIfInvited
        && !DontAutoDelete
        && string.IsNullOrEmpty(Notes);
}

/// <summary>
/// Merged display view — the observation fields + the customization
/// fields for one player. Built by <see cref="Services.PlayerDatabase"/>
/// for the UI; not persisted directly.
/// </summary>
/// <remarks>
/// The split is invisible to the table view + edit dialog (they keep
/// reading one record), but writes have to go to the right layer:
/// observation writes call <see cref="Services.PlayerDatabase.RecordObservation"/>;
/// customization writes (from the edit dialog Save path) call
/// <see cref="Services.PlayerDatabase.EditCustomization"/>.
/// </remarks>
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
    IReadOnlyList<EquipmentItem>? Equipment = null)
{
    /// <summary>
    /// Combined display name — <c>"GivenName FamilyName"</c>, trimmed.
    /// Identical contract to <see cref="PlayerObservation.DisplayName"/>
    /// so callers don't have to know which type they're holding.
    /// </summary>
    public string DisplayName =>
        string.IsNullOrEmpty(FamilyName) ? GivenName : $"{GivenName} {FamilyName}";

    /// <inheritdoc cref="PlayerObservation.SplitName"/>
    public static (string Given, string Family) SplitName(string? name)
        => PlayerObservation.SplitName(name);

    /// <summary>Combine a BBS-tier observation with the loaded character's customization (if any) into a single display row.</summary>
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
        Equipment:           obs.Equipment);

    /// <summary>Pull just the customization slice off this merged row (used by the edit dialog Save path).</summary>
    public PlayerCustomization ToCustomization() => new(
        RemoteControls:      RemoteControls,
        InviteToPartyIfSeen: InviteToPartyIfSeen,
        JoinPartyIfInvited:  JoinPartyIfInvited,
        DontAutoDelete:      DontAutoDelete,
        Notes:               Notes);
}

/// <summary>
/// Per-player allowed remote-command categories. Matches MegaMUD's
/// "Allowed Remote Control" panel layout — 12 grouped categories that
/// span the individual <c>@-command</c> set documented at
/// <see href="https://kyau.net/wiki/MajorMUD:Remote_Commands"/>. The
/// <see cref="Game.Remote.RemoteCommandManager"/> (Phase 6 PR 6.2)
/// consults this bitmask before dispatching any @-command from a
/// non-party player.
/// </summary>
/// <remarks>
/// Empty (<see cref="None"/>) means "deny every category" — the default
/// for newly-observed players. <see cref="All"/> grants the full set in
/// one flag. Hard-blocks for destructive commands (<c>@do reroll</c> /
/// <c>@do suicide</c> when lives ≤ threshold, <c>@party reroll</c> /
/// <c>@party suicide</c> always) bypass this bitmask entirely — those
/// are engine policy, not per-player choices.
/// </remarks>
[Flags]
public enum PlayerRemoteControls
{
    None                = 0,

    /// <summary>Identification — <c>@version</c>.</summary>
    QueryVersion        = 1 << 0,

    /// <summary>Experience snapshot — <c>@exp</c>.</summary>
    QueryExperience     = 1 << 1,

    /// <summary>Vital signs + status — <c>@health</c>, <c>@par</c>.</summary>
    QueryHealthStatus   = 1 << 2,

    /// <summary>Where am I — <c>@where</c>.</summary>
    QueryLocation       = 1 << 3,

    /// <summary>Inventory snapshot — <c>@have</c>, <c>@wealth</c>, <c>@enc</c>.</summary>
    QueryInventory      = 1 << 4,

    /// <summary>Solicit a party invite — <c>@invite</c>.</summary>
    RequestInvite       = 1 << 5,

    /// <summary>Direct movement — <c>@goto</c>, <c>@follow</c>.</summary>
    MovePlayer          = 1 << 6,

    /// <summary><c>@do &lt;command&gt;</c> passthrough — highest trust.</summary>
    ExecuteCommands     = 1 << 7,

    /// <summary>Force-disconnect — <c>@hangup</c>.</summary>
    HangupDisconnect    = 1 << 8,

    /// <summary>Toggle auto-modes / engine settings — <c>@auto-*</c>.</summary>
    AlterSettings       = 1 << 9,

    /// <summary>Re-route incoming chat — <c>@divert</c>.</summary>
    DivertConversations = 1 << 10,

    /// <summary>Admin / wizard commands (sysop-only on most realms).</summary>
    SysopCommands       = 1 << 11,

    /// <summary>Convenience — every category above flipped on.</summary>
    All = QueryVersion | QueryExperience | QueryHealthStatus | QueryLocation
        | QueryInventory | RequestInvite | MovePlayer | ExecuteCommands
        | HangupDisconnect | AlterSettings | DivertConversations | SysopCommands,
}

/// <summary>
/// One equipment slot's contents from a <c>look &lt;player&gt;</c>
/// response. <see cref="SlotLabel"/> is the literal label printed by
/// the server (e.g. <c>"Torso"</c>, <c>"Weapon Hand"</c>,
/// <c>"Two Handed"</c>); we don't normalise — different realms print
/// 2H weapons with either <c>"Weapon Hand"</c> or <c>"Two Handed"</c>
/// and consumers can treat both equivalently when needed.
/// </summary>
/// <param name="SlotLabel">As printed by the server. Wrist / Finger / Worn can repeat across multiple items.</param>
/// <param name="ItemName">Item display name as printed.</param>
public readonly record struct EquipmentItem(string SlotLabel, string ItemName);
