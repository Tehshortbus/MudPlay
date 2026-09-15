using MudPlay.Models.GameData;

namespace MudPlay.Game.Remote;

// Single source of truth mapping every documented MajorMUD / MegaMUD @-command
// to the PlayerRemoteControls category that gates it. Sourced from the
// bearfather wiki canonical reference:
// https://kyau.net/wiki/MajorMUD:Remote_Commands
//
// Whichever handler wires a command, it looks the category up here via
// TryGetCategory rather than hardcoding — keeping the mapping in one place means
// "MudPlay grants Raijin QueryHealthStatus" produces consistent behaviour across
// every @health-class command without per-handler ceremony.
//
// Categories follow the 12-checkbox grid in the Game Data Browser → Players edit
// dialog:
//   - QueryVersion — version / fingerprint introspection + @help.
//   - QueryExperience — exp / level numbers.
//   - QueryHealthStatus — health / mana / state flags / lives.
//   - QueryLocation — @where / @path / @who (room, route progress, who's in the
//     room).
//   - QueryInventory — items / cash / encumbrance / have-checks.
//   - RequestInvite — party invite / join / leave signals.
//   - MovePlayer — goto / loop / lair / stop / rego.
//   - ExecuteCommands — @do passthrough, bulk inventory actions (@get-all /
//     @drop-all / @deposit-all), and gear-set apply (@equip-<set>).
//   - HangupDisconnect — @hangup / @relog.
//   - AlterSettings — auto-* toggles, @settings. Note: @reset also sits in this
//     category in the catalog, but its actual home is the session-stats tracker
//     (it zeroes the live tracking metrics — exp earned per hour, combat round
//     observations, etc.); the AlterSettings categorisation is just
//     permission-grouping with the other "alter something on my behalf" verbs.
//   - DivertConversations — @divert.
//   - SysopCommands ("Elevated Commands" in the Players-tab UI) — high-trust
//     commands beyond ordinary control: irreversible character actions
//     (@suicide). Wider than just sysop powers.
//   - QueryBossTimers — @timer (boss respawn timers being tracked). Its own
//     category so a user can grant boss-timer queries independently of @where.
//   - QueryItemLocation — @roomba (last room an item was seen in, per the
//     BBS-tier Roomba item-sighting log). Its own category, same as
//     QueryBossTimers/QueryDeaths, since none of these are documented MajorMUD
//     wiki commands — they're MudPlay-specific extensions.
//
// Party-coordination commands (@wait / @ok / @comeback / @forget / @share) map
// to PlayerRemoteControls.None — they're gated by the engine's party-whitelist
// branch instead of the per-player flag check. Any active party member can issue
// them by default. @comeback and @forget additionally honour a bridge
// (RemoteCommandManager.ComebackEligibility / ForgetEligibility) so a member
// already dropped server-side — no longer an IsActivePartyMember — can still
// reconnect-recover or tear down. Settings.Talk → Disallow @party commands narrows ONLY the
// @party <sub> directive path (attack / rest / meditate / go / …); it does not
// touch these coordination signals. @kill is NOT in this family — it's an action
// request ("attack this target on my behalf") and sits at
// PlayerRemoteControls.ExecuteCommands alongside @do / @heal.
//
// The ailment / status broadcast tokens (@poisoned / @blind / @confused /
// @diseased / @held) are deliberately NOT in this catalog. They're not
// permission-gated remote commands — they're say-channel state announcements
// emitted by Conditions.AilmentSyncEngine and observed by
// Conditions.PartyAilmentTracker to mirror a member's condition on the party
// window. Their suppression lives in the cure / ailment settings, not the
// per-player remote-control grid.
//
// @heal is the exception in that family: it's
// PlayerRemoteControls.ExecuteCommands rather than party-whitelist. The semantic
// is "do something on my behalf" (cast a heal on the sender) rather than a
// coordination signal, and a sender may legitimately need it even when the
// receiver's auto-heal thresholds don't naturally pick them up (settings
// mismatch between healer and target). Requires the receiver to have granted the
// sender ExecuteCommands explicitly.
public static class RemoteCommandCatalog
{
    // Canonical command → required category map. Keyed case-insensitively.
    // Sentinel PlayerRemoteControls.None means "party-whitelist gated"; consult
    // the catalog comment above for the full list of party-whitelist commands.
    public static readonly IReadOnlyDictionary<string, PlayerRemoteControls> Map =
        new Dictionary<string, PlayerRemoteControls>(StringComparer.OrdinalIgnoreCase)
        {
            // ===== Basic Commands =====
            ["@version"]      = PlayerRemoteControls.QueryVersion,
            ["@health"]       = PlayerRemoteControls.QueryHealthStatus,
            ["@exp"]          = PlayerRemoteControls.QueryExperience,
            ["@level"]        = PlayerRemoteControls.QueryExperience,
            ["@status"]       = PlayerRemoteControls.QueryHealthStatus,
            ["@lives"]        = PlayerRemoteControls.QueryHealthStatus,
            ["@where"]        = PlayerRemoteControls.QueryLocation,
            ["@path"]         = PlayerRemoteControls.QueryLocation,
            ["@who"]          = PlayerRemoteControls.QueryLocation,
            ["@timer"]        = PlayerRemoteControls.QueryBossTimers,   // own permission — grant boss-timer queries separately
            ["@death"]        = PlayerRemoteControls.QueryDeaths,       // own permission — grant unrecovered-death queries separately
            ["@roomba"]       = PlayerRemoteControls.QueryItemLocation, // own permission — grant item-location queries separately
            ["@help"]         = PlayerRemoteControls.QueryVersion,
            ["@what"]         = PlayerRemoteControls.QueryInventory,
            ["@wealth"]       = PlayerRemoteControls.QueryInventory,
            ["@enc"]          = PlayerRemoteControls.QueryInventory,
            ["@have"]         = PlayerRemoteControls.QueryInventory,
            ["@inv"]          = PlayerRemoteControls.QueryInventory,   // carried pack + keys — what a look can't see
            ["@token"]        = PlayerRemoteControls.QueryInventory,   // remaining daily charges of a held transport token
            ["@suicide"]      = PlayerRemoteControls.SysopCommands,   // irreversible — gated under Elevated Commands
            ["@invite"]       = PlayerRemoteControls.RequestInvite,
            ["@join"]         = PlayerRemoteControls.RequestInvite,
            // Bulk inventory verbs — operate on *all* applicable items
            // (@get-all: everything on the ground we can pick up; @drop-all:
            // everything in the pack we can drop; @deposit-all: bank it all).
            // Distinct from @equip-<set>, which is a per-slot loadout swap.
            ["@get-all"]      = PlayerRemoteControls.ExecuteCommands,
            ["@drop-all"]     = PlayerRemoteControls.ExecuteCommands,
            ["@deposit-all"]  = PlayerRemoteControls.ExecuteCommands,
            ["@do"]           = PlayerRemoteControls.ExecuteCommands,
            // @kill <target> asks a party member to attack a named target
            // on the sender's behalf — an action request, not a party
            // coordination signal, so it's per-player ExecuteCommands-gated
            // rather than party-whitelist. Handler lives in KillHandler.cs.
            ["@kill"]         = PlayerRemoteControls.ExecuteCommands,
            // @trap <dir> asks a Traps-skilled character to search +
            // disarm a trap on the sender's behalf; @trap stop aborts.
            // Soft-gated on Stats.Traps > 0 inside the handler; the
            // permission tier covers the "do something on my behalf"
            // semantic.
            ["@trap"]         = PlayerRemoteControls.ExecuteCommands,
            // @train asks us to train (level up) and, if Auto-train-stats is on,
            // spend the CP plan — assuming we're already at a trainer (no walk).
            // "Do something on my behalf", so ExecuteCommands like @do / @kill.
            ["@train"]        = PlayerRemoteControls.ExecuteCommands,
            // @equip-<setname> asks us to wear one of our saved gear sets
            // (Workshop Equipment tab) — the text after "@equip-" is the set
            // keyword, dispatched via RemoteCommandManager.RegisterPrefixHandler.
            // This bare "@equip" key is what the @help listing + Players-tab
            // tooltip surface; the wire form always carries a suffix. "Do
            // something on my behalf", so ExecuteCommands like @do / @train.
            // Handler lives in EquipHandler.cs.
            ["@equip"]        = PlayerRemoteControls.ExecuteCommands,

            // ===== Movement / Loops =====
            // @looponce / @roam from the upstream MegaMUD catalog aren't
            // supported — there's no random-walk roam mode and loops always
            // cycle. @lair is the counterpart for the Auto-Lair scheduler.
            // Handler lives in MovePlayerHandler.cs.
            ["@goto"]         = PlayerRemoteControls.MovePlayer,
            ["@loop"]         = PlayerRemoteControls.MovePlayer,
            ["@lair"]         = PlayerRemoteControls.MovePlayer,
            ["@stop"]         = PlayerRemoteControls.MovePlayer,
            ["@rego"]         = PlayerRemoteControls.MovePlayer,

            // ===== Toggle Settings =====
            // @atkprio / @atkorder split the legacy @attack-last verb: one
            // sets the priority target, the other the target-ordering mode.
            // No-arg form queries the current value; an arg sets it. Handler
            // lives in AtkConfigHandler.cs, writes CombatSettings.
            ["@atkprio"]      = PlayerRemoteControls.AlterSettings,
            ["@atkorder"]     = PlayerRemoteControls.AlterSettings,
            ["@auto-all"]     = PlayerRemoteControls.AlterSettings,
            ["@auto-combat"]  = PlayerRemoteControls.AlterSettings,
            ["@auto-nuke"]    = PlayerRemoteControls.AlterSettings,
            ["@auto-heal"]    = PlayerRemoteControls.AlterSettings,
            ["@auto-rest"]    = PlayerRemoteControls.AlterSettings,
            ["@auto-bless"]   = PlayerRemoteControls.AlterSettings,
            ["@auto-light"]   = PlayerRemoteControls.AlterSettings,
            ["@auto-cash"]    = PlayerRemoteControls.AlterSettings,
            ["@auto-get"]     = PlayerRemoteControls.AlterSettings,
            ["@auto-sneak"]   = PlayerRemoteControls.AlterSettings,
            ["@auto-hide"]    = PlayerRemoteControls.AlterSettings,
            ["@auto-search"]  = PlayerRemoteControls.AlterSettings,
            ["@settings"]     = PlayerRemoteControls.AlterSettings,
            ["@reset"]        = PlayerRemoteControls.AlterSettings,
            ["@profile"]      = PlayerRemoteControls.AlterSettings,   // swap the active casting spell profile
            ["@divert"]       = PlayerRemoteControls.DivertConversations,
            ["@hangup"]       = PlayerRemoteControls.HangupDisconnect,
            ["@relog"]        = PlayerRemoteControls.HangupDisconnect,

            // ===== Party Response (party-whitelist gated) =====
            // None = "any active party member", per engine convention.
            ["@wait"]         = PlayerRemoteControls.None,
            ["@ok"]           = PlayerRemoteControls.None,
            ["@comeback"]     = PlayerRemoteControls.None,
            // @forget is the reconnect-recovery teardown paired with @comeback:
            // a follower calls off their own pickup, or a leader declines to
            // recover them. Party-whitelist gated, with the same left-behind /
            // remembered-leader bridge @comeback uses (a dropped member is no
            // longer an IsActivePartyMember). See RemoteCommandManager.ForgetEligibility.
            ["@forget"]       = PlayerRemoteControls.None,
            // (@kill moved to ExecuteCommands — see Basic Commands above.)
            // @heal sits at ExecuteCommands rather than None: it's an
            // action request ("cast heal on me"), not a coordination
            // signal. A sender may legitimately need it even when the
            // receiver's auto-heal thresholds don't naturally pick
            // them up (settings mismatch between healer and target).
            // HealCommandHandler wires the receive side (a configured
            // healer polls `par` so CastingDirector heals the requester);
            // the emit side is HealthManager's follower flee-substitute.
            ["@heal"]         = PlayerRemoteControls.ExecuteCommands,
            // @party at QueryHealthStatus — non-party players with that
            // grant can use the no-args form as a status query
            // ("are you solo / leading / following?"). The engine
            // ALSO applies an @party-specific party-member fallback
            // in IsAuthorised so the "base @party always allowed
            // inside an active party" rule still holds even when the
            // sender has no per-player grant. The destructive
            // sub-command dispatch path (Local channel + args) lives
            // in PartyEssentialHandlers.OnParty and gates on
            // IsActivePartyMember + !DisallowPartyDirectives itself.
            // Hard-blocks (@party suicide, @party reroll) bypass both
            // at engine level via IsHardBlocked.
            ["@party"]        = PlayerRemoteControls.QueryHealthStatus,
            ["@share"]        = PlayerRemoteControls.None,
        };

