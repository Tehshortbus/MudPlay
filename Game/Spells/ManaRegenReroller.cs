using MudPlay.Services;

namespace MudPlay.Game.Spells;

// Threshold + cap governing a mana-regen roll-spell reroll cycle, read live
// from SpellsSettings on every decision so a mid-cycle settings edit takes
// effect on the next roll.
//
// Threshold: reroll while the rolled spells: contribution lands below this.
// null disables rerolling — the spell then just recasts on expiry through the
// normal buff path, no abil read. Cap: hard ceiling on consecutive rerolls per
// cast cycle before accepting whatever landed. Unlimited: ignore Cap and keep
// rerolling until the roll clears the threshold (the mana floor still suspends /
// resumes the cycle) — the "reroll infinite" toggle, so the user needn't set an
// obscene Cap.
public readonly record struct ManaRegenRerollConfig(int? Threshold, int Cap, bool Unlimited = false);

// Paradigm-only reroll state machine for a code-145 mana-regen roll spell
// (nature tap / mana flux) — a persistent +/- modifier to the mana-regen rate
// whose landed value is a fresh roll each cast. After the spell lands we read
// its rolled contribution off abil 145's spells: slice (surfaced by
// AbilBreakdownParser); a roll below the configured threshold drags the regen
// rate down, so we recast to try for a better one — up to Cap times,
// hard-stopping early if the next cast can't be paid without dropping under the
// buff floor.
//
// Purely a state machine driven by two external events: the roll spell being CAST
// (OnRollSpellLanded, wired from the CastingDirector's send path — NOT the
// applied-line confirm, which never fires for a roll spell because it lands via a
// shared condition that can't be mapped back to the specific spell) and a parsed
// abil breakdown (AbilBreakdownParser.BreakdownParsed). It owns no timers, no wire
// access, and no spell classification: the caller only invokes OnRollSpellLanded
// for a spell it has already resolved to a code-145 roll spell, and supplies the
// query / recast / afford actions. This keeps the decision logic deterministic.
//
// The Stock realm has no abil breakdown to read, so there the landed roll is read
// back off the next natural mana tick instead: the engine pays
// (100 + gear% + roll) × base / 100 per 30 s tick, truncated, so the tick pins the
// roll to a band (ManaRegenBreakpointCalculator.RollsForTick). A meditate tick pays
// the unscaled base and lands on the same prompt as every other natural tick, so a
// meditating jump above the base is base + natural. Both realms compare the roll
// percent against the threshold; on Stock a band straddling it counts as a pass (a
// reroll only when the roll is definitely below).
//
// Cycle life: a cast while idle opens a cycle and zeroes the reroll counter;
// a cast mid-cycle is the recast we asked for and preserves the counter.
// Either way it fires one abil 145 query. The returned breakdown decides accept
// (roll >= threshold, cap reached, or floor hit) or reroll (recast, counter++).
// Serial by construction — each abil read is bracketed by a cast, so there
// is never more than one query in flight.
public sealed class ManaRegenReroller : IDisposable
{
    // LogService category — appears as [ManaRegen] rows per reroll decision.
    public const string LogCategory = "ManaRegen";

    // The ability code the reroll logic reads off an abil breakdown —
    // mana-regen is ability 145.
    public const int ManaRegenAbilityCode = RegenSpellClassifier.ManaRegenCode;

    // True when formula is a mana-regen roll spell — it carries a
    // code-ManaRegenAbilityCode ability whose stored AbilVal is 0, the
    // signature of nature tap / mana flux (the magnitude is rolled from the
    // level-scaled Min/Max range on each cast). A fixed +N regen buff (AbilVal =
    // N) or a mana HoT (chaos surge, codes 150 / 123) is excluded — rerolling
    // those is pointless. Thin alias over RegenSpellClassifier.IsManaRegenRoll so
    // the "what counts as a roll spell" rule lives in exactly one place, shared by
    // the caller's landing classifier and the Spells tab's range readout.
    public static bool IsRollSpell(in SpellFormulaInput formula)
        => RegenSpellClassifier.IsManaRegenRoll(formula);

