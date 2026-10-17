using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;

namespace MudPlay.Game.Combat;

// Keeps a damage ledger per 5-second MajorMUD combat round — what every combatant in
// the room dealt and took, plus the damage no line names a side for — and emits it as
// a RoundSummary at each round boundary. Keeps a ring buffer of the last 50 rounds.
//
// A round's lines arrive as one burst and the next round's burst is ~5 s away, so a
// round closes once its lines have gone quiet for SettleWindow — its totals print
// right after its own lines, before the between-round cast that waits for HP to
// settle. The room clearing of hostiles closes it at once: the walker moves on in
// that same instant, and nothing is left to hit in the round. Local death closes it
// at once too (MarkCombatEnded), as does *Combat Off* when no scheduler is bound. TickEngine's CombatTickElapsed heartbeat (OnCombatTick) is the
// backstop when no scheduler is bound; it fires on the NEXT round's first combat
// line as well as on a 5 s timer, so it only closes a round at least MinTickAge
// old — never the one that line just opened.
//
// A damage shield's line (ShieldReflectLines) names only who it struck back at, so
// it goes to the other side of the hit just before it: the shield's wearer.
//
// Every "... for N damage!" line is read by DamageLineAttributor against the room
// roster (NoteRoomEntities) and the party. A caster's-eye spell line names no caster
// ("Acid sears the orc for 12 damage!" is also what everyone else sees), so it counts
// as ours only when it matches one of our own spells AND we sent a cast lately
// (NoteOwnCast). A weapon proc names only its victim too, but it
// lands right after the swing that set it off, so it goes to whoever just hit that same
// monster. Otherwise the dealer is unknown.
//
// Our room spell names no victim, only the room ("A hellish storm of fire and brimstone
// scorches your foes for 603 damage!"): every monster in the room at the time takes about that much,
// each adjusted by its own magic resistance, which no line shows (user, 2026-09-29) —
// so each is credited the full amount. A room spell of ours whose line says "your foe"
// is told by the spell's scope in the game data, not its wording. Someone else's room
// spell names nobody at all
// ("An acidic tempest blows through the room for 431 damage!"), but the line right
// before it is the caster's announce ("Midnight summons a powerful tempest into the
// room!"), so it's theirs, spread over the room's monsters the same way (report
// paradigm-20260929-161536).
//
// A damage line opens a round when we're in combat, when it's ours, or when it names
// both sides (someone else's fight in the room). Damage to us with no named source —
// a poison tick, a room hazard — joins an open round but doesn't start one between
// fights.
//
// Settings → Combat "Show combat round totals" prints the ledger on the terminal.
// The opt-in combat trace file is written here when shouldWriteTrace returns true
// on round close; it's queried per round so a mid-session toggle applies at once.
public sealed class RoundDamageTracker : IDisposable
{
    // LogService category — appears as [Round] rows on each closed round.
    public const string LogCategory = "Round";

    // Capacity of the in-memory ring buffer of recent rounds.
    public const int RingCapacity = 50;

    // FightRound restarts at 1 once the room is clear of hostiles (InCombat dropping),
    // or when a round opens this long after the previous one closed — a fight InCombat
    // never covered, like a party member's while we stood by. Back-to-back rounds of
    // one fight open within one round of each other.
    private static readonly TimeSpan FightGap = TimeSpan.FromSeconds(7);

    // How long a cast we sent can still own a caster's-eye spell line: the round it
    // lands in, with slack for the line arriving just after the tick.
    private static readonly TimeSpan OwnCastWindow = TimeSpan.FromSeconds(6);

    // How long after a hit its weapon proc can still land: the same burst.
    private static readonly TimeSpan ProcWindow = TimeSpan.FromSeconds(1);

    // Quiet after a round's last line that ends it (see the header): well past any gap
    // inside a burst, and under CastingDirector's 400 ms HP settle, so the totals come
    // before the between-round cast (report paradigm-20260928-232024).
    private static readonly TimeSpan SettleWindow = TimeSpan.FromMilliseconds(250);

    // The youngest round the combat tick may close.
    private static readonly TimeSpan MinTickAge = TimeSpan.FromSeconds(1);

