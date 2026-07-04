using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.ViewModels.GameData.Tables;

namespace FujinTerm.ViewModels.GameData;

// Shell view-model for the Game Data Browser window. Holds two sidebar groups
// (engine-backed tabs on top in EngineSections, MDB-derived JSON tables below in
// TableSections), a search box, and the currently selected section (whose View renders
// in the content pane).
public sealed partial class GameDataBrowserViewModel : ObservableObject, IDisposable
{
    private readonly GameDataCache _gameData;

    // Full section catalog — drives the search filter.
    public ObservableCollection<GameDataSectionViewModel> Sections { get; } = new();

    // Top sidebar group — engine-backed tabs that don't come from a MajorMUD MDB
    // (Players / Macros / Triggers / Aliases / Messages).
    public ObservableCollection<GameDataSectionViewModel> EngineSections { get; } = new();

    // Bottom sidebar group — MDB-derived JSON tables (Monsters / Items / Spells / Rooms
    // / Shops / Races / Classes / TextBlocks).
    public ObservableCollection<GameDataSectionViewModel> TableSections { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private GameDataSectionViewModel? _selectedSection;

    [ObservableProperty]
    private string _searchText = string.Empty;

    // Footer status line — shows the active section name and the active game-data set.
    // Empty set name renders as "(no set)" so users notice when no data is loaded.
    public string StatusText
    {
        get
        {
            string section = SelectedSection?.Title ?? "Pick a section from the sidebar.";
            string set = _gameData.ActiveSet ?? "(no set)";
            return $"{section}  ·  Set: {set}";
        }
    }

    private readonly TriggerEngine? _triggers;
    private readonly AliasEngine? _aliases;
    private readonly PlayerDatabase? _players;
    private readonly MacroStore? _macros;
    private readonly MessageStore? _messages;
    private readonly MonsterMessageStore? _monsterMessages;
    private readonly MonsterOverlaySeedStore? _monsterOverlaySeed;
    private readonly ItemOverlaySeedStore? _itemOverlaySeed;
    private readonly SettingsResolver? _resolver;
    private readonly DialogService? _dialogs;
    private readonly KeybindingStore? _keybindings;
    private readonly ProfileService? _profile;
    private readonly RoomGraphManager? _roomGraph;

    public GameDataBrowserViewModel(GameDataCache gameData, string? initialSectionId = null)
        : this(gameData, triggers: null, aliases: null, players: null, macros: null, messages: null, monsterMessages: null, monsterOverlaySeed: null, itemOverlaySeed: null, resolver: null, dialogs: null, keybindings: null, profile: null, roomGraph: null, initialSectionId: initialSectionId) { }

    public GameDataBrowserViewModel(
        GameDataCache gameData,
        TriggerEngine? triggers,
        AliasEngine? aliases = null,
        PlayerDatabase? players = null,
        MacroStore? macros = null,
        MessageStore? messages = null,
        MonsterMessageStore? monsterMessages = null,
        MonsterOverlaySeedStore? monsterOverlaySeed = null,
        ItemOverlaySeedStore? itemOverlaySeed = null,
        SettingsResolver? resolver = null,
        DialogService? dialogs = null,
        KeybindingStore? keybindings = null,
        ProfileService? profile = null,
        RoomGraphManager? roomGraph = null,
        string? initialSectionId = null)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        _gameData = gameData;
        _triggers = triggers;
        _aliases = aliases;
        _players = players;
        _macros = macros;
        _messages = messages;
        _monsterMessages = monsterMessages;
        _monsterOverlaySeed = monsterOverlaySeed;
        _itemOverlaySeed = itemOverlaySeed;
        _resolver = resolver;
        _dialogs = dialogs;
        _keybindings = keybindings;
        _profile = profile;
        _roomGraph = roomGraph;
        _gameData.ActiveSetChanged += OnActiveSetChanged;

        SeedSections();
        RebuildVisibleSections();

        SelectedSection = initialSectionId is not null
            ? Sections.FirstOrDefault(s => string.Equals(s.Id, initialSectionId, StringComparison.OrdinalIgnoreCase))
              ?? Sections.FirstOrDefault()
            : Sections.FirstOrDefault();
    }

    private void OnActiveSetChanged(string? set) => OnPropertyChanged(nameof(StatusText));

    // Detach from GameDataCache.ActiveSetChanged and dispose every section in Sections.
    // The browser window calls this from its Closed handler — without it each open leaks
    // the entire VM tree (sections subscribe to long-lived service events that pin their
    // cached rows and View instances forever, growing the heap on every reopen).
    public void Dispose()
    {
        _gameData.ActiveSetChanged -= OnActiveSetChanged;
        foreach (GameDataSectionViewModel section in Sections)
            section.Dispose();
    }

    partial void OnSearchTextChanged(string value) => RebuildVisibleSections();

    // Drive each section's lazy-load on first selection AND break the dual-ListBox
    // feedback loop. The sidebar splits the section list into two ListBoxes
    // (engine-backed / MDB-derived); both bind SelectedItem TwoWay to SelectedSection.
    // When the user picks an item in one, the other ListBox sees a SelectedSection value
    // not in its ItemsSource and writes back null — which would blow away the real
    // selection. Restore the previous value whenever we receive a null from that feedback
    // path. Sections shouldn't be "no selection" anyway; clicking empty space in a
    // ListBox isn't a meaningful "clear" gesture for this browser.
    partial void OnSelectedSectionChanged(GameDataSectionViewModel? oldValue, GameDataSectionViewModel? newValue)
    {
        if (newValue is null && oldValue is not null)
        {
            SelectedSection = oldValue;
            return;
        }
        if (newValue is Tables.GameDataTableSectionViewModel tab)
            tab.OnActivated();
    }

