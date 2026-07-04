using FujinTerm.Game.GameData;

namespace FujinTerm.Game.Spells;

// Renders a spell's level-scaled effect string ("Dmg 14–22 · Heal 30–45 ·
// 24 seconds · AC +10 · Removes poison") from its SpellFormulaInput, reproducing
// MajorMUD's spell-effect text: the Dmg / Heal ranges fold the damage / heal
// ability codes, durations convert 3-second spell-round ticks to seconds,
// stat-affect abilities surface signed magnitudes, and RemovesSpell targets
// render by name.
//
// Shared by the Spell Book (SpellBookRowViewModel, scaled to the character's
// level) and the Game Data Items "Other Info" pane (weapon use-cast / proc
// effects, rendered at the spell's own base level).
public static class SpellEffectFormatter
{
    // Compose the effect string for formula at level (the level-scaled
    // Min/Max/Dur figures clamp to the spell's ReqLevel, so a level-0 "base"
    // render still yields real damage). Returns "—" when the spell produces no
    // figure at all.
    //
    // resolveChain maps a spell number to its formula so chained end-cast (Abil
    // 151) spells resolve to a real follow-up. resolveSpellName maps a spell
    // number to its name (for the RemovesSpell clause); may be null.
    // resolveTextblockCasts maps a TextBlock record number to the spells it
    // casts (for Abil-148 expansion); may be null. resolveMonsterName maps a
    // monster number to its name, so a Summon (Abil 12) renders "Summon hydra"
    // instead of "Summon +590"; may be null (then Summon falls back to the raw
    // number).
    public static string Format(
        in SpellFormulaInput formula,
        int level,
        Func<int, SpellFormulaInput?> resolveChain,
        Func<int, string?>? resolveSpellName = null,
        Func<int, IReadOnlyList<KnownSpell>>? resolveTextblockCasts = null,
        Func<int, string?>? resolveMonsterName = null)
        => FormatCore(formula, level, resolveChain, resolveSpellName, resolveTextblockCasts,
                      resolveMonsterName, visited: null, suppressDuration: false);

    // Format implementation that threads the EndCast cycle-guard set. visited
    // carries the spell numbers already being expanded up the EndCast chain so a
    // spell whose chain loops back to an ancestor stops rather than recursing
    // forever; null at the top level (a fresh set is allocated only when an
    // EndCast is encountered).
    private static string FormatCore(
        in SpellFormulaInput formula,
        int level,
        Func<int, SpellFormulaInput?> resolveChain,
        Func<int, string?>? resolveSpellName,
        Func<int, IReadOnlyList<KnownSpell>>? resolveTextblockCasts,
        Func<int, string?>? resolveMonsterName,
        HashSet<int>? visited,
        bool suppressDuration)
    {
        List<string> parts = new();

        long minDmg = SpellCalculator.MinDamage(formula, level, resolveChain);
        long maxDmg = SpellCalculator.MaxDamage(formula, level, resolveChain);
        if (maxDmg > 0 && minDmg >= 0)
            parts.Add($"Dmg {Range(minDmg, maxDmg)}");
        else if (minDmg != 0 || maxDmg != 0)
            // Raw negative / inverted stored range (sacrifice -60..-20,
            // dragonfire 100..-50) — MajorMUD's verbatim spell data shows the
            // specific damage ability with the signed range as-is rather than
            // folding it into a positive "Dmg" figure.
            parts.Add(DamageAbilityLabel(formula, minDmg, maxDmg));

        long minHeal = SpellCalculator.MinHeal(formula, level, resolveChain);
        long maxHeal = SpellCalculator.MaxHeal(formula, level, resolveChain);
        if (maxHeal > 0) parts.Add($"Heal {Range(minHeal, maxHeal)}");

        // Stat affects, plus any TextBlock-cast buff expansion. The expansion
        // suppresses its children's own durations and reports the longest back
        // here (childDurTicks) so a Mystic "form" — a spell that casts a
        // TextBlock buff whose duration mirrors the form spell's — surfaces a
        // single duration figure instead of printing both.
        string affects = BuildAffects(
            formula, level, resolveChain, resolveSpellName, resolveTextblockCasts, resolveMonsterName,
            out long childDurTicks);

        // Durations are stored in 3-second spell-round ticks; show seconds. When
        // this render is itself a suppressed TextBlock child the parent owns the
        // duration figure, so emit none. Otherwise show the longer of this
        // spell's own duration and its TextBlock children's.
        if (!suppressDuration)
        {
            long durTicks = Math.Max(SpellCalculator.Duration(formula, level), childDurTicks);
            if (durTicks > 0) parts.Add($"{durTicks * SpellCalculator.SpellRoundSeconds} seconds");
        }

        if (affects.Length > 0) parts.Add(affects);

        // EndCast chains are whole follow-up spells, not stat affects — each
        // gets its own " · " part (after the affects rollup, before cleanup)
        // so "Dmg 100–150 · 20% EndCast spear slam knockdown (…)" reads as a
        // sequence rather than comma-mixed with bonus magnitudes.
        foreach (SpellAbility a in formula.Abilities)
        {
            if (a.Code != EndCastCode) continue;
            // A non-zero AbilVal names one chained spell; a zero AbilVal is the
            // random-cast marker (pool = the MinBase..MaxBase spell range).
            string clause = a.Value != 0
                ? BuildEndCast(
                    formula, a.Value, level, resolveChain, resolveSpellName, resolveTextblockCasts,
                    resolveMonsterName, visited)
                : BuildRandomEndCast(
                    formula, level, resolveChain, resolveSpellName, resolveTextblockCasts,
                    resolveMonsterName, visited);
            if (clause.Length > 0) parts.Add(clause);
        }

        // A spell's "Removes …" clause trails its own gain figures — consistent
        // with multi-cast textblock expansion (gains first, cleanup last).
        string removes = BuildRemoves(formula, resolveSpellName);
        if (removes.Length > 0) parts.Add(removes);

        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }

    // Ability codes the stat-affect rollup skips. Two groups:
    // - Already surfaced elsewhere — Damage (1), DrainLife (8), Damage(-MR)
    //   (17), Heal (18) fold into the Dmg / Heal figure; EndCast (151) expands
    //   into its chained spell as its own part and EndCast% (164) is consumed as
    //   that clause's prefix (see BuildEndCast); RemovesSpell (122) is rendered
    //   by name separately (see BuildRemoves).
    // - Display-only message slots the game hides by default — ConfuseMsg (101),
    //   DescMsg (115), StartMsg (120), ShockMsg (137).
    private static readonly int[] _affectSkip = { 1, 8, 17, 18, 122, 101, 115, 120, 137, 151, 164 };

    // Summon ability code (Abil 12) — its AbilVal is the summoned monster
    // number, resolved to a name when a resolver is supplied.
    private const int SummonCode = 12;

    // Display-only friendly wording for the effect string — plain English for
    // the jargon-y ability names that surface here. Canonical AbilityNames is
    // deliberately left untouched (it still drives the spell's field-by-field
    // rows + the Monster ability rollups); this only softens the at-a-glance
    // Effect summary. Limited to flag-style codes (rendered name-only) where a
    // phrase reads cleanly without a trailing magnitude.
    private static readonly Dictionary<int, string> _friendlyAffect = new()
    {
        { 144, "ignores magic resistance" },   // NonMagicalSpell
        {  51, "anti-magic" },                  // AntiMagic
        {  23, "undead only" },                 // AffectsUndeadOnly
        {  80, "animals only" },                // AffectsAnimalsOnly
        { 108, "living only" },                 // AffectsLivingOnly
        { 109, "non-living only" },             // NonLiving
        {  97, "good only" },                   // GoodOnly
        {  98, "evil only" },                   // EvilOnly
        { 110, "non-good only" },               // NotGood
        { 111, "non-evil only" },               // NotEvil
        { 112, "neutral only" },                // NeutralOnly
        { 113, "non-neutral only" },            // NotNeutral
    };

    private static string FriendlyAffect(int code, string canonical)
        => _friendlyAffect.TryGetValue(code, out string? friendly) ? friendly : canonical;