    private readonly MessageRouter _router;
    private readonly PlayerState _state;
    private readonly LogService? _log;
    private readonly Func<bool> _shouldWriteTrace;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, Action, IDisposable>? _scheduleDelay;
    private IDisposable? _settleTimer;
    private DateTimeOffset _lastActivityAt;

    private readonly IDisposable _mobMissesSub;
    private readonly IDisposable _combatStatusSub;

    private readonly Queue<RoundSummary> _ring = new(RingCapacity);
    private readonly object _ringLock = new();

    // Room roster: every name a line may use for an occupant (RawName with its flavor
    // word, and the ResolvedName base form) mapped to the name the ledger shows.
    private Dictionary<string, string> _rosterNames = new(StringComparer.OrdinalIgnoreCase);
    // The room's monsters, one entry per monster, as the ledger shows them — whom our
    // room spell hits.
    private List<string> _foes = new();
    private Func<IEnumerable<string>>? _partyNames;
    private Func<string?>? _selfName;
    private Func<string, bool>? _isOwnSpellLine;
    private Func<string, bool>? _isOwnRoomSpellLine;
    private Func<bool>? _ownSpellRepeating;
    private DateTimeOffset _lastOwnCastAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastOwnAttackCastAt = DateTimeOffset.MinValue;
    // The last hit that named both sides — a proc right after it is the hitter's.
    private (string Source, string Target, DateTimeOffset At, string Line)? _lastHit;
    // The room's monsters by the name on their lines → game-data number.
    private Dictionary<string, int> _foeNumbers = new(StringComparer.OrdinalIgnoreCase);
    // The line before the one being read — a room spell's caster announce.
    private string? _previousLine;

    private DebugLogWriter? _trace;
    private RoundAccumulator? _current;
    private int _roundCounter;
    private int _fightRound;
    private bool _fightOver;
    private DateTimeOffset? _lastClosedAt;
    private bool _disposed;

    // Fired after each round closes, with the summary payload. Subscribers run on
    // the MessageRouter's marshalled thread.
    public event Action<RoundSummary>? RoundComplete;

    // Fired for each damage line with how the ledger read it (the line, then its
    // RoundTotalsFormatter.LedgerTag) — the Wire Inspector's Classified view.
    public event Action<string, string>? LineAttributed;

    // Fired for each damage line as the ledger credited it, whether or not it fell in
    // a round — MonsterHpTracker's running estimates and CombatSessionTracker's own
    // statistics.
    public event Action<AttributedLine>? Attributed;

    // Snapshot of the ring buffer, oldest first.
    public IReadOnlyList<RoundSummary> Recent
    {
        get { lock (_ringLock) { return _ring.ToArray(); } }
    }

    public RoundDamageTracker(
        MessageRouter router,
        PlayerState state,
        LogService? log = null,
        Func<bool>? shouldWriteTrace = null,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, Action, IDisposable>? scheduleDelay = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(state);
        _router = router;
        _state = state;
        _log   = log;
        _shouldWriteTrace = shouldWriteTrace ?? (static () => false);
        _now = clock ?? (static () => DateTimeOffset.Now);
        _scheduleDelay = scheduleDelay;

        _router.LineDispatched += OnLine;
        _state.PropertyChanged += OnStateChanged;
        // A monster swinging and missing is round activity with no damage line.
        _mobMissesSub    = router.Subscribe(KnownPatterns.MobMisses,   _ => { Current(_now()); NoteActivity(); });
        _combatStatusSub = router.Subscribe(KnownPatterns.CombatStatus, OnCombatStatus);
    }

    // Late-bound sources for naming combatants: the party's names, the local
    // character's own name (so the party list's copy of it isn't read as someone
    // else), and whether a line is one of our own spells.
    public void SetNameSources(Func<IEnumerable<string>> partyNames, Func<string?> selfName)
    {
        _partyNames = partyNames;
        _selfName = selfName;
    }

