namespace FujinTerm.ViewModels.GameData.Edit;

// One row of the room-detail popup's trainer cost table: the level being trained
// to and the copper it costs to advance into it at this trainer's markup. Both
// are pre-formatted strings — Cost is already reduced to its friendliest coin
// denomination by ShopPriceCalculator.FormatCopper.
public sealed record TrainerCostRow(string Level, string Cost);
