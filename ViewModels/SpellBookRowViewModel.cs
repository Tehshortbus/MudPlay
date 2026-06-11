using FujinTerm.Game.GameData;
using FujinTerm.Game.Spells;

namespace FujinTerm.ViewModels;

/// <summary>
/// One immutable row in the <see cref="SpellBookViewModel"/> table: a single
/// <see cref="KnownSpell"/> from the class's learnable list, paired with
/// whether the character has obtained it and the level-scaled effect /
/// mana figures (via <see cref="SpellCalculator"/>) for the book's current
/// <paramref name="level"/>.
/// </summary>
/// <remarks>
/// Rows are throwaway — the window rebuilds the whole collection whenever
/// <see cref="SpellbookState.Changed"/> fires (class swap, level change, or
/// obtained-set update), so there's no per-row change notification.
/// </remarks>
public sealed class SpellBookRowViewModel
{
    public SpellBookRowViewModel(
        KnownSpell spell,
        bool isObtained,
        int level,
        Func<int, SpellFormulaInput?> resolveChain,
        Func<int, string?>? resolveSpellName = null,
        Func<int, IReadOnlyList<KnownSpell>>? resolveTextblockCasts = null)
    {
        Short = spell.Short;
        Name = spell.Name;
        ReqLevel = spell.ReqLevel;
        IsObtained = isObtained;

        Mana = SpellCalculator.ManaCost(spell.Formula);
        ManaText = Mana.ToString();
        EffectText = BuildEffect(spell.Formula, level, resolveChain, resolveSpellName, resolveTextblockCasts);
        FormulaText = BuildFormula(spell.Formula);
    }

    /// <summary>
    /// Seconds per spell-duration tick — MMUD Explorer's
    /// <c>SPELL_ROUND_SECS</c> (<c>modMMudFunc.bas</c>). Spell durations are
    /// stored in ticks; the display multiplies by this to show seconds.
    /// Distinct from the 5-second combat round (<c>ROUND_SECS</c>).
    /// </summary>
    private const int SpellRoundSeconds = 3;

    /// <summary>The verbatim <c>Spells.Short</c> cast-code the player types.</summary>
    public string Short { get; }

    /// <summary>The full <c>Spells.Name</c>.</summary>
    public string Name { get; }

    /// <summary>Level the spell unlocks at (<c>Spells.ReqLevel</c>).</summary>
    public int ReqLevel { get; }

    /// <summary>True when the character has learned this spell.</summary>
    public bool IsObtained { get; }

    /// <summary>Checkmark glyph for the obtained column ("✓" or empty).</summary>
    public string ObtainedGlyph => IsObtained ? "✓" : string.Empty;

    /// <summary>The unlock level as a bare number — the unlock-level cell.</summary>
    public string ReqLevelText => ReqLevel.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Per-round mana cost — numeric, for column sorting.</summary>
    public long Mana { get; }

    /// <summary>Per-round mana cost at the spell's energy multiplier.</summary>
    public string ManaText { get; }

    /// <summary>
    /// Level-scaled effect at the book's current level: "Dmg 14–22",
    /// "Heal 30–45", "Dur 8", plus any decoded stat-affect abilities the
    /// spell grants ("AC +10", "Strength +3"), joined by " · ". "—" when
    /// the spell produces no figure at all.
    /// </summary>
    public string EffectText { get; }

    /// <summary>
    /// Ability codes the stat-affect rollup skips. Two groups, both matching
    /// MMUD Explorer's <c>PullSpellEQ</c> / <c>GetAbilityName</c> behaviour:
    /// <list type="bullet">
    /// <item>Already surfaced elsewhere — Damage (1), DrainLife (8),
    /// Damage(-MR) (17), Heal (18) fold into the Dmg / Heal figure;
    /// EndCast (151) is a cast-chaining marker; RemovesSpell (122) is rendered
    /// by name separately (see <see cref="BuildEffect"/>).</item>
    /// <item>Display-only message slots MME hides by default (its
    /// <c>GetAbilityName</c> returns "" without <c>bForceAll</c>) —
    /// ConfuseMsg (101), DescMsg (115), StartMsg (120), ShockMsg (137).</item>
    /// </list>
    /// </summary>
    private static readonly int[] _affectSkip = { 1, 8, 17, 18, 151, 122, 101, 115, 120, 137 };