    // isOwnRoomSpellLine: the line is one of our spells that the game data scopes to
    // the room. Its wording may name one victim or none ("… ravages your foe for N
    // damage!" from spirit horde, report paradigm-20261006-051627), so the wording
    // test alone left its damage on nobody.
    public void SetOwnSpellLineCheck(Func<string, bool> isOwnSpellLine, Func<string, bool>? isOwnRoomSpellLine = null)
    {
        _isOwnSpellLine = isOwnSpellLine;
        _isOwnRoomSpellLine = isOwnRoomSpellLine;
    }

    // Whether the server is still repeating a spell we announced. It repeats every
    // round with no new cast from us, so the round after the first lands outside
    // OwnCastWindow and read as nobody's.
    public void SetOwnSpellRepeating(Func<bool> repeating) => _ownSpellRepeating = repeating;

    // Whether a damage line is a room's own damage, which falls anywhere in the
    // round (the round clock's test, wired by AppServices). Such a line is damage
    // taken in a round already open and never opens one or keeps one open: read as
    // a fight's it showed up as a round of its own two seconds after the real one.
    public void SetOffRoundDamageCheck(Func<string, bool> isOffRoundDamage) => _isOffRoundDamage = isOffRoundDamage;

    private Func<string, bool>? _isOffRoundDamage;

    // The room's monsters as MonsterHpTracker has them: the one a hit on a name lands on
    // (the first of that name) and every one of them, each with a stable id and its HP
    // estimate before the hit. Unbound, or a monster with no HP data, and damage counts
    // in full under the monster's name alone. capAtHp is Settings → Combat "Cap at
    // monster HP", read per line so a toggle applies at once; unbound, hits are capped.
    private Func<string, (int Id, int Hp)?>? _monsterTarget;
    private Func<IReadOnlyList<(int Id, string Name, int Hp)>>? _roomMonsters;
    private Func<bool> _capAtHp = static () => true;
    public void SetMonsterHp(
        Func<string, (int Id, int Hp)?> targetOf, Func<IReadOnlyList<(int Id, string Name, int Hp)>> roomMonsters,
        Func<bool>? capAtHp = null)
    {
        _monsterTarget = targetOf;
        _roomMonsters = roomMonsters;
        if (capAtHp is not null) _capAtHp = capAtHp;
    }

    // Whether a monster (by game-data number) carries a damage shield, the Shockshield
    // Message ability. Lets a struck-back line in wording never seen before go to the
    // monster just hit. Unbound, only the known wordings count.
    private Func<int, bool>? _wearsDamageShield;
    public void SetDamageShieldCheck(Func<int, bool> wears) => _wearsDamageShield = wears;

    // The room's occupants changed (a fresh "Also here:", an arrival, a death).
    public void NoteRoomEntities(RoomEntitiesObservation obs)
    {
        Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
        List<string> foes = new();
        Dictionary<string, int> numbers = new(StringComparer.OrdinalIgnoreCase);
        foreach (RoomEntity e in obs.Entities)
        {
            if (string.IsNullOrWhiteSpace(e.RawName)) continue;
            names[e.RawName] = e.RawName;
            if (e.Kind == EntityKind.Monster)
            {
                foes.Add(e.RawName);
                if (e.MonsterNumber is int number) numbers[e.RawName] = number;
            }
            if (!string.IsNullOrWhiteSpace(e.ResolvedName)) names.TryAdd(e.ResolvedName, e.RawName);
        }
        _rosterNames = names;
        _foes = foes;
        _foeNumbers = numbers;
        // Someone arriving mid-round is in the room for the rest of it.
        if (_current is { } round)
        {
            round.Seed(Names().Values);
            SeedMonsters(round);
        }
    }

    // We sent a cast (engine or typed), so a caster's-eye spell line in the next few
    // seconds may be ours. attack is false for a cast that costs no round energy (a
    // heal, a buff, a cure): it can't be what a missed swing really was.
    public void NoteOwnCast(bool attack = true)
    {
        _lastOwnCastAt = _now();
        if (attack) _lastOwnAttackCastAt = _lastOwnCastAt;
    }

