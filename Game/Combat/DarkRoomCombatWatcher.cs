using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Combat;

// Engages a monster that shares a dark room with us — one the normal room
// display can't reveal. A room too dark to see in prints only "The room is very
// dark..." / "The room is pitch black..." with no name, no exits, and crucially
// no "Also here:" line (see GAME_MECHANICS.md), so RoomEntityClassifier never
// learns the hostile is present and auto-combat sits idle while the mob swings.
//
// Three things leak through the darkness, and each is a reveal:
//
//   attacks   a line whose shape is combat and nothing else — an article plus
//             "at you", or "you for N damage". Acted on the first time it is
//             seen.
//   swings    the same attacker swinging at us twice inside one round. This one
//             is article-OPTIONAL, which is how a monster with a proper name is
//             caught, and that looseness is why one is not enough: the shape is
//             also the shape of "The barmaid smiles at you." A repeat is
//             evidence, because a monster names itself on every swing while an
//             emote says its line once.
//   party     a party member's "<player> moves to attack <monster>." announce.
//             A same-room broadcast (GAME_MECHANICS.md), so a monster a party
//             member engages here shares our room even though the display never
//             named it, and it usually arrives a round before the mob first
//             swings at us.
//
// THE NAME IS READ OFF THE LINE, NOT OUT OF THE PATTERN. The attacker's name is
// followed by its own verb phrase from the .mdb and nothing marks the boundary,
// so a capture group guesses: MobHits reads "The bugbear captain all-out cleaves
// you for 20 damage!" as the name "bugbear captain all-out". Instead every
// word-prefix of the line is offered to the classifier and the longest one it
// recognises wins. The leading article is optional because a monster with a
// proper name is printed without one.
//
// Injecting the resolved monster into the classifier's observation — rather than
// sending an attack — is what keeps this honest: CombatManager then engages it
// by name through the same hostility filter and Target Order a lit-room "Also
// here:" monster goes through, so a non-hostile that trips a trigger is dropped
// instead of swung at, and an attacker that resolves to nothing is left alone.
//
// Scope guard: everything here is gated on RoomTracker.IsInDarkRoom. In a lit
// room the "Also here:" line already lists occupants and the normal classifier /
// death-watcher pipeline keeps the target list honest, so this watcher must
// never fabricate a target there — it has no retraction path of its own beyond
// the one below, and a phantom in a lit room would never be withdrawn.
//
// RETRACTION LISTENS FOR BOTH REFUSALS. A target that died unseen or left leaves
// the roster only when the server says so, and it says so two ways: "Your
// command had no effect." and "You don't see <X> here!" (also spelled "You do
// not see <X> here!"). Missing the second one strands a dark-room target
// permanently — nothing withdraws it, so AlreadyPresent below suppresses every
// later reveal and the room stays unfought while the mob swings.
public sealed class DarkRoomCombatWatcher : IDisposable
{
    // LogService category — [DarkRoomCombat] rows per revealed / retracted mob.
    public const string LogCategory = "DarkRoomCombat";

    // How close together two swings from the same attacker must fall before we
    // read them as a fight rather than as room chatter. One combat round.
    public static readonly TimeSpan SwingRepeatWindow = TimeSpan.FromSeconds(6);

    // Longest name we will try to read off the head of a line. "A tall elite orc
    // guard" is four words past its article; six is slack.
    private const int MaxNameWords = 6;

    private readonly MessageRouter _router;
    private readonly RoomTracker _roomTracker;
    private readonly RoomEntityClassifier _classifier;
    private readonly Func<string?> _currentTarget;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private readonly List<IDisposable> _subs = new();

    private string _lastSwinger = "";
    private DateTimeOffset _lastSwingAt = DateTimeOffset.MinValue;
    private bool _disposed;

    // Last reveal and last hold-off, for the bug-report surface: a report that
    // shows neither is a report where this watcher never saw anything.
    public DateTimeOffset? LastRevealAt { get; private set; }
    public string? LastRevealName { get; private set; }
    public string? LastRevealLine { get; private set; }
    public string? LastHeldOffReason { get; private set; }

    public DarkRoomCombatWatcher(
        MessageRouter router,
        RoomTracker roomTracker,
        RoomEntityClassifier classifier,
        Func<string?> currentTarget,
        Func<DateTimeOffset>? now = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(roomTracker);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(currentTarget);
        _router = router;
        _roomTracker = roomTracker;
        _classifier = classifier;
        _currentTarget = currentTarget;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _log = log;
        _subs.Add(router.Subscribe(KnownPatterns.MobMisses, OnAttackLine));
        _subs.Add(router.Subscribe(KnownPatterns.IncomingDamage, OnAttackLine));
        _subs.Add(router.Subscribe(KnownPatterns.MobHitsNoDamage, OnAttackLine));
        _subs.Add(router.Subscribe(KnownPatterns.IncomingAttack, OnSwing));
        _subs.Add(router.Subscribe(KnownPatterns.PartyAttackAnnounce, OnPartyAttack));
        _subs.Add(router.Subscribe(KnownPatterns.CommandNoEffect, OnCommandNoEffect));
        _subs.Add(router.Subscribe(KnownPatterns.TargetNotHere, OnTargetNotHere));
    }

    // An attack line whose shape is combat and nothing else: MobMisses requires
    // the article and "at you", IncomingDamage requires "you for N damage". Both
    // are specific enough to act on the first one seen.
    private void OnAttackLine(MatchResult match)
    {
        if (!_roomTracker.IsInDarkRoom) return;
        TryReveal(match.Text, match.Text, source: "attacker");
    }

