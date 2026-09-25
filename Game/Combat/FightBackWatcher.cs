using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Combat;

// Swing back when something is hurting us and nothing else has engaged it.
//
// WHY THIS EXISTS. Every other route into combat learns the monster's name from
// a ROOM line — the "Also here:" roster, the arrival notice — and each is one
// wording away from silence. Two rounds of that were reported in a row: a
// pursuing monster whose arrival notice the catalogue could not parse, and then,
// with that fixed and shipped, a bugbear captain that beat on an idle client for
// an hour ("for the next hour it is fighting me without me doing anything").
//
// This route reads the attacker off the ATTACK ITSELF, which is the one line
// that cannot be missing while we are being hit.
//
// IT ALWAYS NAMES A TARGET. An earlier draft sent the attack command bare and
// let the server pick whatever was in the room. The project owner rejected that
// — "the blind 'a' should not work by itself, it should require a target
// always" — and it deserved rejecting: a bare swing has no idea what it is
// hitting, so one misfire in a shop opens on a shopkeeper. The attacker is
// resolved to a real monster FIRST and handed to the classifier, so
// CombatManager engages it the ordinary way: by name, through the same
// hostility filter and Target Order a lit-room "Also here:" monster goes
// through. A non-hostile that somehow trips a trigger is dropped by that filter
// instead of being swung at, and nothing here can invent a target the room does
// not have.
//
// THREE TRIGGERS:
//
//   damage    "... you for N damage!", article-free. A named monster gets no
//             article — the game prints "Goru-Nezar swings at you!" — so the
//             article-bound MobHits missed 240 paradigm bosses and 128 stock
//             ones. Also catches damage whose line names no attacker at all.
//   swings    the same source swinging at us TWICE inside one round, landed or
//             not. The reported capture is page after page of misses around a
//             single landed blow, so waiting for damage costs rounds. A monster
//             names itself on every swing; an emote says its line once. That
//             repetition is the whole safety margin, because "The barmaid smiles
//             at you." fits the swing shape exactly.
//   HP drop   the prompt's HP fell while nothing is engaged. This one CANNOT
//             name anybody, so it never injects: it re-offers the roster we
//             already hold, which makes CombatManager re-pick among monsters it
//             already knows are here. With an empty roster it does nothing,
//             which is right — there is nobody to name.
//
// Everything is gated on the auto-combat master switch, so a client with the
// engine off still sits there and takes it, which is what "off" means.
public sealed class FightBackWatcher : IDisposable
{
    // LogService category — one row per reveal, and one when we hold off.
    public const string LogCategory = "FightBack";

    // One server round. A beating that spans rounds gets at most one reveal per
    // round; several lines inside the SAME round get one between them.
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(4);

    // How close together two swings from the same source must fall before we
    // read them as a fight rather than as room chatter. One round, so the
    // reported session — whose monster swung twice per round — qualifies on its
    // first round.
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(6);

    // Longest monster name we will try to read off the head of a line. "A tall
    // elite orc guard" is four words past its article; six is slack.
    private const int MaxNameWords = 6;

    private readonly RoomEntityClassifier _classifier;
    private readonly Func<bool> _isEnabled;
    private readonly Func<string?> _currentTarget;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private readonly List<IDisposable> _subs = new();

    // The server's own word on whether we are fighting: "*Combat Engaged*" /
    // "*Combat Off*". Authoritative and cheaper than inferring it.
    private bool _engaged;
    private DateTimeOffset _lastAction = DateTimeOffset.MinValue;
    private int _lastHp = -1;
    private string _lastSwinger = "";
    private DateTimeOffset _lastSwingAt = DateTimeOffset.MinValue;
    private bool _disposed;

    public FightBackWatcher(
        MessageRouter router,
        RoomEntityClassifier classifier,
        Func<bool> isEnabled,
        Func<string?> currentTarget,
        Func<DateTimeOffset>? now = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(currentTarget);
        _classifier = classifier;
        _isEnabled = isEnabled;
        _currentTarget = currentTarget;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _log = log;

        _subs.Add(router.Subscribe(KnownPatterns.IncomingDamage, OnDamage));
        _subs.Add(router.Subscribe(KnownPatterns.IncomingAttack, OnSwing));
        _subs.Add(router.Subscribe(KnownPatterns.CombatStatus, OnCombatStatus));
        _subs.Add(router.Subscribe(KnownPatterns.StatusLine, OnStatusLine));
    }

    private void OnCombatStatus(MatchResult match)
    {
        string status = match.Groups.Count > 0 ? match.Groups[0] : string.Empty;
        _engaged = status.Equals("Engaged", StringComparison.OrdinalIgnoreCase);
    }