    // We sent an attack spell recently enough that it may still be landing. A heal
    // going out between rounds doesn't count: it made a weapon user's only swing of
    // the round, a whiff, read as a resisted cast.
    public bool AttackCastLately => _now() - _lastOwnAttackCastAt <= OwnCastWindow;

    private void OnLine(LineExtractor.EmittedLine line)
    {
        // This runs for every line the server sends, on the UI thread, so everything
        // but a damage line leaves on one ordinal substring check; the roster and the
        // attributor only run for the few damage lines a round carries.
        // LineDispatched already leaves out other players' chat.
        if (line.IsPromptLine) return;
        string text = line.Text;
        string? previous = _previousLine;
        _previousLine = text;
        if (!text.Contains(" damage", StringComparison.Ordinal)) return;

        Dictionary<string, string> display = Names();
        if (!DamageLineAttributor.TryAttribute(text, display.Keys, out DamageAttribution a)) return;

        string? source = a.Source is null ? null : Display(a.Source, display);
        string? target = a.Target is null ? null : Display(a.Target, display);
        DateTimeOffset now = _now();
        bool ownSpell = false, proc = false, ownRoomSpell = false;
        if (source is null && !a.NoDealer && target != DamageLineAttributor.Self
            && (now - _lastOwnCastAt <= OwnCastWindow || _ownSpellRepeating?.Invoke() == true)
            && _isOwnSpellLine?.Invoke(text) == true)
        {
            source = DamageLineAttributor.Self;
            ownSpell = true;
            ownRoomSpell = target is null && _isOwnRoomSpellLine?.Invoke(text) == true;
        }
        // A weapon proc names only its victim ("Flames burn the orc", "The orc takes 3
        // damage from the cold!") and fires right after the swing that connected, so it
        // belongs to whoever just hit that same monster (report paradigm-20260929-043055).
        if (source is null && !a.NoDealer && target is not null && target != DamageLineAttributor.Self
            && _lastHit is { } hit && hit.Target.Equals(target, StringComparison.OrdinalIgnoreCase)
            && now - hit.At <= ProcWindow)
        {
            source = hit.Source;
            proc = true;
        }
        bool othersRoomSpell = false;
        if (source is null && target is null && !a.NoDealer && _foes.Count > 0 && HitsTheRoom(text)
            && AnnouncedBy(previous, display) is { } caster)
        {
            source = caster;
            othersRoomSpell = true;
        }
        // A damage shield strikes back at whoever just hit its wearer, and its line
        // doesn't say whose shield: "wraith weed is scorched for 4 damage!" right after
        // the weed hit us is ours; "You are scorched …" right after we hit a monster is
        // that monster's (report: a priest's hellfire shield credited to unknown).
        // "You are scorched …" reads like a condition's damage with no attacker; a
        // shield's is the struck monster's.
        // A shield whose wording isn't known still shows itself when the monster just hit
        // is one the game data gives a shield and this line directly follows that hit.
        bool reflect = false;
        if (source is null && target is not null
            && _lastHit is { } struck && now - struck.At <= ProcWindow
            && (ShieldReflectLines.IsReflect(text)
                || (string.Equals(previous, struck.Line, StringComparison.Ordinal)
                    && ShieldReflectLines.HasStruckBackShape(text)
                    && _foeNumbers.TryGetValue(struck.Target, out int struckNumber)
                    && _wearsDamageShield?.Invoke(struckNumber) == true)))
        {
            if (target == DamageLineAttributor.Self ? struck.Source == DamageLineAttributor.Self
                    : struck.Source.Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                source = struck.Target;
                reflect = true;
                a = a with { NoDealer = false };
            }
        }
        if (source is not null && target is not null && !a.NoDealer && !reflect) _lastHit = (source, target, now, text);

        bool roomSpell = (source == DamageLineAttributor.Self || othersRoomSpell) && target is null && !a.NoDealer
            && _foes.Count > 0 && (ownRoomSpell || HitsTheRoom(text));
        IReadOnlyList<string> foesHit = roomSpell ? _foes : Array.Empty<string>();

        // Worked out before Attributed, whose MonsterHpTracker takes the hit off the HP
        // it caps against.
        List<(string Row, int? Id, int Taken, int Other)> hits = Hits(target, a.Amount, foesHit.Count > 0);
        int? dealerId = !a.NoDealer && source is not null && IsFoe(source)
            ? _monsterTarget?.Invoke(source)?.Id : null;

        bool offRound = _isOffRoundDamage?.Invoke(text) == true;
        bool opens = !offRound
            && (_state.InCombat
                || source == DamageLineAttributor.Self
                || (source is not null && target is not null));
        Attributed?.Invoke(new AttributedLine(text,
            new DamageAttribution(a.NoDealer ? null : source, target, a.Amount, a.NoDealer), ownSpell, proc, foesHit.Count));
        bool counted = _current is not null || opens;
        if (LineAttributed is { } attributed)
            attributed(text, RoundTotalsFormatter.LedgerTag(a.NoDealer ? null : source, target, a.Amount, counted, a.NoDealer, foesHit.Count));
        if (!counted) return;

        RoundAccumulator round = Current(now);
        if (!offRound) NoteActivity();
        // Damage nobody dealt (a poison tick) is only damage taken. The dealer dealt what
        // the victims could take.
        // Each number is kept twice: as the cap setting counts it, and the other way
        // (Other), so a reader with its own cap choice has both to pick from.
        int dealt = hits.Count > 0 ? hits.Sum(h => h.Taken) : a.Amount;
        int dealtOther = hits.Count > 0 ? hits.Sum(h => h.Other) : a.Amount;
        if (!a.NoDealer)
        {
            if (source is null)
            {
                round.UnknownDealt += dealt;
                round.UnknownDealtOther += dealtOther;
            }
            else
            {
                round.For(source).AddDealt(dealt, dealtOther);
                if (dealerId is { } did) round.Monster(did, source).AddDealt(dealt, dealtOther);
            }
        }
        if (hits.Count == 0) round.UnknownTaken += a.Amount;
        foreach ((string row, int? id, int taken, int other) in hits)
        {
            round.For(row).AddTaken(taken, other);
            if (id is { } mid) round.Monster(mid, row).AddTaken(taken, other);
        }
    }