    // Per-command help — argument syntax + a one-line description, surfaced by
    // `@help <command>`. Every command in Map has an entry here (enforced by a
    // catalog-completeness test), including the party-whitelist ones, since @help
    // describes commands regardless of how they're gated. Keyed case-insensitively
    // by the same @-prefixed name as Map. Descriptions are drawn from the Help
    // guide's remote-@-command section and kept terse enough that
    // "Syntax — Description" fits one telepath line.
    public static readonly IReadOnlyDictionary<string, RemoteCommandHelp> Help =
        new Dictionary<string, RemoteCommandHelp>(StringComparer.OrdinalIgnoreCase)
        {
            ["@version"]      = new("@version", "the app name + version"),
            ["@health"]       = new("@health", "HP / MA / Kai and resting-or-meditating state"),
            ["@exp"]          = new("@exp", "exp remaining to level, exp/hour rate, and time-to-level"),
            ["@level"]        = new("@level", "level, current exp, and exp to next"),
            ["@status"]       = new("@status", "what you're doing (walking/looping/fighting/resting), your room, and any ailments"),
            ["@lives"]        = new("@lives", "lives remaining"),
            ["@where"]        = new("@where", "room name, map/room, and exits"),
            ["@path"]         = new("@path", "movement engine activity + step progress; when idle, the last loop/auto-lair run"),
            ["@who"]          = new("@who", "other players / monsters in your room"),
            ["@timer"]        = new("@timer [name]", "boss respawn timers (all, or matching a name); @timer sync shares them client-to-client"),
            ["@death"]        = new("@death [all]", "unrecovered deaths from the recovery log — the latest, or all of them"),
            ["@roomba"]       = new("@roomba <item>", "where an item was last seen across gang-house rooms; @roomba sync shares the log"),
            ["@help"]         = new("@help [command]", "the commands you're allowed to use, or one command's syntax"),
            ["@what"]         = new("@what", "items on the room floor"),
            ["@wealth"]       = new("@wealth", "your coins and total value"),
            ["@enc"]          = new("@enc", "encumbrance"),
            ["@have"]         = new("@have <item>", "whether you carry, wear, or hold a matching item / key"),
            ["@inv"]          = new("@inv", "your carried pack and keys"),
            ["@token"]        = new("@token <name>", "remaining daily charges of a held transport token"),
            ["@suicide"]      = new("@suicide", "forces your character's death (Elevated; uses your stored suicide password; blocked at/below your lives threshold)"),
            ["@invite"]       = new("@invite", "asks you to invite the sender into your party"),
            ["@join"]         = new("@join", "asks you to join the sender's party"),
            ["@get-all"]      = new("@get-all", "pick up everything on the ground you can"),
            ["@drop-all"]     = new("@drop-all", "drop everything unworn in your pack"),
            ["@deposit-all"]  = new("@deposit-all", "bank all excess coin"),
            ["@do"]           = new("@do <command>", "sends the command verbatim to the game (highest-trust)"),
            ["@kill"]         = new("@kill <target>", "retargets your combat onto the named monster this round"),
            ["@trap"]         = new("@trap <dir>", "search and disarm a trap in that direction; @trap stop aborts"),
            ["@train"]        = new("@train", "trains (and applies your CP plan if Auto-train-stats is on); assumes you're at a trainer"),
            ["@equip"]        = new("@equip-<set>", "wears a saved gear set by keyword (e.g. @equip-backstab; @equip-all = Default set)"),
            ["@goto"]         = new("@goto <destination>", "walks you to a GOTO favorite, a searched room (coords/name/acronym), or a boss"),
            ["@loop"]         = new("@loop <name|coords|last>", "starts a saved loop, an ad-hoc coordinate loop (≥2 coords), or re-runs the last loop run this session ('last')"),
            ["@lair"]         = new("@lair <name|coords>", "starts an Auto-Lair setup"),
            ["@stop"]         = new("@stop", "pauses your movement"),
            ["@rego"]         = new("@rego", "resumes your movement"),
            ["@atkprio"]      = new("@atkprio [1|2|3 <name>]", "Target Priority: bare reports it; 1 Default, 2 follow-leader, 3 <name> attack-what-player"),
            ["@atkorder"]     = new("@atkorder [1-4 <name>]", "Attack Order: bare reports it; 1 Default, 2 last-party, 3 last-room, 4 <name> attack-after"),
            ["@auto-all"]     = new("@auto-all [on|off]", "kill switch: off stops every engine, on restores what was running"),
            ["@auto-combat"]  = new("@auto-combat [on|off]", "toggle the auto-combat engine"),
            ["@auto-nuke"]    = new("@auto-nuke [on|off]", "toggle the auto-nuke (offensive spell) engine"),
            ["@auto-heal"]    = new("@auto-heal [on|off]", "toggle auto-heal (same flag as @auto-rest)"),
            ["@auto-rest"]    = new("@auto-rest [on|off]", "toggle auto-rest / heal"),
            ["@auto-bless"]   = new("@auto-bless [on|off]", "toggle the auto-bless engine"),
            ["@auto-light"]   = new("@auto-light [on|off]", "toggle the auto-light engine"),
            ["@auto-cash"]    = new("@auto-cash [on|off]", "toggle the auto-cash engine"),
            ["@auto-get"]     = new("@auto-get [on|off]", "toggle the auto-get (loot) engine"),
            ["@auto-sneak"]   = new("@auto-sneak [on|off]", "toggle the auto-sneak engine"),
            ["@auto-hide"]    = new("@auto-hide [on|off]", "toggle the auto-hide engine"),
            ["@auto-search"]  = new("@auto-search [on|off]", "toggle the auto-search engine"),
            ["@settings"]     = new("@settings", "reports every engine's on/off state"),
            ["@reset"]        = new("@reset", "zeroes your Session Stats counters"),
            ["@profile"]      = new("@profile [n|name]", "swaps your active combat profile; bare reports the roster"),
            ["@divert"]       = new("@divert [player]", "forwards your incoming telepaths to another player; bare stops"),
            ["@hangup"]       = new("@hangup", "drops your connection and stays down (no auto-reconnect)"),
            ["@relog"]        = new("@relog", "cleanly exits, then reconnects and auto-logs back in"),
            ["@wait"]         = new("@wait", "hold: automation pauses until @ok releases it"),
            ["@ok"]           = new("@ok", "releases a @wait hold"),
            ["@comeback"]     = new("@comeback [map/room]", "stranded member asks the party to come recover them"),
            ["@forget"]       = new("@forget", "calls off a @comeback recovery"),
            ["@heal"]         = new("@heal", "asks a configured party healer to heal whoever's low"),
            ["@party"]        = new("@party [directive]", "bare reports solo/following/leading; with args on say, relays the directive to your character"),
            ["@share"]        = new("@share", "splits your held coin evenly across the party"),
        };

