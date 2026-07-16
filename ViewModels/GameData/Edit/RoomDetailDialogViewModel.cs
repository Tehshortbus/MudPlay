using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game;
using FujinTerm.Game.Calculators;
using FujinTerm.Game.GameData;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Edit;

// Interactive "everything attached to this room" popup — opened from the Rooms
// tab (row double-click) and the Monsters tab's spawn/placed/summoned room
// chips. Reuses RoomTooltipBuilder for the descriptive tail (shop / room spell /
// light / room commands / regen) so the popup never drifts from the Navigation
// map hover, and layers clickable affordances on top:
//   - the room title centres the Navigation map on that room (opening the
//     window if it's closed),
//   - each exit destination re-roots the popup itself on the neighbour and, if
//     the map is already open, lets it follow along (never force-opens it),
//   - each monster name jumps to its Game Data record,
//   - Add / Remove buttons toggle the room on the per-BBS blacklist.
public sealed partial class RoomDetailDialogViewModel
    : ObservableObject, IDialogViewModel<bool>
{
    public event Action<bool>? CloseRequested;

    private readonly AppServices _services;
    private RoomKey _key;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _keyLabel = string.Empty;

    [ObservableProperty]
    private string _monsterHeader = string.Empty;

    public ObservableCollection<RoomDetailLink> Monsters { get; } = new();
    public bool HasMonsters => Monsters.Count > 0;

    public ObservableCollection<RoomDetailLink> Exits { get; } = new();
    public bool HasExits => Exits.Count > 0;

    // Shop readout — a stock table (item · max · regen · buy · sell), rendered
    // only for merchant shops that actually carry stock. Banks fall back to the
    // plain "Shop: <name>" line in ExtrasText; trainers render a level band
    // (below) instead of a stock table.
    public ObservableCollection<ShopStockRow> ShopStock { get; } = new();
    public bool HasShop => ShopStock.Count > 0;

    // The name/subtitle header block is shared by merchant shops and trainers,
    // so it shows whenever either is present. Trainers carry no stock, so they'd
    // fail HasShop's count gate on their own.
    public bool HasShopSection => HasShop || HasTrainer;

    [ObservableProperty]
    private string _shopName = string.Empty;

    [ObservableProperty]
    private string _shopSubtitle = string.Empty;

    // Trainer level band — "Trains levels X–Y" for a training room (ShopType 8),
    // empty otherwise. Its own line so a hypothetical multi-band trainer could
    // list several; the stock schema only ever expresses one contiguous band.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrainer))]
    [NotifyPropertyChangedFor(nameof(HasShopSection))]
    private string _trainerBand = string.Empty;
    public bool HasTrainer => TrainerBand.Length > 0;

    // Per-level training cost at this trainer — one row per level in its band, the
    // copper to advance into that level at the trainer's own markup. Charm plays no
    // part in training price, so these read the same for every character.
    public ObservableCollection<TrainerCostRow> TrainerCosts { get; } = new();
    public bool HasTrainerCosts => TrainerCosts.Count > 0;

    // A wide-band trainer (e.g. a 1–999 sentinel room) would list hundreds of rows;
    // cap the table and flag the tail so the popup stays readable. 80 clears every
    // bounded band in the data — only the 999 sentinel actually trips the cap.
    private const int MaxTrainerCostRows = 80;

    [ObservableProperty]
    private bool _trainerCostsTruncated;

    // Prices are shown at the neutral retail Charm (no discount / surcharge) so
    // the popup reads the same for every character.
    private const int RetailCharm = 50;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExtras))]
    private string _extrasText = string.Empty;
    public bool HasExtras => ExtrasText.Length > 0;

    // Blacklist toggle state — drives the two mutually-exclusive buttons.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddToBlacklist))]
    [NotifyPropertyChangedFor(nameof(CanRemoveFromBlacklist))]
    private bool _isBlacklisted;

    public bool CanAddToBlacklist => !IsBlacklisted;
    public bool CanRemoveFromBlacklist => IsBlacklisted;

    public RoomDetailDialogViewModel(AppServices services, RoomKey key)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        LoadRoom(key);
    }

    // Populate every section for a room. Called once at construction and again
    // whenever an exit is clicked, so the same window walks room-to-room.
    private void LoadRoom(RoomKey key)
    {
        _key = key;
        Monsters.Clear();
        Exits.Clear();
        ShopStock.Clear();
        TrainerCosts.Clear();
        TrainerCostsTruncated = false;
        ShopName = string.Empty;
        ShopSubtitle = string.Empty;
        TrainerBand = string.Empty;

        Room? room = _services.RoomGraph.GetRoom(key);
        if (room is null)
        {
            Title = $"Room {key}";
            KeyLabel = key.ToString();
            MonsterHeader = string.Empty;
            ExtrasText = $"No room record for {key} in the active game-data set.";
            IsBlacklisted = _services.RoomBlacklist.IsBlacklisted(key);
            RaiseSectionVisibility();
            return;
        }

        Title = room.DisplayName;
        KeyLabel = $"Map {key.Map} · Room {key.Room}";
        IsBlacklisted = _services.RoomBlacklist.IsBlacklisted(key);

        // Monsters — lair + summoned (shared resolver), then the placed NPC the
        // tooltip's "Also Here" deliberately omits (a boss / shopkeeper lives on
        // the room's Npc field, not its lair tag).
        IReadOnlyList<RoomTooltipBuilder.RoomMonsterRef> alsoHere =
            RoomTooltipBuilder.ResolveAlsoHere(room, _services.GameData, _services.MonsterSpawns, out int? max);
        MonsterHeader = max is { } m ? $"Also here (max {m}):" : "Also here:";

        var monsterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (RoomTooltipBuilder.RoomMonsterRef mref in alsoHere)
        {
            monsterNames.Add(mref.Name);
            Monsters.Add(MakeMonsterLink(mref.Id, mref.Name, note: null));
        }
        if (room.Npc > 0)
        {
            string placed = _services.GameData.FindNameByNumber("Monsters", room.Npc)
                ?? $"#{room.Npc}";
            if (monsterNames.Add(placed))
                Monsters.Add(MakeMonsterLink(room.Npc, placed, note: "placed"));
        }

        // Exits — one clickable row per obvious exit, using the same ordering +
        // hint rendering as the map tooltip.
        foreach ((Direction dir, RoomExit exit) in RoomTooltipBuilder.OrderedExits(room))
        {
            Room? dest = _services.RoomGraph.GetRoom(exit.Target);
            string destName = dest is not null ? dest.DisplayName : exit.Target.ToString();
            string label = $"{RoomTooltipBuilder.DirectionLabel(dir)} → {destName} ({exit.Target})";
            string hint = RoomTooltipBuilder.FormatExitHint(exit, _services.GameData);
            RoomKey target = exit.Target;
            Exits.Add(new RoomDetailLink(
                label,
                hint.Length > 0 ? hint : null,
                new RelayCommand(() => OnExitClicked(target))));
        }

        bool richShop = PopulateShop(room);

        ExtrasText = RoomTooltipBuilder.BuildDetailExtras(
            room, _services.RoomGraph, _services.GameData, _services.TBInfo,
            _services.SpellCatalog, _services.PlayerIllumination.Current,
            includeShop: !richShop);

        RaiseSectionVisibility();
    }

    // The MajorMUD ShopType that marks a training room. A trainer carries a level
    // band + served class; some (Bard / Thief rooms) also stock items, so a shop
    // can be a trainer AND a merchant at once.
    private const int TrainingShopType = 8;

    // Fill the rich shop section. A merchant with stock gets the item table; a
    // trainer (ShopType 8) gets a level band; a trainer that also stocks items
    // (Bard / Thief rooms) gets both. Assumes the collection + header fields were
    // cleared by LoadRoom. Returns true when anything was rendered (so the plain
    // "Shop:" line is dropped from ExtrasText); false for no shop, an empty bank,
    // or an unresolved shop id — those keep the plain line.
    private bool PopulateShop(Room room)
    {
        if (room.Shop <= 0) return false;

        ShopDefinition? def = ShopInventoryReader.Read(_services.GameData, room.Shop);
        if (def is null) return false;

        bool isTrainer = def.ShopType == TrainingShopType;
        bool hasStock = def.Stock.Count > 0;
        if (!isTrainer && !hasStock) return false;   // bank / empty → plain line

        ShopName = string.IsNullOrEmpty(def.Name) ? $"Shop #{def.Number}" : def.Name;
        ShopSubtitle = BuildShopSubtitle(def, isTrainer, hasStock);

        if (isTrainer)
        {
            TrainerBand = def.MaxLevel > def.MinLevel
                ? $"Trains levels {def.MinLevel}–{def.MaxLevel}"
                : $"Trains level {def.MinLevel}";
            PopulateTrainerCosts(def);
        }

        if (hasStock)
            PopulateStock(def);

        return true;
    }

    // Fill the per-level training cost table. A row's cost is the copper to advance
    // INTO that level, so it's priced off the previous level's exp step —
    // TrainCopper takes level-1. Level 1 is the free 0-exp start, so the table
    // begins at level 2 (or the trainer's floor, whichever is higher). The band is
    // capped at MaxTrainerCostRows; a wider band flags the omitted tail.
    private void PopulateTrainerCosts(ShopDefinition def)
    {
        int first = Math.Max(2, def.MinLevel);
        int last = def.MaxLevel;
        if (last < first) return;

        int shown = 0;
        for (int level = first; level <= last && shown < MaxTrainerCostRows; level++, shown++)
        {
            double copper = ShopPriceCalculator.TrainCopper(level - 1, def.MarkupPercent);
            TrainerCosts.Add(new TrainerCostRow(
                level.ToString(CultureInfo.InvariantCulture),
                ShopPriceCalculator.FormatCopper(copper)));
        }
        TrainerCostsTruncated = last >= first + MaxTrainerCostRows;
    }

    // One subtitle line composed from whichever facets apply: the shop type, the
    // class a trainer serves (ClassRest 0 = any class), and — for a stock-bearing
    // shop — the buy markup + the charm the prices are quoted at.
    private string BuildShopSubtitle(ShopDefinition def, bool isTrainer, bool hasStock)
    {
        var parts = new List<string>
        {
            LookupEnums.FormatShopType(def.ShopType.ToString(CultureInfo.InvariantCulture))
                ?? (isTrainer ? "Training" : "Shop"),
        };
        if (isTrainer)
            parts.Add(def.ClassRestriction > 0
                ? _services.GameData.FindNameByNumber("Classes", def.ClassRestriction)
                    ?? $"Class {def.ClassRestriction}"
                : "all classes");
        if (hasStock)
        {
            if (def.MarkupPercent > 0) parts.Add($"buy markup {def.MarkupPercent}%");
            parts.Add($"prices at {RetailCharm} charm");
        }
        return string.Join(" · ", parts);
    }

    // Populate the stock table rows for a shop that carries stock, priced at the
    // neutral retail charm under the active realm's formula.
    private void PopulateStock(ShopDefinition def)
    {
        RealmType realm = _services.GameData.ActiveRealm;
        foreach (ShopStockEntry e in def.Stock)
        {
            string buy, sell;
            if (e.BaseCopper > 0)
            {
                buy = ShopPriceCalculator.FormatCopper(
                    ShopPriceCalculator.BuyCopper(e.BaseCopper, def.MarkupPercent, RetailCharm));
                sell = ShopPriceCalculator.FormatCopper(
                    ShopPriceCalculator.SellCopper(e.BaseCopper, RetailCharm, realm));
            }
            else
            {
                buy = "Free";
                sell = "—";
            }

            int id = e.ItemId;
            ShopStock.Add(new ShopStockRow(
                e.ItemName,
                e.MaxStock.ToString(CultureInfo.InvariantCulture),
                FormatRegen(e),
                buy, sell,
                new RelayCommand(() => _services.OpenItemGameData(id))));
        }
    }

    // Restock phrase, matching MMUD Explorer's Regen column. %-N 100 → always
    // replenishes; 0 → never self-restocks ("no regen" — the shop only holds one
    // when a player sold it there); between → a probabilistic trickle. Renders as
    // "<%> for <amount> per <period>", the period humanised from minutes.
    private static string FormatRegen(ShopStockEntry e)
    {
        if (e.RestockPercent <= 0) return "no regen";
        int amount = e.RestockAmount > 0 ? e.RestockAmount : 0;
        return $"{e.RestockPercent}% for {amount} per {FormatRegenPeriod(e.RestockPeriod)}";
    }

    // Restock period (Time-N, in minutes) humanised the way the reference client
    // shows it: 10 → "10m", 120 → "2h", 240 → "4h", 90 → "1h30m".
    private static string FormatRegenPeriod(int minutes)
    {
        if (minutes <= 0) return "?";
        int h = minutes / 60, m = minutes % 60;
        if (h > 0 && m > 0) return $"{h}h{m}m";
        return h > 0 ? $"{h}h" : $"{m}m";
    }

    // HasMonsters / HasExits / HasShop track collection counts, not observable
    // fields, so a re-load has to poke their bindings by hand.
    private void RaiseSectionVisibility()
    {
        OnPropertyChanged(nameof(HasMonsters));
        OnPropertyChanged(nameof(HasExits));
        OnPropertyChanged(nameof(HasShop));
        OnPropertyChanged(nameof(HasShopSection));
        OnPropertyChanged(nameof(HasTrainerCosts));
    }

    // Exit click — walk the popup to the neighbour and let an already-open map
    // follow, without dragging the map onto the screen when it's closed.
    private void OnExitClicked(RoomKey target)
    {
        LoadRoom(target);
        _services.CenterNavigationIfOpen(target);
    }

    private RoomDetailLink MakeMonsterLink(int id, string name, string? note)
        => new(name, note, new RelayCommand(() => _services.OpenMonsterGameData(id)));

    // Title / key click — open (or focus) the Navigation map and centre it on
    // whichever room the popup is currently showing.
    [RelayCommand]
    private void OpenInNavigation() => _services.NavigateToRoom(_key);

    [RelayCommand]
    private void AddToBlacklist()
    {
        if (IsBlacklisted) return;
        _services.RoomBlacklist.Add(_key, Title);          // fires Changed → map redraws
        IsBlacklisted = true;
    }

    [RelayCommand]
    private void RemoveFromBlacklist()
    {
        if (!IsBlacklisted) return;
        _services.RoomBlacklist.Remove(_key);              // fires Changed → map redraws
        IsBlacklisted = false;
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(true);
}
