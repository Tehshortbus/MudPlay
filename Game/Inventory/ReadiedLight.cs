namespace FujinTerm.Game.Inventory;

/// <summary>
/// The light source the player currently has lit, parsed from an <c>i</c> dump
/// where a readied light prints inline as <c>lantern (Readied/239)</c> — the
/// same <c>(&lt;suffix&gt;)</c> shape as worn gear, but the suffix is
/// <c>Readied/&lt;N&gt;</c> where <c>N</c> is the remaining charge counter.
/// Only one light is readied at a time, so <see cref="InventorySnapshot"/>
/// carries at most one of these.
/// </summary>
/// <param name="Name">Bare item name, suffix stripped (e.g. <c>lantern</c>).</param>
/// <param name="Readied">
/// The live <c>Readied/N</c> counter. It equals the item's game-data
/// <c>UseCount / 10</c> at full charge (torch 800 → 80, lantern 2400 → 240) and
/// drains one point on a global 30-second tick.
/// </param>
public readonly record struct ReadiedLight(string Name, int Readied)
{
    /// <summary>
    /// Estimated burn time left at 30 s per readied point. This is an upper
    /// bound: the first drop after lighting can land under 30 s (alignment to
    /// the global tick), so the true remaining time is within one tick of this.
    /// </summary>
    public System.TimeSpan RemainingTime => System.TimeSpan.FromSeconds(Readied * 30);
}
