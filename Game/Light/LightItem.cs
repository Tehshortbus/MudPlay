namespace FujinTerm.Game.Light;

// One light-source item (ItemType 6) from the active game-data set: its projected
// illumination and burn budget. Produced by LightItemIndex and used by the
// auto-light provisioning logic to answer "how much illumination does this give"
// and "how long does it burn".
//
// Number is the MDB item id; Name is the verbatim MDB name (e.g. lantern).
//
// Strength is the illumination the light projects when readied — MajorMUD ability
// code 54 (IlluTarget). It's the y term a readied light contributes to the
// visibility check y + roomLight >= -150 (torch 100, lantern 175, scaled lantern
// 200). Distinct from worn +illu gear (codes 13 / 14), which the character carries
// independently.
//
// UseCount is the burn budget. The in-game (Readied/N) counter starts at
// FullReadied (UseCount / 10) and drains one point on a global 30-second tick.
public readonly record struct LightItem(int Number, string Name, int Strength, int UseCount)
{
    // In-game (Readied/N) counter at full charge: UseCount / 10.
    public int FullReadied => UseCount / 10;

    // Total burn time from a full charge, at 30 s per readied point (torch ~ 40
    // min, lantern ~ 2 h). Computed off FullReadied so it matches the observable
    // counter for non-multiple-of-10 budgets.
    public System.TimeSpan BurnTime => System.TimeSpan.FromSeconds(FullReadied * 30);
}
