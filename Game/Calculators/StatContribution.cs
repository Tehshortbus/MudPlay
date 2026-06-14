namespace FujinTerm.Game.Calculators;

/// <summary>
/// One equipped item's contribution to a single derived stat, used to build
/// the per-stat tooltip breakdown in the Character Workshop (e.g. hovering the
/// Armour Class total lists each item and what it added).
/// </summary>
/// <param name="ItemName">Source item that contributed the value.</param>
/// <param name="DisplayValue">
/// Pre-formatted contribution string (e.g. <c>"11.9/1.3"</c> for an item's base
/// AC/DR, <c>"+15"</c> for an ability bonus).
/// </param>
/// <param name="Tag">
/// Optional qualifier shown alongside the value (e.g. <c>"[BLUR]"</c> for an
/// AC bonus that comes from a blur ability rather than worn armour).
/// </param>
public readonly record struct StatContribution(string ItemName, string DisplayValue, string? Tag = null);
