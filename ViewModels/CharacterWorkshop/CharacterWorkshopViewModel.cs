using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game;
using FujinTerm.Game.Inventory;
using FujinTerm.Game.Recovery;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// Shell view-model for the Character Workshop window. Mirrors MudProxy's
/// <c>CharacterStatusDialog</c> shape: a flat tab strip across the six
/// Phase-10 tabs — Character Info / Death Recovery / Level Projection /
/// CP Allocation / Quest Status / Equipment Manager. The first four are wired;
/// Quest Status and Equipment Manager remain stub placeholders until their tab
/// ships in later Phase-10 PRs.
/// </summary>
public sealed partial class CharacterWorkshopViewModel : ObservableObject, IDisposable
{
    private readonly ProfileService _profile;
    private readonly GameDataCache _gameData;

    public ObservableCollection<WorkshopSectionViewModel> Sections { get; } = new();

    [ObservableProperty] private WorkshopSectionViewModel? _selectedSection;

    /// <summary>
    /// Window title — <c>"Player Workshop - {character} - {bbs} - {realm}"</c>.
    /// Recomputed live as the profile / pinned BBS / active game-data set
    /// (realm) change while the window is open.
    /// </summary>
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
        _profile = profile;
        _gameData = gameData;

        // Tab order matches the Phase-10 plan's nav order. Character Info and
        // Death Recovery are wired; the rest are stubs until their PR lands.
        Sections.Add(new CharacterInfoSectionViewModel(playerStats, gameData, inventory, players, alignment));

        Sections.Add(new DeathSectionViewModel(recovery, profile));

        // The CP Allocation tab (writer) and Level Projection tab (reader) share
        // one plan state so the projection's HP / regen reflect planned training.
        var planState = new CpPlanState();
        Sections.Add(new LevelProjectionSectionViewModel(playerStats, gameData, planState));

        Sections.Add(new CpAllocationSectionViewModel(playerStats, gameData, inventory, profile, planState, trainerWalk));

        Sections.Add(new StubWorkshopSectionViewModel(
            "queststatus", "Quest Status",
            "Phase 10 — PR 10.10–10.11",
            "Known-quest checklist with base required level, plus per-quest step-flagging " +
            "that walks completion in order. Completing a quest applies its bonuses to " +
            "Character Info. Wires when the Quest Status tab ships."));
        Sections.Add(new StubWorkshopSectionViewModel(
            "equipment", "Equipment Manager",
            "Phase 10 — PR 10.12–10.15",
            "State-based gear-swapping engine: 21-slot grid, saved sets with the fixed " +
            "6-condition trigger list, and the item finder. Snapshot Current pulls from the " +
            "live inventory. Wires when the Equipment Manager tab ships."));

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

    /// <summary>
    /// Dispose every section so they detach from long-lived service events,
    /// and unsubscribe the title's own hooks. Called from the Workshop window's
    /// <c>Closed</c> handler — the window (and these view-models) are rebuilt on
    /// each open.
    /// </summary>
    public void Dispose()
    {
        _profile.ProfileLoaded -= OnProfileTitleChanged;
        _profile.BbsPinApplied -= OnProfileTitleChanged;
        _gameData.ActiveSetChanged -= OnSetTitleChanged;
        foreach (WorkshopSectionViewModel section in Sections)
            section.Dispose();
    }
}
