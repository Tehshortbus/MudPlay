using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.GameData;

// One training-shop room discovered in the active set's Shops table. Number is
// Shops.Number; Name is the shop name (usually the trainer / role label, e.g.
// "Training Room"); Map / Room come from Assigned To (0 when unresolved);
// RoomName is the host room's display name from Rooms (empty when unresolved);
// MinLevel / MaxLevel are MinLVL / MaxLVL; ClassRest is the class restriction
// (0 = universal, like the Training Room); Markup is the Shops.Markup% that
// scales the per-level training fee.
public readonly record struct TrainerShop(
    int Number, string Name, int Map, int Room, string RoomName, int MinLevel, int MaxLevel, int ClassRest, int Markup)
{
    // True when a host room resolved from the shop's Assigned To.
    public bool HasRoom => Map > 0 && Room > 0;

    // Stable per-row identity (shop + host room) for the allow/disallow set. A
    // multi-room shop yields one row per room, so the disabled set keys on the
    // physical trainer location, not just the shop number — enabling Newhaven's
    // Training Room while disabling Silvermere's works because they're distinct.
    public string RowKey => string.Create(CultureInfo.InvariantCulture, $"{Number}/{Map}/{Room}");

    // True when this trainer serves a character at level, using MajorMUD's exact
    // gate: served when !(MinLVL > level+1 || MaxLVL <= level).
    public bool ServesLevel(int level) => !(MinLevel > level + 1 || MaxLevel <= level);

    // True when this trainer serves classNumber: the universal Training Room
    // (ClassRest == 0) serves every class; a class trainer serves only its own
    // class number.
    public bool ServesClass(int classNumber) => ClassRest == 0 || ClassRest == classNumber;
}

// Why a trainer that serves the level/class can't be used from here. Ordered so a
// ranking sorts usable trainers ahead of ruled-out ones.
public enum TrainerSkip { None, Unreachable, Disabled }

// One trainer as ranked by TrainerCatalog.RankCandidates: Distance is the hop count
// when usable, null when Skip says why it was ruled out.
public readonly record struct TrainerCandidate(TrainerShop Trainer, int? Distance, TrainerSkip Skip);

// Enumerates the training shops (ShopType == 8) in the active game-data set,
// resolving each one's host room(s) from Assigned To. A shop assigned to several
// rooms yields one TrainerShop per room (so the universal Training Room appears
// once for Silvermere and once for Newhaven). Trainers whose MaxLVL is the 999
// sentinel (e.g. the unreachable "Sysop Trainer", shop 39) are skipped, as are
// shops with no parseable room (nothing to route to).
//
// Drives the Settings → Auto-Trainer table (the discovered-trainers list) and the
// navigation engine's "which trainer for this level/class" resolution. Level
// ranges + rooms come straight from the data, so the in-game town progression
// (Newhaven → Silvermere → Aldreth → Aged Titan …) falls out without any
// hardcoding.
public static class TrainerCatalog
{
    // Shops.ShopType value for a training shop.
    public const int TrainingShopType = 8;

    // MaxLVL sentinel marking a non-reachable / placeholder trainer to ignore.
    public const int IgnoredMaxLevel = 999;

    // The list keeps each trainer's room name, a learned one included, so it is
    // dropped when those change (GameDataCache.LearnedRoomNamesChanged).
    public const string DerivedKey = "trainers";

