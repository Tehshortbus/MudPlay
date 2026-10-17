using MudPlay.Game;

namespace MudPlay.Models.Profile;

// Per-character "Spells" settings — drives Game.Spells.CastingDirector's
// self-cast decisions: which spell to use for heal / regen / cure / buff slots,
// plus the between-round priority order across categories. Stored as the
// "Spells" entry in CharacterProfile.Settings.
//
// Thresholds that trigger heal casts (HP / MA level at which a spell fires) live
// on HealthSettings — the Health tab UI surfaces them. SpellsSettings only owns
// the spell names. Party-cast picks live on PartySettings.
//
// Priority slots are 1–10 (lower = earlier). Two pairs share priority (Minor
// party heal covers single + party AOE versions; same for Major) because the
// engine picks single vs AOE at cast time based on how many party members are
// below threshold — only the category ordering lives here.
//
// The defaults below are the order a fresh profile starts with. A profile saved
// before the Priority buffs category existed is put right once, on load
// (ProfileMigrations): a list nobody had reordered takes these defaults, an edited
// one keeps its order with Priority buffs added at the end.
public sealed class SpellsSettings
{
    // ----- Category priority (1-10) ---------------------------------

    // Priority slot for the last-resort EmergencyHealSpell. Defaults to 1 so an
    // emergency leads every between-round cast, but it's user-reorderable like
    // any other slot.
    public int PriorityEmergencyHeal { get; set; } = 1;

    // Priority slot shared by Party tab's Major single-target heal and Major
    // AOE party heal.
    public int PriorityMajorPartyHeal { get; set; } = 2;

    // Priority slot shared by Party tab's Minor single-target heal and Minor
    // AOE party heal.
    public int PriorityMinorPartyHeal { get; set; } = 3;

    // Priority slot for this tab's MajorHealSpell.
    public int PriorityMajorSelfHeal { get; set; } = 4;

    // Priority slot for the buffs ticked "Priority buff" in the Buff Watchdog:
    // the ones worth casting ahead of a minor heal (a damage shield, a ward).
    public int PriorityPriorityBuffs { get; set; } = 5;

    // Priority slot for this tab's MinorHealSpell.
    public int PriorityMinorSelfHeal { get; set; } = 6;

    // Priority slot for the downed-ally rescue heal. Formerly hardcoded to lead;
    // now a reorderable slot.
    public int PriorityDownedAllyHeal { get; set; } = 7;

    // Priority slot for cure spells (the four cure-* picks below).
    public int PriorityCuring { get; set; } = 8;

    // Priority slot for every other buff / bless cast.
    public int PriorityBuffing { get; set; } = 9;

    // Priority slot for between-round debuffs (CombatSettings' debuff slots).
    public int PriorityDebuffing { get; set; } = 10;

    // ----- Healing / regeneration -----------------------------------

    // Cheap self-heal — fires when HealthSettings.MinorHealCombatTrigger trips
    // during combat, or HealthSettings.HealRestTrigger during rest.
    public string? MinorHealSpell { get; set; }

    // Expensive self-heal — fires when HealthSettings.MajorHealCombatTrigger
    // trips.
    public string? MajorHealSpell { get; set; }

    // Last-resort self-heal — fires when HealthSettings.EmergencyHealTrigger
    // trips, unconditionally ahead of every other between-round category
    // (major/minor heal, cures, buffs, debuffs — see
    // Game.Spells.CastingDirector.PrioritisedCategories). Falls back to
    // MajorHealSpell then MinorHealSpell when unset, so an emergency still
    // fires something rather than nothing.
    public string? EmergencyHealSpell { get; set; }

    // Auto-regen utility (e.g. troll-skin) cast during downtime.
    public string? HpRegenSpell { get; set; }

    // Auto-regen utility for the mana pool. Holds either a code-145 regen-rate
    // roll spell (nature tap / mana flux — a persistent ± modifier to the
    // mana-regen rate that the reroll logic can re-cast when it lands below
    // ManaRegenRerollThreshold) or a mana heal-over-time buff (chaos surge —
    // recast on expiry like an HP HoT). The reroll path only engages for roll
    // spells; HoTs fall through to the normal recast-when-due behaviour.
    public string? MaRegenSpell { get; set; }

    // Reroll the MaRegenSpell when its rolled mana-regen contribution (the
    // code-145 spells: slice, positive or negative) lands below this value — a
    // bad roll drags the mana-regen rate down, so the engine re-casts to try for
    // a better one. null disables rerolling (the spell then just recasts on
    // expiry). Seeded from the spell's level-scaled roll range on the Spells
    // tab.
    public int? ManaRegenRerollThreshold { get; set; }

