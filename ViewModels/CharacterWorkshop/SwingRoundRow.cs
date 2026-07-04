namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// One round in the Swing calculator's 10-round breakdown: the swings landed
/// that round and the energy carried into the next (the remainder rolls forward,
/// which is what produces the repeating swing pattern).
/// </summary>
/// <param name="Round">1-based round number.</param>
/// <param name="Swings">Swings landed this round.</param>
/// <param name="EnergyCarried">Energy carried into the next round.</param>
public readonly record struct SwingRoundRow(int Round, int Swings, int EnergyCarried);
