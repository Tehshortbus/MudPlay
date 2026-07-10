using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game;
using FujinTerm.Game.Inventory;
using FujinTerm.Game.Recovery;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// Shell view-model for the Character Workshop window: a flat tab strip of
// sections — Character Info / Death Recovery / Level Projection / CP Allocation /
// Quest Status / Equipment Manager.
public sealed partial class CharacterWorkshopViewModel : ObservableObject, IDisposable
{
    private readonly ProfileService _profile;
    private readonly GameDataCache _gameData;

    public ObservableCollection<WorkshopSectionViewModel> Sections { get; } = new();

    [ObservableProperty] private WorkshopSectionViewModel? _selectedSection;

    // Window title — "Player Workshop - {character} - {bbs} - {realm}". Recomputed
    // live as the profile / pinned BBS / active game-data set (realm) change while
    // the window is open.
    [ObservableProperty] private string _windowTitle = "Player Workshop";

    public CharacterWorkshopViewModel(
        DeathRecoveryManager recovery,
        ProfileService profile,
        PlayerStats playerStats,
        GameDataCache gameData,
        InventoryManager inventory,
        PlayerDatabase players,
        AlignmentTracker alignment,
        TrainerWalkManager trainerWalk,
        QuestStore quests,
        EquipmentManager equipment,
        string? initialSectionId = null)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(playerStats);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(alignment);
        ArgumentNullException.ThrowIfNull(trainerWalk);
        ArgumentNullException.ThrowIfNull(quests);
        ArgumentNullException.ThrowIfNull(equipment);
        _profile = profile;
        _gameData = gameData;

        // The Quest Status tab (writer) publishes completed-quest bonuses into this
        // shared state; the Character Info tab (reader) folds them into derived combat.
        var questBonuses = new QuestBonusState();

        Sections.Add(new CharacterInfoSectionViewModel(playerStats, gameData, inventory, players, alignment, questBonuses));

        Sections.Add(new DeathSectionViewModel(recovery, profile));

        // The CP Allocation tab (writer) and Level Projection tab (reader) share
        // one plan state so the projection's HP / regen reflect planned training.
        var planState = new CpPlanState();
        Sections.Add(new LevelProjectionSectionViewModel(playerStats, gameData, planState));

        Sections.Add(new CpAllocationSectionViewModel(playerStats, gameData, inventory, profile, planState, trainerWalk));

        Sections.Add(new QuestSectionViewModel(playerStats, gameData, profile, quests, questBonuses));

        Sections.Add(new EquipmentSectionViewModel(profile, inventory, gameData, equipment, playerStats, players));

        Sections.Add(new CalculatorsSectionViewModel(playerStats, gameData, inventory, questBonuses, profile));

        SelectedSection = initialSectionId is not null
            ? Sections.FirstOrDefault(s => string.Equals(s.Id, initialSectionId, StringComparison.OrdinalIgnoreCase))
              ?? Sections.FirstOrDefault()
            : Sections.FirstOrDefault();

        UpdateTitle();
        _profile.ProfileLoaded += OnProfileTitleChanged;
        _profile.BbsPinApplied += OnProfileTitleChanged;
        _gameData.ActiveSetChanged += OnSetTitleChanged;
    }

    private void OnProfileTitleChanged(CharacterProfile _) => UpdateTitle();
    private void OnSetTitleChanged(string? _) => UpdateTitle();

    private void UpdateTitle()
    {
        string character = _profile.CurrentProfileName ?? "{default}";
        string bbs = _profile.CurrentBbsName ?? "{No BBS}";
        string realm = _gameData.ActiveRealm == RealmType.ParaMud ? "ParaMUD" : "Stock";
        WindowTitle = $"Player Workshop - {character} - {bbs} - {realm}";
    }

    // Dispose every section so they detach from long-lived service events, and
    // unsubscribe the title's own hooks. Called from the Workshop window's Closed
    // handler — the window (and these view-models) are rebuilt on each open.
    public void Dispose()
    {
        _profile.ProfileLoaded -= OnProfileTitleChanged;
        _profile.BbsPinApplied -= OnProfileTitleChanged;
        _gameData.ActiveSetChanged -= OnSetTitleChanged;
        foreach (WorkshopSectionViewModel section in Sections)
            section.Dispose();
    }
}
