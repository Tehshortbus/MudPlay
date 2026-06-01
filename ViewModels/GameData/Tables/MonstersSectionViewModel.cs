using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.GameData;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.ViewModels.GameData.Edit;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Monsters tab. Renders the imported MajorMUD
/// <c>Monsters</c> table — the static MDB table that drives Auto-Lair
/// respawn timers (via <c>RegenTime</c>), Phase 13 CombatManager's
/// per-monster behaviour gating, and the Phase 9 Workshop COMBAT
/// preview's damage projection.
/// </summary>
/// <remarks>
/// Column names mirror the MajorMUD MDB schema verbatim (per
/// <c>data-v1.11p.mdb</c>). <c>EXP</c> is the experience reward,
/// <c>MagicRes</c> is the magic-resist score, <c>AvgDmg</c> is the
/// average per-round outgoing damage, <c>RegenTime</c> is respawn
/// cadence in ticks. <c>Type</c> and <c>Align</c> render via
/// <see cref="MmudEnums"/> ("Solo" / "Lawful Good" / etc.) and
/// <c>Undead</c> is a boolean from the MDB so it already arrives
/// as <c>"true"</c> / <c>"false"</c>.
/// </remarks>
public sealed class MonstersSectionViewModel : JsonTableSectionViewModel, IEditableTableSectionViewModel
{
    private readonly GameDataCache _cache;
    private readonly DialogService? _dialogs;
    private readonly SettingsResolver? _resolverRef;
    private readonly MonsterMessageStore? _monsterMessages;
    private readonly MonsterOverlaySeedStore? _overlaySeed;

    public override string Id => "monsters";
    public override string Title => "Monsters";

