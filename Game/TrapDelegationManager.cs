using System.Linq;
using FujinTerm.Game.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game;

// Party-member trap delegation. When the walker hits a trapped exit and the
// LOCAL character can't disarm it, but a party member can, this manager
// broadcasts @trap <dir> on the say channel and resumes the walk when that
// member's client replies on say with the existing trap-disarm reply string
// ("Trap to the {dir} disarmed." on success; the failure / stop strings
// otherwise).
//
// Signal separation (architectural guardrail): the LOCAL self-disarm path keys
// exclusively on the game's own first-person disarm signals via
// TrapDisarmManager — it never watches say replies. This manager is the ONLY
// consumer of remote say replies, and only for the delegation branch. The two
// paths converge on the walker's single signal-agnostic OnTrapReply callback
// but their signal SOURCES stay distinct.
//
// Capability check (per the trap-capability ability codes in
// AbilityNames.HasTrapAbility): a member's CLASS is the main gate — its Classes
// row is scanned for a trap-skill grant. RACE is secondary, consulted only when
// we actually know the member's race (a "shadowy figure" look leaves it unknown,
// so we don't assume). Race for a joined member comes from the PlayerDatabase;
// if it's missing we look at them once on join to capture it (GreetManager skips
// party members, so the look has to originate here).
//
// No automatic timeout — symmetric with the local path, which relies on the
// game's guaranteed reply. A capable party member always replies; if the user
// wants out they stop the walk, which cancels the pending delegation via Cancel.
public sealed class TrapDelegationManager : System.IDisposable
{
    private const string LogCategory = "TrapDelegate";

    private readonly PartyManager _party;
    private readonly PlayerDatabase _players;
    private readonly GameDataCache _gameData;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();
    private readonly System.IDisposable _localSaySub;
    private bool _disposed;

    // The walker's resume callback for the in-flight delegation, or null when idle.
    private System.Action<string>? _pendingReply;

    public TrapDelegationManager(
        PartyManager party,
        PlayerDatabase players,
        GameDataCache gameData,
        MessageRouter router,
        LogService? log = null)
    {
        System.ArgumentNullException.ThrowIfNull(party);
        System.ArgumentNullException.ThrowIfNull(players);
        System.ArgumentNullException.ThrowIfNull(gameData);
        System.ArgumentNullException.ThrowIfNull(router);
        _party    = party;
        _players  = players;
        _gameData = gameData;
        _log      = log;

        _party.MemberFollowConfirmed += OnMemberJoined;
        _localSaySub = router.Subscribe(KnownPatterns.ConversationLocal, OnLocalSay);
    }

    // Bind the wire-sender. Same shape as the rest of the engine-side handlers —
    // used for both the look <name> race probe and the @trap <dir> say broadcast.
    public void SetWireSender(System.Action<byte[]> sender) => _wire.Bind(sender);

    // Test seam — bytes the manager asked to write to the wire.
    internal System.Collections.Generic.List<byte[]> LastSentForTests => _wire.LastSentForTests;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _party.MemberFollowConfirmed -= OnMemberJoined;
        _localSaySub.Dispose();
    }

    // ----- Capability -----------------------------------------------------

    // True when at least one joined party member (excluding the local character
    // and not-yet-joined invitees) can disarm traps — class main gate, race
    // secondary. Used by the walker's delegation gate.
    public bool AnyPartyMemberCanDisarm()
    {
        foreach (PartyMember m in _party.State.Members)
        {
            if (m.IsSelf || m.IsInvited) continue;
            if (MemberCanDisarm(m)) return true;
        }
        return false;
    }

    // Class is the main gate; race (from the PlayerDatabase, unknown for a
    // "shadowy figure") is secondary. Shared with the local self-disarm check.
    private bool MemberCanDisarm(PartyMember m)
        => AbilityNames.ClassOrRaceGrantsTraps(_gameData, m.Class, LookupRace(m.Name));

    private string? LookupRace(string name)
        => _players.Players
            .FirstOrDefault(p => p.GivenName.Equals(FirstWord(name), System.StringComparison.OrdinalIgnoreCase))
            ?.Race;

    // ----- Race capture on join -------------------------------------------

    private void OnMemberJoined(string name)
    {
        // Class already settles capability → no race probe needed (race: null
        // scopes the shared check to the class gate alone).
        PartyMember? m = _party.State.Members
            .FirstOrDefault(x => x.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase));
        if (m is not null && AbilityNames.ClassOrRaceGrantsTraps(_gameData, m.Class, null)) return;

        // Race already known → no probe needed.
        if (!string.IsNullOrWhiteSpace(LookupRace(name))) return;

        // Unknown race on a class that doesn't grant traps — look once so
        // the LookParser → PlayerDatabase.RecordLook pipeline fills it in.
        // ("shadowy figure" leaves race null; we simply stay unable.)
        _wire.Send("look " + FirstWord(name));
        _log?.Info(LogCategory, $"probing race of joined member '{name}' via look");
    }

    // ----- Delegation -----------------------------------------------------

    // Broadcast @trap <dir> on say and arm the resume-watch. onReply is the
    // walker's signal-agnostic OnTrapReply — invoked once when a capable party
    // member's say reply lands (success / failure / stop), at which point the
    // walker resumes or aborts.
    public void Delegate(string dirWord, System.Action<string> onReply)
    {
        System.ArgumentNullException.ThrowIfNull(onReply);
        _pendingReply = onReply;
        _wire.Send(".@trap " + dirWord);
        _log?.Info(LogCategory, $"delegated trap {dirWord} to party on say");
    }

    // Drop the in-flight delegation watch without resuming the walk. Called when
    // the walk is stopped / superseded mid-delegation so a later stray say reply
    // can't resume a dead walk.
    public void Cancel() => _pendingReply = null;

    private void OnLocalSay(MatchResult result)
    {
        if (_pendingReply is null) return;

        // Group 0 = speaker (empty for our own "You say …"), group 1 = text.
        string? speaker = result.Groups.Count > 0 ? result.Groups[0] : null;
        string? message = result.Groups.Count > 1 ? result.Groups[1] : null;
        if (string.IsNullOrEmpty(speaker) || string.IsNullOrEmpty(message)) return;

        // Only a party member's reply resumes us — ignore random room chatter.
        if (!IsPartyMember(speaker)) return;
        if (!IsTrapReply(message)) return;

        System.Action<string> reply = _pendingReply;
        _pendingReply = null;
        _log?.Info(LogCategory, $"{speaker} say reply resumes delegated trap: '{message}'");
        reply(message);
    }

    private bool IsPartyMember(string speaker)
        => _party.State.Members.Any(m =>
            !m.IsSelf
            && m.Name.Equals(speaker, System.StringComparison.OrdinalIgnoreCase));

    // The existing TrapDisarmManager reply strings — success ("…disarmed."),
    // search give-up ("Couldn't find trap…"), and stop ("Trap flow stopped.").
    private static bool IsTrapReply(string message)
        => message.Contains("disarmed", System.StringComparison.OrdinalIgnoreCase)
        || message.Contains("Couldn't find trap", System.StringComparison.OrdinalIgnoreCase)
        || message.Contains("Trap flow stopped", System.StringComparison.OrdinalIgnoreCase);

    private static string FirstWord(string name)
    {
        int sp = name.IndexOf(' ');
        return sp < 0 ? name : name[..sp];
    }
}