    // Look up the required category for a command. Returns false for unknown
    // commands so the caller can decide whether to register the handler anyway
    // (user-defined triggers, future extension points). Lookup is
    // case-insensitive; a trailing bang on the wire form (e.g. an emphatic
    // @stop!) is stripped so it matches the bare command name.
    public static bool TryGetCategory(string command, out PlayerRemoteControls category)
    {
        if (string.IsNullOrEmpty(command)) { category = default; return false; }
        string key = command;
        // Strip a trailing `!` so an emphatic wire form matches the bare
        // command name in the catalog.
        if (key[^1] == '!') key = key[..^1];
        return Map.TryGetValue(key, out category);
    }

    // Look up a command's help. Accepts the name with or without the leading `@`
    // (so `@help suicide` and `@help @suicide` both resolve) and strips a trailing
    // `!`. Case-insensitive. Returns false for unknown commands.
    public static bool TryGetHelp(string command, out RemoteCommandHelp help)
    {
        help = default;
        if (string.IsNullOrWhiteSpace(command)) return false;
        string key = command.Trim();
        if (key[^1] == '!') key = key[..^1];
        if (key.Length == 0) return false;
        if (key[0] != '@') key = "@" + key;
        return Help.TryGetValue(key, out help);
    }

    // Total number of documented commands in the catalog. Useful for tests that
    // pin "every wiki command is mapped".
    public static int Count => Map.Count;
}
