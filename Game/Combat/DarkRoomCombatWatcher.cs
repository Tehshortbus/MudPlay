using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Combat;

// Engages a monster that shares a dark room with us — one the normal room
// display can't reveal. A room too dark to see in prints only "The room is very
// dark..." / "The room is pitch black..." with no name, no exits, and crucially
// no "Also here:" line (see GAME_MECHANICS.md), so RoomEntityClassifier never
// learns the hostile is present and auto-combat sits idle while the mob swings.
//
// The one signal that leaks through is the mob's incoming attack line, rendered
// in dark cyan: a miss "The <monster> <verb> at you" or a hit "The <monster>
// <verb> you for N damage!". The <monster> token is the monster's real name, so
// injecting it into the classifier's observation drives CombatManager to send
// "a <monster>" exactly as if it had been listed under "Also here:". Confirmed
// with the user: `a cave bear` engages a cave bear revealed only by its attack
// line.
//
// Scope guard: everything here is gated on RoomTracker.IsInDarkRoom. In a lit
// room the "Also here:" line already lists occupants and the normal
// classifier / death-watcher pipeline keeps the target list honest, so this
// watcher must never fabricate a target there. The same gate keeps the
// "Your command had no effect." retraction (below) from colliding with the
// dropped / mortally-wounded bounce that emits the same line in a lit room.
public sealed class DarkRoomCombatWatcher : IDisposable
{
    // LogService category — [DarkRoomCombat] rows per revealed / retracted mob.
    public const string LogCategory = "DarkRoomCombat";

    private readonly RoomTracker _roomTracker;
    private readonly RoomEntityClassifier _classifier;
    private readonly Func<string?> _currentTarget;
    private readonly LogService? _log;
    private readonly IDisposable _mobMissesSub;
    private readonly IDisposable _mobHitsSub;
    private readonly IDisposable _noEffectSub;
    private bool _disposed;

    public DarkRoomCombatWatcher(
        MessageRouter router,
        RoomTracker roomTracker,
        RoomEntityClassifier classifier,
        Func<string?> currentTarget,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(roomTracker);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(currentTarget);
        _roomTracker = roomTracker;
        _classifier = classifier;
        _currentTarget = currentTarget;
        _log = log;
        _mobMissesSub = router.Subscribe(KnownPatterns.MobMisses, OnMobAttack);
        _mobHitsSub = router.Subscribe(KnownPatterns.MobHits, OnMobAttack);
        _noEffectSub = router.Subscribe(KnownPatterns.CommandNoEffect, OnCommandNoEffect);
    }

    private void OnMobAttack(MatchResult match)
    {
        if (!_roomTracker.IsInDarkRoom) return;
        if (match.Groups.Count == 0) return;

        // Both patterns capture the monster name at group 0 (MobMisses:
        // "The (?<target>...) <verb> at you"; MobHits: same head, "... you for
        // N damage!").
        string target = match.Groups[0].Trim();
        if (target.Length == 0) return;

        // A dark room re-emits the same mob's attack line every round. Once the
        // mob is in the classifier's observation, skip — re-appending it each
        // round would pile duplicates into the entity list.
        if (AlreadyPresent(target)) return;

        RoomEntity classified = _classifier.Classify(target);
        if (classified.Kind != EntityKind.Monster || classified.MonsterNumber is null)
        {
            // Unknown name, or a message record with no Monsters-table link.
            // CombatManager skips null-number monsters, so injecting one would
            // hold the combat gate without ever engaging — worse than doing
            // nothing. Log at Debug (this fires every attack round) and leave it
            // out; the user can add the monster to their data to enable engage.
            _log?.Debug(LogCategory,
                $"dark-room attacker '{target}' not a known engageable monster — not injecting");
            return;
        }

        _classifier.AppendArrivalEntity(classified, rawWireLine: match.Text);
        _log?.Info(LogCategory,
            $"dark-room attacker revealed name={classified.ResolvedName} — injected for auto-combat");
    }

    private void OnCommandNoEffect(MatchResult _)
    {
        if (!_roomTracker.IsInDarkRoom) return;
        if (_currentTarget() is not { Length: > 0 } target) return;

        // "Your command had no effect." after our attack means the target isn't
        // in the room. In the dark we injected it off its attack line, and it
        // has since died (no visible death line reaches us) or fled. Drop it
        // from the classifier's list; the re-fired EntitiesObserved leaves the
        // engageable list empty, so CombatManager clears its target and stops
        // swinging at the phantom.
        if (_classifier.RemoveDeadEntity(target))
            _log?.Info(LogCategory,
                $"dark-room target '{target}' not in room (command had no effect) — retracted");
        else
            _log?.Debug(LogCategory,
                $"command had no effect but target '{target}' wasn't in the room list");
    }

    private bool AlreadyPresent(string target)
    {
        if (_classifier.Current is not { } cur) return false;
        foreach (RoomEntity e in cur.Entities)
        {
            if (e.Kind != EntityKind.Monster) continue;
            if (string.Equals(e.RawName, target, StringComparison.OrdinalIgnoreCase)
             || string.Equals(e.ResolvedName, target, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mobMissesSub.Dispose();
        _mobHitsSub.Dispose();
        _noEffectSub.Dispose();
    }
}
