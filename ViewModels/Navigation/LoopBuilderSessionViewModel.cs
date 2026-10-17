using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.ViewModels.Guides;

namespace MudPlay.ViewModels.Navigation;

// Ephemeral session for the Navigation window's Loop mode. Tracks the user's
// clicked rooms and uses LoopManager.ExpandWaypoints to BFS-gap-fill the path
// between them so the saved loop is ready to run. On Save the click list
// becomes the loop's Waypoints (each click → one LoopWaypoint). Per-waypoint
// commands + delays can be attached while building — click a row to open
// WaypointActionEditDialog (SetClickAction) — or later in the loop editor.
public sealed partial class LoopBuilderSessionViewModel : ObservableObject
{
    private readonly LoopManager _loops;
    private readonly RoomGraphManager _graph;
    private readonly IRoomFilter? _filter;
    private readonly List<RoomKey> _clicks = new();
    // The game-data set the rooms clicked are of: the one the loop catalogue was on
    // when they were clicked, which is where Save writes.
    private string? _builtOn;

    public LoopBuilderSessionViewModel(LoopManager loops, RoomGraphManager graph, IRoomFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(graph);
        _loops = loops;
        _graph = graph;
        _filter = filter;
        _builtOn = loops.SetName;
        ProposedName = $"Loop {DateTime.Now:HH-mm}";
    }

    // The active game data changed. Under another set the rooms clicked are other
    // rooms, and Save would write them into that set's Loops folder, a file every
    // character and every running client on that set reads: the build is dropped,
    // and the session carries on, empty, on the new set. The same set read in again
    // keeps it. True when a build with rooms in it was dropped; builtOn is then the
    // set it belonged to.
    public bool DropIfBuiltOnOtherGameData(out string? builtOn)
    {
        builtOn = _builtOn;
        if (string.Equals(_builtOn, _loops.SetName, StringComparison.OrdinalIgnoreCase)) return false;
        bool had = HasClicks;
        _builtOn = _loops.SetName;
        Clear();
        // The name and notes were the dropped loop's.
        Notes = string.Empty;
        ProposedName = $"Loop {DateTime.Now:HH-mm}";
        return had;
    }

    // Click sequence rendered in the bottom strip — read-only externally.
    public ObservableCollection<LoopBuilderRow> Clicks { get; } = new();

    [ObservableProperty] private string _proposedName = "";
    [ObservableProperty] private string _notes = string.Empty;

    // Loop-wide "only attack in lair rooms" (an Entire Loop Setting), set from
    // the build-time ⚙ Entire Loop Settings flyout. Carried onto the loop on
    // Save/BuildTransient. Loop-wide, not per-waypoint. This and the lair wait
    // below are the building character's: Save keeps them for that character
    // (LoopManager.Save), and the loop file it writes holds neither.
    [ObservableProperty] private bool _onlyAttackInLairRooms;

    // Entire Loop Settings: wait one step short of a lair until the debuff can be
    // cast, and how (Game.Map.LairEntryDebuffModes: 0 wait for spells, 1 block them,
    // 2 wait for the priority buffs only).
    [ObservableProperty] private bool _waitToDebuffBeforeLairs;
    [ObservableProperty] private int _lairDebuffWaitChoice;

    [ObservableProperty] private int _expandedStepCount;
    [ObservableProperty] private string _unreachableSummary = string.Empty;

    // Flattened RoomKey sequence for the map's loop-builder polyline: every
    // click + every BFS-filled intermediate room + the closing leg back to
    // click 0. Null when fewer than two clicks. Refreshed alongside
    // ExpandedStepCount on every click change.
    [ObservableProperty] private IReadOnlyList<RoomKey>? _previewedRoomKeys;

    // Just the clicks the user laid down, in order (the map numbers these).
    // Distinct from PreviewedRoomKeys which
    // carries every BFS-filled intermediate.
    [ObservableProperty] private IReadOnlyList<RoomKey>? _waypointKeys;

    public bool HasClicks => Clicks.Count > 0;
    public bool CanSave   => Clicks.Count >= 2 && string.IsNullOrEmpty(UnreachableSummary);

    public void AddClick(RoomKey key)
    {
        if (_graph.GetRoom(key) is not { } room) return;
        // The same room twice in a row is allowed on purpose: it's a zero-length
        // "stay put" step that runs another command in place (e.g. two barmaid
        // steps — hand in pies, then convert the coin). The expander treats it as
        // 0 moves rather than an unreachable leg, so it saves + runs cleanly.
        _clicks.Add(key);
        // 1-based index matches the map's red numbered waypoint
        // markers + the post-edit RenumberRows pass. Clicks.Count is
        // pre-Add, so the first row gets 1, not 0.
        Clicks.Add(new LoopBuilderRow(Clicks.Count + 1, key, room.DisplayName));
        OnPropertyChanged(nameof(HasClicks));
        Reexpand();
    }

