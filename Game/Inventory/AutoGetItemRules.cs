using System.Globalization;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Inventory;

// One item's own Auto-Get Items rules, Auto-collect and Max to get, found by a
// name typed in chat and read or changed for the loaded character.
//
// A change is saved at the "only for this character" tier by the rule the Game
// Data item editor saves by (GameDataOverrideApplier.ApplyFieldsAt), for the asked
// fields alone: each holds for this character whatever a lower tier says, and
// every other field resolves as it did. The editor shows it at that tier, and
// AutoGetItemsManager, which resolves an item's rules on every survey, acts on it
// from the next one.
public sealed class AutoGetItemRules
{
    private const string Table = "Items";

    private readonly SettingsResolver _resolver;
    private readonly ItemNameStore _names;
    private readonly ItemOverlaySeedStore _seed;

    public AutoGetItemRules(SettingsResolver resolver, ItemNameStore names, ItemOverlaySeedStore seed)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(seed);
        _resolver = resolver;
        _names = names;
        _seed = seed;
    }

    // An item's rules as they resolve. Max is null when there is none (blank, or
    // "All"): the engine then collects every copy it sees. CarryOnlyOne holds the
    // engine to one whatever Max says.
    public readonly record struct State(bool AutoCollect, int? Max, bool CannotBeTaken, bool CarryOnlyOne);

    // The items the typed name stands for (ItemNameStore.FindAllByName).
    public IReadOnlyList<int> Find(string typed) => _names.FindAllByName(typed);

    public string NameOf(int number) =>
        _names.GetName(number) ?? $"item #{number.ToString(CultureInfo.InvariantCulture)}";

    // Only a named character has a tier of its own to hold the change.
    public bool CanChange => _resolver.CanWriteAt(SettingsTier.Character);

    public State Read(int number) => StateOf(Resolve(number));

    // Set Auto-collect (when autoCollect is given) and Max to get (when setMax is,
    // a null max clearing it) at the character tier, and return the rules as they
    // now resolve.
    public State Change(int number, bool? autoCollect, bool setMax, int? max)
    {
        ItemOverlay seed = _seed.GetOverlay(number);
        ItemOverlay wanted = Resolve(number);
        List<string> fields = new(2);
        if (autoCollect is { } on)
        {
            wanted = wanted with { AutoCollect = on };
            fields.Add(nameof(ItemOverlay.AutoCollect));
        }
        if (setMax)
        {
            wanted = wanted with { MaxToGet = max?.ToString(CultureInfo.InvariantCulture) };
            fields.Add(nameof(ItemOverlay.MaxToGet));
        }
        GameDataOverrideApplier.ApplyFieldsAt(_resolver, Table, Id(number), SettingsTier.Character, wanted, seed, fields);
        return Read(number);
    }

    private ItemOverlay Resolve(int number) =>
        _resolver.ResolveGameData(Table, Id(number), _seed.GetOverlay(number));

    private static string Id(int number) => number.ToString(CultureInfo.InvariantCulture);

    private static State StateOf(ItemOverlay o)
    {
        int cap = ItemOverlay.ParseCount(o.MaxToGet, int.MaxValue);
        return new State(o.AutoCollect ?? false, cap == int.MaxValue ? null : cap, o.CannotBeTaken ?? false,
            o.CarryOnlyOne ?? false);
    }
}
