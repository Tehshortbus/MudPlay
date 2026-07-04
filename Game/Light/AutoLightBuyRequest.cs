namespace FujinTerm.Game.Light;

// A provisioning hand-off from AutoLightProvisioner to AutoLightShopRouter: the
// planner returned Buy for a light the pack doesn't hold, so a shop detour is
// warranted. Carries the resolved MDB item id (shop / carried-count lookups key
// on id), the verbatim light name (used literally in the buy <name> command),
// and how many copies to stock for the configured carry duration (>= 1).
public readonly record struct AutoLightBuyRequest(int ItemId, string LightName, int Count);
