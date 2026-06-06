using FujinTerm.Game.Map;

namespace FujinTerm.ViewModels.Navigation;

/// <summary>
/// One row in the Navigation GOTO pane — a saved favourite room with
/// the user's chosen label (or the graph display name when no label
/// was supplied). Bindings: <see cref="Label"/> drives the visible
/// text; <see cref="Key"/> is the walk-to target.
/// </summary>
public sealed record FavoriteRowViewModel(RoomKey Key, string Label);