    private void OnSwing(MatchResult match)
    {
        if (!_roomTracker.IsInDarkRoom) return;
        string who = (match.Groups.Count > 0 ? match.Groups[0] : "").Trim();
        if (who.Length == 0) return;

        DateTimeOffset now = _now();
        bool again = now - _lastSwingAt <= SwingRepeatWindow
                     && who.Equals(_lastSwinger, StringComparison.OrdinalIgnoreCase);
        _lastSwinger = who;
        _lastSwingAt = now;
        if (again) TryReveal(match.Text, match.Text, source: "repeated swing");
    }

    private void OnPartyAttack(MatchResult match)
    {
        if (!_roomTracker.IsInDarkRoom) return;
        // PartyAttackAnnounce captures the announcer at group 0 and the monster
        // it targets at group 1 ("<player> moves to attack <target>."). We want
        // the target — the monster now confirmed in our dark room. It is already
        // a bare name, so it goes straight to the classifier.
        if (match.Groups.Count < 2) return;
        Inject(match.Groups[1].Trim(), match.Text, "party-attack");
    }

    // Read the attacker off the line, then inject it.
    private void TryReveal(string line, string wireLine, string source)
    {
        if (ResolveFromLine(line) is not { } name)
        {
            // Nothing in the sentence resolves to a monster we know. Injecting a
            // guess would hold the combat gate on something CombatManager cannot
            // rank, so we say nothing. Debug: this fires every attack round.
            Hold($"{source}: no known monster in '{line}'");
            return;
        }
        Inject(name, wireLine, source);
    }

    private void Inject(string target, string wireLine, string source)
    {
        if (target.Length == 0) return;

        // A dark room re-emits the same mob's attack line every round, and the
        // leader's engage announce can arrive alongside it. Once the mob is in
        // the classifier's observation, skip — re-appending each round would pile
        // duplicates into the entity list.
        if (AlreadyPresent(target)) return;

        RoomEntity classified = _classifier.Classify(target);
        if (classified.Kind != EntityKind.Monster || classified.MonsterNumber is null)
        {
            // An unknown name, a message record with no Monsters-table link, or
            // (on the party-attack path) a PvP target that resolves to a Player.
            Hold($"{source}: '{target}' is not an engageable monster");
            return;
        }

        LastRevealAt = _now();
        LastRevealName = classified.ResolvedName;
        LastRevealLine = wireLine;
        LastHeldOffReason = null;
        _classifier.AppendArrivalEntity(classified, rawWireLine: wireLine);
        _log?.Info(LogCategory,
            $"dark-room {source} revealed name={classified.ResolvedName} — injected for auto-combat");
    }

    // THE LONGEST PREFIX THAT NAMES A MONSTER WINS. As far as the sentence goes,
    // "bugbear captain swings at you" could be a monster called "bugbear" that
    // "captain swings", so each prefix is tried and the longest match taken.
    private string? ResolveFromLine(string line)
    {
        string head = (line ?? string.Empty).TrimStart();
        foreach (string article in new[] { "The ", "the ", "A ", "An ", "an " })
        {
            if (head.StartsWith(article, StringComparison.Ordinal))
            {
                head = head[article.Length..];
                break;
            }
        }
        string[] words = head.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? best = null;
        for (int n = 1; n <= Math.Min(MaxNameWords, words.Length); n++)
        {
            string candidate = string.Join(' ', words, 0, n)
                .TrimEnd('!', '.', ',', ';', ':', '\'');
            if (candidate.Length == 0) continue;
            RoomEntity got = _classifier.Classify(candidate);
            if (got.Kind == EntityKind.Monster && got.MonsterNumber is not null)
                best = candidate;
        }
        return best;
    }

    private void OnTargetNotHere(MatchResult match)
    {
        if (match.Groups.Count > 0 && !_classifier.RefusalNamesMonster(match.Groups[0], _currentTarget())) return;
        OnTargetGone(match);
    }

    // Only a refused attack says the target left. The same line answers any command
    // that did nothing, so one echoed for something else (a cast) retracts nobody.
    private void OnCommandNoEffect(MatchResult match)
    {
        if (_currentTarget() is { Length: > 0 } target
            && _router.ReplyIsForCommandNotNaming(target)) return;
        OnTargetGone(match);
    }

    private void OnTargetGone(MatchResult _)
    {
        if (!_roomTracker.IsInDarkRoom) return;
        if (_currentTarget() is not { Length: > 0 } target) return;

        // The server refused our attack, so the target is not in the room: in the
        // dark it was injected off its attack line and has since died (no visible
        // death line reaches us) or fled. Dropping it re-fires EntitiesObserved
        // with the phantom gone, which clears CombatManager's target and — just
        // as importantly — lets AlreadyPresent above stop suppressing reveals, so
        // whatever is actually here can be found.
        if (_classifier.RemoveDeadEntity(target))
            _log?.Info(LogCategory,
                $"dark-room target '{target}' refused by the server — retracted");
        else
            _log?.Debug(LogCategory,
                $"server refused '{target}' but it wasn't in the room list");
    }

    private void Hold(string reason)
    {
        LastHeldOffReason = reason;
        _log?.Debug(LogCategory, "dark-room " + reason + " — not injecting");
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
        foreach (IDisposable sub in _subs) sub.Dispose();
        _subs.Clear();
    }
}