    private readonly AbilBreakdownParser _parser;
    private readonly Func<ManaRegenRerollConfig> _readConfig;
    private readonly Action _sendAbilQuery;
    private readonly Action<string> _recast;
    private readonly Func<bool> _canAffordReroll;
    // True on the Stock realm — there's no `abil 145`, so roll quality is read back
    // off the observed natural mana tick (an MP jump on the statline) instead.
    private readonly Func<bool> _useTickMonitor;
    // Stock's tick inputs (null when the character's stats / class aren't known yet).
    private readonly Func<StockManaRollContext?> _stockContext;
    private readonly Func<DateTimeOffset> _clock;
    // A reroll is a between-round cast, and a between-round cast mid-fight turns combat
    // OFF — it breaks a running room spell and costs the round's attack (report
    // paradigm-20260924-123009: rerolled flux twice while an obsidian golem went
    // unattacked). So a reroll decided mid-fight is held until combat ends.
    private readonly Func<bool> _inCombat;
    private readonly LogService? _log;

    private string? _activeShort;
    private int _rerollsUsed;
    private bool _awaitingAbil;
    private bool _awaitingTick;
    private DateTimeOffset _awaitingTickSince;
    // True when a cycle has more rerolls left but the next recast couldn't be paid
    // without dropping under the mana floor. The cycle is SUSPENDED, not ended: the
    // reroll counter is preserved and OnRecoveryTick resumes it once mana recovers,
    // so it uses its full cap instead of surrendering at the floor (report
    // paradigm-20260901-114223 — gave up at 3/20 when it ran out of mana).
    private bool _waitingForMana;
    // True when a cycle's next reroll is held until the fight ends (see _inCombat).
    private bool _waitingForCombat;
    private bool _disposed;

    public ManaRegenReroller(
        AbilBreakdownParser parser,
        Func<ManaRegenRerollConfig> readConfig,
        Action sendAbilQuery,
        Action<string> recast,
        Func<bool> canAffordReroll,
        Func<bool> useTickMonitor,
        LogService? log = null,
        Func<bool>? inCombat = null,
        Func<StockManaRollContext?>? stockContext = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(parser);
        _parser = parser;
        _readConfig = readConfig;
        _sendAbilQuery = sendAbilQuery;
        _recast = recast;
        _canAffordReroll = canAffordReroll;
        _useTickMonitor = useTickMonitor;
        _inCombat = inCombat ?? (() => false);
        _stockContext = stockContext ?? (() => null);
        _clock = clock ?? (() => DateTimeOffset.Now);
        _log = log;
        _parser.BreakdownParsed += OnBreakdown;
    }

    // True while a reroll cycle is mid-flight (a roll landed and we're awaiting
    // its abil read, or about to recast). For diagnostics.
    public bool CycleActive => _activeShort is not null;

    // Rerolls spent in the current cycle; resets when a fresh roll lands while
    // idle. For diagnostics.
    public int RerollsUsed => _rerollsUsed;

    // True while a cycle is suspended at the mana floor, waiting for mana to recover
    // before the next reroll. For the bug report, so a paused reroll reads as
    // "waiting for mana" rather than looking stalled.
    public bool WaitingForMana => _waitingForMana;

    // True while a cycle's next reroll is held for the current fight to end.
    public bool WaitingForCombat => _waitingForCombat;

    // The roll quality last judged — the abil-145 spells value (Paradigm) or, on Stock,
    // the top of the band the observed tick allows (a band straddling the threshold
    // passes). Null until the first roll is evaluated.
    public int? LastObservedValue { get; private set; }

    // The last judged roll as shown in logs and the bug report: the abil value on
    // Paradigm, "lo..hi (tick N)" on Stock.
    public string? LastObservedText { get; private set; }

    // The natural tick pays at least 1 MP for any roll that isn't terrible, so a
    // cycle that sees none this long after the cast (mana below max, the tick not
    // hidden by a full pool) read the roll as paying 0. A natural tick is every 30 s.
    private static readonly TimeSpan NoTickTimeout = TimeSpan.FromSeconds(40);

    // The master switch (true = off).
    public Func<bool>? MasterSwitchOff { get; set; }

