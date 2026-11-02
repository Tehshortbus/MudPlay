using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;

namespace MudPlay.Services;

// Keeps every value in a settings file to what its field may hold, as the file is
// read. global.json, a bbs.json and a profile.json are the files a user edits by
// hand, imports or carries between machines, and a value nobody would set (a
// 100000-column terminal, a cleanup window of two billion days) crashed startup or
// had it allocate tens of gigabytes. JsonStore.Load hands each of the three to
// Deserialize.
//
// A file the serializer reads as it stands (every file, normally) is read that way,
// and the object it gives is then walked for a number outside its field's
// SettingRange, pulled to the nearer end, and an enum number the enum doesn't have,
// set to the field's default. Only a file the serializer refuses is read as a tree,
// fixed and tried again:
//
//   - a number its field's type can't hold (1e300 in an int) is pulled into the
//     type's range, and a fraction in a whole-number field is rounded;
//   - anything else one field holds that its type can't take (text in a number, a
//     list where an object goes) is left out, so that field takes the value from
//     the tier below, or the default.
//
// The serializer doesn't hold a field to its type's promise of a value: a null
// written into a text, list or record field, or a positional record's field the file
// leaves out, reads back null, and the code reading it throws far from the file. So
// the walk also gives such a field its default, and takes a null entry out of a list
// or map whose entries have to hold something.
//
// One bad field used to fail the whole file: a profile that wouldn't parse came up
// as the default profile, the character's whole workspace missing for the session.
// Now only that field is lost. The tab-keyed sections each tier's Settings
// dictionary holds are checked against their own DTOs the same way.
//
// The values used are what the client reads, and what the file holds if it is
// saved; reading never writes it. A value left out is gone from the file at its
// next save, so the file as it was is first copied beside it
// (<file>.unreadable-<UTC time>-NN.bak, the newest BackupsKept kept). Each value moved or left out
// is raised on Clamped (AppServices writes it to the program log at Warn), naming
// the field, the tier, the value found and the value used. A file that isn't JSON at
// all still fails to load, as it always did.
public static class SettingsValueGuard
{
    // Raised with a line for the program log per value moved or left out. A read of
    // a file says only what the last read of the same file didn't: the same file is
    // read again on every profile listing, heartbeat merge and reload, and a value
    // fixed and then broken again is said again.
    public static event Action<string>? Clamped;

