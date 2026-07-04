using FujinTerm.Game.Spells;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;

namespace FujinTerm.Game.Combat;

// Aggregates the session's combat lines into the CombatSessionStats the Session
// Stats panel displays: the player's swing accuracy (hit / miss / crit),
// physical and backstab damage extents, incoming-attack defence (mob hits /
// misses / dodges), the per-round damage spread, and two game-data-driven rows
// the fixed regex patterns can't see — the configured attack spell cast and the
// equipped weapon's proc. Pure downstream subscriber — it never sends to the
// wire, only observes MessageRouter lines and RoundDamageTracker.RoundComplete.
//
// Spell / proc recognition is keyed off the game-data caster message
// (CasterMessageMatcher), resolved from the configured attack-spell slots and
// the worn weapon and refreshed on the data boundaries via RefreshMatchers.
// Both are observed on MessageRouter.LineDispatched (which runs before the fixed
// patterns) so a recognised line vetoes the physical-swing classifier in
// OnUserHits — a spell / proc is never double-counted as a melee swing, and
// never moves the hit / miss / crit denominators or the physical extent. Its
// damage still reaches the per-round total through RoundDamageTracker's own
// subscription. A weapon proc only counts when the previous offensive line was
// a landed swing, matching the MajorMUD rule that a proc fires after a basic
// attack connects.
//
// Only the local player's own swings count toward the offensive figures, so
// OnUserHits requires the first-person "You" source (the same convention
// conversation.local uses to tell our own speech from another player's).
// Classification is keyword-driven on the raw line, per the MajorMUD line
// vocabulary: "surprise" ⇒ backstab, "critically" ⇒ crit (physical only — bash
// / smash / backstab / spells never crit), otherwise a plain hit.
//
// Threading: every mutation runs on the router's marshalled (UI) dispatch
// thread, as does RoundDamageTracker.RoundComplete; the window VM reads Snapshot
// on the same thread. The counters are therefore lock-free, matching
// RoundDamageTracker.
public sealed class CombatSessionTracker : IDisposable
{
    private readonly MessageRouter _router;
    private readonly IDisposable _userHitsSub;
    private readonly IDisposable _userMissesSub;
    private readonly IDisposable _userDodgesSub;
    private readonly IDisposable _mobHitsSub;
    private readonly IDisposable _mobMissesSub;
    private readonly IDisposable _combatStatusSub;
    private readonly RoundDamageTracker _rounds;

    // Game-data-driven recognisers, resolved from live config + inventory and
    // refreshed on the data boundaries (see RefreshMatchers). Null delegates =>
    // no recognition (the proc / spell rows stay zero), so the tracker still
    // works standalone with only the fixed-pattern offensive figures.
    private readonly Func<IReadOnlyList<CasterMessageMatcher>>? _resolveSpellMatchers;
    private readonly Func<CasterMessageMatcher?>? _resolveProcMatcher;
    private IReadOnlyList<CasterMessageMatcher> _spellMatchers = Array.Empty<CasterMessageMatcher>();
    private CasterMessageMatcher? _procMatcher;

    private DamageTally _hit;
    private DamageTally _crit;
    private DamageTally _backstab;
    private DamageTally _round;
    private DamageTally _proc;
    private DamageTally _spell;
    private int _misses;
    private int _mobHits;
    private int _mobMisses;
    private int _dodges;
    // A proc fires only after a basic swing connects; set when one lands, and
    // consumed (cleared) by the proc it precedes or cleared by a whiff.
    private bool _lastWasLandedSwing;
    // True between "*Combat Engaged*" and "*Combat Off*" (re-asserted by any
    // hit / mob line). The UserMisses skeleton also matches self-emotes ending
    // in "!", so a miss is only counted while this is set — outside combat a
    // "You feel much better!" can't be a swing whiff. Any real swing-miss is
    // bracketed by combat, so this never suppresses a genuine miss.
    private bool _engaged;
    // Set on the LineDispatched pass when the current line is claimed as a
    // configured spell-cast or a weapon proc, so the later OnUserHits doesn't
    // double-count it as a physical swing. Reset at the start of each line.
    private bool _currentLineRecognized;
    private bool _disposed;

    // Raised after any observation updates the tallies, so the Session Stats VM
    // can refresh its bound figures. Fires on the dispatch thread; debouncing
    // (if wanted) is the subscriber's concern.
    public event Action? Changed;

    public CombatSessionTracker(
        MessageRouter router,
        RoundDamageTracker rounds,
        Func<IReadOnlyList<CasterMessageMatcher>>? resolveSpellMatchers = null,
        Func<CasterMessageMatcher?>? resolveProcMatcher = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(rounds);
        _router = router;
        _rounds = rounds;
        _resolveSpellMatchers = resolveSpellMatchers;
        _resolveProcMatcher = resolveProcMatcher;

        _userHitsSub     = router.Subscribe(KnownPatterns.UserHits,     OnUserHits);
        _userMissesSub   = router.Subscribe(KnownPatterns.UserMisses,   OnUserMisses);
        _userDodgesSub   = router.Subscribe(KnownPatterns.UserDodges,   OnUserDodges);
        _mobHitsSub      = router.Subscribe(KnownPatterns.MobHits,      OnMobHits);
        _mobMissesSub    = router.Subscribe(KnownPatterns.MobMisses,    OnMobMisses);
        _combatStatusSub = router.Subscribe(KnownPatterns.CombatStatus, OnCombatStatus);
        // LineDispatched fires once per line BEFORE the fixed patterns, so the
        // game-data recogniser claims a spell-cast / proc line and vetoes the
        // physical-swing classifier that runs a moment later in OnUserHits.
        router.LineDispatched += OnLineDispatched;
        _rounds.RoundComplete += OnRoundComplete;

        RefreshMatchers();
    }

