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
/// Source list ported from MudProxy's <c>AbilityNames.cs</c>. Codes
/// are the in-game values used by the MajorMUD engine; new realm
/// releases may add codes that aren't in this table — the helper
/// surfaces those as <c>Unknown({id})</c> rather than failing, so
/// the surrounding views still render.
/// </remarks>
public static class AbilityNames
{
    private static readonly FrozenDictionary<int, string> _names = new Dictionary<int, string>
    {
        { 1,  "DamageNoMR" },        { 2,  "AC" },              { 3,  "Rcol" },
        { 4,  "MaxDamage" },         { 5,  "Rfir" },            { 6,  "Enslave" },
        { 7,  "DR" },                { 8,  "Drain" },           { 9,  "ShadowStealth" },
        { 10, "ACBlur" },            { 11, "AlterEnergyLevel" },{ 12, "Summon" },
        { 13, "Illu" },              { 14, "RoomIllu" },        { 15, "GypsyFortune" },
        { 16, "Rinaldo" },           { 17, "DamageWithMR" },    { 18, "Heal" },
        { 19, "Poison" },            { 20, "CurePoison" },      { 21, "ImmuPoison" },
        { 22, "Accuracy" },          { 23, "AffectsUndead" },   { 24, "Prev" },
        { 25, "Prgd" },              { 26, "DetectMagic" },     { 27, "Stealth" },
        { 28, "Magical" },           { 29, "Punch" },           { 30, "Kick" },
        { 31, "Bash" },              { 32, "Smash" },           { 33, "KillBlow" },
        { 34, "Dodge" },             { 35, "JumpKick" },        { 36, "MagicRes" },
        { 37, "Picklocks" },         { 38, "Tracking" },        { 39, "Thievery" },
        { 40, "FindTraps" },         { 41, "DisarmTraps" },     { 42, "LearnSpell" },
        { 43, "CastSpell" },         { 44, "Intel" },           { 45, "Willpower" },
        { 46, "Strength" },          { 47, "Health" },          { 48, "Agility" },
        { 49, "Charm" },             { 50, "Quest1" },          { 51, "AntiMagic" },
        { 52, "EvilInCombat" },      { 53, "BlindingLight" },   { 54, "TargetIllu" },
        { 55, "AlterLightDuration" },{ 56, "RechargeItem" },    { 57, "SeeHidden" },
        { 58, "Crits" },             { 59, "ClassOK" },         { 60, "Fear" },
        { 61, "AffectExit" },        { 62, "AlterEvilChance" }, { 63, "AlterExperience" },
        { 64, "AddCP" },             { 65, "Rsto" },            { 66, "Rlit" },
        { 67, "Quickness" },         { 68, "Slowness" },        { 69, "MaxMana" },
        { 70, "SpellCasting" },
        // Item-data extras surfaced by MegaMUD's Game Item Details
        // (verified against stock items 172 / 203 / 283 / 304 / 741 / 784).
        { 86, "Quality" },           { 114, "PercentSpell" },
        // 116 = BSable flag — per MMUD Explorer's modItemParse / frmMain
        // weapon filter (Case 116: 'BSable check / bBSAble = True),
        // a weapon is eligible for backstab when any of its Abil-N
        // slots holds code 116. Surface in the dialog as "BSable" so
        // it's visible info but the combat code (future) is the
        // authoritative consumer — it scans Items.json for this code.
        { 116, "BSable" },
        { 119, "DelAtMaint" },       { 121, "Recharge" },
        { 135, "MinLevel" },         { 145, "ManaRgn" },
        { 170, "Sleep" },          { 171, "Invisibility" },
        { 172, "SeeInvisible" },     { 173, "Scry" },           { 174, "StealMana" },
        { 175, "StealHPToMP" },      { 176, "StealMPToHP" },    { 177, "SpellColours" },
        { 178, "ShadowForm" },       { 179, "FindTrapsValue" }, { 180, "PicklocksValue" },
        { 181, "GangHouseDeed" },    { 182, "GangHouseTax" },   { 183, "GangHouseItem" },
        { 184, "GangShopController" },{ 185, "NoAttackIfItemNum" },{ 186, "PerfectStealth" },
        { 187, "Meditate" },         { 188, "UniquePerPool" },  { 189, "WitchyBadgeQuest" },
        { 190, "NoStock" },
        { 200, "MandosQuest" },      { 201, "VolumsQuest" },    { 202, "CartographersQuest" },
        { 203, "LoremastersQuest" }, { 204, "GuildmasterQuest" },{ 205, "DarkbaneQuest" },
        { 206, "GrizzledRanger" },   { 207, "AmazonHuntress" }, { 208, "Conquest" },
        { 209, "Conquest2" },        { 210, "TarlChain" },      { 211, "MerchantCaptain" },
        { 212, "TrendelQuest" },     { 213, "LucaProdigio" },   { 214, "EtherealWatcher" },
        { 215, "KatoQuest" },        { 220, "NagaQuest" },      { 221, "DreadWraith" },
        { 222, "CourtesanQuest" },
        { 1001, "GrantThievery" },   { 1002, "GrantTraps" },    { 1003, "GrantPicklocks" },
        { 1004, "GrantTracking" },   { 1103, "ShadowHome" },
    }.ToFrozenDictionary();

    /// <summary>Return the canonical name for an ability id, or <c>null</c> when the code is unmapped.</summary>
    public static string? GetName(int abilityId)
        => _names.TryGetValue(abilityId, out string? name) ? name : null;

    /// <summary>
    /// Render an ability id as a label. Unknown codes come back as
    /// <c>"Unknown({id})"</c> instead of <c>null</c> so view bindings
    /// always have something to display.
    /// </summary>
    public static string FormatId(int abilityId)
        => GetName(abilityId) ?? $"Unknown({abilityId})";
}
