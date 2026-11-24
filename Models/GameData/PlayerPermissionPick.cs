namespace MudPlay.Models.GameData;

// A character's choice of its own remote-settings list. Bbs ties the number to the
// BBS that issued it, so a profile moved to another BBS doesn't carry a number that
// means nothing there.
public readonly record struct PlayerPermissionPick(Guid Bbs, long Number);