    // Re-resolve the configured attack-spell and equipped-weapon-proc matchers
    // from live game data. Called on the boundaries that move them: connect /
    // character switch, a Combat-tab edit, a game-data set swap, and a weapon
    // swap. Cheap and idempotent — safe to call on a hot event.
    public void RefreshMatchers()
    {
        _spellMatchers = _resolveSpellMatchers?.Invoke() ?? Array.Empty<CasterMessageMatcher>();
        _procMatcher = _resolveProcMatcher?.Invoke();
    }

    // Point-in-time copy of the session's combat figures. The physical extent is
    // the min/max across whichever swing categories have landed; an empty
    // category contributes nothing.
    public CombatSessionStats Snapshot()
        => new(
            Hits:                _hit.Count,
            Crits:               _crit.Count,
            Backstabs:           _backstab.Count,
            Misses:              _misses,
            HitMinDamage:        _hit.Count == 0 ? 0 : _hit.Min,
            HitMaxDamage:        _hit.Max,
            HitTotalDamage:      _hit.Sum,
            CritMinDamage:       _crit.Count == 0 ? 0 : _crit.Min,
            CritMaxDamage:       _crit.Max,
            CritTotalDamage:     _crit.Sum,
            BackstabMinDamage:   _backstab.Count == 0 ? 0 : _backstab.Min,
            BackstabMaxDamage:   _backstab.Max,
            BackstabTotalDamage: _backstab.Sum,
            MobHits:             _mobHits,
            MobMisses:           _mobMisses,
            Dodges:              _dodges,
            RoundsWithDamage:    _round.Count,
            RoundMinDamage:      _round.Count == 0 ? 0 : _round.Min,
            RoundMaxDamage:      _round.Max,
            RoundTotalDamage:    _round.Sum,
            ProcHits:            _proc.Count,
            ProcMinDamage:       _proc.Count == 0 ? 0 : _proc.Min,
            ProcMaxDamage:       _proc.Max,
            ProcTotalDamage:     _proc.Sum,
            SpellHits:           _spell.Count,
            SpellMinDamage:      _spell.Count == 0 ? 0 : _spell.Min,
            SpellMaxDamage:      _spell.Max,
            SpellTotalDamage:    _spell.Sum);

    // Zero every counter — called on the session boundary (connect / character
    // switch), matching RoundDamageTracker.Reset.
    public void Reset()
    {
        _hit = default;
        _crit = default;
        _backstab = default;
        _round = default;
        _proc = default;
        _spell = default;
        _misses = 0;
        _mobHits = 0;
        _mobMisses = 0;
        _dodges = 0;
        _lastWasLandedSwing = false;
        _currentLineRecognized = false;
        _engaged = false;
        Changed?.Invoke();
    }

    private void OnLineDispatched(LineExtractor.EmittedLine line)
    {
        // Reset per line — the flag only ever describes the line currently
        // being dispatched (this runs first, before the fixed patterns).
        _currentLineRecognized = false;
        if (_spellMatchers.Count == 0 && _procMatcher is null) return;

        string text = line.Text;

        // Configured attack spell takes precedence over the physical-swing
        // classifier: a cast like "You cast {s} at {target} for {damage}
        // damage!" carries the first-person "You" source UserHits also matches,
        // so without claiming it here it would be miscounted as a melee hit.
        foreach (CasterMessageMatcher m in _spellMatchers)
        {
            if (m.TryMatchDamage(text, out int sdmg))
            {
                _spell.Add(sdmg);
                _currentLineRecognized = true;
                Changed?.Invoke();
                return;
            }
        }

        // A weapon proc fires only AFTER a basic swing connects, so it's only
        // counted when the previous offensive line was a landed hit. Folding it
        // into its own row (not _hit) keeps it out of the swing accuracy +
        // physical extent; its damage still rolls into the per-round total via
        // RoundDamageTracker's own UserHits subscription.
        if (_lastWasLandedSwing && _procMatcher is { } proc && proc.TryMatchDamage(text, out int pdmg))
        {
            _proc.Add(pdmg);
            _lastWasLandedSwing = false; // one proc per connected swing
            _currentLineRecognized = true;
            Changed?.Invoke();
        }
    }

    private void OnUserHits(MatchResult match)
    {
        // A line already claimed as a configured spell-cast or weapon proc
        // (recognised on the LineDispatched pass, which runs first) must not
        // also count as a physical swing.
        if (_currentLineRecognized) return;

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
        _lastWasLandedSwing = true; // a weapon proc may follow this connected swing
        Changed?.Invoke();
    }

    private void OnUserMisses(MatchResult _)
    {
        // The miss skeleton also matches self-emotes ending in "!", so only a
        // line seen while combat is engaged is a real swing whiff.
        if (!_engaged) return;
        _misses++;
        _lastWasLandedSwing = false; // a whiff can't precede a proc
        Changed?.Invoke();
    }

    private void OnUserDodges(MatchResult _)
    {
        _engaged = true; // an incoming attack means we're mid-combat
        _dodges++;
        Changed?.Invoke();
    }

    private void OnMobHits(MatchResult _)
    {
        _engaged = true; // an incoming attack means we're mid-combat
        _mobHits++;
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
        _engaged = string.Equals(match.Groups[0], "Engaged", StringComparison.OrdinalIgnoreCase);
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
        _mobHitsSub.Dispose();
        _mobMissesSub.Dispose();
        _combatStatusSub.Dispose();
        _router.LineDispatched -= OnLineDispatched;
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
