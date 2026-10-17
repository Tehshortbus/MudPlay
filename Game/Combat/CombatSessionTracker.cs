using MudPlay.Game.Spells;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;

namespace MudPlay.Game.Combat;

// Aggregates our own combat into the CombatSessionStats the Session Stats panel
// displays: our swing accuracy (hit / miss / crit), the damage of our swings, procs
// and spells, the blows that hit us and how hard, what we avoided, and the per-round
// damage spread. Pure downstream subscriber — it never sends to the wire.
//
// Damage comes off the same round ledger that prints the combat round totals
// (RoundDamageTracker.Attributed), so the two always agree on whose damage a line
// was. Of the lines the ledger credits to us, one matching the caster message of a
// spell we know is that spell's (CasterMessageMatcher, refreshed via
// RefreshMatchers); one the ledger read as a weapon proc, or phrased "Your …", is a
// proc; the rest are our swings, which OnUserHits classifies. A spell or proc line
// never counts as a swing, so it never moves the hit / miss / crit denominators or
// the swing extents. Every damage line the ledger credits to someone hitting us is a
// blow taken; misses and dodges come from the fixed patterns.
//
// Backstabs keep their own rate: CombatManager reports each `bs` it sees answered
// (OnBackstabResolved). A stab answered without "surprise" failed; when the answer
// was a whiff, that miss is the stab's, not a regular attack's, so it leaves the
// normal miss count.
//
// Only the local player's own swings count toward the offensive figures, so
// OnUserHits requires the first-person "You" source (the same convention
// conversation.local uses to tell our own speech from another player's).
// Classification is keyword-driven on the raw line, per the MajorMUD line
// vocabulary: "surprise" ⇒ backstab, "critically" ⇒ crit (physical only — bash
// / smash / backstab / spells never crit), otherwise a plain hit.
//
// Threading: every mutation runs on the router's marshalled (UI) dispatch
// thread, as do RoundDamageTracker's events; the window VM reads Snapshot on the
// same thread. The counters are therefore lock-free, matching RoundDamageTracker.
public sealed class CombatSessionTracker : IDisposable
{
    private readonly IDisposable _userHitsSub;
    private readonly IDisposable _userMissesSub;
    private readonly IDisposable _userDodgesSub;
    private readonly IDisposable _mobMissesSub;
    private readonly IDisposable _combatStatusSub;
    private readonly RoundDamageTracker _rounds;

    // Our known spells' caster-message matchers, configured attack slots first,
    // refreshed on the data boundaries (see RefreshMatchers). A null resolver means no
    // spell recognition — our spell lines then count as procs or swings.
    private readonly Func<IReadOnlyList<SpellLineMatcher>>? _resolveSpellMatchers;
    private IReadOnlyList<SpellLineMatcher> _spellMatchers = Array.Empty<SpellLineMatcher>();

    // Per-spell damage per landed cast + resisted-cast count, keyed by spell name. A
    // landing is a recognised damage line, and a follow-up line (the spell the cast
    // chains to) adds to the cast it follows; Misses = a resisted cast (see
    // ResolvePendingSpellMiss). Ordered by first appearance for a stable display.
    private sealed class SpellAccum { public List<int> Casts = new(); public int Misses; }
    private readonly Dictionary<string, SpellAccum> _perSpell = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _spellOrder = new();
    private string? _lastSpell;

