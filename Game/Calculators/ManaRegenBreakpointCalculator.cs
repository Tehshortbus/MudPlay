namespace MudPlay.Game.Calculators;

// Mana-regen breakpoint planning math for casters (mage / druid). The natural,
// non-resting, non-meditating per-tick amount is CharacterCalculator.CalcManaRegen
// — the engine-verified formula the Level Projection grid already trusts. Because
// that value is integer-truncated, it steps up by whole MP at discrete +ManaRgn%
// thresholds; those steps ARE the "breakpoints" (there is no separate table).
//
// A code-145 roll spell (nature tap / mana flux) rolls a magnitude from its
// level-scaled [Min,Max] range each cast, and that magnitude adds straight into
// the +ManaRgn% total (it is a percent modifier, not flat mana). So the question
// "what roll pushes me over the next tick?" reduces to: sweep the roll value
// across [Min,Max], find where the tick steps up, and map each step back to the
// roll value needed to reach it. Anything the range can't reach is a reroll that
// only burns mana for no extra tick.
//
// Pure and gear-independent of any UI: the caller supplies the resolved inputs
// (level, magery type/level, stats, gear +ManaRgn%, realm) and the spell's
// level-scaled roll range; SpellCalculator.AffectMagnitude produces that range.
public static class ManaRegenBreakpointCalculator
{
    // Passive (non-resting) mana-regen tick cadence — one tick every 30 s / 6
    // rounds. The client observes this as "MP +N after ~30s"; used only to render
    // a per-minute rate, never in the tick amount itself.
    public const int PassiveTickSeconds = 30;

    public readonly record struct Inputs(
        int Level,
        int MageryType,        // 1 = mage (INT), 2 = priest (WIL), 3 = druid ((INT+WIL)/2), 4 = bard (CHA), 5 = kai
        int Intellect,
        int Willpower,
        int MageryLevel,       // class magery tier (Classes.MageryLVL), constant per class
        int GearRegenPercent,  // summed +ManaRgn% from gear / quests (code 145), no spell
        RealmType Realm,
        int Charm = 0);

    // One reachable tick step the roll spell can push us to: the tick it yields
    // and the roll value it needs.
    public readonly record struct Breakpoint(int Tick, int RollValueNeeded);

    public readonly record struct Result(
        int WorstTick,     // gear + spell rolling its minimum
        int BestTick,      // gear + spell rolling its maximum
        int RollMin,       // level-scaled spell roll range
        int RollMax,
        IReadOnlyList<Breakpoint> Breakpoints);   // steps in (WorstTick, BestTick]

    // Per-tick amount at a given rolled +ManaRgn% (0 = no spell). Gear is always
    // folded in; the engine formula truncates, so this is the whole-MP tick.
    public static int Tick(in Inputs i, int rolledRegenPercent)
        => CharacterCalculator.CalcManaRegen(
            i.Level, i.Intellect, i.Willpower, i.Charm,
            i.MageryType, i.MageryLevel, i.GearRegenPercent + rolledRegenPercent,
            isMeditating: false, i.Realm);

    public static Result Compute(in Inputs i, int rollMin, int rollMax)
    {
        if (rollMax < rollMin) (rollMin, rollMax) = (rollMax, rollMin);
        int worst = Tick(i, rollMin);
        int best = Tick(i, rollMax);

        var steps = new List<Breakpoint>();
        for (int t = worst + 1; t <= best; t++)
            if (MinRollFor(i, t, rollMin, rollMax) is { } v) steps.Add(new Breakpoint(t, v));

        return new Result(worst, best, rollMin, rollMax, steps);
    }

    // The rolls in [rollMin, rollMax] whose natural tick is exactly `tick` — how Stock,
    // with no `abil 145`, reads a landed roll back off the mana it pays. The engine
    // truncates, so one tick covers a band of rolls, not a single value. Null when no
    // roll in range gives that tick (gear or stats changed under the reading).
    public static (int Lo, int Hi)? RollsForTick(in Inputs i, int tick, int rollMin, int rollMax)
    {
        if (rollMax < rollMin) (rollMin, rollMax) = (rollMax, rollMin);
        int lo = Tick(i, rollMin) >= tick ? rollMin : MinRollFor(i, tick, rollMin, rollMax) ?? int.MaxValue;
        if (lo > rollMax || Tick(i, lo) != tick) return null;
        int hi = MinRollFor(i, tick + 1, lo, rollMax) is { } next ? next - 1 : rollMax;
        return (lo, hi);
    }

    // Smallest roll value in [lo,hi] whose tick reaches at least targetTick, or
    // null if even the best roll falls short. Tick is monotonic non-decreasing in
    // the rolled percent, so a binary search converges.
    private static int? MinRollFor(in Inputs i, int targetTick, int lo, int hi)
    {
        if (Tick(i, hi) < targetTick) return null;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (Tick(i, mid) >= targetTick) hi = mid;
            else lo = mid + 1;
        }
        return lo;
    }
}