    // EndCast ability code (Abil 151). Its AbilVal is the spell number the cast
    // chains into on completion.
    private const int EndCastCode = 151;

    // EndCast% ability code (Abil 164) — the percentage chance the sibling
    // EndCastCode chain fires. Consumed by BuildEndCast as the clause's prefix,
    // never rendered as a standalone "+N" affect.
    private const int EndCastPercentCode = 164;

    // RemovesSpell ability code (Abil 122).
    private const int RemovesSpellCode = 122;

    // DispellMagic (73) / NegateAbility (124) ability codes. Their AbilVal is an
    // ability-code pointer naming what the spell dispels / negates, not a
    // magnitude — so they render "DispellMagic (Poison)" rather than
    // "DispellMagic +19". A zero AbilVal renders the bare name.
    private const int DispelMagicCode = 73;

    // NegateAbility ability code (Abil 124) — shares DispelMagicCode's
    // ability-pointer rendering.
    private const int NegateAbilityCode = 124;

    // TextBlock ability code (Abil 148). Its AbilVal is a TextBlock record
    // number the spell executes, not a stat magnitude — so it renders unsigned
    // ("TextBlock 869").
    private const int TextBlockCode = 148;

    // Pure-flag ability codes. They carry no magnitude and render name-only.
    // Without this guard a flag that coexists with a damage / heal spell (so the
    // level-scaled Min/Max is non-zero) wrongly inherits that spell's range —
    // e.g. NonMagicalSpell (144) on "way of the dragon" printing
    // "NonMagicalSpell +13 to +22", duplicating the "Dmg 13–22" figure.
    private static readonly int[] _flagOnly =
    {
        23, 51, 52, 80, 97, 98, 100,
        108, 109, 110, 111, 112, 113,
        119, 138, 144, 178,
    };

