using FujinTerm.Models.GameData;

namespace FujinTerm.Game.Remote;

/// <summary>
/// Single source of truth mapping every documented MajorMUD / MegaMUD
/// @-command to the <see cref="PlayerRemoteControls"/> category that
/// gates it. Sourced from the bearfather wiki canonical reference:
/// https://kyau.net/wiki/MajorMUD:Remote_Commands
/// </summary>
/// <remarks>
/// <para>
/// Phase 6 only ships handlers for the party-essential subset
/// (<see cref="PartyEssentialHandlers"/>); Phase 7 and Phase 12 register
/// the rest. Whichever phase wires a command, it looks the category up
/// here via <see cref="TryGetCategory"/> rather than hardcoding —
/// keeping the mapping in one place means "Fujin grants Raijin
/// QueryHealthStatus" produces consistent behaviour across every
/// `@health`-class command without per-handler ceremony.
/// </para>
/// <para>
/// Categories follow the 12-checkbox grid in the Game Data Browser →
/// Players edit dialog:
/// <list type="bullet">
///   <item><b>QueryVersion</b> — version / fingerprint introspection.</item>
///   <item><b>QueryExperience</b> — exp / level numbers.</item>
///   <item><b>QueryHealthStatus</b> — health / mana / state flags / lives.</item>
///   <item><b>QueryLocation</b> — room / path / who-saw-whom.</item>
///   <item><b>QueryInventory</b> — items / cash / encumbrance / have-checks.</item>
///   <item><b>RequestInvite</b> — party invite / join / leave signals.</item>
///   <item><b>MovePlayer</b> — goto / loop / roam / stop / rego.</item>
///   <item><b>ExecuteCommands</b> — @do passthrough + bulk inventory actions
///         (@get-all / @drop-all / @equip-all / @deposit-all).</item>
///   <item><b>HangupDisconnect</b> — @hangup / @relog.</item>
///   <item><b>AlterSettings</b> — auto-* toggles, @settings, @reset.</item>
///   <item><b>DivertConversations</b> — @divert.</item>
///   <item><b>SysopCommands</b> ("Elevated Commands" in the Players-tab
///         UI) — high-trust commands beyond ordinary control:
///         mudop-only (@home) and irreversible character actions
///         (@suicide). Wider than just sysop powers per FujinTerm
///         convention.</item>
/// </list>
/// </para>
/// <para>
/// Party-coordination commands (@wait / @ok / @comeback / @blind /
/// @diseased / @held / @party / @kill / @share / @panic) map to
/// <see cref="PlayerRemoteControls.None"/> — they're gated by the engine's
/// party-whitelist branch instead of the per-player flag check. Any
/// active party member can issue them by default; the user disables them
/// wholesale via Settings.Talk → Disallow @party commands.
/// </para>
/// <para>
/// <c>@heal</c> is the exception in that family: it's
/// <see cref="PlayerRemoteControls.ExecuteCommands"/> rather than
/// party-whitelist. The semantic is "do something on my behalf"
/// (cast a heal on the sender) rather than a coordination signal,
/// and a sender may legitimately need it even when the receiver's
/// auto-heal thresholds don't naturally pick them up (settings
/// mismatch between healer and target). Requires the receiver to
/// have granted the sender ExecuteCommands explicitly. Phase 12
/// CastingDirector wires the handler.
/// </para>
/// </remarks>
public static class RemoteCommandCatalog
{
    /// <summary>
    /// Canonical command → required category map. Keyed
    /// case-insensitively. Sentinel <see cref="PlayerRemoteControls.None"/>
    /// means "party-whitelist gated"; consult the catalog comment above
    /// for the full list of party-whitelist commands.
    /// </summary>
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
            ["@seen"]         = PlayerRemoteControls.QueryLocation,
            ["@who"]          = PlayerRemoteControls.QueryLocation,
            ["@what"]         = PlayerRemoteControls.QueryInventory,
            ["@wealth"]       = PlayerRemoteControls.QueryInventory,
            ["@enc"]          = PlayerRemoteControls.QueryInventory,
            ["@have"]         = PlayerRemoteControls.QueryInventory,
            ["@home"]         = PlayerRemoteControls.SysopCommands,   // mudop-only per bearfather
            ["@suicide"]      = PlayerRemoteControls.SysopCommands,   // irreversible — gated under Elevated Commands
            ["@invite"]       = PlayerRemoteControls.RequestInvite,
            ["@join"]         = PlayerRemoteControls.RequestInvite,
            ["@forget"]       = PlayerRemoteControls.RequestInvite,
            ["@get-all"]      = PlayerRemoteControls.ExecuteCommands,
            ["@drop-all"]     = PlayerRemoteControls.ExecuteCommands,
            ["@equip-all"]    = PlayerRemoteControls.ExecuteCommands,
            ["@deposit-all"]  = PlayerRemoteControls.ExecuteCommands,
            ["@do"]           = PlayerRemoteControls.ExecuteCommands,

