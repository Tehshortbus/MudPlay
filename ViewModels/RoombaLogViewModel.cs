using System;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Map;

namespace MudPlay.ViewModels;

// Read-only Roomba run log, opened from the Workshop Roomba tab's "Roomba Log"
// button. Surfaces the per-move record, the items left behind (with why), and an
// end-of-run summary (rooms sorted / items sorted / the explicit unmovable list).
// Refreshes live off GhSweepManager while a sweep runs.
public sealed partial class RoombaLogViewModel : ObservableObject, IDisposable
{
    private readonly GhSweepManager _sweep;

    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _movementLog = string.Empty;
    [ObservableProperty] private string _leftBehind = string.Empty;

    // Rooms that refused a drop this sweep, and the categories that ran out of
    // space because of it. These used to reach the program log only, which is the
    // wrong place for the one thing the user can actually act on.
    [ObservableProperty] private string _outOfSpace = string.Empty;

    // Drives the panel's warning border: amber only when a room actually ran out of
    // space, so a clean sweep's "(no room ran out of space)" doesn't sit in an
    // alarm-coloured box.
    [ObservableProperty] private bool _hasOutOfSpace;

    public RoombaLogViewModel(GhSweepManager sweep)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        _sweep = sweep;
        _sweep.PhaseChanged += Refresh;
        _sweep.SweepCompleted += OnCompleted;
        Refresh();
    }

    private void OnCompleted(GhSweepReport report) => Refresh();

    // GhSweepManager is UI-thread-confined, so its events fire on the UI thread —
    // setting the observables directly here is safe.
    private void Refresh()
    {
        MovementLog = _sweep.MovedSoFar.Count == 0
            ? (_sweep.Mode == GhSweepManager.SweepMode.InventoryOnly
                ? "(inventory-only run — nothing is ever moved)"
                : "(nothing moved yet)")
            : string.Join("\n", _sweep.MovedSoFar.Select(m => $"{m.Count}x {m.ItemName}: {m.From} → {m.To}"));

        LeftBehind = _sweep.LeftInPlace.Count == 0
            ? "(nothing left behind)"
            : string.Join("\n", _sweep.LeftInPlace.Select(f => $"{f.ItemName} at {f.Room} ({DescribeReason(f.Reason)})"));

        OutOfSpace = BuildOutOfSpace();
        HasOutOfSpace = _sweep.SaturatedGroups.Count > 0 || _sweep.FullRooms.Count > 0;

        int itemsSorted = _sweep.MovedSoFar.Sum(m => m.Count);
        int roomsSorted = _sweep.MovedSoFar.Select(m => m.From).Distinct().Count();
        var unmovable = _sweep.LeftInPlace.Where(f => f.Reason == GhLeftReason.TooHeavy).ToList();

        StringBuilder sb = new();
        if (_sweep.Mode == GhSweepManager.SweepMode.InventoryOnly)
            sb.AppendLine("Mode: inventory only — rooms observed and logged, nothing moved");
        sb.AppendLine($"Rooms sorted: {roomsSorted}");
        sb.AppendLine($"Items sorted: {itemsSorted}");
        if (unmovable.Count == 0)
        {
            sb.Append("Unmovable items: none");
        }
        else
        {
            sb.AppendLine($"Unmovable items ({unmovable.Count}) — too heavy to carry:");
            foreach (GhSweepItemFound f in unmovable) sb.AppendLine($"  • {f.ItemName} at {f.Room}");
        }
        if (_sweep.Stranded.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Still carried — sweep ended before these were dropped ({_sweep.Stranded.Count}):");
            foreach (GhSweepStranded s in _sweep.Stranded)
                sb.Append($"  • {s.ItemName} — meant for {s.IntendedDestination}\n");
        }
        Summary = sb.ToString().TrimEnd();
    }

    // Leads with what to do about it, then the evidence. A list of full rooms is
    // only useful next to the categories they've starved.
    private string BuildOutOfSpace()
    {
        var groups = _sweep.SaturatedGroups;
        var full = _sweep.FullRooms.OrderBy(r => r.Map).ThenBy(r => r.Room).ToList();
        if (groups.Count == 0 && full.Count == 0)
            return "(no room ran out of space)";

        StringBuilder sb = new();
        foreach (GhSaturatedGroup g in groups)
        {
            sb.AppendLine($"Label another room for these — every room that takes them is full "
                          + $"({string.Join(", ", g.Rooms)}):");
            foreach (string item in g.ItemNames.Take(12)) sb.AppendLine($"  • {item}");
            if (g.ItemNames.Count > 12) sb.AppendLine($"  • …and {g.ItemNames.Count - 12} more");
            sb.AppendLine();
        }

        if (full.Count > 0)
        {
            sb.AppendLine($"Rooms that refused a drop this sweep ({full.Count}):");
            sb.Append("  " + string.Join(", ", full));
        }
        return sb.ToString().TrimEnd();
    }

    // Every reason spelled out. A catch-all default would silently relabel each
    // new GhLeftReason as "no matching room" — which reads as "go label a room
    // for this", the one action that would not help when the real reason is a
    // full house or an item auto-discard is about to bin.
    private static string DescribeReason(GhLeftReason reason) => reason switch
    {
        GhLeftReason.TooHeavy => "too heavy to carry",
        GhLeftReason.GoneBySortTime => "gone by sort time",
        GhLeftReason.NotFoundBySearch => "hidden, and this sweep's search didn't find it — still there",
        GhLeftReason.RefusedByGame => "the game won't let it be picked up",
        GhLeftReason.OneAlreadyHeld => "only one can be held, and you already hold one",
        GhLeftReason.AllDestinationsFull => "every room that takes it is full",
        GhLeftReason.NotActuallyCarried => "not in inventory — the pickup never landed",
        GhLeftReason.CouldNotComplete => "couldn't be sorted this sweep — no room or headroom",
        GhLeftReason.Ignored => "on the ignore list",
        _ => "no matching room",
    };

    public void Dispose()
    {
        _sweep.PhaseChanged -= Refresh;
        _sweep.SweepCompleted -= OnCompleted;
    }
}
