using System.Collections.Frozen;
using System.Collections.Generic;

namespace FujinTerm.Game.GameData;

/// <summary>
/// Static lookup for MajorMUD ability ids → human-readable names. The
/// MDB-imported Items / Spells / Monsters tables encode ability
/// effects as a sequence of <c>Abil-N</c> / <c>AbilVal-N</c> column
/// pairs; consumers use this helper to render the codes into the
/// names a player recognises ("AC", "Damage", "Heal", quest flags,
/// etc.).
/// </summary>
/// <remarks>
/// <para>
/// Canonical names ported from MMUD Explorer's <c>GetAbilityName</c>
/// (<c>modMMudFunc.bas</c> in <c>syntax53/MMUD-Explorer</c> on GitHub).
/// Using MME's spelling keeps the labels familiar to anyone who's
/// used that tool — e.g. <c>"Damage(-MR)"</c> rather than
/// <c>"DamageWithMR"</c>, <c>"LearnSp"</c> rather than
/// <c>"LearnSpell"</c>, <c>"CastsSp"</c> rather than
/// <c>"CastSpell"</c>.
/// </para>
/// <para>
/// A few codes are display-only message slots in MME (101 ConfuseMsg,
/// 115 DescMsg, 120 StartMsg, 137 ShockMsg, 144 NonMagicalSpell, 155
/// DeathText, 1116 AccountVerified). MME hides them by default; we
/// surface them since the Monster / Items dialog's ability iteration
/// shows every non-zero <c>Abil-N</c> — having a name beats showing
/// the raw integer.
/// </para>
/// </remarks>
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
        // 116 = BSAccu (MME's canonical name). The Items dialog's
        // explicit BSable Yes/No row gates on the presence of this
        // code; the value (when present) is the BS accuracy bonus.
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
        // MME's source has a duplicate Case 1101 collision (UseSpell);
        // taking MeetsReqToHit since it's the first definition.
        { 1103, "ShadowRest" },         { 1104, "AlterSpellHeal" },    { 1105, "AlterSpells" },
        { 1106, "AlterSpellBuffs" },    { 1107, "NoAutoLearn" },       { 1108, "NotForPVP" },
        { 1109, "Enchant" },            { 1110, "BSDR" },              { 1111, "Absorb" },
        { 1112, "Patrol" },             { 1113, "VileWard" },          { 1114, "CastOnKill%" },
        { 1115, "NoFirstKillDrop" },    { 1116, "AccountVerified" },   { 1117, "NotSellable" },
        { 1118, "NoRandomRegen" },      { 1119, "Del@Ganghouse" },
    }.ToFrozenDictionary();

    /// <summary>
    /// Return the canonical name for an ability id, or a sensible
    /// generated label when the code is unmapped. Codes in the
    /// unnamed quest-flag ranges (191..199, 219, 223..400) render as
    /// <c>"QuestFlag{N}"</c> matching MME's <c>bForceAll</c> path.
    /// Anything else falls back to <c>"Ability {N}"</c> so we never
    /// surface a raw <c>"Abil{N}"</c> at the call site.
    /// </summary>
    public static string? GetName(int abilityId)
    {
        if (_names.TryGetValue(abilityId, out string? name)) return name;
        if ((abilityId >= 191 && abilityId <= 199)
            || abilityId == 219
            || (abilityId >= 223 && abilityId <= 400))
            return $"QuestFlag{abilityId}";
        return abilityId > 0 ? $"Ability {abilityId}" : null;
    }

    /// <summary>
    /// Render an ability id as a label. Unknown codes come back as
    /// <c>"Unknown({id})"</c> instead of <c>null</c> so view bindings
    /// always have something to display.
    /// </summary>
    public static string FormatId(int abilityId)
        => GetName(abilityId) ?? $"Unknown({abilityId})";

    /// <summary>
    /// Comma-joined summary of every non-zero <c>Abil-N</c> /
    /// <c>AbilVal-N</c> pair on a JSON record. Used to surface a
    /// human-readable rollup column for Race / Class rows in the
    /// Game Data Browser (each row's awarded abilities at a glance).
    /// </summary>
    /// <param name="element">Source JSON object (Races / Classes / Items / etc. row).</param>
    /// <param name="slots">Number of <c>Abil-N</c> slots to scan; defaults to 10.</param>
    /// <param name="skipCodes">
    /// Optional ability codes to omit from the rollup. Pass <c>{ 59 }</c>
    /// for the Classes tab — code 59 (ClassOk) is meaningful as a
    /// per-item class-restriction in the Items context but its value
    /// on a class row itself is internal / inert data MME deliberately
    /// hides (the only stock class with the entry is Druid with
    /// <c>AbilVal=74</c>).
    /// </param>
    /// <returns>
    /// <c>"Illu +80, RaceStealth, Crits +1, Accuracy3 +3"</c> — values
    /// render signed when non-zero, name-only when the ability has no
    /// magnitude (flag-style abilities). Empty string when no slots are
    /// populated.
    /// </returns>
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
            if (val == 0)
                parts.Add(name);
            else if (val > 0)
                parts.Add($"{name} +{val.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            else
                parts.Add($"{name} {val.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }
        return string.Join(", ", parts);
    }
}
