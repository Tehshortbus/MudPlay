using System.Collections.Generic;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;

namespace MudPlay.Services;

// The route picker's sys goto card: jump with a Sys Goto row, then walk on from its
// landing (AutoWalkManager.SysGotoCardPlan). Path is that walk, the landing first,
// for the map preview and the step count; the jump itself is no graph edge. Its
// pick hands Location to the walk (WalkTo's sysGotoVia), so the walk takes this jump
// and no other. LandingLabel and Eta are filled in by the picker, on the UI thread.
public sealed record SysGotoRouteCard(
    SysopGotoLocation Location,
    RoomKey Landing,
    IReadOnlyList<RoomKey> Path,
    string? LandingLabel = null,
    TimeSpan Eta = default)
{
    // The jump counts as one step, as the walk's step list shows it.
    public int StepCount => Path.Count;
}