            // ===== Movement / Loops =====
            ["@goto"]         = PlayerRemoteControls.MovePlayer,
            ["@loop"]         = PlayerRemoteControls.MovePlayer,
            ["@looponce"]     = PlayerRemoteControls.MovePlayer,
            ["@roam"]         = PlayerRemoteControls.MovePlayer,
            ["@stop"]         = PlayerRemoteControls.MovePlayer,
            ["@rego"]         = PlayerRemoteControls.MovePlayer,

            // ===== Toggle Settings =====
            ["@attack-last"]  = PlayerRemoteControls.AlterSettings,
            ["@auto-all"]     = PlayerRemoteControls.AlterSettings,
            ["@auto-combat"]  = PlayerRemoteControls.AlterSettings,
            ["@auto-nuke"]    = PlayerRemoteControls.AlterSettings,
            ["@auto-heal"]    = PlayerRemoteControls.AlterSettings,
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
            // @heal sits at ExecuteCommands rather than None: it's an
            // action request ("cast heal on me"), not a coordination
            // signal. A sender may legitimately need it even when the
            // receiver's auto-heal thresholds don't naturally pick
            // them up (settings mismatch between healer and target).
            // Phase 12 CastingDirector wires the handler.
            ["@heal"]         = PlayerRemoteControls.ExecuteCommands,
            ["@blind"]        = PlayerRemoteControls.None,
            ["@diseased"]     = PlayerRemoteControls.None,
            ["@held"]         = PlayerRemoteControls.None,
            // @party at QueryHealthStatus — non-party players with that
            // grant can use the no-args form as a status query
            // ("are you solo / leading / following?"). The engine
            // ALSO applies an @party-specific party-member fallback
            // in IsAuthorised so the Phase 6 "base @party always
            // allowed inside an active party" rule still holds even
            // when the sender has no per-player grant. The destructive
            // sub-command dispatch path (Local channel + args) lives
            // in PartyEssentialHandlers.OnParty and gates on
            // IsActivePartyMember + !DisablePartyWhitelist itself.
            // Hard-blocks (@party suicide, @party reroll) bypass both
            // at engine level via IsHardBlocked.
            ["@party"]        = PlayerRemoteControls.QueryHealthStatus,
            ["@kill"]         = PlayerRemoteControls.None,
            ["@share"]        = PlayerRemoteControls.None,
            ["@panic"]        = PlayerRemoteControls.None,  // wiki form is `@panic!`; match the bang-stripped command name
        };

    /// <summary>
    /// Look up the required category for a command. Returns <c>false</c>
    /// for unknown commands so the caller can decide whether to register
    /// the handler anyway (Phase 5 user-defined triggers, future
    /// extension points). Lookup is case-insensitive; trailing bangs
    /// (<c>@panic!</c>) are stripped so the wiki form matches.
    /// </summary>
    public static bool TryGetCategory(string command, out PlayerRemoteControls category)
    {
        if (string.IsNullOrEmpty(command)) { category = default; return false; }
        string key = command;
        // Strip trailing `!` so `@panic!` matches `@panic` in the catalog.
        if (key[^1] == '!') key = key[..^1];
        return Map.TryGetValue(key, out category);
    }

    /// <summary>
    /// Total number of documented commands in the catalog. Useful for
    /// tests that pin "every wiki command is mapped".
    /// </summary>
    public static int Count => Map.Count;
}