    // Remove the click at index (CURRENT NAV row click → remove). No-op when
    // index is out of range.
    public void RemoveClickAt(int index)
    {
        if (index < 0 || index >= _clicks.Count) return;
        _clicks.RemoveAt(index);
        Clicks.RemoveAt(index);
        RenumberRows();
        OnPropertyChanged(nameof(HasClicks));
        Reexpand();
    }

    // Alt+click on the map: take the room back out of the loop. A loop can pass
    // through a room more than once, so it's the most recent click on it that goes.
    // False when the room isn't in the loop.
    public bool RemoveLastClickOf(RoomKey key)
    {
        int index = _clicks.LastIndexOf(key);
        if (index < 0) return false;
        RemoveClickAt(index);
        return true;
    }

    // Map drag: move the room's most recent waypoint to another room, keeping its
    // number, command, delay and flags. A move that leaves the loop with no route
    // is undone. False when nothing moved.
    public bool MoveLastClickOf(RoomKey from, RoomKey to)
    {
        int index = _clicks.LastIndexOf(from);
        if (index < 0 || from.Equals(to) || _graph.GetRoom(to) is not { } room) return false;
        bool wasRoutable = string.IsNullOrEmpty(UnreachableSummary);
        LoopBuilderRow old = Clicks[index];
        _clicks[index] = to;
        Clicks[index] = old with { Key = to, Name = room.DisplayName };
        Reexpand();
        if (wasRoutable && !string.IsNullOrEmpty(UnreachableSummary))
        {
            _clicks[index] = from;
            Clicks[index] = old;
            Reexpand();
            return false;
        }
        return true;
    }

    // Attach (or clear) a per-waypoint command + delay on the click at index —
    // driven by clicking the row in the loop-builder strip, which opens
    // WaypointActionEditDialog. Commands don't change the
    // gap-filled path, so no re-expand. No-op when index is out of range.
    public void SetClickAction(int index, string? command, long delayMs, bool doNotRest = false, bool doNotAttack = false,
        bool restHereHp = false, bool restHereMana = false)
    {
        if (index < 0 || index >= Clicks.Count) return;
        string? cmd = string.IsNullOrWhiteSpace(command) ? null : command;
        Clicks[index] = Clicks[index] with
        {
            Command = cmd,
            DelayMs = cmd is null ? 0 : Math.Max(0, delayMs),
            DoNotRest = doNotRest,
            DoNotAttack = doNotAttack,
            RestHereHp = restHereHp,
            RestHereMana = restHereMana,
        };
    }

    // Move the click at fromIndex to toIndex (CURRENT NAV drag-reorder).
    // No-op when indices match or either is out of range.
    public void MoveClick(int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex) return;
        if (fromIndex < 0 || fromIndex >= _clicks.Count) return;
        if (toIndex   < 0 || toIndex   >= _clicks.Count) return;

