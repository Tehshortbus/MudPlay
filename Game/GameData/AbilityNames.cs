using System.Collections.Frozen;
using System.Collections.Generic;

namespace FujinTerm.Game.GameData;

// Static lookup for MajorMUD ability ids → human-readable names. The MDB-imported
// Items / Spells / Monsters tables encode ability effects as a sequence of Abil-N /
// AbilVal-N column pairs; consumers use this helper to render the codes into the
// names a player recognises ("AC", "Damage", "Heal", quest flags, etc.).
//
// The spellings follow the community-standard MajorMUD ability labels, so they read
// the way veteran players expect — e.g. "Damage(-MR)" rather than "DamageWithMR",
// "LearnSp" rather than "LearnSpell", "CastsSp" rather than "CastSpell".
//
// A few codes are display-only message slots (101 ConfuseMsg, 115 DescMsg, 120
// StartMsg, 137 ShockMsg, 144 NonMagicalSpell, 155 DeathText, 1116
// AccountVerified). We surface them since the Monster / Items dialog's ability
// iteration shows every non-zero Abil-N — having a name beats showing the raw
// integer.
public static class AbilityNames
{
    private static readonly FrozenDictionary<int, string> _names = new Dictionary<int, string>
    {
        // 1..50 — core stats / damage / heal / status effects
        {   1, "Damage" },              {   2, "AC" },                 {   3, "Resist-Cold" },
        {   4, "MaxDamage" },           {   5, "Resist-Fire" },        {   6, "Enslave" },
        {   7, "DR" },                  {   8, "DrainLife" },          {   9, "Shadow" },
        {  10, "AC Blur" },             {  11, "AlterEnergyLevel" },   {  12, "Summon" },
        {  13, "Illu" },                {  14, "RoomIllu" },           {  15, "GypsyFortune" },
        {  16, "Rinaldo" },             {  17, "Damage(-MR)" },        {  18, "Heal" },
        {  19, "Poison" },              {  20, "CurePoison" },         {  21, "ImmuPoison" },
        {  22, "Accuracy" },            {  23, "AffectsUndeadOnly" },  {  24, "ProtEvil" },
        {  25, "ProtGood" },            {  26, "DetectMagic" },        {  27, "Stealth" },
        {  28, "Magical" },             {  29, "Punch" },              {  30, "Kick" },
        {  31, "Bash" },                {  32, "Smash" },              {  33, "Killblow" },
        {  34, "Dodge" },               {  35, "JumpKick" },           {  36, "M.R." },
        {  37, "Picklocks" },           {  38, "Tracking" },           {  39, "Thievery" },
        {  40, "FindTraps" },           {  41, "DisarmTraps" },        {  42, "LearnSp" },
        {  43, "CastsSp" },             {  44, "Intel" },              {  45, "Wisdom" },
        {  46, "Strength" },            {  47, "Health" },             {  48, "Agility" },
        {  49, "Charm" },               {  50, "MageBaneQuest" },

        // 51..100 — anti-magic / class skills / item flags
        {  51, "AntiMagic" },           {  52, "EvilInCombat" },       {  53, "BlindingLight" },
        {  54, "IlluTarget" },          {  55, "AlterLightDuration" }, {  56, "RechargeItem" },
        {  57, "SeeHidden" },           {  58, "Crits" },              {  59, "ClassOk" },
        {  60, "Fear" },                {  61, "AffectExit" },         {  62, "AlterEvilChance" },
        {  63, "AlterExperience" },     {  64, "AddCP" },              {  65, "Resist-Stone" },
        {  66, "Resist-Lightning" },    {  67, "Quickness" },          {  68, "Slowness" },
        {  69, "MaxMana" },             {  70, "Spellcasting" },       {  71, "Confusion" },
        {  72, "ShockShield" },         {  73, "DispellMagic" },       {  74, "HoldPerson" },
        {  75, "Paralyze" },            {  76, "Mute" },               {  77, "Perception" },
        {  78, "Animal" },              {  79, "MageBind" },           {  80, "AffectsAnimalsOnly" },
        {  81, "Freedom" },             {  82, "Cursed" },             {  83, "CursedMajor" },
        {  84, "RemoveCurse" },         {  85, "Shatter" },            {  86, "Quality" },
        {  87, "Speed" },               {  88, "MaxHP" },              {  89, "PunchAcc" },
        {  90, "KickAcc" },             {  91, "JumpKAcc" },           {  92, "PunchDmg" },
        {  93, "KickDmg" },             {  94, "JumpKDmg" },           {  95, "Slay" },
        {  96, "Encum%" },              {  97, "GoodOnly" },           {  98, "EvilOnly" },
        {  99, "AlterDRpercent" },      { 100, "LoyalItem" },

        // 101..150 — message slots / target restrictions / spell-only codes
        { 101, "ConfuseMsg" },          { 102, "RaceStealth" },        { 103, "ClassStealth" },
        { 104, "DefenseModifier" },     { 105, "Accuracy2" },          { 106, "Accuracy3" },
        { 107, "BlindUser" },           { 108, "AffectsLivingOnly" },  { 109, "NonLiving" },
        { 110, "NotGood" },             { 111, "NotEvil" },            { 112, "NeutralOnly" },
        { 113, "NotNeutral" },          { 114, "%Spell" },             { 115, "DescMsg" },
        // 116 = BSAccu. The Items dialog's explicit BSable Yes/No row gates on
        // the presence of this code; the value (when present) is the BS accuracy
        // bonus.
        { 116, "BSAccu" },
        { 117, "BsMinDmg" },            { 118, "BsMaxDmg" },           { 119, "Del@Maint" },
        { 120, "StartMsg" },            { 121, "Recharge" },           { 122, "RemovesSpell" },
        { 123, "HPRegen" },             { 124, "NegateAbility" },      { 125, "IceSorcQuest" },
        { 126, "GoodQuest" },           { 127, "NeutralQuest" },       { 128, "EvilQuest" },
        { 129, "DarkDruidQuest" },      { 130, "BloodChampQuest" },    { 131, "SheDragonQuest" },
        { 132, "WereratQuest" },        { 133, "PhoenixQuest" },       { 134, "DaoLordQuest" },
        { 135, "MinLevel" },            { 136, "MaxLevel" },           { 137, "ShockMsg" },
        { 138, "RoomVisible" },         { 139, "SpellImmu" },          { 140, "TeleportRoom" },
        { 141, "TeleportMap" },         { 142, "HitMagic" },           { 143, "ClearItem" },
        { 144, "NonMagicalSpell" },     { 145, "ManaRgn" },            { 146, "MonsGuards" },
        { 147, "Resist-Water" },        { 148, "TextBlock" },          { 149, "Remove@Maint" },
        { 150, "HealMana" },

        // 151..190 — endcast / runes / quest items / item-equip behaviour /
        // GreaterMUD additions
        { 151, "EndCast" },             { 152, "Rune" },               { 153, "KillSpell" },
        { 154, "Visible@Maint" },       { 155, "DeathText" },          { 156, "QuestItem" },
        { 157, "ScatterItems" },        { 158, "ReqToHit" },           { 159, "KaiBind" },
        { 160, "GiveTempSpell" },       { 161, "OpenDoor" },           { 162, "Lore" },
        { 163, "SpellComponent" },      { 164, "EndCast%" },           { 165, "AlterSpDmg" },
        { 166, "AlterSpLength" },       { 167, "UnEquipItem" },        { 168, "EquipItem" },
        { 169, "CannotWearLocation" },  { 170, "Sleep" },              { 171, "Invisibility" },
        { 172, "SeeInvisible" },        { 173, "Scry" },               { 174, "StealMana" },
        { 175, "StealHPtoMP" },         { 176, "StealMPtoHP" },        { 177, "SpellColours" },
        { 178, "Shadowform" },          { 179, "FindTrapsValue" },     { 180, "PickLocksValue" },
        { 181, "GHouseDeed" },          { 182, "GHouseTax" },          { 183, "GHouseItem" },
        { 184, "GShopItem" },           { 185, "NoAttackIfItemNum" },  { 186, "PerfectStealth" },
        { 187, "Meditate" },            { 188, "Unique Pool" },        { 189, "Witchy Badges" },
        { 190, "No Stock" },

        // 200..222 — GreaterMUD quest flags (named ones)
        { 200, "Mandos Quest" },        { 201, "Volums Quest" },       { 202, "CartographerQuest" },
        { 203, "LoremasterQuest" },     { 204, "GuildmasterQuest" },   { 205, "DarkbaneQuest" },
        { 206, "GrizzledRanger" },      { 207, "AmazonHuntress" },     { 208, "Conquest1" },
        { 209, "Conquest2" },           { 210, "TarlChain" },          { 211, "MerchantCaptain" },
        { 212, "TrendelQuest" },        { 213, "LucaProdigio" },       { 214, "EtherealWatcher" },
        { 215, "KatoQuest" },           { 216, "GoodCheck" },          { 217, "NeutralCheck" },
        { 218, "EvilCheck" },           { 220, "NagaQuest" },          { 221, "DreadWraith" },
        { 222, "CourtesanQuest" },

        // 1001..1119 — Grants / GreaterMUD high-range extras
        { 1001, "GrantThievery" },      { 1002, "GrantTraps" },        { 1003, "GrantPicklocks" },
        { 1004, "GrantTracking" },      { 1100, "AntiMagicNotOK" },    { 1101, "MeetsReqToHit" },
        // Code 1101 has two conflicting meanings in the wild (MeetsReqToHit and
        // UseSpell); we take MeetsReqToHit as the canonical label.
        { 1103, "ShadowRest" },         { 1104, "AlterSpellHeal" },    { 1105, "AlterSpells" },
        { 1106, "AlterSpellBuffs" },    { 1107, "NoAutoLearn" },       { 1108, "NotForPVP" },
        { 1109, "Enchant" },            { 1110, "BSDR" },              { 1111, "Absorb" },
        { 1112, "Patrol" },             { 1113, "VileWard" },          { 1114, "CastOnKill%" },
        { 1115, "NoFirstKillDrop" },    { 1116, "AccountVerified" },   { 1117, "NotSellable" },
        { 1118, "NoRandomRegen" },      { 1119, "Del@Ganghouse" },
    }.ToFrozenDictionary();

