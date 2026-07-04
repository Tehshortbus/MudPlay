namespace FujinTerm.Game.Light;

/// <summary>
/// One light-source item (<c>ItemType 6</c>) from the active game-data set:
/// its projected illumination and burn budget. Produced by
/// <see cref="LightItemIndex"/> and used by the auto-light provisioning logic
/// to answer "how much illumination does this give" and "how long does it burn".
/// </summary>
/// <param name="Number">MDB <c>Number</c> (item id).</param>
/// <param name="Name">Verbatim MDB <c>Name</c> (e.g. <c>lantern</c>).</param>
/// <param name="Strength">
/// The illumination the light projects when readied — MajorMUD ability code 54
/// (<c>IlluTarget</c>). This is the <c>y</c> term a readied light contributes to
/// the visibility check <c>y + roomLight &gt;= -150</c> (torch 100, lantern 175,
/// scaled lantern 200). Distinct from worn +illu gear (codes 13 / 14), which the
/// character carries independently.
/// </param>
/// <param name="UseCount">
/// The burn budget. The in-game <c>(Readied/N)</c> counter starts at
/// <see cref="FullReadied"/> (<c>UseCount / 10</c>) and drains one point on a
/// global 30-second tick.
/// </param>
public readonly record struct LightItem(int Number, string Name, int Strength, int UseCount)
{
    /// <summary>In-game (Readied/N) counter at full charge: <c>UseCount / 10</c>.</summary>
    public int FullReadied => UseCount / 10;

    /// <summary>
    /// Total burn time from a full charge, at 30 s per readied point
    /// (torch ≈ 40 min, lantern ≈ 2 h). Computed off <see cref="FullReadied"/> so
    /// it matches the observable counter for non-multiple-of-10 budgets.
    /// </summary>
    public System.TimeSpan BurnTime => System.TimeSpan.FromSeconds(FullReadied * 30);
}
