namespace FujinTerm.ViewModels.CharacterWorkshop;

// One round in the Swing calculator's 10-round breakdown: the swings landed that
// round (Swings) and the energy carried into the next (EnergyCarried) — the
// remainder rolls forward, which is what produces the repeating swing pattern.
// Round is the 1-based round number.
public readonly record struct SwingRoundRow(int Round, int Swings, int EnergyCarried);
