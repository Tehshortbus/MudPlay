using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Edit;

/// <summary>
/// View-model for the Game Data Browser → Monsters tab's per-record
/// edit dialog. Mirrors MegaMUD's Monster/NPC Details dialog: editable
/// left pane (Use-tier, Name, Relationship, Priority, Experience,
/// MaxHP override + Max twin, Pre-attack / Attack spell IDs + counts,
/// Not hostile / Don't backstab flags) + read-only right pane
/// (<see cref="MdbInfo"/> — sourced from the MDB <c>Monsters</c> row).
/// </summary>
public sealed partial class MonsterEditDialogViewModel : ObservableObject, IDialogViewModel<MonsterEditResult>
{
    public event Action<MonsterEditResult?>? CloseRequested;

    public string WccNoStr { get; }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private SettingsTier _useTier = SettingsTier.Character;

    [ObservableProperty] private MonsterRelationship _relationship = MonsterRelationship.Enemy;
    [ObservableProperty] private MonsterAttackPriority _priority = MonsterAttackPriority.Normal;

    [ObservableProperty] private string _experience = string.Empty;
    [ObservableProperty] private string _maxHp = string.Empty;
    [ObservableProperty] private string _maxHpMax = string.Empty;

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

        Experience = (existing?.ExperienceOverride is { } e) ? e.ToString() : string.Empty;
        MaxHp      = (existing?.MaxHpOverride      is { } h) ? h.ToString() : string.Empty;
        MaxHpMax   = (existing?.MaxHpMax           is { } m) ? m.ToString() : string.Empty;

        PreAttackSpellId = (existing?.PreAttackSpellId is { } pi) ? pi.ToString() : string.Empty;
        PreAttackCount   = (existing?.PreAttackCount   is { } pc) ? pc.ToString() : string.Empty;
        AttackSpellId    = (existing?.AttackSpellId    is { } ai) ? ai.ToString() : string.Empty;
        AttackCount      = (existing?.AttackCount      is { } ac) ? ac.ToString() : string.Empty;

        NotHostile   = existing?.NotHostile   ?? false;
        DontBackstab = existing?.DontBackstab ?? false;
    }

    [RelayCommand]
    private void Save()
    {
        MonsterOverlay overlay = new()
        {
            Name               = string.IsNullOrWhiteSpace(Name) ? null : Name,
            Relationship       = Relationship,
            Priority           = Priority,
            ExperienceOverride = ParseNullableInt(Experience),
            MaxHpOverride      = ParseNullableInt(MaxHp),
            MaxHpMax           = ParseNullableInt(MaxHpMax),
            PreAttackSpellId   = ParseNullableInt(PreAttackSpellId),
            PreAttackCount     = ParseNullableInt(PreAttackCount),
            AttackSpellId      = ParseNullableInt(AttackSpellId),
            AttackCount        = ParseNullableInt(AttackCount),
            NotHostile         = NotHostile,
            DontBackstab       = DontBackstab,
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
