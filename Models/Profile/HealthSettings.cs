namespace FujinTerm.Models.Profile;

// Per-character "Health" settings — drives Game.Health.HealthManager passive
// HP / MA threshold behavior: rest, meditate, run, hang. Stored as the "Health"
// entry in CharacterProfile.Settings.
//
// HealthManager asserts HealthRecovery / ManaRecovery gates on
// Game.Map.MovementCoordinator when the live HP/MA crosses the configured
// trigger; clears the gate once recovery passes the "rest until" target.
//
// Spell decisions (which heal / bless / regen spell to cast) live on
// SpellsSettings — the routing CastingDirector reads HealthSettings for the
// trigger threshold and SpellsSettings for the spell name. The Health tab UI
// surfaces the threshold inputs; the spell pickers live on the Spells tab.
public sealed class HealthSettings
{
    // ----- HP -------------------------------------------------------

    // How RestMaxHp / RestIfBelowHp / RunIfBelowHp / HangIfBelowHp are read at
    // engine time. Default Percentage.
    public ThresholdMode HpThresholdMode { get; set; } = ThresholdMode.Percentage;

    // Stop resting once HP reaches this value (per HpThresholdMode). Default 95 (%).
    public int RestMaxHp { get; set; } = 95;

    // Assert HealthRecovery gate when HP falls below this value. Default 60 (%).
    public int RestIfBelowHp { get; set; } = 60;

    // Trigger flee behavior when HP falls below this value. Default 20 (%).
    public int RunIfBelowHp { get; set; } = 20;

    // Drop the connection when HP falls below this emergency threshold. Default 5 (%).
    // A point on one continuous HP scale (per HpThresholdMode): from 100 %/max down
    // through 0 into the negatives — HP% goes negative while bleeding out, as the
    // game's par display shows. It may go negative in either mode, down to the
    // per-BBS death floor (BbsProfile.PlayerDiesAtHp), letting the player set the
    // hangup deep in the bleeding-out band, closer to death. There is no zero
    // sentinel: turning the auto-hangup off is GeneralSettings.DisableHangups.
    public int HangIfBelowHp { get; set; } = 5;

    // ----- Heal-spell thresholds (CastingDirector triggers) ---------

    // Cast the rest-time heal spell (SpellsSettings.MinorHealSpell) during a
    // rest cycle when HP drops below this value. Default 80 (%).
    public int HealRestTrigger { get; set; } = 80;

    // Cast SpellsSettings.MinorHealSpell during combat when HP drops below this
    // value. Default 70 (%).
    public int MinorHealCombatTrigger { get; set; } = 70;

    // Cast SpellsSettings.MajorHealSpell during combat when HP drops below this
    // value. Default 40 (%).
    public int MajorHealCombatTrigger { get; set; } = 40;

    // ----- MA / Kai -------------------------------------------------

    // How RestMaxMa / RestIfBelowMa / RunIfBelowMa / BlessIfAboveMa are read at
    // engine time. Default Percentage.
    public ThresholdMode MaThresholdMode { get; set; } = ThresholdMode.Percentage;

    // Stop resting (caster pool) once MA reaches this value. Default 95 (%).
    public int RestMaxMa { get; set; } = 95;

    // Assert ManaRecovery gate when MA falls below this value. Default 30 (%).
    public int RestIfBelowMa { get; set; } = 30;

    // Trigger flee behavior when the caster pool drops below this. Default 10 (%).
    public int RunIfBelowMa { get; set; } = 10;

    // Re-cast party / self buffs once MA recovers past this value.
    // CastingDirector trigger. Default 70 (%).
    public int BlessIfAboveMa { get; set; } = 70;

    // Mana-floor for heal spells while resting / idle (out of combat):
    // CastingDirector only casts a heal when the caster pool sits at or above
    // this value (per MaThresholdMode), so a low pool regenerates instead of
    // being drained on heals. 0 disables the gate. Default 50 (%).
    public int HealIfAboveMaResting { get; set; } = 50;

    // Mana-floor for heal spells while in combat — same meaning as
    // HealIfAboveMaResting but applied during combat, where survival usually
    // outweighs mana conservation. 0 disables the gate (always heal). Default 0.
    public int HealIfAboveMaCombat { get; set; }

    // ----- Resting Options ------------------------------------------

    // Use the class-specific meditate command on classes that have it. Default false.
    public bool UseMeditateAbility { get; set; }

    // Sit and meditate first when both HP and MA are low. Default false (rest
    // covers both).
    public bool MeditateBeforeResting { get; set; }

    // Rest while hidden/sneaking without leaving stealth, on classes that have the
    // ShadowRest ability (Paradigm; game-data code 1103). When on, a solo, stealthed
    // ShadowRest character rests in place even with a monster in the room — the game
    // keeps it un-attacked while stealthed. Inert on classes without the ability.
    // Default false.
    public bool UtilizeShadowRest { get; set; }

    // ----- Resting commands -----------------------------------------

    // Sent right before rest / meditate. ^M and ; are both treated as carriage
    // returns so multiple commands can be chained. Default empty.
    public string PreRestCommand { get; set; } = string.Empty;

    // Sent right after standing up from rest / meditate. Same chaining rules as
    // PreRestCommand. Default empty.
    public string PostRestCommand { get; set; } = string.Empty;
}
