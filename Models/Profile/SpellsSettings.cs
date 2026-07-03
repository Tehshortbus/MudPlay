using FujinTerm.Game;

namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character "Spells" settings — drives the Phase 9 PR 9.D
/// <see cref="Game.Spells.CastingDirector"/>'s self-cast decisions: which
/// spell to use for heal / regen / cure / buff slots, plus the
/// between-round priority order across categories. Stored as the
/// <c>"Spells"</c> entry in <see cref="CharacterProfile.Settings"/>.
/// </summary>
/// <remarks>
/// <para>
/// Thresholds that trigger heal casts (HP / MA level at which a spell fires)
/// live on <see cref="HealthSettings"/> — the Health tab UI surfaces them.
/// SpellsSettings only owns the spell names. Party-cast picks live on
/// <see cref="PartySettings"/> (per the Phase 9 plan's tab split).
/// </para>
/// <para>
/// Priority slots are 1–7 (lower = earlier). Two pairs share priority
/// (Minor party heal covers single + party AOE versions; same for Major)
/// because the engine picks single vs AOE at cast time based on how many
/// party members are below threshold — only the category ordering lives
/// here.
/// </para>
/// </remarks>
public sealed class SpellsSettings
{
    // ----- Category priority (1-7) ----------------------------------

    /// <summary>Priority slot shared by Party tab's Minor single-target heal
    /// and Minor AOE party heal.</summary>
    public int PriorityMinorPartyHeal { get; set; } = 1;

    /// <summary>Priority slot shared by Party tab's Major single-target heal
    /// and Major AOE party heal.</summary>
    public int PriorityMajorPartyHeal { get; set; } = 2;

    /// <summary>Priority slot for this tab's <see cref="MinorHealSpell"/>.</summary>
    public int PriorityMinorSelfHeal { get; set; } = 3;

    /// <summary>Priority slot for this tab's <see cref="MajorHealSpell"/>.</summary>
    public int PriorityMajorSelfHeal { get; set; } = 4;

    /// <summary>Priority slot for cure spells (the four cure-* picks below).</summary>
    public int PriorityCuring { get; set; } = 5;

    /// <summary>Priority slot for buff / bless casts (the bless slots).</summary>
    public int PriorityBuffing { get; set; } = 6;

    /// <summary>Priority slot for between-round debuffs (CombatSettings'
    /// debuff slots).</summary>
    public int PriorityDebuffing { get; set; } = 7;

    // ----- Healing / regeneration -----------------------------------

    /// <summary>Cheap self-heal — fires when
    /// <see cref="HealthSettings.MinorHealCombatTrigger"/> trips during
    /// combat, or <see cref="HealthSettings.HealRestTrigger"/> during rest.</summary>
    public string? MinorHealSpell { get; set; }

    /// <summary>Expensive self-heal — fires when
    /// <see cref="HealthSettings.MajorHealCombatTrigger"/> trips.</summary>
    public string? MajorHealSpell { get; set; }

    /// <summary>Auto-regen utility (e.g. troll-skin) cast during downtime.</summary>
    public string? HpRegenSpell { get; set; }

    /// <summary>Auto-regen utility for the mana pool. Holds either a
    /// code-145 regen-rate <i>roll</i> spell (nature tap / mana flux — a
    /// persistent ± modifier to the mana-regen rate that the reroll logic can
    /// re-cast when it lands below <see cref="ManaRegenRerollThreshold"/>) or a
    /// mana heal-over-time buff (chaos surge — recast on expiry like an HP
    /// HoT). The reroll path only engages for roll spells; HoTs fall through to
    /// the normal recast-when-due behaviour.</summary>
    public string? MaRegenSpell { get; set; }

    /// <summary>
    /// Reroll the <see cref="MaRegenSpell"/> when its rolled mana-regen
    /// contribution (the code-145 <c>spells:</c> slice, positive or negative)
    /// lands below this value — a bad roll drags the mana-regen rate down, so
    /// the engine re-casts to try for a better one. <c>null</c> disables
    /// rerolling (the spell then just recasts on expiry). Seeded from the
    /// spell's level-scaled roll range on the Spells tab.
    /// </summary>
    public int? ManaRegenRerollThreshold { get; set; }

    /// <summary>
    /// Maximum consecutive rerolls of the <see cref="MaRegenSpell"/> per cast
    /// cycle before accepting whatever landed — a hard ceiling so a persistently
    /// unlucky streak can't drain the mana pool. Rerolling also hard-stops early
    /// if paying for the next cast would drop mana below the buff floor.
    /// Defaults to 3.
    /// </summary>
    public int ManaRegenRerollCap { get; set; } = 3;

