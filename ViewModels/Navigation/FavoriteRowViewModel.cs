using FujinTerm.Game.Map;

namespace FujinTerm.ViewModels.Navigation;

// One row in the Navigation GOTO pane — a saved favourite room with the
// user's chosen label (or the graph display name when no label was
// supplied). Label drives the visible text; Key is the walk-to target;
// Folder (a /-separated path, empty = root) is the grouping the GOTO tree
// files this row under.
public sealed record FavoriteRowViewModel(RoomKey Key, string Label, string Folder);
