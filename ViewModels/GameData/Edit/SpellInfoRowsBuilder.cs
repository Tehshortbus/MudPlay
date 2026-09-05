using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.GameData;
using MudPlay.Game.Spells;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Edit;

// Builds the Message/Spell edit dialog's "Game Data" tab content — the read-only field
// dump for a spell (effect summary, curated growth block, magery, abilities with their
// cross-referenced names, teleport destination, textblock-walked summons/casts/item-gates,
// and the "Learned From" / "Casted By" source lists). Extracted from the Spells browser
// tab so the same record can be opened by Number from outside the browser (Room Info →
// SpellRecordDialogService) as well as from a browser row (SpellsSectionViewModel). Pure
// read of the active set's tables; the ColumnFormatters (enum column formatting) come from
// the section so the dialog's enum rows match the grid's.
public sealed class SpellInfoRowsBuilder
{
    private readonly GameDataCache _cache;

    // Enum-column formatters for the Game Data tab, shared with the Spells grid
    // (SpellsSectionViewModel.ColumnFormatters returns this) so the dialog's enum rows and the
    // grid's cells never drift.
    internal static readonly IReadOnlyDictionary<string, Func<string?, string?>> ColumnFormatters =
        new Dictionary<string, Func<string?, string?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Magery"]  = LookupEnums.FormatMagery,
            ["AttType"] = LookupEnums.FormatSpellAttackType,
            ["Targets"] = LookupEnums.FormatSpellTargets,
        };

    // Scaling columns folded into the curated growth block — emitted once, in place of the
    // first of them (MinBase, in MDB key order). The raw per-level numbers are unreadable on
    // their own; SpellGrowthFormatter turns them into a magnitude range + per-level formula.
    private static readonly HashSet<string> _scalingFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "MinBase", "MinInc", "MinIncLVLs",
        "MaxBase", "MaxInc", "MaxIncLVLs",
        "Dur", "DurInc", "DurIncLVLs", "Cap",
    };

    // Human-readable labels for the raw MDB column keys — the Game Data tab reads
    // as plain English instead of the terse Jet column names. A field not listed
    // here keeps its raw name (Number, Name, Learned From, Classes already read
    // fine as-is).
    private static readonly IReadOnlyDictionary<string, string> _friendlyLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Short"] = "Cast Code",
            ["ReqLevel"] = "Required Level",
            ["EnergyCost"] = "Energy Cost",
            ["ManaCost"] = "Mana Cost",
            ["Diff"] = "Difficulty",
            ["TypeOfResists"] = "Resist Type",
            ["Magery"] = "School",
            ["Casted By"] = "Cast By",
            ["LVL Cap"] = "Level Cap",
            ["LVL Increases"] = "Level Scaling",
        };

    private static string FriendlyLabel(string field)
        => _friendlyLabels.TryGetValue(field, out string? friendly) ? friendly : field;

    // Ability codes that are display-only message-record pointers (their AbilVal
    // is a Messages row number, not a magnitude). Meaningless as a field row, so
    // they're dropped from the Game Data tab the same way the effect summary
    // skips them. ConfuseMsg (101), DescMsg (115), StartMsg (120), ShockMsg (137).
    private static readonly HashSet<int> _messageOnlyCodes = new() { 101, 115, 120, 137 };

    // DR (Abil 7) — stored at 10x the applied value (raw 10 -> +1.0 DR).
    private const int DamageResistAbil = 7;

    // Pure-flag ability codes: they carry NO magnitude, so a zero AbilVal on one
    // must render name-only, never a scaled range inherited from a coexisting
    // damage/heal magnitude (e.g. NonMagicalSpell 144 beside a damage spell).
    // Mirrors SpellEffectFormatter's flag list — kept local so this display
    // builder doesn't reach into the formatter's internals.
    private static readonly HashSet<int> _flagOnlyAbil = new()
    {
        23, 51, 52, 80, 97, 98, 100, 108, 109, 110, 111, 112, 113, 119, 138, 144, 178,
    };

    public SpellInfoRowsBuilder(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    // The spell's full imported data for the dialog's Game Data tab. Enum columns (attack-type /
    // targets) format via the shared lookups; Magery and MageryLVL collapse to one "Mage-1" row;
    // the raw per-level scaling columns collapse to a curated growth block (magnitude range, level
    // cap, per-level formula, at-cap duration); each non-zero ability slot resolves to its name
    // with any numeric reference (CastsSp / Summon / EquipItem / …) translated to the real Spell /
    // Monster / Item name; and the "Learned From" / "Casted By" source lists resolve their
    // "Item #N" / "Monster #N" tokens to real names. Empty when the active set has no Spells table
    // or no matching row.
    public IReadOnlyList<GameDataInfoRow> Build(int spellNumber)
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

        // A damage spell gets the interactive level/resist damage calculator on
        // the dialog (SpellDamageCalcViewModel), so suppress the static damage
        // rows that used to show it two contradictory ways: the "Effect" summary,
        // the growth-block magnitude range, and the per-level "Level Scaling" row.
        bool isDamageSpell = formula is { } df && SpellDamageCalculator.IsDamageSpell(df);

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
            if (!isDamageSpell && effect.Length > 0 && effect != "—" && !BareTextblock.IsMatch(effect))
                rows.Add(new GameDataInfoRow("Effect", effect));
        }

        bool teleportRendered = false;
        bool growthRendered = false;
        bool removesRendered = false;
        foreach (JsonProperty prop in el.EnumerateObject())
        {
            string field = prop.Name;

            // Scaling columns collapse into one curated growth block, emitted
            // in place of the first of them encountered (MinBase).
            if (_scalingFields.Contains(field))
            {
                if (!growthRendered)
                {
                    EmitGrowthBlock(rows, formula, isDamageSpell);
                    growthRendered = true;
                }
                continue;
            }

            // MageryLVL folds into the Magery row ("Mage-1"); never shown alone.
            if (string.Equals(field, "MageryLVL", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(field, "Magery", StringComparison.OrdinalIgnoreCase))
            {
                if (MageryDisplay(el, prop.Value) is { } magery)
                    rows.Add(new GameDataInfoRow(FriendlyLabel("Magery"), magery));
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

                // EndCast (151) — the spell this one chain-casts at the end of its
                // effect (e.g. poison bolt's damage then EndCasts the poison-bite DoT
                // #1366). Render a clickable Spells link with its [#N] id, like
                // Casts/Removes/Negate, instead of the generic "EndCast 1366 (…)" text.
                if (code == 151)
                {
                    if (_cache.FindNameByNumber("Spells", val) is not null)
                        rows.Add(BuildLinkRow("End cast", "Spells", new[] { val }));
                    else if (val > 0)
                        rows.Add(new GameDataInfoRow("End cast", val.ToString(CultureInfo.InvariantCulture)));
                    continue;
                }

                // NegateAbility (124) — its value is the negated spell.
                if (code == 124)
                {
                    if (_cache.FindNameByNumber("Spells", val) is { } sp)
                        rows.Add(BuildLinkRow("Negate", "Spells", new[] { val }));
                    else if (val > 0)
                        rows.Add(new GameDataInfoRow("Negate", val.ToString(CultureInfo.InvariantCulture)));
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
                            rows.Add(BuildLinkRow("Summons", "Monsters", fx.Summons));
                        if (fx.Casts.Count > 0)
                            rows.Add(BuildLinkRow("Casts", "Spells", fx.Casts));
                        if (fx.Required.Count > 0)
                            rows.Add(BuildLinkRow("Requires carrying", "Items", fx.Required));
                        if (fx.Avoided.Count > 0)
                            rows.Add(BuildLinkRow("Avoided by carrying", "Items", fx.Avoided));
                    }
                    continue;
                }

                // RemovesSpell (122) — collapse every slot into one linked
                // "Removes" row (the removed spells by name) rather than a raw
                // "RemovesSpell: 132 (mageshield)" line per slot, mirroring the
                // effect summary's "Removes …" clause.
                if (code == 122)
                {
                    if (!removesRendered)
                    {
                        List<int> removed = CollectAbilVals(el, 122);
                        if (removed.Count > 0) rows.Add(BuildLinkRow("Removes", "Spells", removed));
                        removesRendered = true;
                    }
                    continue;
                }

                // Display-only message-record pointers (DescMsg, StartMsg, …) —
                // their value is a Messages row number, meaningless as a field.
                if (_messageOnlyCodes.Contains(code)) continue;

                string abilName = AbilityNames.GetName(code) ?? $"Ability {code}";

                // DR is stored at 10x the applied value — show the real +N.N gain.
                if (code == DamageResistAbil)
                {
                    long drRaw = val != 0 || formula is not { } drF
                        ? val
                        : SpellCalculator.AffectMagnitude(drF, ScaleTopLevel(drF)).Max;
                    rows.Add(new GameDataInfoRow(abilName, SignedTenth(drRaw)));
                    continue;
                }

                // A zero stored value on a plain stat-affect means it SCALES with
                // level off the growth block's Min/Max base. Show the affect's real
                // range (required level -> cap) instead of a meaningless "0" — this
                // is what the effect summary's "AC Blur +5" and the growth block's
                // "Magnitude" both derived from, now stated once against its affect.
                // Flags carry no magnitude and no-magnitude affects fall through to
                // the generic path (they've nothing to scale).
                if (val == 0 && formula is { } affF
                    && ResolveAbilityReference(code, val) is null && !_flagOnlyAbil.Contains(code))
                {
                    // Min: is the base value at the spell's learned level; Max: is
                    // where it grows to at the level cap.
                    long loVal = SpellCalculator.AffectMagnitude(affF, affF.ReqLevel).Max;
                    long hiVal = SpellCalculator.AffectMagnitude(affF, ScaleTopLevel(affF)).Max;
                    if (loVal != 0 || hiVal != 0)
                    {
                        rows.Add(new GameDataInfoRow(
                            abilName, loVal == hiVal ? Signed(hiVal) : $"Min: {Signed(loVal)}, Max: {Signed(hiVal)}"));
                        continue;
                    }
                }

                string valueText = val.ToString(CultureInfo.InvariantCulture);
                if (ResolveAbilityReference(code, val) is { } refName)
                    valueText += $" ({refName})";
                rows.Add(new GameDataInfoRow(abilName, valueText));
                continue;
            }

            // "Casted By" / "Learned From" — a comma-joined list of "<Kind> #N"
            // source tokens. Render each source as a clickable link to its record
            // instead of plain text.
            if (IsSourceListField(field))
            {
                if (BuildSourceListLinkRow(field, prop.Value) is { } srcRow) rows.Add(srcRow);
                continue;
            }

            if (RenderField(field, prop.Value) is { } rendered)
                rows.Add(new GameDataInfoRow(FriendlyLabel(field), rendered));
        }

        AppendNegatedByRow(rows, spellNumber);
        return rows;
    }

    private static bool IsSourceListField(string field) =>
        string.Equals(field, "Casted By", StringComparison.OrdinalIgnoreCase)
        || string.Equals(field, "Learned From", StringComparison.OrdinalIgnoreCase);

    // The blue MdbLink Open command for a record in table (Monsters / Items /
    // Spells), routed through the same AppServices openers the item record's
    // DroppedByRow / ItemLink use. Only the lambda touches AppServices.Current —
    // deferred until a click — so building rows stays free of it.
    private static ICommand OpenCommand(string table, int number) => table switch
    {
        "Monsters" => new RelayCommand(() => AppServices.Current.OpenMonsterGameData(number)),
        "Items"    => new RelayCommand(() => AppServices.Current.OpenItemGameData(number)),
        "Spells"   => new AsyncRelayCommand(() => AppServices.Current.OpenSpellRecordAsync(number)),
        _          => new RelayCommand(() => { }),
    };

    // A row whose value is a list of record references (ids in table), each a
    // clickable link. Value keeps the plain comma-joined names as the text
    // fallback; Links carries the clickable segments with their inline "," gaps.
    private GameDataInfoRow BuildLinkRow(string label, string table, IReadOnlyList<int> ids)
    {
        var names = new List<string>(ids.Count);
        var links = new List<GameDataRecordLink>(ids.Count);
        for (int i = 0; i < ids.Count; i++)
        {
            string name = _cache.FindNameByNumber(table, ids[i]) ?? $"{table.TrimEnd('s')} #{ids[i]}";
            names.Add($"{name} [#{ids[i]}]");
            string sep = i < ids.Count - 1 ? ", " : string.Empty;
            // The [#N] record number rides in the (plain) trailing so it's shown but isn't
            // part of the clickable name — matching how the Monster record shows its spell refs.
            links.Add(new GameDataRecordLink(name, $" [#{ids[i]}]{sep}", OpenCommand(table, ids[i])));
        }
        return new GameDataInfoRow(label, string.Join(", ", names), links);
    }

    // Link-rendering variant of the source-list cell: resolve each "<Kind> #N"
    // token to its record name, rendering Monsters / Items / Spells as clickable
    // links and other kinds (Room / TextBlock / Class) or unresolvable tokens as
    // inert text. Keeps the MDB's trailing "+" cap marker as a "+ more" tail.
    // Returns null when the cell is empty.
    private GameDataInfoRow? BuildSourceListLinkRow(string field, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || CleanString(value.GetString()) is not { } raw)
            return null;

        string[] parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var names = new List<string>();
        var links = new List<GameDataRecordLink>();
        bool cappedInData = false;
        foreach (string part in parts)
        {
            if (part.Contains('+')) { cappedInData = true; continue; }   // MDB list-cap marker
            (string display, ICommand open, bool linked) = ResolveSource(part);
            if (names.Contains(display, StringComparer.OrdinalIgnoreCase)) continue;
            names.Add(display);
            links.Add(new GameDataRecordLink(display, ", ", open, linked));
        }
        if (links.Count == 0) return cappedInData ? new GameDataInfoRow(FriendlyLabel(field), "+ more") : null;

        // Trim the last real link's separator, then append the cap marker as text.
        GameDataRecordLink last = links[^1];
        links[^1] = new GameDataRecordLink(last.Name, cappedInData ? ", " : string.Empty, last.Open, last.IsLinked);
        if (cappedInData) links.Add(new GameDataRecordLink("+ more", string.Empty, NoOpCommand, isLinked: false));

        string text = string.Join(", ", names) + (cappedInData ? ", + more" : string.Empty);
        return new GameDataInfoRow(FriendlyLabel(field), text, links);
    }

    private static readonly ICommand NoOpCommand = new RelayCommand(() => { });

    // Resolve a "<Kind> #N" source token to its display name and open command:
    // clickable for Monsters / Items / Spells, an inert no-op for other kinds
    // (Room / TextBlock / Class) or a token that doesn't resolve (kept as text so
    // nothing is dropped).
    private (string Display, ICommand Open, bool Linked) ResolveSource(string token)
    {
        Match m = SourceToken.Match(token);
        if (m.Success
            && int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
        {
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
            if (table is not null && _cache.FindNameByNumber(table, number) is { } name)
            {
                bool linked = table is "Monsters" or "Items" or "Spells";
                // Show the record number on a spell/monster/item reference, same as the
                // Removes/Casts link rows and the Monster record's spell refs.
                string display = linked ? $"{name} [#{number}]" : name;
                return (display, linked ? OpenCommand(table, number) : NoOpCommand, linked);
            }
        }
        return (token, NoOpCommand, false);
    }

    // Reverse cross-reference: the items that negate this spell. The Items table
    // carries a NegateSpell-0..9 column family (10 slots, each a Spells.Number) —
    // the game's item→spell negation relation, the inverse of the item record's
    // own "Negates" rows — so carrying such an item negates this spell against the
    // bearer. Surfacing it on the spell's own record answers "what counters this?"
    // at a glance. Read directly like the Rooms / TBInfo scans above rather than
    // reusing RoomHazardIndex's hazard-scoped copy, which isn't a reusable
    // accessor and would couple this dialog to the hazard service. Omitted when no
    // item negates the spell; one scan per dialog open (not a hot path).
    private void AppendNegatedByRow(List<GameDataInfoRow> rows, int spellNumber)
    {
        if (spellNumber <= 0) return;
        JsonDocument? doc = _cache.GetRawTable("Items");
        if (doc is null) return;

        var itemNumbers = new List<int>();
        foreach (JsonElement item in doc.RootElement.EnumerateArray())
        {
            bool negates = false;
            for (int i = 0; i < 10 && !negates; i++)
                negates = ReadInt(item, $"NegateSpell-{i}") == spellNumber;
            if (!negates) continue;

            int number = ReadInt(item, "Number");
            if (number > 0 && !itemNumbers.Contains(number)) itemNumbers.Add(number);
        }

        if (itemNumbers.Count > 0)
            rows.Add(BuildLinkRow("Negated by", "Items", itemNumbers));
    }

    // "Mage-1" / "Priest" — the spell's casting school with its magery-level suffix folded in
    // (suffix dropped when MageryLVL is 0). Null when the school value can't be formatted.
    private static string? MageryDisplay(JsonElement el, JsonElement mageryValue)
    {
        string? school = LookupEnums.FormatMagery(
            mageryValue.ValueKind == JsonValueKind.Number ? mageryValue.GetRawText() : null);
        if (string.IsNullOrWhiteSpace(school)) return null;
        int lvl = ReadInt(el, "MageryLVL");
        return lvl > 0 ? $"{school}-{lvl.ToString(CultureInfo.InvariantCulture)}" : school;
    }

    // The curated growth block — magnitude range ("Damage(-MR): 18 to 68"), level cap, per-level
    // growth formula ("Max: 24+(2*lvl)"), and at-cap duration.
    private static void EmitGrowthBlock(List<GameDataInfoRow> rows, SpellFormulaInput? formula, bool isDamageSpell)
    {
        if (formula is not { } f) return;

        // Show the magnitude range ONLY when it names a real damage/heal affect
        // ("Damage(-MR): 18 to 68") — that's data no ability row carries. For a
        // scaling stat-affect the label falls back to a bare "Magnitude", which
        // just repeats the affect's own row (now shown as "AC Blur +5 → +12"), so
        // it's suppressed here to kill the duplication. A damage spell suppresses
        // both the magnitude AND the per-level "Level Scaling" row — the
        // interactive damage calculator owns those.
        string magnitudeLabel = SpellGrowthFormatter.MagnitudeLabel(f);
        if (!isDamageSpell && !string.Equals(magnitudeLabel, "Magnitude", StringComparison.Ordinal)
            && SpellGrowthFormatter.MagnitudeRange(f) is { } range)
            rows.Add(new GameDataInfoRow(magnitudeLabel, range));
        if (f.Cap > 0)
            rows.Add(new GameDataInfoRow(FriendlyLabel("LVL Cap"), f.Cap.ToString(CultureInfo.InvariantCulture)));
        if (!isDamageSpell && SpellGrowthFormatter.GrowthFormula(f) is { } growth)
            rows.Add(new GameDataInfoRow(FriendlyLabel("LVL Increases"), growth));
        long durSecs = SpellGrowthFormatter.DurationSeconds(f);
        if (durSecs > 0)
            rows.Add(new GameDataInfoRow(
                "Duration",
                $"{durSecs.ToString(CultureInfo.InvariantCulture)} {(durSecs == 1 ? "second" : "seconds")}"));
    }

    // "map/room (room name)" for a teleport spell — the destination map comes from TeleportMap
    // (141), the room from TeleportRoom (140), the name resolved against the Rooms table.
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

    // Every AbilVal on the row whose Abil code matches, in slot order — used to
    // collapse the repeated RemovesSpell (122) slots into a single linked row.
    private static List<int> CollectAbilVals(JsonElement el, int code)
    {
        List<int> vals = new();
        for (int i = 0; i < 10; i++)
            if (ReadInt(el, $"Abil-{i}") == code)
            {
                int v = ReadInt(el, $"AbilVal-{i}");
                if (v > 0 && !vals.Contains(v)) vals.Add(v);
            }
        return vals;
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

    // Render one scalar (non-ability) field: enum columns via the shared formatters, the
    // "Learned From" / "Casted By" source lists resolved to real names, blank / NUL text dropped.
    // Returns null to omit the field.
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

        // Boolean / enum-coded columns read better as words than raw integers.
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n))
        {
            if (string.Equals(field, "Learnable", StringComparison.OrdinalIgnoreCase))
                return n != 0 ? "Yes" : "No";
            // EnergyCost is spend-per-fire against the 1000-energy round budget:
            // 0 = a buff cast between combat rounds; otherwise it fires up to
            // floor(1000 / EnergyCost) times a round (a 500-cost nuke twice).
            if (string.Equals(field, "EnergyCost", StringComparison.OrdinalIgnoreCase))
            {
                if (n <= 0) return "0 (between rounds)";
                int perRound = 1000 / n;
                return perRound <= 1
                    ? $"{n.ToString(CultureInfo.InvariantCulture)} (once per round)"
                    : $"{n.ToString(CultureInfo.InvariantCulture)} (up to {perRound.ToString(CultureInfo.InvariantCulture)} times per round)";
            }
            // TypeOfResists gates the full-resist roll: 0 never, 1 only vs an
            // AntiMagic target, 2 always eligible (see GAME_MECHANICS).
            if (string.Equals(field, "TypeOfResists", StringComparison.OrdinalIgnoreCase))
                return n switch
                {
                    0 => "none",
                    1 => "only vs AntiMagic",
                    2 => "normal",
                    _ => n.ToString(CultureInfo.InvariantCulture),
                };
        }

        if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
        if (value.ValueKind == JsonValueKind.String) return CleanString(value.GetString());
        return null;
    }

    // The top level a spell's scaling reaches — its Cap, or ReqLevel when
    // uncapped — used to show an affect's at-cap value.
    private static int ScaleTopLevel(in SpellFormulaInput f) => f.Cap > 0 ? f.Cap : f.ReqLevel;

    // "+10" / "-5" signed magnitude (negatives carry their own minus sign).
    private static string Signed(long value)
        => value > 0
            ? $"+{value.ToString(CultureInfo.InvariantCulture)}"
            : value.ToString(CultureInfo.InvariantCulture);

    // Signed value stored at 10x the applied figure (DR), shown to the tenth:
    // raw 10 -> "+1.0", raw 22 -> "+2.2".
    private static string SignedTenth(long raw)
    {
        string s = (raw / 10.0).ToString("0.0", CultureInfo.InvariantCulture);
        return raw > 0 ? $"+{s}" : s;
    }

    // Reference-bearing ability codes → the table their AbilVal points at, resolved to that row's
    // name. Null when the code isn't a reference, the value is non-positive, or no matching row.
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

    // Effects collected from walking a spell's TBInfo textblock chain.
    private sealed class TextblockEffects
    {
        public readonly List<int> Summons = new();   // summon N (monster numbers)
        public readonly List<int> Casts = new();     // cast N (spell numbers)
        public readonly List<int> Avoided = new();   // failitem N (carrying avoids the effect)
        public readonly List<int> Required = new();  // checkitem N (required for the effect)

        // True once the chain actually does something harmful/active — the item gates are only
        // meaningful when they guard a cast or summon.
        public bool HasEffect => Summons.Count > 0 || Casts.Count > 0;

        public static void AddUnique(List<int> list, int v) { if (!list.Contains(v)) list.Add(v); }
    }

    // Walk a spell's TBInfo textblock action chain (bounded depth, cycle-guarded) and collect what
    // it does: monsters it summons, spells it casts, and the failitem / checkitem item gates around
    // them. Chains follow random N branches and the LinkTo pointer.
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

        // Item gates are only meaningful when the chain produced an active effect.
        if (!fx.HasEffect) { fx.Avoided.Clear(); fx.Required.Clear(); }
        return fx;
    }

    // "<Kind> #<number>" with any trailing chance / qualifier ("(50%)") ignored.
    private static readonly Regex SourceToken = new(@"^([A-Za-z]+)\s*#\s*(\d+)", RegexOptions.Compiled);

    // The effect formatter's bare "TextBlock 9404" fallback — suppressed in favour of the
    // walked Summons / Casts rows.
    private static readonly Regex BareTextblock = new(@"^TextBlock \d+$", RegexOptions.Compiled);

    private static int ReadInt(JsonElement row, string property)
        => row.TryGetProperty(property, out JsonElement e)
           && e.ValueKind == JsonValueKind.Number
           && e.TryGetInt32(out int n) ? n : 0;

    // NUL-aware trim — the MDB importer writes a literal "\0" for empty Jet text columns, so a
    // plain GetString can hand back NUL / whitespace.
    private static string? CleanString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        foreach (char c in raw)
            if (c != '\0' && !char.IsWhiteSpace(c)) return raw.Trim();
        return null;
    }
}
