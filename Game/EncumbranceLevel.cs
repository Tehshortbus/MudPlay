namespace FujinTerm.Game;

/// <summary>
/// Encumbrance bracket the server reports on the <c>enc</c> line — drives
/// the Auto-Lair scheduler's per-hop travel-cost lookup and the
/// hop-timing calibrator's bucket tag.
/// </summary>
/// <remarks>
/// Stock MajorMUD reports five brackets (None / Light / Medium / Heavy /
/// Encumbered) on the line:
/// <c>"Encumbrance:    0/2880  -  None  [0%]"</c>. <see cref="Unknown"/>
/// is the default until the player first <c>enc</c>s during a session.
/// </remarks>
public enum EncumbranceLevel
{
    Unknown = 0,
    None,
    Light,
    Medium,
    Heavy,
    Encumbered,
}
