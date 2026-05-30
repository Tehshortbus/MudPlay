namespace FujinTerm.Models.GameData;

/// <summary>
/// One observed-or-edited player. Stored at the BBS tier by default —
/// the same display name on a different BBS represents a different
/// person, so per-BBS storage is the natural fit. Per-character
/// promotion of a record (for personalised permission overrides) and
/// global promotion (for cross-BBS friend lists) use the standard
/// 4-tier resolver.
/// </summary>
/// <remarks>
/// Observation writes (from <c>who</c> output) refresh
/// <see cref="GivenName"/> / <see cref="FamilyName"/> / <see cref="Class"/>
/// / <see cref="Race"/> / <see cref="Alignment"/> / <see cref="Title"/>
/// / <see cref="LastSeenUtc"/>. User-edited fields
/// (<see cref="Notes"/>, <see cref="RemoteControls"/>,
/// <see cref="InviteToPartyIfSeen"/>, <see cref="JoinPartyIfInvited"/>,
/// <see cref="DontAutoDelete"/>) are never overwritten by observation —
/// <see cref="Services.PlayerDatabase.RecordObservation"/> enforces this.
/// </remarks>
/// <param name="GivenName">First word of the in-game name (the "Forged" in "Forged Paradigm"). May be empty for legacy records.</param>
/// <param name="FamilyName">Remainder of the in-game name after the first space. Empty when the player has a single-word name.</param>
/// <param name="Class">Most recent class seen — from <c>@health</c> / <c>@stat</c> remotes (the <c>who</c> table doesn't carry it).</param>
/// <param name="Race">Most recent race seen — same source as <see cref="Class"/>.</param>
/// <param name="Alignment">Most recent alignment seen on <c>who</c>. <c>"Neutral"</c> when the alignment column was blank.</param>
/// <param name="Title">Most recent title seen on <c>who</c>. Class + level range can be inferred from the title via the future class-titles table.</param>
/// <param name="Gang">Most recent gang/guild name (<c>"of …"</c> suffix on <c>who</c>). Empty when the player is ungang'd.</param>
/// <param name="Role">MegaMUD-style trailing marker — <c>M</c> mudop, <c>S</c> sysop, <c>V</c> visitor, <c>null</c> for regular players.</param>
/// <param name="FirstSeenUtc">When this record was first created.</param>
/// <param name="LastSeenUtc">When this record was last refreshed by a <c>who</c> observation.</param>
/// <param name="Notes">Free-form note the user can attach via the edit dialog.</param>
/// <param name="RemoteControls">Bitmask of @-command categories the user has explicitly allowed.</param>
/// <param name="InviteToPartyIfSeen">Auto-invite this player when our character spots them in the room.</param>
/// <param name="JoinPartyIfInvited">Auto-accept party invites from this player.</param>
/// <param name="DontAutoDelete">Skip this record during stale-record cleanup.</param>
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
    bool DontAutoDelete = false)
{
    /// <summary>
    /// Combined display name — <c>"GivenName FamilyName"</c>, trimmed.
    /// Used by the database's case-insensitive lookup and by the Players
    /// tab for searches that ignore the first/last split.
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
/// Per-player allowed remote-command categories. Matches MegaMUD's
/// "Allowed Remote Control" panel layout — 12 grouped categories that
/// span the individual <c>@-command</c> set documented at
/// <see href="https://kyau.net/wiki/MajorMUD:Remote_Commands"/>. The
/// Phase 13 <c>RemoteCommandManager</c> consults this bitmask before
/// dispatching any @-command from a non-party player.
/// </summary>
/// <remarks>
/// Empty (<see cref="None"/>) means "deny every category" — the default
/// for newly-observed players. <see cref="All"/> grants the full set in
/// one flag. Hard-blocks for destructive commands (<c>@do reroll</c> /
/// <c>@do suicide</c> when lives ≤ 3, <c>@party reroll</c> / <c>@party suicide</c>
/// always) bypass this bitmask entirely — those are policy, not
/// per-player choices.
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
