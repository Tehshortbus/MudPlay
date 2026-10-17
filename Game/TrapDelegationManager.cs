using System.Linq;
using MudPlay.Game.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game;

// Party-member trap delegation. When the walker hits a trapped exit and the
// LOCAL character can't disarm it, but a party member can, this manager says
// @trap <dir> to the room and resumes the walk on what the party answers.
//
// The exchange (user, 2026-10-09): a client that takes the trap answers at once
// with TrapReply.Attempting, which is how we know someone has it; it then disarms
// (resting first if the trap put it under its rest threshold) and says the result.
// The first member to report the exit clear resumes the walk. A member who gives
// up only ends it when no other member that accepted is still working. If nobody
// accepts inside AcceptWindow, nobody is going to (the capable member isn't running
// this client, hasn't granted the command, or isn't in the room), and the walk is
// told so instead of waiting on an answer that will never come.
//
// Answers are read from say and from telepath: a sneaking member's replies are
// moved to telepath so they don't break its sneak.
//
// Signal separation (architectural guardrail): the LOCAL self-disarm path keys
// exclusively on the game's own first-person disarm signals via
// TrapDisarmManager — it never watches chat. This manager is the ONLY consumer of
// remote replies, and only for the delegation branch. The two paths converge on
// the walker's single OnTrapReply callback but their signal SOURCES stay distinct.
//
// Capability check (per the trap-capability ability codes in
// AbilityNames.HasTrapAbility): a member's CLASS is the main gate — its Classes
// row is scanned for a trap-skill grant. RACE is secondary, consulted only when
// we actually know the member's race (a "shadowy figure" look leaves it unknown,
// so we don't assume). Race for a joined member comes from the PlayerDatabase;
// if it's missing we look at them once on join to capture it (GreetManager skips
// party members, so the look has to originate here).
public sealed class TrapDelegationManager : System.IDisposable
{
    private const string LogCategory = "TrapDelegate";

    private readonly PartyManager _party;
    private readonly PlayerDatabase _players;
    private readonly GameDataCache _gameData;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();
    private readonly System.IDisposable _localSaySub;
    private readonly System.IDisposable _telepathSub;
    private readonly System.Func<System.TimeSpan, System.Action, System.IDisposable>? _scheduleDelay;
    private bool _disposed;

    // How long a member's client has to accept. The acceptance is one chat line
    // sent before any disarm, so this only has to outlast a slow link.
    internal static readonly System.TimeSpan AcceptWindow = System.TimeSpan.FromSeconds(10);

    // The walker's resume callback for the in-flight delegation, or null when idle.
    private System.Action<string>? _pendingReply;
    private string _pendingDirection = string.Empty;
    private System.IDisposable? _acceptTimer;

    // Members who accepted the trap in flight and haven't reported a result.
    private readonly System.Collections.Generic.HashSet<string> _working =
        new(System.StringComparer.OrdinalIgnoreCase);

    // What the delegation in flight is waiting on, for the bug report.
    public string Describe() => _pendingReply is null
        ? "idle"
        : _working.Count == 0
            ? $"trap {_pendingDirection}: asked the party, nobody has accepted yet"
            : $"trap {_pendingDirection}: {string.Join(", ", _working)} working on it";

    public TrapDelegationManager(
        PartyManager party,
        PlayerDatabase players,
        GameDataCache gameData,
        MessageRouter router,
        LogService? log = null,
        System.Func<System.TimeSpan, System.Action, System.IDisposable>? scheduleDelay = null)
    {
        System.ArgumentNullException.ThrowIfNull(party);
        System.ArgumentNullException.ThrowIfNull(players);
        System.ArgumentNullException.ThrowIfNull(gameData);
        System.ArgumentNullException.ThrowIfNull(router);
        _party    = party;
        _players  = players;
        _gameData = gameData;
        _log      = log;

        _scheduleDelay = scheduleDelay;

        _party.MemberFollowConfirmed += OnMemberJoined;
        // Say carries speaker, directed-say target, text; telepath speaker, text.
        _localSaySub = router.Subscribe(KnownPatterns.ConversationLocal,
            m => OnPartyChat(Group(m, 0), Group(m, 2)));
        _telepathSub = router.Subscribe(KnownPatterns.ConversationTelepathIn,
            m => OnPartyChat(Group(m, 0), Group(m, 1)));
    }

    private static string? Group(MatchResult m, int index) =>
        m.Groups.Count > index ? m.Groups[index] : null;

    // Bind the wire-sender. Same shape as the rest of the engine-side handlers —
    // used for both the look <name> race probe and the @trap <dir> say broadcast.
    public void SetWireSender(System.Action<byte[]> sender) => _wire.Bind(sender);

    // Wired by AppServices to AutoPartyManager.IsReformSettling. While a
    // party-splitting-teleport reform is settling, we skip the race-probe look
    // entirely — no member looks during that evolution. Looking at a member the
    // instant they rejoin renders their description right as the walker's movement
    // gate releases, and a stray look landing on the walk's next-step room
    // confirmation re-strands the resuming move. Null = no reform coordinator
    // wired (always probe).
    public System.Func<bool>? IsPartyReformSettling { get; set; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _party.MemberFollowConfirmed -= OnMemberJoined;
        _localSaySub.Dispose();
        _telepathSub.Dispose();
        _acceptTimer?.Dispose();
    }

