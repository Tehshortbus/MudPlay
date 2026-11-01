using System.Text.Json;

namespace MudPlay.Game.Inventory;

// The server's inventory (Anomaly.Items.List): purse, carried, worn and key ring
// replaced from it as a full `i` read replaces them. Encumbrance and the readied
// light still come from the text, which the snapshot doesn't carry.
public sealed partial class InventoryManager
{
    public Func<bool>? GmcpOwnsItems { get; set; }
    // Whether the server's item card says the item is a key, for a key the
    // client's own list doesn't know.
    public Func<int, bool>? ServerItemIsKey { get; set; }
    public event Action<InventorySnapshot>? TextInventoryObserved;

    // True while Changed is raised for the server's inventory: the whole pack read
    // anew, as a text `i` is, not one line's step.
    public bool IsApplyingServerSnapshot { get; private set; }

    // fullSnapshot false re-lays the same snapshot (a new item card, a text read
    // supplying the light), which isn't a fresh inventory read.
    public void ApplyGmcpItems(JsonElement value, bool fullSnapshot = true)
    {
        JsonElement coins = value.GetProperty("coins");
        List<string> carried = new();
        List<string> keys = new();
        List<EquippedItem> worn = new();
        foreach (JsonElement item in value.GetProperty("inv").EnumerateArray())
        {
            string name = item.GetProperty("name").GetString()!;
            bool isKey = _isKey?.Invoke(name) == true || ServerItemIsKey?.Invoke(item.GetProperty("num").GetInt32()) == true;
            (isKey ? keys : carried).Add(name);
        }
        foreach (JsonElement item in value.GetProperty("worn").EnumerateArray())
            worn.Add(new(item.GetProperty("name").GetString()!, ServerSlot(item.GetProperty("slot").GetString()!)));

        lock (_lock)
        {
            long oldCoins = TotalCoins();
            _copper = coins.GetProperty("copper").GetInt32();
            _silver = coins.GetProperty("silver").GetInt32();
            _gold = coins.GetProperty("gold").GetInt32();
            _platinum = coins.GetProperty("platinum").GetInt32();
            _runic = coins.GetProperty("runic").GetInt32();
            _wealthCopper = ComputeWealth(_copper, _silver, _gold, _platinum, _runic);
            // The weight reading comes from the text's `i`; the coins move it as a
            // text coin line would.
            if (_maxWeight > 0) ApplyWeightDelta(oldCoins);
            // The readied light is carried but held apart, as the text read holds it;
            // one no longer carried isn't readied.
            if (_readiedLight is { } light)
            {
                int index = carried.FindIndex(name => name.Equals(light.Name, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) carried.RemoveAt(index);
                else _readiedLight = null;
            }
            _pendingDepositCopper = 0;
            _equipped = worn;
            _carried = NormalizeStacks(carried);
            _keys = keys;
            _loaded = true;
            _lastUpdated = DateTimeOffset.Now;
        }
        _log?.Debug(LogCategory, $"server inventory: {carried.Count} carried, {worn.Count} worn, {keys.Count} keys");
        IsApplyingServerSnapshot = true;
        try { RaiseChanged(); }
        finally { IsApplyingServerSnapshot = false; }
        if (fullSnapshot) RaiseFullInventoryParsed();
    }

    // The server's inventory is owned no longer: nothing is known until the next `i`
    // but the weight, which came from the text's `i` all along and is kept, as a
    // dropped connection keeps it.
    public void ResetGmcpItems()
    {
        lock (_lock)
        {
            _copper = _silver = _gold = _platinum = _runic = 0;
            _wealthCopper = 0;
            _pendingDepositCopper = 0;
            _equipped = Array.Empty<EquippedItem>();
            _carried = Array.Empty<string>();
            _keys = Array.Empty<string>();
            _loaded = false;
            _readiedLight = null;
            _litSinceRead = null;
        }
        RaiseChanged();
    }

    // The server's slot names, as the text's `i` names them.
    private static string ServerSlot(string slot) => slot switch
    {
        "weapon" => "Weapon Hand",
        "off-hand" => "Off-Hand",
        _ => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(slot),
    };
}
