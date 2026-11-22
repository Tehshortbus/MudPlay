namespace MudPlay.Models.GameData;

// Who shares the remote-command permissions in the Players table. Chosen once per
// BBS. Realm and Bbs replace each character's own list rather than adding to it, so
// a permission taken away is gone for every character sharing it.
public enum PlayerPermissionScope { Character, Realm, Bbs }
