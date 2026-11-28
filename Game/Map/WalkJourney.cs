namespace MudPlay.Game.Map;

// Where a trip is going and how it was told to get there. The walker plans one leg
// at a time and forgets a leg's settings when the leg ends, but a trip is often
// several legs: a re-plan, a side trip to fetch an item the route needs, a restart
// after a sell or a flee. Each of those is planned from this, so the route the user
// picked on a card is still the route after the third leg.
//
// PreferTeleportFree null is a walk nobody chose a route for, which follows the
// automatic-walk teleport setting. ClosedGates are gate items a plan through the
// acquirable gates must not count as in hand: the picked route goes round the exits
// that need them.
//
// AgreedHazardRooms are the hazard rooms, uncountered when the route was shown, that
// lie on the card's route. Picking the card agreed to walking into those. It did not
// agree to any other hazard room, so they are named one by one: a plan made later
// from another room opens these and nothing else. A picked route that names none
// agreed to none.
//
// Fetch is what the journey was told to fetch on its way and the trades a route
// card named for it (JourneyFetch). Being on the journey is its whole lifetime.
//
// SysGotoShortcuts false is a route the user picked from a card: the walk keeps to
// it, and none of its legs or re-plans adds a `sys goto` shortcut the card didn't
// show. The sys goto card itself says which jump it takes (AutoWalkManager.WalkTo's
// sysGotoVia), once.
//
// OwnedLeg marks a walk that is one leg of something which ends the moment its walk
// is taken away (a Stock spill sweep's walk to its next stop). The walker runs it
// like any journey but doesn't report it through AutoWalkManager.Journey: everything
// that saves "the walk under way" to pick it back up after an errand reads that, and
// such a leg's destination is a room nobody waits at once its owner has ended.
public sealed record WalkJourney(
    RoomKey Destination,
    bool ThroughGates = false,
    bool ArmAcquisition = true,
    bool AvoidTeleports = false,
    bool AvoidTraps = false,
    bool IgnoreAvoids = false,
    bool? PreferTeleportFree = null,
    bool PickedRoute = false,
    IReadOnlyCollection<int>? ClosedGates = null,
    IReadOnlyCollection<RoomKey>? AgreedHazardRooms = null,
    JourneyFetch? Fetch = null,
    bool OwnedLeg = false,
    bool SysGotoShortcuts = true)
{
    // "on foot unless walking is impossible, going round the gates that need
    // item(s) 815", for the program log and the bug report.
    public string Describe()
    {
        string teleports = AvoidTeleports ? "never teleports"
            : PreferTeleportFree switch
            {
                true => "on foot unless walking is impossible",
                false => "the picked route's teleports",
                null => "teleports by the automatic-walk setting",
            };
        return teleports
            + (ClosedGates is { Count: > 0 } ? $", going round the gates that need item(s) {string.Join("/", ClosedGates)}" : "")
            + (IgnoreAvoids ? ", through avoided rooms" : "")
            + (AvoidTraps ? ", fewest traps" : "")
            + (SysGotoShortcuts ? "" : ", no sys goto shortcut")
            + (!PickedRoute ? ""
                : AgreedHazardRooms is not { Count: > 0 } ? ", no hazard room agreed to"
                : $", hazard room(s) {string.Join(", ", AgreedHazardRooms)} agreed to")
            + (Fetch is not { HasItems: true } ? "" : $", fetching item(s) {string.Join("/", Fetch.Items)}")
            + (Fetch is not { Trades.Count: > 0 } ? ""
                : $", trade(s) agreed: {string.Join(", ", Fetch.Trades.Select(t => $"{t.Key} for {t.Value}"))}");
    }
}
