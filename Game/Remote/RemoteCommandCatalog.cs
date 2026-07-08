using FujinTerm.Models.GameData;

namespace FujinTerm.Game.Remote;

// Single source of truth mapping every documented MajorMUD / MegaMUD @-command
// to the PlayerRemoteControls category that gates it. Sourced from the
// bearfather wiki canonical reference:
// https://kyau.net/wiki/MajorMUD:Remote_Commands
//
// Whichever handler wires a command, it looks the category up here via
// TryGetCategory rather than hardcoding — keeping the mapping in one place means
// "Fujin grants Raijin QueryHealthStatus" produces consistent behaviour across
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
            ["@help"]         = PlayerRemoteControls.QueryVersion,
            ["@what"]         = PlayerRemoteControls.QueryInventory,
            ["@wealth"]       = PlayerRemoteControls.QueryInventory,
            ["@enc"]          = PlayerRemoteControls.QueryInventory,
            ["@have"]         = PlayerRemoteControls.QueryInventory,
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

    // Total number of documented commands in the catalog. Useful for tests that
    // pin "every wiki command is mapped".
    public static int Count => Map.Count;
}
