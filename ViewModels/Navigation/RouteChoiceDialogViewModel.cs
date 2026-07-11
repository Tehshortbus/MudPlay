using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Navigation;

// Which route the user picked in the RouteChoiceDialog. Cancel returns null
// (walk nothing) rather than a member of this enum.
public enum RouteChoiceResult
{
    Free,    // the longer gate-free route
    Gated,   // the shorter route through the acquirable gate(s)
}

// Free-vs-direct route picker. Shown only when RouteChoicePlanner found a direct
// route that saves steps but crosses an acquirable gate the crosser can't yet
// pass. The user chooses the safe/free detour or the shorter route (which arms
// the acquisition pipeline for the missing items). Cancel walks nothing.
public sealed partial class RouteChoiceDialogViewModel
    : ObservableObject, IDialogViewModel<RouteChoiceResult?>
{
    public event Action<RouteChoiceResult?>? CloseRequested;

    public string Heading { get; }
    public string FreeSummary { get; }
    public string GatedSummary { get; }
    public string RequirementSummary { get; }

    public RouteChoiceDialogViewModel(
        RouteChoice choice,
        string destinationLabel,
        Func<int, string?> itemName)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(itemName);

        Heading = $"Two routes to {destinationLabel}";
        FreeSummary = $"Free route — {Steps(choice.FreeStepCount)}, no items needed";
        GatedSummary = $"Direct route — {Steps(choice.GatedStepCount)}";
        RequirementSummary = "Requires " + DescribeRequirements(choice.Requirements, itemName);
    }

    private static string Steps(int n) => n == 1 ? "1 step" : $"{n} steps";

    // "a raft; the iron key; a gnomish fish-helm or a waterskin" — each
    // requirement is one clause; a hazard's any-of counters join with " or ".
    private static string DescribeRequirements(
        IReadOnlyList<RouteRequirement> reqs, Func<int, string?> itemName)
    {
        IEnumerable<string> clauses = reqs.Select(r =>
            string.Join(" or ", r.ItemIds.Select(id => itemName(id) ?? $"item #{id}")));
        return string.Join("; ", clauses);
    }

    [RelayCommand]
    private void TakeFree() => CloseRequested?.Invoke(RouteChoiceResult.Free);

    [RelayCommand]
    private void TakeGated() => CloseRequested?.Invoke(RouteChoiceResult.Gated);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}