    private DamageTally _hit;
    private DamageTally _crit;
    private DamageTally _backstab;
    private DamageTally _round;
    private DamageTally _proc;
    private DamageTally _hitTaken;
    // The last counted miss may actually be a spell-cast EMOTE ("You scatter some
    // ashes in a sweeping motion!") — a self-emote ending in "!" the miss skeleton
    // can't tell from a real whiff. Set when a miss is counted; a spell that lands
    // right after retracts it (OnAttributed), and a physical hit / combat-off
    // resolves it (weapon whiff or resisted cast).
    private bool _emoteMissCandidate;
    // Any physical swing landed this combat — distinguishes a weapon whiff (keep the
    // miss) from a resisted spell cast (reattribute) at combat-off. Reset on Engaged.
    private bool _physicalHitThisCombat;
    private int _misses;
    private int _backstabFails;
    // Within one line's dispatch: the miss just counted, and a `bs` whiff reported
    // before OnUserMisses saw it — CombatManager and this tracker hear the line in
    // either order.
    private string? _lastMissLine;
    private string? _backstabWhiff;
    private int _mobMisses;
    private int _dodges;
    // True between "*Combat Engaged*" and "*Combat Off*" (re-asserted by any
    // hit / mob line). The UserMisses skeleton also matches self-emotes ending
    // in "!", so a miss is only counted while this is set — outside combat a
    // "You feel much better!" can't be a swing whiff. Any real swing-miss is
    // bracketed by combat, so this never suppresses a genuine miss.
    private bool _engaged;
    // The line the ledger pass just claimed as one of our spells or procs, so the
    // UserHits pass that follows for the same line doesn't count it as a swing too.
    private string? _recognizedLine;
    private readonly MessageRouter _router;
    private bool _disposed;

    // Raised after any observation updates the tallies, so the Session Stats VM
    // can refresh its bound figures. Fires on the dispatch thread; debouncing
    // (if wanted) is the subscriber's concern.
    public event Action? Changed;

    // Raised when the miss last counted is taken back because one of our spells
    // landed right after it: that "miss" was the spell's cast line, not a swing.
    // MonsterObservationTracker counts the same line against the monster being
    // fought and takes it back on this.
    //
    // Not raised for the guess made when a fight ends (ResolvePendingSpellMiss).
    // That one only moves a figure between two session totals; on a monster's saved
    // record it took a weapon user's real whiff away whenever a cast had lately gone
    // out. A cast that doesn't land therefore still reads as a missed swing there.
    public event Action? CastLineMissRetracted;

    // "Is this name a party member, or ourselves?" Null = none known, so nothing is skipped.
    private readonly Func<string, bool>? _isPartyMember;

    public CombatSessionTracker(
        MessageRouter router,
        RoundDamageTracker rounds,
        Func<IReadOnlyList<SpellLineMatcher>>? resolveSpellMatchers = null,
        Func<string, bool>? isPartyMember = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(rounds);
        _router = router;
        _rounds = rounds;
        _resolveSpellMatchers = resolveSpellMatchers;
        _isPartyMember = isPartyMember;

        _userHitsSub     = router.Subscribe(KnownPatterns.UserHits,     OnUserHits);
        _userMissesSub   = router.Subscribe(KnownPatterns.UserMisses,   OnUserMisses);
        _userDodgesSub   = router.Subscribe(KnownPatterns.UserDodges,   OnUserDodges);
        _mobMissesSub    = router.Subscribe(KnownPatterns.MobMisses,    OnMobMisses);
        _combatStatusSub = router.Subscribe(KnownPatterns.CombatStatus, OnCombatStatus);
        // The ledger reads a line on MessageRouter.LineDispatched, before the fixed
        // patterns, so a spell / proc line is claimed before OnUserHits sees it.
        _rounds.Attributed += OnAttributed;
        router.LineDispatched += OnLineStart;
        _rounds.RoundComplete += OnRoundComplete;

        RefreshMatchers();
    }

    // Re-resolve our spells' matchers from live game data. Called on the boundaries
    // that move them: connect / character switch, a Combat-tab edit, a game-data set
    // swap, and a spellbook change.
    public void RefreshMatchers()
        => _spellMatchers = _resolveSpellMatchers?.Invoke() ?? Array.Empty<SpellLineMatcher>();

    // The spells whose damage lines we recognise, configured attack slots first.
    public IReadOnlyList<string> RecognisedSpells
        => _spellMatchers.Where(m => !m.FollowUp).Select(m => m.Name).ToList();

    // True when line is the caster's-eye damage line of one of our spells.
    // RoundDamageTracker uses it to own a spell line that names no caster.
    public bool MatchesOwnSpell(string line) => SpellOf(line, out _) is not null;

