namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character "Health" settings — drives <see cref="Game.Health.HealthManager"/>
/// (PR 9.B) passive HP / MA threshold behavior: rest, meditate, run, hang.
/// Stored as the <c>"Health"</c> entry in <see cref="CharacterProfile.Settings"/>.
/// </summary>
/// <remarks>
/// <para>
/// HealthManager asserts <c>HealthRecovery</c> / <c>ManaRecovery</c> gates on
/// <see cref="Game.Map.MovementCoordinator"/> when the live HP/MA crosses the
/// configured trigger; clears the gate once recovery passes the "rest until"
/// target. See <c>docs/10-phase-9-automation-engines.md</c> § Cross-cut 1.
/// </para>
/// <para>
/// Spell decisions (which heal / bless / regen spell to cast) live on
/// <see cref="SpellsSettings"/> — the routing CastingDirector (PR 9.D) reads
/// HealthSettings for the trigger threshold and SpellsSettings for the spell
/// name. The Health tab UI surfaces the threshold inputs; the spell pickers
/// live on the Spells tab.
/// </para>
/// </remarks>
public sealed class HealthSettings
{
    // ----- HP -------------------------------------------------------

    /// <summary>How <see cref="RestMaxHp"/> / <see cref="RestIfBelowHp"/> /
    /// <see cref="RunIfBelowHp"/> / <see cref="HangIfBelowHp"/> are read at
    /// engine time. Default <see cref="ThresholdMode.Percentage"/>.</summary>
    public ThresholdMode HpThresholdMode { get; set; } = ThresholdMode.Percentage;

    /// <summary>Stop resting once HP reaches this value (per
    /// <see cref="HpThresholdMode"/>). Default 95 (%).</summary>
    public int RestMaxHp { get; set; } = 95;

    /// <summary>Assert <c>HealthRecovery</c> gate when HP falls below this
    /// value. Default 60 (%).</summary>
    public int RestIfBelowHp { get; set; } = 60;

    /// <summary>Trigger flee behavior when HP falls below this value.
    /// Default 20 (%).</summary>
    public int RunIfBelowHp { get; set; } = 20;

    /// <summary>Drop the connection when HP falls below this emergency
    /// threshold. Default 5 (%).</summary>
    public int HangIfBelowHp { get; set; } = 5;

    // ----- Heal-spell thresholds (CastingDirector triggers) ---------

    /// <summary>Cast the rest-time heal spell (<see cref="SpellsSettings.MinorHealSpell"/>)
    /// during a rest cycle when HP drops below this value. Default 80 (%).</summary>
    public int HealRestTrigger { get; set; } = 80;

    /// <summary>Cast <see cref="SpellsSettings.MinorHealSpell"/> during combat
    /// when HP drops below this value. Default 70 (%).</summary>
    public int MinorHealCombatTrigger { get; set; } = 70;

    /// <summary>Cast <see cref="SpellsSettings.MajorHealSpell"/> during combat
    /// when HP drops below this value. Default 40 (%).</summary>
    public int MajorHealCombatTrigger { get; set; } = 40;

    // ----- MA / Kai -------------------------------------------------

    /// <summary>How <see cref="RestMaxMa"/> / <see cref="RestIfBelowMa"/> /
    /// <see cref="RunIfBelowMa"/> / <see cref="BlessIfAboveMa"/> are read at
    /// engine time. Default <see cref="ThresholdMode.Percentage"/>.</summary>
    public ThresholdMode MaThresholdMode { get; set; } = ThresholdMode.Percentage;

    /// <summary>Stop resting (caster pool) once MA reaches this value.
    /// Default 95 (%).</summary>
    public int RestMaxMa { get; set; } = 95;

    /// <summary>Assert <c>ManaRecovery</c> gate when MA falls below this
    /// value. Default 30 (%).</summary>
    public int RestIfBelowMa { get; set; } = 30;

    /// <summary>Trigger flee behavior when the caster pool drops below this.
    /// Default 10 (%).</summary>
    public int RunIfBelowMa { get; set; } = 10;

    /// <summary>Re-cast party / self buffs once MA recovers past this value.
    /// CastingDirector trigger. Default 70 (%).</summary>
    public int BlessIfAboveMa { get; set; } = 70;

    // ----- Meditation -----------------------------------------------

    /// <summary>Use the class-specific <c>meditate</c> command on classes that
    /// have it. Default false.</summary>
    public bool UseMeditateAbility { get; set; }

    /// <summary>Sit and meditate first when both HP and MA are low. Default
    /// false (rest covers both).</summary>
    public bool MeditateBeforeResting { get; set; }

    // ----- Resting commands -----------------------------------------

    /// <summary>Sent right before <c>rest</c> / <c>meditate</c>. <c>^M</c> and
    /// <c>;</c> are both treated as carriage returns so multiple commands can
    /// be chained. Default empty.</summary>
    public string PreRestCommand { get; set; } = string.Empty;

    /// <summary>Sent right after standing up from rest / meditate. Same
    /// chaining rules as <see cref="PreRestCommand"/>. Default empty.</summary>
    public string PostRestCommand { get; set; } = string.Empty;
}