    // Return the canonical name for an ability id, or a sensible generated label
    // when the code is unmapped. Codes in the unnamed quest-flag ranges (191..199,
    // 219, 223..400) render as "QuestFlag{N}". Anything else falls back to
    // "Ability {N}" so we never surface a raw "Abil{N}" at the call site.
    public static string? GetName(int abilityId)
    {
        if (_names.TryGetValue(abilityId, out string? name)) return name;
        if ((abilityId >= 191 && abilityId <= 199)
            || abilityId == 219
            || (abilityId >= 223 && abilityId <= 400))
            return $"QuestFlag{abilityId}";
        return abilityId > 0 ? $"Ability {abilityId}" : null;
    }

    // Render an ability id as a label. Unknown codes come back as "Unknown({id})"
    // instead of null so view bindings always have something to display.
    public static string FormatId(int abilityId)
        => GetName(abilityId) ?? $"Unknown({abilityId})";

    // Comma-joined summary of every non-zero Abil-N / AbilVal-N pair on a JSON
    // record. Used to surface a human-readable rollup column for Race / Class rows
    // in the Game Data Browser (each row's awarded abilities at a glance).
    //
    // element is the source JSON object (Races / Classes / Items / etc. row); slots
    // is the number of Abil-N slots to scan (defaults to 10). skipCodes optionally
    // omits ability codes from the rollup — pass { 59 } for the Classes tab, where
    // code 59 (ClassOk) is meaningful as a per-item class-restriction in the Items
    // context but is internal / inert data on a class row itself (the only stock
    // class with the entry is Druid with AbilVal=74).
    //
    // Returns e.g. "Illu +80, RaceStealth, Crits +1, Accuracy3 +3" — values render
    // signed when non-zero, name-only when the ability has no magnitude (flag-style
    // abilities). Empty string when no slots are populated.
    public static string SummarizeAbilities(
        System.Text.Json.JsonElement element,
        int slots = 10,
        IReadOnlyCollection<int>? skipCodes = null)
    {
        List<string> parts = new();
        for (int i = 0; i < slots; i++)
        {
            if (!element.TryGetProperty($"Abil-{i}", out System.Text.Json.JsonElement codeEl)) continue;
            if (codeEl.ValueKind != System.Text.Json.JsonValueKind.Number) continue;
            if (!codeEl.TryGetInt32(out int code) || code == 0) continue;
            if (skipCodes is not null && skipCodes.Contains(code)) continue;

            int val = 0;
            if (element.TryGetProperty($"AbilVal-{i}", out System.Text.Json.JsonElement valEl)
                && valEl.ValueKind == System.Text.Json.JsonValueKind.Number
                && valEl.TryGetInt32(out int v))
            {
                val = v;
            }

            string? name = GetName(code);
            if (name is null) continue;
            parts.Add(FormatPart(name, val));
        }
        return string.Join(", ", parts);
    }

