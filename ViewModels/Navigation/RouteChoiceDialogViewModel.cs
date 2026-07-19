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
// pass. Clicking a route selects it and previews its line on the map (no walk
// yet); the Go button commits the selected route (the direct one arms the
// acquisition pipeline for the missing items). Cancel / X walks nothing.
public sealed partial class RouteChoiceDialogViewModel
    : ObservableObject, IDialogViewModel<RouteChoiceResult?>
{
    public event Action<RouteChoiceResult?>? CloseRequested;

    // Raised when the user selects a route to preview (before committing), so the
    // caller can draw that route's line on the map. Null clears the preview. The
    // picker never draws the map itself — it has no map knowledge; the prompt
    // maps the selected route to its FreePath / GatedPath and pushes it.
    public event Action<RouteChoiceResult?>? PreviewRequested;

    public string Heading { get; }
    public string FreeSummary { get; }
    public string GatedSummary { get; }
    public string RequirementSummary { get; }

    // False when there's no gate-free route — the direct (hazard-crossing) route
    // is the only way there. The Free card renders as a disabled "why you can't
    // just walk it" note; only the direct route is selectable.
    public bool HasFreeRoute { get; }

    // Which route the user has selected to preview. Null until they click one —
    // Go stays disabled until then, forcing the click-to-preview-then-Go flow.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFreeSelected))]
    [NotifyPropertyChangedFor(nameof(IsGatedSelected))]
    [NotifyCanExecuteChangedFor(nameof(GoCommand))]
    private RouteChoiceResult? _selectedRoute;

    public bool IsFreeSelected => SelectedRoute == RouteChoiceResult.Free;
    public bool IsGatedSelected => SelectedRoute == RouteChoiceResult.Gated;

    public RouteChoiceDialogViewModel(
        RouteChoice choice,
        string destinationLabel,
        Func<int, string?> itemName,
        Func<int, string?>? shopNameForItem = null,
        Func<int, string?>? dropNameForItem = null)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(itemName);

        HasFreeRoute = choice.HasFreeRoute;
        Heading = HasFreeRoute
            ? $"Two routes to {destinationLabel}"
            : $"Only route to {destinationLabel} crosses a hazard";
        FreeSummary = HasFreeRoute
            ? $"Free route — {Steps(choice.FreeStepCount)}, no items needed"
            : "No hazard-free route — every path there crosses a hazard you must counter";
        GatedSummary = HasFreeRoute
            ? $"Direct route — {Steps(choice.GatedStepCount)}"
            : $"Route — {Steps(choice.GatedStepCount)}";
        RequirementSummary = "Requires "
            + DescribeRequirements(choice.Requirements, itemName, shopNameForItem, dropNameForItem);
    }

    private static string Steps(int n) => n == 1 ? "1 step" : $"{n} steps";

    // "a raft (buy at General Store); the iron key; a waterskin (dropped by a
    // sand nomad)" — each requirement is one clause; a hazard's any-of counters
    // join with " or ". An Item / Ticket gate, or a SINGLE-counter hazard, whose
    // item the walk will auto-source gets a tail naming where: "(buy at <shop>)"
    // when a shop sells it, else "(dropped by <monster>)" when a flagged dropper
    // is reachable. Keys and any-of hazard counters never get a tail — a key
    // isn't sourced and an any-of hazard group posts no single auto-obtain
    // path-item need. Shop wins over drop when both resolve (a buy is cheap and
    // deterministic; the routers are mutually exclusive shop-first anyway).
    private static string DescribeRequirements(
        IReadOnlyList<RouteRequirement> reqs,
        Func<int, string?> itemName,
        Func<int, string?>? shopNameForItem,
        Func<int, string?>? dropNameForItem)
    {
        IEnumerable<string> clauses = reqs.Select(r =>
        {
            string items = string.Join(" or ", r.ItemIds.Select(id => itemName(id) ?? $"item #{id}"));
            bool autoSourced = r.Kind is RouteRequirementKind.CarryItem or RouteRequirementKind.Ticket
                || (r.Kind is RouteRequirementKind.HazardProtection && r.ItemIds.Count == 1);
            if (!autoSourced || r.ItemIds.Count != 1)
                return items;
            if (shopNameForItem?.Invoke(r.ItemIds[0]) is { Length: > 0 } shop)
                return $"{items} (buy at {shop})";
            if (dropNameForItem?.Invoke(r.ItemIds[0]) is { Length: > 0 } monster)
                return $"{items} (dropped by {monster})";
            return items;
        });
        return string.Join("; ", clauses);
    }

    [RelayCommand]
    private void SelectFree()
    {
        if (!HasFreeRoute) return;   // no gate-free route to pick
        SelectedRoute = RouteChoiceResult.Free;
        PreviewRequested?.Invoke(RouteChoiceResult.Free);
    }

    [RelayCommand]
    private void SelectGated()
    {
        SelectedRoute = RouteChoiceResult.Gated;
        PreviewRequested?.Invoke(RouteChoiceResult.Gated);
    }

    private bool CanGo => SelectedRoute is not null;

    [RelayCommand(CanExecute = nameof(CanGo))]
    private void Go() => CloseRequested?.Invoke(SelectedRoute);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}
