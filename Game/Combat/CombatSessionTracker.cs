using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Combat;

/// <summary>
/// Phase 11 — aggregates the session's combat lines into the
/// <see cref="CombatSessionStats"/> the Session Stats panel displays: the
/// player's swing accuracy (hit / miss / crit), physical and backstab damage
/// extents, incoming-attack defence (mob hits / misses / dodges), and the
/// per-round damage spread. Pure downstream subscriber — it never sends to the
/// wire, only observes <see cref="MessageRouter"/> lines and
/// <see cref="RoundDamageTracker.RoundComplete"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only the <i>local player's own</i> swings count toward the offensive
/// figures, so <see cref="OnUserHits"/> requires the first-person "You" source
/// (the same convention <c>conversation.local</c> uses to tell our own speech
/// from another player's). Classification is keyword-driven on the raw line,
/// per the MajorMUD line vocabulary: "surprise" ⇒ backstab, "critically" ⇒
/// crit (physical only — bash / smash / backstab / spells never crit),
/// otherwise a plain hit.
/// </para>
/// <para>
/// Threading: every mutation runs on the router's marshalled (UI) dispatch
/// thread, as does <see cref="RoundDamageTracker.RoundComplete"/>; the window
/// VM reads <see cref="Snapshot"/> on the same thread. The counters are
/// therefore lock-free, matching <see cref="RoundDamageTracker"/>.
/// </para>
/// </remarks>
public sealed class CombatSessionTracker : IDisposable
{
    private readonly IDisposable _userHitsSub;
    private readonly IDisposable _userMissesSub;
    private readonly IDisposable _userDodgesSub;
    private readonly IDisposable _mobHitsSub;
    private readonly IDisposable _mobMissesSub;
    private readonly RoundDamageTracker _rounds;

    private DamageTally _hit;
    private DamageTally _crit;
    private DamageTally _backstab;
    private DamageTally _round;
    private int _misses;
    private int _mobHits;
    private int _mobMisses;
    private int _dodges;
    private bool _disposed;

    /// <summary>Raised after any observation updates the tallies, so the
    /// Session Stats VM can refresh its bound figures. Fires on the dispatch
    /// thread; debouncing (if wanted) is the subscriber's concern.</summary>
    public event Action? Changed;

    public CombatSessionTracker(MessageRouter router, RoundDamageTracker rounds)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(rounds);
        _rounds = rounds;

        _userHitsSub   = router.Subscribe(KnownPatterns.UserHits,   OnUserHits);
        _userMissesSub = router.Subscribe(KnownPatterns.UserMisses, OnUserMisses);
        _userDodgesSub = router.Subscribe(KnownPatterns.UserDodges, OnUserDodges);
        _mobHitsSub    = router.Subscribe(KnownPatterns.MobHits,    OnMobHits);
        _mobMissesSub  = router.Subscribe(KnownPatterns.MobMisses,  OnMobMisses);
        _rounds.RoundComplete += OnRoundComplete;
    }

    /// <summary>Point-in-time copy of the session's combat figures. The
    /// physical extent is the min/max across whichever swing categories have
    /// landed; an empty category contributes nothing.</summary>
    public CombatSessionStats Snapshot()
        => new(
            Hits:                _hit.Count,
            Crits:               _crit.Count,
            Backstabs:           _backstab.Count,
            Misses:              _misses,
            PhysicalMinDamage:   MinOf(_hit, _crit, _backstab),
            PhysicalMaxDamage:   Math.Max(_hit.Max, Math.Max(_crit.Max, _backstab.Max)),
            PhysicalTotalDamage: _hit.Sum + _crit.Sum + _backstab.Sum,
            BackstabMinDamage:   _backstab.Count == 0 ? 0 : _backstab.Min,
            BackstabMaxDamage:   _backstab.Max,
            BackstabTotalDamage: _backstab.Sum,
            MobHits:             _mobHits,
            MobMisses:           _mobMisses,
            Dodges:              _dodges,
            RoundsWithDamage:    _round.Count,
            RoundMinDamage:      _round.Count == 0 ? 0 : _round.Min,
            RoundMaxDamage:      _round.Max,
            RoundTotalDamage:    _round.Sum);

    /// <summary>Zero every counter — called on the session boundary
    /// (connect / character switch), matching
    /// <see cref="RoundDamageTracker.Reset"/>.</summary>
    public void Reset()
    {
        _hit = default;
        _crit = default;
        _backstab = default;
        _round = default;
        _misses = 0;
        _mobHits = 0;
        _mobMisses = 0;
        _dodges = 0;
        Changed?.Invoke();
    }

    private void OnUserHits(MatchResult match)
    {
        // KnownPatterns.UserHits also fires on "The {mob} {verb} you for N
        // damage!" and on another player's swing. Only OUR own first-person
        // "You" swings feed the player's accuracy figures.
        if (match.Groups.Count < 3 ||
            !string.Equals(match.Groups[0], "You", StringComparison.OrdinalIgnoreCase))
            return;

        if (!int.TryParse(match.Groups[2], out int dmg)) return;

        string line = match.Text;
        if (line.Contains("surprise", StringComparison.OrdinalIgnoreCase))
            _backstab.Add(dmg);
        else if (line.Contains("critically", StringComparison.OrdinalIgnoreCase))
            _crit.Add(dmg);
        else
            _hit.Add(dmg);
        Changed?.Invoke();
    }

    private void OnUserMisses(MatchResult _)
    {
        _misses++;
        Changed?.Invoke();
    }

    private void OnUserDodges(MatchResult _)
    {
        _dodges++;
        Changed?.Invoke();
    }

    private void OnMobHits(MatchResult _)
    {
        _mobHits++;
        Changed?.Invoke();
    }

    private void OnMobMisses(MatchResult match)
    {
        // A dodge line satisfies MobMisses too (its "{mob} … at you" shape);
        // it's already counted by OnUserDodges, so skip it here to keep the
        // plain-miss tally and the dodge denominator from double-counting.
        if (match.Text.Contains("dodge", StringComparison.OrdinalIgnoreCase)) return;
        _mobMisses++;
        Changed?.Invoke();
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

    // Smallest min across the non-empty tallies (an empty tally's Min is 0 and
    // must not win the comparison); 0 when nothing has landed yet.
    private static int MinOf(in DamageTally a, in DamageTally b, in DamageTally c)
    {
        int min = int.MaxValue;
        if (a.Count > 0) min = Math.Min(min, a.Min);
        if (b.Count > 0) min = Math.Min(min, b.Min);
        if (c.Count > 0) min = Math.Min(min, c.Min);
        return min == int.MaxValue ? 0 : min;
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