    // The roll spell spellShort was just CAST (from the CastingDirector's send path;
    // the cast is already on the wire, so the abil query below reads the fresh roll).
    // A cast while idle opens a new cycle with the reroll counter reset; a cast
    // mid-cycle is the recast we triggered and keeps the counter. Either way, fire an
    // abil 145 query to read what it rolled. No-op (and abandons any cycle) when
    // rerolling is disabled.
    public void OnRollSpellLanded(string spellShort)
    {
        if (string.IsNullOrWhiteSpace(spellShort)) return;

        ManaRegenRerollConfig cfg = _readConfig();
        if (cfg.Threshold is null || MasterSwitchOff?.Invoke() == true)
        {
            // Rerolling disabled (or the master switch is off, and a roll spell
            // the user cast by hand is theirs) — nothing to verify; the spell just
            // rides the normal recast-on-expiry buff path. Ensure we hold no stale
            // cycle.
            Reset();
            return;
        }

        // A cast is on the wire (our resume recast, or maintenance recasting an
        // expired flux) — supersede any mana-floor wait; the abil read below drives
        // the decision from here.
        _waitingForMana = false;

        if (_activeShort is null
            || !string.Equals(_activeShort, spellShort, StringComparison.OrdinalIgnoreCase))
        {
            // Fresh cycle: idle, or a different roll spell replaced the tracked one.
            _activeShort = spellShort;
            _rerollsUsed = 0;
            _log?.Info(LogCategory,
                $"reroll cycle start spell={spellShort} threshold={cfg.Threshold} cap={CapLabel(cfg)}");
        }
        else
        {
            _log?.Debug(LogCategory,
                $"recast landed spell={spellShort} reroll={_rerollsUsed}/{cfg.Cap}");
        }

        // Paradigm reads the rolled contribution off `abil 145`; Stock instead waits
        // for the next observed passive mana tick (an MP jump on the statline).
        if (_useTickMonitor())
        {
            _awaitingTick = true;
            _awaitingTickSince = _clock();
            _log?.Debug(LogCategory, "awaiting the next natural mana tick to read the roll");
        }
        else
        {
            _awaitingAbil = true;
            _log?.Debug(LogCategory, "querying abil 145 for rolled mana-regen contribution");
            _sendAbilQuery();
        }
    }

    private void OnBreakdown(AbilBreakdown b)
    {
        if (!_awaitingAbil) return;
        if (b.Code != ManaRegenAbilityCode) return;   // an unrelated abil query — keep waiting

        _awaitingAbil = false;
        _log?.Debug(LogCategory,
            $"observed rolled mana-regen contribution spells={b.Spells} (abil total {b.Total})");
        Decide(b.Spells, "roll");
    }

    // A mana uptick was observed (Stock): read the landed roll back off it. Only
    // consumed while a cycle awaits a tick. A jump that filled the pool was cut short
    // and says nothing. While meditating, a jump of exactly the base is the meditate
    // tick alone (wait for the next); a bigger one is base + the natural tick.
    public void OnManaTickObserved(RegenSample sample, bool reachedMax)
    {
        if (!_awaitingTick) return;
        if (reachedMax)
        {
            _log?.Debug(LogCategory, $"mana tick +{sample.Delta} filled the pool — can't read the roll off it");
            return;
        }
        if (_stockContext() is not { } ctx)
        {
            _awaitingTick = false;
            _log?.Info(LogCategory, "can't read the roll (stats or class unknown) — accepting it");
            Accept();
            return;
        }
        if (!ctx.GearSettled)
        {
            _log?.Debug(LogCategory, $"mana tick +{sample.Delta} landed mid gear swap — waiting for the next");
            return;
        }

        int natural = sample.Delta;
        if (sample.Position == PlayerPosition.Meditating)
        {
            if (sample.Delta == ctx.MeditateTick) return;   // the meditate tick alone
            if (sample.Delta < ctx.MeditateTick)
            {
                _log?.Debug(LogCategory,
                    $"mana tick +{sample.Delta} below the meditate tick {ctx.MeditateTick} — skipping it");
                return;
            }
            natural = sample.Delta - ctx.MeditateTick;
        }
        JudgeNaturalTick(natural, ctx);
    }

