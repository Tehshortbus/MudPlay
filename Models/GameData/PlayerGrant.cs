namespace MudPlay.Models.GameData;

// The part of a PlayerCustomization that follows the permission scope. The @dupe
// lock travels with the permissions: left per character, a player holding a shared
// Elevated Commands could spend one @dupe on each of our characters. So does
// Don't-delete: a player kept for their shared permissions must not be purged by
// a character that never set the flag itself.
public readonly record struct PlayerGrant(
    PlayerRemoteControls RemoteControls = PlayerRemoteControls.None,
    DateTime? DupeUsedAtUtc = null,
    string? DupedPlayer = null,
    bool DontAutoDelete = false)
{
    public bool IsEmpty => RemoteControls == PlayerRemoteControls.None && DupeUsedAtUtc is null && !DontAutoDelete;

    public static PlayerGrant Of(PlayerCustomization c) =>
        new(c.RemoteControls, c.DupeUsedAtUtc, c.DupedPlayer, c.DontAutoDelete);

    public PlayerCustomization AppliedTo(PlayerCustomization c) => c with
    {
        RemoteControls = RemoteControls,
        DupeUsedAtUtc  = DupeUsedAtUtc,
        DupedPlayer    = DupedPlayer,
        DontAutoDelete = DontAutoDelete,
    };
}
