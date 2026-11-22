namespace MudPlay.Models.GameData;

// The part of a PlayerCustomization that follows the permission scope. The @dupe
// lock travels with the permissions: left per character, a player holding a shared
// Elevated Commands could spend one @dupe on each of our characters.
public readonly record struct PlayerGrant(
    PlayerRemoteControls RemoteControls = PlayerRemoteControls.None,
    DateTime? DupeUsedAtUtc = null,
    string? DupedPlayer = null)
{
    public bool IsEmpty => RemoteControls == PlayerRemoteControls.None && DupeUsedAtUtc is null;

    public static PlayerGrant Of(PlayerCustomization c) => new(c.RemoteControls, c.DupeUsedAtUtc, c.DupedPlayer);

    public PlayerCustomization AppliedTo(PlayerCustomization c) => c with
    {
        RemoteControls = RemoteControls,
        DupeUsedAtUtc  = DupeUsedAtUtc,
        DupedPlayer    = DupedPlayer,
    };
}
