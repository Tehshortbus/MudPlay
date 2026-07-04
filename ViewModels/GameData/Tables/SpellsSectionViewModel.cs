using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.GameData;
using FujinTerm.Game.Spells;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.ViewModels.GameData.Edit;

namespace FujinTerm.ViewModels.GameData.Tables;

// Game Data Browser → Spells tab. Renders the imported MajorMUD Spells table — fuel for the
// CastingDirector + the Settings → Spells / Party spell pickers + the Workshop Spell Book.
//
// Column names mirror the MajorMUD MDB schema verbatim. Short is the cast-name shortcode (e.g.
// "star"), ReqLevel is the cast prerequisite, Diff is the cast-difficulty score. Magery, AttType,
// and Targets render via LookupEnums ("Mage" / "Cold" / "Full Area" / etc.).
//
// Double-click a row → opens every MessageRecord that links Spells#N for that row's spell number
// in its own modeless MessageEditDialogViewModel. Multi-message spells (e.g. apostrophe / wording
// variants of one effect line) stack as cascaded windows the user drags apart if needed. Zero
// matches surfaces a one-shot info dialog naming the spell so the user sees the gap rather than a
// silent no-op.
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

    // Double-click handler — opens every Message linked to this spell.
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

    // Mirror of MessagesSectionViewModel.ApplyResult — Id-keyed update-or-append into the store +
    // persist.
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

    // Scaling columns folded into the curated growth block — emitted once, in
    // place of the first of them (MinBase, in MDB key order). The raw per-level
    // numbers are unreadable on their own; SpellGrowthFormatter turns them into
    // a magnitude range + per-level formula instead.
    private static readonly HashSet<string> _scalingFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "MinBase", "MinInc", "MinIncLVLs",
        "MaxBase", "MaxInc", "MaxIncLVLs",
        "Dur", "DurInc", "DurIncLVLs", "Cap",
    };

    // Test seam — exercises BuildSpellInfoRows (the dialog's Game Data tab content) without
    // standing up a dialog.
    internal IReadOnlyList<GameDataInfoRow> BuildSpellInfoRowsForTests(int spellNumber)
        => BuildSpellInfoRows(spellNumber);

    // The spell's full imported data for the dialog's Game Data tab. Enum columns (attack-type /
    // targets) format via the shared lookups; Magery and MageryLVL collapse to one "Mage-1" row;
    // the raw per-level scaling columns collapse to a curated growth block (magnitude range, level
    // cap, per-level formula, at-cap duration); each non-zero ability slot resolves to its name
    // with any numeric reference (CastsSp / Summon / EquipItem / …) translated to the real Spell /
    // Monster / Item name; and the "Learned From" / "Casted By" source lists resolve their
    // "Item #N" / "Monster #N" tokens to real names. Empty when the active set has no Spells table
    // or no matching row.
    private IReadOnlyList<GameDataInfoRow> BuildSpellInfoRows(int spellNumber)
    {
        var rows = new List<GameDataInfoRow>();

        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is null) return rows;

        JsonElement? found = null;
        foreach (JsonElement r in doc.RootElement.EnumerateArray())
            if (ReadInt(r, "Number") == spellNumber) { found = r; break; }
        if (found is not { } el) return rows;

        // Scaling inputs projected once — feeds the curated growth block that
        // replaces the raw MinBase / MaxInc / Cap / … columns.
        KnownSpellCatalog catalog = new(_cache);
        SpellFormulaInput? formula = catalog.GetFormulaByNumber(spellNumber);

        // Plain-English effect summary at the top — the same translator the
        // Items "Other Info" pane + the Spell Book use, so the spell reads as
        // "Dmg 1–4 · then casts X · Slowness" instead of a wall of raw ability
        // codes (the field-by-field rows below stay for the full detail).
        // Rendered at level 0: the formatter clamps Min/Max to ReqLevel so the
        // base figures are still real, and the per-level scaling lives in the
        // curated growth block.
        if (formula is { } effectFormula)
        {
            IReadOnlyDictionary<int, IReadOnlyList<KnownSpell>> tbIndex = catalog.BuildCastByTextblockIndex();
            string effect = SpellEffectFormatter.Format(
                effectFormula, level: 0,
                resolveChain: catalog.GetFormulaByNumber,
                resolveSpellName: catalog.GetSpellNameByNumber,
                resolveTextblockCasts: tb => tbIndex.TryGetValue(tb, out IReadOnlyList<KnownSpell>? list)
                    ? list : Array.Empty<KnownSpell>(),
                resolveMonsterName: n => _cache.FindNameByNumber("Monsters", n));
            // Skip the formatter's unhelpful "TextBlock N" fallback (emitted
            // when a spell's only effect is a textblock it can't expand) — the
            // Summons / Casts / item-gate rows from the textblock walk below
            // carry the real info instead.
            if (effect.Length > 0 && effect != "—" && !BareTextblock.IsMatch(effect))
                rows.Add(new GameDataInfoRow("Effect", effect));
        }

        bool teleportRendered = false;
        bool growthRendered = false;
        foreach (JsonProperty prop in el.EnumerateObject())
        {
            string field = prop.Name;

            // Scaling columns collapse into one curated growth block, emitted
            // in place of the first of them encountered (MinBase).
            if (_scalingFields.Contains(field))
            {
                if (!growthRendered)
                {
                    EmitGrowthBlock(rows, formula);
                    growthRendered = true;
                }
                continue;
            }

            // MageryLVL folds into the Magery row ("Mage-1"); never shown alone.
            if (string.Equals(field, "MageryLVL", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(field, "Magery", StringComparison.OrdinalIgnoreCase))
            {
                if (MageryDisplay(el, prop.Value) is { } magery)
                    rows.Add(new GameDataInfoRow("Magery", magery));
                continue;
            }

            // Ability pairs (Abil-N + AbilVal-N) collapse to one row; skip the
            // value half — it's rendered alongside its code.
            if (field.StartsWith("AbilVal-", StringComparison.Ordinal)) continue;
            if (field.StartsWith("Abil-", StringComparison.Ordinal))
            {
                if (prop.Value.ValueKind != JsonValueKind.Number
                    || !prop.Value.TryGetInt32(out int code) || code == 0)
                    continue;

                // Damage / heal codes (1, 8, 17, 18) feed the curated magnitude
                // range above — don't also render a bare "Damage(-MR): 0" row.
                if (code is 1 or 8 or 17 or 18) continue;

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

                // TextBlock (148) — the spell executes a TBInfo record. The bare
                // record number means nothing to the user; instead walk the
                // textblock's action chain and surface what it actually does:
                // monsters it summons, spells it casts (the damage), and the
                // item gate around them (e.g. the "silver river" room-damage
                // spell is avoided by carrying a raft; a forest room-spell
                // summons monsters unless you hold an item). AbilVal holds the
                // record number; a few spells stash it in MinBase with a zero
                // AbilVal instead.
                if (code == 148)
                {
                    int tb = val > 0 ? val : ReadInt(el, "MinBase");
                    TextblockEffects fx = WalkTextblockChain(tb);
                    if (fx.HasEffect)
                    {
                        if (fx.Summons.Count > 0)
                            rows.Add(new GameDataInfoRow("Summons", JoinNames("Monsters", fx.Summons)));
                        if (fx.Casts.Count > 0)
                            rows.Add(new GameDataInfoRow("Casts", JoinNames("Spells", fx.Casts)));
                        if (fx.Required.Count > 0)
                            rows.Add(new GameDataInfoRow("Requires carrying", JoinNames("Items", fx.Required)));
                        if (fx.Avoided.Count > 0)
                            rows.Add(new GameDataInfoRow("Avoided by carrying", JoinNames("Items", fx.Avoided)));
                    }
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

    // "Mage-1" / "Priest" — the spell's casting school with its magery-level
    // suffix folded in (suffix dropped when MageryLVL is 0). Null when the
    // school value can't be formatted.
    private static string? MageryDisplay(JsonElement el, JsonElement mageryValue)
    {
        string? school = LookupEnums.FormatMagery(
            mageryValue.ValueKind == JsonValueKind.Number ? mageryValue.GetRawText() : null);
        if (string.IsNullOrWhiteSpace(school)) return null;
        int lvl = ReadInt(el, "MageryLVL");
        return lvl > 0 ? $"{school}-{lvl.ToString(CultureInfo.InvariantCulture)}" : school;
    }

    // The curated growth block — magnitude range ("Damage(-MR): 18 to 68"), level cap, per-level
    // growth formula ("Max: 24+(2*lvl)"), and at-cap duration. "LVL Cap" / "LVL Increases", minus
    // the deliberately-omitted "OOM in N rounds" line.
    private static void EmitGrowthBlock(List<GameDataInfoRow> rows, SpellFormulaInput? formula)
    {
        if (formula is not { } f) return;

        if (SpellGrowthFormatter.MagnitudeRange(f) is { } range)
            rows.Add(new GameDataInfoRow(SpellGrowthFormatter.MagnitudeLabel(f), range));
        if (f.Cap > 0)
            rows.Add(new GameDataInfoRow("LVL Cap", f.Cap.ToString(CultureInfo.InvariantCulture)));
        if (SpellGrowthFormatter.GrowthFormula(f) is { } growth)
            rows.Add(new GameDataInfoRow("LVL Increases", growth));
        long durSecs = SpellGrowthFormatter.DurationSeconds(f);
        if (durSecs > 0)
            rows.Add(new GameDataInfoRow(
                "Duration",
                $"{durSecs.ToString(CultureInfo.InvariantCulture)} {(durSecs == 1 ? "second" : "seconds")}"));
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
    // formatters, the "Learned From" / "Casted By" source lists resolved to real
    // names, blank / NUL text dropped. Returns null to omit the field.
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
            bool isSourceList =
                string.Equals(field, "Casted By", StringComparison.OrdinalIgnoreCase)
                || string.Equals(field, "Learned From", StringComparison.OrdinalIgnoreCase);
            return isSourceList ? ResolveSourceList(s) : s;
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

    // A "Learned From" / "Casted By" cell is a comma-joined list of
    // "<Kind> #<number>" source tokens (e.g. "Item #328, Monster #198"). Resolve
    // each token to the real Item / Monster / Spell / … name, dedupe, and list
    // them all — the Game Data tab scrolls, so no app-side truncation. A lone
    // trailing "+" token is the MDB's own cap on a very long list (it stops
    // emitting after ~20 sources); surface that as "+ more" rather than a
    // dangling "+".
    private string ResolveSourceList(string raw)
    {
        string[] parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var names = new List<string>();
        bool cappedInData = false;
        foreach (string part in parts)
        {
            // The MDB caps the list with a trailing "+" marker; depending on
            // where the field length fell it can arrive clean (", +") or
            // glued onto a half-written token (", Ro+"). No real source token
            // (Room M/R, Monster #N, …) contains '+', so any '+' marks the cap.
            if (part.Contains('+')) { cappedInData = true; continue; }
            string name = ResolveSourceToken(part);
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }
        string joined = string.Join(", ", names);
        return cappedInData ? $"{joined}, + more" : joined;
    }

    // Translate one "<Kind> #<number>" source token to the referenced row's
    // name (Item → Items, Monster → Monsters, …). Falls back to the raw token
    // when the kind is unknown or the number has no matching row, so an
    // unresolved reference stays visible rather than being dropped.
    private string ResolveSourceToken(string token)
    {
        Match m = SourceToken.Match(token);
        if (!m.Success
            || !int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            return token;
        string? table = m.Groups[1].Value.ToLowerInvariant() switch
        {
            "item"      => "Items",
            "monster"   => "Monsters",
            "spell"     => "Spells",
            "room"      => "Rooms",
            "textblock" => "TextBlocks",
            "class"     => "Classes",
            _           => null,
        };
        if (table is null) return token;
        return _cache.FindNameByNumber(table, number) ?? token;
    }

    // Effects collected from walking a spell's TBInfo textblock chain.
    private sealed class TextblockEffects
    {
        public readonly List<int> Summons = new();   // summon N (monster numbers)
        public readonly List<int> Casts = new();     // cast N (spell numbers)
        public readonly List<int> Avoided = new();   // failitem N (carrying avoids the effect)
        public readonly List<int> Required = new();  // checkitem N (required for the effect)

        // True once the chain actually does something harmful/active — the item gates are only
        // meaningful when they guard a cast or summon (so a quest give-item textblock isn't
        // mistaken for a damage gate).
        public bool HasEffect => Summons.Count > 0 || Casts.Count > 0;

        public static void AddUnique(List<int> list, int v) { if (!list.Contains(v)) list.Add(v); }
    }

    // Walk a spell's TBInfo textblock action chain (bounded depth, cycle-guarded) and collect what
    // it does: monsters it summons, spells it casts, and the failitem / checkitem item gates
    // around them. Chains follow random N branches and the LinkTo pointer, so effects nested
    // behind a roll (e.g. a forest room-spell whose spawn sits two random-jumps deep) still
    // surface. TBInfo is read via the cache (small table) and indexed once.
    private TextblockEffects WalkTextblockChain(int rootTextblock)
    {
        var fx = new TextblockEffects();
        if (rootTextblock <= 0) return fx;

        JsonDocument? doc = _cache.GetRawTable("TBInfo");
        if (doc is null) return fx;

        var byNumber = new Dictionary<int, JsonElement>();
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            int num = ReadInt(el, "Number");
            if (num > 0) byNumber.TryAdd(num, el);
        }

        const int MaxDepth = 8;
        var visited = new HashSet<int>();

        void Walk(int tb, int depth)
        {
            if (tb <= 0 || depth > MaxDepth || !visited.Add(tb)) return;
            if (!byNumber.TryGetValue(tb, out JsonElement entry)) return;

            string? action = entry.TryGetProperty("Action", out JsonElement a) && a.ValueKind == JsonValueKind.String
                ? a.GetString() : null;
            if (!string.IsNullOrEmpty(action))
            {
                foreach (string line in action.Split('\n'))
                foreach (string rawCmd in line.Split(':'))
                {
                    string[] tok = rawCmd.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (tok.Length < 2 || !int.TryParse(tok[1], out int arg)) continue;
                    switch (tok[0].ToLowerInvariant())
                    {
                        case "summon":    TextblockEffects.AddUnique(fx.Summons, arg); break;
                        case "cast":      TextblockEffects.AddUnique(fx.Casts, arg); break;
                        case "failitem":  TextblockEffects.AddUnique(fx.Avoided, arg); break;
                        case "checkitem": TextblockEffects.AddUnique(fx.Required, arg); break;
                        case "random":    Walk(arg, depth + 1); break;
                    }
                }
            }

            int linkTo = ReadInt(entry, "LinkTo");
            if (linkTo > 0) Walk(linkTo, depth + 1);
        }

        Walk(rootTextblock, 0);

        // Item gates are only meaningful when the chain produced an active
        // effect — otherwise a quest hook's checkitem would look like a gate.
        if (!fx.HasEffect) { fx.Avoided.Clear(); fx.Required.Clear(); }
        return fx;
    }

    // Resolve ids in table to their Name (falling back to "<Table> #N"), comma-joined.
    private string JoinNames(string table, IReadOnlyList<int> ids)
    {
        var names = new List<string>(ids.Count);
        foreach (int id in ids)
            names.Add(_cache.FindNameByNumber(table, id) ?? $"{table.TrimEnd('s')} #{id}");
        return string.Join(", ", names);
    }

    // "<Kind> #<number>" with any trailing chance / qualifier ("(50%)") ignored.
    private static readonly Regex SourceToken = new(@"^([A-Za-z]+)\s*#\s*(\d+)", RegexOptions.Compiled);

    // The effect formatter's bare "TextBlock 9404" fallback — suppressed in
    // favour of the walked Summons / Casts rows.
    private static readonly Regex BareTextblock = new(@"^TextBlock \d+$", RegexOptions.Compiled);

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
