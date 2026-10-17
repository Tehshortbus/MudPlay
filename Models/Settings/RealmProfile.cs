namespace MudPlay.Models.Settings;

// One realm on a BBS. A board can host several versions of the game, picked from
// its menu, and each usually differs — often with its own MDB export — so what a
// realm needs (its game data, menu commands, death floor, hang-up penalty, cleanup
// time, currency name) lives here, and everything the client collects while playing it (players
// seen, blacklist, leaderboard, roomba, quests, realm-tier game-data edits, boss
// timers) is stored in its folder (AppPaths.RealmFolder). Character profiles are
// assigned to a realm by name (CharacterProfile.Realm); characters on the same
// realm share its data.
public sealed class RealmProfile
{
    // The user's name for the realm, unique on its BBS; also the folder name.
    public string Name { get; set; } = string.Empty;

    // Imported game-data set folder this realm uses. null falls back to
    // GlobalSettings.DefaultGameDataSet. Also writable from File → Game Data →
    // Active set.
    public string? ActiveGameDataSet { get; set; }

    // ----- Game-menu commands -----
    // The commands the client sends at the MajorMUD main menu to enter / leave
    // the game. Defaults are the standard MajorMUD picks.

    // Sent at the main menu to enter the game. Consumed by MainMenuEntryAutomation
    // when the client detects the main menu after a (re)connect.
    public string GameEntryCommand { get; set; } = "E";

    // Sent at the main menu to log off. Fired by HangupHandler on a permitted
    // @hangup and by the cleanup-warning logout flow.
    public string GameExitCommand { get; set; } = "=x";

    // ----- Realm mechanics -----

    // The negative-HP floor at which a character actually dies. Hitting 0 HP only
    // *drops* you (bleeding out — can't move/fight/cast, but revivable and still
    // able to hang up); death happens when HP falls to this value. A realm balance
    // knob, not a per-character stat. Seeded at the standard -25; clamp consumers
    // treat any positive value as 0. The emergency auto-hangup reads this so it
    // keeps firing through the whole bleeding-out window.
    public int PlayerDiesAtHp { get; set; } = -25;

    // Let the client trace the realm's true death floor from observed *slow*
    // deaths and refine PlayerDiesAtHp toward it. The seed (-25) is only a guess; a
    // bleed-out crosses the floor one tick at a time and lands right at it, so its
    // HP reading is an accurate measurement. Off pins the user's manual value.
    public bool AutoRefineDeathFloor { get; set; } = true;

    // Players can attack one another on this realm. Nothing in the game's output
    // says so, so the user tells us; the PvP settings and the room-attack care
    // around other players only apply where this is on.
    public bool PvpEnabled { get; set; }

    // ----- Hang-up penalty -----
    // What this board does to a character who hangs up in a fight (GAME_MECHANICS
    // "Hang-up / lost carrier"). The board sets it and nothing the game prints
    // says what it is, so the user records it here. The program log and the bug
    // report show it (Game.Health.HangupPenaltyNotice), and where an item count is
    // set the client looks for what was dropped on the way back into the game
    // (Game.Inventory.HangupItemRecheck). No hang-up is sent, held or skipped
    // because of it.
    //
    // Persisted with the realm in the BBS file (BBS/{bbs}/bbs.json → Realms), the
    // tier the death floor is on: a board's PvE and PvP realms are separate game
    // instances, each with its own rule.

    // The board penalises hang-ups. Off leaves everything below unused.
    public bool HangupPenaltyEnabled { get; set; }

    // A hang-up in PvP combat takes a share of max HP between these two
    // percentages and drops up to this many items. The HP pair starts at the
    // game's own initial values; the item count at 0, since most boards that
    // penalise take HP only.
    public int HangupPvpHpFromPercent { get; set; } = 25;
    public int HangupPvpHpToPercent { get; set; } = 50;
    public int HangupPvpItemsDropped { get; set; }

    // The board penalises a hang-up in a fight with a monster too, with its own
    // HP share and item count.
    public bool HangupPvePenaltyEnabled { get; set; }
    public int HangupPveHpFromPercent { get; set; } = 25;
    public int HangupPveHpToPercent { get; set; } = 50;
    public int HangupPveItemsDropped { get; set; }

    // The board penalises every hang-up made in the game, in a fight or not, at the
    // monster side's figures. Off, a hang-up outside a fight is taken as free.
    // Game.Inventory.HangupItemRecheck acts on it, deciding whether a death by the
    // penalty is worth looking for after such a hang-up (a hold on movement, and a
    // `stat` if none was read); HangupPenaltyNotice words it into the program log's
    // hang-up line and the bug report.
    public bool HangupOutsideFightPenaltyEnabled { get; set; }

    // How many deaths seen on this board ended in a room the standard Stock list
    // doesn't allow for the room died in, while the board was running on the
    // unmodified Stock game data. Two say the board's rooms were edited: from
    // then on the list is taken there as it is on any board with edited rooms
    // (Game.Recovery.HangupArrivalListTrust.Unverified), saying where the dead
    // arrive and never that a room is the wrong one. Written and read by
    // Game.Recovery.StandardListContradictions; no setting shows it, and a bug
    // report does.
    public int DeathsContradictingStandardArrivalRooms { get; set; }

    // The game-data set those deaths were seen on. The count is evidence about
    // this board against that set, so it counts for nothing while the realm plays
    // another: pointing the realm at another set starts it afresh.
    public string? DeathsContradictingStandardArrivalRoomsOn { get; set; }

    // The realm's nightly cleanup wall-clock time. Some bosses ("Respawns @
    // Cleanup" in the boss table) reset only at this daily cleanup — a marked one
    // reads DEAD until the next cleanup, then ALIVE. Format "HH:mm" in
    // CleanupTimeZoneId's zone. Blank disables cleanup-boss timers.
    public string CleanupTimeOfDay { get; set; } = "21:00";

    // Time-zone id CleanupTimeOfDay is expressed in. Defaults to the computer's own
    // zone; invalid / empty falls back to the local zone.
    public string CleanupTimeZoneId { get; set; } = TimeZoneInfo.Local.Id;

    // Name for the top (runic) denomination. Some realms relabel "runic" to a
    // realm-specific word — which changes the coin wording the server sends AND the
    // bare keyword the client keys currency commands on. Blank/whitespace falls
    // back to "runic". Consumed via CurrencyNaming.
    public string RunicCurrencyName { get; set; } = "runic";

}
