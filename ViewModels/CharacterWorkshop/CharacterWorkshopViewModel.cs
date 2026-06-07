using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game.Recovery;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// Shell view-model for the Player Workshop window. Mirrors MudProxy's
/// <c>CharacterStatusDialog</c> shape: left-rail navigation across
/// STATS / PROGRESS / EQUIP / COMBAT / QUESTS / DEATH. v1 wires the
/// shell + DEATH section to <see cref="DeathRecoveryManager"/>; the
/// rest are stub placeholders until their feature ships.
/// </summary>
public sealed partial class CharacterWorkshopViewModel : ObservableObject
{
    public ObservableCollection<WorkshopSectionViewModel> Sections { get; } = new();

    [ObservableProperty] private WorkshopSectionViewModel? _selectedSection;

    /// <summary>Raised when the shell wants the host window to close
    /// (Cancel / re-press toggle path).</summary>
    public event Action? CloseRequested;

    public CharacterWorkshopViewModel(
        DeathRecoveryManager recovery,
        ProfileService profile,
        string? initialSectionId = null)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(profile);

        // Section order matches the master plan's left-rail nav.
        Sections.Add(new StubWorkshopSectionViewModel(
            "stats", "Stats",
            "Phase 9 — STATS",
            "Base / equipment / derived stat sheet, live CP allocation, build snapshots, " +
            "and the new-character planner. Wires when STATS section ships."));
        Sections.Add(new StubWorkshopSectionViewModel(
            "progress", "Progress",
            "Phase 9 — PROGRESS",
            "Level / exp curves, EXP / CP calculator, and spell-unlock projection. " +
            "Sole owner of exp progression data in the app."));
        Sections.Add(new StubWorkshopSectionViewModel(
            "equip", "Equipment",
            "Phase 9 — EQUIP",
            "21-slot equipment grid, saved sets with auto-equip triggers, and the item " +
            "finder. Snapshot Current pulls from the live inventory."));
        Sections.Add(new StubWorkshopSectionViewModel(
            "combat", "Combat",
            "Phase 9 — COMBAT",
            "Predicted DPS / rounds-to-kill vs a chosen monster, computed from current " +
            "STATS + EQUIP."));
        Sections.Add(new StubWorkshopSectionViewModel(
            "quests", "Quests",
            "Phase 9 — QUESTS",
            "Checkbox list of known quests (TextBlocks-resolved, class- and realm-filtered). " +
            "Completing a quest applies its bonuses to STATS."));

        // The one wired section.
        Sections.Add(new DeathSectionViewModel(recovery, profile));

        SelectedSection = initialSectionId is not null
            ? Sections.FirstOrDefault(s => string.Equals(s.Id, initialSectionId, StringComparison.OrdinalIgnoreCase))
              ?? Sections.FirstOrDefault()
            : Sections.FirstOrDefault();
    }

    /// <summary>Close the window without committing pending edits (none
    /// exist in v1 — Workshop is observe-only — but the toggle policy
    /// expects the API).</summary>
    public void DiscardAndClose() => CloseRequested?.Invoke();

    /// <summary>Close path triggered by toggle re-press. Same as
    /// <see cref="DiscardAndClose"/> in v1.</summary>
    public void ApplyAndClose() => CloseRequested?.Invoke();
}
