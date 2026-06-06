using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Navigation;

/// <summary>
/// Small modeless dialog for tweaking a single marked lair's respawn
/// timer override — opened from the CURRENT NAV row's ✎ button while
/// in Auto-Lair build mode (or during a Run). Save writes the value
/// through to <see cref="AutoLairManager.SetOverride"/>; the live
/// scheduler picks the change up on its next tick.
/// </summary>
/// <remarks>
/// Three exit paths:
/// <list type="bullet">
///   <item>Save with a non-zero value → SetOverride(key, seconds).</item>
///   <item>Save with zero (or empty) → SetOverride(key, null) — falls
///   back to the game-data default. Useful when the user previously
///   pushed an override and now wants to revert.</item>
///   <item>Cancel / X → no-op.</item>
/// </list>
/// The Result payload carries the committed value (or null on cancel)
/// so callers that care about the outcome can react; the dialog VM
/// itself does the SetOverride write so no caller-side wiring is
/// needed.
/// </remarks>
public sealed partial class LairTimerEditDialogViewModel : ObservableObject, IDialogViewModel<LairTimerEditDialogResult?>
{
    public event Action<LairTimerEditDialogResult?>? CloseRequested;

    private readonly AutoLairManager _mgr;
    private readonly RoomKey _key;

    public string RoomLabel { get; }

    /// <summary>
    /// Read-only context the dialog surfaces above the editor so the
    /// user can see what the current override is replacing — game
    /// default per <see cref="LairTimerStore.DefaultRespawnSeconds"/>,
    /// or "no game-data timer" when the room isn't tagged.
    /// </summary>
    public string DefaultHint { get; }

    /// <summary>
    /// Live override seconds. <c>0</c> means "no override"; Save
    /// clears the override and falls back to the game-data default.
    /// </summary>
    [ObservableProperty] private int _overrideSeconds;

    public LairTimerEditDialogViewModel(
        AutoLairManager manager,
        LairTimerStore timers,
        RoomKey key,
        string roomLabel)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(timers);
        _mgr = manager;
        _key = key;
        RoomLabel = roomLabel;

        _overrideSeconds = manager.GetOverride(key) ?? 0;
        int? def = timers.DefaultRespawnSeconds(key);
        DefaultHint = def is { } d ? $"game default {d}s" : "no game-data timer";
    }

    [RelayCommand]
    private void Save()
    {
        int? final = OverrideSeconds <= 0 ? null : OverrideSeconds;
        _mgr.SetOverride(_key, final);
        CloseRequested?.Invoke(new LairTimerEditDialogResult(_key, final));
    }

    [RelayCommand]
    private void ClearOverride()
    {
        OverrideSeconds = 0;
        _mgr.SetOverride(_key, null);
        CloseRequested?.Invoke(new LairTimerEditDialogResult(_key, null));
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}

/// <summary>Result payload — committed key + committed override (null = cleared / game-default).</summary>
public sealed record LairTimerEditDialogResult(RoomKey Key, int? OverrideSeconds);