    // True when that spell is one the game data scopes to the whole room.
    public bool MatchesOwnRoomSpell(string line) => SpellOf(line, out _) is { HitsRoom: true };

    private SpellLineMatcher? SpellOf(string line, out int damage)
    {
        foreach (SpellLineMatcher m in _spellMatchers)
            if (m.Matcher.TryMatchDamage(line, out damage)) return m;
        damage = 0;
        return null;
    }

    // Per-spell accumulator, created on first sight of a spell so the display keeps
    // configured-but-never-cast spells out until they actually fire.
    private SpellAccum Spell(string name)
    {
        if (!_perSpell.TryGetValue(name, out SpellAccum? a))
        {
            a = new SpellAccum();
            _perSpell[name] = a;
            _spellOrder.Add(name);
        }
        return a;
    }

    // A miss left un-retracted (no spell landed after it) in a combat with no
    // physical swing, right after we cast an attack spell, was a RESISTED cast, not a
    // whiff — move it from the physical-miss bucket to the resist count of the spell
    // we last landed, else our first configured attack spell.
    private void ResolvePendingSpellMiss()
    {
        if (_emoteMissCandidate && !_physicalHitThisCombat && _rounds.AttackCastLately
            && (_lastSpell ?? (_spellMatchers.Count > 0 ? _spellMatchers[0].Name : null)) is { } spell
            && _misses > 0)
        {
            _misses--;
            Spell(spell).Misses++;
            Changed?.Invoke();
        }
        _emoteMissCandidate = false;
    }

    private IReadOnlyList<SpellCombatStat> BuildSpellStats()
    {
        if (_spellOrder.Count == 0) return Array.Empty<SpellCombatStat>();
        List<SpellCombatStat> list = new(_spellOrder.Count);
        foreach (string name in _spellOrder)
        {
            SpellAccum a = _perSpell[name];
            if (a.Casts.Count == 0 && a.Misses == 0) continue;
            list.Add(new SpellCombatStat(
                name, a.Casts.Count, a.Misses,
                a.Casts.Count == 0 ? 0 : a.Casts.Min(), a.Casts.Count == 0 ? 0 : a.Casts.Max(),
                a.Casts.Sum(d => (long)d)));
        }
        return list;
    }

    // Point-in-time copy of the session's combat figures. The physical extent is
    // the min/max across whichever swing categories have landed; an empty
    // category contributes nothing.
    public CombatSessionStats Snapshot()
    {
        DamageTally spell = default;
        foreach (SpellAccum a in _perSpell.Values)
            foreach (int d in a.Casts) spell.Add(d);
        return new(
            Hits:                _hit.Count,
            Crits:               _crit.Count,
            Backstabs:           _backstab.Count,
            Misses:              _misses,
            BackstabFails:       _backstabFails,
            HitMinDamage:        _hit.Count == 0 ? 0 : _hit.Min,
            HitMaxDamage:        _hit.Max,
            HitTotalDamage:      _hit.Sum,
            CritMinDamage:       _crit.Count == 0 ? 0 : _crit.Min,
            CritMaxDamage:       _crit.Max,
            CritTotalDamage:     _crit.Sum,
            BackstabMinDamage:   _backstab.Count == 0 ? 0 : _backstab.Min,
            BackstabMaxDamage:   _backstab.Max,
            BackstabTotalDamage: _backstab.Sum,
            MobHits:             _hitTaken.Count,
            MobMisses:           _mobMisses,
            Dodges:              _dodges,
            HitTakenMinDamage:   _hitTaken.Count == 0 ? 0 : _hitTaken.Min,
            HitTakenMaxDamage:   _hitTaken.Max,
            HitTakenTotalDamage: _hitTaken.Sum,
            RoundsWithDamage:    _round.Count,
            RoundMinDamage:      _round.Count == 0 ? 0 : _round.Min,
            RoundMaxDamage:      _round.Max,
            RoundTotalDamage:    _round.Sum,
            ProcHits:            _proc.Count,
            ProcMinDamage:       _proc.Count == 0 ? 0 : _proc.Min,
            ProcMaxDamage:       _proc.Max,
            ProcTotalDamage:     _proc.Sum,
            SpellHits:           spell.Count,
            SpellMinDamage:      spell.Count == 0 ? 0 : spell.Min,
            SpellMaxDamage:      spell.Max,
            SpellTotalDamage:    spell.Sum,
            Spells:              BuildSpellStats());
    }

