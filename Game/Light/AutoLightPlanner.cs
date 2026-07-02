using FujinTerm.Game.Inventory;
using FujinTerm.Models.Profile;

namespace FujinTerm.Game.Light;

/// <summary>
/// Pure decision core for the auto-light engine. Given the darkness of the route
/// ahead (<see cref="RouteLightScanner"/>), the character's worn illumination,
/// the currently readied light, the lights carried in the pack, the buyable
/// light catalogue, and the <see cref="AutoLightSettings"/> knobs, it answers
/// <em>what to do right now</em> as an <see cref="AutoLightPlan"/> — ready a
/// carried light, buy a provisioning batch, top up a dwindling supply, or
/// nothing. It never touches the wire or the game state; the wiring layer turns
/// the plan into <c>hold</c> / <c>buy</c> commands.
/// </summary>
/// <remarks>
/// Preferred vs. auto: a named <see cref="AutoLightSettings.PreferredLightName"/>
/// is used as-is (the user's explicit pick, even if a stronger light would be
/// needed to fully clear the darkest room); the auto sentinel (null name) lets
/// the planner pick the weakest catalogue light that still reaches
/// <see cref="LightModel.SeeThreshold"/> for the route's darkest room, avoiding
/// overkill. Coverage is measured with
/// <see cref="LightModel.IlluGapToSee(int,int)"/> against the worn-only illu, so
/// the chosen light is the one that — once readied — covers the room on its own.
/// </remarks>
public static class AutoLightPlanner
{
    /// <summary>Global light tick: one <c>Readied/N</c> point drains every 30 s.</summary>
    private const double SecondsPerReadiedPoint = 30.0;

    public static AutoLightPlan Plan(
        RouteLightScan routeScan,
        int wornIllu,
        ReadiedLight? readied,
        IReadOnlyList<LightItem> carriedLights,
        IReadOnlyList<LightItem> catalogue,
        AutoLightSettings settings)
    {
        ArgumentNullException.ThrowIfNull(carriedLights);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(settings);

        bool provisioningOn = settings.CarryHours > 0;
        LightItem? preferred = FindByName(settings.PreferredLightName, catalogue);

        // 1. Reorder — a readied light dwindling below the minute threshold gets
        //    topped up. Only meaningful when provisioning is on (there's a carry
        //    target to restock to) and we can identify what to rebuy.
        if (provisioningOn && settings.ReorderThresholdMinutes > 0 && readied is { } rl)
        {
            double remainingMinutes = rl.Readied * SecondsPerReadiedPoint / 60.0;
            if (remainingMinutes < settings.ReorderThresholdMinutes)
            {
                LightItem? restock = FindByName(rl.Name, catalogue) ?? preferred;
                if (restock is { } rs)
                    return AutoLightPlan.BuyLight(
                        rs.Name, CarryCount(settings.CarryHours, rs.BurnTime),
                        $"reorder: {rl.Name} at ~{remainingMinutes:0} min left");
            }
        }

        // 2. Route runs dark — ready a carried light that covers it, else buy one.
        if (routeScan.NeedsLight)
        {
            int minStrength = LightModel.IlluGapToSee(wornIllu, routeScan.DarkestRoomLight);

            // a. Ready a carried light — no shop trip. Preferred if carried, else
            //    the weakest carried light that reaches the darkest room.
            if (Choose(carriedLights, minStrength, preferred) is { } carried)
                return AutoLightPlan.ReadyLight(
                    carried.Name, $"route dark: ready {carried.Name} (need illu {minStrength})");

            // b. Nothing carried covers it — provision one from a shop.
            if (provisioningOn && Choose(catalogue, minStrength, preferred) is { } buy)
                return AutoLightPlan.BuyLight(
                    buy.Name, CarryCount(settings.CarryHours, buy.BurnTime),
                    $"route dark: provision {buy.Name} (need illu {minStrength})");

            // c. Can't buy (provisioning off / nothing buyable) — ready the
            //    strongest carried light as a partial mitigation, else nothing.
            return Strongest(carriedLights) is { } fallback
                ? AutoLightPlan.ReadyLight(
                    fallback.Name, $"route dark: ready {fallback.Name} (best carried, may not fully cover)")
                : AutoLightPlan.Nothing("route dark: no light carried and provisioning off");
        }

        return AutoLightPlan.Nothing("route lit");
    }

    /// <summary>
    /// Pick a light from <paramref name="from"/>: the user's
    /// <paramref name="preferred"/> when set (and present in the list), otherwise
    /// the weakest light that still reaches the darkest room
    /// (<c>Strength &gt;= <paramref name="minStrength"/></c>). Null when nothing
    /// qualifies.
    /// </summary>
    private static LightItem? Choose(IReadOnlyList<LightItem> from, int minStrength, LightItem? preferred)
    {
        if (preferred is { } p)
            return FindByName(p.Name, from);

        LightItem? best = null;
        foreach (LightItem l in from)
        {
            if (l.Strength < minStrength) continue;
            if (best is not { } b || l.Strength < b.Strength) best = l;
        }
        return best;
    }

    private static LightItem? Strongest(IReadOnlyList<LightItem> lights)
    {
        LightItem? best = null;
        foreach (LightItem l in lights)
            if (best is not { } b || l.Strength > b.Strength) best = l;
        return best;
    }

    private static LightItem? FindByName(string? name, IReadOnlyList<LightItem> lights)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (LightItem l in lights)
            if (string.Equals(l.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                return l;
        return null;
    }

    /// <summary>How many of a light to buy to cover
    /// <paramref name="carryHours"/> of lit time, rounding up. At least one when
    /// provisioning; a light with no burn budget yields a single copy.</summary>
    private static int CarryCount(int carryHours, TimeSpan burnTime)
    {
        if (burnTime.TotalHours <= 0) return 1;
        return Math.Max(1, (int)Math.Ceiling(carryHours / burnTime.TotalHours));
    }
}