    // Who took a line's damage, and how much. A monster can't take more than the HP it
    // has left: a hit past it (an 812 room spell on a 540-HP muckworm) counts only what
    // killed it (user, 2026-09-29) — when the user has the cap on; off, the line's own
    // number counts, so a killing blow reads as big as the game printed it. HP it
    // regained — regen, a heal — is in the estimate already. An estimate already at 0
    // that still takes a hit was wrong, so that hit counts in full. A room spell hits
    // each monster once, matched to the tracker's monsters of each name in order.
    private List<(string Row, int? Id, int Taken, int Other)> Hits(string? target, int amount, bool roomSpell)
    {
        List<(string, int?, int, int)> hits = new();
        bool cap = _capAtHp();
        // What a tracked monster took by the cap setting, and what it would have by
        // the opposite one.
        (int Taken, int Other) Split(int hpLeft)
        {
            int capped = Capped(amount, hpLeft);
            return cap ? (capped, amount) : (amount, capped);
        }
        if (roomSpell)
        {
            Dictionary<string, Queue<(int Id, int Hp)>> tracked = new(StringComparer.OrdinalIgnoreCase);
            foreach ((int id, string name, int hp) in _roomMonsters?.Invoke() ?? Array.Empty<(int, string, int)>())
            {
                if (!tracked.TryGetValue(name, out Queue<(int, int)>? q)) tracked[name] = q = new();
                q.Enqueue((id, hp));
            }
            foreach (string foe in _foes)
                hits.Add(tracked.TryGetValue(foe, out Queue<(int Id, int Hp)>? q) && q.Count > 0 && q.Dequeue() is var m
                    ? (foe, m.Id, Split(m.Hp).Taken, Split(m.Hp).Other)
                    : (foe, null, amount, amount));
        }
        else if (target is not null)
        {
            hits.Add(IsFoe(target) && _monsterTarget?.Invoke(target) is { } m
                ? (target, m.Id, Split(m.Hp).Taken, Split(m.Hp).Other)
                : (target, null, amount, amount));
        }
        return hits;
    }

