using System.Collections.Generic;
using System.Globalization;
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
/// Game Data Browser → Spells tab. Renders the imported MajorMUD
/// <c>Spells</c> table — fuel for the Phase 13 CastingDirector + the
/// Settings → Spells / Party spell pickers + the Phase 9 Workshop
/// Spell Book.
/// </summary>
/// <remarks>
/// <para>
/// Column names mirror the MajorMUD MDB schema verbatim. <c>Short</c>
/// is the cast-name shortcode (e.g. <c>"star"</c>), <c>ReqLevel</c> is
/// the cast prerequisite, <c>Diff</c> is the cast-difficulty score.
/// <c>Magery</c>, <c>AttType</c>, and <c>Targets</c> render via
/// <see cref="LookupEnums"/> ("Mage" / "Cold" / "Full Area" / etc.).
/// </para>
/// <para>
/// Double-click a row → opens every <see cref="MessageRecord"/> that
/// links <c>Spells#N</c> for that row's spell number in its own
/// modeless <see cref="MessageEditDialogViewModel"/>. Multi-message
/// spells (e.g. apostrophe / wording variants of one effect line)
/// stack as cascaded windows the user drags apart if needed. Zero
/// matches surfaces a one-shot info dialog naming the spell so the
/// user sees the gap rather than a silent no-op.
/// </para>
/// </remarks>
public sealed class SpellsSectionViewModel : JsonTableSectionViewModel, IEditableTableSectionViewModel
{
    private readonly GameDataCache _cache;
    private readonly MessageStore? _messages;
    private readonly DialogService? _dialogs;

    public override string Id => "spells";
    public override string Title => "Spells";

