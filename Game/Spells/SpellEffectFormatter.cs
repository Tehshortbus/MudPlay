using FujinTerm.Game.GameData;

namespace FujinTerm.Game.Spells;

/// <summary>
/// Renders a spell's level-scaled effect string ("Dmg 14–22 · Heal 30–45 ·
/// 24 seconds · AC +10 · Removes poison") from its <see cref="SpellFormulaInput"/>.
/// A faithful port of MMUD Explorer's <c>PullSpellEQ</c> (<c>modMMudFunc.bas</c>):
/// the Dmg / Heal ranges fold the damage / heal ability codes, durations convert
/// 3-second spell-round ticks to seconds, stat-affect abilities surface signed
/// magnitudes, and <c>RemovesSpell</c> targets render by name.
/// </summary>
/// <remarks>
/// Shared by the Spell Book (<see cref="ViewModels.SpellBookRowViewModel"/>,
/// scaled to the character's level) and the Game Data Items "Other Info" pane
/// (weapon use-cast / proc effects, rendered at the spell's own base level).
/// </remarks>
public static class SpellEffectFormatter
{
    /// <summary>
    /// Seconds per spell-duration tick — MMUD Explorer's <c>SPELL_ROUND_SECS</c>
    /// (<c>modMMudFunc.bas</c>). Spell durations are stored in ticks; the display
    /// multiplies by this to show seconds. Distinct from the 5-second combat round.
    /// </summary>
    private const int SpellRoundSeconds = 3;

    /// <summary>
    /// Compose the effect string for <paramref name="formula"/> at
    /// <paramref name="level"/> (the level-scaled Min/Max/Dur figures clamp to the
    /// spell's <c>ReqLevel</c>, so a level-0 "base" render still yields real
    /// damage). Returns <c>"—"</c> when the spell produces no figure at all.
    /// </summary>
    /// <param name="resolveChain">Maps a spell number to its formula so chained
    /// end-cast (Abil 151) spells resolve to a real follow-up.</param>
    /// <param name="resolveSpellName">Maps a spell number to its name (for the
    /// RemovesSpell clause). May be <c>null</c>.</param>
    /// <param name="resolveTextblockCasts">Maps a TextBlock record number to the
    /// spells it casts (for Abil-148 expansion). May be <c>null</c>.</param>
    /// <param name="resolveMonsterName">Maps a monster number to its name, so a
    /// Summon (Abil 12) renders "Summon hydra" instead of "Summon +590". May be
    /// <c>null</c> (then Summon falls back to the raw number).</param>
    public static string Format(
        in SpellFormulaInput formula,
        int level,
        Func<int, SpellFormulaInput?> resolveChain,
        Func<int, string?>? resolveSpellName = null,
        Func<int, IReadOnlyList<KnownSpell>>? resolveTextblockCasts = null,
        Func<int, string?>? resolveMonsterName = null)
        => FormatCore(formula, level, resolveChain, resolveSpellName, resolveTextblockCasts,
                      resolveMonsterName, visited: null, suppressDuration: false);

