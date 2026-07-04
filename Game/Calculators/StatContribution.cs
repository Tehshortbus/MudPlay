namespace FujinTerm.Game.Calculators;

// One equipped item's contribution to a single derived stat, used to build the
// per-stat tooltip breakdown in the Character Workshop (e.g. hovering the Armour
// Class total lists each item and what it added).
//   ItemName     — source item that contributed the value.
//   DisplayValue — pre-formatted contribution string (e.g. "11.9/1.3" for an
//                  item's base AC/DR, "+15" for an ability bonus).
//   Tag          — optional qualifier shown alongside the value (e.g. "[BLUR]"
//                  for an AC bonus from a blur ability rather than worn armour).
public readonly record struct StatContribution(string ItemName, string DisplayValue, string? Tag = null);
