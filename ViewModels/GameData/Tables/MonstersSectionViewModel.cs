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
        MonsterMessageStore? monsterMessages = null)
        : base(cache, resolver)
    {
        _cache = cache;
        _dialogs = dialogs;
        _resolverRef = resolver;
        _monsterMessages = monsterMessages;
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
        // BBS → Global → Defaults). ResolveGameData starts from a
        // blank MonsterOverlay base and overlays each tier's delta
        // in priority order, so the dialog opens showing exactly
        // what the runtime engines will see for this monster. When no
        // tier has an override the returned overlay is all-null and
        // the dialog's defaults (Enemy / Normal / no flags) take
        // over.
        MonsterOverlay existing = _resolverRef?.ResolveGameData<MonsterOverlay>(
            "Monsters", wcc, new MonsterOverlay())
            ?? new MonsterOverlay();

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

            // Found the row — copy every field as a key/value pair for the
            // read-only right pane. Skip null / empty values to keep the
            // pane compact.
            foreach (JsonProperty prop in el.EnumerateObject())
            {
                string raw = prop.Value.ValueKind switch
                {
                    JsonValueKind.Null      => string.Empty,
                    JsonValueKind.Undefined => string.Empty,
                    JsonValueKind.String    => prop.Value.GetString() ?? string.Empty,
                    JsonValueKind.Number    => prop.Value.ToString(),
                    JsonValueKind.True      => "true",
                    JsonValueKind.False     => "false",
                    _                        => prop.Value.ToString(),
                };
                if (string.IsNullOrWhiteSpace(raw) || raw == "0") continue;
                kv.Add(new KeyValuePair<string, string>(prop.Name, raw));
            }
            break;
        }
        return kv;
    }
}