    /// <summary>Cast when HP is full and we're between actions (room spell,
    /// debuff, etc.).</summary>
    public string? WhenHpFullSpell { get; set; }

    /// <summary>Cast when MA is full and we're between actions.</summary>
    public string? WhenMaFullSpell { get; set; }

    // ----- Cures ----------------------------------------------------

    /// <summary>Cure-paralyze / cure-hold spell — canonically <c>freedom</c>
    /// in stock MajorMUD.</summary>
    public string? CureHoldsSpell { get; set; }

    /// <summary>Cure-poison spell.</summary>
    public string? CurePoisonSpell { get; set; }

    /// <summary>Cure-disease spell.</summary>
    public string? CureDiseaseSpell { get; set; }

    /// <summary>Cure-blindness spell.</summary>
    public string? CureBlindnessSpell { get; set; }

    // ----- Utility --------------------------------------------------

    /// <summary>Cast when entering a dark room (pairs with the future
    /// Settings.Other "Provide light in dimly lit rooms" toggle).</summary>
    public string? RoomLightSpell { get; set; }

    // ----- Bless slots (sparse) -------------------------------------

    /// <summary>
    /// Self-buff picks the <see cref="Game.Spells.CastingDirector"/> recasts
    /// when the buff isn't active, keyed by 1-based slot index (slot order is
    /// cast priority). Sparse: only filled slots persist, so a profile built
    /// on a 15-slot <see cref="RealmType.ParaMud"/> realm loads on a 10-slot
    /// <see cref="RealmType.Stock"/> realm without carrying ghost slots — and
    /// the out-of-range picks round-trip back when it returns to a wider realm.
    /// Each value is the 4-letter cast-code the game recognises.
    /// </summary>
    public Dictionary<int, string> BlessSlots { get; set; } = new();

    /// <summary>Self-bless slots the Spells tab shows on a Stock realm. Stock
    /// MajorMUD caps active effects at 10 (buffs and debuffs share them), so 10
    /// is the working ceiling — nothing beyond it can stay up anyway.</summary>
    public const int StockBlessSlotCount = 10;

    /// <summary>Self-bless slots shown on a ParaMud realm. The effect cap is
    /// lifted there, so item / self / party buffs comfortably exceed 10; 15 is
    /// reasonable headroom for the common case.</summary>
    public const int ParaMudBlessSlotCount = 15;

    /// <summary>How many self-bless slots the Spells tab surfaces for a realm
    /// family — <see cref="ParaMudBlessSlotCount"/> on ParaMud, otherwise
    /// <see cref="StockBlessSlotCount"/>.</summary>
    public static int BlessSlotCountFor(RealmType realm) =>
        realm == RealmType.ParaMud ? ParaMudBlessSlotCount : StockBlessSlotCount;

    // ----- Ailment handling / coordination ------------------------------
    // The four "Ignore X" gates suppress the automatic @wait that
    // AilmentSyncEngine telepaths to the party leader when we catch that
    // ailment. Default UNCHECKED — most parties want to pause on every
    // ailment; the toggle is for edge cases ("we're at the boss fight,
    // don't pause for a poison tick"). Always-on triggers (over-encumbered,
    // MovementPrevented, Stunned) bypass these flags.

    /// <summary>Don't @wait for poison. Default false (pause).</summary>
    public bool IgnorePoison    { get; set; }

    /// <summary>Don't @wait for blindness. Default false (pause).</summary>
    public bool IgnoreBlindness { get; set; }

    /// <summary>Don't @wait for confusion. Default false (pause).</summary>
    public bool IgnoreConfusion { get; set; }

    /// <summary>Don't @wait for disease. Default false (pause).</summary>
    public bool IgnoreDiseased  { get; set; }

    // The four "do not announce" gates suppress the say-channel broadcast
    // (".@poisoned" etc.) AilmentSyncEngine emits so other FujinTerm clients
    // in the room can mirror our state. Independent of the Ignore* flags —
    // a party may want the leader to pause but not broadcast on say, or
    // vice-versa. Default UNCHECKED = announce.

    /// <summary>Suppress the say-announce when poisoned. Default false (announce).</summary>
    public bool DoNotAnnouncePoison    { get; set; }

    /// <summary>Suppress the say-announce when blinded. Default false (announce).</summary>
    public bool DoNotAnnounceBlindness { get; set; }

    /// <summary>Suppress the say-announce when confused. Default false (announce).</summary>
    public bool DoNotAnnounceConfusion { get; set; }

    /// <summary>Suppress the say-announce when diseased. Default false (announce).</summary>
    public bool DoNotAnnounceDiseased  { get; set; }
}