    // Maximum consecutive rerolls of the MaRegenSpell per cast cycle before
    // accepting whatever landed — a hard ceiling so a persistently unlucky
    // streak can't drain the mana pool. Rerolling also hard-stops early if
    // paying for the next cast would drop mana below the buff floor. Defaults to
    // 3.
    public int ManaRegenRerollCap { get; set; } = 3;

    // LEGACY: the old dedicated "cast when HP / mana is full" picks. A buff slot's
    // OnlyWhenHpFull / OnlyWhenMaFull does this now; the profile migration folds
    // these into such a slot. Kept so an older profile (or a MegaMUD import) still
    // loads and migrates.
    public string? WhenHpFullSpell { get; set; }
    public string? WhenMaFullSpell { get; set; }

    // ----- Cures ----------------------------------------------------

    // Cure-paralyze / cure-hold spell — canonically freedom in stock MajorMUD.
    public string? CureHoldsSpell { get; set; }

    // Cure-poison spell.
    public string? CurePoisonSpell { get; set; }

    // Cure-disease spell.
    public string? CureDiseaseSpell { get; set; }

    // Cure-blindness spell.
    public string? CureBlindnessSpell { get; set; }

    // "Cure after combat", one per cure: that cure waits until the fight is over
    // instead of taking a round's between-round cast. Off = cure as soon as no
    // higher-ranked cast is due.
    public bool CureHoldsAfterCombat { get; set; }
    public bool CurePoisonAfterCombat { get; set; }
    public bool CureDiseaseAfterCombat { get; set; }
    public bool CureBlindnessAfterCombat { get; set; }

    // ----- Utility --------------------------------------------------

    // LEGACY: the old room-light spell pick. A self buff slot flagged OnlyWhenDark
    // is what Auto-Light casts now; the profile migration folds this into such a
    // slot. Kept so an older profile (or a MegaMUD import) still loads and migrates.
    public string? RoomLightSpell { get; set; }

    // ----- Bless slots (sparse) -------------------------------------

    // Self-buff picks Game.Spells.CastingDirector recasts when the buff isn't
    // active, keyed by 1-based slot index (slot order is cast priority). Sparse:
    // only filled slots persist, so a profile built on a 15-slot
    // RealmType.ParaMud realm loads on a 10-slot RealmType.Stock realm without
    // carrying ghost slots — and the out-of-range picks round-trip back when it
    // returns to a wider realm. Each value is the 4-letter cast-code the game
    // recognises.
    public Dictionary<int, string> BlessSlots { get; set; } = new();

    // Per-bless-slot recast lead: how many seconds before a self-buff's tracked
    // expiry the CastingDirector recasts it. Sparse and keyed by the same 1-based
    // slot index as BlessSlots — an absent key means DefaultBlessRecastMarginSec.
    // 0 = don't recast until the buff has actually expired (a wear-off message or
    // the tracked timer running out). Only non-default values for filled slots
    // persist, so a profile that never touches the pickers stays clean and the
    // out-of-range picks round-trip like BlessSlots itself.
    public Dictionary<int, int> BlessSlotRecastMargins { get; set; } = new();

    // Default recast lead when a bless slot (self or party) doesn't override it.
    // Shared with BuffSlot and the CastingDirector so the "15s before expiry"
    // default lives in one place.
    public const int DefaultBlessRecastMarginSec = 15;

    // LEGACY: the two switches every self-buff shared. Each buff slot carries its
    // own now (BuffSlot.BlessWhileResting / BlessDuringCombat); profile schema 5
    // copied these onto the slots that existed then. Kept so an older profile still
    // loads and migrates.
    public bool SelfBlessWhileResting { get; set; }
    public bool SelfBlessDuringCombat { get; set; }

    // ----- Ailment handling / coordination ------------------------------
    // The four "Ignore X" gates suppress the automatic @wait that
    // AilmentSyncEngine telepaths to the party leader when we catch that
    // ailment. Default UNCHECKED — most parties want to pause on every
    // ailment; the toggle is for edge cases ("we're at the boss fight,
    // don't pause for a poison tick"). Always-on triggers (over-encumbered,
    // MovementPrevented, Stunned) bypass these flags.

    // Each Ignore<X> is the single "I don't care about this ailment" toggle: it
    // suppresses BOTH the @wait telepath to the leader AND the say-channel announce
    // (".@blind" etc.) AilmentSyncEngine emits for other clients to mirror. Default
    // false (pause + announce).

    // Ignore poison. Default false.
    public bool IgnorePoison    { get; set; }

    // Ignore blindness. Default false.
    public bool IgnoreBlindness { get; set; }

    // Ignore confusion. Default false.
    public bool IgnoreConfusion { get; set; }

    // Ignore disease. Default false.
    public bool IgnoreDiseased  { get; set; }
}
