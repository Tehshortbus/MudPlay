using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Services;

namespace FujinTerm.ViewModels;

/// <summary>
/// Modeless dialog opened when the LogPane user double-clicks a row
/// whose <see cref="LogEntry.Source"/> is <c>"RoomClassifier"</c> and
/// whose <see cref="LogEntry.Context"/> carries the raw "Also Here:"
/// wire line. Surfaces the offending entry name + the parsed list and
/// lets the user pick a fix action.
/// </summary>
/// <remarks>
/// <para>
/// PR 9.0a sub-G ships the skeleton — fields, layout, and the two
/// outbound actions (Add-as-flavor-prefix / Add-as-player-observation).
/// PR 9.0b's <see cref="Game.Combat.RoomEntityClassifier"/> opens the
/// dialog when it emits the underlying Warn row, and wires the chosen
/// action to either <see cref="Game.GameData.MonsterMessageStore"/>
/// (for flavor prefixes) or <see cref="Game.GameData.PlayerDatabase"/>
/// (for player observations) at the active 4-tier scope. The dialog
/// itself doesn't touch the data layer — it only collects the user's
/// intent, returns it via <see cref="CloseRequested"/>, and lets the
/// caller commit.
/// </para>
/// <para>
/// Result semantics: <see cref="UnknownEntityFixAction.AddFlavorPrefix"/>
/// returns the dialog's <see cref="UnknownEntity"/> as the prefix
/// candidate; the caller chooses the target monster (via the typed
/// MonsterMessageStore lookup the classifier already has).
/// <see cref="UnknownEntityFixAction.AddPlayerObservation"/> returns
/// the same string as the player name. Cancel returns null.
/// </para>
/// </remarks>
public sealed partial class UnknownEntityFixDialogViewModel : ObservableObject,
    IDialogViewModel<UnknownEntityFixResult?>
{
    public event Action<UnknownEntityFixResult?>? CloseRequested;

    public UnknownEntityFixDialogViewModel(string rawAlsoHereLine, string unknownEntity)
    {
        RawAlsoHereLine = rawAlsoHereLine ?? string.Empty;
        UnknownEntity   = unknownEntity   ?? string.Empty;
    }

    /// <summary>The raw "Also Here:" line from the wire, copied verbatim
    /// for the user to inspect.</summary>
    public string RawAlsoHereLine { get; }

    /// <summary>The single comma-separated name segment the classifier
    /// could not resolve to a Monster or a Player.</summary>
    public string UnknownEntity { get; }

    /// <summary>
    /// Help text shown above the action buttons explaining what each
    /// action does. Inline rather than per-button tooltips so the user
    /// sees both options before picking.
    /// </summary>
    public string GuidanceText =>
        "If the name is a monster the classifier hasn't seen yet, add it as a flavor " +
        "prefix to an existing monster (e.g. \"stinking\" → giant rat). If it's a player " +
        "the database hasn't observed via `who`, add a placeholder observation now and " +
        "let the next `who` enrich it. Cancel leaves nothing changed.";

    [RelayCommand]
    private void AddFlavorPrefix() =>
        CloseRequested?.Invoke(new UnknownEntityFixResult(
            UnknownEntityFixAction.AddFlavorPrefix, UnknownEntity));

    [RelayCommand]
    private void AddPlayerObservation() =>
        CloseRequested?.Invoke(new UnknownEntityFixResult(
            UnknownEntityFixAction.AddPlayerObservation, UnknownEntity));

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}

/// <summary>Outcome of the user's pick in
/// <see cref="UnknownEntityFixDialogViewModel"/>.</summary>
public enum UnknownEntityFixAction
{
    /// <summary>Treat the unknown name as a flavor prefix to attach to an
    /// existing monster. The caller picks which monster.</summary>
    AddFlavorPrefix,

    /// <summary>Treat the unknown name as a player and add a placeholder
    /// observation to the per-BBS player database.</summary>
    AddPlayerObservation,
}

/// <summary>Payload returned via
/// <see cref="UnknownEntityFixDialogViewModel.CloseRequested"/> on a
/// committed pick. Null result = Cancel.</summary>
/// <param name="Action">Which fix the user picked.</param>
/// <param name="EntityName">The unknown name from the Also-Here line —
/// stored so the caller doesn't have to thread it through state.</param>
public sealed record UnknownEntityFixResult(
    UnknownEntityFixAction Action,
    string EntityName);