    /// <summary>
    /// Format implementation that threads the EndCast cycle-guard set.
    /// <paramref name="visited"/> carries the spell numbers already being
    /// expanded up the EndCast chain so a spell whose chain loops back to an
    /// ancestor stops rather than recursing forever; <c>null</c> at the top
    /// level (a fresh set is allocated only when an EndCast is encountered).
    /// </summary>
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
            // dragonfire 100..-50) — MajorMUD's verbatim spell data. MME shows
            // the specific damage ability with the signed range as-is rather
            // than folding it into a positive "Dmg" figure.
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
            if (durTicks > 0) parts.Add($"{durTicks * SpellRoundSeconds} seconds");
        }

        if (affects.Length > 0) parts.Add(affects);

        // EndCast chains are whole follow-up spells, not stat affects — each
        // gets its own " · " part (after the affects rollup, before cleanup)
        // so "Dmg 100–150 · 20% EndCast spear slam knockdown (…)" reads as a
        // sequence rather than comma-mixed with bonus magnitudes.
        foreach (SpellAbility a in formula.Abilities)
        {
            if (a.Code != EndCastCode) continue;
            // A non-zero AbilVal names one chained spell; a zero AbilVal is
            // MME's random-cast marker (pool = the MinBase..MaxBase spell range).
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

    /// <summary>
    /// Ability codes the stat-affect rollup skips. Two groups, both matching
    /// MMUD Explorer's <c>PullSpellEQ</c> / <c>GetAbilityName</c> behaviour:
    /// <list type="bullet">
    /// <item>Already surfaced elsewhere — Damage (1), DrainLife (8),
    /// Damage(-MR) (17), Heal (18) fold into the Dmg / Heal figure;
    /// EndCast (151) expands into its chained spell as its own part and
    /// EndCast% (164) is consumed as that clause's prefix (see
    /// <see cref="BuildEndCast"/>); RemovesSpell (122) is rendered by name
    /// separately (see <see cref="BuildRemoves"/>).</item>
    /// <item>Display-only message slots MME hides by default (its
    /// <c>GetAbilityName</c> returns "" without <c>bForceAll</c>) —
    /// ConfuseMsg (101), DescMsg (115), StartMsg (120), ShockMsg (137).</item>
    /// </list>
    /// </summary>
    private static readonly int[] _affectSkip = { 1, 8, 17, 18, 122, 101, 115, 120, 137, 151, 164 };

    /// <summary>Summon ability code (MME Abil 12) — its <c>AbilVal</c> is the
    /// summoned monster number, resolved to a name when a resolver is supplied.</summary>
    private const int SummonCode = 12;

    /// <summary>
    /// Display-only friendly wording for the effect string — plain English for
    /// the jargon-y MME ability names that surface here. Canonical
    /// <see cref="AbilityNames"/> is deliberately left untouched (it still
    /// drives the spell's field-by-field rows + the Monster ability rollups);
    /// this only softens the at-a-glance Effect summary. Limited to flag-style
    /// codes (rendered name-only) where a phrase reads cleanly without a
    /// trailing magnitude.
    /// </summary>
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

    /// <summary>EndCast ability code (MME Abil 151). Its <c>AbilVal</c> is the
    /// spell number the cast chains into on completion.</summary>
    private const int EndCastCode = 151;

    /// <summary>EndCast% ability code (MME Abil 164) — the percentage chance
    /// the sibling <see cref="EndCastCode"/> chain fires. Consumed by
    /// <see cref="BuildEndCast"/> as the clause's prefix, never rendered as a
    /// standalone "+N" affect.</summary>
    private const int EndCastPercentCode = 164;

    /// <summary>RemovesSpell ability code (MME Abil 122).</summary>
    private const int RemovesSpellCode = 122;

    /// <summary>DispellMagic (73) / NegateAbility (124) ability codes. Their
    /// <c>AbilVal</c> is an ability-code *pointer* naming what the spell
    /// dispels / negates, not a magnitude — so they render
    /// "DispellMagic (Poison)" rather than "DispellMagic +19", matching MME's
    /// <c>GetAbilityStats</c> (<c>Case 73, 124: … &amp; " (" &amp; GetAbilityName(nValue) &amp; ")"</c>,
    /// gated by <c>If Not nValue = 0</c> so a zero AbilVal renders the bare name).</summary>
    private const int DispelMagicCode = 73;

    /// <summary>NegateAbility ability code (MME Abil 124) — shares
    /// <see cref="DispelMagicCode"/>'s ability-pointer rendering.</summary>
    private const int NegateAbilityCode = 124;

    /// <summary>
    /// TextBlock ability code (MME Abil 148). Its <c>AbilVal</c> is a
    /// TextBlock *record number* the spell executes, not a stat magnitude —
    /// so it renders unsigned ("TextBlock 869"), matching MME's NO-HEADER
    /// group in <c>GetAbilityStats</c> (<c>GetAbilityName(148) &amp; " " &amp; nValue</c>).
    /// </summary>
    private const int TextBlockCode = 148;

    /// <summary>
    /// Pure-flag ability codes — MME's <c>PullSpellEQ</c> flag group
    /// (<c>Case 23, 51, 52, 80, 97, 98, 100, 108 To 113, 119, 138, 144, 178</c>).
    /// They carry no magnitude; MME renders them name-only via
    /// <c>GetAbilityStats</c> with no value. Without this guard a flag that
    /// coexists with a damage / heal spell (so the level-scaled Min/Max is
    /// non-zero) wrongly inherits that spell's range — e.g. NonMagicalSpell
    /// (144) on "way of the dragon" printing "NonMagicalSpell +13 to +22",
    /// duplicating the "Dmg 13–22" figure.
    /// </summary>
    private static readonly int[] _flagOnly =
    {
        23, 51, 52, 80, 97, 98, 100,
        108, 109, 110, 111, 112, 113,
        119, 138, 144, 178,
    };

    /// <summary>
    /// Decode the stat-affect abilities the spell grants ("AC +10",
    /// "M.R. +5", "MaxDamage +4 to +8") for the Effect column. Mirrors MMUD
    /// Explorer's <c>PullSpellEQ</c> generic-affect path: a slot carrying a
    /// stored <c>AbilVal</c> shows that signed value; a slot with
    /// <c>AbilVal == 0</c> shows the spell's level-scaled Min/Max range
    /// instead (clamped to the obtain level, so MR / Stealth / backstab /
    /// MaxDamage figures surface their magnitude rather than name-only).
    /// Damage / heal / message / removed-spell codes are surfaced elsewhere
    /// (see <see cref="_affectSkip"/>).
    /// </summary>
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

            // Display-only friendly wording (jargon → plain English); canonical
            // AbilityNames is left intact for the field-by-field rows.
            string name = FriendlyAffect(a.Code, canonical);

            // TextBlock's AbilVal is a record number, not a magnitude. Prefer
            // the real effect(s) the textblock casts — resolved via the Spells
            // "Casted By" reverse-link — so a chained transform's duration /
            // stat bonuses surface inline. Falls back to the unsigned record
            // number (MME's NO-HEADER path, never "TextBlock +869") when the
            // textblock links to nothing.
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
            // zero AbilVal renders the bare name (MME gates the parens behind
            // If Not nValue = 0).
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
            // damage / heal range (MME's flag group renders name-only).
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

    /// <summary>
    /// Expand an EndCast (Abil 151) slot into its chained spell's effects,
    /// prefixed by the sibling EndCast% (Abil 164) chance when present —
    /// "20% EndCast spear slam knockdown (12 seconds · HoldPerson +100, …)".
    /// Recurses through nested EndCasts (a chained spell may itself EndCast),
    /// guarding against a chain that loops back to an ancestor via
    /// <paramref name="visited"/>. When the chained spell can't be resolved or
    /// produces no figure, only the named prefix shows.
    /// </summary>
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

    /// <summary>
    /// Expand an EndCast (Abil 151) whose AbilVal is 0 — MME's random-cast
    /// marker. The spell's <c>MinBase</c>..<c>MaxBase</c> fields hold a spell-
    /// NUMBER range (not a damage range); on cast the game fires one spell
    /// picked at random from that pool ("random dmg" → rocks shred / ice
    /// freezes / … each Dmg 5–15). Renders
    /// "EndCast (random): name1 / name2 / … (effect)" when every pool member
    /// shares one effect (the common parallel-element case), else pairs each
    /// name with its own effect. Empty when the spell does direct damage / heal
    /// (then MinBase/MaxBase are a magnitude range, already surfaced as "Dmg …")
    /// or the pool resolves to nothing.
    /// </summary>
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

    /// <summary>The sibling EndCast% (Abil 164) chance on the same formula —
    /// the percentage the EndCast chain fires. 0 when absent.</summary>
    private static int EndCastPercent(in SpellFormulaInput formula)
    {
        foreach (SpellAbility a in formula.Abilities)
            if (a.Code == EndCastPercentCode) return a.Value;
        return 0;
    }

    /// <summary>
    /// Expand an Abil-148 TextBlock reference into the effect(s) of the real
    /// spells that textblock casts, resolved via the Spells <c>Casted By</c>
    /// reverse-link. Each linked spell's effect is rendered with
    /// <see cref="Format"/> and joined with ", "; spells producing no
    /// figure ("—") are dropped. <b>One level deep</b> — the recursion passes
    /// no textblock resolver, so a textblock that casts a spell looping back to
    /// the same textblock can't recurse infinitely. Empty when no resolver is
    /// supplied or the textblock links to nothing.
    /// </summary>
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

    /// <summary>True when a rendered effect string is nothing but a
    /// <c>"Removes …"</c> clause (a pure-cleanup spell), so it can be sorted
    /// after the gain figures in a multi-cast textblock expansion.</summary>
    private static bool IsRemovesOnlyEffect(string effect)
        => effect.StartsWith("Removes ", StringComparison.Ordinal)
        && !effect.Contains(" · ", StringComparison.Ordinal);

    /// <summary>Signed magnitude — <c>"+10"</c> / <c>"-5"</c> (negatives carry
    /// their own minus sign, matching MME's affect headers).</summary>
    private static string Signed(long value)
        => value > 0
            ? $"+{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string SignedRange(long min, long max)
        => min == max ? Signed(min) : $"{Signed(min)} to {Signed(max)}";

    /// <summary>Unsigned magnitude range — for reference values (TextBlock
    /// record numbers) where a leading <c>+</c> would be misleading.</summary>
    private static string Unsigned(long min, long max)
        => min == max
            ? min.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : $"{min.ToString(System.Globalization.CultureInfo.InvariantCulture)} to {max.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Render any RemovesSpell (Abil 122) slots by the removed spell's name —
    /// MME's <c>PullSpellEQ</c> resolves the AbilVal via <c>GetSpellName</c>.
    /// Empty when the spell removes nothing or no name resolver is supplied.
    /// </summary>
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

    /// <summary>Damage-bearing ability codes whose stored Min/MaxBase form the
    /// spell's damage range — Damage (1), DrainLife (8), Damage(-MR) (17).</summary>
    private static readonly int[] _damageCodes = { 1, 8, 17 };

    /// <summary>
    /// Render a damage ability whose stored range is negative or inverted
    /// (min &gt; max) verbatim — "DrainLife -60 to -20", "Damage 100 to -50".
    /// Mirrors MME's <c>PullSpellEQ</c>, which prints
    /// <c>GetAbilityName(code) &amp; " " &amp; min &amp; " to " &amp; max</c> for
    /// damage slots with no "+" header and no sign normalisation. A
    /// NonMagicalSpell (144) slot rewrites "Damage(-MR)" back to "Damage",
    /// matching MME's post-pass <c>Replace</c>.
    /// </summary>
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
