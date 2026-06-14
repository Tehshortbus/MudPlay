using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game;
using FujinTerm.Game.Inventory;
using FujinTerm.Game.Recovery;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// Shell view-model for the Character Workshop window. Mirrors MudProxy's
/// <c>CharacterStatusDialog</c> shape: a flat tab strip across the six
/// Phase-10 tabs — Character Info / Death Recovery / Level Projection /
/// CP Allocation / Quest Status / Equipment Manager. Character Info and
/// Death Recovery are wired; the other four are stub placeholders until
/// their tab ships in later Phase-10 PRs.
/// </summary>
public sealed partial class CharacterWorkshopViewModel : ObservableObject
{
    public ObservableCollection<WorkshopSectionViewModel> Sections { get; } = new();

    [ObservableProperty] private WorkshopSectionViewModel? _selectedSection;

    public CharacterWorkshopViewModel(
        DeathRecoveryManager recovery,
        ProfileService profile,
        PlayerStats playerStats,
        GameDataCache gameData,
        InventoryManager inventory,
        PlayerDatabase players,
        string? initialSectionId = null)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(playerStats);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(players);

        // Tab order matches the Phase-10 plan's nav order. Character Info and
        // Death Recovery are wired; the rest are stubs until their PR lands.
        Sections.Add(new CharacterInfoSectionViewModel(playerStats, gameData, inventory, players));

        // The one wired tab.
        Sections.Add(new DeathSectionViewModel(recovery, profile));

        Sections.Add(new StubWorkshopSectionViewModel(
            "levelprojection", "Level Projection",
            "Phase 10 — PR 10.6",
            "Per-level exp curve with HP / MP ranges and regen projection across a target " +
            "level range, realm-aware, reflecting the planned CP build. Wires when the Level " +
            "Projection tab ships."));
        Sections.Add(new StubWorkshopSectionViewModel(
            "cpallocation", "CP Allocation",
            "Phase 10 — PR 10.7–10.9",
            "Editable per-level character-point plan that drives auto-train and the @train " +
            "remote command, and tracks the level-up training window. Wires when the CP " +
            "Allocation tab ships."));
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
    }
}
