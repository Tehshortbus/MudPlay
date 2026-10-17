using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Combat;

// Recognises monster deaths from the always-present exp signal. A kill prints, in a
// fixed order, the monster's death-flavor line → "You gain N experience." → *Combat
// Off*. The exp line is the reliable, monster-agnostic kill signal (see GAME_MECHANICS
// "Kill detection and monster-kill message order"): an exp gain followed by a *Combat
// Off* within a short window is a death. MonsterDied names no monster — consumers
// attribute the kill to whatever they were fighting (CombatManager.CurrentTarget)
// and force a roster re-display to pick the survivor.
//
// Not every *Combat Off* inside the window is the kill's. On Paradigm a room spell
// that leaves a survivor prints its kills' exp lines and no Off at all, and the next
// Off is then whatever command stops that spell: the attack sent at the survivor, a
// cast, a `break`. CommandOffProbe names those, and they are not deaths.
//
// Those kills are deaths all the same, and are raised on their exp lines by the one
// party that knows a room spell of ours made them: CombatManager, through
// NoteRoomSpellKill. A kill is then counted when it happens, not when the last
// monster of the room dies or some later Off happens to fall inside the window.
//
// Per-monster death MESSAGES were retired: they're arbitrary per-monster flavor with
// no shared keyword and no distinctive colour, so a generic wording/colour matcher is
// infeasible. The exp line is the only reliable generic signal, and our own targeting
// names the mob — so the per-monster DeathLine data bought nothing and is gone.
public sealed class MonsterDeathWatcher : IDisposable
{
    // LogService category — appears as [MonsterDeath] rows per fire.
    public const string LogCategory = "MonsterDeath";

    // Window after a "You gain N exp." line within which a *Combat Off* qualifies as a
    // kill confirmation.
    private static readonly TimeSpan ExpToCombatOffWindow = TimeSpan.FromSeconds(5);

    private readonly LogService? _log;
    private readonly CommandOffProbe _offProbe;
    private readonly IDisposable _expSub;
    private readonly IDisposable _combatStatusSub;

    private DateTimeOffset? _lastExpAt;
    private int? _lastExpAmount;
    private bool _disposed;

    // Fires once per observed death. Subscribers run on the line-emitting thread.
    public event Action<MonsterDeathEvent>? MonsterDied;

    private readonly Func<DateTimeOffset> _now;

    public MonsterDeathWatcher(MessageRouter router, LogService? log = null, Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.Now);
        ArgumentNullException.ThrowIfNull(router);
        _log = log;
        _offProbe = new CommandOffProbe(router);
        // Ahead of CombatManager's handler of the same exp line (tieBreak 0): that one
        // may count the kill there and then (NoteRoomSpellKill), and the exp it spends
        // has to be held already, or this handler would hold it again afterwards and
        // the kill's own *Combat Off* would count it twice.
        _expSub          = router.Subscribe(KnownPatterns.UserGainExperience, OnExp, tieBreak: 100);
        _combatStatusSub = router.Subscribe(KnownPatterns.CombatStatus,        OnCombatStatus);
    }

    private void OnExp(MatchResult m)
    {
        if (m.Groups.Count == 0) return;
        if (!int.TryParse(m.Groups[0], out int exp)) return;
        _lastExpAt = _now();
        _lastExpAmount = exp;
    }

    // A kill made under our own room-attack spell, counted on its exp line: such a
    // kill has no *Combat Off* of its own while anything in the room survives
    // (Paradigm), so there is nothing later to count it on. roster is the kinds of
    // monster the room listed, one of which died.
    //
    // The exp line is spent here. Where the game does print a *Combat Off* after it
    // (Stock after every kill, Paradigm after the kill that empties the room), that
    // Off finds no exp held and raises nothing more for the same kill.
    public void NoteRoomSpellKill(int? experience, IReadOnlyList<MonsterDeathIdentity> roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        if (_disposed) return;
        _lastExpAt = null;
        _lastExpAmount = null;

        _log?.Info(LogCategory,
            $"death — exp={experience} under a room spell; one of {roster.Count} kind(s) of monster the room listed");
        MonsterDied?.Invoke(new MonsterDeathEvent(
            ExperienceGained: experience,
            At:               _now(),
            RoomSpellRoster:  roster));
    }

    private void OnCombatStatus(MatchResult m)
    {
        if (m.Groups.Count == 0) return;
        if (!string.Equals(m.Groups[0], "Off", StringComparison.OrdinalIgnoreCase)) return;
        if (_lastExpAt is not { } expAt) return;

        DateTimeOffset now = _now();
        if (now - expAt > ExpToCombatOffWindow) return;

        // The exp is dropped with it: its kill's own Off would have come straight
        // after it, so no later Off is that kill's either.
        if (_offProbe.CommandAnswered is { } command)
        {
            _log?.Info(LogCategory,
                $"*Combat Off* answers '{command}', not a kill — no death read from the exp "
                + $"{(now - expAt).TotalSeconds:F1}s before it");
            _lastExpAt = null;
            _lastExpAmount = null;
            return;
        }

        MonsterDeathEvent evt = new(
            ExperienceGained: _lastExpAmount,
            At:               now);
        _log?.Info(LogCategory,
            $"death — exp={_lastExpAmount} + *Combat Off* within {ExpToCombatOffWindow.TotalSeconds:F0}s");
        MonsterDied?.Invoke(evt);

        // Consumed — a second *Combat Off* must not re-fire on the same exp.
        _lastExpAt = null;
        _lastExpAmount = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _offProbe.Dispose();
        _expSub.Dispose();
        _combatStatusSub.Dispose();
    }
}
