using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Game.Quests;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.ViewModels.Navigation;

// Builds + opens the read-only "Details…" browse window for a route polyline,
// resolving every per-room link (monsters, hazard, item gate) + the room-click
// map highlight from AppServices. Shared by the Navigation window's CURRENT-NAV
// Details button and the route picker's Details button so both show the same
// window and rows.
public static class RouteDetailsLauncher
{
    // The RouteDetailRow list for a route's room-key polyline (source-first). Empty
    // when the polyline is trivial. Each monster link carries its live Hits-You-% so
    // the window can colour names by danger without recomputing.
    public static IReadOnlyList<RouteDetailRow> BuildRows(AppServices services, IReadOnlyList<RoomKey>? polyline)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (polyline is not { Count: > 1 }) return Array.Empty<RouteDetailRow>();
        PlayerDefenseProfile? def = LiveDefense(services);
        return CurrentRouteDetails.Build(
            services.RoomGraph, services.Bfs, services.Movement,
            polyline, services.ItemNames.GetName,
            key => MonsterLinks(services, key, def),
            services.HighlightWhereRoom,
            key => RoomHazard(services, key),
            id => ItemLink(services, id));
    }

    // The fully-wired browse VM for a polyline: rows + the persisted hit-% colour
    // state + a Global-tier persist callback. The single construction path so both
    // openers (nav header + route picker) get identical settings + persistence.
    public static RouteDetailsDialogViewModel BuildViewModel(
        AppServices services, string title, IReadOnlyList<RoomKey>? polyline)
    {
        ArgumentNullException.ThrowIfNull(services);
        MonsterHitColorSettings colors =
            services.Profile.Current?.MonsterHitColors ?? new MonsterHitColorSettings();
        return new RouteDetailsDialogViewModel(
            TitleWithEta(services, title, polyline),
            BuildRows(services, polyline),
            colors.Enabled, colors.GreenMax, colors.YellowMax,
            (enabled, greenMax, yellowMax) =>
            {
                // Per-character: saved on the loaded profile so each character keeps
                // its own toggle + band split and the window opens the way that
                // character last left it. A plain profile Save is quiet (no
                // ProfileLoaded / ProfileMutated fan-out).
                if (services.Profile.Current is not { } profile) return;
                profile.MonsterHitColors = new MonsterHitColorSettings
                {
                    Enabled = enabled, GreenMax = greenMax, YellowMax = yellowMax,
                };
                services.Profile.Save();
            });
    }

    // Open the browse window for a polyline (modeless, fire-and-forget). Returns the
    // VM so a caller can toggle it closed (RequestClose) on a re-press.
    public static RouteDetailsDialogViewModel Open(
        AppServices services, string title, IReadOnlyList<RoomKey>? polyline)
    {
        ArgumentNullException.ThrowIfNull(services);
        var vm = BuildViewModel(services, title, polyline);
        _ = services.Dialogs.OpenWindowAsync<RouteDetailsDialogViewModel, bool?>(vm);
        return vm;
    }

    // Append the approximate arrival ETA for a route's polyline to a title — the same
    // realm-aware per-hop travel + lair-fight dwell estimate the picker cards and the
    // walk-status line show. No suffix for a trivial route or when the estimate is 0.
    public static string TitleWithEta(
        AppServices services, string title, IReadOnlyList<RoomKey>? polyline)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (polyline is not { Count: > 1 }) return title;
        TimeSpan eta = RouteEtaEstimator.Estimate(
            polyline, services.AutoLair.TravelCostModel, services.RoomGraph.GetRoom,
            includeLairDwell: services.IsAutoCombatEnabled);
        return eta > TimeSpan.Zero ? $"{title}  ·  ~{RouteEtaEstimator.FormatCompact(eta)}" : title;
    }

    // The player's live defence for Hits-You-% colouring, or null when there's no
    // character context yet (no stat capture) — callers then leave monsters on their
    // alignment tint. Assembled from the live loadout by the shared IncomingHitEstimator.
    private static PlayerDefenseProfile? LiveDefense(AppServices services)
    {
        PlayerStats stats = services.PlayerStats;
        if (stats is null || stats.Level <= 0) return null;
        var snapshot = services.Inventory.Snapshot;
        var profile = services.Profile.Current;
        int? classId = CompletedQuestBonuses.ResolveClassId(services.GameData, stats.Class);
        IReadOnlyList<QuestBonus> quests =
            CompletedQuestBonuses.Resolve(services.GameData, classId, profile?.QuestLog);
        return IncomingHitEstimator.BuildLiveDefense(
            stats, snapshot.EquippedItems, snapshot.Encumbrance, services.GameData,
            profile?.PartyBuffs, services.Spellbook?.Available, quests);
    }

    // Monster-name tints by MajorMUD alignment code (Monsters-table Align): the
    // town-guard white the game itself shows for a Lawful-Good NPC, a dark cyan for
    // Neutral, combat-red for anything evil — mirroring what the terminal renders.
    private static readonly IBrush AlignEvilBrush = new SolidColorBrush(Color.Parse("#E06060"));   // AccentRed
    private static readonly IBrush AlignNeutralBrush = new SolidColorBrush(Color.Parse("#3E9AA6")); // dark cyan
    private static readonly IBrush AlignGoodBrush = new SolidColorBrush(Color.Parse("#FFFFFF"));    // bright white

    // Align → tint, using the game's own alignment codes (0 Good, 1 Evil, 2 Chaotic
    // Evil, 3 Neutral, 4 Lawful Good, 5 Neutral Evil, 6 Lawful Evil). Evil is the
    // {1,2,5,6} set and Good the {0,4} set, matching MonsterMatchupCalculator; a
    // Neutral or unknown alignment falls to the neutral cyan.
    private static IBrush MonsterAlignBrush(int? align) => align switch
    {
        1 or 2 or 5 or 6 => AlignEvilBrush,
        0 or 4 => AlignGoodBrush,
        _ => AlignNeutralBrush,
    };

    // A room's placed + lair monsters (deduped by id), each opening its record. Tinted
    // by alignment by default, and carrying its live Hits-You-% (when a character is
    // loaded) so the window can recolour by danger.
    private static IReadOnlyList<RoomDetailLink> MonsterLinks(
        AppServices services, RoomKey key, PlayerDefenseProfile? def)
    {
        Room? room = services.RoomGraph.GetRoom(key);
        if (room is null) return Array.Empty<RoomDetailLink>();
        RoomTooltipBuilder.RoomMonsters rm =
            RoomTooltipBuilder.ResolveRoomMonsters(room, services.GameData, services.MonsterSpawns);

        var links = new List<RoomDetailLink>(rm.Placed.Count + rm.Lair.Count);
        var seen = new HashSet<int>();
        foreach (RoomTooltipBuilder.RoomMonsterRef m in rm.Placed.Concat(rm.Lair))
        {
            if (!seen.Add(m.Id)) continue;
            MonsterCatalogEntry? entry = services.MonsterCatalog.Get(m.Id);
            IBrush tint = MonsterAlignBrush(entry?.Align);
            int? hitYou = def is { } profile && entry is not null
                ? IncomingHitEstimator.WeightedHitPercent(entry, profile, services.GameData.ActiveRealm)
                : null;
            // A see-hidden monster (SeeHidden ability) defeats sneak — the template
            // flanks its name with an eyeball marker so you know it'll spot you coming.
            bool seesHidden = services.SeeHidden is { } seeHidden && seeHidden.Has(m.Id);
            links.Add(new RoomDetailLink($"{m.Name}(#{m.Id})", null,
                new AsyncRelayCommand(() => services.OpenMonsterRecordAsync(m.Id)))
            {
                Accent = tint,
                AlignAccent = tint,
                HitPercent = hitYou,
                SeesHidden = seesHidden,
            });
        }
        return links;
    }

    // A room's protectable cast-on-enter hazard (RoomHazardIndex) — the harmful
    // spell + its counter items. Null for a room with no room-entry hazard (an
    // item-gated exit off it is folded in by CurrentRouteDetails.Build).
    private static RouteStepWarning? RoomHazard(AppServices services, RoomKey key)
    {
        Room? room = services.RoomGraph.GetRoom(key);
        if (room is null || room.Spell <= 0) return null;
        if (services.RoomHazards.HazardForSpell(room.Spell) is not { } hz) return null;

        string spellName = services.GameData.FindNameByNumber("Spells", room.Spell)
            ?? $"spell #{room.Spell}";
        var spellLink = new RoomDetailLink(spellName, null,
            new AsyncRelayCommand(() => services.OpenSpellRecordAsync(room.Spell)));

        var counters = new List<RoomDetailLink>(hz.ProtectingItems.Count);
        foreach (int itemId in hz.ProtectingItems)
            counters.Add(ItemLink(services, itemId));
        return new RouteStepWarning(spellLink, counters);
    }

    // An item id → a clickable link to its Game Data record (hazard counters +
    // item-gated-exit requirements alike).
    private static RoomDetailLink ItemLink(AppServices services, int itemId)
    {
        string itemName = services.ItemNames.GetName(itemId) ?? $"item #{itemId}";
        return new RoomDetailLink(itemName, null,
            new RelayCommand(() => services.OpenItemGameData(itemId)));
    }
}
