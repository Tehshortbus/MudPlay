using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Edit;

/// <summary>
/// View-model for the Game Data Browser → Monsters tab's per-record
/// edit dialog. Mirrors MegaMUD's Monster/NPC Details dialog but only
/// surfaces fields we actually let the user override — the MDB row is
/// canonical for stats like Experience and MaxHP, so those are
/// read-only on the right-pane <see cref="MdbInfo"/> and not duplicated
/// as editable fields. Editable left pane: Use-tier, Name,
/// Relationship, Priority, the two override-spell slots + Max counts,
/// NotHostile / DontBackstab flags.
/// </summary>
public sealed partial class MonsterEditDialogViewModel : ObservableObject, IDialogViewModel<MonsterEditResult>
{
    public event Action<MonsterEditResult?>? CloseRequested;

    public string WccNoStr { get; }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private SettingsTier _useTier = SettingsTier.Character;

    [ObservableProperty] private MonsterRelationship _relationship = MonsterRelationship.Enemy;
    [ObservableProperty] private MonsterAttackPriority _priority = MonsterAttackPriority.Normal;

    [ObservableProperty] private string _preAttackSpellId = string.Empty;
    [ObservableProperty] private string _preAttackCount = string.Empty;
    [ObservableProperty] private string _attackSpellId = string.Empty;
    [ObservableProperty] private string _attackCount = string.Empty;

    [ObservableProperty] private bool _notHostile;
    [ObservableProperty] private bool _dontBackstab;

    /// <summary>Read-only right-pane key/value pairs sourced from the MDB monster row.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> MdbInfo { get; }

    public IReadOnlyList<MonsterRelationship> AvailableRelationships { get; } =
        Enum.GetValues<MonsterRelationship>().ToArray();

    public IReadOnlyList<MonsterAttackPriority> AvailablePriorities { get; } =
        Enum.GetValues<MonsterAttackPriority>().ToArray();

    public IReadOnlyList<SettingsTier> AvailableTiers { get; } =
        Enum.GetValues<SettingsTier>().ToArray();

    public string Title => $"Monster — {(Name.Length > 0 ? Name : $"#{WccNoStr}")}";

    public MonsterEditDialogViewModel(
        string wccNoStr,
        string mdbName,
        MonsterOverlay? existing,
        SettingsTier currentTier,
        IReadOnlyList<KeyValuePair<string, string>> mdbInfo)
    {
        WccNoStr = wccNoStr;
        Name     = existing?.Name ?? mdbName;
        UseTier  = currentTier;
        MdbInfo  = mdbInfo;

        Relationship = existing?.Relationship ?? MonsterRelationship.Enemy;
        Priority     = existing?.Priority     ?? MonsterAttackPriority.Normal;

        PreAttackSpellId = (existing?.OverridePreAttackSpellId is { } pi) ? pi.ToString() : string.Empty;
        PreAttackCount   = (existing?.OverridePreAttackCount   is { } pc) ? pc.ToString() : string.Empty;
        AttackSpellId    = (existing?.OverrideAttackSpellId    is { } ai) ? ai.ToString() : string.Empty;
        AttackCount      = (existing?.OverrideAttackCount      is { } ac) ? ac.ToString() : string.Empty;

        NotHostile   = existing?.NotHostile   ?? false;
        DontBackstab = existing?.DontBackstab ?? false;
    }

    [RelayCommand]
    private void Save()
    {
        MonsterOverlay overlay = new()
        {
            Name                     = string.IsNullOrWhiteSpace(Name) ? null : Name,
            Relationship             = Relationship,
            Priority                 = Priority,
            OverridePreAttackSpellId = ParseNullableInt(PreAttackSpellId),
            OverridePreAttackCount   = ParseNullableInt(PreAttackCount),
            OverrideAttackSpellId    = ParseNullableInt(AttackSpellId),
            OverrideAttackCount      = ParseNullableInt(AttackCount),
            NotHostile               = NotHostile,
            DontBackstab             = DontBackstab,
        };

        CloseRequested?.Invoke(new MonsterEditResult(WccNoStr, overlay, UseTier));
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    private static int? ParseNullableInt(string? text)
        => int.TryParse(text, out int n) ? n : null;
}

/// <summary>Returned by <see cref="MonsterEditDialogViewModel"/> on Save.</summary>
/// <param name="WccNoStr">The monster's WCC No as a string — primary key for the overlay write.</param>
/// <param name="Overlay">The user's edited overlay payload.</param>
/// <param name="Tier">The tier the overlay should be written at.</param>
public sealed record MonsterEditResult(
    string         WccNoStr,
    MonsterOverlay Overlay,
    SettingsTier   Tier);