    // Decode the stat-affect abilities the spell grants ("AC +10", "M.R. +5",
    // "MaxDamage +4 to +8") for the Effect column. Generic-affect path: a slot
    // carrying a stored AbilVal shows that signed value; a slot with AbilVal ==
    // 0 shows the spell's level-scaled Min/Max range instead (clamped to the
    // obtain level, so MR / Stealth / backstab / MaxDamage figures surface their
    // magnitude rather than name-only). Damage / heal / message / removed-spell
    // codes are surfaced elsewhere (see _affectSkip).
    private static string BuildAffects(
        in SpellFormulaInput formula,
        int level,
        Func<int, SpellFormulaInput?> resolveChain,
        Func<int, string?>? resolveSpellName,
        Func<int, IReadOnlyList<KnownSpell>>? resolveTextblockCasts,
        Func<int, string?>? resolveMonsterName,
        out long childDurTicks)
    {
        childDurTicks = 0;
        (long affMin, long affMax) = SpellCalculator.AffectMagnitude(formula, level);

        List<string> parts = new();
        foreach (SpellAbility a in formula.Abilities)
        {
            if (a.Code == 0 || Array.IndexOf(_affectSkip, a.Code) >= 0) continue;
            string? canonical = AbilityNames.GetName(a.Code);
            if (canonical is null) continue;

            // Summon (12): AbilVal is a monster number, not a magnitude — render
            // the creature's name ("Summon hydra") rather than "Summon +590".
            if (a.Code == SummonCode)
            {
                string? mon = a.Value > 0 ? resolveMonsterName?.Invoke(a.Value)?.Trim() : null;
                parts.Add(string.IsNullOrEmpty(mon)
                    ? (a.Value != 0 ? $"{canonical} {Signed(a.Value)}" : canonical)
                    : $"Summon {mon}");
                continue;
            }

            // Display-only friendly wording (jargon to plain English); canonical
            // AbilityNames is left intact for the field-by-field rows.
            string name = FriendlyAffect(a.Code, canonical);

            // TextBlock's AbilVal is a record number, not a magnitude. Prefer
            // the real effect(s) the textblock casts — resolved via the Spells
            // "Casted By" reverse-link — so a chained transform's duration /
            // stat bonuses surface inline. Falls back to the unsigned record
            // number (never "TextBlock +869") when the textblock links to
            // nothing.
            if (a.Code == TextBlockCode)
            {
                string expanded = ExpandTextblockCasts(
                    a.Value, level, resolveChain, resolveSpellName, resolveTextblockCasts,
                    resolveMonsterName, out long tbDurTicks);
                if (tbDurTicks > childDurTicks) childDurTicks = tbDurTicks;
                if (expanded.Length > 0)
                {
                    parts.Add(expanded);
                    continue;
                }
                parts.Add(a.Value != 0
                    ? $"{name} {a.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                    : $"{name} {Unsigned(affMin, affMax)}");
                continue;
            }

            // DispellMagic / NegateAbility carry an ability-code pointer in
            // AbilVal naming what they strip, not a magnitude. Render the
            // target ability's name in parens ("DispellMagic (Poison)"); a
            // zero AbilVal renders the bare name.
            if (a.Code == DispelMagicCode || a.Code == NegateAbilityCode)
            {
                if (a.Value == 0) parts.Add(name);
                else
                {
                    string? target = AbilityNames.GetName(a.Value);
                    parts.Add(string.IsNullOrEmpty(target)
                        ? $"{name} (#{a.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)})"
                        : $"{name} ({target})");
                }
                continue;
            }

            // Pure flags carry no magnitude — never attach the coexisting
            // damage / heal range (the flag group renders name-only).
            if (a.Value == 0 && Array.IndexOf(_flagOnly, a.Code) >= 0)
            {
                parts.Add(name);
                continue;
            }

            if (a.Value != 0)
                parts.Add($"{name} {Signed(a.Value)}");
            else if (affMin != 0 || affMax != 0)
                parts.Add($"{name} {SignedRange(affMin, affMax)}");
            else
                parts.Add(name); // flag-style affect — no magnitude to show
        }
        return string.Join(", ", parts);
    }

    // Expand an EndCast (Abil 151) slot into its chained spell's effects,
    // prefixed by the sibling EndCast% (Abil 164) chance when present —
    // "20% EndCast spear slam knockdown (12 seconds · HoldPerson +100, …)".
    // Recurses through nested EndCasts (a chained spell may itself EndCast),
    // guarding against a chain that loops back to an ancestor via visited. When
    // the chained spell can't be resolved or produces no figure, only the named
    // prefix shows.
    private static string BuildEndCast(
        in SpellFormulaInput parent,
        int chainedNumber,
        int level,
        Func<int, SpellFormulaInput?> resolveChain,
        Func<int, string?>? resolveSpellName,
        Func<int, IReadOnlyList<KnownSpell>>? resolveTextblockCasts,
        Func<int, string?>? resolveMonsterName,
        HashSet<int>? visited)
    {
        if (chainedNumber == 0) return string.Empty;

        visited ??= new HashSet<int>();
        if (parent.Number != 0) visited.Add(parent.Number);
        if (!visited.Add(chainedNumber)) return string.Empty; // chain loops back — stop

        string name = resolveSpellName?.Invoke(chainedNumber)?.Trim() is { Length: > 0 } resolved
            ? resolved
            : $"#{chainedNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

        int pct = EndCastPercent(parent);
        string prefix = pct > 0
            ? $"{pct.ToString(System.Globalization.CultureInfo.InvariantCulture)}% EndCast {name}"
            : $"EndCast {name}";

        string effect = resolveChain(chainedNumber) is { } chained
            ? FormatCore(chained, level, resolveChain, resolveSpellName, resolveTextblockCasts,
                         resolveMonsterName, visited, suppressDuration: false)
            : string.Empty;

        return effect.Length == 0 || effect == "—" ? prefix : $"{prefix} ({effect})";
    }

    // Expand an EndCast (Abil 151) whose AbilVal is 0 — the random-cast marker.
    // The spell's MinBase..MaxBase fields hold a spell-NUMBER range (not a
    // damage range); on cast the game fires one spell picked at random from that
    // pool ("random dmg" fires rocks shred / ice freezes / … each Dmg 5–15).
    // Renders "EndCast (random): name1 / name2 / … (effect)" when every pool
    // member shares one effect (the common parallel-element case), else pairs
    // each name with its own effect. Empty when the spell does direct damage /
    // heal (then MinBase/MaxBase are a magnitude range, already surfaced as
    // "Dmg …") or the pool resolves to nothing.
    private static string BuildRandomEndCast(
        in SpellFormulaInput parent,
        int level,
        Func<int, SpellFormulaInput?> resolveChain,
        Func<int, string?>? resolveSpellName,
        Func<int, IReadOnlyList<KnownSpell>>? resolveTextblockCasts,
        Func<int, string?>? resolveMonsterName,
        HashSet<int>? visited)
    {
        // A direct-damage / heal spell uses MinBase/MaxBase as its magnitude
        // range, not a spell pool — don't misread those as spell numbers.
        foreach (SpellAbility a in parent.Abilities)
            if (Array.IndexOf(_damageCodes, a.Code) >= 0 || a.Code == 18) return string.Empty;

        int lo = parent.MinBase, hi = parent.MaxBase;
        if (lo < 1 || hi < lo) return string.Empty;

        visited ??= new HashSet<int>();
        if (parent.Number != 0) visited.Add(parent.Number);

        List<string> names = new();
        List<string> effects = new();
        for (int n = lo; n <= hi; n++)
        {
            if (resolveChain(n) is not { } target) continue;
            if (!visited.Add(n)) continue; // pool member already expanded up-chain

            string name = resolveSpellName?.Invoke(n)?.Trim() is { Length: > 0 } resolved
                ? resolved
                : $"#{n.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            string effect = FormatCore(
                target, level, resolveChain, resolveSpellName, resolveTextblockCasts,
                resolveMonsterName, visited, suppressDuration: false);
            names.Add(name);
            effects.Add(effect == "—" ? string.Empty : effect);
        }

        if (names.Count == 0) return string.Empty;

        // Pool members are usually parallel elemental variants sharing one
        // effect — collapse to "names… (effect)". When they diverge, pair each.
        if (effects.All(e => e == effects[0]))
        {
            string joined = string.Join(" / ", names);
            return effects[0].Length == 0
                ? $"EndCast (random): {joined}"
                : $"EndCast (random): {joined} ({effects[0]})";
        }

        List<string> paired = new();
        for (int i = 0; i < names.Count; i++)
            paired.Add(effects[i].Length == 0 ? names[i] : $"{names[i]} ({effects[i]})");
        return $"EndCast (random): {string.Join(" / ", paired)}";
    }

