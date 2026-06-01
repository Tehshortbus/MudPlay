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

            // HP + HP Regen on separate rows.
            AddRowIfNonZero(kv, "HP",       ReadInt(el, "HP"));
            AddRowIfNonZero(kv, "HP Regen", ReadInt(el, "HPRegen"));

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

            // Create / Death spells — cross-ref to Spells.Name
            int createSpell = ReadInt(el, "CreateSpell");
            if (createSpell > 0)
                AddRow(kv, "Create Spell", LookupSpellName(createSpell) ?? $"Spell #{createSpell}");

            int deathSpell = ReadInt(el, "DeathSpell");
            if (deathSpell > 0)
                AddRow(kv, "Death Spell", LookupSpellName(deathSpell) ?? $"Spell #{deathSpell}");

            int greetTxt = ReadInt(el, "GreetTXT");
            if (greetTxt > 0) AddRow(kv, "Greet", $"Textblock #{greetTxt}");

            AddRowIfNonZero(kv, "BS Defense", ReadInt(el, "BSDefense"));

            // Abilities — iterate Abil-0..9, friendly labels via
            // AbilityNames. Code 146 = "Guarded by" splits into its
            // own row with monster-name resolution.
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
                string label = AbilityNames.GetName(code) ?? $"Abil{code}";
                abilities.Add(val == 0 ? label : $"{label} {val}");
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

            // Attacks 0..4 — only when AttName is non-empty.
            for (int i = 0, shown = 0; i < 5; i++)
            {
                string attName = ReadString(el, $"AttName-{i}");
                if (string.IsNullOrWhiteSpace(attName)) continue;
                int min = ReadInt(el, $"AttMin-{i}");
                int max = ReadInt(el, $"AttMax-{i}");
                int acc = ReadInt(el, $"AttAcc-{i}");
                int pct = ReadInt(el, $"Att%-{i}");
                string row = $"{attName}: {min}-{max} dmg";
                if (acc > 0) row += $", {acc} acc";
                if (pct > 0) row += $", {pct}%";
                AddRow(kv, shown == 0 ? "Attacks" : string.Empty, row);
                shown++;
            }

            // Mid Spells 0..4 — combat-cast spells the monster fires
            // between attacks.
            for (int i = 0, shown = 0; i < 5; i++)
            {
                int spellId = ReadInt(el, $"MidSpell-{i}");
                if (spellId == 0) continue;
                int pct = ReadInt(el, $"MidSpell%-{i}");
                int lvl = ReadInt(el, $"MidSpellLVL-{i}");
                string spellName = LookupSpellName(spellId) ?? $"Spell #{spellId}";
                string row = spellName;
                if (lvl > 0) row += $" lvl {lvl}";
                if (pct > 0) row += $" ({pct}%)";
                AddRow(kv, shown == 0 ? "Mid Spells" : string.Empty, row);
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

    private string? LookupItemName(int itemId)    => LookupNameByNumber("Items",    itemId);
    private string? LookupSpellName(int spellId)  => LookupNameByNumber("Spells",   spellId);
    private string? LookupMonsterName(int monNum) => LookupNameByNumber("Monsters", monNum);

    private string? LookupNameByNumber(string table, int number)
    {
        if (number <= 0) return null;
        JsonDocument? doc = _cache.GetRawTable(table);
        if (doc is null) return null;
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("Number", out JsonElement n)) continue;
            if (n.ValueKind != JsonValueKind.Number) continue;
            if (n.GetInt32() != number) continue;
            string name = ReadString(el, "Name");
            return string.IsNullOrEmpty(name) ? null : name;
        }
        return null;
    }
}