    /// <summary>RemovesSpell ability code (MME Abil 122).</summary>
    private const int RemovesSpellCode = 122;

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
    /// The raw scaling formula (base value + per-level slope) shown as a
    /// tooltip so the player can see how the effect grows, independent of
    /// the current level. Empty when the spell has no scaling magnitude.
    /// </summary>
    public string FormulaText { get; }

    private static string BuildEffect(
        in SpellFormulaInput formula,
        int level,
        Func<int, SpellFormulaInput?> resolveChain,
        Func<int, string?>? resolveSpellName,
        Func<int, IReadOnlyList<KnownSpell>>? resolveTextblockCasts = null)
    {
        List<string> parts = new();

        long minDmg = SpellCalculator.MinDamage(formula, level, resolveChain);
        long maxDmg = SpellCalculator.MaxDamage(formula, level, resolveChain);
        if (maxDmg > 0) parts.Add($"Dmg {Range(minDmg, maxDmg)}");

        long minHeal = SpellCalculator.MinHeal(formula, level, resolveChain);
        long maxHeal = SpellCalculator.MaxHeal(formula, level, resolveChain);
        if (maxHeal > 0) parts.Add($"Heal {Range(minHeal, maxHeal)}");

        // Durations are stored in 3-second spell-round ticks; show seconds.
        long dur = SpellCalculator.Duration(formula, level);
        if (dur > 0) parts.Add($"{dur * SpellRoundSeconds} seconds");

        string removes = BuildRemoves(formula, resolveSpellName);
        if (removes.Length > 0) parts.Add(removes);

        string affects = BuildAffects(formula, level, resolveChain, resolveSpellName, resolveTextblockCasts);
        if (affects.Length > 0) parts.Add(affects);

        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }

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
        Func<int, IReadOnlyList<KnownSpell>>? resolveTextblockCasts)
    {
        (long affMin, long affMax) = SpellCalculator.AffectMagnitude(formula, level);

        List<string> parts = new();
        foreach (SpellAbility a in formula.Abilities)
        {
            if (a.Code == 0 || Array.IndexOf(_affectSkip, a.Code) >= 0) continue;
            string? name = AbilityNames.GetName(a.Code);
            if (name is null) continue;

            // TextBlock's AbilVal is a record number, not a magnitude. Prefer
            // the real effect(s) the textblock casts — resolved via the Spells
            // "Casted By" reverse-link — so a chained transform's duration /
            // stat bonuses surface inline. Falls back to the unsigned record
            // number (MME's NO-HEADER path, never "TextBlock +869") when the
            // textblock links to nothing.
            if (a.Code == TextBlockCode)
            {
                string expanded = ExpandTextblockCasts(
                    a.Value, level, resolveChain, resolveSpellName, resolveTextblockCasts);
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
    /// Expand an Abil-148 TextBlock reference into the effect(s) of the real
    /// spells that textblock casts, resolved via the Spells <c>Casted By</c>
    /// reverse-link. Each linked spell's effect is rendered with
    /// <see cref="BuildEffect"/> and joined with ", "; spells producing no
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
        Func<int, IReadOnlyList<KnownSpell>>? resolveTextblockCasts)
    {
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
            string effect = BuildEffect(s.Formula, level, resolveChain, resolveSpellName, resolveTextblockCasts: null);
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

    private static string Range(long min, long max)
        => min == max ? max.ToString() : $"{min}–{max}";

    private static string BuildFormula(in SpellFormulaInput formula)
    {
        List<string> parts = new();
        if (formula.MinBase != 0 || formula.MaxBase != 0)
            parts.Add($"base {formula.MinBase}–{formula.MaxBase}");
        if (formula.MinIncLVLs > 0 && formula.MinInc != 0)
            parts.Add($"min +{formula.MinInc}/{formula.MinIncLVLs}lv");
        if (formula.MaxIncLVLs > 0 && formula.MaxInc != 0)
            parts.Add($"max +{formula.MaxInc}/{formula.MaxIncLVLs}lv");
        if (formula.Dur != 0 || (formula.DurIncLVLs > 0 && formula.DurInc != 0))
        {
            string slope = formula.DurIncLVLs > 0 && formula.DurInc != 0
                ? $" +{formula.DurInc}/{formula.DurIncLVLs}lv"
                : string.Empty;
            parts.Add($"dur {formula.Dur}{slope}");
        }
        return string.Join(", ", parts);
    }
}