    // Something took health off us, and the line usually names it.
    private void OnDamage(MatchResult match) => Reveal(match.Text, "took damage");

    // A swing, landed or not. ONE is not evidence: the shape it matches is the
    // shape of "The barmaid smiles at you." too. The SAME source swinging again
    // inside a round is, because that is what a fight looks like.
    private void OnSwing(MatchResult match)
    {
        string who = (match.Groups.Count > 0 ? match.Groups[0] : "").Trim();
        if (who.Length == 0) return;
        DateTimeOffset now = _now();
        bool again = now - _lastSwingAt <= RepeatWindow
                     && who.Equals(_lastSwinger, StringComparison.OrdinalIgnoreCase);
        _lastSwinger = who;
        _lastSwingAt = now;
        if (again) Reveal(match.Text, $"{who} keeps swinging at us");
    }

    // HP fell and we are not fighting. There is no attacker in a prompt, so this
    // never invents one: it re-offers the roster we already hold so the engine
    // re-picks among monsters it already knows are in the room.
    private void OnStatusLine(MatchResult match)
    {
        if (match.Groups.Count == 0) return;
        if (!int.TryParse(match.Groups[0], out int hp)) return;

        int previous = _lastHp;
        _lastHp = hp;
        if (previous < 0 || hp >= previous) return;      // first read, or healing
        if (hp <= 0) return;                             // dead / mortally wounded
        if (!Ready()) return;
        if (!AnyMonsterKnownHere())
        {
            _log?.Debug(LogCategory,
                $"HP {previous} -> {hp} while idle, but the room list is empty — "
              + "nothing to name, so nothing to attack");
            return;
        }
        _lastAction = _now();
        _log?.Info(LogCategory,
            $"HP {previous} -> {hp} while idle with a monster already known here "
          + "— re-offering the room so the engine picks a target");
        _classifier.ReemitCurrent();
    }

    // Resolve the attacker from the line and hand it to the classifier, which is
    // what gets CombatManager to engage it BY NAME.
    private void Reveal(string wireLine, string why)
    {
        if (!Ready()) return;
        if (ResolveAttacker(wireLine) is not { } attacker)
        {
            // Nothing we can name. Deliberately silent rather than swinging at
            // whatever is there: see the class note.
            _log?.Debug(LogCategory,
                $"{why}, but no known monster could be read off '{wireLine}' — "
              + "holding off rather than attacking blind");
            return;
        }
        if (AlreadyPresent(attacker)) return;

        _lastAction = _now();
        _classifier.AppendArrivalEntity(attacker, rawWireLine: wireLine);
        _log?.Info(LogCategory,
            $"{why}; revealed {attacker.ResolvedName} from its attack line so the "
          + "engine can engage it by name");
    }

    // The gates every trigger shares.
    private bool Ready()
    {
        if (!_isEnabled()) return false;
        if (_engaged) return false;
        if (_currentTarget() is { Length: > 0 }) return false;
        return _now() - _lastAction >= Cooldown;
    }

    // THE LONGEST PREFIX THAT NAMES A MONSTER WINS. An attack line is the
    // attacker's name followed by its own verb phrase out of the .mdb, and
    // nothing in the sentence marks the boundary: as far as a regex knows,
    // "bugbear captain swings at you" could be a monster called "bugbear" that
    // "captain swings". So each prefix is offered to the classifier and the
    // longest one it recognises is taken — "bugbear captain", not "bugbear".
    private RoomEntity? ResolveAttacker(string line)
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
        RoomEntity? best = null;
        for (int n = 1; n <= Math.Min(MaxNameWords, words.Length); n++)
        {
            // Trailing punctuation belongs to the sentence, not to the name.
            string candidate = string.Join(' ', words, 0, n)
                .TrimEnd('!', '.', ',', ';', ':', '\'');
            if (candidate.Length == 0) continue;
            RoomEntity got = _classifier.Classify(candidate);
            if (got.Kind == EntityKind.Monster && got.MonsterNumber is not null)
                best = got;
        }
        return best;
    }

    private bool AlreadyPresent(RoomEntity attacker)
    {
        if (_classifier.Current is not { } cur) return false;
        foreach (RoomEntity e in cur.Entities)
        {
            if (e.Kind != EntityKind.Monster) continue;
            if (string.Equals(e.ResolvedName, attacker.ResolvedName,
                              StringComparison.OrdinalIgnoreCase)
             || string.Equals(e.RawName, attacker.RawName,
                              StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private bool AnyMonsterKnownHere()
    {
        if (_classifier.Current is not { } cur) return false;
        foreach (RoomEntity e in cur.Entities)
            if (e.Kind == EntityKind.Monster) return true;
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