        RoomKey key = _clicks[fromIndex];
        _clicks.RemoveAt(fromIndex);
        _clicks.Insert(toIndex, key);
        Clicks.Move(fromIndex, toIndex);
        RenumberRows();
        Reexpand();
    }

    private void RenumberRows()
    {
        for (int i = 0; i < Clicks.Count; i++)
        {
            Clicks[i] = Clicks[i] with { Index = i + 1 };
        }
    }

    public void Clear()
    {
        _clicks.Clear();
        Clicks.Clear();
        ExpandedStepCount = 0;
        UnreachableSummary = string.Empty;
        PreviewedRoomKeys = null;
        WaypointKeys = null;
        OnlyAttackInLairRooms = false;
        WaitToDebuffBeforeLairs = false;
        LairDebuffWaitChoice = LairEntryDebuffModes.WaitForSpellsChoice;
        OnPropertyChanged(nameof(HasClicks));
        OnPropertyChanged(nameof(CanSave));
    }

    // Persist the current loop under ProposedName. Returns the saved Loop, or
    // null when the session isn't ready to save.
    public Loop? Save()
    {
        if (!CanSave) return null;
        Loop? loop = BuildTransient();
        if (loop is null) return null;
        _loops.Save(loop);
        GuideActions.Report(GuideActions.LoopSaved);
        Clear();
        return loop;
    }

    // Build a Loop from the current clicks WITHOUT writing it to disk. Used
    // by the Run button so transient "try this loop" runs don't pollute the
    // saved-loops list — the user persists explicitly via the Manage dialog.
    // Returns null when the session isn't ready (fewer than 2 clicks,
    // gap-fill unreachable). The build session is left intact so the user can
    // still Save it from Manage later.
    public Loop? BuildTransient()
    {
        if (!CanSave) return null;
        if (_clicks.Count < 2) return null;

        // Carry any per-waypoint command + delay the user attached while building
        // (click a row → WaypointActionEditDialog); rows with no action stay a bare
        // move. Iterate Clicks (which holds the actions), parallel to _clicks.
        var waypoints = new List<LoopWaypoint>(Clicks.Count);
        foreach (LoopBuilderRow row in Clicks)
            waypoints.Add(new LoopWaypoint(row.Key, row.Command, row.DelayMs, row.DoNotRest, row.DoNotAttack)
            {
                RestHereHp = row.RestHereHp,
                RestHereMana = row.RestHereMana,
            });

        return new Loop(ProposedName, waypoints)
        {
            Notes = Notes ?? string.Empty,
            OnlyAttackInLairRooms = OnlyAttackInLairRooms,
            LairEntryDebuff = LairEntryDebuffModes.From(WaitToDebuffBeforeLairs, LairDebuffWaitChoice),
        };
    }

    private void Reexpand()
    {
        // Always refresh the waypoint marker list so the map's numbered
        // pins update on every click, even for the < 2 click case (one
        // marker still draws when the user has placed one room).
        WaypointKeys = _clicks.Count == 0 ? null : new List<RoomKey>(_clicks);

        if (_clicks.Count < 2)
        {
            ExpandedStepCount = 0;
            UnreachableSummary = string.Empty;
            PreviewedRoomKeys = null;
            OnPropertyChanged(nameof(CanSave));
            return;
        }
        var waypoints = new List<LoopWaypoint>(_clicks.Count);
        foreach (RoomKey k in _clicks) waypoints.Add(new LoopWaypoint(k));
        (var steps, var unreachable) = _loops.ExpandWaypoints(waypoints, _filter);
        ExpandedStepCount = steps.Count;
        UnreachableSummary = unreachable.Count == 0
            ? string.Empty
            : $"{unreachable.Count} unreachable segment(s)";
        PreviewedRoomKeys = unreachable.Count == 0
            ? BuildRoomKeySequence(_clicks[0], steps)
            : null;     // hide preview when the cycle has gaps
        OnPropertyChanged(nameof(CanSave));
    }

    // Walk the expanded direction list from start and accumulate every room
    // the cycle visits. Returns null if any intermediate exit doesn't resolve
    // (graph mutation between expansion and walking — shouldn't happen in
    // normal use but the null guard keeps the renderer from drawing a partial
    // polyline).
    private IReadOnlyList<RoomKey>? BuildRoomKeySequence(RoomKey start, IReadOnlyList<LoopStep> steps)
    {
        var sequence = new List<RoomKey>(steps.Count + 1) { start };
        RoomKey cursor = start;
        foreach (LoopStep step in steps)
        {
            if (step is not MoveLoopStep move) continue;
            if (_graph.GetRoom(cursor) is not { } room) return null;
            if (!room.Exits.TryGetValue(move.Direction, out RoomExit exit)) return null;
            cursor = exit.Target;
            sequence.Add(cursor);
        }
        return sequence;
    }
}

// Single click row shown in the bottom strip — index + room label, plus an
// optional per-waypoint command + delay the user attaches by clicking the row
// (same as the loop editor's per-waypoint action). Carried into the saved loop's
// LoopWaypoint on Save/BuildTransient. HasCommand drives the row's "has an action"
// marker.
public sealed record LoopBuilderRow(
    int Index, RoomKey Key, string Name, string? Command = null, long DelayMs = 0, bool DoNotRest = false, bool DoNotAttack = false,
    bool RestHereHp = false, bool RestHereMana = false)
{
    public bool HasCommand => !string.IsNullOrWhiteSpace(Command);
    public bool HasDoNotRest => DoNotRest;
    public bool HasDoNotAttack => DoNotAttack;
    public bool HasRestHere => RestHereHp || RestHereMana;
    public string RestHereTip => RestHereHp && RestHereMana ? "Rest up here (HP and mana)"
        : RestHereHp ? "Rest up here (HP)" : "Rest up here (mana)";
}