    protected override string TableName => "Monsters";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Number",
        "Name",
        "EXP",
        "HP",
        "ArmourClass",
        "DamageResist",
        "MagicRes",
        "AvgDmg",
        "Energy",
        "HPRegen",
        "RegenTime",
        "Type",
        "Align",
        "Undead",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "monster", "mob", "enemy", "creature", "lair", "regen", "respawn",
    };

    protected override IReadOnlyDictionary<string, Func<string?, string?>> ColumnFormatters { get; } =
        new Dictionary<string, Func<string?, string?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Type"]  = MmudEnums.FormatMonType,
            ["Align"] = MmudEnums.FormatMonAlignment,
        };

    public IRelayCommand<GameDataRow?> OpenEditAsyncCommand { get; }
    ICommand IEditableTableSectionViewModel.OpenEditCommand => OpenEditAsyncCommand;

    public MonstersSectionViewModel(
        GameDataCache cache,
        SettingsResolver? resolver = null,
        DialogService? dialogs = null,
        MonsterMessageStore? monsterMessages = null,
        MonsterOverlaySeedStore? overlaySeed = null)
        : base(cache, resolver)
    {
        _cache = cache;
        _dialogs = dialogs;
        _resolverRef = resolver;
        _monsterMessages = monsterMessages;
        _overlaySeed = overlaySeed;
        OpenEditAsyncCommand = new AsyncRelayCommand<GameDataRow?>(OpenEditAsync);
    }

    private async Task OpenEditAsync(GameDataRow? row)
    {
        if (row is null || _dialogs is null) return;
        string? wcc = row.Get("Number");
        if (string.IsNullOrEmpty(wcc)) return;

        // Pull the MDB row for the right-pane "Other Info" pane.
        IReadOnlyList<KeyValuePair<string, string>> mdbInfo = BuildMdbInfo(wcc);

        // Existing overlay — always merged across all 4 tiers (Char →
        // BBS → Global → Defaults). The Defaults-tier baseline comes
        // from the realm-flavored MonsterOverlaySeedStore: for stock
        // realms the seed encodes the relationship + priority + flag
        // values shipped by MegaMUD's Monsters.md (decoded offline);
        // for Paradigm realms the seed comes from the Paradigm-build
        // Monsters.md. ResolveGameData then overlays each higher tier's
        // delta in priority order so the dialog opens showing exactly
        // what the runtime engines will see for this monster.
        MonsterOverlay seedDefaults =
            (_overlaySeed is not null && int.TryParse(wcc, out int seedNum))
                ? _overlaySeed.GetOverlay(seedNum)
                : new MonsterOverlay();
        MonsterOverlay existing = _resolverRef?.ResolveGameData<MonsterOverlay>(
            "Monsters", wcc, seedDefaults)
            ?? seedDefaults;

        // Look up the existing monster-message record by Number so the
        // Messages section in the dialog opens hydrated. Null when the
        // store isn't wired or no record exists for this monster.
        MonsterMessageRecord? existingMessages = null;
        if (_monsterMessages is not null && int.TryParse(wcc, out int wccNum))
            existingMessages = _monsterMessages.FindByMonsterNumber(wccNum);

        MonsterEditDialogViewModel vm = new(
            wccNoStr:    wcc,
            mdbName:     row.Get("Name") ?? string.Empty,
            existing:    existing,
            currentTier: row.SourceTier,
            mdbInfo:     mdbInfo,
            messages:    existingMessages);

        MonsterEditResult? result = await _dialogs.OpenWindowAsync<MonsterEditDialogViewModel, MonsterEditResult>(vm);
        if (result is null) return;

        // Defaults tier is read-only for monsters (MDB is the source).
        // Pick Character as the safe fallback if the user accidentally
        // chose Defaults — the resolver itself throws otherwise.
        SettingsTier tier = result.Tier == SettingsTier.Defaults ? SettingsTier.Character : result.Tier;

        _resolverRef?.WriteGameDataAt(tier, "Monsters", result.WccNoStr, result.Overlay);

        // Apply the messages edit when present. Id-keyed replace using
        // the original record's Id (so content edits that flip the
        // projected Id still target the right slot); Upsert when no
        // original existed (first-time authoring).
        if (_monsterMessages is not null && result.UpdatedMessages is not null)
        {
            if (result.OriginalMessages is not null)
                _monsterMessages.Replace(result.OriginalMessages.Id, result.UpdatedMessages);
            else
                _monsterMessages.Upsert(result.UpdatedMessages);
        }

        Reload();
    }

    /// <summary>
    /// Right-pane "Other Info" for the Monster edit dialog. Mirrors the
    /// row order + transforms of MME's <c>PullMonsterDetail</c>
    /// (<c>modMain.bas</c>):
    /// <list type="number">
    ///   <item>WCC No / Experience (with <c>ExpMulti</c> when &gt; 1)</item>
    ///   <item>Regen Time / Game Limit (suffix "(no respawn)" when RegenTime = 0)</item>
    ///   <item>Type / Alignment / Undead</item>
    ///   <item>HP / HP Regen — kept on separate rows per user spec.</item>
    ///   <item>AC/DR combined slash, MR, Follow %, Charm LVL</item>
    ///   <item>Cash — raw R/P/G/S/C breakdown</item>
    ///   <item>Weapon / Create Spell / Death Spell / Greet (cross-ref names)</item>
    ///   <item>BS Defense (when &gt; 0)</item>
    ///   <item>Abilities — friendly labels from <see cref="AbilityNames"/>;
    ///         <c>Abil-N = 146</c> (Guarded by) split into its own row with
    ///         monster-name cross-ref.</item>
    ///   <item>Item Drops 0..9 / Attacks 0..4 / Mid Spells 0..4 — first row in
    ///         each group carries the section label; subsequent rows have a
    ///         blank key so they indent visually under the header.</item>
    /// </list>
    /// Per user spec we deliberately omit combat-sim outputs (predicted
    /// damage etc. — Phase 9 Workshop COMBAT Preview territory) and the
    /// alignment-derived <c>[Hostile]</c>/<c>[Not-Hostile]</c> tag.
    /// </summary>
    private IReadOnlyList<KeyValuePair<string, string>> BuildMdbInfo(string wccNoStr)
    {
        List<KeyValuePair<string, string>> kv = new();
        if (!int.TryParse(wccNoStr, out int wccNo)) return kv;

        JsonDocument? doc = _cache.GetRawTable("Monsters");
        if (doc is null) return kv;

        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("Number", out JsonElement numProp)) continue;
            if (numProp.ValueKind != JsonValueKind.Number) continue;
            if (numProp.GetInt32() != wccNo) continue;

            AddRow(kv, "WCC No", wccNoStr);

            int exp = ReadInt(el, "EXP");
            int multi = ReadInt(el, "ExpMulti");
            if (exp != 0)
            {
                string expDisplay = exp.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
                if (multi > 1) expDisplay += $" (×{multi})";
                AddRow(kv, "Experience", expDisplay);
            }

            int regenTime = ReadInt(el, "RegenTime");
            if (regenTime > 0)
                AddRow(kv, "Regen Time", $"{regenTime} hour{(regenTime == 1 ? "" : "s")}");

            int gameLimit = ReadInt(el, "GameLimit");
            if (gameLimit > 0)
                AddRow(kv, "Game Limit",
                    regenTime == 0 ? $"{gameLimit} (no respawn)" : gameLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));

            AddRowIfPresent(kv, "Type",     MmudEnums.FormatMonType(ReadString(el, "Type")));
            AddRowIfPresent(kv, "Alignment", MmudEnums.FormatMonAlignment(ReadString(el, "Align")));

            if (ReadInt(el, "Undead") == 1) AddRow(kv, "Undead", "Yes");

            // HP + HP Regen combined into one row, matching MME's
            // "7200 (Regens: 2000 HPs every 90 seconds [18 rounds])" form.
            // 90s / 18 rounds is the classic MajorMUD tick (5s per round
            // × 18 = 90s). If we add GreaterMUD support later, swap to
            // 30s / 6 rounds on that realm via a Settings.RealmType
            // branch.
            int hp = ReadInt(el, "HP");
            if (hp > 0)
            {
                string hpDisplay = hp.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
                int hpRegen = ReadInt(el, "HPRegen");
                if (hpRegen > 0)
                    hpDisplay += $" (Regens: {hpRegen:N0} HPs every 90 seconds [18 rounds])";
                AddRow(kv, "HP", hpDisplay);
            }

            int ac = ReadInt(el, "ArmourClass");
            int dr = ReadInt(el, "DamageResist");
            if (ac != 0 || dr != 0) AddRow(kv, "AC/DR", $"{ac}/{dr}");

            AddRowIfNonZero(kv, "MR",        ReadInt(el, "MagicRes"));
            AddRowIfNonZero(kv, "Follow %",  ReadInt(el, "Follow%"));
            AddRowIfNonZero(kv, "Charm LVL", ReadInt(el, "CharmLVL"));

            // Cash — raw coin breakdown ("2R/3P/5G/10S/2C"). Skipped
            // entirely when every coin slot is zero.
            string cash = BuildCashBreakdown(el);
            if (!string.IsNullOrEmpty(cash)) AddRow(kv, "Cash (up to)", cash);

            // Weapon — cross-ref to Items.Name
            int weaponId = ReadInt(el, "Weapon");
            if (weaponId > 0)
                AddRow(kv, "Weapon", LookupItemName(weaponId) ?? $"Item #{weaponId}");

            // Create / Death spells — cross-ref to Spells.Name with brief
            // effect descriptor when one of the primary Abil codes is set.
            int createSpell = ReadInt(el, "CreateSpell");
            if (createSpell > 0) AddRow(kv, "Create Spell", ResolveSpellWithEffect(createSpell));
            int deathSpell = ReadInt(el, "DeathSpell");
            if (deathSpell > 0) AddRow(kv, "Death Spell",  ResolveSpellWithEffect(deathSpell));

            int greetTxt = ReadInt(el, "GreetTXT");
            if (greetTxt > 0) AddRow(kv, "Greet", $"Textblock #{greetTxt}");

            AddRowIfNonZero(kv, "BS Defense", ReadInt(el, "BSDefense"));

            // Abilities — iterate Abil-0..9, friendly labels via
            // AbilityNames. Code 146 = "Guarded by" splits into its
            // own row with monster-name resolution. Values render
            // signed ("+5" / "-50") so resist-style abilities read
            // unambiguously vs MME's display.
            List<string> abilities = new();
            List<int>    guards    = new();
            for (int i = 0; i < 10; i++)
            {
                int code = ReadInt(el, $"Abil-{i}");
                if (code == 0) continue;
                int val = ReadInt(el, $"AbilVal-{i}");
                if (code == 146)
                {
                    guards.Add(val);
                    continue;
                }
                string label = AbilityNames.GetName(code) ?? $"Ability {code}";
                abilities.Add(val == 0 ? label : $"{label} {FormatSigned(val)}");
            }
            if (abilities.Count > 0)
                AddRow(kv, "Abilities", string.Join(", ", abilities));
            if (guards.Count > 0)
            {
                List<string> guardNames = new();
                foreach (int g in guards)
                    guardNames.Add(LookupMonsterName(g) ?? $"Monster #{g}");
                AddRow(kv, "Guarded by", string.Join(", ", guardNames));
            }

            // Item Drops — one row per non-zero DropItem-N slot. First
            // row carries the "Item Drops" header label; subsequent
            // rows use a blank key so they visually indent.
            for (int i = 0, shown = 0; i < 10; i++)
            {
                int itemId = ReadInt(el, $"DropItem-{i}");
                if (itemId == 0) continue;
                int pct = ReadInt(el, $"DropItem%-{i}");
                string itemName = LookupItemName(itemId) ?? $"Item #{itemId}";
                string row = pct > 0 ? $"{itemName} ({pct}%)" : itemName;
                AddRow(kv, shown == 0 ? "Item Drops" : string.Empty, row);
                shown++;
            }

            // ----- Mob's Attacks (each AttType-N in 1..3 with Att%>0
            // gets its own header + sub-rows) -----
            //
            // Format mirrors MME's PullMonsterDetail attack rendering:
            //
            //   Mob's Attacks         {Energy} energy/round
            //   (20%) claws           Min-Max: 50-90
            //                         Accuracy: 60
            //                         Energy: 250 (Max 4x/round)
            //                         Hit Spell: poison cloud      (when AttHitSpell > 0)
            //   (15%) Cast Fireball   Spell: fireball lvl 50
            //                         Success %: 75
            //                         Energy: 300 (Max 3x/round)
            //
            // AttType 1 (normal) / 3 (rob) → Min-Max + Accuracy fields.
            // AttType 2 (spell)            → AttAcc holds the spell id,
            //                                 AttMax holds the cast level,
            //                                 AttMin holds the success %.
            //
            // Per-attack percent uses AttTrue% when present (the actual
            // probability) and falls back to Att% otherwise (the
            // cumulative-threshold value MME uses).
            int monsterEnergy = ReadInt(el, "Energy");
            bool hasAttacks = false;
            for (int i = 0; i < 5; i++)
            {
                int at = ReadInt(el, $"AttType-{i}");
                if (at >= 1 && at <= 3 && ReadInt(el, $"Att%-{i}") > 0) { hasAttacks = true; break; }
                if (ReadInt(el, $"MidSpell-{i}") > 0)                  { hasAttacks = true; break; }
            }
            if (hasAttacks)
            {
                // Monster energy is the per-round budget the mob spends
                // on attacks/spells. Stock MajorMUD is always 1000;
                // paradigm / future realms may differ.
                AddRow(kv, "Mob's Attacks",
                    monsterEnergy > 0
                        ? $"{monsterEnergy} energy/round"
                        : string.Empty);

                for (int i = 0; i < 5; i++)
                {
                    int attType = ReadInt(el, $"AttType-{i}");
                    int attPct  = ReadInt(el, $"Att%-{i}");
                    if (attType < 1 || attType > 3 || attPct <= 0) continue;

                    string attName = ReadString(el, $"AttName-{i}").Trim();
                    int attEnergy  = ReadInt(el, $"AttEnergy-{i}");
                    int hitSpell   = ReadInt(el, $"AttHitSpell-{i}");
                    int trueRound  = (int)Math.Round(ReadDouble(el, $"AttTrue%-{i}"));
                    int displayPct = trueRound > 0 ? trueRound : attPct;

                    string header = string.IsNullOrEmpty(attName)
                        ? $"({displayPct}%) Attack {i + 1}"
                        : $"({displayPct}%) {attName}";

                    if (attType == 1 || attType == 3)
                    {
                        int min = ReadInt(el, $"AttMin-{i}");
                        int max = ReadInt(el, $"AttMax-{i}");
                        int acc = ReadInt(el, $"AttAcc-{i}");
                        AddRow(kv, header, $"Min-Max: {min}-{max}");
                        AddRow(kv, string.Empty, $"Accuracy: {acc}");
                        AddRow(kv, string.Empty, FormatEnergyRow(attEnergy, monsterEnergy));
                        if (hitSpell > 0)
                            AddRow(kv, string.Empty, $"Hit Spell: {ResolveSpellWithEffect(hitSpell)}");
                    }
                    else // attType == 2 (spell-attack)
                    {
                        int spellId   = ReadInt(el, $"AttAcc-{i}");
                        int spellLvl  = ReadInt(el, $"AttMax-{i}");
                        int successPc = ReadInt(el, $"AttMin-{i}");
                        string spell  = ResolveSpellWithEffect(spellId, spellLvl);
                        AddRow(kv, header,
                            spellLvl > 0 ? $"Spell: {spell} lvl {spellLvl}" : $"Spell: {spell}");
                        AddRow(kv, string.Empty, $"Success %: {successPc}");
                        AddRow(kv, string.Empty, FormatEnergyRow(attEnergy, monsterEnergy));
                        if (hitSpell > 0)
                            AddRow(kv, string.Empty, $"Hit Spell: {ResolveSpellWithEffect(hitSpell)}");
                    }
                }
            }

            // ----- Between Rounds (formerly Mid Spells) -----
            // MidSpell% is stored as a cumulative threshold across the 5
            // slots (slot 0's value is its raw chance; slot N's is the
            // running sum). Per MME, the display shows the DELTA so each
            // row reads as the actual chance for that spell to fire.
            int cumulative = 0;
            for (int i = 0, shown = 0; i < 5; i++)
            {
                int spellId = ReadInt(el, $"MidSpell-{i}");
                if (spellId == 0) continue;
                int threshold = ReadInt(el, $"MidSpell%-{i}");
                int delta = threshold - cumulative;
                cumulative = threshold;
                int lvl = ReadInt(el, $"MidSpellLVL-{i}");
                string spellName = ResolveSpellWithEffect(spellId, lvl);
                string row = lvl > 0 ? $"({delta}%) [{spellName}, lvl {lvl}]" : $"({delta}%) [{spellName}]";
                AddRow(kv, shown == 0 ? "Between Rounds" : string.Empty, row);
                shown++;
            }

            break;
        }
        return kv;
    }

    // ----- Field readers + row helpers -----

    private static int ReadInt(JsonElement el, string field)
    {
        if (!el.TryGetProperty(field, out JsonElement v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
    }

    private static double ReadDouble(JsonElement el, string field)
    {
        if (!el.TryGetProperty(field, out JsonElement v)) return 0d;
        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? d : 0d;
    }

    /// <summary>"+5" / "-50" / "0" — used by signed-value ability rows.</summary>
    private static string FormatSigned(int n) => n > 0
        ? "+" + n.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"Energy: 250 (Max 4x/round)" — divides monster total by per-attack cost.</summary>
    private static string FormatEnergyRow(int attEnergy, int monsterEnergy)
    {
        if (attEnergy <= 0) return $"Energy: {attEnergy}";
        if (monsterEnergy <= 0) return $"Energy: {attEnergy}";
        return $"Energy: {attEnergy} (Max {monsterEnergy / attEnergy}x/round)";
    }

    private static string ReadString(JsonElement el, string field)
    {
        if (!el.TryGetProperty(field, out JsonElement v)) return string.Empty;
        return v.ValueKind switch
        {
            JsonValueKind.Null      => string.Empty,
            JsonValueKind.Undefined => string.Empty,
            JsonValueKind.String    => v.GetString() ?? string.Empty,
            JsonValueKind.Number    => v.ToString(),
            _                        => v.ToString(),
        };
    }

    private static void AddRow(List<KeyValuePair<string, string>> kv, string label, string value)
        => kv.Add(new KeyValuePair<string, string>(label, value));

    private static void AddRowIfPresent(List<KeyValuePair<string, string>> kv, string label, string? value)
    {
        if (!string.IsNullOrEmpty(value)) AddRow(kv, label, value);
    }

    private static void AddRowIfNonZero(List<KeyValuePair<string, string>> kv, string label, int value)
    {
        if (value != 0) AddRow(kv, label, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>"2R/3P/5G/10S/2C" — only non-zero coins, slash-separated.</summary>
    private static string BuildCashBreakdown(JsonElement el)
    {
        List<string> parts = new();
        void Maybe(string field, string letter)
        {
            int v = ReadInt(el, field);
            if (v > 0) parts.Add(v.ToString(System.Globalization.CultureInfo.InvariantCulture) + letter);
        }
        Maybe("R", "R");
        Maybe("P", "P");
        Maybe("G", "G");
        Maybe("S", "S");
        Maybe("C", "C");
        return string.Join("/", parts);
    }

    // ----- Cross-reference helpers (Items / Spells / Monsters) -----
    //
    // Thin shims over GameDataCache.FindNameByNumber that add this VM's
    // two stricter semantics: skip non-positive ids (zero is the "no
    // link" sentinel) and treat empty/missing Name as null so callers
    // get a single boolean check instead of having to also test for "".

    private string? LookupItemName(int itemId)    => LookupName("Items",    itemId);
    private string? LookupSpellName(int spellId)  => LookupName("Spells",   spellId);
    private string? LookupMonsterName(int monNum) => LookupName("Monsters", monNum);

    private string? LookupName(string table, int number)
    {
        if (number <= 0) return null;
        string? name = _cache.FindNameByNumber(table, number);
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>
    /// "{spell name} (effect)" — spell name with a brief effect descriptor
    /// when the spell's primary <c>Abil-N</c> entries surface a recognisable
    /// effect (damage range, heal range, poison, fear, etc.). Falls back to
    /// just the spell name when none of the primary effect codes are
    /// present. Used by Hit Spell / Death Spell / Create Spell /
    /// Between Rounds rows.
    /// </summary>
    /// <param name="castLevel">
    /// Cast level for damage-range scaling. Pass 0 to use the spell's
    /// raw <c>MinBase</c>/<c>MaxBase</c> (correct for monster hit / death /
    /// create spells per MME — <c>PullSpellEQ(False, ...)</c>). For
    /// Between Rounds spells pass <c>MidSpellLVL-N</c>.
    /// </param>
    private string ResolveSpellWithEffect(int spellId, int castLevel = 0)
    {
        string name = LookupSpellName(spellId) ?? $"Spell #{spellId}";
        string effect = ResolveSpellEffect(spellId, castLevel);
        return string.IsNullOrEmpty(effect) ? name : $"{name} ({effect})";
    }

    /// <summary>Brief comma-joined effect descriptor from a spell's primary Abil-N codes.</summary>
    private string ResolveSpellEffect(int spellId, int castLevel)
    {
        if (spellId <= 0) return string.Empty;
        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is null) return string.Empty;

        JsonElement? found = null;
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("Number", out JsonElement n)) continue;
            if (n.ValueKind != JsonValueKind.Number) continue;
            if (n.GetInt32() == spellId) { found = el; break; }
        }
        if (found is null) return string.Empty;
        JsonElement s = found.Value;

        // Min/Max with optional level scaling (per MME GetCurrentSpellMinMax).
        int minBase    = ReadInt(s, "MinBase");
        int maxBase    = ReadInt(s, "MaxBase");
        int minInc     = ReadInt(s, "MinInc");
        int maxInc     = ReadInt(s, "MaxInc");
        int minIncLvls = ReadInt(s, "MinIncLVLs");
        int maxIncLvls = ReadInt(s, "MaxIncLVLs");
        int cap        = ReadInt(s, "Cap");

        int min = minBase;
        int max = maxBase;
        if (castLevel > 0)
        {
            if (minIncLvls > 0) min += (minInc / minIncLvls) * castLevel;
            if (maxIncLvls > 0) max += (maxInc / maxIncLvls) * castLevel;
            if (cap > 0)
            {
                if (min > cap) min = cap;
                if (max > cap) max = cap;
            }
        }

        // Pick out the primary effect codes. Full PullSpellEQ recursion
        // (nested EndCast / Summon / Teleport / TextBlock descriptors) is
        // out of scope here — the Spells tab is the place to dig into the
        // full ability chain; the Monster dialog only surfaces the
        // headline effect so the row reads at a glance.
        List<string> effects = new();
        for (int i = 0; i < 10; i++)
        {
            int code = ReadInt(s, $"Abil-{i}");
            if (code == 0) continue;
            string? desc = code switch
            {
                 1 => FormatRange("dmg",   min, max),  // Damage
                17 => FormatRange("dmg",   min, max),  // Damage(-MR)
                18 => FormatRange("heal",  min, max),  // Heal
                 8 => FormatRange("drain", min, max),  // DrainLife
                19 => "poison",
                60 => "fear",
                71 => "confusion",
                95 => "slay",
                53 => "blindness",
                12 => "summon",
                _  => null,
            };
            if (!string.IsNullOrEmpty(desc) && !effects.Contains(desc)) effects.Add(desc);
        }
        return string.Join(", ", effects);
    }

    /// <summary>"dmg 10-30" / "dmg 10" / "" when both ends are zero.</summary>
    private static string FormatRange(string label, int min, int max)
    {
        if (min == 0 && max == 0) return string.Empty;
        if (min == max) return $"{label} {min}";
        return $"{label} {min}-{max}";
    }

}
