using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.ViewModels.GameData.Tables;

// Game Data Browser → Monsters tab. Renders the imported MajorMUD Monsters table — the static
// MDB table that drives Auto-Lair respawn timers (via RegenTime), CombatManager's per-monster
// behaviour gating, and the Workshop COMBAT preview's damage projection.
//
// Column names mirror the MajorMUD MDB schema verbatim (per data-v1.11p.mdb). EXP is the
// experience reward, MagicRes is the magic-resist score, AvgDmg is the average per-round
// outgoing damage, RegenTime is respawn cadence in ticks. Type and Align render via
// LookupEnums ("Solo" / "Lawful Good" / etc.). Undead is a byte-boolean from the MDB
// (0 = no, non-zero = yes — the MDB stores Boolean True as -1, which arrives as 255).
public sealed class MonstersSectionViewModel : JsonTableSectionViewModel, IEditableTableSectionViewModel
{
    private readonly GameDataCache _cache;
    private readonly DialogService? _dialogs;
    private readonly SettingsResolver? _resolverRef;
    private readonly MonsterOverlaySeedStore? _overlaySeed;
    private readonly RoomGraphManager? _roomGraph;

    public override string Id => "monsters";
    public override string Title => "Monsters";

    protected override string TableName => "Monsters";

    // The monster table's columns, in display order. Several are synthesised in
    // ComputeRowCells (AcDr, Dodge, Mag, Damage, Efficiency, Accuracy, EXP, Lairs)
    // rather than being raw MDB fields — see there for how each is derived.
    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Number",
        "Name",
        "RegenTime",     // "Rgn" — respawn timer
        "EXP",           // "65000 (20x)" — base reward with its multiplier (see ComputeRowCells)
        "HP",
        "AcDr",          // synthesised "AC/DR"
        "Dodge",         // synthesised from ability code 34
        "MagicRes",      // "MR"
        "Accuracy",      // synthesised majority/max attack accuracy
        "Damage",        // rounded AvgDmg
        "Efficiency",    // synthesised "Exp/(Dmg+HP)" exp-per-effort metric
        "AvgLairExp",    // "Lair Exp"
        "Lairs",         // synthesised: Σ TotalLairs across the monster's lair groups
        "AvgLairSize",   // synthesised: lair-count-weighted average mobs per lair
        "BiggestLair",   // synthesised: largest mob count across the monster's lair groups
        "Mag",           // synthesised hitmag level from ability code 28
        "Undead",        // raw MDB flag (0 = living), rendered + filterable
    };

    // Friendly grid headers — the columns above keep their raw MDB keys (so binding / search /
    // formatters work) but render compact labels.
    public override IReadOnlyDictionary<string, string> ColumnHeaders { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Number"]     = "ID",
            ["RegenTime"]  = "Respawn",
            ["EXP"]        = "Exp",
            ["AcDr"]       = "AC/DR",
            ["MagicRes"]   = "Magic Res",
            ["Accuracy"]   = "Acc (typ/max)",
            ["Damage"]      = "Avg Damage",
            ["Mag"]         = "Mag-wpn req",
            ["Efficiency"]  = "Exp Eff",
            ["AvgLairExp"]  = "Lair Exp",
            ["Lairs"]       = "# Lairs",
            ["AvgLairSize"] = "Avg Lair Size",
            ["BiggestLair"] = "Biggest Lair",
        };

    // Carried on each row for filtering but not shown as grid columns: the raw
    // AC / DR fields (the grid shows them combined), Alignment + Type (their
    // dropdowns read the formatted value), and the Monster-Intel facets synthesised
    // in ComputeRowCells (elemental resists, spell immunity, flag presences).
    protected override IReadOnlyList<string> FilterOnlyColumns { get; } =
        new[]
        {
            "Align", "Type", "ArmourClass", "DamageResist",
            "ResCold", "ResFire", "ResStone", "ResLightning", "ResWater",
            "SpellImmu", "Animal", "NonLiving", "CastsSpells", "HasLoot",
        };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "monster", "mob", "enemy", "creature", "lair", "regen", "respawn",
    };

    // MajorMUD's HP-regen tick: a monster heals its HPRegen amount once every 90 seconds
    // (18 combat rounds × 5 s). Shared by the "HP Regen" grid column and the edit dialog's HP
    // detail row so the two never drift. (GreaterMUD's 30 s / 6 rounds would branch here off a
    // realm flag if/when that realm is supported.)
    private const int RegenIntervalSeconds = 90;

    protected override IReadOnlyDictionary<string, Func<string?, string?>> ColumnFormatters { get; } =
        new Dictionary<string, Func<string?, string?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["EXP"]        = FormatThousands,
            ["HP"]         = FormatThousands,
            ["AvgLairExp"] = FormatThousands,
            ["Efficiency"] = FormatThousands,
            // Undead monsters render an "✗"; living monsters read blank.
            ["Undead"]     = static raw => raw is null or "" or "0" ? "" : "✗",
            // Filter-only columns: format so the Alignment / Type dropdowns read
            // names, not codes.
            ["Align"]      = LookupEnums.FormatMonAlignment,
            ["Type"]       = LookupEnums.FormatMonType,
        };

    // Thousands-separated display for big counts ("300,000"); 0 / blank render empty.
    // The raw value stays comma-free, so the leading-int threshold filters read it
    // directly while the grid shows the grouped form (the sort comparer parses either).
    internal static string? FormatThousands(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        if (!long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out long n))
            return raw;
        return n == 0 ? "" : n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
    }

    public IRelayCommand<GameDataRow?> OpenEditAsyncCommand { get; }
    ICommand IEditableTableSectionViewModel.OpenEditCommand => OpenEditAsyncCommand;

    public MonstersSectionViewModel(
        GameDataCache cache,
        SettingsResolver? resolver = null,
        DialogService? dialogs = null,
        MonsterOverlaySeedStore? overlaySeed = null,
        RoomGraphManager? roomGraph = null)
        : base(cache, resolver)
    {
        _cache = cache;
        _dialogs = dialogs;
        _resolverRef = resolver;
        _overlaySeed = overlaySeed;
        _roomGraph = roomGraph;
        OpenEditAsyncCommand = new AsyncRelayCommand<GameDataRow?>(OpenEditAsync);

        // Curation filter panel, grouped into legible sections. Every numeric facet
        // is a min/max range (either bound optional) so you can bracket — HP 500–2000,
        // or AC ≤ 50 to find easy kills. The value tested is the leading integer of the
        // cell's raw value (so "80/10" AC/DR reads 80). This absorbs Monster Intel's
        // filtering dimensions (elemental resists, spell immunity, magic-weapon
        // requirement, type/flags, loot). Live — editing any control re-filters.
        FilterGroups.Add(new FilterGroup("Combat",
            ranges: new[]
            {
                new RangeFilter("Exp", "EXP", "Experience per kill (base × multiplier)"),
                new RangeFilter("HP", "HP"),
                new RangeFilter("Avg damage", "Damage", "Average damage it deals per round"),
                new RangeFilter("Accuracy", "Accuracy", "Its attack accuracy — higher means it hits you more"),
                new RangeFilter("AC", "ArmourClass", "Armour Class — harder to hit as this rises"),
                new RangeFilter("DR", "DamageResist", "Damage Resist — flat reduction to physical damage it takes"),
                new RangeFilter("Dodge", "Dodge"),
                new RangeFilter("Magic Resist", "MagicRes", "Cuts spell damage once above 50; never fully immune"),
            }));

        FilterGroups.Add(new FilterGroup("Elemental defenses",
            ranges: new[]
            {
                new RangeFilter("Cold resist %", "ResCold", ResistHint),
                new RangeFilter("Fire resist %", "ResFire", ResistHint),
                new RangeFilter("Stone resist %", "ResStone", ResistHint),
                new RangeFilter("Lightning resist %", "ResLightning", ResistHint),
                new RangeFilter("Water resist %", "ResWater", ResistHint),
            }));

        FilterGroups.Add(new FilterGroup("Casting & immunity",
            ranges: new[]
            {
                new RangeFilter("Magic-weapon req", "Mag",
                    "Your weapon's HitMagic must be at least this to hit it physically (0 = any weapon)"),
                new RangeFilter("Spell immunity", "SpellImmu",
                    "Immune to spells whose ReqLevel is below this (0 = none)"),
            },
            bools: new[]
            {
                new BoolFilter("Casts spells", "CastsSpells", FlagPresent, "Has a between-rounds spell it casts"),
            }));

        FilterGroups.Add(new FilterGroup("Type & alignment",
            bools: new[]
            {
                new BoolFilter("Undead", "Undead", FlagPresent),
                new BoolFilter("Animal", "Animal", FlagPresent),
                new BoolFilter("Non-living", "NonLiving", FlagPresent, "Immune to life-drain"),
            },
            categories: new[]
            {
                new CategoryFilter("Type", "Type", WithAny(LookupEnums.MonTypeOptions)),
                new CategoryFilter("Alignment", "Align", WithAny(LookupEnums.MonAlignmentOptions)),
            }));

        FilterGroups.Add(new FilterGroup("Loot & lairs",
            ranges: new[]
            {
                new RangeFilter("Lair Exp", "AvgLairExp"),
                new RangeFilter("# Lairs", "Lairs"),
                new RangeFilter("Respawn (sec)", "RegenTime"),
            },
            bools: new[]
            {
                new BoolFilter("Drops an item", "HasLoot", FlagPresent),
            }));
    }

    // A flag facet stores "1" when present, blank otherwise.
    private static readonly Func<string?, bool> FlagPresent = static raw => !(raw is null or "" or "0");

    private const string ResistHint = "Negative = vulnerable (extra damage), 100 = immune, over 100 = healed";

    // Prepend "(any)" to a fixed option list for a category dropdown.
    private static IReadOnlyList<string> WithAny(IReadOnlyList<string> options)
    {
        var list = new List<string>(options.Count + 1) { CategoryFilter.AnyOption };
        list.AddRange(options);
        return list;
    }

    // Monster Number → lair stats from the room graph: Count (# rooms whose lair tag
    // names it = # Lairs), SumMax + MaxMax of those rooms' per-room "(Max N)" caps
    // (for the average / biggest lair size). Sourced from the immutable
    // RoomGraphManager.LairSizeByMonster snapshot so the per-room lair size matches
    // the monster record's Spawns-In list — the Lairs table's group-level "Mobs" field
    // is a different quantity and gave wrong "Biggest Lair" values. Captured each load.
    private System.Collections.Generic.IReadOnlyDictionary<int, (int Count, long SumMax, int MaxMax)> _lairIndex
        = new Dictionary<int, (int, long, int)>();

    protected override void PopulateRows(System.Collections.Generic.IList<GameDataRow> rows)
    {
        BuildLairIndex();
        base.PopulateRows(rows);
    }

    // Capture the room graph's immutable per-monster lair-size snapshot (built from
    // each room's lair tag). Empty when no graph is wired (e.g. tests) — the lair
    // columns then render blank.
    private void BuildLairIndex()
        => _lairIndex = _roomGraph?.LairSizeByMonster
            ?? new Dictionary<int, (int, long, int)>();

    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    protected override IReadOnlyDictionary<string, string?>? ComputeRowCells(JsonElement element)
    {
        int baseExp = ReadInt(element, "EXP");
        int mult = ReadInt(element, "ExpMulti");
        if (mult <= 0) mult = 1;
        long effExp = (long)baseExp * mult;
        int hp = ReadInt(element, "HP");
        int ac = ReadInt(element, "ArmourClass");
        int dr = ReadInt(element, "DamageResist");
        int damage = (int)Math.Round(ReadDouble(element, "AvgDmg"), MidpointRounding.AwayFromZero);
        int dodge = ReadAbilValue(element, 34);   // ability code 34 = Dodge
        int mag = ReadAbilValue(element, 28);     // ability code 28 = Magical (hitmag level)

        var cells = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            // Exp = the actual experience earned per kill = base × multiplier. Stored
            // comma-free (the formatter groups it) so the threshold filter's leading-int
            // read lands on the full value.
            ["EXP"]        = effExp.ToString(Inv),
            ["AcDr"]       = $"{ac}/{dr}",
            ["Dodge"]      = dodge > 0 ? dodge.ToString(Inv) : null,
            ["Mag"]        = mag > 0 ? mag.ToString(Inv) : null,
            ["Damage"]     = damage > 0 ? damage.ToString(Inv) : null,
            ["Accuracy"]   = ComputeAttackAccuracy(element),
            ["Efficiency"] = ComputeEfficiency(effExp, damage, hp),
            // Filter-only facets absorbed from Monster Intel. Elemental resists are
            // the signed % (0 = none, negative = vulnerable) stored ALWAYS so a
            // "resist ≤ 0" query can find non-resistant monsters; spell immunity
            // likewise. The flag facets store "1" when present, else blank.
            ["ResCold"]      = ReadAbilValue(element, 3).ToString(Inv),
            ["ResFire"]      = ReadAbilValue(element, 5).ToString(Inv),
            ["ResStone"]     = ReadAbilValue(element, 65).ToString(Inv),
            ["ResLightning"] = ReadAbilValue(element, 66).ToString(Inv),
            ["ResWater"]     = ReadAbilValue(element, 147).ToString(Inv),
            ["SpellImmu"]    = ReadAbilValue(element, 139).ToString(Inv),
            ["Animal"]       = HasAbil(element, 78) ? "1" : null,
            ["NonLiving"]    = HasAbil(element, 109) ? "1" : null,
            ["CastsSpells"]  = HasMidSpell(element) ? "1" : null,
            ["HasLoot"]      = HasDrop(element) ? "1" : null,
        };
        if (_lairIndex.TryGetValue(ReadInt(element, "Number"), out (int Count, long SumMax, int MaxMax) lair)
            && lair.Count > 0)
        {
            cells["Lairs"]       = lair.Count.ToString(Inv);
            cells["AvgLairSize"] = ((double)lair.SumMax / lair.Count).ToString("0.#", Inv);
            cells["BiggestLair"] = lair.MaxMax.ToString(Inv);
        }
        return cells;
    }

    // The "Exp/(Dmg+HP)" exp-per-effort metric — effective exp per (two rounds of the
    // monster's damage + its HP), ×100. Higher = better exp for the risk.
    private static string? ComputeEfficiency(long effExp, int damage, int hp)
    {
        int denom = 2 * damage + hp;
        if (denom <= 0 || effExp <= 0) return null;
        long eff = (long)Math.Round(effExp * 100.0 / denom, MidpointRounding.AwayFromZero);
        return eff.ToString(Inv);
    }

    // Value of an ability code in the monster's Abil-0..9 slots (0 if absent). Monster
    // Dodge (code 34) and hitmag level (code 28 "Magical") are stored as abilities, not
    // base columns, so both surface through here.
    private static int ReadAbilValue(JsonElement el, int code)
    {
        for (int i = 0; i < 10; i++)
            if (ReadInt(el, $"Abil-{i}") == code)
                return ReadInt(el, $"AbilVal-{i}");
        return 0;
    }

    // Presence of an ability code in the monster's Abil-0..9 slots — for the flag
    // facets (Animal 78, NonLiving 109) whose value carries no meaning, only
    // presence.
    private static bool HasAbil(JsonElement el, int code)
    {
        for (int i = 0; i < 10; i++)
            if (ReadInt(el, $"Abil-{i}") == code) return true;
        return false;
    }

    // True when the monster casts a between-rounds spell (any MidSpell-0..4 slot set).
    private static bool HasMidSpell(JsonElement el)
    {
        for (int i = 0; i < 5; i++)
            if (ReadInt(el, $"MidSpell-{i}") > 0) return true;
        return false;
    }

    // True when the monster drops at least one item (any DropItem-0..9 slot set).
    private static bool HasDrop(JsonElement el)
    {
        for (int i = 0; i < 10; i++)
            if (ReadInt(el, $"DropItem-{i}") > 0) return true;
        return false;
    }

    // "Acc (Maj/Mx)" — the accuracy of the monster's majority (most-frequent) physical
    // attack, then its highest accuracy across all physical attacks. Collapses to one
    // number when they match. Only physical attacks count (AttType 1/3 with a non-zero
    // chance). A spell-only monster has no physical accuracy, so it renders blank — the
    // AttAcc-0 slot of a spell attack holds a spell id, not an accuracy, so it must not
    // be shown here.
    internal static string? ComputeAttackAccuracy(JsonElement el)
    {
        int majAcc = 0, maxAcc = 0;
        double bestChance = -1;
        for (int i = 0; i < 6; i++)
        {
            int attType = ReadInt(el, $"AttType-{i}");
            if (attType != 1 && attType != 3) continue;
            if (ReadInt(el, $"Att%-{i}") <= 0) continue;
            int acc = ReadInt(el, $"AttAcc-{i}");
            double chance = ReadDouble(el, $"AttTrue%-{i}");
            if (chance > bestChance) { bestChance = chance; majAcc = acc; }
            if (acc > maxAcc) maxAcc = acc;
        }
        if (bestChance < 0) return null;   // no physical attack → blank
        return majAcc == maxAcc ? majAcc.ToString(Inv) : $"{majAcc}/{maxAcc}";
    }

    private async Task OpenEditAsync(GameDataRow? row)
    {
        if (row is null || _dialogs is null) return;
        string? wcc = row.Get("Number");
        if (string.IsNullOrEmpty(wcc)) return;

        // Pull the MDB row for the right-pane "Other Info" pane.
        IReadOnlyList<MdbInfoRow> mdbInfo = BuildMdbInfo(wcc);

        // Existing overlay — always merged across all 4 tiers (Char →
        // BBS → Global → Defaults). The Defaults-tier baseline comes
        // from the realm-flavored MonsterOverlaySeedStore: for stock
        // realms the seed encodes the relationship + priority + flag
        // values from the decoded stock Monsters.md; for Paradigm realms
        // the seed comes from the Paradigm-build Monsters.md. ResolveGameData
        // then overlays each higher tier's
        // delta in priority order so the dialog opens showing exactly
        // what the runtime engines will see for this monster.
        MonsterOverlay seedDefaults =
            (_overlaySeed is not null && int.TryParse(wcc, out int seedNum))
                ? _overlaySeed.GetOverlay(seedNum)
                : new MonsterOverlay();
        MonsterOverlay existing = _resolverRef?.ResolveGameData<MonsterOverlay>(
            "Monsters", wcc, seedDefaults)
            ?? seedDefaults;

        MonsterEditDialogViewModel vm = new(
            wccNoStr:         wcc,
            mdbName:          row.Get("Name") ?? string.Empty,
            existing:         existing,
            currentTier:      row.SourceTier,
            mdbInfo:          mdbInfo,
            writableTiers:    _resolverRef?.WritableTiers(),
            installedDefaults: seedDefaults,
            // Lets "Override Attack" auto-resolve a typed cast-code (e.g.
            // "turn") onto the mana-gated spell rung instead of silently
            // falling through to a raw, ungated command — see
            // MonsterEditDialogViewModel.ParseAttackOverride.
            resolveSpellShort: AppServices.Current.SpellShort.NumberByShort,
            // Inverse — shows the cast-code again on reopen instead of the
            // internal Spells.Number it resolved to.
            resolveSpellNumber: AppServices.Current.SpellShort.ShortByNumber,
            // Typeahead for the override spell pickers — the character's castable
            // spells, same source the Settings → Combat spell slots use.
            spellSuggestions:   AppServices.Current.Spellbook.AvailablePicks,
            // Min-mana control parity with Settings → Combat (mode caps the box + drives
            // the %↔value label; live max mana snapshot for the conversion).
            manaModePercentage: AppServices.Current.CombatSpellManaModeIsPercentage,
            liveMaxMa:          AppServices.Current.PlayerState.MaxMa);

        MonsterEditResult? result = await _dialogs.OpenWindowAsync<MonsterEditDialogViewModel, MonsterEditResult>(vm);
        if (result is null) return;

        // Installed-defaults reset (confirm + wipe all tiers), redundant-override
        // cleanup (edit == seed → clear the tier), or a normal write — one shared path.
        if (_resolverRef is { } resolver)
            await GameDataOverrideApplier.ApplyAsync(
                resolver, AppServices.Current.Confirm, "Monsters", result.WccNoStr,
                result.Tier, result.Overlay, result.EqualsInstalledDefaults);

        Reload();
    }

    // The right-pane "Other Info" assembly lives in the shared MonsterMdbInfoBuilder now, so
    // the same record opens by Number from outside the browser (the Navigation Room Info panel
    // → MonsterRecordDialogService) as well as from a browser row here.
    private IReadOnlyList<MdbInfoRow> BuildMdbInfo(string wccNoStr)
        => new MonsterMdbInfoBuilder(_cache, _roomGraph, AppServices.Current.TBInfo, _dialogs).Build(wccNoStr);

    // Grid-cell JSON readers kept here (the builder carries its own copies) — used by the
    // synthesised columns in ComputeRowCells.
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


}