    // Zero every counter — called on the session boundary (connect / character
    // switch), matching RoundDamageTracker.Reset.
    public void Reset()
    {
        _hit = default;
        _crit = default;
        _backstab = default;
        _round = default;
        _proc = default;
        _hitTaken = default;
        _perSpell.Clear();
        _spellOrder.Clear();
        _lastSpell = null;
        _misses = 0;
        _backstabFails = 0;
        _lastMissLine = null;
        _backstabWhiff = null;
        _mobMisses = 0;
        _dodges = 0;
        _emoteMissCandidate = false;
        _physicalHitThisCombat = false;
        _recognizedLine = null;
        _engaged = false;
        Changed?.Invoke();
    }

    private void OnLineStart(LineExtractor.EmittedLine _)
    {
        _lastMissLine = null;
        _backstabWhiff = null;
    }

    // A `bs` we sent was answered by line. A landed stab is already counted by
    // OnUserHits off its "surprise"; a failed one counts against the backstab rate.
    public void OnBackstabResolved(string line, bool landed)
    {
        if (landed) return;
        _backstabFails++;
        if (string.Equals(line, _lastMissLine, StringComparison.Ordinal))
        {
            _misses--;
            _emoteMissCandidate = false;
            _lastMissLine = null;
        }
        else
        {
            _backstabWhiff = line;
        }
        Changed?.Invoke();
    }

    private void OnAttributed(AttributedLine line)
    {
        _recognizedLine = null;
        DamageAttribution sides = line.Sides;
        if (sides.NoDealer) return;

        if (sides.Target == DamageLineAttributor.Self)
        {
            _engaged = true; // an incoming blow means we're mid-combat
            _hitTaken.Add(sides.Amount);
            Changed?.Invoke();
            return;
        }
        if (sides.Source != DamageLineAttributor.Self) return;

        // One of our spells. Its cast line ("You cast … for N damage!") has the
        // first-person "You" source UserHits also matches, so it's claimed here.
        if (SpellOf(line.Text, out int dmg) is { } matched)
        {
            _recognizedLine = line.Text;
            List<int> casts = Spell(matched.Name).Casts;
            if (matched.FollowUp)
            {
                // The chained spell's line belongs to the cast it follows.
                if (casts.Count > 0) casts[^1] += dmg;
                Changed?.Invoke();
                return;
            }
            casts.Add(dmg);
            _lastSpell = matched.Name;
            // The cast's emote was just counted as a physical miss; a landed spell
            // means that "miss" was the emote — retract it so spell combat doesn't
            // inflate the miss count.
            if (_emoteMissCandidate && _misses > 0)
            {
                _misses--;
                CastLineMissRetracted?.Invoke();
            }
            _emoteMissCandidate = false;
            _recognizedLine = line.Text;
            Changed?.Invoke();
            return;
        }

        // Our swings all read "You …"; anything else of ours — a weapon's "Your weapon
        // sears …", or a proc naming only its victim — is a proc.
        if (line.Proc || line.Text.StartsWith("Your ", StringComparison.Ordinal))
        {
            _proc.Add(sides.Amount);
            _recognizedLine = line.Text;
            Changed?.Invoke();
        }
    }