    // The sibling EndCast% (Abil 164) chance on the same formula — the
    // percentage the EndCast chain fires. 0 when absent.
    private static int EndCastPercent(in SpellFormulaInput formula)
    {
        foreach (SpellAbility a in formula.Abilities)
            if (a.Code == EndCastPercentCode) return a.Value;
        return 0;
    }

    // Expand an Abil-148 TextBlock reference into the effect(s) of the real
    // spells that textblock casts, resolved via the Spells "Casted By"
    // reverse-link. Each linked spell's effect is rendered with Format and
    // joined with ", "; spells producing no figure ("—") are dropped. One level
    // deep — the recursion passes no textblock resolver, so a textblock that
    // casts a spell looping back to the same textblock can't recurse
    // infinitely. Empty when no resolver is supplied or the textblock links to
    // nothing.
    private static string ExpandTextblockCasts(
        int textblock,
        int level,
        Func<int, SpellFormulaInput?> resolveChain,
        Func<int, string?>? resolveSpellName,
        Func<int, IReadOnlyList<KnownSpell>>? resolveTextblockCasts,
        Func<int, string?>? resolveMonsterName,
        out long maxChildDurTicks)
    {
        maxChildDurTicks = 0;
        if (resolveTextblockCasts is null) return string.Empty;
        IReadOnlyList<KnownSpell> casts = resolveTextblockCasts(textblock);
        if (casts.Count == 0) return string.Empty;

        // A textblock that grants a buff often also casts a pure cleanup spell
        // (e.g. the Style forms cast "remove forms" alongside the form buff).
        // Keep the gain figures first and let the "Removes …" clause trail —
        // independent of the order the reverse-link happens to return.
        List<string> gains = new();
        List<string> removes = new();
        foreach (KnownSpell s in casts)
        {
            // Hoist the longest child duration to the parent and render the child
            // with its own duration suppressed — the form spell already carries a
            // (usually equal or shorter) duration, and we want one figure, not two.
            long childDur = SpellCalculator.Duration(s.Formula, level);
            if (childDur > maxChildDurTicks) maxChildDurTicks = childDur;

            string effect = FormatCore(
                s.Formula, level, resolveChain, resolveSpellName,
                resolveTextblockCasts: null, resolveMonsterName, visited: null, suppressDuration: true);
            if (effect.Length == 0 || effect == "—") continue;
            (IsRemovesOnlyEffect(effect) ? removes : gains).Add(effect);
        }
        gains.AddRange(removes);
        return string.Join(", ", gains);
    }