    // Built once per game-data set (GameDataCache.Derived).
    public static IReadOnlyList<TrainerShop> Enumerate(GameDataCache gameData)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        return gameData.Derived(DerivedKey, () => Build(gameData));
    }

    private static List<TrainerShop> Build(GameDataCache gameData)
    {
        var trainers = new List<TrainerShop>();

        GameTable? doc = gameData.GetTable("Shops");
        if (doc is null) return trainers;

        foreach (GameRow el in doc.Rows)
        {
            if (GetInt(el, "ShopType") != TrainingShopType) continue;
            int maxLevel = GetInt(el, "MaxLVL");
            if (maxLevel == IgnoredMaxLevel) continue;

            int number = GetInt(el, "Number");
            string name = GetString(el, "Name");
            int minLevel = GetInt(el, "MinLVL");
            int classRest = GetInt(el, "ClassRest");
            int markup = GetInt(el, "Markup%");

            foreach ((int map, int room) in ShopRoomParser.ParseRooms(GetString(el, "Assigned To")))
            {
                string roomName = RoomNameIndex.NameOf(gameData, map, room);
                trainers.Add(new TrainerShop(number, name, map, room, roomName, minLevel, maxLevel, classRest, markup));
            }
        }
        return trainers;
    }

    // Pick the trainer that can serve the character, nearest or cheapest by `by`:
    // serves level, is universal or matches classNumber, isn't in disabled (keyed by
    // TrainerShop.RowKey), has a resolvable room, and is reachable per distance
    // (which returns the path length to a trainer's room, or null when
    // unreachable). Returns null when nothing qualifies — e.g. a quest-gated level
    // with no matching trainer. Pure: the caller supplies the distance metric (BFS).
    public static TrainerShop? SelectTrainer(
        IReadOnlyList<TrainerShop> trainers, int level, int classNumber,
        IReadOnlyCollection<string> disabled, Func<TrainerShop, int?> distance, TrainerChoice by) =>
        FirstUsable(RankCandidates(trainers, level, classNumber, disabled, distance, by));

    // Pick the nearest trainer for a `train stats` (CP allocation) action. Unlike a
    // level-up `train`, applying stat points is NOT level-band gated — any trainer that
    // isn't class-restricted against you works, at any level (confirmed mechanic, see
    // GAME_MECHANICS). So this drops the ServesLevel filter and keeps everything else:
    // class match, resolvable room, not-disabled, reachable. Standing IN a class-valid
    // trainer yields distance 0, so it's selected and CP applies in place — no walk.
    public static TrainerShop? SelectNearestForStats(
        IReadOnlyList<TrainerShop> trainers, int classNumber,
        IReadOnlyCollection<string> disabled, Func<TrainerShop, int?> distance) =>
        FirstUsable(RankCandidates(trainers, level: null, classNumber, disabled, distance, TrainerChoice.Closest));

    // Every trainer that could serve this level (null = any level, the `train stats`
    // case) and class, ranked the way the selectors choose: usable ones first, in
    // the order `by` asks for (see Prefer), then the ruled-out ones with the reason.
    // The selectors take the first usable entry, and the auto-trainer logs the same
    // list, so a report shows exactly why one trainer beat another instead of just
    // which one won.
    public static IReadOnlyList<TrainerCandidate> RankCandidates(
        IReadOnlyList<TrainerShop> trainers, int? level, int classNumber,
        IReadOnlyCollection<string> disabled, Func<TrainerShop, int?> distance, TrainerChoice by)
    {
        ArgumentNullException.ThrowIfNull(trainers);
        ArgumentNullException.ThrowIfNull(disabled);
        ArgumentNullException.ThrowIfNull(distance);

        var candidates = new List<TrainerCandidate>();
        foreach (TrainerShop t in trainers)
        {
            if (!t.HasRoom) continue;
            if (level is { } lvl && !t.ServesLevel(lvl)) continue;
            if (!t.ServesClass(classNumber)) continue;
            if (disabled.Contains(t.RowKey))
                candidates.Add(new(t, null, TrainerSkip.Disabled));
            else if (distance(t) is { } dist)
                candidates.Add(new(t, dist, TrainerSkip.None));
            else
                candidates.Add(new(t, null, TrainerSkip.Unreachable));
        }

        candidates.Sort((a, b) =>
        {
            int c = a.Skip.CompareTo(b.Skip);
            if (c != 0) return c;
            c = Prefer(a.Trainer, a.Distance ?? int.MaxValue, b.Trainer, b.Distance ?? int.MaxValue, by);
            return c != 0 ? c : string.CompareOrdinal(a.Trainer.RowKey, b.Trainer.RowKey);
        });
        return candidates;
    }

    // Which of two trainers comes first (negative: a): the nearer, or for Cheapest the
    // lower markup (the fee scales with it, so it's the cheaper lesson at any level),
    // the other measure breaking a tie.
    public static int Prefer(TrainerShop a, int distanceA, TrainerShop b, int distanceB, TrainerChoice by)
    {
        int byDistance = distanceA.CompareTo(distanceB);
        int byMarkup = a.Markup.CompareTo(b.Markup);
        return by == TrainerChoice.Cheapest
            ? byMarkup != 0 ? byMarkup : byDistance
            : byDistance != 0 ? byDistance : byMarkup;
    }

    // One-line rendering of a ranking for the program log and the bug report, e.g.
    // "Training Room 1/1376 (25 steps, markup 100%), Gypsy Training Room 1/384
    // (30 steps, markup 90%), Training Room 1/2147 (disabled)".
    public static string DescribeCandidates(IReadOnlyList<TrainerCandidate> ranked)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        if (ranked.Count == 0) return "(no trainer serves this level/class)";
        var parts = new List<string>(ranked.Count);
        foreach (TrainerCandidate c in ranked)
        {
            string why = c.Skip switch
            {
                TrainerSkip.Disabled => "disabled",
                TrainerSkip.Unreachable => "no path",
                _ => (c.Distance == 1 ? "1 step" : $"{c.Distance} steps") + $", markup {c.Trainer.Markup}%",
            };
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"{c.Trainer.Name} {c.Trainer.Map}/{c.Trainer.Room} ({why})"));
        }
        return string.Join(", ", parts);
    }

    public static TrainerShop? FirstUsable(IReadOnlyList<TrainerCandidate> ranked) =>
        ranked.Count > 0 && ranked[0].Skip == TrainerSkip.None ? ranked[0].Trainer : null;

    // Cheapest markup among trainers that can teach a character of classNumber up
    // to targetLevel (MinLVL <= targetLevel <= MaxLVL, class-ok). Training cost
    // rises monotonically with markup for a fixed level, so the lowest-markup
    // trainer is always the cheapest — the caller feeds this into
    // ShopPriceCalculator.TrainCopper. Ignores room reachability (cost is
    // location-independent) and the auto-trainer disabled set (this is a price
    // projection, not a walk target). Null when no trainer serves that
    // level/class — e.g. a quest-gated level or a class with no super trainer.
    public static int? CheapestMarkup(IReadOnlyList<TrainerShop> trainers, int targetLevel, int classNumber)
    {
        ArgumentNullException.ThrowIfNull(trainers);

        int? cheapest = null;
        foreach (TrainerShop t in trainers)
        {
            if (!t.ServesLevel(targetLevel - 1)) continue;
            if (!t.ServesClass(classNumber)) continue;
            if (cheapest is null || t.Markup < cheapest) cheapest = t.Markup;
        }
        return cheapest;
    }

    private static int GetInt(GameRow el, string prop) =>
        el.TryGetProperty(prop, out GameValue v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)
            ? n : 0;

    private static string GetString(GameRow el, string prop) =>
        el.TryGetProperty(prop, out GameValue v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;
}
