namespace MudPlay.Game.Map;

// A relocation a Roomba sweep has still to make: Count of ItemName from From to To.
// Hidden when the item only shows to a search; Carried once it has been picked up.
// Runtime only — never persisted.
public sealed record GhSweepPendingMove(RoomKey From, RoomKey To, string ItemName, int Count, bool Hidden, bool Carried);
