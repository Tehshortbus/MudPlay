using System.ComponentModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game.Recovery;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.CharacterWorkshop;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// DEATH section — binds to <see cref="DeathRecoveryManager"/>'s
/// observables (LivesRemaining / LastKiller / LastDeathAt /
/// DeathCount) and surfaces the full
/// <see cref="CharacterProfile.DeathHistory"/> list for review.
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
    [ObservableProperty] private string? _lastKiller;
    [ObservableProperty] private DateTimeOffset? _lastDeathAt;
    [ObservableProperty] private int _deathCount;
    [ObservableProperty] private IReadOnlyList<DeathRecord> _history = Array.Empty<DeathRecord>();

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

    /// <summary>Manually re-pull observables (e.g. after "Mark recovered").</summary>
    public void Refresh()
    {
        LivesRemaining = _recovery.LivesRemaining;
        LastKiller = _recovery.LastKiller;
        LastDeathAt = _recovery.LastDeathAt;
        DeathCount = _recovery.DeathCount;

        CharacterProfile? p = _profile.Current;
        History = p?.DeathHistory is { } list && list.Count > 0
            ? list.AsReadOnly()
            : Array.Empty<DeathRecord>();
    }

    private void OnRecoveryChanged(object? sender, PropertyChangedEventArgs _) => Refresh();
    private void OnProfileChanged(CharacterProfile _) => Refresh();
}