    protected override string TableName => "Spells";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Number",
        "Name",
        "Short",
        "Magery",
        "MageryLVL",
        "ReqLevel",
        "ManaCost",
        "EnergyCost",
        "Diff",
        "AttType",
        "Targets",
        "MinBase",
        "MaxBase",
        "Dur",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "spell", "magery", "mana", "cast", "level", "code", "short", "target",
    };

    protected override IReadOnlyDictionary<string, Func<string?, string?>> ColumnFormatters { get; } =
        new Dictionary<string, Func<string?, string?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Magery"]  = LookupEnums.FormatMagery,
            ["AttType"] = LookupEnums.FormatSpellAttackType,
            ["Targets"] = LookupEnums.FormatSpellTargets,
        };

    /// <summary>Double-click handler — opens every Message linked to this spell.</summary>
    public IAsyncRelayCommand<GameDataRow?> OpenLinkedMessagesCommand { get; }

    ICommand IEditableTableSectionViewModel.OpenEditCommand => OpenLinkedMessagesCommand;

    public SpellsSectionViewModel(
        GameDataCache cache,
        SettingsResolver? resolver = null,
        MessageStore? messages = null,
        DialogService? dialogs = null) : base(cache, resolver)
    {
        _cache    = cache;
        _messages = messages;
        _dialogs  = dialogs;
        OpenLinkedMessagesCommand = new AsyncRelayCommand<GameDataRow?>(OpenLinkedMessagesAsync);
    }

    private async Task OpenLinkedMessagesAsync(GameDataRow? row)
    {
        if (row is null || _messages is null || _dialogs is null) return;

        string? numText = row.Get("Number");
        if (!int.TryParse(numText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int spellNumber))
            return;
        string spellName = row.Get("Name") ?? string.Empty;

        // Post-restructure: each spell has at most ONE MessageRecord
        // (the wcc generator emits one record per spell-name with all
        // perspective slots populated as fields on the same record).
        // Find that one record by Spells#N link and open the editor —
        // or fall back to the "no message" InfoDialog if the spell
        // has no record at all.
        MessageRecord? match = _messages.Messages
            .FirstOrDefault(m => m.Links is not null && m.Links.Any(l =>
                string.Equals(l.Table, "Spells", StringComparison.OrdinalIgnoreCase)
                && l.Number == spellNumber));

        // Always open the tabbed dialog: the Message tab edits the player-cast
        // message (if any), and the Game Data tab shows the spell's imported
        // fields. A message only exists for spells the player casts — spells
        // cast by rooms / items / textblocks have none, so for those we open a
        // new record pre-linked to the spell (Message tab ready to author) while
        // the Game Data tab still surfaces what the spell does.
        IReadOnlyList<GameDataInfoRow> info = BuildSpellInfoRows(spellNumber);

        MessageRecord record;
        bool isNew;
        if (match is not null)
        {
            record = match;
            isNew = false;
        }
        else
        {
            record = new MessageRecord(
                Id:              string.Empty,
                Name:            spellName,
                Action:          MessageAction.Ignore,
                Flags:           MessageFlags.None,
                RawFlagsHex:     0,
                Response:        string.Empty,
                CasterMessage:   string.Empty,
                TargetMessage:   string.Empty,
                WitnessMessage:  string.Empty,
                AppliedMessage:  string.Empty,
                AppliedEndsWith: string.Empty,
                Links:           new[] { new GameDataLink("Spells", spellNumber) });
            isNew = true;
        }

        MessageEditDialogViewModel vm = new(
            record,
            currentTier:     SettingsTier.Defaults,
            existingRecords: _messages.Messages,
            isNew:           isNew,
            cache:           _cache,
            gameDataInfo:    info);
        MessageEditResult? result = await _dialogs
            .OpenWindowAsync<MessageEditDialogViewModel, MessageEditResult>(vm);
        if (result is null) return;
        ApplyResult(result);
    }

    /// <summary>
    /// Mirror of <c>MessagesSectionViewModel.ApplyResult</c> — Id-keyed
    /// update-or-append into the store + persist.
    /// </summary>
    private void ApplyResult(MessageEditResult result)
    {
        if (_messages is null) return;
        int idx = -1;
        for (int i = 0; i < _messages.Messages.Count; i++)
        {
            if (_messages.Messages[i].Id == result.Original.Id) { idx = i; break; }
        }
        if (idx >= 0) _messages.Messages[idx] = result.Updated;
        else          _messages.Messages.Add(result.Updated);
        _messages.Save();
    }

    /// <summary>
    /// The spell's full imported data for the dialog's Game Data tab — every
    /// field on the spell's row: enum columns (Magery / attack-type / targets)
    /// formatted, each non-zero ability slot resolved to its name, and any
    /// numeric reference (CastsSp / Summon / EquipItem / …) translated to the
    /// real Spell / Monster / Item name. Empty when the active set has no Spells
    /// table or no matching row.
    /// </summary>
    private IReadOnlyList<GameDataInfoRow> BuildSpellInfoRows(int spellNumber)
    {
        var rows = new List<GameDataInfoRow>();

        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is null) return rows;

        JsonElement? found = null;
        foreach (JsonElement r in doc.RootElement.EnumerateArray())
            if (ReadInt(r, "Number") == spellNumber) { found = r; break; }
        if (found is not { } el) return rows;

        bool teleportRendered = false;
        foreach (JsonProperty prop in el.EnumerateObject())
        {
            string field = prop.Name;

            // Ability pairs (Abil-N + AbilVal-N) collapse to one row; skip the
            // value half — it's rendered alongside its code.
            if (field.StartsWith("AbilVal-", StringComparison.Ordinal)) continue;
            if (field.StartsWith("Abil-", StringComparison.Ordinal))
            {
                if (prop.Value.ValueKind != JsonValueKind.Number
                    || !prop.Value.TryGetInt32(out int code) || code == 0)
                    continue;
                string slot = field["Abil-".Length..];
                int val = ReadInt(el, $"AbilVal-{slot}");

                // TeleportRoom (140) + TeleportMap (141) collapse into one
                // destination row: "map/room (room name)".
                if (code is 140 or 141)
                {
                    if (!teleportRendered)
                    {
                        rows.Add(new GameDataInfoRow("Teleport Destination", TeleportDestination(el)));
                        teleportRendered = true;
                    }
                    continue;
                }

                // NegateAbility (124) — its value is the negated spell.
                if (code == 124)
                {
                    string? sp = _cache.FindNameByNumber("Spells", val);
                    rows.Add(new GameDataInfoRow("Negate", sp ?? val.ToString(CultureInfo.InvariantCulture)));
                    continue;
                }

                string abilName = AbilityNames.GetName(code) ?? $"Ability {code}";
                string valueText = val.ToString(CultureInfo.InvariantCulture);
                if (ResolveAbilityReference(code, val) is { } refName)
                    valueText += $" ({refName})";
                rows.Add(new GameDataInfoRow(abilName, valueText));
                continue;
            }

            if (RenderField(field, prop.Value) is { } rendered)
                rows.Add(new GameDataInfoRow(field, rendered));
        }

        return rows;
    }

    // "map/room (room name)" for a teleport spell — the destination map comes
    // from TeleportMap (141), the room from TeleportRoom (140), and the name is
    // resolved against the Rooms table by map + room.
    private string TeleportDestination(JsonElement el)
    {
        int map = FindAbilVal(el, 141);
        int room = FindAbilVal(el, 140);
        string dest = $"{map}/{room}";
        if (ResolveRoomName(map, room) is { } name) dest += $" ({name})";
        return dest;
    }

    private static int FindAbilVal(JsonElement el, int code)
    {
        for (int i = 0; i < 10; i++)
            if (ReadInt(el, $"Abil-{i}") == code) return ReadInt(el, $"AbilVal-{i}");
        return 0;
    }

    // Resolve a (map, room) pair to the room's Name in the Rooms table.
    private string? ResolveRoomName(int map, int room)
    {
        if (map <= 0 || room <= 0) return null;
        JsonDocument? doc = _cache.GetRawTable("Rooms");
        if (doc is null) return null;
        foreach (JsonElement r in doc.RootElement.EnumerateArray())
            if (ReadInt(r, "Map Number") == map && ReadInt(r, "Room Number") == room)
                return r.TryGetProperty("Name", out JsonElement e) && e.ValueKind == JsonValueKind.String
                    ? CleanString(e.GetString())
                    : null;
        return null;
    }

    // Render one scalar (non-ability) field: enum columns via the shared
    // formatters, the "Casted By" source list summarised, blank / NUL text
    // dropped. Returns null to omit the field.
    private string? RenderField(string field, JsonElement value)
    {
        if (ColumnFormatters.TryGetValue(field, out var fmt))
        {
            string? raw = value.ValueKind switch
            {
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.String => value.GetString(),
                _ => null,
            };
            string? formatted = fmt(raw);
            return string.IsNullOrWhiteSpace(formatted) ? null : formatted;
        }

        if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
        if (value.ValueKind == JsonValueKind.String)
        {
            if (CleanString(value.GetString()) is not { } s) return null;
            return string.Equals(field, "Casted By", StringComparison.OrdinalIgnoreCase)
                ? SummarizeList(s)
                : s;
        }
        return null;
    }

    // Reference-bearing ability codes → the table their AbilVal points at,
    // resolved to that row's name. Null when the code isn't a reference, the
    // value is non-positive, or the number has no matching row.
    private string? ResolveAbilityReference(int code, int val)
    {
        if (val <= 0) return null;
        string? table = code switch
        {
            // Learn / Casts / Removes / EndCast / KillSpell / GiveTempSpell.
            42 or 43 or 122 or 151 or 153 or 160 => "Spells",
            // Summon / MonsGuards.
            12 or 146 => "Monsters",
            // ClearItem / UnEquipItem / EquipItem / NoAttackIfItemNum.
            143 or 167 or 168 or 185 => "Items",
            _ => null,
        };
        return table is null ? null : _cache.FindNameByNumber(table, val);
    }

    // Comma-joined source lists ("Casted By") can run to dozens of rooms; show
    // the count + the first several so a non-scrolling info dialog stays readable.
    private static string SummarizeList(string raw)
    {
        string[] parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length <= 8) return string.Join(", ", parts);
        return $"{parts.Length} sources — {string.Join(", ", parts.Take(8))}, …";
    }

    private static int ReadInt(JsonElement row, string property)
        => row.TryGetProperty(property, out JsonElement e)
           && e.ValueKind == JsonValueKind.Number
           && e.TryGetInt32(out int n) ? n : 0;

    // NUL-aware trim — the MDB importer writes a literal "\0" for empty Jet text
    // columns, so a plain GetString can hand back NUL / whitespace.
    private static string? CleanString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        foreach (char c in raw)
            if (c != '\0' && !char.IsWhiteSpace(c)) return raw.Trim();
        return null;
    }
}
