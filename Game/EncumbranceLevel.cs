namespace FujinTerm.Game;

// Encumbrance bracket the server reports on the `enc` line — drives the
// Auto-Lair scheduler's per-hop travel-cost lookup and the hop-timing
// calibrator's bucket tag.
//
// Stock MajorMUD reports five brackets (None / Light / Medium / Heavy /
// Encumbered) on the line: "Encumbrance:    0/2880  -  None  [0%]". Unknown is
// the default until the player first `enc`s during a session.
public enum EncumbranceLevel
{
    Unknown = 0,
    None,
    Light,
    Medium,
    Heavy,
    Encumbered,
}
