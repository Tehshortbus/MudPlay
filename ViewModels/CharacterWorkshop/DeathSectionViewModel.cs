using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Recovery;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.CharacterWorkshop;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// DEATH section — the Death Recovery surface. Binds to
/// <see cref="DeathRecoveryManager"/> for the lives count, the
/// deathpile record grid, the Auto-Recover / Auto-Equip toggles, and
/// the recovery actions (Walk to Room / Recover Now / Mark Recovered /
/// Clear).
/// </summary>
public sealed partial class DeathSectionViewModel : WorkshopSectionViewModel
{
    private readonly DeathRecoveryManager _recovery;
    private readonly ProfileService _profile;
    private Control? _view;

    public override string Id => "death";
    public override string Title => "Death";
    public override Control View => _view ??= new DeathSectionView { DataContext = this };

    [ObservableProperty] private int _livesRemaining;
    [ObservableProperty] private int _deathCount;

    [ObservableProperty] private IReadOnlyList<DeathRecord> _records = Array.Empty<DeathRecord>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WalkToRoomCommand))]
    [NotifyCanExecuteChangedFor(nameof(RecoverNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(MarkRecoveredCommand))]
    [NotifyCanExecuteChangedFor(nameof(ViewEntryCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearSelectedCommand))]
    private DeathRecord? _selectedRecord;

    /// <summary>When true the detail panel under the grid is shown for the selected record.</summary>
    [ObservableProperty] private bool _detailsVisible;

    public DeathSectionViewModel(DeathRecoveryManager recovery, ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(profile);
        _recovery = recovery;
        _profile = profile;
        _recovery.PropertyChanged += OnRecoveryChanged;
        _profile.ProfileLoaded += OnProfileChanged;
        Refresh();
    }

    /// <summary>
    /// Auto-grab a deathpile's lost items on re-entry. Two-way bound to
    /// the manager's persisted per-character toggle. Inert on the actual
    /// grab until inventory tracking lands.
    /// </summary>
    public bool AutoRecover
    {
        get => _recovery.AutoRecover;
        set { if (_recovery.AutoRecover != value) { _recovery.AutoRecover = value; OnPropertyChanged(); } }
    }

    /// <summary>Re-equip items worn at death after recovery. Two-way bound to the manager.</summary>
    public bool AutoEquip
    {
        get => _recovery.AutoEquip;
        set { if (_recovery.AutoEquip != value) { _recovery.AutoEquip = value; OnPropertyChanged(); } }
    }

    /// <summary>Re-pull observables + record list from the manager.</summary>
    public void Refresh()
    {
        LivesRemaining = _recovery.LivesRemaining;
        DeathCount = _recovery.DeathCount;

        DeathRecord? prevSelected = SelectedRecord;
        Records = _recovery.Records.OrderByDescending(r => r.RecordNumber).ToList();
        // Keep the selection pinned to the same record across refresh
        // when it still exists; otherwise drop it.
        SelectedRecord = prevSelected is not null && Records.Contains(prevSelected)
            ? prevSelected
            : null;

        OnPropertyChanged(nameof(AutoRecover));
        OnPropertyChanged(nameof(AutoEquip));
        ClearAllRecoveredCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRecoverSelected))]
    private void WalkToRoom() { if (SelectedRecord is { } r) _recovery.WalkToDeathRoom(r); }

    [RelayCommand(CanExecute = nameof(CanRecoverSelected))]
    private void RecoverNow() { if (SelectedRecord is { } r) _recovery.RecoverNow(r); }

    [RelayCommand(CanExecute = nameof(CanMarkRecovered))]
    private void MarkRecovered() { if (SelectedRecord is { } r) _recovery.MarkRecovered(r); }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ViewEntry() => DetailsVisible = true;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ClearSelected() { if (SelectedRecord is { } r) _recovery.ClearSelected(r); }

    [RelayCommand(CanExecute = nameof(CanClearAllRecovered))]
    private void ClearAllRecovered() => _recovery.ClearAllRecovered();

    [RelayCommand]
    private void SimulateDeath() => _recovery.SimulateDeath();

    private bool HasSelection() => SelectedRecord is not null;
    private bool CanRecoverSelected() => SelectedRecord is { Room: not null } r && r.Status != DeathRecoveryStatus.Recovered;
    private bool CanMarkRecovered() => SelectedRecord is { } r && r.Status != DeathRecoveryStatus.Recovered;
    private bool CanClearAllRecovered() => Records.Any(r => r.Status == DeathRecoveryStatus.Recovered);

    private void OnRecoveryChanged(object? sender, PropertyChangedEventArgs _) => Refresh();
    private void OnProfileChanged(CharacterProfile _) => Refresh();
}