    // Comma-joined summary of a sequence of (ability code, value) pairs — the
    // in-memory counterpart to the JsonElement overload, for callers that already
    // hold decoded ability slots (e.g. the Spell Book's per-spell effect rollup).
    // Same formatting: signed magnitude when non-zero, name-only for flag-style
    // abilities; zero codes and any skipCodes are omitted.
    public static string SummarizeAbilities(
        IEnumerable<(int Code, int Value)> abilities,
        IReadOnlyCollection<int>? skipCodes = null)
    {
        ArgumentNullException.ThrowIfNull(abilities);
        List<string> parts = new();
        foreach ((int code, int value) in abilities)
        {
            if (code == 0) continue;
            if (skipCodes is not null && skipCodes.Contains(code)) continue;
            string? name = GetName(code);
            if (name is null) continue;
            parts.Add(FormatPart(name, value));
        }
        return string.Join(", ", parts);
    }

    // Ability codes that grant the in-game Traps skill — i.e. the class / race can
    // search for and disarm traps. In stock MajorMUD data the grant is code 40
    // (FindTraps — the single "Traps" skill governs both find and disarm); 41
    // (DisarmTraps) and 1002 (GrantTraps) cover custom / ParaMUD sets that encode
    // the grant differently. A row carrying any of these can disarm.
    private static readonly System.Collections.Generic.HashSet<int> TrapGrantCodes = new() { 40, 41, 1002 };