    private void JudgeNaturalTick(int natural, StockManaRollContext ctx)
    {
        _awaitingTick = false;
        if (Calculators.ManaRegenBreakpointCalculator.RollsForTick(ctx.Inputs, natural, ctx.RollMin, ctx.RollMax)
            is not { } band)
        {
            _log?.Info(LogCategory,
                $"natural mana tick {natural} matches no roll in {ctx.RollMin}..{ctx.RollMax} " +
                "(gear or stats changed?) — accepting it");
            Accept();
            return;
        }
        string shown = band.Lo == band.Hi ? $"{band.Lo} (tick {natural})" : $"{band.Lo}..{band.Hi} (tick {natural})";
        _log?.Debug(LogCategory, $"natural mana tick {natural} → roll {band.Lo}..{band.Hi}");
        Decide(band.Hi, "roll", shown);
    }

    // Close the cycle on a roll we can't judge, remembering nothing about it.
    private void Accept()
    {
        LastObservedValue = null;
        LastObservedText = null;
        Reset();
    }

    // Host heartbeat (~1s). Resume a cycle suspended at the mana floor once mana has
    // climbed back enough to pay for the next recast. Idle unless a cycle is waiting.
    // The recast rides the normal reroll path (RequestManaRegenReroll → priority cast
    // loop), so it takes the round's cast slot cleanly rather than racing a swing.
    public void OnRecoveryTick()
    {
        // Stock: no natural tick since the cast though mana could show one — the roll
        // pays 0 MP (the natural tick is a whole-MP amount that can round to nothing).
        if (_awaitingTick && _clock() - _awaitingTickSince >= NoTickTimeout
            && _stockContext() is { ManaBelowMax: true, GearSettled: true } ctx)
        {
            _log?.Debug(LogCategory, $"no natural mana tick in {NoTickTimeout.TotalSeconds:0}s — the roll pays 0");
            JudgeNaturalTick(0, ctx);
            return;
        }
        if (!_waitingForMana && !_waitingForCombat) return;
        if (_activeShort is not { } shortCode) { _waitingForMana = _waitingForCombat = false; return; }

        ManaRegenRerollConfig cfg = _readConfig();
        if (cfg.Threshold is null) { Reset(); return; }     // rerolling disabled meanwhile
        if (!cfg.Unlimited && _rerollsUsed >= cfg.Cap) { Reset(); return; }   // defensive: nothing left to spend
        if (_inCombat()) return;                            // still fighting — keep holding
        if (!_canAffordReroll())                            // still under the floor — keep waiting
        {
            _waitingForCombat = false;
            _waitingForMana = true;
            return;
        }

        string why = _waitingForCombat ? "the fight ended" : "mana recovery";
        _waitingForMana = _waitingForCombat = false;
        _rerollsUsed++;
        _log?.Info(LogCategory,
            $"resuming reroll spell={shortCode} after {why} — attempt {_rerollsUsed}/{CapLabel(cfg)}");
        _recast(shortCode);
    }