    private void RebuildVisibleSections()
    {
        string filter = (SearchText ?? string.Empty).Trim();

        EngineSections.Clear();
        TableSections.Clear();

        foreach (GameDataSectionViewModel s in Sections)
        {
            bool matches = filter.Length == 0 ||
                s.SearchableLabels.Any(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase));
            if (!matches) continue;

            // Engine-backed sections live in EngineGroup; everything
            // else is a JSON table — split by the concrete base type
            // so the sidebar can render two groups with a visual
            // separator between them.
            if (s is JsonTableSectionViewModel) TableSections.Add(s);
            else                                EngineSections.Add(s);
        }
    }

    // Seed the sidebar in the order the user expects: engine-backed tabs (Players →
    // Macros → Triggers → Aliases → Messages) first, then MDB-derived tables (Monsters /
    // Items / Spells / Rooms / Shops / Races / Classes / TextBlocks). Placeholder
    // fallbacks kick in for the engine-backed tabs when the corresponding service wasn't
    // passed to the ctor (e.g. tests).
    private void SeedSections()
    {
        // ----- Engine-backed (top group) ----------------------------------

        if (_players is not null)
            Sections.Add(new PlayersSectionViewModel(_players, _dialogs, _profile));
        else
            AddPlaceholder("players", "Players", "Phase 5",
                "In-game `who` observations + manual overrides; per-player remote-command permission flags.");

        if (_macros is not null)
            Sections.Add(new MacrosSectionViewModel(_macros, _dialogs, _keybindings));
        else
            AddPlaceholder("macros", "Macros", "Phase 5 / Phase 10",
                "Read-only listing — double-click row opens the Phase 10 Macro editor.");

        if (_triggers is not null)
            Sections.Add(new TriggersSectionViewModel(_triggers, _dialogs));
        else
            AddPlaceholder("triggers", "Triggers", "Phase 5",
                "User-defined incoming-text patterns → actions; named session variables shared with Aliases.");

        if (_aliases is not null)
            Sections.Add(new AliasesSectionViewModel(_aliases, _dialogs));
        else
            AddPlaceholder("aliases", "Aliases", "Phase 5",
                "User-defined outgoing typed-shortcut → command expansion; positional args + shared variables.");

        if (_messages is not null)
            Sections.Add(new MessagesSectionViewModel(_messages, _dialogs, _resolver, _gameData));
        else
            AddPlaceholder("messages", "Messages", "Phase 5",
                "Per-set Messages/Responses catalogue. Seeded from the wcc-derived JSON at " +
                "Data/Global/Messages.seed.json; per-set edits persist at " +
                "Data/game data/{set}/messages.json.");

        // ----- MDB-derived (bottom group) ---------------------------------

        Sections.Add(new MonstersSectionViewModel(_gameData, _resolver, _dialogs, _monsterMessages, _monsterOverlaySeed, _roomGraph));
        Sections.Add(new ItemsSectionViewModel(_gameData, _resolver, _dialogs, _itemOverlaySeed));
        Sections.Add(new SpellsSectionViewModel(_gameData, _resolver, _messages, _dialogs));
        Sections.Add(new RoomsSectionViewModel(_gameData, _resolver));
        Sections.Add(new LairsSectionViewModel(_gameData, _resolver));
        Sections.Add(new ShopsSectionViewModel(_gameData, _resolver));
        Sections.Add(new RacesSectionViewModel(_gameData, _resolver));
        Sections.Add(new ClassesSectionViewModel(_gameData, _resolver));
        Sections.Add(new TextBlocksSectionViewModel(_gameData, _resolver));
        Sections.Add(new InfoSectionViewModel(_gameData, _resolver));

        // Hook every section's NavigationRequested event so cross-section
        // jumps (e.g. Shops → Rooms double-click) can route through the
        // browser. Wire LAST so every section is in Sections first.
        foreach (GameDataSectionViewModel section in Sections)
            section.NavigationRequested += OnNavigationRequested;

        void AddPlaceholder(string id, string title, string phase, string description)
            => Sections.Add(new PlaceholderGameDataSectionViewModel(id, title, phase, description));
    }

    // Route a section's NavigationRequested event to the named target: activate the
    // target tab, then defer the row-selection one dispatcher tick so the DataGrid has a
    // chance to swap views before we touch SelectedItem. The target's SelectRowMatching
    // queues the predicate when called before the rows materialise.
    //
    // Switching the visible section first matters for a re-selection in the warm-load
    // path: setting SelectedRow while the target tab is still hidden lets the DataGrid
    // skip applying the new selection visual when the tab eventually shows (Avalonia's
    // virtualised DataGrid doesn't always realise a row container for the new
    // SelectedItem if the previous SelectedItem was already in view from a prior
    // navigation).
    private void OnNavigationRequested(NavigationRequest req)
    {
        GameDataSectionViewModel? target =
            Sections.FirstOrDefault(s => string.Equals(s.Id, req.TargetSectionId, StringComparison.OrdinalIgnoreCase));
        if (target is null) return;

        SelectedSection = target;

        if (req.RowSelector is not null && target is Tables.GameDataTableSectionViewModel table)
        {
            // Defer past the tab-swap layout pass. Background priority
            // is low enough that Avalonia's section-switch render runs
            // first; SelectRowMatching either fires immediately (warm
            // load) or queues the predicate for LoadAsync's
            // continuation (cold load).
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => table.SelectRowMatching(req.RowSelector),
                Avalonia.Threading.DispatcherPriority.Background);
        }
    }
}