    // The tab-keyed sections every tier's Settings dictionary holds, by key: what a
    // section's values are checked against. A key not listed is left as it is.
    public static readonly IReadOnlyDictionary<string, Type> Sections = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        ["AutoLair"] = typeof(AutoLairSettings),
        ["AutoLight"] = typeof(AutoLightSettings),
        ["AutoTrainer"] = typeof(AutoTrainerSettings),
        ["Cash"] = typeof(CashSettings),
        ["Combat"] = typeof(CombatSettings),
        ["Confirm"] = typeof(ConfirmSettings),
        ["ContextMenu"] = typeof(ContextMenuSettings),
        ["General"] = typeof(GeneralSettings),
        ["Health"] = typeof(HealthSettings),
        ["HelpWebsites"] = typeof(HelpWebsitesSettings),
        ["LogDiagnostics"] = typeof(LogDiagnosticsSettings),
        ["Other"] = typeof(OtherSettings),
        ["Party"] = typeof(PartySettings),
        [PeriodicDamageRoomSpellSettings.TabKey] = typeof(PeriodicDamageRoomSpellSettings),
        ["Pvp"] = typeof(PvpSettings),
        [RoundTotalsWindowSettings.SectionKey] = typeof(RoundTotalsWindowSettings),
        ["Sounds"] = typeof(SoundSettings),
        ["Spells"] = typeof(SpellsSettings),
        ["Statline"] = typeof(StatlineSettings),
        [StatusBarSettings.SectionKey] = typeof(StatusBarSettings),
        ["Talk"] = typeof(TalkSettings),
        ["Teleports"] = typeof(TeleportSettings),
        ["Toolbar"] = typeof(ToolbarSettings),
    };

    // How many copies of a file's state before a value was left out are kept.
    public const int BackupsKept = 3;

    // The tier a file deserialized as type is, or null when type is not one of the
    // settings files.
    public static string? TierOf(Type type) =>
        type == typeof(GlobalSettings) ? "Global"
        : type == typeof(BbsProfile) ? "BBS"
        : type == typeof(CharacterProfile) ? "Character"
        : null;

    // Deserialize json, read from path, as T with its values held to their fields.
    public static T? Deserialize<T>(string json, string path, string tier) where T : class
    {
        List<SettingsClamp> clamps = new();
        T? result;
        bool read;
        try
        {
            result = JsonSerializer.Deserialize<T>(json, JsonStore.Options);
            read = true;
        }
        catch (JsonException)
        {
            result = null;
            read = false;
        }

        if (read)
        {
            if (result is not null) Walk(result, PlanOf(typeof(T)), new List<object>(), clamps, apply: true);
        }
        else
        {
            // Not JSON at all throws here, as it always did. Built a property at a time,
            // so a key written twice is taken as the serializer takes it: the last.
            JsonNode? node = ToNode(JsonDocument.Parse(json, DocumentOptions).RootElement);
            Fix(node, typeof(T), null, "", RootFallback, clamps);
            result = (T?)DeserializeLeavingOutBadFields(node, typeof(T), "", RootFallback, clamps);
            // The tree is fixed for what the serializer refuses; a null it takes.
            if (result is not null) Walk(result, PlanOf(typeof(T)), new List<object>(), clamps, apply: true);
        }

        string? backup = clamps.Any(c => c.LeftOut) ? KeepBackup(path, json) : null;
        List<string> lines = new(clamps.Count);
        foreach (SettingsClamp c in clamps)
        {
            string line = $"{tier} setting {c.Field} in {path} was {c.Found}, {c.Why}; using {c.Used} (written to the file if it is saved).";
            if (c.LeftOut && backup is not null) line += $" The file as it was is kept as {Path.GetFileName(backup)}.";
            lines.Add(line);
        }
        Report(path, lines);
        return result;
    }

    private const string RootFallback = "the default";
    private const string SectionFallback = "the value from the tier below, or the default";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = JsonStore.Options.AllowTrailingCommas,
        CommentHandling = JsonStore.Options.ReadCommentHandling,
    };

    // ----- The object a file read as it stands --------------------------------

    private enum Check { Number, NumberList, NumberMap, Enum, EnumList, EnumKeys, Child, ChildList, ChildMap, Sections, Required, RequiredItems }

    // Fresh makes the value a Required field is given: a new one each time, so two
    // fixed fields never share one list.
    private sealed record Step(PropertyInfo Property, string Name, Check Check, Type Type,
        SettingRangeAttribute? Range, NumberKind? Kind, object? Default, Func<object>? Fresh = null);

    // What to look at in an object of each type, cached: a profile's record lists
    // hold thousands of the same type. A type with nothing to check, all the way
    // down, has an empty plan and is passed over.
    private static readonly ConcurrentDictionary<Type, Step[]> Plans = new();

    [ThreadStatic] private static HashSet<Type>? _planning;

    private static Step[] PlanOf(Type type)
    {
        if (Plans.TryGetValue(type, out Step[]? plan)) return plan;
        _planning ??= new HashSet<Type>();
        // A type met again while its own plan is being made (a tree of itself) has
        // no plan yet; MakePlan steps into it anyway (IsPlanning), and the walk
        // reads the finished plan when it gets there.
        if (!_planning.Add(type)) return Array.Empty<Step>();
        try
        {
            plan = MakePlan(type);
        }
        finally
        {
            _planning.Remove(type);
        }
        return Plans.GetOrAdd(type, plan);
    }

    private static bool IsPlanning(Type type) => _planning?.Contains(type) == true;

    // Worth stepping into: a type of ours with something to check, or one whose plan
    // is still being made (a tree of itself: a context menu's entries hold entries).
    private static bool HasWork(Type type) => IsPlanning(type) || PlanOf(type).Length > 0;

    private static Step[] MakePlan(Type type)
    {
        if (!IsOwnType(type)) return Array.Empty<Step>();
        object? defaults = type.IsValueType || type.GetConstructor(Type.EmptyTypes) is not null ? Activator.CreateInstance(type) : null;
        NullabilityInfoContext nullability = new();
        List<Step> steps = new();
        foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
            if (p.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always }) continue;
            string name = p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name;
            SettingRangeAttribute? range = p.GetCustomAttribute<SettingRangeAttribute>();
            Type t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            Type? element = ElementType(t), value = DictionaryValueType(t), key = DictionaryKeyType(t);
            NullabilityInfo declared = nullability.Create(p);
            // Ahead of the property's other steps, which then look over the value given.
            if (!t.IsValueType && p.CanWrite && declared.ReadState == NullabilityState.NotNull
                && FreshOf(p, type, defaults) is { } fresh)
                steps.Add(new Step(p, name, Check.Required, t, null, null, null, fresh));
            if (EntriesMustHoldValues(declared, t))
                steps.Add(new Step(p, name, Check.RequiredItems, t, null, null, null));
            if (HoldsSections(p))
                steps.Add(new Step(p, name, Check.Sections, t, null, null, null));
            else if (NumberKind.Of(t) is { } kind)
            {
                if (range is not null && p.CanWrite) steps.Add(new Step(p, name, Check.Number, t, range, kind, null));
            }
            else if (t.IsEnum)
            {
                if (p.CanWrite) steps.Add(new Step(p, name, Check.Enum, t, null, null, DefaultOf(p, t, defaults)));
            }
            else if (element is not null)
            {
                Type e = Nullable.GetUnderlyingType(element) ?? element;
                if (NumberKind.Of(e) is { } k)
                {
                    if (range is not null) steps.Add(new Step(p, name, Check.NumberList, e, range, k, null));
                }
                else if (e.IsEnum) steps.Add(new Step(p, name, Check.EnumList, e, null, null, null));
                else if (HasWork(e)) steps.Add(new Step(p, name, Check.ChildList, e, null, null, null));
            }
            else if (value is not null)
            {
                if (key is { IsEnum: true }) steps.Add(new Step(p, name, Check.EnumKeys, key, null, null, null));
                Type v = Nullable.GetUnderlyingType(value) ?? value;
                if (NumberKind.Of(v) is { } k)
                {
                    if (range is not null) steps.Add(new Step(p, name, Check.NumberMap, v, range, k, null));
                }
                else if (HasWork(v)) steps.Add(new Step(p, name, Check.ChildMap, v, null, null, null));
            }
            else if (HasWork(t))
                steps.Add(new Step(p, name, Check.Child, t, null, null, null));
        }
        return steps.ToArray();
    }

    // What a field that has to hold a value is given in place of a null: the DTO's own
    // default, else an empty text or collection. None for a field with neither (an
    // interface-typed list on a positional record), which is left as it is.
    private static Func<object>? FreshOf(PropertyInfo p, Type type, object? defaults)
    {
        if (defaults is not null && p.GetValue(defaults) is { } own)
            return own is string text ? () => text : () => p.GetValue(Activator.CreateInstance(type))!;
        if (p.PropertyType == typeof(string)) return () => string.Empty;
        if (p.PropertyType is { IsClass: true, IsAbstract: false } c && c.GetConstructor(Type.EmptyTypes) is not null)
            return () => Activator.CreateInstance(c)!;
        return null;
    }

    // A list's or map's entries are declared to hold a value (a List<string>, not a
    // List<string?>), so a null among them is taken out.
    private static bool EntriesMustHoldValues(NullabilityInfo declared, Type t)
    {
        NullabilityInfo? entry = t.IsArray ? declared.ElementType
            : ElementType(t) is not null ? declared.GenericTypeArguments[0]
            : DictionaryValueType(t) is not null ? declared.GenericTypeArguments[1]
            : null;
        return entry is { ReadState: NullabilityState.NotNull } && !entry.Type.IsValueType;
    }

    // The value an enum field is put back to: the DTO's own default when that is one
    // of the enum's values, else the enum's zero (or null, for a nullable field).
    private static object? DefaultOf(PropertyInfo p, Type enumType, object? defaults)
    {
        object? own = defaults is not null ? p.GetValue(defaults) : null;
        if (own is not null && IsValidEnum(enumType, own)) return own;
        return Nullable.GetUnderlyingType(p.PropertyType) is not null ? null : Activator.CreateInstance(enumType);
    }

    // Look over obj by its plan; with apply, fix what is out of place, else stop at
    // the first thing found. True when something was found.
    private static bool Walk(object obj, Step[] plan, List<object> path, List<SettingsClamp>? clamps, bool apply)
    {
        bool found = false;
        foreach (Step step in plan)
        {
            object? value = step.Property.GetValue(obj);
            if (value is null)
            {
                if (step.Check != Check.Required) continue;
                if (!apply) return true;
                found = true;
                step.Property.SetValue(obj, step.Fresh!());
                path.Add(step.Name);
                clamps!.Add(new SettingsClamp(PathText(path), "null", "which it can't be", FieldDefault));
                path.RemoveAt(path.Count - 1);
                continue;
            }
            path.Add(step.Name);
            try
            {
                switch (step.Check)
                {
                    case Check.Number:
                        if (OutOfRange(value, step, out decimal used))
                        {
                            if (!apply) return true;
                            found = true;
                            step.Property.SetValue(obj, ToNumber(used, step.Type));
                            clamps!.Add(Note(path, value, step, used));
                        }
                        break;
                    case Check.Enum:
                        if (!IsValidEnum(step.Type, value))
                        {
                            if (!apply) return true;
                            found = true;
                            step.Property.SetValue(obj, step.Default);
                            clamps!.Add(new SettingsClamp(PathText(path), Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                                $"not one of {step.Type.Name}'s values", $"the default ({step.Default?.ToString() ?? "none"})", LeftOut: true));
                        }
                        break;
                    case Check.NumberList:
                        if (value is IList list)
                            for (int i = 0; i < list.Count; i++)
                                if (list[i] is { } item && OutOfRange(item, step, out decimal u))
                                {
                                    if (!apply) return true;
                                    found = true;
                                    list[i] = ToNumber(u, step.Type);
                                    path.Add(i);
                                    clamps!.Add(Note(path, item, step, u));
                                    path.RemoveAt(path.Count - 1);
                                }
                        break;
                    case Check.NumberMap:
                        if (value is IDictionary map)
                            foreach (object key in map.Keys.Cast<object>().ToList())
                                if (map[key] is { } item && OutOfRange(item, step, out decimal u))
                                {
                                    if (!apply) return true;
                                    found = true;
                                    map[key] = ToNumber(u, step.Type);
                                    path.Add(new MapKey(key));
                                    clamps!.Add(Note(path, item, step, u));
                                    path.RemoveAt(path.Count - 1);
                                }
                        break;
                    // A struct is walked as a boxed copy and put back once fixed.
                    case Check.Child:
                        if (Walk(value, PlanOf(value.GetType()), path, clamps, apply))
                        {
                            if (!apply) return true;
                            found = true;
                            if (value.GetType().IsValueType && step.Property.CanWrite) step.Property.SetValue(obj, value);
                        }
                        break;
                    case Check.ChildList:
                        if (value is IList children)
                            for (int i = 0; i < children.Count; i++)
                            {
                                if (children[i] is not { } child) continue;
                                path.Add(i);
                                bool inChild = Walk(child, PlanOf(child.GetType()), path, clamps, apply);
                                path.RemoveAt(path.Count - 1);
                                if (inChild && !apply) return true;
                                if (inChild && child.GetType().IsValueType) children[i] = child;
                                found |= inChild;
                            }
                        break;
                    case Check.ChildMap:
                        if (value is IDictionary childMap)
                            foreach (object key in childMap.Keys.Cast<object>().ToList())
                            {
                                if (childMap[key] is not { } child) continue;
                                path.Add(new MapKey(key));
                                bool inChild = Walk(child, PlanOf(child.GetType()), path, clamps, apply);
                                path.RemoveAt(path.Count - 1);
                                if (inChild && !apply) return true;
                                if (inChild && child.GetType().IsValueType) childMap[key] = child;
                                found |= inChild;
                            }
                        break;
                    case Check.EnumList:
                        if (value is IList enums)
                            for (int i = enums.Count - 1; i >= 0; i--)
                                if (enums[i] is { } item && !IsValidEnum(step.Type, item))
                                {
                                    if (!apply) return true;
                                    found = true;
                                    enums.RemoveAt(i);
                                    path.Add(i);
                                    clamps!.Add(LeftOutEnum(path, item, step.Type));
                                    path.RemoveAt(path.Count - 1);
                                }
                        break;
                    case Check.EnumKeys:
                        if (value is IDictionary keyed)
                            foreach (object key in keyed.Keys.Cast<object>().ToList())
                                if (!IsValidEnum(step.Type, key))
                                {
                                    if (!apply) return true;
                                    found = true;
                                    keyed.Remove(key);
                                    path.Add(new MapKey(key));
                                    clamps!.Add(LeftOutEnum(path, key, step.Type));
                                    path.RemoveAt(path.Count - 1);
                                }
                        break;
                    case Check.RequiredItems:
                        if (value is IList { IsFixedSize: false } entries)
                        {
                            for (int i = entries.Count - 1; i >= 0; i--)
                                if (entries[i] is null)
                                {
                                    if (!apply) return true;
                                    found = true;
                                    entries.RemoveAt(i);
                                    path.Add(i);
                                    clamps!.Add(LeftOutNull(path));
                                    path.RemoveAt(path.Count - 1);
                                }
                        }
                        else if (value is IDictionary keyedEntries)
                        {
                            foreach (object key in keyedEntries.Keys.Cast<object>().ToList())
                                if (keyedEntries[key] is null)
                                {
                                    if (!apply) return true;
                                    found = true;
                                    keyedEntries.Remove(key);
                                    path.Add(new MapKey(key));
                                    clamps!.Add(LeftOutNull(path));
                                    path.RemoveAt(path.Count - 1);
                                }
                        }
                        break;
                    case Check.Sections:
                        if (apply && value is Dictionary<string, JsonElement> sections)
                            foreach (string key in sections.Keys.ToList())
                                if (Sections.TryGetValue(key, out Type? sectionType))
                                    found |= CheckSection(sections, key, sectionType, $"{PathText(path)}.{key}", clamps!);
                        break;
                }
            }
            finally
            {
                path.RemoveAt(path.Count - 1);
            }
        }
        return found;
    }

    // A section of a file that read as it stands: read as its DTO and looked over,
    // and only one with something out of place is fixed as a tree. A section holds
    // only the fields set at its tier, so it is fixed where it is written, never
    // replaced by the whole DTO.
    private static bool CheckSection(Dictionary<string, JsonElement> sections, string key, Type type, string at, List<SettingsClamp> clamps)
    {
        JsonElement element = sections[key];
        if (element.ValueKind != JsonValueKind.Object) return false;
        try
        {
            if (element.Deserialize(type, JsonStore.Options) is not { } section
                || !Walk(section, PlanOf(type), new List<object>(), null, apply: false))
                return false;
        }
        catch (JsonException)
        {
            // Fixed below.
        }
        JsonNode node = ToNode(element)!;
        if (!FixSection(node, type, at, clamps)) return false;
        sections[key] = JsonSerializer.SerializeToElement(node, JsonStore.Options);
        return true;
    }

    // ----- One section, read on its own -----------------------------------------

    // A section read by itself (SettingsSection), as type, held to its fields as a
    // file's sections are held as the file is read: read as it stands and looked
    // over, and fixed as a tree only when the serializer refuses it. For a section no
    // file read has checked (written in memory since, or under a key Sections doesn't
    // list) and for the tiers merged. where names whose section it is for the log; a
    // field left out reads as the DTO's default. Each value moved or left out is
    // raised on Clamped; false, raised too, when the section can't be read even so,
    // and its reader then takes all of its defaults.
    public static bool TryReadSection(JsonElement section, Type type, string key, string where, out object? value) =>
        TryReadSection(type, key, where, () => section.Deserialize(type, JsonStore.Options), () => ToNode(section), out value);

    // As TryReadSection, for a section held as a tree (the tiers merged); the tree may
    // be changed.
    public static bool TryReadSection(JsonNode section, Type type, string key, string where, out object? value) =>
        TryReadSection(type, key, where, () => section.Deserialize(type, JsonStore.Options), () => section, out value);

    // Raise line on Clamped every time it is said: one line per thing that happened
    // (a skipped write is a user's action that didn't take).
    public static void Say(string line) => Clamped?.Invoke(line);

    private const string FieldDefault = "the default";

    private static bool TryReadSection(Type type, string key, string where,
        Func<object?> plain, Func<JsonNode?> tree, out object? value)
    {
        List<SettingsClamp> clamps = new();
        List<string> lines = new();
        bool read = true;
        value = null;
        try
        {
            value = plain();
            if (value is not null) Walk(value, PlanOf(type), new List<object> { key }, clamps, apply: true);
        }
        catch (JsonException)
        {
            clamps.Clear();
            try
            {
                JsonNode? node = tree();
                Fix(node, type, null, key, FieldDefault, clamps);
                value = DeserializeLeavingOutBadFields(node, type, key, FieldDefault, clamps);
                if (value is not null) Walk(value, PlanOf(type), new List<object> { key }, clamps, apply: true);
            }
            catch (JsonException ex)
            {
                read = false;
                value = null;
                clamps.Clear();
                lines.Add($"{where} section {key} couldn't be read ({Reason(ex)}); using all of its defaults.");
            }
        }
        foreach (SettingsClamp c in clamps)
            lines.Add($"{where} setting {c.Field} was {c.Found}, {c.Why}; using {c.Used}.");
        Report($"{where} section {key}", lines);
        return read;
    }

    private static bool OutOfRange(object value, Step step, out decimal used)
    {
        decimal lo = Math.Max(step.Kind!.Min, ToDecimal(step.Range!.Min));
        decimal hi = Math.Min(step.Kind.Max, ToDecimal(step.Range.Max));
        if (value is double or float && Math.Abs(Convert.ToDouble(value, CultureInfo.InvariantCulture)) >= (double)decimal.MaxValue)
        {
            // Past decimal's ±7.9e28 is past every field's range: only the sign matters.
            used = Convert.ToDouble(value, CultureInfo.InvariantCulture) < 0 ? lo : hi;
            return true;
        }
        decimal d = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        used = Math.Clamp(d, lo, hi);
        return used != d;
    }

    private static SettingsClamp Note(List<object> path, object found, Step step, decimal used) =>
        new(PathText(path), Convert.ToString(found, CultureInfo.InvariantCulture) ?? "",
            $"outside {Show(Math.Max(step.Kind!.Min, ToDecimal(step.Range!.Min)))} to {Show(Math.Min(step.Kind.Max, ToDecimal(step.Range.Max)))}",
            Show(used));

    private static SettingsClamp LeftOutEnum(List<object> path, object value, Type enumType) =>
        new(PathText(path), Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            $"not one of {enumType.Name}'s values", "nothing (left out)", LeftOut: true);

    private static SettingsClamp LeftOutNull(List<object> path) =>
        new(PathText(path), "null", "an entry that has to hold something", "nothing (left out)", LeftOut: true);

    private static object ToNumber(decimal value, Type type) => Convert.ChangeType(value, type, CultureInfo.InvariantCulture);

    // A dictionary key on the path, shown [key] like a list index, not .key like a
    // property.
    private readonly record struct MapKey(object Key);

    private static string PathText(List<object> path) => string.Concat(path.Select((step, i) => step switch
    {
        string name => i == 0 ? name : "." + name,
        MapKey key => $"[{Convert.ToString(key.Key, CultureInfo.InvariantCulture)}]",
        _ => $"[{Convert.ToString(step, CultureInfo.InvariantCulture)}]",
    }));

    private static bool IsValidEnum(Type enumType, object value)
    {
        if (!enumType.IsDefined(typeof(FlagsAttribute), false)) return Enum.IsDefined(enumType, value);
        long all = 0;
        foreach (object v in Enum.GetValues(enumType)) all |= Convert.ToInt64(v, CultureInfo.InvariantCulture);
        return (Convert.ToInt64(value, CultureInfo.InvariantCulture) & ~all) == 0;
    }

    // The client's own DTOs: classes, records and structs (a player's customisation,
    // a key chord), not an enum, a string or a framework type.
    private static bool IsOwnType(Type t) =>
        (t.IsClass || (t.IsValueType && !t.IsPrimitive && !t.IsEnum))
        && t.Namespace?.StartsWith("MudPlay", StringComparison.Ordinal) == true;

    // ----- The tree a file the serializer refused -----------------------------

    // The most fields one read leaves out before it gives up: past that it isn't a
    // field or two gone bad, it's another kind of file.
    private const int MostFieldsLeftOut = 100;

    // Deserialize node as type; a field the serializer refuses is taken out (its
    // path is in the refusal) and the rest tried again.
    private static object? DeserializeLeavingOutBadFields(JsonNode? node, Type type, string at, string fallback, List<SettingsClamp> clamps)
    {
        for (int leftOut = 0; ; leftOut++)
        {
            try
            {
                return node.Deserialize(type, JsonStore.Options);
            }
            catch (JsonException ex) when (leftOut < MostFieldsLeftOut && Detach(node, ex.Path) is { } removed)
            {
                clamps.Add(new SettingsClamp(Join(at, removed.Path), Shown(removed.Node),
                    $"which it can't hold ({Reason(ex)})", fallback, LeftOut: true));
            }
        }
    }

    // Fix a section's tree. False, and nothing said of it but that, when it holds
    // more it can't read than MostFieldsLeftOut: it is then left as written for its
    // own reader, and the rest of the file still loads.
    private static bool FixSection(JsonNode section, Type type, string at, List<SettingsClamp> clamps)
    {
        int before = clamps.Count;
        try
        {
            Fix(section, type, null, at, SectionFallback, clamps);
            DeserializeLeavingOutBadFields(section, type, at, SectionFallback, clamps);
            return true;
        }
        catch (JsonException ex)
        {
            clamps.RemoveRange(before, clamps.Count - before);
            clamps.Add(new SettingsClamp(at, "a section", $"with more than {MostFieldsLeftOut} values it can't read ({Reason(ex)})",
                "the section as written"));
            return false;
        }
    }

    // Returns the node to put in node's place when it had to change, else null.
    private static JsonValue? Fix(JsonNode? node, Type type, SettingRangeAttribute? range, string path, string fallback, List<SettingsClamp> clamps)
    {
        if (node is null) return null;
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (NumberKind.Of(type) is { } kind)
            return node is JsonValue value && value.GetValueKind() == JsonValueKind.Number
                ? FixNumber(value, kind, range, path, clamps)
                : null;
        if (ElementType(type) is { } element)
        {
            if (node is JsonArray array)
            {
                Type e = Nullable.GetUnderlyingType(element) ?? element;
                for (int i = array.Count - 1; i >= 0; i--)
                {
                    if (e.IsEnum && !IsEnumNumberValid(array[i], e))
                    {
                        clamps.Add(new SettingsClamp($"{path}[{i}]", array[i]!.ToJsonString(), $"not one of {e.Name}'s values", "nothing (left out)", LeftOut: true));
                        array.RemoveAt(i);
                    }
                    else if (Fix(array[i], element, range, $"{path}[{i}]", fallback, clamps) is { } changed) array[i] = changed;
                }
            }
            return null;
        }
        if (DictionaryValueType(type) is { } valueType)
        {
            if (node is JsonObject map)
                foreach (string key in map.Select(p => p.Key).ToList())
                {
                    if (DictionaryKeyType(type) is { IsEnum: true } keyType && long.TryParse(key, out long k)
                        && !IsValidEnum(keyType, Enum.ToObject(keyType, k)))
                    {
                        clamps.Add(new SettingsClamp($"{path}[{key}]", key, $"not one of {keyType.Name}'s values", "nothing (left out)", LeftOut: true));
                        map.Remove(key);
                    }
                    else if (Fix(map[key], valueType, range, $"{path}[{key}]", fallback, clamps) is { } changed) map[key] = changed;
                }
            return null;
        }
        if (node is JsonObject obj && Shape(type) is { } shape)
        {
            foreach (string name in obj.Select(p => p.Key).ToList())
            {
                if (!shape.TryGetValue(name, out Member member)) continue;
                string at = path.Length == 0 ? name : $"{path}.{name}";
                if (member.HoldsSections)
                {
                    if (obj[name] is JsonObject sections)
                        foreach (string key in sections.Select(p => p.Key).ToList())
                            if (Sections.TryGetValue(key, out Type? sectionType) && sections[key] is JsonObject section)
                            {
                                JsonNode original = section.DeepClone();
                                if (!FixSection(section, sectionType, $"{at}.{key}", clamps)) sections[key] = original;
                            }
                    continue;
                }
                Type memberType = Nullable.GetUnderlyingType(member.Type) ?? member.Type;
                if (memberType.IsEnum && !IsEnumNumberValid(obj[name], memberType))
                {
                    clamps.Add(new SettingsClamp(at, obj[name]!.ToJsonString(), $"not one of {memberType.Name}'s values", fallback, LeftOut: true));
                    obj.Remove(name);
                    continue;
                }
                if (Fix(obj[name], member.Type, member.Range, at, fallback, clamps) is { } changed) obj[name] = changed;
            }
        }
        return null;
    }

    // False only for a number the enum hasn't got; a name is left to the serializer.
    private static bool IsEnumNumberValid(JsonNode? node, Type enumType) =>
        node is not JsonValue number || number.GetValueKind() != JsonValueKind.Number
        || (number.TryGetValue(out long n) && IsValidEnum(enumType, Enum.ToObject(enumType, n)));

    private static JsonValue? FixNumber(JsonValue value, NumberKind kind, SettingRangeAttribute? range, string path, List<SettingsClamp> clamps)
    {
        decimal lo = kind.Min, hi = kind.Max;
        if (range is not null)
        {
            lo = Math.Max(lo, ToDecimal(range.Min));
            hi = Math.Min(hi, ToDecimal(range.Max));
        }
        string raw = value.ToJsonString();
        decimal used;
        if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal found))
        {
            decimal whole = kind.Whole ? decimal.Round(found, MidpointRounding.AwayFromZero) : found;
            used = Math.Clamp(whole, lo, hi);
            if (used == whole && whole != found)
            {
                clamps.Add(new SettingsClamp(path, raw, "not a whole number", Show(used)));
                return kind.Create(used);
            }
            if (used == found)
            {
                // In range, but a whole-number field written as 5.0 or 5e0 still
                // fails the serializer: the same value, written as it reads it.
                return kind.Whole && raw.Any(c => c is '.' or 'e' or 'E') ? kind.Create(used) : null;
            }
        }
        else
        {
            // Past decimal's ±7.9e28 is past every field's range: only the sign matters.
            used = raw.StartsWith('-') ? lo : hi;
        }
        clamps.Add(new SettingsClamp(path, raw, $"outside {Show(lo)} to {Show(hi)}", Show(used)));
        return kind.Create(used);
    }

    // A tree a property at a time, so a key written twice is set twice and the last
    // stands, as the serializer reads it (a parsed node tree refuses the file).
    private static JsonNode? ToNode(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                JsonObject obj = new();
                foreach (JsonProperty p in element.EnumerateObject()) obj[p.Name] = ToNode(p.Value);
                return obj;
            case JsonValueKind.Array:
                JsonArray array = new();
                foreach (JsonElement item in element.EnumerateArray()) array.Add(ToNode(item));
                return array;
            case JsonValueKind.Null:
                return null;
            default:
                return JsonValue.Create(element);
        }
    }

    private static string Reason(JsonException ex)
    {
        string message = ex.Message;
        int cut = message.IndexOf(" Path:", StringComparison.Ordinal);
        return (cut > 0 ? message[..cut] : message).TrimEnd('.');
    }

    private static string Shown(JsonNode? node)
    {
        string text = node?.ToJsonString() ?? "null";
        return text.Length <= 80 ? text : text[..77] + "...";
    }

    private static string Join(string at, string path) =>
        at.Length == 0 ? path : path.StartsWith('[') ? at + path : $"{at}.{path}";

    // Take the node at a serializer path ($.A.B[2], $['odd key']) out of root.
    // Returns it with the path as the log shows it, or null when the path names
    // nothing that can be taken out (the root itself, or a node not there).
    private static (string Path, JsonNode? Node)? Detach(JsonNode? root, string? path)
    {
        if (root is null || path is null || !path.StartsWith('$')) return null;
        List<object> steps = new();
        int i = 1;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                int end = i + 1;
                while (end < path.Length && path[end] is not ('.' or '[')) end++;
                steps.Add(path[(i + 1)..end]);
                i = end;
            }
            else if (path[i] == '[' && i + 1 < path.Length && path[i + 1] == '\'')
            {
                int end = path.IndexOf("']", i + 2, StringComparison.Ordinal);
                if (end < 0) return null;
                steps.Add(path[(i + 2)..end]);
                i = end + 2;
            }
            else if (path[i] == '[')
            {
                int end = path.IndexOf(']', i);
                if (end < 0 || !int.TryParse(path[(i + 1)..end], out int index)) return null;
                steps.Add(index);
                i = end + 1;
            }
            else return null;
        }
        if (steps.Count == 0) return null;

        JsonNode? parent = root;
        for (int k = 0; k < steps.Count - 1 && parent is not null; k++)
            parent = steps[k] is int n
                ? parent is JsonArray a && n < a.Count ? a[n] : null
                : (parent as JsonObject)?[(string)steps[k]];
        string shown = PathText(steps);
        switch (parent, steps[^1])
        {
            case (JsonObject obj, string name) when obj.ContainsKey(name):
                JsonNode? taken = obj[name];
                obj.Remove(name);
                return (shown, taken);
            case (JsonArray array, int index) when index < array.Count:
                JsonNode? item = array[index];
                array.RemoveAt(index);
                return (shown, item);
            default:
                return null;
        }
    }

    // A bound as decimal, pinned to decimal's range (a range attribute may say
    // double.MaxValue for "no upper bound").
    private static decimal ToDecimal(double bound) =>
        bound >= (double)decimal.MaxValue ? decimal.MaxValue
        : bound <= (double)decimal.MinValue ? decimal.MinValue
        : (decimal)bound;

    private static string Show(decimal d) => d.ToString(CultureInfo.InvariantCulture);

    private static Type? ElementType(Type type)
    {
        if (type.IsArray) return type.GetElementType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() is var g
            && (g == typeof(List<>) || g == typeof(IList<>) || g == typeof(IReadOnlyList<>) || g == typeof(HashSet<>)))
            return type.GetGenericArguments()[0];
        return null;
    }

    private static Type? DictionaryKeyType(Type type) =>
        DictionaryValueType(type) is not null ? Nullable.GetUnderlyingType(type.GetGenericArguments()[0]) ?? type.GetGenericArguments()[0] : null;

    private static Type? DictionaryValueType(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() is var g
            && (g == typeof(Dictionary<,>) || g == typeof(IDictionary<,>) || g == typeof(IReadOnlyDictionary<,>))
            ? type.GetGenericArguments()[1]
            : null;

    // ----- Shapes -----------------------------------------------------------

    private readonly record struct Member(Type Type, SettingRangeAttribute? Range, bool HoldsSections);

    // A type's properties as the serializer names them, for the tree walk.
    private static readonly ConcurrentDictionary<Type, Dictionary<string, Member>?> Shapes = new();

    private static Dictionary<string, Member>? Shape(Type type) => Shapes.GetOrAdd(type, static t =>
    {
        // Only the client's own DTOs (classes and records, structs among them) have a
        // shape worth walking; a JsonElement, a string or a framework type is left alone.
        if (t.IsEnum || t.IsPrimitive || t.Namespace?.StartsWith("MudPlay", StringComparison.Ordinal) != true) return null;
        Dictionary<string, Member> members = new(StringComparer.Ordinal);
        foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // Get-only too: a positional record's are filled through its constructor.
            if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
            if (p.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always }) continue;
            string name = p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name;
            members[name] = new Member(p.PropertyType, p.GetCustomAttribute<SettingRangeAttribute>(), HoldsSections(p));
        }
        return members;
    });

    // The Settings dictionary on each tier's file: its values are sections, typed
    // by their key (Sections), not by the dictionary's JsonElement.
    private static bool HoldsSections(PropertyInfo p) =>
        p.Name == nameof(GlobalSettings.Settings)
        && p.PropertyType == typeof(Dictionary<string, JsonElement>)
        && TierOf(p.DeclaringType!) is not null;

    // ----- Number kinds -------------------------------------------------------

    private sealed record NumberKind(decimal Min, decimal Max, bool Whole, Func<decimal, JsonValue> Create)
    {
        private static readonly Dictionary<Type, NumberKind> Kinds = new()
        {
            [typeof(int)] = new(int.MinValue, int.MaxValue, true, d => JsonValue.Create((int)d)),
            [typeof(long)] = new(long.MinValue, long.MaxValue, true, d => JsonValue.Create((long)d)),
            [typeof(short)] = new(short.MinValue, short.MaxValue, true, d => JsonValue.Create((short)d)),
            [typeof(byte)] = new(byte.MinValue, byte.MaxValue, true, d => JsonValue.Create((byte)d)),
            [typeof(uint)] = new(uint.MinValue, uint.MaxValue, true, d => JsonValue.Create((uint)d)),
            [typeof(double)] = new(decimal.MinValue, decimal.MaxValue, false, d => JsonValue.Create((double)d)),
            [typeof(float)] = new(decimal.MinValue, decimal.MaxValue, false, d => JsonValue.Create((float)d)),
            [typeof(decimal)] = new(decimal.MinValue, decimal.MaxValue, false, d => JsonValue.Create(d)),
        };

        public static NumberKind? Of(Type type) => Kinds.GetValueOrDefault(type);
    }

    // ----- Backups ------------------------------------------------------------

    // Keep json, the file as it was read, as <file>.unreadable-<UTC time>-NN.bak
    // beside it, unless the newest copy already holds the same text, and keep only
    // the newest BackupsKept. The stamp is UTC to the millisecond and every name
    // carries a two-digit count, so names sort in the order they were made, which
    // is what the pruning goes by. A copy is counted on from the newest one when
    // the clock has not passed its stamp (several within one millisecond, or a
    // clock set back). The first free count would not do: once the oldest copy of
    // a stamp is pruned its count is free again, and a copy given it sorts below
    // the ones before it and is pruned as the oldest.
    // Returns the copy that holds the text, or null when none could be made (said,
    // and the load goes on: losing the copy must not cost the profile too).
    private static string? KeepBackup(string path, string json)
    {
        try
        {
            string folder = Path.GetDirectoryName(path)!;
            string prefix = Path.GetFileName(path) + ".unreadable-";
            List<string> kept = Directory.GetFiles(folder, prefix + "*.bak").Order(StringComparer.Ordinal).ToList();
            if (kept.Count > 0 && File.ReadAllText(kept[^1]) == json) return kept[^1];

            string stamp = DateTime.UtcNow.ToString(BackupStampFormat, CultureInfo.InvariantCulture) + "Z";
            int n = 0;
            if (kept.Count > 0 && BackupName(Path.GetFileName(kept[^1]), prefix) is { } newest
                && string.CompareOrdinal(newest.Stamp, stamp) >= 0)
                (stamp, n) = (newest.Stamp, newest.Count + 1);
            string backup = Path.Combine(folder, $"{prefix}{stamp}-{n:00}.bak");
            while (File.Exists(backup)) backup = Path.Combine(folder, $"{prefix}{stamp}-{++n:00}.bak");
            File.WriteAllText(backup, json);
            kept.Add(backup);
            kept.Sort(StringComparer.Ordinal);
            foreach (string old in kept.Take(Math.Max(0, kept.Count - BackupsKept))) File.Delete(old);
            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report(path + " (backup)", new List<string> { $"Couldn't keep a copy of {path} before values in it were left out: {ex.Message}" });
            return null;
        }
    }

    private const string BackupStampFormat = "yyyyMMdd-HHmmssfff";

    // The stamp and count of a copy named <prefix><stamp>Z-NN.bak, or null for a
    // name in any other form (an older client's copies were stamped to the second).
    private static (string Stamp, int Count)? BackupName(string name, string prefix)
    {
        int stampLength = BackupStampFormat.Length + 1;
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".bak", StringComparison.Ordinal)) return null;
        string rest = name[prefix.Length..^".bak".Length];
        if (rest.Length < stampLength + 2 || rest[stampLength - 1] != 'Z' || rest[stampLength] != '-') return null;
        return int.TryParse(rest.AsSpan(stampLength + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int count)
            ? (rest[..stampLength], count)
            : null;
    }

    // ----- Reporting ----------------------------------------------------------

    // The lines each file's last read raised. Bounded by forgetting all of them, at
    // worst saying a file's lines once more.
    private static readonly Dictionary<string, HashSet<string>> SaidFor = new(StringComparer.Ordinal);
    private const int FilesRemembered = 1000;

    private static void Report(string path, List<string> lines)
    {
        Action<string>? handler = Clamped;
        // Nobody listening yet (a read before the log is up): not remembered, so the
        // next read of the file says it.
        if (handler is null) return;
        List<string> fresh;
        lock (SaidFor)
        {
            SaidFor.TryGetValue(path, out HashSet<string>? before);
            fresh = before is null ? lines : lines.Where(l => !before.Contains(l)).ToList();
            if (lines.Count == 0) SaidFor.Remove(path);
            else
            {
                if (SaidFor.Count >= FilesRemembered) SaidFor.Clear();
                SaidFor[path] = new HashSet<string>(lines, StringComparer.Ordinal);
            }
        }
        foreach (string line in fresh) handler(line);
    }
}

// One value SettingsValueGuard moved or left out: where it sits in the file, what
// the file held, why it couldn't stand, what the client uses instead, and whether
// it was left out (lost from the file at its next save) rather than moved.
public readonly record struct SettingsClamp(string Field, string Found, string Why, string Used, bool LeftOut = false);
