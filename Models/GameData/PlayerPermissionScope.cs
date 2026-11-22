namespace MudPlay.Models.GameData;

// Who shares a player's remote settings in the Players table: the permissions, the
// party toggles and Don't-delete. Chosen once per BBS. Realm and Bbs replace each
// character's own list rather than adding to it, so a permission taken away is gone
// for every character sharing it.
public enum PlayerPermissionScope { Character, Realm, Bbs }