    // True when the JSON row (a Classes / Races record) carries any trap-skill grant
    // in its Abil-0..9 slots — used by the trap-delegation capability check to decide
    // whether a party member's class or race makes them worth firing @trap at.
    // element is the source Classes / Races object; slots is the number of Abil-N
    // slots to scan (defaults to 10).
    public static bool HasTrapAbility(System.Text.Json.JsonElement element, int slots = 10)
    {
        for (int i = 0; i < slots; i++)
        {
            if (!element.TryGetProperty($"Abil-{i}", out System.Text.Json.JsonElement codeEl)) continue;
            if (codeEl.ValueKind != System.Text.Json.JsonValueKind.Number) continue;
            if (codeEl.TryGetInt32(out int code) && TrapGrantCodes.Contains(code)) return true;
        }
        return false;
    }

    // Class-ability code that grants ShadowRest — a Paradigm ability letting a
    // hidden/sneaking character rest in a room with monsters without being
    // attacked (see GAME_MECHANICS "ShadowRest"). Not a stock mechanic; the code
    // maps to "ShadowRest" above.
    private const int ShadowRestCode = 1103;

    // True when the JSON row (a Classes record) carries the ShadowRest grant in any
    // of its Abil-0..9 slots — the capability gate for the shadow-rest resting
    // behavior. element is the source Classes object; slots is the number of Abil-N
    // slots to scan (defaults to 10).
    public static bool HasShadowRest(System.Text.Json.JsonElement element, int slots = 10)
    {
        for (int i = 0; i < slots; i++)
        {
            if (!element.TryGetProperty($"Abil-{i}", out System.Text.Json.JsonElement codeEl)) continue;
            if (codeEl.ValueKind != System.Text.Json.JsonValueKind.Number) continue;
            if (codeEl.TryGetInt32(out int code) && code == ShadowRestCode) return true;
        }
        return false;
    }

    // Render one decoded ability slot — "AC +10", "Slowness -5", or "RaceStealth"
    // (name only, value 0).
    private static string FormatPart(string name, int value)
        => value == 0
            ? name
            : value > 0
                ? $"{name} +{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                : $"{name} {value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