    private void OnUserHits(MatchResult match)
    {
        // A line the ledger pass already claimed as our spell or proc isn't a swing.
        if (string.Equals(match.Text, _recognizedLine, StringComparison.Ordinal)) return;

        // KnownPatterns.UserHits also fires on "The {mob} {verb} you for N
        // damage!" and on another player's swing. Only OUR own first-person
        // "You" swings feed the player's accuracy figures.
        if (match.Groups.Count < 3 ||
            !string.Equals(match.Groups[0], "You", StringComparison.OrdinalIgnoreCase))
            return;

        if (!int.TryParse(match.Groups[2], out int dmg)) return;

        _engaged = true; // a landed swing means we're mid-combat
        string line = match.Text;
        if (line.Contains("surprise", StringComparison.OrdinalIgnoreCase))
            _backstab.Add(dmg);
        else if (line.Contains("critically", StringComparison.OrdinalIgnoreCase))
            _crit.Add(dmg);
        else
            _hit.Add(dmg);
        _emoteMissCandidate = false; // a real physical swing landed — not a spell round
        _physicalHitThisCombat = true;
        Changed?.Invoke();
    }

    private void OnUserMisses(MatchResult match)
    {
        // Already counted as the failed backstab it answered.
        if (string.Equals(match.Text, _backstabWhiff, StringComparison.Ordinal)) return;
        // A buff of ours a party member negated or resisted is no swing of ours.
        if (_isPartyMember is not null && NegatedCastLine.IsOwnCastAtPartyMember(match.Text, _isPartyMember)) return;
        // The miss skeleton also matches self-emotes ending in "!", so only a
        // line seen while combat is engaged is a real swing whiff.
        if (!_engaged) return;
        _misses++;
        _lastMissLine = match.Text;
        // This miss might be a spell-cast emote, not a whiff — a spell landing right
        // after retracts it (OnAttributed); an un-retracted one is resolved at
        // combat-off (weapon whiff kept, resisted cast reattributed).
        _emoteMissCandidate = true;
        Changed?.Invoke();
    }

    private void OnUserDodges(MatchResult _)
    {
        _engaged = true; // an incoming attack means we're mid-combat
        _dodges++;
        Changed?.Invoke();
    }

    private void OnMobMisses(MatchResult match)
    {
        // A dodge line satisfies MobMisses too (its "{mob} … at you" shape);
        // it's already counted by OnUserDodges, so skip it here to keep the
        // plain-miss tally and the dodge denominator from double-counting.
        if (match.Text.Contains("dodge", StringComparison.OrdinalIgnoreCase)) return;
        _engaged = true; // an incoming attack means we're mid-combat
        _mobMisses++;
        Changed?.Invoke();
    }

    private void OnCombatStatus(MatchResult match)
    {
        // (?<status>Engaged|Off) — arm the miss gate while engaged, disarm when
        // the server reports combat ended. Hits / mob lines re-arm it, so a
        // spurious mid-round "*Combat Off*" (the server emits one when we cast)
        // doesn't strand a following real swing-miss uncounted.
        if (match.Groups.Count == 0) return;
        bool engaged = string.Equals(match.Groups[0], "Engaged", StringComparison.OrdinalIgnoreCase);
        if (engaged)
            _physicalHitThisCombat = false; // fresh combat — no swing has landed yet
        else
            ResolvePendingSpellMiss();      // combat ended — reclassify a stuck emote-miss
        _engaged = engaged;
    }

    private void OnRoundComplete(RoundSummary summary)
    {
        // "Characters per round" damage = total damage WE dealt in the round;
        // skip rounds where we dealt nothing (pure-defence rounds) so they
        // don't drag the min to 0.
        if (summary.DamageDealt <= 0) return;
        _round.Add(summary.DamageDealt);
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _userHitsSub.Dispose();
        _userMissesSub.Dispose();
        _userDodgesSub.Dispose();
        _mobMissesSub.Dispose();
        _combatStatusSub.Dispose();
        _rounds.Attributed -= OnAttributed;
        _router.LineDispatched -= OnLineStart;
        _rounds.RoundComplete -= OnRoundComplete;
    }

    // Running count + damage extent for one swing category. Min/Max are only
    // meaningful once Count > 0; callers guard the empty case.
    private struct DamageTally
    {
        public int Count;
        public int Min;
        public int Max;
        public long Sum;

        public void Add(int damage)
        {
            if (Count == 0) { Min = damage; Max = damage; }
            else
            {
                if (damage < Min) Min = damage;
                if (damage > Max) Max = damage;
            }
            Count++;
            Sum += damage;
        }
    }
}
