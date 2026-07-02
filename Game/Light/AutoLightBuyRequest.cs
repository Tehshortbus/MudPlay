namespace FujinTerm.Game.Light;

/// <summary>
/// A provisioning hand-off from <see cref="AutoLightProvisioner"/> to
/// <see cref="AutoLightShopRouter"/>: the planner returned
/// <see cref="AutoLightAction.Buy"/> for a light the pack doesn't hold, so a
/// shop detour is warranted. Carries the resolved MDB item id (for the shop /
/// carried-count lookups, which key on id), the verbatim light name (for the
/// <c>buy &lt;name&gt;</c> command), and how many copies to stock for the
/// configured carry duration.
/// </summary>
/// <param name="ItemId">MDB <c>Number</c> of the light to provision.</param>
/// <param name="LightName">Verbatim MDB name, used literally in <c>buy</c>.</param>
/// <param name="Count">Copies to buy — the carry-duration batch (>= 1).</param>
public readonly record struct AutoLightBuyRequest(int ItemId, string LightName, int Count);