    // The shared accept-or-reroll decision. value is the roll's quality — the rolled
    // percent on Paradigm, the observed tick on Stock — compared against the configured
    // threshold; reroll while below it, up to the cap, pausing at the mana floor.
    private void Decide(int value, string valueLabel, string? shown = null)
    {
        if (_activeShort is not { } shortCode) return;   // defensive: no active cycle
        LastObservedValue = value;
        LastObservedText = shown ?? value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string v = LastObservedText;
        ManaRegenRerollConfig cfg = _readConfig();

        // Threshold went null mid-cycle (settings edit) — accept and drop out.
        if (cfg.Threshold is not { } threshold)
        {
            _log?.Info(LogCategory, $"reroll disabled mid-cycle — accepting spell={shortCode} {valueLabel}={v}");
            Reset();
            return;
        }

        if (value >= threshold)
        {
            _log?.Info(LogCategory,
                $"accepted spell={shortCode} {valueLabel}={v} >= threshold={threshold} " +
                $"after {_rerollsUsed} reroll(s)");
            Reset();
            return;
        }

        if (!cfg.Unlimited && _rerollsUsed >= cfg.Cap)
        {
            _log?.Info(LogCategory,
                $"reroll cap reached spell={shortCode} {valueLabel}={v} < threshold={threshold} " +
                $"cap={cfg.Cap} — accepting");
            Reset();
            return;
        }

        if (_inCombat())
        {
            _awaitingAbil = false;
            _awaitingTick = false;
            _waitingForCombat = true;
            _log?.Info(LogCategory,
                $"reroll held for combat spell={shortCode} {valueLabel}={v} < threshold={threshold} " +
                $"— a mid-fight recast would break combat; rerolling once the fight ends");
            return;
        }

        if (!_canAffordReroll())
        {
            // Out of mana to recast, but rerolls remain — SUSPEND the cycle rather
            // than accept the bad roll. Keep the counter; OnRecoveryTick resumes the
            // next attempt once mana climbs back over the floor, so we spend the full
            // cap instead of quitting at the floor (report paradigm-20260901-114223).
            _awaitingAbil = false;
            _awaitingTick = false;
            _waitingForMana = true;
            _log?.Info(LogCategory,
                $"reroll paused at mana floor spell={shortCode} {valueLabel}={v} < threshold={threshold} " +
                $"after {_rerollsUsed}/{CapLabel(cfg)} reroll(s) — waiting for mana to recover before the next attempt");
            return;
        }

        _rerollsUsed++;
        _log?.Info(LogCategory,
            $"rerolling spell={shortCode} {valueLabel}={v} < threshold={threshold} " +
            $"attempt {_rerollsUsed}/{CapLabel(cfg)}");
        _recast(shortCode);
    }

    // Cap for the log: "∞" when unlimited, else the numeric ceiling.
    private static string CapLabel(ManaRegenRerollConfig cfg) => cfg.Unlimited ? "∞" : cfg.Cap.ToString();

    // A reroll-config edit (cap raised / threshold loosened / infinite turned on) can
    // now warrant rerolling the roll spell that's ALREADY active — its last landed roll
    // is remembered in LastObservedValue. Re-open a cycle and stage one recast when that
    // roll is now below the new threshold and a reroll is allowed, so bumping the setting
    // acts on the live buff instead of waiting for the next natural recast (report
    // paradigm-20260909-113655: user set flux 0→20 expecting the active -2 to reroll). The
    // caller supplies the active roll spell's short code and has confirmed it's up. No-op
    // when a cycle is already in flight, no roll has been observed, rerolling is disabled,
    // no reroll is allowed, or the active roll already clears the threshold.
    public void ReconsiderActiveRoll(string spellShort)
    {
        if (string.IsNullOrWhiteSpace(spellShort)) return;
        if (_activeShort is not null) return;              // a cycle is running; it reads live config already
        if (LastObservedValue is not { } last) return;     // never saw a roll to judge
        ManaRegenRerollConfig cfg = _readConfig();
        if (cfg.Threshold is not { } threshold) return;     // rerolling disabled
        if (!cfg.Unlimited && cfg.Cap <= 0) return;         // no rerolls allowed
        if (last >= threshold) return;                      // the active roll already clears the bar

        _activeShort = spellShort;
        _rerollsUsed = 0;
        if (!_canAffordReroll())
        {
            // Below the mana floor — SUSPEND; OnRecoveryTick fires the reroll once mana climbs.
            _waitingForMana = true;
            _log?.Info(LogCategory,
                $"config change — active {spellShort} roll {last} < threshold {threshold} (cap {CapLabel(cfg)}): " +
                "under the mana floor, waiting to reroll");
            return;
        }
        _rerollsUsed = 1;
        _log?.Info(LogCategory,
            $"config change — rerolling active {spellShort} (last roll {last} < threshold {threshold}) attempt 1/{CapLabel(cfg)}");
        _recast(spellShort);
    }

    // Abandon any in-progress cycle — call on disconnect / death / spell-pick
    // change so a stale wait can't strand the reroller.
    public void Reset()
    {
        _activeShort = null;
        _rerollsUsed = 0;
        _awaitingAbil = false;
        _awaitingTick = false;
        _waitingForMana = false;
        _waitingForCombat = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _parser.BreakdownParsed -= OnBreakdown;
    }
}
