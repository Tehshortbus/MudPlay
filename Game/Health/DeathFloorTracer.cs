using System.ComponentModel;
using FujinTerm.Models.Settings;
using FujinTerm.Services;

namespace FujinTerm.Game.Health;

// Traces a realm's true negative-HP death floor from observed *slow* deaths and
// refines BbsProfile.PlayerDiesAtHp toward it. The seed (-25) is only a guess;
// this lets real deaths correct it.
//
// Why the HP trajectory and not the death line: MajorMUD prints exactly one
// death message ("You have been slain by <killer>.") whether you were overkilled
// by a single blow or bled out one tick at a time — a bleed-out still names the
// last attacker, so the line can't tell the two apart. Only the HP trajectory
// into death can. [CONFIRMED]
//
// Why only slow deaths measure the floor: the floor is a fixed per-realm
// constant, but a bleed-out crosses it one small tick at a time and lands right
// at it, so its HP reading is the floor. A single big hit overshoots far past
// the floor with no clamp, so its reading understates (over-negatives) the floor
// and must never refine it. [CONFIRMED]
//
// Classifier (self-calibrating, no magic bleed-tick constant): while HP <= 0 the
// character is dropped and bleeding, so consecutive downward HP steps in that
// band are bleed ticks — we learn the realm's tick size from them. A death is
// SLOW when we watched the character bleed (at least one in-band tick) and died
// in the band, with no in-band step big enough to be a combat hit. A death
// straight from positive HP (no bleed) or with a large in-band drop (a monster
// blowing past the floor) is an OVERKILL and is discarded. Per the user, the
// trace assumes we aren't taking a huge hit that leaps right past the floor —
// the big-step guard below is a best-effort backstop, not a guarantee (an
// overshoot the client never sees a prompt for can't be caught).
public sealed class DeathFloorTracer : IDisposable
{
    // LogService category — [DeathFloor] rows per refine / discarded death.
    public const string LogCategory = "DeathFloor";

    // A single downward HP step larger than this share of max HP is treated as a
    // combat hit, not a passive bleed tick — bleeding drains slowly, a blow that
    // can blow past the death floor is a large chunk of health. Deliberately
    // conservative: a missed slow-death measurement is cheap (the next clean
    // death corrects it), but a corrupted floor is not, so we discard when a big
    // step contaminates the descent rather than risk refining off an overkill.
    private const double MaxBleedStepFractionOfMaxHp = 0.10;

    private readonly PlayerState _state;
    private readonly Func<BbsProfile?> _resolveBbs;
    private readonly Action<BbsProfile> _saveBbs;
    private readonly LogService? _log;
    private bool _disposed;

    // Descent state — reset whenever HP climbs back above 0 (healed / respawned),
    // so each fresh dive into the bleeding-out band is measured on its own.
    private int? _lastHp;
    private bool _sawBleedTick;
    private int _maxInBandStep;

    public DeathFloorTracer(
        PlayerState state,
        Func<BbsProfile?> resolveBbs,
        Action<BbsProfile> saveBbs,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(resolveBbs);
        ArgumentNullException.ThrowIfNull(saveBbs);
        _state = state;
        _resolveBbs = resolveBbs;
        _saveBbs = saveBbs;
        _log = log;
        _state.PropertyChanged += OnStateChanged;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerState.Hp)) NoteHp(_state.Hp);
    }

    // Fold one HP reading into the descent trajectory. The initial default Hp=0
    // never lands here — PropertyChanged only fires on an actual value change —
    // so the first reading is always a real prompt.
    private void NoteHp(int hp)
    {
        if (hp > 0)
        {
            // Back in the positive — healed or respawned. A new descent starts
            // clean; a prior bleed-out that didn't end in death is forgotten.
            _lastHp = hp;
            _sawBleedTick = false;
            _maxInBandStep = 0;
            return;
        }

        // In the bleeding-out band (hp <= 0). A downward move from a reading that
        // was also in the band is a bleed tick — the only place we can measure
        // the realm's passive drain and spot a combat hit that doesn't belong.
        if (_lastHp is int prev && prev <= 0 && hp < prev)
        {
            _sawBleedTick = true;
            int step = prev - hp;
            if (step > _maxInBandStep) _maxInBandStep = step;
        }
        _lastHp = hp;
    }

    // The local player just died (wired to DeathLineWatcher.PlayerDied). Classify
    // the death off the trajectory captured so far and, when it's a clean slow
    // death, refine the active BBS's death floor to the measured HP.
    public void RecordDeath()
    {
        // Snapshot + reset first: whatever happens, the next life starts with a
        // clean descent (the respawn's positive-HP prompt would reset it anyway,
        // but a death we discard shouldn't leak state into the next one).
        int? deathHp = _lastHp;
        bool sawBleed = _sawBleedTick;
        int maxStep = _maxInBandStep;
        _lastHp = null;
        _sawBleedTick = false;
        _maxInBandStep = 0;

        BbsProfile? bbs = _resolveBbs();
        if (bbs is null || !bbs.AutoRefineDeathFloor) return;

        if (!IsSlowDeath(deathHp, sawBleed, maxStep, _state.MaxHp, out int measured, out string reason))
        {
            _log?.Debug(LogCategory,
                $"death not used to refine floor ({reason}); floor {bbs.PlayerDiesAtHp} unchanged.");
            return;
        }

        int old = bbs.PlayerDiesAtHp;
        if (measured == old)
        {
            _log?.Debug(LogCategory,
                $"slow death at HP {measured} confirms realm floor {old} (BBS '{bbs.Name}').");
            return;
        }

        bbs.PlayerDiesAtHp = measured;
        _saveBbs(bbs);
        _log?.Info(LogCategory,
            $"slow death at HP {measured} — refined realm floor {old} → {measured} (BBS '{bbs.Name}').");
    }

    // A death is a slow (measurable) death when we watched the character bleed
    // gradually into the band and cross the floor by a bleed tick, not a blow.
    // measured = the accurate floor reading; reason = why a rejected death was
    // rejected (for the log).
    private static bool IsSlowDeath(
        int? deathHp, bool sawBleed, int maxInBandStep, int maxHp,
        out int measured, out string reason)
    {
        measured = 0;

        if (deathHp is not int hp)
        {
            reason = "no HP reading before death";
            return false;
        }
        if (hp > 0)
        {
            // Killed outright from positive HP (or the death HP was never seen) —
            // an overkill that never entered the bleeding-out band we can measure.
            reason = $"died from positive HP ({hp}), not a bleed-out";
            return false;
        }
        if (!sawBleed)
        {
            // Dropped straight into the band in one step (a big hit) with no
            // gradual bleed observed — can't confirm this landed at the floor.
            reason = "no gradual bleed observed before death";
            return false;
        }
        if (maxHp <= 0)
        {
            // Without a known max HP the big-step guard can't run; refuse rather
            // than risk refining off an unbounded reading.
            reason = "max HP unknown — cannot bound bleed steps";
            return false;
        }
        if (maxInBandStep > maxHp * MaxBleedStepFractionOfMaxHp)
        {
            // A combat hit contaminated the descent — it blew past the floor, so
            // the reading over-negatives it.
            reason = $"large in-band drop ({maxInBandStep} > {MaxBleedStepFractionOfMaxHp:P0} of {maxHp} max HP) — overkill";
            return false;
        }

        measured = hp;
        reason = string.Empty;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.PropertyChanged -= OnStateChanged;
    }
}