    // ----- Capability -----------------------------------------------------

    // True when at least one joined party member (excluding the local character
    // and not-yet-joined invitees) can disarm traps — class main gate, race
    // secondary. A walk only delegates a trap when this is true.
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

    // The master switch (true = off): off, a joining member is not looked at for
    // its race. Delegation itself is asked for only by a walk, which is frozen.
    public Func<bool>? MasterSwitchOff { get; set; }

    private void OnMemberJoined(string name)
    {
        if (MasterSwitchOff?.Invoke() == true) return;
        // Class already settles capability → no race probe needed (race: null
        // scopes the shared check to the class gate alone).
        PartyMember? m = _party.State.Members
            .FirstOrDefault(x => x.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase));
        if (m is not null && AbilityNames.ClassOrRaceGrantsTraps(_gameData, m.Class, null)) return;

        // Race already known → no probe needed.
        if (!string.IsNullOrWhiteSpace(LookupRace(name))) return;

        // A party-splitting-teleport reform is settling — skip the look. No member
        // looks during that evolution: a look here renders the member's
        // description right as the walker's gate releases and can re-strand the
        // resuming move. Race stays unknown (safe non-trap-capable default) until
        // a later, non-reform join or manual look fills it in.
        if (IsPartyReformSettling?.Invoke() == true)
        {
            _log?.Info(LogCategory, $"skipping race probe for '{name}' — party reform settling");
            return;
        }

        // Unknown race on a class that doesn't grant traps — look once so
        // the LookParser → PlayerDatabase.RecordLook pipeline fills it in.
        // ("shadowy figure" leaves race null; we simply stay unable.)
        _wire.Send("look " + FirstWord(name));
        _log?.Info(LogCategory, $"probing race of joined member '{name}' via look");
    }

    // ----- Delegation -----------------------------------------------------

    // Say @trap <dir> to the room and wait on the party. onReply is the walker's
    // OnTrapReply, invoked once with the line that ends the wait: a member's result,
    // or TrapReply.Unanswered when nobody accepted.
    public void Delegate(string dirWord, System.Action<string> onReply)
    {
        System.ArgumentNullException.ThrowIfNull(onReply);
        Clear();
        _pendingReply = onReply;
        _pendingDirection = dirWord;
        _wire.Send(".@trap " + dirWord);
        _acceptTimer = _scheduleDelay?.Invoke(AcceptWindow, OnNobodyAccepted);
        _log?.Info(LogCategory, $"delegated trap {dirWord} to party on say");
    }

    // Drop the in-flight delegation watch without resuming the walk. Called when
    // the walk is stopped / superseded mid-delegation so a later stray reply
    // can't resume a dead walk.
    public void Cancel() => Clear();

    private void Clear()
    {
        _pendingReply = null;
        _pendingDirection = string.Empty;
        _working.Clear();
        _acceptTimer?.Dispose();
        _acceptTimer = null;
    }

    private void OnNobodyAccepted()
    {
        if (_pendingReply is null || _working.Count > 0) return;
        _log?.Info(LogCategory,
            $"no party member accepted trap {_pendingDirection} in {AcceptWindow.TotalSeconds:0}s: "
            + "nobody able is running this client with the command granted, or they aren't in the room");
        Finish(TrapReply.Unanswered);
    }

    private void OnPartyChat(string? speaker, string? message)
    {
        if (_pendingReply is null) return;
        if (string.IsNullOrEmpty(speaker) || string.IsNullOrEmpty(message)) return;
        // Only a party member's reply counts — ignore random room chatter.
        if (!IsPartyMember(speaker)) return;

        if (TrapReply.IsAttempting(message))
        {
            _working.Add(speaker);
            _acceptTimer?.Dispose();
            _acceptTimer = null;
            _log?.Info(LogCategory, $"{speaker} accepted trap {_pendingDirection}; waiting for the result");
            return;
        }

        if (TrapReply.Read(message) is not { } outcome) return;
        _working.Remove(speaker);

        // One member giving up doesn't end it while another that accepted is
        // still at the trap.
        if (outcome != TrapReplyOutcome.Clear && _working.Count > 0)
        {
            _log?.Info(LogCategory,
                $"{speaker} gave up on trap {_pendingDirection} ('{message}'); still waiting on {string.Join(", ", _working)}");
            return;
        }

        _log?.Info(LogCategory, $"{speaker}'s reply ends delegated trap {_pendingDirection}: '{message}'");
        Finish(message);
    }

    private void Finish(string line)
    {
        System.Action<string>? reply = _pendingReply;
        Clear();
        reply?.Invoke(line);
    }

    private bool IsPartyMember(string speaker)
        => _party.State.Members.Any(m =>
            !m.IsSelf
            && FirstWord(m.Name).Equals(speaker, System.StringComparison.OrdinalIgnoreCase));

    private static string FirstWord(string name)
    {
        int sp = name.IndexOf(' ');
        return sp < 0 ? name : name[..sp];
    }
}
