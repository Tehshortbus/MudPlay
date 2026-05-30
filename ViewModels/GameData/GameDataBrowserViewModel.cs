using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Services;
using FujinTerm.ViewModels.GameData.Tables;

namespace FujinTerm.ViewModels.GameData;

/// <summary>
/// Shell view-model for the Game Data Browser window. Holds the
/// sidebar list of sections, a search box, and the currently selected
/// section (whose <see cref="GameDataSectionViewModel.View"/> renders
/// in the content pane).
/// </summary>
/// <remarks>
/// PR 5.4 ships the shell with placeholders for every eventual tab.
/// Per-tab PRs (5.5+) replace each placeholder with a real section
/// view-model. Section list is constructed once in
/// <see cref="SeedSections"/>; tracks the active <see cref="GameDataCache"/>
/// set in the status line so the user always knows which data they're
/// browsing.
/// </remarks>
public sealed partial class GameDataBrowserViewModel : ObservableObject
{
    private readonly GameDataCache _gameData;

    /// <summary>Full section catalog — drives the search filter and the sidebar order.</summary>
    public ObservableCollection<GameDataSectionViewModel> Sections { get; } = new();

    /// <summary>Filtered view the sidebar binds against. Recomputed on search-text change.</summary>
    public ObservableCollection<GameDataSectionViewModel> VisibleSections { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private GameDataSectionViewModel? _selectedSection;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>
    /// Footer status line — shows the active section name and the
    /// active game-data set. Empty set name renders as "(no set)" so
    /// users notice when no data is loaded.
    /// </summary>
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

    public GameDataBrowserViewModel(GameDataCache gameData, string? initialSectionId = null)
        : this(gameData, triggers: null, aliases: null, initialSectionId) { }

    public GameDataBrowserViewModel(
        GameDataCache gameData,
        TriggerEngine? triggers,
        AliasEngine? aliases = null,
        string? initialSectionId = null)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        _gameData = gameData;
        _triggers = triggers;
        _aliases = aliases;
        _gameData.ActiveSetChanged += OnActiveSetChanged;

        SeedSections();
        RebuildVisibleSections();

        SelectedSection = initialSectionId is not null
            ? Sections.FirstOrDefault(s => string.Equals(s.Id, initialSectionId, StringComparison.OrdinalIgnoreCase))
              ?? Sections.FirstOrDefault()
            : Sections.FirstOrDefault();
    }

    private void OnActiveSetChanged(string? set) => OnPropertyChanged(nameof(StatusText));

    partial void OnSearchTextChanged(string value) => RebuildVisibleSections();

    private void RebuildVisibleSections()
    {
        VisibleSections.Clear();
        string filter = (SearchText ?? string.Empty).Trim();

        foreach (GameDataSectionViewModel s in Sections)
        {
            if (filter.Length == 0 ||
                s.SearchableLabels.Any(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            {
                VisibleSections.Add(s);
            }
        }
    }

    /// <summary>
    /// Seed the sidebar. PR 5.4 fills every slot with a placeholder
    /// advertising the eventual PR. Subsequent per-tab PRs replace
    /// the matching <c>Add(...)</c> line with a real section VM.
    /// </summary>
    private void SeedSections()
    {
        Sections.Add(new MonstersSectionViewModel(_gameData));
        Sections.Add(new ItemsSectionViewModel(_gameData));
        Sections.Add(new SpellsSectionViewModel(_gameData));
        Sections.Add(new ConditionsSectionViewModel(_gameData));
        if (_triggers is not null)
            Sections.Add(new TriggersSectionViewModel(_triggers));
        else
            Add("triggers", "Triggers", "Phase 5 PR 5.10",
                "User-defined incoming-text patterns → actions; named session variables shared with Aliases.");
        if (_aliases is not null)
            Sections.Add(new AliasesSectionViewModel(_aliases));
        else
            Add("aliases", "Aliases", "Phase 5 PR 5.11",
                "User-defined outgoing typed-shortcut → command expansion; positional args + shared variables.");
        Sections.Add(new RoomsSectionViewModel(_gameData));
        Sections.Add(new PathsSectionViewModel(_gameData));
        Sections.Add(new LairsSectionViewModel(_gameData));
        Sections.Add(new ShopsSectionViewModel(_gameData));
        Sections.Add(new RacesSectionViewModel(_gameData));
        Sections.Add(new ClassesSectionViewModel(_gameData));
        Sections.Add(new TextBlocksSectionViewModel(_gameData));
        Add("players",       "Players",        "Phase 5 PR 5.20", "In-game `who` observations + manual overrides; per-player remote-command permission flags.");
        Add("favorites",     "Favorites",      "Phase 5 PR 5.21", "Folder hierarchy of named room shortcuts; sidebar of the Phase 7 Goto / Loop dialogs.");
        Add("macros",        "Macros",         "Phase 5 PR 5.22", "Read-only listing — double-click row opens the Phase 10 Macro editor.");

        void Add(string id, string title, string phase, string description)
            => Sections.Add(new PlaceholderGameDataSectionViewModel(id, title, phase, description));
    }
}
