namespace MudPlay.Models.GameData;

// The part of a PlayerCustomization that follows the permission scope. The @dupe
// lock travels with the permissions: left per character, a player holding a shared
// Elevated Commands could spend one @dupe on each of our characters. So do
// Don't-delete, so a character that never set it can't purge a player kept for the
// others, and the two party toggles. Only the free-form notes stay per character.
public readonly record struct PlayerGrant(
    PlayerRemoteControls RemoteControls = PlayerRemoteControls.None,
    DateTime? DupeUsedAtUtc = null,
    string? DupedPlayer = null,
    bool DontAutoDelete = false,
    bool InviteToPartyIfSeen = false,
    bool JoinPartyIfInvited = false)
{
    public bool IsEmpty => RemoteControls == PlayerRemoteControls.None && DupeUsedAtUtc is null
        && !DontAutoDelete && !InviteToPartyIfSeen && !JoinPartyIfInvited;

    public static PlayerGrant Of(PlayerCustomization c) => new(
        c.RemoteControls, c.DupeUsedAtUtc, c.DupedPlayer, c.DontAutoDelete, c.InviteToPartyIfSeen, c.JoinPartyIfInvited);

    public PlayerCustomization AppliedTo(PlayerCustomization c) => c with
    {
        RemoteControls = RemoteControls,
        DupeUsedAtUtc  = DupeUsedAtUtc,
        DupedPlayer    = DupedPlayer,
        DontAutoDelete = DontAutoDelete,
        InviteToPartyIfSeen = InviteToPartyIfSeen,
        JoinPartyIfInvited  = JoinPartyIfInvited,
    };
}
