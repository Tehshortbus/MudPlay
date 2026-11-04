using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Pure sort-queue decision logic, split out of GhSweepManager so it's
// testable without the engine's LoopRunner/RoomTracker/MessageRouter
// wiring. Takes exactly what recon observed (never anything carried) and
// what's labeled, and decides what moves where.
public static class GhSortQueueBuilder
{
    // Auto-get- and auto-discard-flagged items ARE sorted like any other: the
    // auto-discard engine is held off for the duration of a sweep (GhSweepManager.
    // IsActive gates it), so the old tug-of-war — collect, get binned, find it
    // again next lap, and worse, the binning leaving Roomba's ledger claiming a
    // held item it no longer has — can't happen while sorting. Roomba relocates
    // them to their labeled rooms; the player's auto-collect/discard resumes once
    // the sweep ends.
    public static (IReadOnlyList<GhPendingMove> Moves, IReadOnlyList<GhSweepItemFound> LeftInPlace) Build(
        IReadOnlyDictionary<RoomKey, IReadOnlyList<string>> observedByRoom,
        IReadOnlyCollection<GhRoomLabel> labels,
        ItemNameStore itemNames,
        IReadOnlyCollection<string>? ignored = null)
    {
        List<GhPendingMove> moves = new();
        List<GhSweepItemFound> leftInPlace = new();

        Dictionary<RoomKey, GhRoomLabel> labelsByRoom = new();
        foreach (GhRoomLabel l in labels) labelsByRoom[new RoomKey(l.Map, l.Room)] = l;

        // Nothing is known full at plan time — the sweep hasn't tried a drop yet.
        // GhSweepManager re-resolves through the same function with its live
        // full-room set when a destination refuses.
        HashSet<RoomKey> nothingFull = new();

        foreach ((RoomKey room, IReadOnlyList<string> items) in observedByRoom)
        {
            labelsByRoom.TryGetValue(room, out GhRoomLabel? currentLabel);

            foreach (string entry in items)
            {
                GhItemClass? cls = GhItemClassifier.Classify(itemNames, entry);
                if (cls is null) continue;   // unresolvable — shouldn't occur, cash is pre-filtered

                if (GhItemClassifier.IsGuardEmblem(entry))
                {
                    leftInPlace.Add(new GhSweepItemFound(room, entry, GhLeftReason.NoMatchingRoom));
                    continue;
                }

                if (currentLabel is not null
                    && GhItemClassifier.MatchesAny(currentLabel, cls.Value)) continue;   // already correct room

                (int count, string singularName) = CountedCommand.SplitLeadingCount(entry);
                string canonicalName = itemNames.FindByName(entry) is int number
                    ? itemNames.GetName(number) ?? singularName
                    : singularName;

                if (ignored is not null && ignored.Contains(canonicalName))
                {
                    leftInPlace.Add(new GhSweepItemFound(room, entry, GhLeftReason.Ignored));
                    continue;
                }

                // First matching label, else the catch-all, else nowhere. Already
                // sitting in the room it resolves to counts as correctly placed.
                if (GhDestinationResolver.Resolve(cls.Value, labels, nothingFull) is not { } dest)
                {
                    leftInPlace.Add(new GhSweepItemFound(room, entry, GhLeftReason.NoMatchingRoom));
                    continue;
                }
                if (dest.Equals(room)) continue;

                moves.Add(new GhPendingMove(room, dest, canonicalName, count));
            }
        }

        return (moves, leftInPlace);
    }
}
