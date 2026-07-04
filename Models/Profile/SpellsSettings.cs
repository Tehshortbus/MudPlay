using FujinTerm.Game;

namespace FujinTerm.Models.Profile;

// Per-character "Spells" settings — drives Game.Spells.CastingDirector's
// self-cast decisions: which spell to use for heal / regen / cure / buff slots,
// plus the between-round priority order across categories. Stored as the
// "Spells" entry in CharacterProfile.Settings.
//
// Thresholds that trigger heal casts (HP / MA level at which a spell fires) live
// on HealthSettings — the Health tab UI surfaces them. SpellsSettings only owns
// the spell names. Party-cast picks live on PartySettings.
//
// Priority slots are 1–7 (lower = earlier). Two pairs share priority (Minor
// party heal covers single + party AOE versions; same for Major) because the
// engine picks single vs AOE at cast time based on how many party members are
// below threshold — only the category ordering lives here.
public sealed class SpellsSettings
{
    // ----- Category priority (1-7) ----------------------------------

    // Priority slot shared by Party tab's Minor single-target heal and Minor
    // AOE party heal.
    public int PriorityMinorPartyHeal { get; set; } = 1;

    // Priority slot shared by Party tab's Major single-target heal and Major
    // AOE party heal.
    public int PriorityMajorPartyHeal { get; set; } = 2;

    // Priority slot for this tab's MinorHealSpell.
    public int PriorityMinorSelfHeal { get; set; } = 3;

    // Priority slot for this tab's MajorHealSpell.
    public int PriorityMajorSelfHeal { get; set; } = 4;

    // Priority slot for cure spells (the four cure-* picks below).
    public int PriorityCuring { get; set; } = 5;

    // Priority slot for buff / bless casts (the bless slots).
    public int PriorityBuffing { get; set; } = 6;

    // Priority slot for between-round debuffs (CombatSettings' debuff slots).
    public int PriorityDebuffing { get; set; } = 7;

    // ----- Healing / regeneration -----------------------------------

    // Cheap self-heal — fires when HealthSettings.MinorHealCombatTrigger trips
    // during combat, or HealthSettings.HealRestTrigger during rest.
    public string? MinorHealSpell { get; set; }

    // Expensive self-heal — fires when HealthSettings.MajorHealCombatTrigger
    // trips.
    public string? MajorHealSpell { get; set; }

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

    // Cast when HP is full and we're between actions (room spell, debuff, etc.).
    public string? WhenHpFullSpell { get; set; }

    // Cast when MA is full and we're between actions.
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

    // ----- Utility --------------------------------------------------

    // Cast when entering a dark room (pairs with the future Settings.Other
    // "Provide light in dimly lit rooms" toggle).
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

    // Self-bless slots the Spells tab shows on a Stock realm. Stock MajorMUD
    // caps active effects at 10 (buffs and debuffs share them), so 10 is the
    // working ceiling — nothing beyond it can stay up anyway.
    public const int StockBlessSlotCount = 10;

    // Self-bless slots shown on a ParaMud realm. The effect cap is lifted there,
    // so item / self / party buffs comfortably exceed 10; 15 is reasonable
    // headroom for the common case.
    public const int ParaMudBlessSlotCount = 15;

    // How many self-bless slots the Spells tab surfaces for a realm family —
    // ParaMudBlessSlotCount on ParaMud, otherwise StockBlessSlotCount.
    public static int BlessSlotCountFor(RealmType realm) =>
        realm == RealmType.ParaMud ? ParaMudBlessSlotCount : StockBlessSlotCount;

    // ----- Ailment handling / coordination ------------------------------
    // The four "Ignore X" gates suppress the automatic @wait that
    // AilmentSyncEngine telepaths to the party leader when we catch that
    // ailment. Default UNCHECKED — most parties want to pause on every
    // ailment; the toggle is for edge cases ("we're at the boss fight,
    // don't pause for a poison tick"). Always-on triggers (over-encumbered,
    // MovementPrevented, Stunned) bypass these flags.

    // Don't @wait for poison. Default false (pause).
    public bool IgnorePoison    { get; set; }

    // Don't @wait for blindness. Default false (pause).
    public bool IgnoreBlindness { get; set; }

    // Don't @wait for confusion. Default false (pause).
    public bool IgnoreConfusion { get; set; }

    // Don't @wait for disease. Default false (pause).
    public bool IgnoreDiseased  { get; set; }

    // The four "do not announce" gates suppress the say-channel broadcast
    // (".@poisoned" etc.) AilmentSyncEngine emits so other FujinTerm clients
    // in the room can mirror our state. Independent of the Ignore* flags —
    // a party may want the leader to pause but not broadcast on say, or
    // vice-versa. Default UNCHECKED = announce.

    // Suppress the say-announce when poisoned. Default false (announce).
    public bool DoNotAnnouncePoison    { get; set; }

    // Suppress the say-announce when blinded. Default false (announce).
    public bool DoNotAnnounceBlindness { get; set; }

    // Suppress the say-announce when confused. Default false (announce).
    public bool DoNotAnnounceConfusion { get; set; }

    // Suppress the say-announce when diseased. Default false (announce).
    public bool DoNotAnnounceDiseased  { get; set; }
}