    private static int Capped(int amount, int hpLeft) => hpLeft > 0 ? Math.Min(amount, hpLeft) : amount;

    private bool IsFoe(string name)
        => name != DamageLineAttributor.Self && _foes.Contains(name, StringComparer.OrdinalIgnoreCase);

    // Every tracked monster in the room gets its own row in the round, even at zero,
    // and each name its most-seen count.
    private void SeedMonsters(RoundAccumulator round)
    {
        round.NoteCounts(_foes);
        foreach ((int id, string name, _) in _roomMonsters?.Invoke() ?? Array.Empty<(int, string, int)>())
            round.Monster(id, name);
    }

    // A room spell's line names the whole room, not a victim: "scorches your foes",
    // "drains your enemies", "You cast swarm on the room". A swing at someone missing
    // from the roster names no victim we know either, so the wording decides.
    private static bool HitsTheRoom(string text)
        => text.Contains("your foes", StringComparison.OrdinalIgnoreCase)
           || text.Contains("your enemies", StringComparison.OrdinalIgnoreCase)
           || text.Contains("the room", StringComparison.OrdinalIgnoreCase);

    // The player a room spell's announce line opens with ("Midnight summons a powerful
    // tempest into the room!") — never a monster, whose room spell would hit us.
    private string? AnnouncedBy(string? announce, Dictionary<string, string> display)
    {
        if (announce is null || !HitsTheRoom(announce)) return null;
        string? caster = null;
        foreach (string name in display.Keys)
            if (name.Length > (caster?.Length ?? 0)
                && announce.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase))
                caster = name;
        if (caster is null) return null;
        string shown = display[caster];
        return _foes.Contains(shown, StringComparer.OrdinalIgnoreCase) ? null : shown;
    }

    // Everyone a line may name: the room roster plus the party, minus ourselves.
    private Dictionary<string, string> Names()
    {
        string? self = _selfName?.Invoke();
        Dictionary<string, string> names = new(_rosterNames, StringComparer.OrdinalIgnoreCase);
        if (_partyNames is not null)
        {
            foreach (string full in _partyNames())
            {
                string given = GivenName(full);
                if (given.Length == 0) continue;
                if (self is not null && given.Equals(GivenName(self), StringComparison.OrdinalIgnoreCase)) continue;
                names.TryAdd(given, given);
            }
        }
        if (self is not null) names.Remove(GivenName(self));
        return names;
    }

    // Us, a member of our party, a monster in the room, or anyone else (another player).
    private CombatantKind KindOf(string name)
    {
        if (name == DamageLineAttributor.Self) return CombatantKind.Self;
        if (_partyNames is not null)
        {
            string? self = _selfName?.Invoke();
            foreach (string member in _partyNames())
            {
                string given = GivenName(member);
                if (self is not null && given.Equals(GivenName(self), StringComparison.OrdinalIgnoreCase)) continue;
                if (given.Equals(name, StringComparison.OrdinalIgnoreCase)) return CombatantKind.Party;
            }
        }
        foreach (string foe in _foes)
            if (foe.Equals(name, StringComparison.OrdinalIgnoreCase)) return CombatantKind.Monster;
        return CombatantKind.Player;
    }

    private static string Display(string name, Dictionary<string, string> display)
        => name == DamageLineAttributor.Self ? name
            : display.TryGetValue(name, out string? shown) ? shown : name;

    private static string GivenName(string name)
    {
        string t = name.Trim();
        int space = t.IndexOf(' ');
        return space < 0 ? t : t[..space];
    }

    // *Combat Off* comes mid-burst — on a kill, the other monsters' swings of the same
    // round follow it (report paradigm-20260928-230456) — so with the settle timer
    // bound the round is left to close on its own quiet. Without one it closes here.
    // The room is clear of hostiles: the round ends now, and the next is a new fight's
    // first.
    private void OnStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlayerState.InCombat) || _state.InCombat) return;
        _fightOver = true;
        if (_current is not null) CloseCurrent(_now());
    }

    private void OnCombatStatus(MatchResult match)
    {
        if (_scheduleDelay is not null || match.Groups.Count == 0) return;
        string status = match.Groups[0];
        if (string.Equals(status, "Off", StringComparison.OrdinalIgnoreCase))
            MarkCombatEnded();
    }

    // The open round, opening a fresh one when none is in flight. Every combatant
    // in the room gets a row up front, so the totals list them even at zero.
    private RoundAccumulator Current(DateTimeOffset now)
    {
        if (_current is { } open)
        {
            open.Engaged |= _state.InCombat;
            return open;
        }
        if (_fightOver || _lastClosedAt is not { } closed || now - closed > FightGap) _fightRound = 0;
        _fightOver = false;
        _fightRound++;
        _current = new RoundAccumulator
        {
            FightRound = _fightRound,
            StartedAt  = now,
            HpStart    = _state.Hp,
            MaStart    = _state.Ma,
            Engaged    = _state.InCombat,
        };
        _current.For(DamageLineAttributor.Self);
        _current.Seed(Names().Values);
        SeedMonsters(_current);
        return _current;
    }

    // A line belonging to the open round: restart the quiet clock that ends it. One
    // timer per round — when it fires early it re-arms for the rest of the window.
    private void NoteActivity()
    {
        _lastActivityAt = _now();
        if (_settleTimer is null && _scheduleDelay is not null)
            _settleTimer = _scheduleDelay(SettleWindow, OnSettle);
    }

    private void OnSettle()
    {
        _settleTimer?.Dispose();
        _settleTimer = null;
        if (_current is null) return;
        TimeSpan quiet = _now() - _lastActivityAt;
        if (quiet < SettleWindow && _scheduleDelay is not null)
        {
            _settleTimer = _scheduleDelay(SettleWindow - quiet, OnSettle);
            return;
        }
        CloseCurrent(_now());
    }

    // The combat heartbeat (TickEngine.CombatTickElapsed), the backstop close. It
    // also fires on the next round's first line, after that line opened its round,
    // so a round younger than MinTickAge is left open.
    public void OnCombatTick()
    {
        if (_current is not null && _now() - _current.StartedAt >= MinTickAge)
            CloseCurrent(_now());
    }

    // Explicitly close the current round (no-op when none is open) — local death,
    // and *Combat Off* when no settle scheduler is bound.
    public void MarkCombatEnded()
    {
        if (_current is null) return;
        CloseCurrent(_now());
    }

    // Reset the ring buffer + round counter. Called on connect to BBS / character
    // switch — same boundary CombatSessionTracker uses for its session
    // aggregates.
    public void Reset()
    {
        _settleTimer?.Dispose();
        _settleTimer = null;
        _current = null;
        _roundCounter = 0;
        _fightRound = 0;
        _fightOver = false;
        _lastClosedAt = null;
        lock (_ringLock) _ring.Clear();
    }

    private void CloseCurrent(DateTimeOffset endedAt)
    {
        _settleTimer?.Dispose();
        _settleTimer = null;
        if (_current is null) return;
        _roundCounter++;
        RoundSummary summary = new(
            RoundNumber:  _roundCounter,
            FightRound:   _current.FightRound,
            StartedAt:    _current.StartedAt,
            EndedAt:      endedAt,
            Combatants:   _current.Rows(KindOf),
            EachMonster:  _current.MonsterRows(),
            UnknownDealt: _current.UnknownDealt,
            Capped:       _capAtHp(),
            UnknownDealtOther: _current.UnknownDealtOther,
            UnknownTaken: _current.UnknownTaken,
            HpBefore:     _current.HpStart,
            HpAfter:      _state.Hp,
            MaBefore:     _current.MaStart,
            MaAfter:      _state.Ma,
            Engaged:      _current.Engaged);

        lock (_ringLock)
        {
            if (_ring.Count >= RingCapacity) _ring.Dequeue();
            _ring.Enqueue(summary);
        }
        _current = null;
        _lastClosedAt = endedAt;

        (string dealt, string taken) = RoundTotalsFormatter.Format(summary);
        _log?.Combat(LogCategory,
            $"n={summary.RoundNumber} {dealt} {taken} hpAfter={summary.HpAfter} maAfter={summary.MaAfter}");

        if (_shouldWriteTrace())
        {
            _trace ??= new DebugLogWriter("combat");
            _trace.WriteLine(
                $"round n={summary.RoundNumber} " +
                $"startedAt={summary.StartedAt:HH:mm:ss.fff} " +
                $"endedAt={summary.EndedAt:HH:mm:ss.fff} " +
                $"{dealt} {taken} " +
                $"hp={summary.HpBefore}->{summary.HpAfter} " +
                $"ma={summary.MaBefore}->{summary.MaAfter}");
        }
        else if (_trace is not null)
        {
            // The Log pane's diagnostics switch went off mid-session — close
            // the open trace file. Re-opens automatically when it
            // flips back on.
            _trace.Dispose();
            _trace = null;
        }

        RoundComplete?.Invoke(summary);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _router.LineDispatched -= OnLine;
        _state.PropertyChanged -= OnStateChanged;
        _mobMissesSub.Dispose();
        _combatStatusSub.Dispose();
        _settleTimer?.Dispose();
        _trace?.Dispose();
    }

    private sealed class RoundAccumulator
    {
        // Rows in first-seen order, keyed by display name.
        private readonly Dictionary<string, Row> _rows = new(StringComparer.OrdinalIgnoreCase);

        public int FightRound;
        public DateTimeOffset StartedAt;
        public int UnknownDealt;
        public int UnknownDealtOther;
        public int UnknownTaken;
        public int HpStart;
        public int MaStart;
        // Kept once set: the kill that clears the room drops InCombat before the
        // round closes.
        public bool Engaged;

        public Row For(string name)
        {
            if (!_rows.TryGetValue(name, out Row? row)) _rows[name] = row = new Row(name);
            return row;
        }

        public void Seed(IEnumerable<string> names)
        {
            foreach (string name in names) For(name);
        }

        // Each monster on its own row, by id: stable while it's in the room.
        private readonly SortedDictionary<int, Row> _monsters = new();
        // The most of each name in the room at once this round.
        private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

        public Row Monster(int id, string name)
        {
            if (!_monsters.TryGetValue(id, out Row? row)) _monsters[id] = row = new Row(name);
            return row;
        }

        public void NoteCounts(IEnumerable<string> foes)
        {
            foreach (IGrouping<string, string> g in foes.GroupBy(f => f, StringComparer.OrdinalIgnoreCase))
                _counts[g.Key] = Math.Max(_counts.GetValueOrDefault(g.Key), g.Count());
        }

        public IReadOnlyList<CombatantDamage> Rows(Func<string, CombatantKind> kindOf)
            => _rows.Values.Select(r => new CombatantDamage(r.Name, r.Dealt, r.Taken, kindOf(r.Name),
                Math.Max(1, _counts.GetValueOrDefault(r.Name)), r.DealtOther, r.TakenOther)).ToArray();

        // Same-named monsters are numbered in the order they were seen: "muckworm #1",
        // "muckworm #2"; a lone one keeps its plain name.
        public IReadOnlyList<CombatantDamage> MonsterRows()
        {
            List<CombatantDamage> rows = new();
            foreach (IGrouping<string, Row> g in _monsters.Values.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                int n = 0, total = g.Count();
                foreach (Row r in g)
                    rows.Add(new CombatantDamage(total > 1 ? $"{r.Name} #{++n}" : r.Name, r.Dealt, r.Taken, CombatantKind.Monster,
                        DealtOther: r.DealtOther, TakenOther: r.TakenOther));
            }
            return rows;
        }
    }

    private sealed class Row(string name)
    {
        public string Name { get; } = name;
        public int Dealt;
        public int Taken;
        // The same two tallies under the opposite cap choice.
        public int DealtOther;
        public int TakenOther;

        public void AddDealt(int amount, int other)
        {
            Dealt += amount;
            DealtOther += other;
        }

        public void AddTaken(int amount, int other)
        {
            Taken += amount;
            TakenOther += other;
        }
    }
}