    // True when a rendered effect string is nothing but a "Removes …" clause (a
    // pure-cleanup spell), so it can be sorted after the gain figures in a
    // multi-cast textblock expansion.
    private static bool IsRemovesOnlyEffect(string effect)
        => effect.StartsWith("Removes ", StringComparison.Ordinal)
        && !effect.Contains(" · ", StringComparison.Ordinal);

    // Signed magnitude — "+10" / "-5" (negatives carry their own minus sign).
    private static string Signed(long value)
        => value > 0
            ? $"+{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string SignedRange(long min, long max)
        => min == max ? Signed(min) : $"{Signed(min)} to {Signed(max)}";

    // Unsigned magnitude range — for reference values (TextBlock record
    // numbers) where a leading + would be misleading.
    private static string Unsigned(long min, long max)
        => min == max
            ? min.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : $"{min.ToString(System.Globalization.CultureInfo.InvariantCulture)} to {max.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    // Render any RemovesSpell (Abil 122) slots by the removed spell's name (the
    // AbilVal resolves to a spell name). Empty when the spell removes nothing or
    // no name resolver is supplied.
    private static string BuildRemoves(in SpellFormulaInput formula, Func<int, string?>? resolveSpellName)
    {
        if (resolveSpellName is null) return string.Empty;

        List<string> names = new();
        foreach (SpellAbility a in formula.Abilities)
        {
            if (a.Code != RemovesSpellCode) continue;
            string? name = resolveSpellName(a.Value);
            names.Add(string.IsNullOrWhiteSpace(name) ? $"#{a.Value}" : name.Trim());
        }
        return names.Count == 0 ? string.Empty : $"Removes {string.Join(", ", names)}";
    }

    // Damage-bearing ability codes whose stored Min/MaxBase form the spell's
    // damage range — Damage (1), DrainLife (8), Damage(-MR) (17).
    private static readonly int[] _damageCodes = { 1, 8, 17 };

    // Render a damage ability whose stored range is negative or inverted
    // (min > max) verbatim — "DrainLife -60 to -20", "Damage 100 to -50". Damage
    // slots print with no "+" header and no sign normalisation. A
    // NonMagicalSpell (144) slot rewrites "Damage(-MR)" back to "Damage".
    private static string DamageAbilityLabel(in SpellFormulaInput formula, long min, long max)
    {
        int code = 0;
        bool nonMagical = false;
        foreach (SpellAbility a in formula.Abilities)
        {
            if (code == 0 && Array.IndexOf(_damageCodes, a.Code) >= 0) code = a.Code;
            if (a.Code == 144) nonMagical = true;
        }

        string name = AbilityNames.GetName(code) ?? "Dmg";
        if (nonMagical && name == "Damage(-MR)") name = "Damage";
        return min == max
            ? $"{name} {min.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : $"{name} {min.ToString(System.Globalization.CultureInfo.InvariantCulture)} to {max.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    private static string Range(long min, long max)
        => min == max ? max.ToString() : $"{min}–{max}";
}
